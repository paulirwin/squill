using Squill.Core;
using Squill.PostgresParser;
using Squill.Provider.Postgres;
using Squill.TestFramework;

namespace Squill.IntegrationTests.Postgres.CompositeAndRangeTypeTest;

// End-to-end coverage for CREATE TYPE beyond the AS ENUM form (issue #122): composite types
// and range types. Both previously threw out of the parser, so a schema declaring either
// could not be built at all.
//
// The redeploy-is-a-no-op assertions are the load-bearing ones. A composite type's attributes
// come back from format_type() with any modifier rendered inline (character varying(60)),
// while the parser carries the bare type plus a Length property — if those two do not agree,
// every deploy would try to alter the type forever. Range types have the same risk around the
// operator class, which the catalog always reports even when the source named none.
public class PostgresCompositeAndRangeTypeTest : PostgresIntegrationTestBase
{
    private static Task<Model> ParseModelAsync(string sql, CancellationToken cancellationToken)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrPostgresParser()),
            cancellationToken);

    private sealed record Deployment(
        IDatabase Database, IDatabaseProvider Provider, IDatabaseModelBuilder ModelBuilder);

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

    private static async Task<SchemaComparison> RedeployComparisonAsync(
        Deployment deployment, string sql, CancellationToken cancellationToken)
        => SchemaCompare.Compare(
            deployment.Provider,
            await ParseModelAsync(sql, cancellationToken),
            await deployment.ModelBuilder.ExtractModelAsync(cancellationToken));

    // The attributes of a composite type, in order, as the catalog reports them.
    private static async Task<List<(string Name, string Type)>> ReadAttributesAsync(
        IDatabase database, string typeName, CancellationToken cancellationToken)
    {
        var attributes = new List<(string, string)>();

        await using var reader = await database.RunScriptReaderAsync(
            $"""
             SELECT a.attname, format_type(a.atttypid, a.atttypmod)
             FROM pg_type t
             JOIN pg_class c ON c.oid = t.typrelid
             JOIN pg_attribute a ON a.attrelid = c.oid
             WHERE t.typname = '{typeName}' AND c.relkind = 'c'
               AND a.attnum > 0 AND NOT a.attisdropped
             ORDER BY a.attnum;
             """,
            cancellationToken: cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            attributes.Add((reader.GetString(0), reader.GetString(1)));
        }

        return attributes;
    }

    // ---- Composite types ----

    [Fact]
    public async Task CompositeType_IsCreatedWithItsAttributesAndRedeploysAsANoOp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // varchar(60) and numeric(10,2) both carry modifiers, which is exactly where the
        // parsed and extracted models could disagree.
        const string sql = """
            CREATE TYPE addr AS (street varchar(60), city text, zip char(5), rate numeric(10,2));
            """;

        var deployment = await DeployAsync(sql, cancellationToken);

        try
        {
            var attributes = await ReadAttributesAsync(deployment.Database, "addr", cancellationToken);

            Assert.Equal(
                [("street", "character varying(60)"), ("city", "text"),
                 ("zip", "character(5)"), ("rate", "numeric(10,2)")],
                attributes);

            Assert.Empty(
                (await RedeployComparisonAsync(deployment, sql, cancellationToken)).Deltas);
        }
        finally
        {
            await deployment.Database.DropAsync(cancellationToken);
        }
    }

    // The composite type is usable as a column type, which is the point of declaring one.
    [Fact]
    public async Task CompositeType_IsUsableAsAColumnType()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        const string sql = """
            CREATE TYPE addr AS (city text, zip char(5));
            CREATE TABLE customer (id integer NOT NULL, address addr);
            """;

        var deployment = await DeployAsync(sql, cancellationToken);

        try
        {
            await deployment.Database.RunScriptAsync(
                "INSERT INTO customer (id, address) VALUES (1, ROW('Denver', '80202')::addr);",
                cancellationToken: cancellationToken);

            await using (var reader = await deployment.Database.RunScriptReaderAsync(
                "SELECT (address).city FROM customer WHERE id = 1;",
                cancellationToken: cancellationToken))
            {
                Assert.True(await reader.ReadAsync(cancellationToken));
                Assert.Equal("Denver", reader.GetString(0));
            }

            Assert.Empty(
                (await RedeployComparisonAsync(deployment, sql, cancellationToken)).Deltas);
        }
        finally
        {
            await deployment.Database.DropAsync(cancellationToken);
        }
    }

    // The heart of the composite-type feature: an attribute is added in place, against a type
    // a populated table column already uses. DROP TYPE + CREATE TYPE would fail outright here.
    [Fact]
    public async Task CompositeType_AddedAttribute_AltersInPlaceAndPreservesData()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        const string changed = """
            CREATE TYPE addr AS (city text, zip char(5), country text);
            CREATE TABLE customer (id integer NOT NULL, address addr);
            """;

        var deployment = await DeployAsync(
            """
            CREATE TYPE addr AS (city text, zip char(5));
            CREATE TABLE customer (id integer NOT NULL, address addr);
            """,
            cancellationToken);

        try
        {
            await deployment.Database.RunScriptAsync(
                "INSERT INTO customer (id, address) VALUES (1, ROW('Denver', '80202')::addr);",
                cancellationToken: cancellationToken);

            await deployment.Database.PublishAsync(
                await RedeployComparisonAsync(deployment, changed, cancellationToken),
                cancellationToken);

            var attributes = await ReadAttributesAsync(deployment.Database, "addr", cancellationToken);

            Assert.Equal(["city", "zip", "country"], attributes.Select(i => i.Name));

            // The existing row survived, with its original values intact.
            await using (var reader = await deployment.Database.RunScriptReaderAsync(
                "SELECT (address).city, (address).country FROM customer WHERE id = 1;",
                cancellationToken: cancellationToken))
            {
                Assert.True(await reader.ReadAsync(cancellationToken));
                Assert.Equal("Denver", reader.GetString(0));
                Assert.True(reader.IsDBNull(1), "The new attribute must be null on existing rows");
            }

            Assert.Empty(
                (await RedeployComparisonAsync(deployment, changed, cancellationToken)).Deltas);
        }
        finally
        {
            await deployment.Database.DropAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task CompositeType_DroppedAttribute_AltersInPlace()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        const string changed = "CREATE TYPE addr AS (city text);";

        var deployment = await DeployAsync(
            "CREATE TYPE addr AS (city text, country text);", cancellationToken);

        try
        {
            await deployment.Database.PublishAsync(
                await RedeployComparisonAsync(deployment, changed, cancellationToken),
                cancellationToken);

            var attributes = await ReadAttributesAsync(deployment.Database, "addr", cancellationToken);

            Assert.Equal(["city"], attributes.Select(i => i.Name));

            Assert.Empty(
                (await RedeployComparisonAsync(deployment, changed, cancellationToken)).Deltas);
        }
        finally
        {
            await deployment.Database.DropAsync(cancellationToken);
        }
    }

    // PostgreSQL refuses to alter a composite attribute's type while a column uses the type —
    // even with CASCADE — so the deploy must fail with our message rather than emitting SQL
    // that the server rejects.
    [Fact]
    public async Task CompositeType_ChangedAttributeType_FailsRatherThanEmittingInvalidSql()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var deployment = await DeployAsync(
            """
            CREATE TYPE addr AS (zip varchar(5));
            CREATE TABLE customer (id integer NOT NULL, address addr);
            """,
            cancellationToken);

        try
        {
            var comparison = await RedeployComparisonAsync(
                deployment,
                """
                CREATE TYPE addr AS (zip varchar(10));
                CREATE TABLE customer (id integer NOT NULL, address addr);
                """,
                cancellationToken);

            var ex = await Assert.ThrowsAsync<NotSupportedException>(
                () => deployment.Database.PublishAsync(comparison, cancellationToken));

            Assert.Contains("addr", ex.Message);
            Assert.Contains("zip", ex.Message);
        }
        finally
        {
            await deployment.Database.DropAsync(cancellationToken);
        }
    }

    // The row type PostgreSQL creates for every table has typtype = 'c' too, so without the
    // relkind filter every table would produce a phantom composite type — and a redeploy would
    // never be a no-op.
    [Fact]
    public async Task TableRowTypes_AreNotExtractedAsCompositeTypes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        const string sql = """
            CREATE TABLE customer (id integer NOT NULL, name text NOT NULL);
            CREATE TABLE orders (id integer NOT NULL, total numeric(10,2));
            CREATE TYPE addr AS (city text);
            """;

        var deployment = await DeployAsync(sql, cancellationToken);

        try
        {
            var extracted = await deployment.ModelBuilder.ExtractModelAsync(cancellationToken);

            var composites = extracted.Elements
                .Where(i => i.Type == PostgresElementTypes.SqlCompositeType)
                .Select(i => i.Name)
                .ToList();

            Assert.Equal(["addr"], composites);

            Assert.Empty(
                (await RedeployComparisonAsync(deployment, sql, cancellationToken)).Deltas);
        }
        finally
        {
            await deployment.Database.DropAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task CompositeType_InNonPublicSchema_RoundTripsAsANoOp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        const string sql = """
            CREATE SCHEMA shipping;
            CREATE TYPE shipping.addr AS (city text, zip char(5));
            """;

        var deployment = await DeployAsync(sql, cancellationToken);

        try
        {
            Assert.Empty(
                (await RedeployComparisonAsync(deployment, sql, cancellationToken)).Deltas);
        }
        finally
        {
            await deployment.Database.DropAsync(cancellationToken);
        }
    }

    // ---- Range types ----

    [Fact]
    public async Task RangeType_IsCreatedAndRedeploysAsANoOp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        const string sql = "CREATE TYPE floatrange AS RANGE (SUBTYPE = float8);";

        var deployment = await DeployAsync(sql, cancellationToken);

        try
        {
            // The range type works, which is the only real proof the DDL was valid.
            await using (var reader = await deployment.Database.RunScriptReaderAsync(
                // The subtype is double precision, so the operand is cast explicitly — an
                // unadorned 3.0 is numeric, for which no containment operator exists.
                "SELECT '[1.0, 5.0)'::floatrange @> 3.0::float8;",
                cancellationToken: cancellationToken))
            {
                Assert.True(await reader.ReadAsync(cancellationToken));
                Assert.True(reader.GetBoolean(0));
            }

            // The catalog always reports a resolved operator class; the source named none, so
            // this only stays a no-op because the default opclass is omitted from the model.
            Assert.Empty(
                (await RedeployComparisonAsync(deployment, sql, cancellationToken)).Deltas);
        }
        finally
        {
            await deployment.Database.DropAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task RangeType_WithExplicitOpclassAndCollation_RoundTripsAsANoOp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        const string sql = """
            CREATE TYPE textrange AS RANGE (SUBTYPE = text, SUBTYPE_OPCLASS = text_pattern_ops);
            """;

        var deployment = await DeployAsync(sql, cancellationToken);

        try
        {
            Assert.Empty(
                (await RedeployComparisonAsync(deployment, sql, cancellationToken)).Deltas);
        }
        finally
        {
            await deployment.Database.DropAsync(cancellationToken);
        }
    }

    // PostgreSQL creates a multirange type alongside every range type. It is implicit, so it
    // must not be extracted as a declared object of its own.
    [Fact]
    public async Task MultirangeTypes_AreNotExtracted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        const string sql = "CREATE TYPE floatrange AS RANGE (SUBTYPE = float8);";

        var deployment = await DeployAsync(sql, cancellationToken);

        try
        {
            var extracted = await deployment.ModelBuilder.ExtractModelAsync(cancellationToken);

            var ranges = extracted.Elements
                .Where(i => i.Type == PostgresElementTypes.SqlRangeType)
                .Select(i => i.Name)
                .ToList();

            Assert.Equal(["floatrange"], ranges);

            // The companion floatmultirange must not have been modeled as any element — not a
            // range type, not a composite type, not anything else.
            Assert.DoesNotContain(extracted.Elements,
                i => i.Name is string name
                    && name.Contains("multirange", StringComparison.Ordinal));

            // Nor must the constructor functions PostgreSQL creates for the range type, which
            // carry an internal dependency on it and cannot be dropped independently.
            Assert.DoesNotContain(extracted.Elements,
                i => i.Type == PostgresElementTypes.SqlFunction
                    && i.Name is string name
                    && name.Contains("floatrange", StringComparison.Ordinal));
        }
        finally
        {
            await deployment.Database.DropAsync(cancellationToken);
        }
    }

    // A range type has no ALTER form, so a changed subtype must fail with our message rather
    // than emit SQL the server rejects.
    [Fact]
    public async Task RangeType_ChangedSubtype_FailsRatherThanEmittingInvalidSql()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var deployment = await DeployAsync(
            "CREATE TYPE r AS RANGE (SUBTYPE = float8);", cancellationToken);

        try
        {
            var comparison = await RedeployComparisonAsync(
                deployment, "CREATE TYPE r AS RANGE (SUBTYPE = numeric);", cancellationToken);

            var ex = await Assert.ThrowsAsync<NotSupportedException>(
                () => deployment.Database.PublishAsync(comparison, cancellationToken));

            Assert.Contains("double precision", ex.Message);
            Assert.Contains("numeric", ex.Message);
        }
        finally
        {
            await deployment.Database.DropAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task RangeType_Dropped_IsRemovedFromTheDatabase()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var deployment = await DeployAsync(
            "CREATE TYPE doomed AS RANGE (SUBTYPE = float8);", cancellationToken);

        try
        {
            var comparison = SchemaCompare.Compare(
                deployment.Provider,
                await ParseModelAsync("", cancellationToken),
                await deployment.ModelBuilder.ExtractModelAsync(cancellationToken),
                new DeployOptions { DropObjectsNotInSource = true });

            await deployment.Database.PublishAsync(comparison, cancellationToken);

            await using var reader = await deployment.Database.RunScriptReaderAsync(
                "SELECT count(*) FROM pg_type WHERE typname = 'doomed';",
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
