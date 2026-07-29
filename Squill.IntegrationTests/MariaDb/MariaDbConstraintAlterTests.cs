using System.Data.Common;
using Squill.Core;
using Squill.MariaDbParser;
using Squill.Provider.MariaDb;

namespace Squill.IntegrationTests.MariaDb;

/// <summary>
/// End-to-end constraint-reconciliation tests for the MariaDB provider (issue #137), run
/// against a real MariaDB or MySQL server. Every scenario is an incremental deploy: publish
/// schema A into a fresh database, then publish schema B against the very same database and
/// assert what the engine actually ends up with. That is Squill's equivalent of an EF Core
/// migration test — there are no migration operations, only "deploy A, then deploy B".
///
/// Every test asserts the CORRECT behaviour. Two of Squill's dependent-element rules are what
/// these scenarios really exercise, and both took a fix to get right:
///
///  * Every dependent is reconciled standalone (see
///    <c>DatabaseDependencyAnalyzerBase.IsDroppableStandaloneDependent</c>). A PK and an FK
///    used to be excluded, and <c>SchemaCompare</c> skips every dependent in its main loop, so
///    changing a PK or adding an FK to a table that ALREADY exists produced no delta at all:
///    the deploy reported success and changed nothing. Fixed by issue #157.
///  * A CHECK constraint's raw predicate does not participate in identity
///    (<c>ParticipatesInIdentity</c> returns false for
///    <c>(SqlCheckConstraint, CheckExpression)</c>, deliberately — both engines rewrite a
///    stored predicate, so a declared one could never hash-match). What participates instead is
///    the canonical form carried alongside it, which is what makes redefining a predicate under
///    the same name a change the deploy acts on. Fixed by issue #156.
///
/// The two defects used to mask each other, which is why they are worth naming together: while
/// the predicate was invisible to <see cref="SchemaCompare"/>, no <see cref="RecreateDelta"/>
/// for a SqlCheckConstraint was ever produced, so <c>MariaDbScriptGenerator</c> having no
/// recreate path for one could not be observed.
///
/// UNIQUE is the case where MariaDB diverges from Postgres: this provider never produces a
/// <c>SqlUniqueConstraint</c> element. A <c>CONSTRAINT x UNIQUE (…)</c> and a
/// <c>CREATE UNIQUE INDEX</c> both become a <c>SqlIndex</c> with <c>IsUnique = true</c>,
/// which IS a droppable standalone dependent and IS handled by the script generator in
/// create, recreate and drop — so those scenarios reconcile correctly, and the tests below
/// pin that down rather than assuming the Postgres behaviour.
///
/// The scenarios live on this abstract base and run once per engine via the concrete
/// <c>MariaDb*</c> / <c>MySql*</c> subclasses at the bottom of this file.
/// </summary>
public abstract class MariaDbConstraintAlterTests
{
    protected abstract MariaDbLikeFixture Fixture { get; }

    private Model ParseModel(string sql, CancellationToken cancellationToken)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrMariaDbParser(), Fixture.SchemaProviderOf()),
            cancellationToken).GetAwaiter().GetResult();

    /// <summary>
    /// Deploys schema A into a fresh database, hands the caller the live database plus the
    /// comparison that would take it to schema B, and always drops the database afterwards.
    /// The comparison is produced but NOT published, so a test can assert the delta shape
    /// first and then decide whether publishing it should succeed or throw — which separates
    /// "the diff was wrong" from "the script generator crashed on a correct diff".
    /// </summary>
    /// <param name="assertModelsDiffer">
    /// When true, first asserts the two parsed models do not hash-match. A scenario that
    /// expects ZERO deltas needs this: without it, <c>Assert.Empty</c> would also pass if the
    /// two schemas had accidentally been written identically, and the test would prove nothing.
    /// </param>
    private async Task RunUpgradeAsync(
        string schemaA,
        string schemaB,
        Func<IDatabase, IDatabaseModelBuilder, SchemaComparison, Task> assert,
        DeployOptions? options = null,
        bool assertModelsDiffer = false)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);

        var a = ParseModel(schemaA, cancellationToken);
        var b = ParseModel(schemaB, cancellationToken);

        if (assertModelsDiffer)
        {
            Assert.False(
                HashUtility.HashesEqual(a.Hash, b.Hash),
                $"[{Fixture.EngineName}] The two schemas parse to the same model, so this "
                + "scenario is not exercising the change it claims to.");
        }

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", cancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var empty = await dbModelBuilder.ExtractModelAsync(cancellationToken);
            await testDb.PublishAsync(SchemaCompare.Compare(provider, a, empty), cancellationToken);

            await testDb.ConnectAsync(cancellationToken);

            var deployed = await dbModelBuilder.ExtractModelAsync(cancellationToken);
            var comparison = SchemaCompare.Compare(provider, b, deployed, options);

            await assert(testDb, dbModelBuilder, comparison);
        }
        finally
        {
            await testDb.DropAsync(cancellationToken);
        }
    }

    // ---- UNIQUE: modeled as a unique SqlIndex on MariaDB, and fully reconciled ----

    /// <summary>
    /// Adding a UNIQUE constraint to a table that already exists. There is no CREATE TABLE to
    /// carry the clause, so it must be reconciled on its own — and on MariaDB it is, because
    /// the constraint is modeled as a unique index, which the script generator can create
    /// standalone. The deployed constraint must also actually enforce uniqueness.
    /// </summary>
    [Fact]
    public async Task AddUniqueConstraintToExistingTable_IsCreated()
    {
        await RunUpgradeAsync(
            """
            CREATE TABLE Foo
            (
                FooPK int NOT NULL PRIMARY KEY,
                FooAK int NOT NULL
            );
            """,
            """
            CREATE TABLE Foo
            (
                FooPK int NOT NULL PRIMARY KEY,
                FooAK int NOT NULL,
                CONSTRAINT AK_Foo_FooAK UNIQUE (FooAK)
            );
            """,
            async (testDb, builder, comparison) =>
            {
                var cancellationToken = TestContext.Current.CancellationToken;

                // The diff side works: the unique constraint is a droppable standalone
                // dependent, so it gets a CreateDelta of its own rather than being lost.
                var create = Assert.IsType<CreateDelta>(Assert.Single(comparison.Deltas));
                Assert.Equal(MariaDbElementTypes.SqlIndex, create.Element.Type);
                Assert.True(create.Element.GetProperty<bool?>(MariaDbPropertyNames.IsUnique));

                await testDb.PublishAsync(comparison, cancellationToken);

                Assert.Equal(0, await NonUniqueAsync(testDb, "Foo", "AK_Foo_FooAK"));

                // And it is enforced: the second row with the same FooAK must be rejected.
                await testDb.RunScriptAsync(
                    "INSERT INTO Foo (FooPK, FooAK) VALUES (1, 100);",
                    cancellationToken: cancellationToken);

                await Assert.ThrowsAnyAsync<DbException>(() => testDb.RunScriptAsync(
                    "INSERT INTO Foo (FooPK, FooAK) VALUES (2, 100);",
                    cancellationToken: cancellationToken));
            });
    }

    /// <summary>
    /// The reverse: a UNIQUE constraint present in the database but absent from the source is
    /// dropped, but only under DropObjectsNotInSource. Afterwards the duplicate insert that
    /// the constraint used to reject must succeed.
    /// </summary>
    [Fact]
    public async Task DropUniqueConstraintFromExistingTable_IsDropped()
    {
        await RunUpgradeAsync(
            """
            CREATE TABLE Foo
            (
                FooPK int NOT NULL PRIMARY KEY,
                FooAK int NOT NULL,
                CONSTRAINT AK_Foo_FooAK UNIQUE (FooAK)
            );
            """,
            """
            CREATE TABLE Foo
            (
                FooPK int NOT NULL PRIMARY KEY,
                FooAK int NOT NULL
            );
            """,
            async (testDb, builder, comparison) =>
            {
                var cancellationToken = TestContext.Current.CancellationToken;

                var drop = Assert.IsType<DropDelta>(Assert.Single(comparison.Deltas));
                Assert.Equal(MariaDbElementTypes.SqlIndex, drop.Element.Type);

                await testDb.PublishAsync(comparison, cancellationToken);

                Assert.Null(await NonUniqueAsync(testDb, "Foo", "AK_Foo_FooAK"));

                // No longer enforced.
                await testDb.RunScriptAsync(
                    "INSERT INTO Foo (FooPK, FooAK) VALUES (1, 100), (2, 100);",
                    cancellationToken: cancellationToken);
            },
            new DeployOptions { DropObjectsNotInSource = true });
    }

    /// <summary>
    /// Widening a UNIQUE constraint's column list under the same name. The name matches in
    /// both models so this is neither a create nor a drop: it must be recognised as a changed
    /// definition and reconciled with a drop-and-recreate, since neither engine can redefine
    /// an index in place.
    /// </summary>
    [Fact]
    public async Task ChangeUniqueConstraintColumns_IsRecreated()
    {
        await RunUpgradeAsync(
            """
            CREATE TABLE Foo
            (
                FooPK int NOT NULL PRIMARY KEY,
                FooAK int NOT NULL,
                CONSTRAINT AK_Foo_FooAK UNIQUE (FooAK)
            );
            """,
            """
            CREATE TABLE Foo
            (
                FooPK int NOT NULL PRIMARY KEY,
                FooAK int NOT NULL,
                CONSTRAINT AK_Foo_FooAK UNIQUE (FooAK, FooPK)
            );
            """,
            async (testDb, builder, comparison) =>
            {
                var cancellationToken = TestContext.Current.CancellationToken;

                var recreate = Assert.IsType<RecreateDelta>(Assert.Single(comparison.Deltas));
                Assert.Equal(MariaDbElementTypes.SqlIndex, recreate.SourceElement.Type);

                await testDb.PublishAsync(comparison, cancellationToken);

                Assert.Equal(
                    ["FooAK", "FooPK"],
                    await IndexColumnsAsync(testDb, "Foo", "AK_Foo_FooAK"));

                // The widened constraint no longer rejects a repeated FooAK on its own.
                await testDb.RunScriptAsync(
                    "INSERT INTO Foo (FooPK, FooAK) VALUES (1, 100), (2, 100);",
                    cancellationToken: cancellationToken);
            });
    }

    // ---- CHECK: adding and dropping work; redefining does not ----

    /// <summary>
    /// The batch's control case. Adding a CHECK to an existing table and dropping it again are
    /// both implemented (ALTER TABLE ADD CONSTRAINT / DROP CONSTRAINT), so this reconciles in
    /// both directions. Enforcement is asserted per engine rather than assumed: MariaDB
    /// enforces CHECK from 10.2 and MySQL only from 8.0.16, and both test containers are
    /// current, so both are expected to reject the violating row.
    /// </summary>
    [Fact]
    public async Task AddAndDropCheckConstraint_AreReconciled()
    {
        const string bare =
            """
            CREATE TABLE People
            (
                Id            int NOT NULL PRIMARY KEY,
                DriverLicense int NOT NULL
            );
            """;

        const string withCheck =
            """
            CREATE TABLE People
            (
                Id            int NOT NULL PRIMARY KEY,
                DriverLicense int NOT NULL,
                CONSTRAINT CK_People_Foo CHECK (DriverLicense > 0)
            );
            """;

        await RunUpgradeAsync(
            bare,
            withCheck,
            async (testDb, builder, comparison) =>
            {
                var cancellationToken = TestContext.Current.CancellationToken;

                var create = Assert.IsType<CreateDelta>(Assert.Single(comparison.Deltas));
                Assert.Equal(MariaDbElementTypes.SqlCheckConstraint, create.Element.Type);

                await testDb.PublishAsync(comparison, cancellationToken);

                // The predicate is enforced by the engine, not merely recorded.
                await Assert.ThrowsAnyAsync<DbException>(() => testDb.RunScriptAsync(
                    "INSERT INTO People (Id, DriverLicense) VALUES (1, 0);",
                    cancellationToken: cancellationToken));

                await testDb.RunScriptAsync(
                    "INSERT INTO People (Id, DriverLicense) VALUES (2, 5);",
                    cancellationToken: cancellationToken);

                // Now go back to the bare table: the constraint must be dropped, and the row
                // it used to reject must become insertable.
                var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
                var deployed = await builder.ExtractModelAsync(cancellationToken);

                var down = SchemaCompare.Compare(
                    provider, ParseModel(bare, cancellationToken), deployed,
                    new DeployOptions { DropObjectsNotInSource = true });

                var drop = Assert.IsType<DropDelta>(Assert.Single(down.Deltas));
                Assert.Equal(MariaDbElementTypes.SqlCheckConstraint, drop.Element.Type);

                await testDb.PublishAsync(down, cancellationToken);

                await testDb.RunScriptAsync(
                    "INSERT INTO People (Id, DriverLicense) VALUES (3, 0);",
                    cancellationToken: cancellationToken);
            });
    }

    /// <summary>
    /// Redefining a CHECK predicate under the SAME constraint name must reconcile, so the
    /// declared predicate is the one left in force.
    ///
    /// The predicate's raw text is still excluded from the hash — both engines rewrite a stored
    /// predicate, so a declared one could never hash-match what is read back (issue #120). What
    /// makes this work is the canonical form carried alongside it, which does take part in
    /// identity (issue #156), so a constraint is identified by its name, table AND predicate.
    ///
    /// Two defects had to be fixed together here, because the first masked the second: while the
    /// predicate was invisible to <see cref="SchemaCompare"/> no <see cref="RecreateDelta"/> for
    /// a SqlCheckConstraint was ever produced, so
    /// <c>MariaDbScriptGenerator.GenerateRecreateScript</c> throwing
    /// <see cref="NotImplementedException"/> for one could not be observed.
    /// </summary>
    [Fact]
    public async Task ChangeCheckPredicate_IsApplied()
    {
        const string loose =
            """
            CREATE TABLE People
            (
                Id            int NOT NULL PRIMARY KEY,
                DriverLicense int NOT NULL,
                CONSTRAINT CK_People_Foo CHECK (DriverLicense > 0)
            );
            """;

        const string tight =
            """
            CREATE TABLE People
            (
                Id            int NOT NULL PRIMARY KEY,
                DriverLicense int NOT NULL,
                CONSTRAINT CK_People_Foo CHECK (DriverLicense > 10)
            );
            """;

        // The root cause, asserted directly and without a database: two DIFFERENT schemas must
        // not parse to models with the same hash, or nothing downstream can recover the
        // difference.
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.False(HashUtility.HashesEqual(
            ParseModel(loose, cancellationToken).Hash,
            ParseModel(tight, cancellationToken).Hash));

        await RunUpgradeAsync(
            loose,
            tight,
            async (testDb, builder, comparison) =>
            {
                // The tightened predicate must produce something to deploy.
                Assert.NotEmpty(comparison.Deltas);

                await testDb.PublishAsync(comparison, cancellationToken);

                // The declared predicate must be the one in force: 5 satisfies the old "> 0"
                // but violates the declared "> 10", so it must be rejected.
                await Assert.ThrowsAnyAsync<Exception>(() => testDb.RunScriptAsync(
                    "INSERT INTO People (Id, DriverLicense) VALUES (1, 5);",
                    cancellationToken: cancellationToken));
            });
    }

    // ---- PK and FK: reconciled standalone on an otherwise-unchanged table (#157) ----

    /// <summary>
    /// Moving the PRIMARY KEY to a different column of an existing table must reconcile, so
    /// the deployed key matches the source.
    ///
    /// A PK is a dependent element, so <c>SchemaCompare</c>'s main loop skips it, and the
    /// table's own hash does not change when it moves because the PK is a separate element.
    /// What notices the move is <c>AddRecreateDeltas</c>, which reconciles every standalone
    /// dependent — and until issue #157 a PK was excluded from that by
    /// <c>IsDroppableStandaloneDependent</c>, leaving nothing to catch it at all.
    ///
    /// The move is a redefinition rather than an add or a remove, so it deploys as a
    /// drop-and-recreate: a table may only have one primary key, so the old one has to go first.
    /// </summary>
    [Fact]
    public async Task MovePrimaryKey_IsApplied()
    {
        await RunUpgradeAsync(
            """
            CREATE TABLE Table1
            (
                Id          int NOT NULL,
                AlternatePK int NOT NULL,
                PRIMARY KEY (Id)
            );
            """,
            """
            CREATE TABLE Table1
            (
                Id          int NOT NULL,
                AlternatePK int NOT NULL,
                PRIMARY KEY (AlternatePK)
            );
            """,
            async (testDb, builder, comparison) =>
            {
                var cancellationToken = TestContext.Current.CancellationToken;

                Assert.NotEmpty(comparison.Deltas);

                await testDb.PublishAsync(comparison, cancellationToken);

                // The primary key must be on AlternatePK, as the source declares.
                Assert.Equal(["AlternatePK"], await IndexColumnsAsync(testDb, "Table1", "PRIMARY"));
            },
            assertModelsDiffer: true);
    }

    /// <summary>
    /// Adding a FOREIGN KEY between two tables that already exist must reconcile. It was
    /// blocked for the same reason as the primary key above, and is fixed by the same change
    /// (issue #157): an FK is now reconciled standalone, so a table that is not itself being
    /// created or rebuilt still gets one added with ALTER TABLE.
    ///
    /// The impact is asserted directly rather than only via the catalog: an orphan row that
    /// the declared ON DELETE CASCADE foreign key forbids must be rejected.
    /// </summary>
    [Fact]
    public async Task AddForeignKeyToExistingTables_IsApplied()
    {
        await RunUpgradeAsync(
            """
            CREATE TABLE Customers
            (
                Id   int NOT NULL PRIMARY KEY,
                Name varchar(100) NOT NULL
            );
            CREATE TABLE Orders
            (
                Id         int NOT NULL PRIMARY KEY,
                CustomerId int NOT NULL
            );
            """,
            """
            CREATE TABLE Customers
            (
                Id   int NOT NULL PRIMARY KEY,
                Name varchar(100) NOT NULL
            );
            CREATE TABLE Orders
            (
                Id         int NOT NULL PRIMARY KEY,
                CustomerId int NOT NULL,
                CONSTRAINT FK_Orders_Customers FOREIGN KEY (CustomerId)
                    REFERENCES Customers (Id) ON DELETE CASCADE
            );
            """,
            async (testDb, builder, comparison) =>
            {
                var cancellationToken = TestContext.Current.CancellationToken;

                Assert.NotEmpty(comparison.Deltas);

                await testDb.PublishAsync(comparison, cancellationToken);

                // The declared foreign key must exist on Orders.
                await using (var reader = await testDb.RunScriptReaderAsync(
                    """
                    SELECT CONSTRAINT_NAME
                    FROM information_schema.REFERENTIAL_CONSTRAINTS
                    WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'Orders';
                    """,
                    cancellationToken: cancellationToken))
                {
                    Assert.True(await reader.ReadAsync(cancellationToken),
                        $"[{Fixture.EngineName}] the declared foreign key is missing on Orders.");
                }

                // The user-visible point: an orphan order the declared FK forbids must be
                // rejected, so the deployed database really enforces the source.
                await Assert.ThrowsAnyAsync<Exception>(() => testDb.RunScriptAsync(
                    "INSERT INTO Orders (Id, CustomerId) VALUES (1, 999);",
                    cancellationToken: cancellationToken));
            },
            assertModelsDiffer: true);
    }

    // ---- Indexes ----

    /// <summary>
    /// Turning an existing non-unique index into a unique one under the same name. Indexes are
    /// droppable standalone dependents and the script generator handles them in create,
    /// recreate and drop, so this reconciles via a <see cref="RecreateDelta"/>.
    ///
    /// There is an asymmetry worth pinning here: a unique index is emitted as an inline
    /// UNIQUE KEY clause when its table is created, but as a standalone CREATE UNIQUE INDEX
    /// when it is recreated. The redeploy-no-op assertion at the end proves the two spellings
    /// extract to the same model — otherwise every subsequent deploy would re-diff.
    /// </summary>
    [Fact]
    public async Task ChangeIndexUniqueness_IsRecreated()
    {
        const string unique =
            """
            CREATE TABLE People
            (
                Id int NOT NULL PRIMARY KEY,
                X  int NOT NULL
            );
            CREATE UNIQUE INDEX IX_People_X ON People (X);
            """;

        await RunUpgradeAsync(
            """
            CREATE TABLE People
            (
                Id int NOT NULL PRIMARY KEY,
                X  int NOT NULL
            );
            CREATE INDEX IX_People_X ON People (X);
            """,
            unique,
            async (testDb, builder, comparison) =>
            {
                var cancellationToken = TestContext.Current.CancellationToken;

                var recreate = Assert.IsType<RecreateDelta>(Assert.Single(comparison.Deltas));
                Assert.Equal(MariaDbElementTypes.SqlIndex, recreate.SourceElement.Type);

                await testDb.PublishAsync(comparison, cancellationToken);

                Assert.Equal(0, await NonUniqueAsync(testDb, "People", "IX_People_X"));

                // The recreate spelling (CREATE UNIQUE INDEX) must extract to the same model
                // as the create spelling (inline UNIQUE KEY), or the next deploy would re-diff.
                var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
                var deployed = await builder.ExtractModelAsync(cancellationToken);

                Assert.Empty(SchemaCompare
                    .Compare(provider, ParseModel(unique, cancellationToken), deployed)
                    .Deltas);
            });
    }

    /// <summary>
    /// Renaming an index. There is nothing to match the two definitions to each other, so this
    /// must resolve to a drop of the old name plus a create of the new one — and only under
    /// DropObjectsNotInSource, or the old index would linger.
    /// </summary>
    [Fact]
    public async Task RenameIndex_DropsOldAndCreatesNew()
    {
        await RunUpgradeAsync(
            """
            CREATE TABLE People
            (
                Id        int NOT NULL PRIMARY KEY,
                FirstName varchar(50) NOT NULL
            );
            CREATE INDEX Foo ON People (FirstName);
            """,
            """
            CREATE TABLE People
            (
                Id        int NOT NULL PRIMARY KEY,
                FirstName varchar(50) NOT NULL
            );
            CREATE INDEX Bar ON People (FirstName);
            """,
            async (testDb, builder, comparison) =>
            {
                var cancellationToken = TestContext.Current.CancellationToken;

                Assert.Equal(2, comparison.Deltas.Count);

                var create = Assert.Single(comparison.Deltas.OfType<CreateDelta>());
                Assert.Equal("Bar", SqlName.UnqualifiedOf((string)create.Element.Name!));

                var drop = Assert.Single(comparison.Deltas.OfType<DropDelta>());
                Assert.Equal("Foo", SqlName.UnqualifiedOf((string)drop.Element.Name!));

                await testDb.PublishAsync(comparison, cancellationToken);

                Assert.Null(await NonUniqueAsync(testDb, "People", "Foo"));
                Assert.Equal(1, await NonUniqueAsync(testDb, "People", "Bar"));
            },
            new DeployOptions { DropObjectsNotInSource = true });
    }

    // ---- information_schema helpers ----

    // NON_UNIQUE for the named index (0 = unique, 1 = not), or null if no such index exists.
    private static async Task<long?> NonUniqueAsync(IDatabase database, string table, string index)
    {
        await using var reader = await database.RunScriptReaderAsync(
            $"""
            SELECT NON_UNIQUE
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME = '{table}'
              AND INDEX_NAME = '{index}'
            LIMIT 1;
            """,
            cancellationToken: TestContext.Current.CancellationToken);

        return await reader.ReadAsync(TestContext.Current.CancellationToken)
            ? Convert.ToInt64(reader.GetValue(0))
            : null;
    }

    // The named index's columns, in key order. "PRIMARY" is the primary key's index name on
    // both engines, so this reads a PK's columns too.
    private static async Task<List<string>> IndexColumnsAsync(
        IDatabase database, string table, string index)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var reader = await database.RunScriptReaderAsync(
            $"""
            SELECT COLUMN_NAME
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME = '{table}'
              AND INDEX_NAME = '{index}'
            ORDER BY SEQ_IN_INDEX;
            """,
            cancellationToken: cancellationToken);

        var columns = new List<string>();

        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }
}

// ---- Per-engine bindings: each scenario runs once against MariaDB and once against MySQL. ----

public sealed class MariaDbConstraintAlterTestsMariaDb(MariaDbFixture fixture)
    : MariaDbConstraintAlterTests, IClassFixture<MariaDbFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

public sealed class MariaDbConstraintAlterTestsMySql(MySqlFixture fixture)
    : MariaDbConstraintAlterTests, IClassFixture<MySqlFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}
