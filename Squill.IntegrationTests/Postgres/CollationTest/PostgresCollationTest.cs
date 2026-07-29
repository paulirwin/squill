using Squill.Core;
using Squill.PostgresParser;
using Squill.Provider.Postgres;
using Squill.TestFramework;

namespace Squill.IntegrationTests.Postgres.CollationTest;

/// <summary>
/// Collation and constraint-attribute coverage for issue #137 (EF Core parity). Scenarios are
/// modelled on the collation and deferrable-constraint migration tests in npgsql/efcore.pg's
/// MigrationsNpgsqlTest; the SQL here is original, written declaratively as Squill users would.
///
/// Every test here asserts the behaviour Squill *should* have, and each one blocked by a known
/// defect carries a <c>[Fact(Skip = ...)]</c> naming its issue, so it turns green on its own
/// once support lands. A column COLLATE, an inline DEFERRABLE foreign key and CREATE COLLATION
/// are supported (#159); a per-column index COLLATE and a table-level DEFERRABLE are still
/// accepted and then quietly dropped (#160).
///
/// "POSIX" is used throughout because it is the one non-default collation present on every
/// stock postgres image, independent of ICU availability or the container's locale.
/// </summary>
public class PostgresCollationTest : PostgresIntegrationTestBase
{
    private static Task<Model> BuildModelAsync(string sql)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrPostgresParser()),
            TestContext.Current.CancellationToken);

    /// <summary>
    /// A column-level COLLATE must build, deploy, and survive a re-extract: the deployed column
    /// carries the declared collation, and redeploying the same source is a no-op.
    /// </summary>
    /// <remarks>
    /// The round-trip half is the fragile part: extraction reports a resolved collation for
    /// every collatable column, so it is recorded only when it differs from the column type's
    /// default. Were it recorded unconditionally, this test's second assertion would fail —
    /// every text column would re-diff on every deploy.
    /// </remarks>
    [Fact]
    public async Task ColumnCollate_RoundTripsWithTheDeclaredCollation()
    {
        const string sql = """
CREATE TABLE people
(
    id  integer PRIMARY KEY,
    ssn character varying(11) COLLATE "POSIX" NOT NULL
);
""";

        var ct = TestContext.Current.CancellationToken;
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
        var model = await BuildModelAsync(sql);

        var testDb = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var target = await dbModelBuilder.ExtractModelAsync(ct);
            await testDb.PublishAsync(SchemaCompare.Compare(provider, model, target), ct);

            // The deployed column must carry the declared collation.
            var collation = await ScalarAsync(testDb, """
SELECT c.collname FROM pg_attribute a
JOIN pg_collation c ON c.oid = a.attcollation
WHERE a.attrelid = 'people'::regclass AND a.attname = 'ssn';
""", ct);

            Assert.Equal("POSIX", collation);

            // And the collation must survive a re-extract, or it re-diffs on every deploy.
            var deployed = await dbModelBuilder.ExtractModelAsync(ct);
            Assert.Empty(SchemaCompare.Compare(provider, model, deployed).Deltas);
        }
        finally
        {
            await testDb.DropAsync(ct);
        }
    }

    /// <summary>
    /// An inline (column-level) DEFERRABLE INITIALLY DEFERRED foreign key must deploy as a
    /// genuinely deferrable constraint.
    /// </summary>
    /// <remarks>
    /// The constraint attributes are a colconstraint alternative of their own, arriving as
    /// sibling nodes of the REFERENCES they qualify rather than as part of it. The pairing with
    /// <see cref="TableLevelDeferrableForeignKey_DeploysAsDeferrable"/> is the interesting part:
    /// the identical constraint written at table level is still parsed and then silently
    /// deployed as NOT deferrable (#160). This spelling now round-trips; its sibling still lies.
    /// </remarks>
    [Fact]
    public async Task InlineDeferrableForeignKey_DeploysAsDeferrable()
    {
        const string sql = """
CREATE TABLE customers
(
    id integer PRIMARY KEY
);

CREATE TABLE orders
(
    id  integer PRIMARY KEY,
    cid integer REFERENCES customers (id) DEFERRABLE INITIALLY DEFERRED
);
""";

        var ct = TestContext.Current.CancellationToken;
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
        var model = await BuildModelAsync(sql);

        var testDb = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);

        try
        {
            var target = await provider.CreateDatabaseModelBuilder(testDb).ExtractModelAsync(ct);
            await testDb.PublishAsync(SchemaCompare.Compare(provider, model, target), ct);

            // The FK is unnamed in the source, so find it by the table it constrains.
            var name = await ScalarAsync(testDb, """
SELECT conname FROM pg_constraint
WHERE conrelid = 'orders'::regclass AND contype = 'f';
""", ct);

            Assert.NotNull(name);

            var (deferrable, deferred) = await ReadDeferrabilityAsync(
                testDb, (string)name, ct);

            Assert.True(deferrable, "The declared constraint is DEFERRABLE.");
            Assert.True(deferred, "The declared constraint is INITIALLY DEFERRED.");
        }
        finally
        {
            await testDb.DropAsync(ct);
        }
    }

    /// <summary>
    /// The table-level spelling of the same foreign key must also deploy as genuinely
    /// deferrable.
    /// </summary>
    /// <remarks>
    /// This takes a different path from the inline form: <c>PostgresVisitor.Constraintelem.cs</c>
    /// never reads the constraint_attributes clause, so DEFERRABLE INITIALLY DEFERRED parses
    /// without complaint and is dropped on the floor. The deployed constraint is a plain,
    /// immediate foreign key that behaves differently from what the source declares — a
    /// mid-transaction insert order the declared schema would permit fails against the deployed
    /// one. Asserted against <c>pg_constraint</c> rather than the model, because neither side of
    /// Squill models deferrability, so a model-level assertion would prove nothing.
    /// </remarks>
    [Fact(Skip = "Blocked by issue #160: PostgresVisitor.Constraintelem.cs never reads the "
                 + "constraint_attributes clause, so DEFERRABLE INITIALLY DEFERRED is parsed "
                 + "and silently discarded.")]
    public async Task TableLevelDeferrableForeignKey_DeploysAsDeferrable()
    {
        const string sql = """
CREATE TABLE customers
(
    id integer PRIMARY KEY
);

CREATE TABLE orders
(
    id  integer PRIMARY KEY,
    cid integer,
    CONSTRAINT fk_orders FOREIGN KEY (cid) REFERENCES customers (id) DEFERRABLE INITIALLY DEFERRED
);
""";

        var ct = TestContext.Current.CancellationToken;
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
        var model = await BuildModelAsync(sql);

        var testDb = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);

        try
        {
            var target = await provider.CreateDatabaseModelBuilder(testDb).ExtractModelAsync(ct);
            await testDb.PublishAsync(SchemaCompare.Compare(provider, model, target), ct);

            var (deferrable, deferred) = await ReadDeferrabilityAsync(testDb, "fk_orders", ct);

            Assert.True(deferrable, "fk_orders is declared DEFERRABLE.");
            Assert.True(deferred, "fk_orders is declared INITIALLY DEFERRED.");
        }
        finally
        {
            await testDb.DropAsync(ct);
        }
    }

    /// <summary>
    /// A per-column COLLATE inside CREATE INDEX must deploy with the declared collation, and
    /// survive a re-extract so the source does not re-diff.
    /// </summary>
    /// <remarks>
    /// The quietest failure of the batch: the grammar's index_elem_options carries a collate_
    /// clause, <c>PostgresVisitor.IndexElem.cs</c> reads asc_desc_, nulls_order_ and class_ but
    /// never collate_, and the provider's index query reads storage_parameters and
    /// filter_predicate but never indcollation. Both sides are blind in the same way, so the
    /// index deploys with the default collation, the re-extracted model matches, and a redeploy
    /// is a clean no-op — the schema is wrong and nothing anywhere reports it.
    /// </remarks>
    [Fact(Skip = "Blocked by issue #160: PostgresVisitor.IndexElem.cs never reads the collate_ "
                 + "clause, so a per-column index COLLATE is parsed and silently discarded.")]
    public async Task IndexElementCollate_DeploysWithTheDeclaredCollation()
    {
        const string sql = """
CREATE TABLE people
(
    id   integer PRIMARY KEY,
    name text
);

CREATE INDEX ix_people_name ON people (name COLLATE "POSIX");
""";

        var ct = TestContext.Current.CancellationToken;
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        // It parses, and it builds a model with the index in it.
        var model = await BuildModelAsync(sql);
        Assert.Single(model.Elements, e => e.Type == PostgresElementTypes.SqlIndex);

        var testDb = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var target = await dbModelBuilder.ExtractModelAsync(ct);
            await testDb.PublishAsync(SchemaCompare.Compare(provider, model, target), ct);

            // The deployed index does not carry "POSIX": its single key column's collation is
            // The deployed index must carry "POSIX" on its single key column.
            var declared = await ScalarAsync(
                testDb, "SELECT oid FROM pg_collation WHERE collname = 'POSIX';", ct);
            Assert.NotNull(declared);

            var actual = await ScalarAsync(testDb, """
SELECT indcollation[0]::text FROM pg_index WHERE indexrelid = 'ix_people_name'::regclass;
""", ct);

            Assert.Equal(
                Convert.ToString(declared, System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToString(actual, System.Globalization.CultureInfo.InvariantCulture));

            // And it must round-trip: the collation has to be modeled on both the parse and the
            // extract side, or the source re-diffs on every deploy.
            var deployed = await dbModelBuilder.ExtractModelAsync(ct);

            Assert.True(
                HashUtility.HashesEqual(model.Hash, deployed.Hash),
                "Parsed and extracted model hashes do not match — the index collation must "
                + "round-trip.");

            Assert.Empty(SchemaCompare.Compare(provider, model, deployed).Deltas);
        }
        finally
        {
            await testDb.DropAsync(ct);
        }
    }

    /// <summary>
    /// Changing a column's collation across two deploys (A: no COLLATE, B: COLLATE "POSIX")
    /// must alter the deployed column's collation.
    /// </summary>
    /// <remarks>
    /// PostgreSQL has no ALTER COLUMN ... SET COLLATE, so the change rides on the TYPE clause.
    /// That also means dropping a collation needs an explicit <c>COLLATE "default"</c>: omitting
    /// the clause keeps the existing collation rather than resetting it.
    /// </remarks>
    [Fact]
    public async Task AlterColumnSetCollation_ChangesTheDeployedCollation()
    {
        const string before = """
CREATE TABLE people
(
    id   integer PRIMARY KEY,
    name text
);
""";
        const string after = """
CREATE TABLE people
(
    id   integer PRIMARY KEY,
    name text COLLATE "POSIX"
);
""";

        var ct = TestContext.Current.CancellationToken;
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var beforeModel = await BuildModelAsync(before);
        var afterModel = await BuildModelAsync(after);

        var testDb = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var empty = await dbModelBuilder.ExtractModelAsync(ct);
            await testDb.PublishAsync(SchemaCompare.Compare(provider, beforeModel, empty), ct);

            // The change must produce a delta and deploy.
            var deployed = await dbModelBuilder.ExtractModelAsync(ct);
            var comparison = SchemaCompare.Compare(provider, afterModel, deployed);
            Assert.NotEmpty(comparison.Deltas);

            await testDb.PublishAsync(comparison, ct);

            var collation = await ScalarAsync(testDb, """
SELECT c.collname FROM pg_attribute a
JOIN pg_collation c ON c.oid = a.attcollation
WHERE a.attrelid = 'people'::regclass AND a.attname = 'name';
""", ct);

            Assert.Equal("POSIX", collation);
        }
        finally
        {
            await testDb.DropAsync(ct);
        }
    }

    /// <summary>
    /// A user-declared CREATE COLLATION must build into a model and deploy, so a project can
    /// declare a non-default collation and reference it from a column.
    /// </summary>
    /// <remarks>
    /// PostgreSQL resolves the declared items into catalog facets and keeps no record of how
    /// they were written — <c>FROM "POSIX"</c> and <c>(LOCALE = 'POSIX', PROVIDER = libc)</c>
    /// store byte-identical rows — so the model carries the resolved facets. That is what makes
    /// the round-trip assertion here hold: the collation is scripted back from what pg_collation
    /// reports, not from the source's spelling.
    /// </remarks>
    [Fact]
    public async Task CreateCollationObject_IsDeployed()
    {
        const string sql = "CREATE COLLATION some_collation (LOCALE = 'POSIX', PROVIDER = libc);";

        var ct = TestContext.Current.CancellationToken;
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
        var model = await BuildModelAsync(sql);

        var testDb = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var target = await dbModelBuilder.ExtractModelAsync(ct);
            await testDb.PublishAsync(SchemaCompare.Compare(provider, model, target), ct);

            var name = await ScalarAsync(
                testDb, "SELECT collname FROM pg_collation WHERE collname = 'some_collation';", ct);

            Assert.Equal("some_collation", name);

            // And it must round-trip, or the declared collation re-diffs on every deploy.
            var deployed = await dbModelBuilder.ExtractModelAsync(ct);
            Assert.Empty(SchemaCompare.Compare(provider, model, deployed).Deltas);
        }
        finally
        {
            await testDb.DropAsync(ct);
        }
    }

    private static async Task<(bool Deferrable, bool Deferred)> ReadDeferrabilityAsync(
        IDatabase database, string constraintName, CancellationToken ct)
    {
        await using var reader = await database.RunScriptReaderAsync(
            $"SELECT condeferrable, condeferred FROM pg_constraint WHERE conname = '{constraintName}';",
            cancellationToken: ct);

        Assert.True(await reader.ReadAsync(ct), $"Constraint {constraintName} was not deployed.");

        return (reader.GetBoolean(0), reader.GetBoolean(1));
    }

    private static async Task<object?> ScalarAsync(
        IDatabase database, string sql, CancellationToken ct)
    {
        await using var reader = await database.RunScriptReaderAsync(sql, cancellationToken: ct);

        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return reader.IsDBNull(0) ? null : reader.GetValue(0);
    }
}
