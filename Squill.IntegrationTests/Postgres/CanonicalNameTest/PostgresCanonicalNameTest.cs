using Squill.Core;
using Squill.PostgresParser;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.CanonicalNameTest;

/// <summary>
/// Verifies the payoff of the SqlName canonicalization: a model built by parsing
/// SQL and a model extracted from a live database use identical element names for
/// the same schema. (Whole-model hash equality is still blocked by other builder
/// divergences — e.g. PK-as-annotation vs PK-as-element — so this asserts names,
/// which is exactly what SqlName was introduced to unify.)
/// </summary>
public class PostgresCanonicalNameTest : PostgresIntegrationTestBase
{
    private const string Sql = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL
);

CREATE INDEX idx_film_title ON film (title);
""";

    [Fact]
    public async Task ParserAndDatabaseBuilders_ProduceIdenticalNames()
    {
        // Parser-built model (no database).
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Film.sql", FileKind.Compile, Sql));
        var parserModel = await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(TestContext.Current.CancellationToken);

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

        // Both builders name the table "film" (canonical, quoted, schema-less).
        Assert.Equal("\"film\"", NameOf(parserModel, PostgresElementTypes.SqlTable));
        Assert.Equal("\"film\"", NameOf(dbModel, PostgresElementTypes.SqlTable));

        // Both name the index "idx_film_title".
        Assert.Equal("\"idx_film_title\"", NameOf(parserModel, PostgresElementTypes.SqlIndex));
        Assert.Equal("\"idx_film_title\"", NameOf(dbModel, PostgresElementTypes.SqlIndex));

        // The index's indexed column resolves to the same canonical reference on both.
        Assert.Equal("\"film\".\"title\"", IndexColumnReference(parserModel));
        Assert.Equal("\"film\".\"title\"", IndexColumnReference(dbModel));
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
}
