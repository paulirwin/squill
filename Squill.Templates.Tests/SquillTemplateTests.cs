using System.Diagnostics;
using Squill.Dacpac;
using Squill.Provider.MariaDb;
using Squill.Provider.Postgres;
using Task = System.Threading.Tasks.Task;

namespace Squill.Templates.Tests;

/// <summary>
/// End-to-end tests for the <c>dotnet new squill</c> template (issue #45): install the
/// template pack into an isolated template hive, scaffold projects with it, and assert the
/// generated <c>.squillproj</c> is correct — including that it builds a valid DACPAC.
///
/// The template ships a project that references the published <c>Squill.Sdk</c> package by
/// name/version. To keep the build hermetic (no package restore, like the sample projects
/// and <c>SquillProjBuildEndToEndTests</c>), the round-trip test rewrites that
/// <c>Sdk="Squill.Sdk/x"</c> reference into a relative <c>Import</c> of the local, uninstalled
/// SDK before building. The template's own content is what's under test; the SDK-package
/// build path is covered elsewhere.
/// </summary>
[Collection("Template")]
public class SquillTemplateTests
{
    // The default-provider template scaffolds a Postgres project with a table and no
    // target-version constraint, and it builds a valid DACPAC.
    [Fact]
    public async Task DotnetNew_DefaultProvider_ScaffoldsBuildableProject()
    {
        var repoRoot = FindRepoRoot();
        using var hive = await TemplateHive.InstallAsync(repoRoot);

        var outDir = hive.NewTempDir("default");
        var (exit, output) = await hive.RunNewAsync(outDir,
            "new", "squill", "--name", "MyDb", "--output", ".");
        Assert.True(exit == 0, $"dotnet new squill should succeed. Output:\n{output}");

        var projPath = Path.Combine(outDir, "MyDb.squillproj");
        Assert.True(File.Exists(projPath), $"Project should be scaffolded at {projPath}");
        Assert.True(File.Exists(Path.Combine(outDir, "Tables", "ExampleTable.sql")),
            "The template should scaffold an example table.");

        var projText = await File.ReadAllTextAsync(projPath, TestContext.Current.CancellationToken);
        Assert.Contains("<SquillProviderName>Postgresql</SquillProviderName>", projText);
        // No target version requested → no SquillTargetVersion element.
        Assert.DoesNotContain("SquillTargetVersion", projText);

        var (metadata, model) = await BuildAndDeserialize(repoRoot, outDir, projPath, "MyDb");
        Assert.Equal("MyDb", metadata.Name);
        Assert.Equal("Postgresql", metadata.ProviderName);
        Assert.Contains(model.Elements, e => e.Type == PostgresElementTypes.SqlTable);
    }

    // Choosing MariaDb and a target version flows both into the generated project, and the
    // project builds a MariaDb-provider DACPAC recording that target version.
    [Fact]
    public async Task DotnetNew_MariaDbWithTargetVersion_ScaffoldsBuildableProject()
    {
        var repoRoot = FindRepoRoot();
        using var hive = await TemplateHive.InstallAsync(repoRoot);

        var outDir = hive.NewTempDir("mariadb");
        var (exit, output) = await hive.RunNewAsync(outDir,
            "new", "squill", "--name", "ShopDb", "--output", ".", "--Provider", "MariaDb", "--TargetVersion", "11");
        Assert.True(exit == 0, $"dotnet new squill should succeed. Output:\n{output}");

        var projPath = Path.Combine(outDir, "ShopDb.squillproj");
        var projText = await File.ReadAllTextAsync(projPath, TestContext.Current.CancellationToken);
        Assert.Contains("<SquillProviderName>MariaDb</SquillProviderName>", projText);
        Assert.Contains("<SquillTargetVersion>11</SquillTargetVersion>", projText);

        var (metadata, model) = await BuildAndDeserialize(repoRoot, outDir, projPath, "ShopDb");
        Assert.Equal("MariaDb", metadata.ProviderName);
        Assert.Equal(11, metadata.TargetMajorVersion);
        Assert.Contains(model.Elements, e => e.Type == MariaDbElementTypes.SqlTable);
    }

    // The MySql choice selects the MariaDB provider (which serves both engines).
    [Fact]
    public async Task DotnetNew_MySqlProvider_ScaffoldsMariaDbProviderProject()
    {
        var repoRoot = FindRepoRoot();
        using var hive = await TemplateHive.InstallAsync(repoRoot);

        var outDir = hive.NewTempDir("mysql");
        var (exit, output) = await hive.RunNewAsync(outDir,
            "new", "squill", "--name", "CatalogDb", "--output", ".", "--Provider", "MySql");
        Assert.True(exit == 0, $"dotnet new squill should succeed. Output:\n{output}");

        var projPath = Path.Combine(outDir, "CatalogDb.squillproj");
        var projText = await File.ReadAllTextAsync(projPath, TestContext.Current.CancellationToken);
        Assert.Contains("<SquillProviderName>MySql</SquillProviderName>", projText);

        var (metadata, model) = await BuildAndDeserialize(repoRoot, outDir, projPath, "CatalogDb");
        // MySql routes to the MariaDB provider (which serves both engines): the recorded
        // provider name is the configured MySql, and the model carries a MariaDb-typed table.
        Assert.Equal("MySql", metadata.ProviderName);
        Assert.Contains(model.Elements, e => e.Type == MariaDbElementTypes.SqlTable);
    }

    // Build the scaffolded project against the local, uninstalled SDK (hermetic) and read
    // back the produced DACPAC.
    private static async Task<(ModelMetadata Metadata, Squill.Core.Model Model)> BuildAndDeserialize(
        string repoRoot, string outDir, string projPath, string dacName)
    {
        // The DACPAC reader resolves its schema provider by reflecting over loaded assemblies.
        // .NET loads them lazily, and const references are inlined (they don't load the
        // declaring assembly), so force both provider assemblies to load via a runtime type
        // reference before Deserialize scans for schema providers.
        _ = typeof(PostgresElementTypes).Assembly;
        _ = typeof(MariaDbElementTypes).Assembly;

        var taskDll = Path.Combine(repoRoot, "Squill.Build", "bin", "Debug", "net10.0", "Squill.Build.dll");
        Assert.True(File.Exists(taskDll),
            $"Squill.Build must be built before this test; expected {taskDll}. Run 'dotnet build Squill.Build'.");

        // Swap the SDK-package reference for a relative import of the local SDK so the build
        // needs no restore — the same mechanism the sample projects and E2E tests use.
        var sdkDir = Path.Combine(repoRoot, "Squill.Sdk", "Sdk");
        var projText = await File.ReadAllTextAsync(projPath, TestContext.Current.CancellationToken);
        var start = projText.IndexOf("<Project", StringComparison.Ordinal);
        var end = projText.IndexOf('>', start) + 1;
        var rewritten =
            "<Project>\n"
            + $"  <Import Project=\"{Path.Combine(sdkDir, "Sdk.props")}\" />\n"
            + projText[end..].Replace("</Project>",
                $"  <Import Project=\"{Path.Combine(sdkDir, "Sdk.targets")}\" />\n</Project>");
        await File.WriteAllTextAsync(projPath, rewritten, TestContext.Current.CancellationToken);

        var (exit, output) = await RunDotnet(outDir, "build", Path.GetFileName(projPath));
        Assert.True(exit == 0, $"dotnet build should succeed. Output:\n{output}");

        var dacpacPath = Path.Combine(outDir, "bin", "Debug", $"{dacName}.dacpac");
        Assert.True(File.Exists(dacpacPath), $"DACPAC should be produced at {dacpacPath}. Output:\n{output}");

        await using var stream = File.OpenRead(dacpacPath);
        return await DacpacSerializer.Deserialize(stream, TestContext.Current.CancellationToken);
    }

    private static async Task<(int ExitCode, string Output)> RunDotnet(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, stdout + stderr);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Squill.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
