using Squill.Core;
using Squill.PostgresParser;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.MultiTableOrderTest;

/// <summary>
/// A model parsed from source must hash-match one extracted from the database it was
/// published to, however many tables it holds (issue #65). The Merkle hash is
/// order-sensitive, so the two builders have to agree on element order — the parser yields
/// each table immediately followed by its own dependents, so extraction must too.
/// </summary>
public class PostgresMultiTableOrderTest : PostgresIntegrationTestBase
{
    private async Task<(Model Parsed, Model Published)> RoundTripAsync(string sql)
    {
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        var parsed = await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(TestContext.Current.CancellationToken);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", TestContext.Current.CancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var emptyModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            await testDb.PublishAsync(
                SchemaCompare.Compare(provider, parsed, emptyModel),
                TestContext.Current.CancellationToken);

            var published = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            return (parsed, published);
        }
        finally
        {
            await testDb.DropAsync(TestContext.Current.CancellationToken);
        }
    }

    private static void AssertHashesMatch(Model parsed, Model published)
    {
        Assert.True(
            HashUtility.HashesEqual(parsed.Hash, published.Hash),
            "Parsed and extracted model hashes do not match.\n"
            + $"Parsed:    {Describe(parsed)}\n"
            + $"Extracted: {Describe(published)}");
    }

    private static string Describe(Model model)
        => string.Join(" | ", model.Elements.Select(i => $"{i.Type}:{i.Name}"));

    // Two independent tables, each with a primary key: the parser interleaves each table
    // with its own PK, so extraction grouping all tables ahead of all PKs would diverge.
    [Fact]
    public async Task TwoTablesWithPrimaryKeys_HashMatch()
    {
        var (parsed, published) = await RoundTripAsync("""
            CREATE TABLE zebra (id integer PRIMARY KEY);
            CREATE TABLE apple (id integer PRIMARY KEY);
            """);

        AssertHashesMatch(parsed, published);
    }

    // Indexes and foreign keys are dependents too, so they must interleave the same way.
    [Fact]
    public async Task MultipleTablesWithIndexesAndForeignKeys_HashMatch()
    {
        var (parsed, published) = await RoundTripAsync("""
            CREATE TABLE author
            (
                author_id integer PRIMARY KEY,
                name      varchar(200) NOT NULL
            );

            CREATE TABLE book
            (
                book_id   integer PRIMARY KEY,
                author_id integer NOT NULL REFERENCES author (author_id),
                title     varchar(400) NOT NULL
            );

            CREATE INDEX ix_book_author_id ON book (author_id);
            """);

        AssertHashesMatch(parsed, published);
    }

    // Tables in non-public schemas must also line up, including a cross-schema reference.
    [Fact]
    public async Task TablesAcrossSchemas_HashMatch()
    {
        var (parsed, published) = await RoundTripAsync("""
            CREATE SCHEMA audit;

            CREATE TABLE book (book_id integer PRIMARY KEY);

            CREATE TABLE audit.book_change
            (
                book_change_id integer PRIMARY KEY,
                book_id        integer NOT NULL REFERENCES public.book (book_id)
            );
            """);

        AssertHashesMatch(parsed, published);
    }

    // The source is written in reverse-alphabetical order, so a builder that sorted tables
    // by name would disagree with the parser, which keeps declaration order.
    [Fact]
    public async Task TablesDeclaredOutOfAlphabeticalOrder_HashMatch()
    {
        var (parsed, published) = await RoundTripAsync("""
            CREATE TABLE zulu (id integer PRIMARY KEY);
            CREATE TABLE yankee (id integer PRIMARY KEY);
            CREATE TABLE xray (id integer PRIMARY KEY);
            """);

        AssertHashesMatch(parsed, published);
    }

    // With matching hashes, a redeploy of unchanged source must produce no deltas at all —
    // the practical payoff of the fix.
    [Fact]
    public async Task RedeployingUnchangedSource_ProducesNoDeltas()
    {
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var (parsed, published) = await RoundTripAsync("""
            CREATE TABLE zebra (id integer PRIMARY KEY);
            CREATE TABLE apple (id integer PRIMARY KEY);
            """);

        Assert.Empty(SchemaCompare.Compare(provider, parsed, published).Deltas);
    }
}
