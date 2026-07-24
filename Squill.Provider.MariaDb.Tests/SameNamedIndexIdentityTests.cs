using Squill.Core;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// An index name is only unique within its table, not across the database (issue #122). MariaDB's
/// Sakila schema relies on this: <c>idx_fk_film_id</c> exists on both <c>film_actor</c> and
/// <c>inventory</c>, and <c>idx_fk_address_id</c> on three tables.
///
/// <para>
/// Element identity in the diff engine therefore has to include the owning table. Without it a
/// source index matched several target indexes at once, and comparing a deployed schema against
/// itself threw "Sequence contains more than one matching element" — so any schema reusing an
/// index name across tables could be deployed once but never redeployed.
/// </para>
/// </summary>
public class SameNamedIndexIdentityTests
{
    private const string TwoTablesSharingAnIndexName =
        """
        CREATE TABLE film_actor
        (
            actor_id int NOT NULL,
            film_id  int NOT NULL,
            PRIMARY KEY (actor_id, film_id)
        );

        CREATE TABLE inventory
        (
            inventory_id int NOT NULL PRIMARY KEY,
            film_id      int NOT NULL
        );

        CREATE INDEX idx_fk_film_id ON film_actor (film_id);
        CREATE INDEX idx_fk_film_id ON inventory (film_id);
        """;

    private static async Task<Model> BuildModelAsync(string sql)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        return (await new ParserWorkspaceModelBuilder(workspace, new AntlrMariaDbParser())
            .ExtractModelAsync(TestContext.Current.CancellationToken)).Model;
    }

    /// <summary>
    /// Comparing a model against itself yields no deltas — the property every redeploy of an
    /// unchanged schema depends on. Before the fix this threw rather than returning an empty
    /// comparison.
    /// </summary>
    [Fact]
    public async Task CompareModelToItself_WithSameNamedIndexesOnDifferentTables_HasNoDeltas()
    {
        var model = await BuildModelAsync(TwoTablesSharingAnIndexName);
        var provider = new MariaDbDatabaseProvider("Server=unused");

        var comparison = SchemaCompare.Compare(provider, model, model);

        Assert.Empty(comparison.Deltas);
    }

    /// <summary>
    /// Both same-named indexes are still created when deploying to an empty database — the fix
    /// scopes identity by table without collapsing the two into one.
    /// </summary>
    [Fact]
    public async Task DeployToEmpty_CreatesBothSameNamedIndexes()
    {
        var model = await BuildModelAsync(TwoTablesSharingAnIndexName);
        var provider = new MariaDbDatabaseProvider("Server=unused");

        var script = new MariaDbScriptGenerator()
            .GenerateScript(SchemaCompare.Compare(provider, model, new Model()));

        Assert.Contains("`film_actor`", script);
        Assert.Contains("`inventory`", script);

        // The index name appears once per table it is defined on.
        var occurrences = script.Split("idx_fk_film_id").Length - 1;
        Assert.Equal(2, occurrences);
    }

    /// <summary>
    /// Adding an index to one table when an index of the same name already exists on another
    /// table creates it, rather than treating the other table's index as the existing one and
    /// doing nothing.
    /// </summary>
    [Fact]
    public async Task AddingSameNamedIndexToASecondTable_IsCreated()
    {
        var target = await BuildModelAsync(
            """
            CREATE TABLE film_actor
            (
                actor_id int NOT NULL,
                film_id  int NOT NULL,
                PRIMARY KEY (actor_id, film_id)
            );

            CREATE TABLE inventory
            (
                inventory_id int NOT NULL PRIMARY KEY,
                film_id      int NOT NULL
            );

            CREATE INDEX idx_fk_film_id ON film_actor (film_id);
            """);

        var source = await BuildModelAsync(TwoTablesSharingAnIndexName);
        var provider = new MariaDbDatabaseProvider("Server=unused");

        var comparison = SchemaCompare.Compare(provider, source, target);

        var delta = Assert.IsType<CreateDelta>(Assert.Single(comparison.Deltas));
        Assert.Equal("idx_fk_film_id", delta.Element.Name);

        // ...and it is the one on inventory, not a duplicate of film_actor's.
        var script = new MariaDbScriptGenerator().GenerateScript(comparison);
        Assert.Contains("`inventory`", script);
        Assert.DoesNotContain("`film_actor`", script);
    }
}
