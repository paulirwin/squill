using Squill.Core;
using Squill.PostgresParser;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.CanonicalNameTest;

/// <summary>
/// Verifies the payoff of the SqlName canonicalization: a model built by parsing
/// SQL and a model extracted from a live database use identical element names for
/// the same schema, that the varchar / character varying type specifiers agree
/// across both builders (issue #6), and — as of issue #25 — that the whole-model
/// Merkle hashes match once the parser builder emits the same canonical defaults
/// (public schema, btree index method, btree ordering defaults) the DB builder does.
/// </summary>
public class PostgresCanonicalNameTest : PostgresIntegrationTestBase
{
    private const string Sql = """
CREATE TABLE film
(
    film_id     integer PRIMARY KEY,
    title       varchar(255) NOT NULL,
    description varchar
);

CREATE INDEX idx_film_title ON film (title);
""";

    [Fact]
    public async Task ParserAndDatabaseBuilders_ProduceIdenticalNames()
    {
        // Parser-built model (no database).
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Film.sql", FileKind.Compile, Sql));
        var parserModel = (await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(TestContext.Current.CancellationToken)).Model;

        // Database-built model: run the same SQL into a real database, then extract.
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
        var db = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", TestContext.Current.CancellationToken);
        Model dbModel;
        try
        {
            await db.ConnectAsync(TestContext.Current.CancellationToken);
            await db.RunScriptAsync(Sql, cancellationToken: TestContext.Current.CancellationToken);
            dbModel = await provider.CreateDatabaseModelBuilder(db)
                .ExtractModelAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            await db.DropAsync(TestContext.Current.CancellationToken);
        }

        // Both builders name the table "film" (canonical, unquoted, schema-less).
        Assert.Equal("film", NameOf(parserModel, PostgresElementTypes.SqlTable));
        Assert.Equal("film", NameOf(dbModel, PostgresElementTypes.SqlTable));

        // Both name the index "idx_film_title".
        Assert.Equal("idx_film_title", NameOf(parserModel, PostgresElementTypes.SqlIndex));
        Assert.Equal("idx_film_title", NameOf(dbModel, PostgresElementTypes.SqlIndex));

        // The index's indexed column resolves to the same canonical reference on both.
        Assert.Equal("film.title", IndexColumnReference(parserModel));
        Assert.Equal("film.title", IndexColumnReference(dbModel));

        // Issue #6: the varchar / character varying type specifiers must be identical
        // between the builders — same canonical type reference, and the same Length
        // (present for varchar(255), absent for a bare varchar, matching Postgres's
        // information_schema.character_maximum_length = NULL for an unbounded varchar).
        Assert.Equal(TypeSpecHash(parserModel, "film.title"), TypeSpecHash(dbModel, "film.title"));
        Assert.Equal(TypeSpecHash(parserModel, "film.description"), TypeSpecHash(dbModel, "film.description"));
    }

    // The payoff of unifying the builders: a parsed model and an extracted model of the
    // same schema hash-match, so schema compare works across sources. As of issue #25 the
    // parser builder emits the same canonical defaults the DB builder does — an implicit
    // "public" Schema relationship, a btree IndexMethod when USING is omitted, and the
    // btree index column's ASC / NULLS LAST ordering defaults — so the whole-model Merkle
    // hashes now match.
    [Fact]
    public async Task ParserAndDatabaseBuilders_ProduceMatchingModelHashes()
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Film.sql", FileKind.Compile, Sql));
        var parserModel = (await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(TestContext.Current.CancellationToken)).Model;

        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
        var db = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", TestContext.Current.CancellationToken);
        Model dbModel;
        try
        {
            await db.ConnectAsync(TestContext.Current.CancellationToken);
            await db.RunScriptAsync(Sql, cancellationToken: TestContext.Current.CancellationToken);
            dbModel = await provider.CreateDatabaseModelBuilder(db)
                .ExtractModelAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            await db.DropAsync(TestContext.Current.CancellationToken);
        }

        Assert.True(HashUtility.HashesEqual(parserModel.Hash, dbModel.Hash),
            "Parser-built and database-built model hashes should match after canonicalization");
    }

    private static string? NameOf(Model model, string elementType)
        => model.Elements.Single(i => i.Type == elementType).Name;

    private static string IndexColumnReference(Model model)
    {
        var index = model.Elements.Single(i => i.Type == PostgresElementTypes.SqlIndex);
        var columnSpec = (Element)index.GetRelationship(PostgresRelationshipNames.ColumnSpecifications)!.Entries.Single();
        var reference = (Reference)columnSpec.GetRelationship(PostgresRelationshipNames.Column)!.Entries.Single();
        return reference.Name;
    }

    // The Merkle hash of a column's type-specifier element captures its type reference
    // plus any Length / Precision / Scale properties — everything issue #6 must unify.
    private static string TypeSpecHash(Model model, string columnName)
    {
        var table = model.Elements.Single(i => i.Type == PostgresElementTypes.SqlTable);
        var columns = table.GetRelationship(PostgresRelationshipNames.Columns)!;
        var column = columns.Entries
            .OfType<Element>()
            .Single(c => c.Name == columnName);
        var typeSpec = (Element)column.GetRelationship(PostgresRelationshipNames.TypeSpecifier)!.Entries.Single();
        return Convert.ToHexString(typeSpec.Hash);
    }
}
