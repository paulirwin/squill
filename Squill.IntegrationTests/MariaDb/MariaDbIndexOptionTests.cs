using MySqlConnector;
using Squill.Core;
using Squill.MariaDbParser;
using Squill.Provider.MariaDb;

namespace Squill.IntegrationTests.MariaDb;

/// <summary>
/// Round trips for the index options of issue #211, run against a real MariaDB or MySQL server.
/// The whole <c>indexOption</c> list used to be walked only to recover a trailing <c>USING</c>,
/// so an index declaring <c>COMMENT</c> or a visibility keyword deployed as if none of it were
/// written.
///
/// <para>
/// The visibility keyword is why these must run on both engines rather than one: measured,
/// MariaDB spells it <c>IGNORED</c> and MySQL spells it <c>INVISIBLE</c>, and each rejects the
/// other's keyword with a <em>syntax error</em>. A single-engine test would pass while the
/// generated DDL failed to parse on the other. The catalog columns diverge the same way
/// (<c>STATISTICS.IGNORED</c> versus <c>STATISTICS.IS_VISIBLE</c>), so each engine's extraction
/// path is exercised only by its own run.
/// </para>
///
/// <para>
/// As in <see cref="MariaDbIndexFidelityTests"/>, a hash-matching round trip is not enough on
/// its own: source and extraction can be blind in the same way and agree with each other while
/// having deployed the wrong index. So each test also reads the deployed shape straight from
/// <c>information_schema</c>.
/// </para>
/// </summary>
public abstract class MariaDbIndexOptionTests
{
    protected abstract MariaDbLikeFixture Fixture { get; }

    private bool IsMySql => Fixture.EngineName == "MySQL";

    /// <summary>The visibility keyword this engine parses; the other is a syntax error here.</summary>
    private string HiddenKeyword => IsMySql ? "INVISIBLE" : "IGNORED";

    private Model ParseModel(string sql, CancellationToken cancellationToken)
        => WorkspaceModelBuilding.BuildModelAsync(
                sql,
                ws => new ParserWorkspaceModelBuilder(ws, new AntlrMariaDbParser(), Fixture.SchemaProviderOf()),
                cancellationToken)
            .GetAwaiter().GetResult();

    private async Task DeployAndInspectAsync(
        string sql,
        Func<Model, MySqlConnection, Task> assert,
        CancellationToken cancellationToken)
    {
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var parsedModel = ParseModel(sql, cancellationToken);

        var databaseName = $"squill_test_{Guid.NewGuid():n}";
        var testDb = await provider.CreateDatabaseAsync(databaseName, cancellationToken);
        var modelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var empty = await modelBuilder.ExtractModelAsync(cancellationToken);
            await testDb.PublishAsync(
                SchemaCompare.Compare(provider, parsedModel, empty), cancellationToken);

            var extracted = await modelBuilder.ExtractModelAsync(cancellationToken);

            Assert.True(
                HashUtility.HashesEqual(parsedModel.Hash, extracted.Hash),
                $"[{Fixture.EngineName}] Parsed and extracted model hashes do not match.\n"
                + $"Parsed:    {ModelAssertions.Describe(parsedModel)}\n"
                + $"Extracted: {ModelAssertions.Describe(extracted)}");

            // Redeploying the same source must be a no-op. An option that failed to round-trip
            // shows up here as a spurious drop-and-recreate.
            Assert.Empty(SchemaCompare.Compare(provider, parsedModel, extracted).Deltas);

            await using var connection = new MySqlConnection(
                new MySqlConnectionStringBuilder(Fixture.ConnectionString) { Database = databaseName }
                    .ConnectionString);
            await connection.OpenAsync(cancellationToken);

            await assert(extracted, connection);
        }
        finally
        {
            await testDb.DropAsync(cancellationToken);
        }
    }

    private static async Task<string?> ScalarAsync(
        MySqlConnection connection, string sql, string indexName, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("ix", indexName);

        var value = await command.ExecuteScalarAsync(cancellationToken);

        return value is null or DBNull ? null : Convert.ToString(value);
    }

    [Fact]
    public async Task IndexComment_RoundTripsAndIsDeployed()
    {
        var ct = TestContext.Current.CancellationToken;

        await DeployAndInspectAsync(
            """
            CREATE TABLE t (a INT, body TEXT);
            CREATE INDEX ix_commented ON t (a) COMMENT 'why this exists';
            """,
            async (model, connection) =>
            {
                var index = Assert.Single(
                    model.Elements,
                    e => e.Type == MariaDbElementTypes.SqlIndex && e.Name == "ix_commented");

                Assert.Equal(
                    "why this exists",
                    index.GetProperty<string>(MariaDbPropertyNames.Comment));

                // The comment the engine actually stored, not merely what we round-tripped.
                var stored = await ScalarAsync(
                    connection,
                    """
                    SELECT INDEX_COMMENT FROM information_schema.STATISTICS
                    WHERE TABLE_SCHEMA = DATABASE() AND INDEX_NAME = @ix LIMIT 1;
                    """,
                    "ix_commented",
                    ct);

                Assert.Equal("why this exists", stored);
            },
            ct);
    }

    /// <summary>
    /// The load-bearing negative case: an index declaring no comment must carry no property, or
    /// it could never hash-match an extraction where the catalog reports the empty string.
    /// </summary>
    [Fact]
    public async Task IndexWithoutComment_RoundTripsWithNoCommentProperty()
    {
        var ct = TestContext.Current.CancellationToken;

        await DeployAndInspectAsync(
            """
            CREATE TABLE t (a INT);
            CREATE INDEX ix_plain ON t (a);
            """,
            (model, _) =>
            {
                var index = Assert.Single(
                    model.Elements,
                    e => e.Type == MariaDbElementTypes.SqlIndex && e.Name == "ix_plain");

                Assert.Null(index.GetProperty<string>(MariaDbPropertyNames.Comment));

                return Task.CompletedTask;
            },
            ct);
    }

    /// <summary>
    /// The engine-specific scenario. Each engine gets its own keyword, and the assertion reads
    /// whichever catalog column that engine reports it in, proving the DDL parsed, the flag
    /// took effect, and the model round-tripped, on both engines.
    /// </summary>
    [Fact]
    public async Task HiddenIndex_RoundTripsAndIsDeployedHidden()
    {
        var ct = TestContext.Current.CancellationToken;

        await DeployAndInspectAsync(
            $"""
            CREATE TABLE t (a INT);
            CREATE INDEX ix_hidden ON t (a) {HiddenKeyword};
            """,
            async (model, connection) =>
            {
                var index = Assert.Single(
                    model.Elements,
                    e => e.Type == MariaDbElementTypes.SqlIndex && e.Name == "ix_hidden");

                Assert.True(index.GetProperty<bool?>(MariaDbPropertyNames.IsHiddenFromOptimizer));

                // MySQL's IS_VISIBLE is 'YES' when the optimizer uses the index; MariaDB's
                // IGNORED is 'YES' when it does not. Both mean hidden here.
                var column = IsMySql ? "IS_VISIBLE" : "IGNORED";
                var stored = await ScalarAsync(
                    connection,
                    $"""
                    SELECT {column} FROM information_schema.STATISTICS
                    WHERE TABLE_SCHEMA = DATABASE() AND INDEX_NAME = @ix LIMIT 1;
                    """,
                    "ix_hidden",
                    ct);

                Assert.Equal(IsMySql ? "NO" : "YES", stored);
            },
            ct);
    }

    [Fact]
    public async Task VisibleIndex_RoundTripsAsNotHidden()
    {
        var ct = TestContext.Current.CancellationToken;

        await DeployAndInspectAsync(
            """
            CREATE TABLE t (a INT);
            CREATE INDEX ix_visible ON t (a);
            """,
            (model, _) =>
            {
                var index = Assert.Single(
                    model.Elements,
                    e => e.Type == MariaDbElementTypes.SqlIndex && e.Name == "ix_visible");

                Assert.Null(index.GetProperty<bool?>(MariaDbPropertyNames.IsHiddenFromOptimizer));

                return Task.CompletedTask;
            },
            ct);
    }

    /// <summary>
    /// A unique index is written into the table body as a UNIQUE KEY rather than as a CREATE
    /// INDEX, so it takes a different rendering path and needs its own round trip.
    /// </summary>
    [Fact]
    public async Task UniqueIndexComment_RoundTripsAndIsDeployed()
    {
        var ct = TestContext.Current.CancellationToken;

        await DeployAndInspectAsync(
            """
            CREATE TABLE t (a INT, UNIQUE KEY ix_unique (a) COMMENT 'unique note');
            """,
            async (model, connection) =>
            {
                var index = Assert.Single(
                    model.Elements,
                    e => e.Type == MariaDbElementTypes.SqlIndex && e.Name == "ix_unique");

                Assert.Equal(
                    "unique note", index.GetProperty<string>(MariaDbPropertyNames.Comment));

                var stored = await ScalarAsync(
                    connection,
                    """
                    SELECT INDEX_COMMENT FROM information_schema.STATISTICS
                    WHERE TABLE_SCHEMA = DATABASE() AND INDEX_NAME = @ix LIMIT 1;
                    """,
                    "ix_unique",
                    ct);

                Assert.Equal("unique note", stored);
            },
            ct);
    }

    /// <summary>
    /// Both options on one index, so the trailing clauses are proven to compose in an order the
    /// engine accepts rather than only working in isolation.
    /// </summary>
    [Fact]
    public async Task CommentAndVisibility_RoundTripTogether()
    {
        var ct = TestContext.Current.CancellationToken;

        await DeployAndInspectAsync(
            $"""
            CREATE TABLE t (a INT, b INT);
            CREATE INDEX ix_both ON t (a, b) COMMENT 'both' {HiddenKeyword};
            """,
            (model, _) =>
            {
                var index = Assert.Single(
                    model.Elements,
                    e => e.Type == MariaDbElementTypes.SqlIndex && e.Name == "ix_both");

                Assert.Equal("both", index.GetProperty<string>(MariaDbPropertyNames.Comment));
                Assert.True(index.GetProperty<bool?>(MariaDbPropertyNames.IsHiddenFromOptimizer));

                return Task.CompletedTask;
            },
            ct);
    }
}

// ---- Per-engine bindings: each scenario runs once against MariaDB and once against MySQL. ----

public sealed class MariaDbIndexOptionTestsMariaDb(MariaDbFixture fixture)
    : MariaDbIndexOptionTests, IClassFixture<MariaDbFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

public sealed class MariaDbIndexOptionTestsMySql(MySqlFixture fixture)
    : MariaDbIndexOptionTests, IClassFixture<MySqlFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}
