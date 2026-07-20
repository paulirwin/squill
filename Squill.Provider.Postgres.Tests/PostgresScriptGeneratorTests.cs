using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Fast, Docker-free tests over the pure model-to-SQL generation. Input models are
/// built with the parser-based model builder (no database required) and diffed
/// against an empty target so every element becomes a CreateDelta.
/// </summary>
public class PostgresScriptGeneratorTests
{
    private static async Task<SchemaComparison> CompareToEmptyAsync(string sql)
    {
        var parser = new AntlrPostgresParser();
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        var model = await new ParserWorkspaceModelBuilder(workspace, parser).ExtractModelAsync();

        var provider = new PostgresDatabaseProvider("Host=unused");
        return SchemaCompare.Compare(provider, model, new Model());
    }

    [Fact]
    public async Task GenerateScript_CreateTable()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL
);
""");

        var generator = new PostgresScriptGenerator();

        var sql = generator.GenerateScript(comparison);

        Assert.Contains("CREATE TABLE \"film\"", sql);
        Assert.Contains("NOT NULL", sql);
        Assert.Contains("varchar(255)", sql);
        // The parser now emits the PK as a first-class element, so it scripts.
        Assert.Contains("PRIMARY KEY", sql);
    }

    [Fact]
    public async Task GenerateScript_CreateTableWithIndex()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL
);

CREATE INDEX idx_film_title ON film (title);
""");

        var generator = new PostgresScriptGenerator();

        var sql = generator.GenerateScript(comparison);

        Assert.Contains("CREATE INDEX \"idx_film_title\" ON \"film\"", sql);
        Assert.Contains("(\"title\")", sql);
    }

    [Fact]
    public void GenerateScript_MultiColumnPrimaryKey_EmitsTableLevelClause()
    {
        // The parser can't yet parse table-level PRIMARY KEY (a, b), so build the model
        // directly through the factory to exercise multi-column PK scripting.
        var table = PostgresModelFactory.CreateTable(SqlName.Object("enrollment"), "public");
        var columns = new Relationship(PostgresRelationshipNames.Columns);
        table.Relationships.Add(columns);
        foreach (var col in new[] { "student_id", "course_id" })
        {
            columns.Add(new Element(PostgresElementTypes.SqlSimpleColumn)
            {
                Name = SqlName.Object("enrollment").Child(col),
                Relationships =
                {
                    new Relationship(PostgresRelationshipNames.TypeSpecifier)
                    {
                        new Element(PostgresElementTypes.SqlTypeSpecifier)
                        {
                            Relationships =
                            {
                                new Relationship(PostgresRelationshipNames.Type)
                                {
                                    new Reference("integer") { ExternalSource = "BuiltIns" }
                                }
                            }
                        }
                    }
                }
            });
        }

        var pk = PostgresModelFactory.CreatePrimaryKey(
            SqlName.Object("PK_enrollment"),
            SqlName.Object("enrollment"),
            columns:
            [
                new PostgresModelFactory.IndexedColumn(SqlName.Object("enrollment").Child("student_id")),
                new PostgresModelFactory.IndexedColumn(SqlName.Object("enrollment").Child("course_id")),
            ]);

        var delta = new CreateDelta(table);
        delta.DependentElements.Add(pk);

        var sql = new PostgresScriptGenerator().GenerateScriptForDelta(delta);

        // Multi-column PK must be a table-level clause, not inline on a single column.
        Assert.DoesNotContain("integer PRIMARY KEY", sql);
        Assert.Contains("PRIMARY KEY (\"student_id\", \"course_id\")", sql);
    }

    [Fact]
    public async Task GenerateScript_UniqueIndexWithMethodAndDirection()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE account
(
    account_id integer PRIMARY KEY,
    email      varchar(255) NOT NULL
);

CREATE UNIQUE INDEX idx_account_email ON account USING btree (email DESC NULLS LAST);
""");

        var generator = new PostgresScriptGenerator();

        var sql = generator.GenerateScript(comparison);

        Assert.Contains("CREATE UNIQUE INDEX \"idx_account_email\" ON \"account\" USING btree", sql);
        Assert.Contains("\"email\" DESC NULLS LAST", sql);
    }

    [Fact]
    public async Task GenerateScript_InlineForeignKeyWithOnDeleteCascade()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE customers
(
    id integer PRIMARY KEY
);

CREATE TABLE orders
(
    id          integer PRIMARY KEY,
    customer_id integer NOT NULL REFERENCES customers (id) ON DELETE CASCADE
);
""");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains(
            "CONSTRAINT \"orders_customer_id_fkey\" FOREIGN KEY (\"customer_id\") REFERENCES \"customers\" (\"id\") ON DELETE CASCADE",
            sql);
    }

    [Fact]
    public async Task GenerateScript_TableLevelCompositeForeignKey()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE orders
(
    id      integer NOT NULL,
    line_no integer NOT NULL,
    PRIMARY KEY (id, line_no)
);

CREATE TABLE order_lines
(
    order_id integer NOT NULL,
    line_no  integer NOT NULL,
    CONSTRAINT fk_lines FOREIGN KEY (order_id, line_no) REFERENCES orders (id, line_no) ON DELETE CASCADE ON UPDATE RESTRICT
);
""");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains(
            "CONSTRAINT \"fk_lines\" FOREIGN KEY (\"order_id\", \"line_no\") REFERENCES \"orders\" (\"id\", \"line_no\") ON DELETE CASCADE ON UPDATE RESTRICT",
            sql);
    }
}
