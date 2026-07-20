using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

public class DependencyAnalyzerTests
{
    [Fact]
    public async Task GetDependentElements_IndexIsDependentOnItsTable()
    {
        const string sql = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title varchar(255) NOT NULL
);

CREATE INDEX idx_title ON film (title);
""";

        var parser = new AntlrPostgresParser();
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Film.sql", FileKind.Compile, sql));

        var builder = new ParserWorkspaceModelBuilder(workspace, parser);
        var model = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var analyzer = new PostgresDatabaseDependencyAnalyzer();

        Assert.True(analyzer.IsDependentElementType(PostgresElementTypes.SqlIndex));

        var table = model.Elements.Single(i => i.Type == PostgresElementTypes.SqlTable);
        var dependents = analyzer.GetDependentElements(table, model);

        Assert.NotNull(dependents);
        var index = Assert.Single(dependents, i => i.Type == PostgresElementTypes.SqlIndex);
        Assert.Equal("idx_title", index.Name);
    }

    [Fact]
    public async Task GetDependentElements_ForeignKeyIsDependentOnItsDefiningTable()
    {
        const string sql = """
CREATE TABLE customers
(
    id integer PRIMARY KEY
);

CREATE TABLE orders
(
    id          integer PRIMARY KEY,
    customer_id integer NOT NULL REFERENCES customers (id) ON DELETE CASCADE
);
""";

        var parser = new AntlrPostgresParser();
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Orders.sql", FileKind.Compile, sql));

        var builder = new ParserWorkspaceModelBuilder(workspace, parser);
        var model = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var analyzer = new PostgresDatabaseDependencyAnalyzer();

        Assert.True(analyzer.IsDependentElementType(PostgresElementTypes.SqlForeignKeyConstraint));

        // The FK is dependent on the referencing (defining) table, not the referenced one.
        var ordersTable = model.Elements.Single(i => i.Type == PostgresElementTypes.SqlTable && (string?)i.Name == "orders");
        var dependents = analyzer.GetDependentElements(ordersTable, model);

        Assert.NotNull(dependents);
        var fk = Assert.Single(dependents, i => i.Type == PostgresElementTypes.SqlForeignKeyConstraint);
        Assert.Equal("orders_customer_id_fkey", fk.Name);

        // The FK must NOT hang off the referenced table.
        var customersTable = model.Elements.Single(i => i.Type == PostgresElementTypes.SqlTable && (string?)i.Name == "customers");
        var customerDependents = analyzer.GetDependentElements(customersTable, model);
        Assert.DoesNotContain(customerDependents!, i => i.Type == PostgresElementTypes.SqlForeignKeyConstraint);
    }
}
