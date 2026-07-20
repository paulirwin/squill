using Squill.Core;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.ForeignKeyTest;

public class PostgresForeignKeyTest : PostgresIntegrationTestBase
{
    // Full round trip for a single-column foreign key with ON DELETE CASCADE: build a
    // model from SQL against a temporary database, publish it into a fresh target,
    // re-extract, and assert the model hashes match. This exercises FK publish
    // scripting and FK extraction (columns, referenced table/column, delete action)
    // against a real Postgres database.
    [Fact]
    public async Task ForeignKeyRoundTrip_ModelHashesMatchAfterPublish()
    {
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var workspace = new Workspace();
        workspace.Files.Add(new EmbeddedResourceFile(
            "Squill.IntegrationTests.Postgres.ForeignKeyTest.TablesWithForeignKey.sql", FileKind.Compile));

        var model = await new TemporaryDatabaseModelBuilder(provider)
            .BuildModelAsync(workspace, TestContext.Current.CancellationToken);

        var fk = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlForeignKeyConstraint);
        Assert.Equal("orders_customer_id_fkey", fk.Name);
        Assert.Equal("Cascade", fk.GetProperty<string>(PostgresPropertyNames.DeleteAction));

        var referencedTable = fk.GetRelationship(PostgresRelationshipNames.ForeignTable);
        Assert.Equal("customers", Assert.IsType<Reference>(Assert.Single(referencedTable!.Entries)).Name);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", TestContext.Current.CancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var emptyModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            var comparison = SchemaCompare.Compare(provider, model, emptyModel);

            await testDb.PublishAsync(comparison, TestContext.Current.CancellationToken);

            var publishedModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            var publishedFk = Assert.Single(publishedModel.Elements,
                i => i.Type == PostgresElementTypes.SqlForeignKeyConstraint);
            Assert.Equal("orders_customer_id_fkey", publishedFk.Name);
            Assert.Equal("Cascade", publishedFk.GetProperty<string>(PostgresPropertyNames.DeleteAction));

            Assert.True(HashUtility.HashesEqual(model.Hash, publishedModel.Hash), "Model hashes do not match");
        }
        finally
        {
            await testDb.DropAsync(TestContext.Current.CancellationToken);
        }
    }

    // Round trip for a named, composite foreign key with both ON DELETE CASCADE and
    // ON UPDATE RESTRICT, referencing a composite primary key. Verifies column-pair
    // ordering and both referential actions survive publish + extract.
    [Fact]
    public async Task CompositeForeignKeyRoundTrip_ModelHashesMatchAfterPublish()
    {
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var workspace = new Workspace();
        workspace.Files.Add(new EmbeddedResourceFile(
            "Squill.IntegrationTests.Postgres.ForeignKeyTest.TablesWithCompositeForeignKey.sql", FileKind.Compile));

        var model = await new TemporaryDatabaseModelBuilder(provider)
            .BuildModelAsync(workspace, TestContext.Current.CancellationToken);

        var fk = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlForeignKeyConstraint);
        Assert.Equal("fk_order_lines_orders", fk.Name);
        Assert.Equal("Cascade", fk.GetProperty<string>(PostgresPropertyNames.DeleteAction));
        Assert.Equal("Restrict", fk.GetProperty<string>(PostgresPropertyNames.UpdateAction));

        var fkColumns = fk.GetRelationship(PostgresRelationshipNames.ForeignKeyColumns)!;
        Assert.Equal(
            new[] { "order_lines.order_id", "order_lines.line_no" },
            fkColumns.Entries.OfType<Reference>().Select(r => r.Name));

        var foreignColumns = fk.GetRelationship(PostgresRelationshipNames.ForeignColumns)!;
        Assert.Equal(
            new[] { "orders.id", "orders.line_no" },
            foreignColumns.Entries.OfType<Reference>().Select(r => r.Name));

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", TestContext.Current.CancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var emptyModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            var comparison = SchemaCompare.Compare(provider, model, emptyModel);

            await testDb.PublishAsync(comparison, TestContext.Current.CancellationToken);

            var publishedModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            Assert.True(HashUtility.HashesEqual(model.Hash, publishedModel.Hash), "Model hashes do not match");
        }
        finally
        {
            await testDb.DropAsync(TestContext.Current.CancellationToken);
        }
    }
}
