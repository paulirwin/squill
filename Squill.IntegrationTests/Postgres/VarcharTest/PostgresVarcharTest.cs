using Squill.Core;
using Squill.PostgresParser;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.VarcharTest;

/// <summary>
/// Issue #6: `varchar` is an alias for `character varying`, and an unbounded
/// `character varying` (no length) "accepts strings of any length"
/// (https://www.postgresql.org/docs/current/datatype-character.html). Postgres has
/// no `varchar(MAX)` — unbounded is simply the absence of a length, and
/// information_schema.columns.character_maximum_length is NULL for such a column.
///
/// This publishes a parser-built table carrying both a bounded and a bare varchar
/// into a real database (proving the generated DDL is valid, executable Postgres),
/// then re-extracts and asserts the type specifiers match what the parser produced —
/// so the bare varchar round-trips with no Length and no spurious IsMax.
/// </summary>
public class PostgresVarcharTest : PostgresIntegrationTestBase
{
    private const string Sql = """
CREATE TABLE notes
(
    title varchar(255) NOT NULL,
    body  varchar
);
""";

    [Fact]
    public async Task BareVarchar_RoundTripsThroughRealPostgres()
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Notes.sql", FileKind.Compile, Sql));
        var parserModel = await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(TestContext.Current.CancellationToken);

        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
        var db = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", TestContext.Current.CancellationToken);
        try
        {
            var dbModelBuilder = provider.CreateDatabaseModelBuilder(db);

            // Empty target -> every element is a CreateDelta; publishing runs the
            // generated DDL. If the bare varchar scripted as `varchar(MAX)`, this
            // would throw a Postgres syntax error.
            var emptyModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);
            var comparison = SchemaCompare.Compare(provider, parserModel, emptyModel);
            await db.PublishAsync(comparison, TestContext.Current.CancellationToken);

            var reExtracted = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            // The bare varchar and the bounded varchar both round-trip with the same
            // type-specifier shape the parser produced.
            Assert.Equal(TypeSpecHash(parserModel, "notes.title"), TypeSpecHash(reExtracted, "notes.title"));
            Assert.Equal(TypeSpecHash(parserModel, "notes.body"), TypeSpecHash(reExtracted, "notes.body"));

            // And the bare varchar specifically carries the canonical type with no length.
            var bodyType = TypeSpecOf(reExtracted, "notes.body");
            var bodyRef = (Reference)bodyType.GetRelationship(PostgresRelationshipNames.Type)!.Entries.Single();
            Assert.Equal("character varying", bodyRef.Name);
            Assert.Null(bodyType.GetProperty<int?>(PostgresPropertyNames.Length));
        }
        finally
        {
            await db.DropAsync(TestContext.Current.CancellationToken);
        }
    }

    private static Element TypeSpecOf(Model model, string columnName)
    {
        var table = model.Elements.Single(i => i.Type == PostgresElementTypes.SqlTable);
        var columns = table.GetRelationship(PostgresRelationshipNames.Columns)!;
        var column = columns.Entries.OfType<Element>().Single(c => c.Name == columnName);
        return (Element)column.GetRelationship(PostgresRelationshipNames.TypeSpecifier)!.Entries.Single();
    }

    private static string TypeSpecHash(Model model, string columnName)
        => Convert.ToHexString(TypeSpecOf(model, columnName).Hash);
}
