using Squill.Core;
using Squill.PostgresParser;
using Squill.Provider.Postgres;
using Squill.TestFramework;

namespace Squill.IntegrationTests.Postgres.SequenceTest;

// End-to-end coverage for standalone CREATE SEQUENCE (issue #122). Before this, a declared
// sequence threw out of the parser workspace builder, so a schema containing one could not be
// built at all.
//
// Each test deploys a schema to a real database and asserts both that the generated DDL is
// valid, executable Postgres and that the catalog converges on the declared state. The
// redeploy-is-a-no-op assertions are the load-bearing ones: a sequence's options only
// round-trip if the parsed model and the model extracted from pg_sequence agree on which
// options to omit as defaults, and any disagreement would make every deploy re-alter the
// sequence forever.
public class PostgresSequenceTest : PostgresIntegrationTestBase
{
    private static Task<Model> ParseModelAsync(string sql, CancellationToken cancellationToken)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrPostgresParser()),
            cancellationToken);

    private sealed record Deployment(
        IDatabase Database, IDatabaseProvider Provider, IDatabaseModelBuilder ModelBuilder);

    // Deploys sql to a fresh database and returns the still-open database for assertions.
    private async Task<Deployment> DeployAsync(string sql, CancellationToken cancellationToken)
    {
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var database = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", cancellationToken);

        var modelBuilder = provider.CreateDatabaseModelBuilder(database);

        await database.PublishAsync(
            SchemaCompare.Compare(provider, await ParseModelAsync(sql, cancellationToken),
                await modelBuilder.ExtractModelAsync(cancellationToken)),
            cancellationToken);

        return new Deployment(database, provider, modelBuilder);
    }

    // Diffs sql against the live database again, returning the deltas a redeploy would apply.
    private static async Task<SchemaComparison> RedeployComparisonAsync(
        Deployment deployment, string sql, CancellationToken cancellationToken)
        => SchemaCompare.Compare(
            deployment.Provider,
            await ParseModelAsync(sql, cancellationToken),
            await deployment.ModelBuilder.ExtractModelAsync(cancellationToken));

    // The sequence's options as the catalog reports them.
    private static async Task<(string Type, long Start, long Increment, long Min, long Max,
        long Cache, bool Cycle)> ReadSequenceAsync(
        IDatabase database, string name, CancellationToken cancellationToken)
    {
        await using var reader = await database.RunScriptReaderAsync(
            $"""
             SELECT format_type(s.seqtypid, NULL) AS data_type, s.seqstart, s.seqincrement,
                    s.seqmin, s.seqmax, s.seqcache, s.seqcycle
             FROM pg_sequence s JOIN pg_class c ON c.oid = s.seqrelid
             WHERE c.relname = '{name}';
             """,
            cancellationToken: cancellationToken);

        Assert.True(await reader.ReadAsync(cancellationToken), $"Sequence {name} was not created");

        return (reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2),
            reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetBoolean(6));
    }

    [Fact]
    public async Task Sequence_BareDeclaration_IsCreatedWithPostgresDefaults()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        const string sql = "CREATE SEQUENCE order_number;";

        var deployment = await DeployAsync(sql, cancellationToken);

        try
        {
            var sequence = await ReadSequenceAsync(
                deployment.Database, "order_number", cancellationToken);

            // A standalone sequence defaults to bigint — not the integer a bare serial column
            // produces. Getting this wrong would silently cap the sequence at 2^31.
            Assert.Equal("bigint", sequence.Type);
            Assert.Equal(1, sequence.Start);
            Assert.Equal(1, sequence.Increment);
            Assert.False(sequence.Cycle);

            // The sequence is usable, which is the only real proof the DDL was valid. The
            // reader is scoped so it is closed before the connection is reused below.
            await using (var reader = await deployment.Database.RunScriptReaderAsync(
                "SELECT nextval('order_number');", cancellationToken: cancellationToken))
            {
                Assert.True(await reader.ReadAsync(cancellationToken));
                Assert.Equal(1, reader.GetInt64(0));
            }

            Assert.Empty(
                (await RedeployComparisonAsync(deployment, sql, cancellationToken)).Deltas);
        }
        finally
        {
            await deployment.Database.DropAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task Sequence_AllOptions_RoundTripAsANoOp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        const string sql = """
            CREATE SEQUENCE order_number
                AS integer
                START WITH 100
                INCREMENT BY 5
                MINVALUE 10
                MAXVALUE 5000
                CACHE 20
                CYCLE;
            """;

        var deployment = await DeployAsync(sql, cancellationToken);

        try
        {
            var sequence = await ReadSequenceAsync(
                deployment.Database, "order_number", cancellationToken);

            Assert.Equal("integer", sequence.Type);
            Assert.Equal(100, sequence.Start);
            Assert.Equal(5, sequence.Increment);
            Assert.Equal(10, sequence.Min);
            Assert.Equal(5000, sequence.Max);
            Assert.Equal(20, sequence.Cache);
            Assert.True(sequence.Cycle);

            // Every option survives a round trip through the catalog, so redeploying the
            // unchanged schema does nothing.
            Assert.Empty(
                (await RedeployComparisonAsync(deployment, sql, cancellationToken)).Deltas);
        }
        finally
        {
            await deployment.Database.DropAsync(cancellationToken);
        }
    }

    // A descending sequence has different defaults (start and maxvalue -1, minvalue = type
    // min), so this checks the direction-aware default handling against the real server.
    [Fact]
    public async Task Sequence_Descending_RoundTripsAsANoOp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        const string sql = "CREATE SEQUENCE countdown INCREMENT BY -1;";

        var deployment = await DeployAsync(sql, cancellationToken);

        try
        {
            var sequence = await ReadSequenceAsync(
                deployment.Database, "countdown", cancellationToken);

            Assert.Equal(-1, sequence.Increment);
            Assert.Equal(-1, sequence.Start);
            Assert.Equal(-1, sequence.Max);
            Assert.Equal(long.MinValue, sequence.Min);

            Assert.Empty(
                (await RedeployComparisonAsync(deployment, sql, cancellationToken)).Deltas);
        }
        finally
        {
            await deployment.Database.DropAsync(cancellationToken);
        }
    }

    // The heart of the feature: a changed sequence is altered in place, so its current value
    // survives. A drop-and-recreate would reset the counter back to the start, silently
    // handing out values that have already been used.
    [Fact]
    public async Task Sequence_ChangedOption_AltersInPlaceAndPreservesCurrentValue()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        const string changed = "CREATE SEQUENCE order_number INCREMENT BY 10 MAXVALUE 900;";

        var deployment = await DeployAsync(
            "CREATE SEQUENCE order_number INCREMENT BY 5;", cancellationToken);

        try
        {
            // Advance the sequence so it has a current value to lose: with INCREMENT BY 5 the
            // three draws yield 1, 6, 11.
            await deployment.Database.RunScriptAsync(
                "SELECT nextval('order_number') FROM generate_series(1, 3);",
                cancellationToken: cancellationToken);

            await deployment.Database.PublishAsync(
                await RedeployComparisonAsync(deployment, changed, cancellationToken),
                cancellationToken);

            var sequence = await ReadSequenceAsync(
                deployment.Database, "order_number", cancellationToken);

            Assert.Equal(10, sequence.Increment);
            Assert.Equal(900, sequence.Max);

            // The counter continues from where it had reached (11) with the new increment,
            // rather than restarting — which is what a drop-and-recreate would have done.
            await using (var reader = await deployment.Database.RunScriptReaderAsync(
                "SELECT nextval('order_number');", cancellationToken: cancellationToken))
            {
                Assert.True(await reader.ReadAsync(cancellationToken));
                Assert.Equal(21, reader.GetInt64(0));
            }

            Assert.Empty(
                (await RedeployComparisonAsync(deployment, changed, cancellationToken)).Deltas);
        }
        finally
        {
            await deployment.Database.DropAsync(cancellationToken);
        }
    }

    // An option removed from the declaration must be actively reset, not merely left alone:
    // the deployed sequence still carries the old value.
    [Fact]
    public async Task Sequence_RemovedOption_IsResetToTheDefault()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        const string changed = "CREATE SEQUENCE s;";

        var deployment = await DeployAsync(
            "CREATE SEQUENCE s INCREMENT BY 5 MAXVALUE 900 CACHE 7 CYCLE;", cancellationToken);

        try
        {
            await deployment.Database.PublishAsync(
                await RedeployComparisonAsync(deployment, changed, cancellationToken),
                cancellationToken);

            var sequence = await ReadSequenceAsync(deployment.Database, "s", cancellationToken);

            Assert.Equal(1, sequence.Increment);
            Assert.Equal(long.MaxValue, sequence.Max);
            Assert.Equal(1, sequence.Cache);
            Assert.False(sequence.Cycle);

            Assert.Empty(
                (await RedeployComparisonAsync(deployment, changed, cancellationToken)).Deltas);
        }
        finally
        {
            await deployment.Database.DropAsync(cancellationToken);
        }
    }

    // The exclusion that makes everything else safe: the sequences PostgreSQL creates behind
    // serial and identity columns must not be extracted as declared objects. If they were,
    // every schema with a serial column would show a phantom sequence the source never
    // declared — and with DropObjectsNotInSource would drop the sequence its column needs.
    [Fact]
    public async Task ImplicitSequencesOfSerialAndIdentityColumns_AreNotExtracted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        const string sql = """
            CREATE TABLE a (id serial PRIMARY KEY, name text NOT NULL);
            CREATE TABLE b (id integer GENERATED ALWAYS AS IDENTITY, name text NOT NULL);
            CREATE SEQUENCE declared_seq;
            """;

        var deployment = await DeployAsync(sql, cancellationToken);

        try
        {
            var extracted = await deployment.ModelBuilder.ExtractModelAsync(cancellationToken);

            var sequences = extracted.Elements
                .Where(i => i.Type == PostgresElementTypes.SqlSequence)
                .Select(i => i.Name)
                .ToList();

            // Only the explicitly declared sequence is an element; a_id_seq and b_id_seq
            // belong to their columns.
            Assert.Equal(["declared_seq"], sequences);

            // And so a redeploy of the same source is a no-op rather than a phantom drop.
            Assert.Empty(
                (await RedeployComparisonAsync(deployment, sql, cancellationToken)).Deltas);
        }
        finally
        {
            await deployment.Database.DropAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task Sequence_InNonPublicSchema_RoundTripsAsANoOp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        const string sql = """
            CREATE SCHEMA inventory;
            CREATE SEQUENCE inventory.order_number INCREMENT BY 3;
            """;

        var deployment = await DeployAsync(sql, cancellationToken);

        try
        {
            var sequence = await ReadSequenceAsync(
                deployment.Database, "order_number", cancellationToken);

            Assert.Equal(3, sequence.Increment);

            Assert.Empty(
                (await RedeployComparisonAsync(deployment, sql, cancellationToken)).Deltas);
        }
        finally
        {
            await deployment.Database.DropAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task Sequence_Dropped_IsRemovedFromTheDatabase()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var deployment = await DeployAsync("CREATE SEQUENCE doomed;", cancellationToken);

        try
        {
            var comparison = SchemaCompare.Compare(
                deployment.Provider,
                await ParseModelAsync("", cancellationToken),
                await deployment.ModelBuilder.ExtractModelAsync(cancellationToken),
                new DeployOptions { DropObjectsNotInSource = true });

            await deployment.Database.PublishAsync(comparison, cancellationToken);

            await using var reader = await deployment.Database.RunScriptReaderAsync(
                "SELECT count(*) FROM pg_class WHERE relkind = 'S' AND relname = 'doomed';",
                cancellationToken: cancellationToken);

            Assert.True(await reader.ReadAsync(cancellationToken));
            Assert.Equal(0, reader.GetInt64(0));
        }
        finally
        {
            await deployment.Database.DropAsync(cancellationToken);
        }
    }
}
