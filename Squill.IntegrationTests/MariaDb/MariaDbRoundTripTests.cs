using Squill.Core;
using Squill.MariaDbParser;
using Squill.Provider.MariaDb;

namespace Squill.IntegrationTests.MariaDb;

/// <summary>
/// End-to-end schema round-trip tests for the MariaDB provider, run against a real MariaDB
/// or MySQL server. Each test parses declarative SQL into a model, publishes it into a fresh
/// database, extracts the database's model, and asserts the two models hash-match — proving
/// the DDL we generate is valid, executable SQL and that a parsed model agrees with one
/// extracted from the live database.
///
/// The scenarios live on this abstract base and run once per engine via the concrete
/// <c>MariaDb*</c> / <c>MySql*</c> subclasses at the bottom of this file.
/// </summary>
public abstract class MariaDbRoundTripTests
{
    protected abstract MariaDbLikeFixture Fixture { get; }

    // Parses the given SQL into a model, publishes it into a fresh database, and asserts the
    // re-extracted database model hash-matches the parsed one. Returns the published model so
    // a caller can make additional assertions about its shape.
    private async Task<Model> AssertRoundTripAsync(string sql, CancellationToken cancellationToken)
    {
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var model = await WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrMariaDbParser(), Fixture.EngineOf()),
            cancellationToken);
        return await RoundTripHarness.AssertRoundTripAsync(
            provider, model, Fixture.EngineName, cancellationToken: cancellationToken);
    }

    [Fact]
    public async Task SimpleTable_RoundTrips()
    {
        await AssertRoundTripAsync("""
            CREATE TABLE distributors
            (
                did  int NOT NULL,
                name varchar(100) NULL
            );
            """, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PrimaryKey_RoundTrips()
    {
        await AssertRoundTripAsync("""
            CREATE TABLE film
            (
                film_id int NOT NULL PRIMARY KEY,
                title   varchar(255) NOT NULL
            );
            """, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AutoIncrement_RoundTrips()
    {
        await AssertRoundTripAsync("""
            CREATE TABLE film
            (
                film_id int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                title   varchar(255) NOT NULL
            );
            """, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CompositePrimaryKey_RoundTrips()
    {
        await AssertRoundTripAsync("""
            CREATE TABLE film_actor
            (
                film_id  int NOT NULL,
                actor_id int NOT NULL,
                PRIMARY KEY (film_id, actor_id)
            );
            """, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task VarcharAndDefaults_RoundTrip()
    {
        await AssertRoundTripAsync("""
            CREATE TABLE account
            (
                id     int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                status varchar(20) NOT NULL DEFAULT 'active',
                logins int NOT NULL DEFAULT 0
            );
            """, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Decimal_RoundTrips()
    {
        await AssertRoundTripAsync("""
            CREATE TABLE payment
            (
                payment_id int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                amount     decimal(10, 2) NOT NULL
            );
            """, TestContext.Current.CancellationToken);
    }

    // enum/set columns carry a value list. The generated DDL must preserve it (issue #73),
    // and the extractor must read it back from information_schema.COLUMN_TYPE in the same
    // spelling, or the parsed and extracted models would not hash-match.
    [Fact]
    public async Task EnumAndSet_RoundTrip()
    {
        await AssertRoundTripAsync("""
            CREATE TABLE film
            (
                film_id          int unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY,
                rating           enum('G','PG','PG-13','R','NC-17') DEFAULT 'G',
                special_features set('Trailers','Commentaries','Deleted Scenes','Behind the Scenes')
            );
            """, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MultiColumnTable_RoundTrips()
    {
        await AssertRoundTripAsync("""
            CREATE TABLE person
            (
                id         int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                first_name varchar(50) NOT NULL,
                last_name  varchar(50) NOT NULL,
                age        int NULL,
                balance    decimal(12, 4) NOT NULL DEFAULT 0
            );
            """, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StandaloneIndex_RoundTrips()
    {
        await AssertRoundTripAsync("""
            CREATE TABLE film
            (
                film_id int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                title   varchar(255) NOT NULL
            );
            CREATE INDEX ix_film_title ON film (title);
            """, TestContext.Current.CancellationToken);
    }

    // MariaDB accepts USING either before the ON clause or after the column list. The grammar
    // binds the two placements to different rules, and the trailing one used to be dropped
    // (issue #123) — which scripted the index without its method. Both must round-trip.
    [Theory]
    [InlineData("CREATE INDEX ix_film_title USING BTREE ON film (title);")]
    [InlineData("CREATE INDEX ix_film_title ON film (title) USING BTREE;")]
    public async Task IndexWithMethod_RoundTrips(string createIndex)
    {
        await AssertRoundTripAsync($"""
            CREATE TABLE film
            (
                film_id int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                title   varchar(255) NOT NULL
            );
            {createIndex}
            """, TestContext.Current.CancellationToken);
    }

    // The same two placements are accepted on an index declared inline in a CREATE TABLE body.
    [Theory]
    [InlineData("INDEX ix_film_title USING BTREE (title)")]
    [InlineData("INDEX ix_film_title (title) USING BTREE")]
    public async Task InlineIndexWithMethod_RoundTrips(string indexDeclaration)
    {
        await AssertRoundTripAsync($"""
            CREATE TABLE film
            (
                film_id int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                title   varchar(255) NOT NULL,
                {indexDeclaration}
            );
            """, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UniqueIndex_RoundTrips()
    {
        await AssertRoundTripAsync("""
            CREATE TABLE account
            (
                id    int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                email varchar(255) NOT NULL,
                CONSTRAINT uq_account_email UNIQUE (email)
            );
            """, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ForeignKey_RoundTrips()
    {
        await AssertRoundTripAsync("""
            CREATE TABLE customer
            (
                id   int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                name varchar(100) NOT NULL
            );
            CREATE TABLE orders
            (
                id          int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                customer_id int NOT NULL,
                CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES customer (id) ON DELETE CASCADE
            );
            """, TestContext.Current.CancellationToken);
    }

    // Two tables referencing each other: no create order satisfies both constraints, so the
    // one closing the cycle is added with ALTER TABLE once both tables exist.
    [Fact]
    public async Task CircularForeignKeys_RoundTrip()
    {
        var model = await AssertRoundTripAsync("""
            CREATE TABLE husband
            (
                id      int NOT NULL PRIMARY KEY,
                wife_id int NULL,
                CONSTRAINT fk_husband_wife FOREIGN KEY (wife_id) REFERENCES wife (id)
            );
            CREATE TABLE wife
            (
                id         int NOT NULL PRIMARY KEY,
                husband_id int NULL,
                CONSTRAINT fk_wife_husband FOREIGN KEY (husband_id) REFERENCES husband (id)
            );
            """, TestContext.Current.CancellationToken);

        // Both directions must survive: deferring one constraint must not lose it, and each
        // must point at the table the source declared.
        var foreignKeys = model.Elements
            .Where(i => i.Type == MariaDbElementTypes.SqlForeignKeyConstraint)
            .ToList();

        Assert.Equal(2, foreignKeys.Count);

        Assert.Contains(foreignKeys, i => ReferencedTable(i) == "wife");
        Assert.Contains(foreignKeys, i => ReferencedTable(i) == "husband");
    }

    // The parser yields each table immediately followed by its own dependents, so the
    // extraction builder must too — grouping every table ahead of every dependent diverges
    // as soon as there is more than one table, and the Merkle hash is order-sensitive.
    // This is the MariaDB counterpart of the Postgres regression in issue #65; the MariaDB
    // builder already interleaves, and these tests keep it that way.
    [Fact]
    public async Task TwoTablesWithPrimaryKeys_RoundTrip()
    {
        await AssertRoundTripAsync("""
            CREATE TABLE zebra (id int NOT NULL PRIMARY KEY);
            CREATE TABLE apple (id int NOT NULL PRIMARY KEY);
            """, TestContext.Current.CancellationToken);
    }

    // Indexes and foreign keys are dependents too, so a table's own must stay with it — and
    // in the order the parser emits them.
    [Fact]
    public async Task MultipleTablesWithIndexesAndForeignKeys_RoundTrip()
    {
        await AssertRoundTripAsync("""
            CREATE TABLE author
            (
                author_id int NOT NULL PRIMARY KEY,
                name      varchar(200) NOT NULL
            );
            CREATE TABLE book
            (
                book_id   int NOT NULL PRIMARY KEY,
                author_id int NOT NULL,
                title     varchar(400) NOT NULL,
                CONSTRAINT fk_book_author FOREIGN KEY (author_id) REFERENCES author (author_id)
            );
            CREATE INDEX ix_book_title ON book (title);
            """, TestContext.Current.CancellationToken);
    }

    // A table carrying a unique constraint (an inline index) and a foreign key at once, so
    // the relative order of the two dependent kinds is pinned rather than left to whichever
    // combination the other tests happen to cover.
    [Fact]
    public async Task TableWithBothUniqueConstraintAndForeignKey_RoundTrips()
    {
        await AssertRoundTripAsync("""
            CREATE TABLE customer
            (
                id int NOT NULL PRIMARY KEY
            );
            CREATE TABLE account
            (
                id          int NOT NULL PRIMARY KEY,
                customer_id int NOT NULL,
                email       varchar(255) NOT NULL,
                CONSTRAINT uq_account_email UNIQUE (email),
                CONSTRAINT fk_account_customer FOREIGN KEY (customer_id) REFERENCES customer (id)
            );
            """, TestContext.Current.CancellationToken);
    }

    // Declared in reverse-alphabetical order, so a builder that sorted tables by name would
    // disagree with the parser, which keeps declaration order.
    [Fact]
    public async Task TablesDeclaredOutOfAlphabeticalOrder_RoundTrip()
    {
        await AssertRoundTripAsync("""
            CREATE TABLE zulu (id int NOT NULL PRIMARY KEY);
            CREATE TABLE yankee (id int NOT NULL PRIMARY KEY);
            CREATE TABLE xray (id int NOT NULL PRIMARY KEY);
            """, TestContext.Current.CancellationToken);
    }

    private static string? ReferencedTable(Element foreignKey)
        => foreignKey.GetRelationship(MariaDbRelationshipNames.ForeignTable)
            ?.Entries.OfType<Reference>().FirstOrDefault()?.Name;
}

// ---- Per-engine bindings: each scenario runs once against MariaDB and once against MySQL. ----

public sealed class MariaDbRoundTripTestsMariaDb(MariaDbFixture fixture)
    : MariaDbRoundTripTests, IClassFixture<MariaDbFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

public sealed class MariaDbRoundTripTestsMySql(MySqlFixture fixture)
    : MariaDbRoundTripTests, IClassFixture<MySqlFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}
