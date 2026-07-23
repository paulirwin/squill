using Squill.Core;
using Squill.PostgresParser;
using Squill.TestFramework;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Docker-free tests over extension version diffing (issue #36). A source (desired)
/// model is built from SQL with the parser; the target (current) model is built
/// element-by-element to stand in for what the database extractor produces — in
/// particular the database always reports an installed version, which the parser-built
/// source may or may not pin. End-to-end behavior is covered by the integration tests.
/// </summary>
public class PostgresExtensionAlterTests
{
    private static readonly PostgresDatabaseProvider Provider = new("Host=unused");

    private static Task<Model> ParseModelAsync(string sql)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrPostgresParser()));

    // A stand-in for the database's model: one extension element carrying an installed
    // version, exactly as PostgresDatabaseModelBuilder now extracts it.
    private static Model InstalledExtensionModel(string name, string version)
    {
        var model = new Model();
        model.Elements.Add(PostgresModelFactory.CreateExtension(SqlName.Object(name), version));
        return model;
    }

    [Fact]
    public void PinnedNewerVersion_EmitsAlterExtensionUpdate()
    {
        var source = new Model();
        source.Elements.Add(PostgresModelFactory.CreateExtension(SqlName.Object("citext"), "1.7"));

        var target = InstalledExtensionModel("citext", "1.6");

        var comparison = SchemaCompare.Compare(Provider, source, target);
        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Single(comparison.Deltas);
        Assert.Contains("ALTER EXTENSION \"citext\" UPDATE TO '1.7';", sql);
    }

    [Fact]
    public void SamePinnedVersion_ProducesNoDelta()
    {
        var source = new Model();
        source.Elements.Add(PostgresModelFactory.CreateExtension(SqlName.Object("citext"), "1.6"));

        var target = InstalledExtensionModel("citext", "1.6");

        var comparison = SchemaCompare.Compare(Provider, source, target);

        Assert.Empty(comparison.Deltas);
    }

    [Fact]
    public async Task UnpinnedVersion_AgainstInstalledVersion_ProducesNoDelta()
    {
        // The source doesn't pin a version, so the installed version is unmanaged: the
        // difference must be backfilled away rather than produce a spurious delta.
        var source = await ParseModelAsync("CREATE EXTENSION citext;");
        var target = InstalledExtensionModel("citext", "1.6");

        var comparison = SchemaCompare.Compare(Provider, source, target);

        Assert.Empty(comparison.Deltas);
    }

    [Fact]
    public void PinnedOlderVersion_StillEmitsUpdate()
    {
        // Squill scripts the target state; UPDATE TO an older version is what the user
        // declared, and Postgres will attempt it. (A downgrade may fail at runtime, but
        // the diff faithfully reflects the pinned desired version.)
        var source = new Model();
        source.Elements.Add(PostgresModelFactory.CreateExtension(SqlName.Object("citext"), "1.5"));

        var target = InstalledExtensionModel("citext", "1.6");

        var comparison = SchemaCompare.Compare(Provider, source, target);
        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("ALTER EXTENSION \"citext\" UPDATE TO '1.5';", sql);
    }

    [Fact]
    public void NewExtension_WithVersion_StillCreates()
    {
        // A pinned-version extension that doesn't exist in the target is a CREATE, not an
        // ALTER — the version-diff path must not swallow a create.
        var source = new Model();
        source.Elements.Add(PostgresModelFactory.CreateExtension(SqlName.Object("citext"), "1.7"));

        var comparison = SchemaCompare.Compare(Provider, source, new Model());
        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("CREATE EXTENSION IF NOT EXISTS \"citext\" VERSION '1.7';", sql);
        Assert.DoesNotContain("ALTER EXTENSION", sql);
    }
}
