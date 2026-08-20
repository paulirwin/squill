using Squill.Core;
using Squill.MariaDbParser;
using Squill.Provider.MariaDb;

namespace Squill.IntegrationTests.MariaDb;

/// <summary>
/// End-to-end sequence tests for the MariaDB provider (issue #218), run against a real MariaDB
/// server. Each test parses declarative SQL into a model, publishes it into a fresh database,
/// extracts the database's model, and asserts the two hash-match, then redeploys to prove the
/// sequence converges rather than re-diffing forever.
///
/// <para>
/// Unlike every other suite here this one binds to MariaDB alone, because MySQL has no
/// sequence object at all: CREATE SEQUENCE is a syntax error there (measured on mysql:latest).
/// The MySQL half of the behaviour is that a build targeting it fails, which needs no server
/// and is covered by a unit test instead.
/// </para>
///
/// <para>
/// A sequence is where the omit-when-default convention earns its keep: the backing table
/// reports every option with its default filled in, so a bare CREATE SEQUENCE would re-diff on
/// every deploy if the build recorded what it did not declare. The redeploy assertion in each
/// scenario is what proves it does not.
/// </para>
/// </summary>
public abstract class MariaDbSequenceTests
{
    protected abstract MariaDbLikeFixture Fixture { get; }

    private Model ParseModel(string sql, CancellationToken cancellationToken)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        return new ParserWorkspaceModelBuilder(workspace, new AntlrMariaDbParser(), Fixture.SchemaProviderOf())
            .ExtractModelAsync(cancellationToken).GetAwaiter().GetResult().Model;
    }

    private async Task<Model> AssertRoundTripAsync(string sql, CancellationToken cancellationToken)
    {
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var model = ParseModel(sql, cancellationToken);

        return await RoundTripHarness.AssertRoundTripAsync(
            provider, model, Fixture.EngineName, assertRedeployNoOp: true,
            cancellationToken: cancellationToken);
    }

    private static Element SingleSequence(Model model)
        => Assert.Single(model.Elements, i => i.Type == MariaDbElementTypes.SqlSequence);

    /// <summary>
    /// The case the omit-when-default convention exists for. The server fills in start 1,
    /// minvalue 1, maxvalue 2^63-2, increment 1 and cache 1000 for a sequence that declared
    /// none of them, so this only round-trips because neither side records them.
    /// </summary>
    [Fact]
    public async Task BareSequence_RoundTrips()
    {
        var model = await AssertRoundTripAsync(
            "CREATE SEQUENCE order_seq;", TestContext.Current.CancellationToken);

        Assert.Empty(SingleSequence(model).Properties);
    }

    [Fact]
    public async Task SequenceWithEveryOption_RoundTrips()
    {
        var model = await AssertRoundTripAsync(
            "CREATE SEQUENCE s INCREMENT BY 5 MINVALUE 10 MAXVALUE 1000 "
            + "START WITH 20 CACHE 50 CYCLE;",
            TestContext.Current.CancellationToken);

        var sequence = SingleSequence(model);

        Assert.Equal(5L, sequence.GetProperty<long?>(MariaDbPropertyNames.Increment));
        Assert.Equal(10L, sequence.GetProperty<long?>(MariaDbPropertyNames.MinValue));
        Assert.Equal(1000L, sequence.GetProperty<long?>(MariaDbPropertyNames.MaxValue));
        Assert.Equal(20L, sequence.GetProperty<long?>(MariaDbPropertyNames.StartValue));
        Assert.Equal(50L, sequence.GetProperty<long?>(MariaDbPropertyNames.CacheSize));
        Assert.True(sequence.GetProperty<bool?>(MariaDbPropertyNames.IsCycling));
    }

    /// <summary>
    /// NOCACHE is cache_size 0, which differs from the default of 1000 and so is recorded and
    /// must be scripted back as the keyword rather than as CACHE 0.
    /// </summary>
    [Fact]
    public async Task SequenceWithNoCache_RoundTrips()
    {
        var model = await AssertRoundTripAsync(
            "CREATE SEQUENCE s NOCACHE;", TestContext.Current.CancellationToken);

        Assert.Equal(0L, SingleSequence(model).GetProperty<long?>(MariaDbPropertyNames.CacheSize));
    }

    /// <summary>
    /// Declaring the engine's own defaults must be indistinguishable from declaring nothing,
    /// since the extractor cannot tell them apart. The measured MariaDB cache default is 1000,
    /// not the 1 the Postgres provider uses, so this is the scenario that would fail had the
    /// defaults been copied across providers.
    /// </summary>
    [Fact]
    public async Task SequenceDeclaringTheEngineDefaults_RoundTripsCleanly()
    {
        var model = await AssertRoundTripAsync(
            "CREATE SEQUENCE s START WITH 1 INCREMENT BY 1 MINVALUE 1 CACHE 1000 NOCYCLE;",
            TestContext.Current.CancellationToken);

        Assert.Empty(SingleSequence(model).Properties);
    }

    /// <summary>
    /// A column defaulting to NEXTVAL and the sequence it draws from, which is the pairing the
    /// issue calls out: the sequence has to be created first or the table's DDL fails.
    /// </summary>
    [Fact]
    public async Task ColumnDefaultingToNextValue_RoundTrips()
    {
        var model = await AssertRoundTripAsync(
            """
            CREATE SEQUENCE order_seq;
            CREATE TABLE orders (
              id BIGINT NOT NULL DEFAULT NEXTVAL(order_seq),
              note VARCHAR(50) NULL,
              PRIMARY KEY (id)
            );
            """,
            TestContext.Current.CancellationToken);

        var column = model.Elements
            .Single(i => i.Type == MariaDbElementTypes.SqlTable)
            .Relationships.Single(r => r.Name == MariaDbRelationshipNames.Columns)
            .Entries.OfType<Element>()
            .Single(c => SqlName.UnqualifiedOf((string)c.Name!) == "id");

        // The database qualifier is dropped, so the model is environment-neutral even though
        // the server stores nextval(`<db>`.`order_seq`).
        Assert.Equal("NEXTVAL(`order_seq`)",
            column.GetProperty<string>(MariaDbPropertyNames.DefaultValue));
    }

    /// <summary>
    /// The sequence must be created before the table that draws from it even when the source
    /// declares them the other way round, or the table's DDL fails at deploy with an unknown
    /// sequence. This is the ordering rule doing real work rather than a model-shape assertion.
    /// </summary>
    [Fact]
    public async Task SequenceDeclaredAfterItsTable_StillDeploys()
    {
        await AssertRoundTripAsync(
            """
            CREATE TABLE orders (
              id BIGINT NOT NULL DEFAULT NEXTVAL(order_seq),
              PRIMARY KEY (id)
            );
            CREATE SEQUENCE order_seq;
            """,
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The deployed sequence actually generates values, which no model comparison can show.
    /// Proves the DDL Squill emits produces a working sequence rather than merely one whose
    /// catalog row matches.
    /// </summary>
    [Fact]
    public async Task DeployedSequence_GeneratesValues()
    {
        await DeployAndInspectAsync(
            "CREATE SEQUENCE s START WITH 100 INCREMENT BY 10;",
            async (database, _) =>
            {
                var cancellationToken = TestContext.Current.CancellationToken;

                await using var reader = await database.RunScriptReaderAsync(
                    "SELECT NEXTVAL(s), NEXTVAL(s);", cancellationToken: cancellationToken);

                Assert.True(await reader.ReadAsync(cancellationToken));
                Assert.Equal(100L, Convert.ToInt64(reader.GetValue(0)));
                Assert.Equal(110L, Convert.ToInt64(reader.GetValue(1)));
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Publishes <paramref name="sql"/> into a fresh database and hands the still-live database
    /// to <paramref name="inspect"/>, so a test can ask the server what it actually got rather
    /// than asking Squill what it thinks it deployed. The round-trip harness drops its database
    /// before returning, which is why this exists alongside it.
    /// </summary>
    private async Task DeployAndInspectAsync(
        string sql,
        Func<IDatabase, string, Task> inspect,
        CancellationToken cancellationToken)
    {
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var parsed = ParseModel(sql, cancellationToken);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", cancellationToken);

        try
        {
            var builder = provider.CreateDatabaseModelBuilder(testDb);
            var empty = await builder.ExtractModelAsync(cancellationToken);

            await testDb.PublishAsync(
                SchemaCompare.Compare(provider, parsed, empty), cancellationToken);

            await inspect(testDb, testDb.Name);
        }
        finally
        {
            await testDb.DropAsync(cancellationToken);
        }
    }
}

// ---- Per-engine binding: MariaDB only, since MySQL has no sequence object. ----

public sealed class MariaDbSequenceTestsMariaDb(MariaDbFixture fixture)
    : MariaDbSequenceTests, IClassFixture<MariaDbFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}
