using MySqlConnector;
using Squill.Core;
using Squill.MariaDbParser;
using Squill.Provider.MariaDb;

namespace Squill.IntegrationTests.MariaDb;

/// <summary>
/// End-to-end A → B table-migration coverage for the MariaDB provider (issue #137), run
/// against a real MariaDB or MySQL server. Every other MariaDB table test publishes a schema
/// into an empty database; nothing exercised the far more destructive second publish — the
/// <see cref="AlterDelta"/> / <see cref="RebuildTableDelta"/> path that adds, drops, widens,
/// re-nulls and re-defaults columns on a table that already holds rows.
///
/// Each scenario deploys schema A, seeds rows, deploys schema B to the same database, and then
/// asserts three things: the seeded data survived, the re-extracted model hash-matches B, and
/// redeploying B is a no-op (a change that re-diffs forever is as broken as one that fails).
///
/// The scenarios live on this abstract base and run once per engine via the concrete
/// <c>MariaDb*</c> / <c>MySql*</c> subclasses at the bottom of this file.
///
/// Scenario ideas (add/drop/widen/re-null/re-default a column, and inserting a column
/// mid-table) are drawn from the migration tests in
/// PomeloFoundation/Pomelo.EntityFrameworkCore.MySql (MIT, Copyright (c) 2017 Pomelo
/// Foundation); the SQL and assertions here are original and shaped to Squill's declarative
/// deploy model rather than EF's migration builder.
/// </summary>
public abstract class MariaDbTableAlterTests
{
    protected abstract MariaDbLikeFixture Fixture { get; }

    private static Task<Model> ParseModelAsync(string sql, CancellationToken cancellationToken)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrMariaDbParser()),
            cancellationToken);

    /// <summary>
    /// Deploys <paramref name="sqlA"/> into a fresh database, runs <paramref name="seedSql"/>,
    /// deploys <paramref name="sqlB"/> to the same database, and asserts the re-extracted model
    /// hash-matches B and that redeploying B is a no-op. The caller's
    /// <paramref name="assertAfterAsync"/> then runs against a raw connection to the migrated
    /// database, so data and catalog assertions are made against the real server.
    /// </summary>
    private async Task AssertMigrationAsync(
        string sqlA,
        string sqlB,
        string seedSql,
        Func<MySqlConnection, Task> assertAfterAsync,
        DeployOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await RunMigrationAsync(
            sqlA,
            [(sqlB, options)],
            seedSql,
            [assertAfterAsync],
            cancellationToken);
    }

    /// <summary>
    /// The general form: deploy A, seed, then deploy each subsequent schema in turn, running the
    /// matching assertion callback after each. Every step asserts the hash match against the
    /// schema just deployed and the redeploy no-op, so a three-stage scenario (A → B → C) pins
    /// the intermediate state as tightly as the final one.
    /// </summary>
    private async Task RunMigrationAsync(
        string sqlA,
        IReadOnlyList<(string Sql, DeployOptions? Options)> steps,
        string seedSql,
        IReadOnlyList<Func<MySqlConnection, Task>> assertions,
        CancellationToken cancellationToken)
    {
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var databaseName = $"squill_test_{Guid.NewGuid():n}";
        var testDb = await provider.CreateDatabaseAsync(databaseName, cancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var modelA = await ParseModelAsync(sqlA, cancellationToken);

            var empty = await dbModelBuilder.ExtractModelAsync(cancellationToken);
            await testDb.PublishAsync(SchemaCompare.Compare(provider, modelA, empty), cancellationToken);

            // Seed rows so every later assertion can prove the migration preserved data rather
            // than quietly recreating an empty table.
            await using (var seedConnection = await OpenAsync(databaseName, cancellationToken))
            {
                await ExecuteAsync(seedConnection, seedSql, cancellationToken);
            }

            for (var i = 0; i < steps.Count; i++)
            {
                var (sql, options) = steps[i];
                var model = await ParseModelAsync(sql, cancellationToken);

                var current = await dbModelBuilder.ExtractModelAsync(cancellationToken);
                await testDb.PublishAsync(
                    SchemaCompare.Compare(provider, model, current, options), cancellationToken);

                var migrated = await dbModelBuilder.ExtractModelAsync(cancellationToken);

                Assert.True(
                    HashUtility.HashesEqual(model.Hash, migrated.Hash),
                    $"[{Fixture.EngineName}] step {i + 1}: the migrated database does not match "
                    + $"the deployed schema.\nDeclared:  {ModelAssertions.Describe(model)}\n"
                    + $"Extracted: {ModelAssertions.Describe(migrated)}");

                // Redeploying the same schema must find nothing to do; a column that re-diffs
                // on every deploy is as broken as one that fails to deploy at all.
                Assert.Empty(SchemaCompare.Compare(provider, model, migrated, options).Deltas);

                await using var connection = await OpenAsync(databaseName, cancellationToken);
                await assertions[i](connection);
            }
        }
        finally
        {
            await testDb.DropAsync(cancellationToken);
        }
    }

    // ---- Scenarios ----

    /// <summary>
    /// The simplest in-place ALTER: a nullable column appended at the tail. The existing rows
    /// must survive and read NULL for the new column.
    /// </summary>
    [Fact]
    public async Task AddColumn_AltersInPlace_AndPreservesData()
    {
        const string before = """
            CREATE TABLE People
            (
                Id int NOT NULL AUTO_INCREMENT PRIMARY KEY
            );
            """;
        const string after = """
            CREATE TABLE People
            (
                Id   int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                Name varchar(30) NULL
            );
            """;

        await AssertMigrationAsync(
            before, after,
            seedSql: "INSERT INTO People () VALUES (), ();",
            assertAfterAsync: async connection =>
            {
                Assert.Equal(2L, Convert.ToInt64(
                    await ScalarAsync(connection, "SELECT count(*) FROM People;")));

                // The added column exists and is NULL for the pre-existing rows.
                Assert.Equal(2L, Convert.ToInt64(await ScalarAsync(
                    connection, "SELECT count(*) FROM People WHERE Name IS NULL;")));
            },
            cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Dropping a column from a table that still exists is part of that table's ALTER, not a
    /// <see cref="DropDelta"/>, so it is <em>not</em> gated by <c>DropObjectsNotInSource</c> —
    /// but it is recorded as data loss, so the default <c>BlockOnPossibleDataLoss</c> blocks it.
    /// This pins both halves: the block under default options, and the drop when overridden.
    /// </summary>
    [Fact]
    public async Task DropColumn_IsBlockedByDefault_AndDropsWhenAllowed()
    {
        const string before = """
            CREATE TABLE People
            (
                Id         int NOT NULL PRIMARY KEY,
                SomeColumn int NOT NULL
            );
            """;
        const string after = """
            CREATE TABLE People
            (
                Id int NOT NULL PRIMARY KEY
            );
            """;

        var cancellationToken = TestContext.Current.CancellationToken;
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);

        // Under the default options, comparing the two schemas records the column drop as data
        // loss and throws before any SQL runs.
        var modelBefore = await ParseModelAsync(before, cancellationToken);
        var modelAfter = await ParseModelAsync(after, cancellationToken);

        var blocked = Assert.Throws<PossibleDataLossException>(
            () => SchemaCompare.Compare(provider, modelAfter, modelBefore));

        Assert.Contains("SomeColumn", blocked.Message);

        // With the guard overridden the column is dropped in place and the rows survive.
        await AssertMigrationAsync(
            before, after,
            seedSql: "INSERT INTO People (Id, SomeColumn) VALUES (1, 10), (2, 20);",
            assertAfterAsync: async connection =>
            {
                Assert.Equal(2L, Convert.ToInt64(
                    await ScalarAsync(connection, "SELECT count(*) FROM People;")));

                Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(connection, """
                    SELECT count(*) FROM information_schema.COLUMNS
                    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'People'
                      AND COLUMN_NAME = 'SomeColumn';
                    """)));
            },
            options: new DeployOptions { BlockOnPossibleDataLoss = false },
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Widening an integer column: <c>MODIFY COLUMN</c> restates the definition, and the stored
    /// values must be unchanged afterwards.
    /// </summary>
    [Fact]
    public async Task WidenIntToBigint_AltersInPlace_AndPreservesValues()
    {
        const string before = """
            CREATE TABLE People
            (
                Id         int NOT NULL PRIMARY KEY,
                SomeColumn int NOT NULL
            );
            """;
        const string after = """
            CREATE TABLE People
            (
                Id         int NOT NULL PRIMARY KEY,
                SomeColumn bigint NOT NULL
            );
            """;

        await AssertMigrationAsync(
            before, after,
            seedSql: "INSERT INTO People (Id, SomeColumn) VALUES (1, 42), (2, 2147483647);",
            assertAfterAsync: async connection =>
            {
                Assert.Equal("bigint", await DataTypeAsync(connection, "People", "SomeColumn"));

                Assert.Equal(42L, Convert.ToInt64(await ScalarAsync(
                    connection, "SELECT SomeColumn FROM People WHERE Id = 1;")));
                Assert.Equal(2147483647L, Convert.ToInt64(await ScalarAsync(
                    connection, "SELECT SomeColumn FROM People WHERE Id = 2;")));

                // The widened column now holds a value the original int could not.
                await ExecuteAsync(
                    connection, "INSERT INTO People (Id, SomeColumn) VALUES (3, 9223372036854775807);",
                    TestContext.Current.CancellationToken);

                Assert.Equal(9223372036854775807L, Convert.ToInt64(await ScalarAsync(
                    connection, "SELECT SomeColumn FROM People WHERE Id = 3;")));
            },
            cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The same widening on a string column, where the change is to the length facet rather
    /// than the type name.
    /// </summary>
    [Fact]
    public async Task WidenVarcharLength_AltersInPlace_AndPreservesValues()
    {
        const string before = """
            CREATE TABLE People
            (
                Id   int NOT NULL PRIMARY KEY,
                Name varchar(50) NOT NULL
            );
            """;
        const string after = """
            CREATE TABLE People
            (
                Id   int NOT NULL PRIMARY KEY,
                Name varchar(200) NOT NULL
            );
            """;

        await AssertMigrationAsync(
            before, after,
            seedSql: "INSERT INTO People (Id, Name) VALUES (1, 'Gandalf');",
            assertAfterAsync: async connection =>
            {
                Assert.Equal(200L, Convert.ToInt64(await ScalarAsync(connection, """
                    SELECT CHARACTER_MAXIMUM_LENGTH FROM information_schema.COLUMNS
                    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'People'
                      AND COLUMN_NAME = 'Name';
                    """)));

                Assert.Equal("Gandalf", await ScalarAsync(
                    connection, "SELECT Name FROM People WHERE Id = 1;"));

                // A value longer than the original 50 characters now fits.
                await ExecuteAsync(
                    connection,
                    "INSERT INTO People (Id, Name) VALUES (2, REPEAT('x', 120));",
                    TestContext.Current.CancellationToken);

                Assert.Equal(120L, Convert.ToInt64(await ScalarAsync(
                    connection, "SELECT CHAR_LENGTH(Name) FROM People WHERE Id = 2;")));
            },
            cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// NULL → NOT NULL with only non-NULL rows present: the clean case, which both engines
    /// accept and which must preserve every value.
    /// </summary>
    [Fact]
    public async Task MakeColumnNotNull_WithNoNullRows_AltersInPlace()
    {
        const string before = """
            CREATE TABLE People
            (
                Id         int NOT NULL PRIMARY KEY,
                SomeColumn varchar(255) NULL
            );
            """;
        const string after = """
            CREATE TABLE People
            (
                Id         int NOT NULL PRIMARY KEY,
                SomeColumn varchar(255) NOT NULL
            );
            """;

        await AssertMigrationAsync(
            before, after,
            seedSql: "INSERT INTO People (Id, SomeColumn) VALUES (1, 'a'), (2, 'b');",
            assertAfterAsync: async connection =>
            {
                Assert.Equal("NO", await ScalarAsync(connection, """
                    SELECT IS_NULLABLE FROM information_schema.COLUMNS
                    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'People'
                      AND COLUMN_NAME = 'SomeColumn';
                    """));

                Assert.Equal("a", await ScalarAsync(
                    connection, "SELECT SomeColumn FROM People WHERE Id = 1;"));
                Assert.Equal("b", await ScalarAsync(
                    connection, "SELECT SomeColumn FROM People WHERE Id = 2;"));

                // The column now rejects NULL.
                await Assert.ThrowsAsync<MySqlException>(() => ExecuteAsync(
                    connection, "INSERT INTO People (Id, SomeColumn) VALUES (3, NULL);",
                    TestContext.Current.CancellationToken));
            },
            cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The reverse direction, NOT NULL → NULL, which is always safe.
    /// </summary>
    [Fact]
    public async Task MakeColumnNullable_AltersInPlace_AndPreservesValues()
    {
        const string before = """
            CREATE TABLE People
            (
                Id         int NOT NULL PRIMARY KEY,
                SomeColumn varchar(255) NOT NULL
            );
            """;
        const string after = """
            CREATE TABLE People
            (
                Id         int NOT NULL PRIMARY KEY,
                SomeColumn varchar(255) NULL
            );
            """;

        await AssertMigrationAsync(
            before, after,
            seedSql: "INSERT INTO People (Id, SomeColumn) VALUES (1, 'a');",
            assertAfterAsync: async connection =>
            {
                Assert.Equal("YES", await ScalarAsync(connection, """
                    SELECT IS_NULLABLE FROM information_schema.COLUMNS
                    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'People'
                      AND COLUMN_NAME = 'SomeColumn';
                    """));

                Assert.Equal("a", await ScalarAsync(
                    connection, "SELECT SomeColumn FROM People WHERE Id = 1;"));

                // A NULL is now accepted.
                await ExecuteAsync(
                    connection, "INSERT INTO People (Id, SomeColumn) VALUES (2, NULL);",
                    TestContext.Current.CancellationToken);

                Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(
                    connection, "SELECT count(*) FROM People WHERE SomeColumn IS NULL;")));
            },
            cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// NULL → NOT NULL with a NULL row already stored. A schema diff cannot see the stored
    /// data, so Squill emits the same <c>MODIFY COLUMN</c> it would for the clean case and the
    /// server is what refuses. Worth pinning because the two engines refuse with different
    /// error codes — MySQL 1138 ("Invalid use of NULL value"), MariaDB 1265 ("Data truncated
    /// for column") — and because the deploy must propagate that failure rather than leave a
    /// half-applied schema. Both current containers run with <c>STRICT_TRANS_TABLES</c>, so
    /// neither coerces the NULL to <c>''</c>.
    /// </summary>
    [Fact]
    public async Task MakeColumnNotNull_WithExistingNullRow_IsRejectedByBothEngines()
    {
        const string before = """
            CREATE TABLE People
            (
                Id         int NOT NULL PRIMARY KEY,
                SomeColumn varchar(255) NULL
            );
            """;
        const string after = """
            CREATE TABLE People
            (
                Id         int NOT NULL PRIMARY KEY,
                SomeColumn varchar(255) NOT NULL
            );
            """;

        var cancellationToken = TestContext.Current.CancellationToken;
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var databaseName = $"squill_test_{Guid.NewGuid():n}";
        var testDb = await provider.CreateDatabaseAsync(databaseName, cancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var modelBefore = await ParseModelAsync(before, cancellationToken);
            var modelAfter = await ParseModelAsync(after, cancellationToken);

            var empty = await dbModelBuilder.ExtractModelAsync(cancellationToken);
            await testDb.PublishAsync(
                SchemaCompare.Compare(provider, modelBefore, empty), cancellationToken);

            await using (var seedConnection = await OpenAsync(databaseName, cancellationToken))
            {
                await ExecuteAsync(
                    seedConnection,
                    "INSERT INTO People (Id, SomeColumn) VALUES (1, 'a'), (2, NULL);",
                    cancellationToken);
            }

            var current = await dbModelBuilder.ExtractModelAsync(cancellationToken);
            var comparison = SchemaCompare.Compare(provider, modelAfter, current);

            // Squill happily produces the ALTER — the stored data is invisible to a schema
            // diff — so the server is the one that refuses, and the deploy must surface that
            // rather than reporting success.
            var error = await Assert.ThrowsAsync<MySqlException>(
                () => testDb.PublishAsync(comparison, cancellationToken));

            // The engines diverge only in how they say no. MySQL rejects the NULL itself
            // (1138, "Invalid use of NULL value"); MariaDB reports it as a truncation of the
            // value it would otherwise have coerced (1265, "Data truncated for column ...").
            // Both are strict-mode refusals — MariaDB's containers have shipped with
            // STRICT_TRANS_TABLES in sql_mode for years, so the older lore that MariaDB
            // silently coerces the NULL to '' does not hold on a current server.
            var expectedError = Fixture.EngineName == "MySQL"
                ? MySqlErrorCode.InvalidUseOfNull
                : MySqlErrorCode.WarningDataTruncated;

            Assert.Equal(expectedError, error.ErrorCode);

            // The failed ALTER left the column, and both rows, exactly as they were.
            await using var connection = await OpenAsync(databaseName, cancellationToken);

            Assert.Equal("YES", await ScalarAsync(connection, """
                SELECT IS_NULLABLE FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'People'
                  AND COLUMN_NAME = 'SomeColumn';
                """));

            Assert.Equal(2L, Convert.ToInt64(
                await ScalarAsync(connection, "SELECT count(*) FROM People;")));
            Assert.Null(await ScalarAsync(
                connection, "SELECT SomeColumn FROM People WHERE Id = 2;"));
        }
        finally
        {
            await testDb.DropAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Adding, changing and then removing a column <c>DEFAULT</c>. The removal is the risky
    /// direction: <c>MODIFY COLUMN</c> restates the whole definition, so omitting the DEFAULT
    /// clause is what drops it — if the generator emitted no ALTER at all, or emitted one that
    /// kept the default, the column would silently keep a value the source no longer declares.
    /// </summary>
    [Fact]
    public async Task AddChangeAndRemoveDefault_IsAppliedAtEachStep()
    {
        const string noDefault = """
            CREATE TABLE People
            (
                Id  int NOT NULL PRIMARY KEY,
                Sum int NOT NULL
            );
            """;
        const string defaultThree = """
            CREATE TABLE People
            (
                Id  int NOT NULL PRIMARY KEY,
                Sum int NOT NULL DEFAULT 3
            );
            """;
        const string defaultSeven = """
            CREATE TABLE People
            (
                Id  int NOT NULL PRIMARY KEY,
                Sum int NOT NULL DEFAULT 7
            );
            """;

        await RunMigrationAsync(
            noDefault,
            [(defaultThree, null), (defaultSeven, null), (noDefault, null)],
            seedSql: "INSERT INTO People (Id, Sum) VALUES (1, 100);",
            assertions:
            [
                async connection =>
                {
                    Assert.Equal("3", await ColumnDefaultAsync(connection, "People", "Sum"));

                    // The default is live: an insert that omits Sum picks it up.
                    await ExecuteAsync(connection, "INSERT INTO People (Id) VALUES (2);",
                        TestContext.Current.CancellationToken);
                    Assert.Equal(3L, Convert.ToInt64(await ScalarAsync(
                        connection, "SELECT Sum FROM People WHERE Id = 2;")));
                },
                async connection =>
                {
                    Assert.Equal("7", await ColumnDefaultAsync(connection, "People", "Sum"));

                    await ExecuteAsync(connection, "INSERT INTO People (Id) VALUES (3);",
                        TestContext.Current.CancellationToken);
                    Assert.Equal(7L, Convert.ToInt64(await ScalarAsync(
                        connection, "SELECT Sum FROM People WHERE Id = 3;")));
                },
                async connection =>
                {
                    // The default is gone from the catalog...
                    Assert.Null(await ColumnDefaultAsync(connection, "People", "Sum"));

                    // ...and the seeded row is untouched by all three ALTERs.
                    Assert.Equal(100L, Convert.ToInt64(await ScalarAsync(
                        connection, "SELECT Sum FROM People WHERE Id = 1;")));
                },
            ],
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Adding and then removing <c>ON UPDATE CURRENT_TIMESTAMP</c>. Creating a column that has
    /// the clause is already covered by <see cref="MariaDbFunctionDefaultTests"/>; the
    /// transition in either direction is not, and it goes through the same
    /// <c>MODIFY COLUMN</c> restatement as a default change. Both engines report the clause in
    /// <c>information_schema.COLUMNS.EXTRA</c>, spelled differently, so the assertion matches
    /// on the "on update" prefix rather than the exact text.
    /// </summary>
    [Fact]
    public async Task AddAndRemoveOnUpdateCurrentTimestamp_IsAppliedAtEachStep()
    {
        const string withoutOnUpdate = """
            CREATE TABLE actor
            (
                actor_id    int NOT NULL PRIMARY KEY,
                last_update timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            """;
        const string withOnUpdate = """
            CREATE TABLE actor
            (
                actor_id    int NOT NULL PRIMARY KEY,
                last_update timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
            );
            """;

        await RunMigrationAsync(
            withoutOnUpdate,
            [(withOnUpdate, null), (withoutOnUpdate, null)],
            seedSql: "INSERT INTO actor (actor_id) VALUES (1);",
            assertions:
            [
                async connection =>
                {
                    var extra = await ExtraAsync(connection, "actor", "last_update");
                    Assert.Contains("on update", extra, StringComparison.OrdinalIgnoreCase);

                    // The clause is live: touching the row refreshes the stamp.
                    await ExecuteAsync(
                        connection,
                        "UPDATE actor SET actor_id = 1 WHERE actor_id = 1;",
                        TestContext.Current.CancellationToken);

                    Assert.Equal(1L, Convert.ToInt64(
                        await ScalarAsync(connection, "SELECT count(*) FROM actor;")));
                },
                async connection =>
                {
                    var extra = await ExtraAsync(connection, "actor", "last_update");
                    Assert.DoesNotContain("on update", extra, StringComparison.OrdinalIgnoreCase);

                    // The default itself survived the removal of the auto-refresh clause.
                    Assert.Contains(
                        "current_timestamp",
                        await ColumnDefaultAsync(connection, "actor", "last_update") ?? "",
                        StringComparison.OrdinalIgnoreCase);
                },
            ],
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A column inserted between two existing ones cannot be appended, so
    /// <c>TableDiffAnalyzerBase.RequiresRebuild</c> forces a full create-copy-drop-rename
    /// rebuild — the most destructive path in the tool, and one that had never run against a
    /// live MariaDB or MySQL server. Every seeded row must come through with its values, the
    /// new column must land in the declared physical position, and the primary key must be back
    /// on the rebuilt table.
    /// </summary>
    [Fact]
    public async Task InsertColumnMidTable_RebuildsTable_AndPreservesData()
    {
        const string before = """
            CREATE TABLE People
            (
                Id int NOT NULL PRIMARY KEY,
                Z  int NULL
            );
            """;
        const string after = """
            CREATE TABLE People
            (
                Id int NOT NULL PRIMARY KEY,
                M  int NULL,
                Z  int NULL
            );
            """;

        await AssertMigrationAsync(
            before, after,
            seedSql: "INSERT INTO People (Id, Z) VALUES (1, 10), (2, 20), (3, 30);",
            assertAfterAsync: async connection =>
            {
                // Every row survived the rebuild, with its Z value.
                Assert.Equal(3L, Convert.ToInt64(
                    await ScalarAsync(connection, "SELECT count(*) FROM People;")));
                Assert.Equal(10L, Convert.ToInt64(await ScalarAsync(
                    connection, "SELECT Z FROM People WHERE Id = 1;")));
                Assert.Equal(20L, Convert.ToInt64(await ScalarAsync(
                    connection, "SELECT Z FROM People WHERE Id = 2;")));
                Assert.Equal(30L, Convert.ToInt64(await ScalarAsync(
                    connection, "SELECT Z FROM People WHERE Id = 3;")));

                // The inserted column is NULL for the copied rows and sits where declared.
                Assert.Equal(3L, Convert.ToInt64(await ScalarAsync(
                    connection, "SELECT count(*) FROM People WHERE M IS NULL;")));

                Assert.Equal(2L, Convert.ToInt64(await ScalarAsync(connection, """
                    SELECT ORDINAL_POSITION FROM information_schema.COLUMNS
                    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'People'
                      AND COLUMN_NAME = 'M';
                    """)));

                // The primary key came back with the rebuilt table.
                Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(connection, """
                    SELECT count(*) FROM information_schema.TABLE_CONSTRAINTS
                    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'People'
                      AND CONSTRAINT_TYPE = 'PRIMARY KEY';
                    """)));
            },
            cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The same mid-table insert on an <c>AUTO_INCREMENT</c> table. The rebuild copies the key
    /// values into a freshly created table, so unless the copy leaves the auto-increment counter
    /// past the highest copied value, the next insert collides with an existing key. (Both
    /// engines recompute the counter from the table's contents on insert, which is what makes
    /// this work — the Postgres provider has to advance its identity sequence explicitly.)
    /// </summary>
    [Fact]
    public async Task InsertColumnMidTable_WithAutoIncrement_RebuildsAndKeepsKeysUsable()
    {
        const string before = """
            CREATE TABLE People
            (
                Id   int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                Name varchar(50) NOT NULL
            );
            """;
        const string after = """
            CREATE TABLE People
            (
                Id       int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                Nickname varchar(50) NULL,
                Name     varchar(50) NOT NULL
            );
            """;

        await AssertMigrationAsync(
            before, after,
            seedSql: "INSERT INTO People (Name) VALUES ('Frodo'), ('Sam');",
            assertAfterAsync: async connection =>
            {
                // Both rows kept their generated keys through the rebuild.
                Assert.Equal("Frodo", await ScalarAsync(
                    connection, "SELECT Name FROM People WHERE Id = 1;"));
                Assert.Equal("Sam", await ScalarAsync(
                    connection, "SELECT Name FROM People WHERE Id = 2;"));

                // A fresh insert must get a new key rather than colliding with a copied one.
                await ExecuteAsync(
                    connection, "INSERT INTO People (Name) VALUES ('Merry');",
                    TestContext.Current.CancellationToken);

                Assert.Equal(3L, Convert.ToInt64(await ScalarAsync(
                    connection, "SELECT Id FROM People WHERE Name = 'Merry';")));
                Assert.Equal(3L, Convert.ToInt64(
                    await ScalarAsync(connection, "SELECT count(*) FROM People;")));
            },
            cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A rebuild of a table another table's foreign key points at. The rebuild renames the table
    /// aside and drops it, which the inbound FK would block, so the generator drops the FK first
    /// and recreates it afterwards. Both tables' rows must survive and the FK must be enforcing
    /// again at the end.
    /// </summary>
    [Fact]
    public async Task RebuildReferencedTable_ReconcilesInboundForeignKey_AndPreservesData()
    {
        const string before = """
            CREATE TABLE customer
            (
                id    int NOT NULL PRIMARY KEY,
                email varchar(320) NOT NULL
            );
            CREATE TABLE orders
            (
                id          int NOT NULL PRIMARY KEY,
                customer_id int NOT NULL,
                CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES customer (id)
            );
            """;
        // full_name is inserted between the existing columns, so customer must be rebuilt.
        const string after = """
            CREATE TABLE customer
            (
                id        int NOT NULL PRIMARY KEY,
                full_name varchar(200) NULL,
                email     varchar(320) NOT NULL
            );
            CREATE TABLE orders
            (
                id          int NOT NULL PRIMARY KEY,
                customer_id int NOT NULL,
                CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES customer (id)
            );
            """;

        await AssertMigrationAsync(
            before, after,
            seedSql: """
                INSERT INTO customer (id, email) VALUES (1, 'a@example.com');
                INSERT INTO orders (id, customer_id) VALUES (10, 1);
                """,
            assertAfterAsync: async connection =>
            {
                Assert.Equal(1L, Convert.ToInt64(
                    await ScalarAsync(connection, "SELECT count(*) FROM customer;")));
                Assert.Equal(1L, Convert.ToInt64(
                    await ScalarAsync(connection, "SELECT count(*) FROM orders;")));
                Assert.Equal("a@example.com", await ScalarAsync(
                    connection, "SELECT email FROM customer WHERE id = 1;"));

                // The FK is back and enforcing.
                Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(connection, """
                    SELECT count(*) FROM information_schema.REFERENTIAL_CONSTRAINTS
                    WHERE CONSTRAINT_SCHEMA = DATABASE() AND TABLE_NAME = 'orders'
                      AND REFERENCED_TABLE_NAME = 'customer';
                    """)));

                await Assert.ThrowsAsync<MySqlException>(() => ExecuteAsync(
                    connection, "INSERT INTO orders (id, customer_id) VALUES (11, 999);",
                    TestContext.Current.CancellationToken));
            },
            cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A declarative tool cannot infer a rename — a column that vanished and one that appeared
    /// are just a drop and an add — so renaming a column in the source is deployed as exactly
    /// that, and the data in the old column is lost. Because the drop is recorded as data loss,
    /// the default options block it; overriding the guard performs the drop + add.
    /// </summary>
    [Fact]
    public async Task RenameColumn_IsDeployedAsDropAndAdd()
    {
        const string before = """
            CREATE TABLE People
            (
                Id         int NOT NULL PRIMARY KEY,
                SomeColumn int NULL
            );
            """;
        const string after = """
            CREATE TABLE People
            (
                Id              int NOT NULL PRIMARY KEY,
                SomeOtherColumn int NULL
            );
            """;

        var cancellationToken = TestContext.Current.CancellationToken;
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);

        var modelBefore = await ParseModelAsync(before, cancellationToken);
        var modelAfter = await ParseModelAsync(after, cancellationToken);

        // Under the default options the drop half is caught by the data-loss guard.
        var blocked = Assert.Throws<PossibleDataLossException>(
            () => SchemaCompare.Compare(provider, modelAfter, modelBefore));

        Assert.Contains("SomeColumn", blocked.Message);

        await AssertMigrationAsync(
            before, after,
            seedSql: "INSERT INTO People (Id, SomeColumn) VALUES (1, 42), (2, 43);",
            assertAfterAsync: async connection =>
            {
                // The rows themselves survive; only the renamed column's values are gone.
                Assert.Equal(2L, Convert.ToInt64(
                    await ScalarAsync(connection, "SELECT count(*) FROM People;")));
                Assert.Equal(2L, Convert.ToInt64(await ScalarAsync(
                    connection, "SELECT count(*) FROM People WHERE SomeOtherColumn IS NULL;")));

                Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(connection, """
                    SELECT count(*) FROM information_schema.COLUMNS
                    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'People'
                      AND COLUMN_NAME = 'SomeColumn';
                    """)));
            },
            options: new DeployOptions { BlockOnPossibleDataLoss = false },
            cancellationToken: cancellationToken);
    }

    // ---- Helpers ----

    private async Task<MySqlConnection> OpenAsync(string databaseName, CancellationToken cancellationToken)
    {
        var connectionString = new MySqlConnectionStringBuilder(Fixture.ConnectionString)
        {
            Database = databaseName
        }.ConnectionString;

        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task ExecuteAsync(
        MySqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<object?> ScalarAsync(MySqlConnection connection, string sql)
    {
        await using var command = new MySqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value is DBNull ? null : value;
    }

    private static async Task<string?> DataTypeAsync(
        MySqlConnection connection, string table, string column)
        => (string?)await ScalarAsync(connection, $"""
            SELECT DATA_TYPE FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}'
              AND COLUMN_NAME = '{column}';
            """);

    private static async Task<string?> ColumnDefaultAsync(
        MySqlConnection connection, string table, string column)
        => (string?)await ScalarAsync(connection, $"""
            SELECT COLUMN_DEFAULT FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}'
              AND COLUMN_NAME = '{column}';
            """);

    private static async Task<string> ExtraAsync(
        MySqlConnection connection, string table, string column)
        => (string?)await ScalarAsync(connection, $"""
            SELECT EXTRA FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}'
              AND COLUMN_NAME = '{column}';
            """) ?? "";
}

// ---- Per-engine bindings: each scenario runs once against MariaDB and once against MySQL. ----

public sealed class MariaDbTableAlterTestsMariaDb(MariaDbFixture fixture)
    : MariaDbTableAlterTests, IClassFixture<MariaDbFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

public sealed class MariaDbTableAlterTestsMySql(MySqlFixture fixture)
    : MariaDbTableAlterTests, IClassFixture<MySqlFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}
