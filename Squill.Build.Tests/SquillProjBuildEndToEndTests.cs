using System.Diagnostics;
using Squill.Dacpac;
using Squill.Provider.MariaDb;
using Squill.Provider.Postgres;
using Task = System.Threading.Tasks.Task;

namespace Squill.Build.Tests;

/// <summary>
/// End-to-end MSBuild test: runs <c>dotnet build</c> on a generated .squillproj that
/// consumes the local Squill SDK, and asserts the SDK's targets produce a valid DACPAC
/// in the output folder — proving the whole SDK pipeline (props → items → task → dacpac)
/// works through real MSBuild, not just the task in isolation.
/// </summary>
public class SquillProjBuildEndToEndTests
{
    [Fact]
    public async Task DotnetBuild_OnSquillProj_ProducesValidDacpac()
    {
        var repoRoot = FindRepoRoot();

        // Ensure the task assembly the SDK targets load exists (Debug output).
        var taskDll = Path.Combine(repoRoot, "Squill.Build", "bin", "Debug", "net10.0", "Squill.Build.dll");
        Assert.True(File.Exists(taskDll),
            $"Squill.Build must be built before this test; expected {taskDll}. Run 'dotnet build Squill.Build'.");

        var tempDir = Directory.CreateTempSubdirectory("squill-e2e");
        try
        {
            // A .squillproj that imports the local SDK by absolute path, so no package
            // restore is needed and the test is hermetic.
            var sdkDir = Path.Combine(repoRoot, "Squill.Sdk", "Sdk");
            var projContent = $"""
<Project>
  <Import Project="{Path.Combine(sdkDir, "Sdk.props")}" />
  <Import Project="{Path.Combine(sdkDir, "Sdk.targets")}" />
</Project>
""";
            await File.WriteAllTextAsync(
                Path.Combine(tempDir.FullName, "TestDb.squillproj"),
                projContent,
                TestContext.Current.CancellationToken);

            await File.WriteAllTextAsync(
                Path.Combine(tempDir.FullName, "Widget.sql"),
                "CREATE TABLE widget (id integer PRIMARY KEY, label varchar(50) NOT NULL);",
                TestContext.Current.CancellationToken);

            var (exitCode, output) = await RunDotnetBuild(tempDir.FullName, "TestDb.squillproj");

            Assert.True(exitCode == 0, $"dotnet build should succeed. Output:\n{output}");

            var dacpacPath = Path.Combine(tempDir.FullName, "bin", "Debug", "TestDb.dacpac");
            Assert.True(File.Exists(dacpacPath), $"DACPAC should be produced at {dacpacPath}. Output:\n{output}");

            await using var stream = File.OpenRead(dacpacPath);
            var (metadata, model) =
                await DacpacSerializer.Deserialize(stream, TestContext.Current.CancellationToken);

            Assert.Equal("TestDb", metadata.Name);
            Assert.Contains(model.Elements, e => e.Type == PostgresElementTypes.SqlTable);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    // The same SDK pipeline, but with SquillProviderName=MariaDb: the task must dispatch to
    // the MariaDB provider so a MariaDB-dialect script (AUTO_INCREMENT) builds and the DACPAC
    // records the MariaDb provider name.
    [Fact]
    public async Task DotnetBuild_OnMariaDbSquillProj_ProducesMariaDbDacpac()
    {
        var repoRoot = FindRepoRoot();

        var taskDll = Path.Combine(repoRoot, "Squill.Build", "bin", "Debug", "net10.0", "Squill.Build.dll");
        Assert.True(File.Exists(taskDll),
            $"Squill.Build must be built before this test; expected {taskDll}. Run 'dotnet build Squill.Build'.");

        var tempDir = Directory.CreateTempSubdirectory("squill-e2e-mariadb");
        try
        {
            var sdkDir = Path.Combine(repoRoot, "Squill.Sdk", "Sdk");
            var projContent = $"""
<Project>
  <Import Project="{Path.Combine(sdkDir, "Sdk.props")}" />
  <PropertyGroup>
    <SquillProviderName>MariaDb</SquillProviderName>
  </PropertyGroup>
  <Import Project="{Path.Combine(sdkDir, "Sdk.targets")}" />
</Project>
""";
            await File.WriteAllTextAsync(
                Path.Combine(tempDir.FullName, "TestDb.squillproj"),
                projContent,
                TestContext.Current.CancellationToken);

            await File.WriteAllTextAsync(
                Path.Combine(tempDir.FullName, "Widget.sql"),
                "CREATE TABLE widget (id int NOT NULL AUTO_INCREMENT PRIMARY KEY, label varchar(50) NOT NULL);",
                TestContext.Current.CancellationToken);

            var (exitCode, output) = await RunDotnetBuild(tempDir.FullName, "TestDb.squillproj");

            Assert.True(exitCode == 0, $"dotnet build should succeed. Output:\n{output}");

            var dacpacPath = Path.Combine(tempDir.FullName, "bin", "Debug", "TestDb.dacpac");
            Assert.True(File.Exists(dacpacPath), $"DACPAC should be produced at {dacpacPath}. Output:\n{output}");

            await using var stream = File.OpenRead(dacpacPath);
            var (metadata, model) =
                await DacpacSerializer.Deserialize(stream, TestContext.Current.CancellationToken);

            Assert.Equal("MariaDb", metadata.ProviderName);
            // The MariaDB provider was used: the model carries a MariaDb-typed table with an
            // auto-increment column, which the Postgres provider would never produce.
            var table = Assert.Single(model.Elements, e => e.Type == MariaDbElementTypes.SqlTable);
            var columns = table.GetRelationship(MariaDbRelationshipNames.Columns)!;
            var idColumn = (Squill.Core.Element)columns.Entries[0];
            Assert.Equal(true, idColumn.GetProperty<bool?>(MariaDbPropertyNames.IsAutoIncrement));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunDotnetBuild(string workingDir, string project)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(project);

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, stdout + stderr);
    }

    // Walk up from the test assembly location until we find the repository root
    // (identified by Squill.slnx), so the test works regardless of CWD.
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
