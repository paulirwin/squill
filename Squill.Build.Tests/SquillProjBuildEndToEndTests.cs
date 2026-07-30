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

    // Setting SquillTargetVersion in the project flows through the SDK pipeline (props →
    // targets task param → task → DACPAC), so the built DACPAC records the target major
    // version (issue #39).
    [Fact]
    public async Task DotnetBuild_WithSquillTargetVersion_RecordsItInDacpac()
    {
        var repoRoot = FindRepoRoot();

        var taskDll = Path.Combine(repoRoot, "Squill.Build", "bin", "Debug", "net10.0", "Squill.Build.dll");
        Assert.True(File.Exists(taskDll),
            $"Squill.Build must be built before this test; expected {taskDll}. Run 'dotnet build Squill.Build'.");

        var tempDir = Directory.CreateTempSubdirectory("squill-e2e-version");
        try
        {
            var sdkDir = Path.Combine(repoRoot, "Squill.Sdk", "Sdk");
            var projContent = $"""
<Project>
  <Import Project="{Path.Combine(sdkDir, "Sdk.props")}" />
  <PropertyGroup>
    <SquillTargetVersion>16</SquillTargetVersion>
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
                "CREATE TABLE widget (id integer PRIMARY KEY, label varchar(50) NOT NULL);",
                TestContext.Current.CancellationToken);

            var (exitCode, output) = await RunDotnetBuild(tempDir.FullName, "TestDb.squillproj");

            Assert.True(exitCode == 0, $"dotnet build should succeed. Output:\n{output}");

            var dacpacPath = Path.Combine(tempDir.FullName, "bin", "Debug", "TestDb.dacpac");
            Assert.True(File.Exists(dacpacPath), $"DACPAC should be produced at {dacpacPath}. Output:\n{output}");

            await using var stream = File.OpenRead(dacpacPath);
            var (metadata, _) =
                await DacpacSerializer.Deserialize(stream, TestContext.Current.CancellationToken);

            Assert.Equal(16, metadata.TargetMajorVersion);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    // Source using a construct newer than SquillTargetVersion is reported as an SQ1003 warning
    // through the same MSBuild channel as every other coded diagnostic (issue #142), so the
    // build still succeeds — and the MSBuild* warning properties apply to it like any other.
    [Theory]
    [InlineData("", 0, "warning SQ1003")]
    // The issue asks for opting into treating it as an error. That needs no Squill-specific
    // property: the warning carries a code, so MSBuild's own escalation turns it fatal.
    //
    // The property is MSBuildWarningsAsErrors, not the WarningsAsErrors that a C# project
    // would use — the latter is a Roslyn compiler option and does not apply to a warning
    // logged by a task, which is what this one is.
    [InlineData("<MSBuildWarningsAsErrors>SQ1003</MSBuildWarningsAsErrors>", 1, "error SQ1003")]
    public async Task DotnetBuild_WithTooNewFeature_ReportsSq1003(
        string escalation, int expectedExitCode, string expectedText)
    {
        var repoRoot = FindRepoRoot();

        var taskDll = Path.Combine(repoRoot, "Squill.Build", "bin", "Debug", "net10.0", "Squill.Build.dll");
        Assert.True(File.Exists(taskDll),
            $"Squill.Build must be built before this test; expected {taskDll}. Run 'dotnet build Squill.Build'.");

        var tempDir = Directory.CreateTempSubdirectory("squill-e2e-too-new");
        try
        {
            var sdkDir = Path.Combine(repoRoot, "Squill.Sdk", "Sdk");

            // NULLS NOT DISTINCT arrived in PostgreSQL 15; this project targets 14.
            var projContent = $"""
<Project>
  <Import Project="{Path.Combine(sdkDir, "Sdk.props")}" />
  <PropertyGroup>
    <SquillTargetVersion>14</SquillTargetVersion>
    {escalation}
  </PropertyGroup>
  <Import Project="{Path.Combine(sdkDir, "Sdk.targets")}" />
</Project>
""";
            await File.WriteAllTextAsync(
                Path.Combine(tempDir.FullName, "TestDb.squillproj"),
                projContent,
                TestContext.Current.CancellationToken);

            await File.WriteAllTextAsync(
                Path.Combine(tempDir.FullName, "Account.sql"),
                "CREATE TABLE account (id integer PRIMARY KEY, email text);\n"
                + "CREATE UNIQUE INDEX ix_account_email ON account (email) NULLS NOT DISTINCT;",
                TestContext.Current.CancellationToken);

            var (exitCode, output) = await RunDotnetBuild(tempDir.FullName, "TestDb.squillproj");

            Assert.True(exitCode == expectedExitCode,
                $"Expected exit code {expectedExitCode}. Output:\n{output}");

            Assert.Contains(expectedText, output);
            Assert.Contains("NULLS NOT DISTINCT", output);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    // A syntax error in a .sql file must surface as a regular MSBuild error with
    // file/line/column metadata (issue #53) — rendered as `Bad.sql(2,x): error SQ0001: ...`
    // in build output — and fail the build.
    [Fact]
    public async Task DotnetBuild_WithSyntaxError_FailsWithSourceDiagnostic()
    {
        var repoRoot = FindRepoRoot();

        var taskDll = Path.Combine(repoRoot, "Squill.Build", "bin", "Debug", "net10.0", "Squill.Build.dll");
        Assert.True(File.Exists(taskDll),
            $"Squill.Build must be built before this test; expected {taskDll}. Run 'dotnet build Squill.Build'.");

        var tempDir = Directory.CreateTempSubdirectory("squill-e2e-error");
        try
        {
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
                Path.Combine(tempDir.FullName, "Bad.sql"),
                "CREATE TABLE widget (id integer PRIMARY KEY);\nCREATE bogus;\n",
                TestContext.Current.CancellationToken);

            var (exitCode, output) = await RunDotnetBuild(tempDir.FullName, "TestDb.squillproj");

            Assert.NotEqual(0, exitCode);
            // MSBuild renders a file-anchored diagnostic as `<file>(<line>,<col>): error <code>:`.
            Assert.Contains("Bad.sql(2,", output);
            Assert.Contains("error SQ0001", output);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    // The SDK's default globs must pick up PreDeploy.sql/PostDeploy.sql as deploy scripts
    // and — critically — exclude them from SquillCompile, so their contents are stored as
    // script text rather than parsed into the schema model (issue #67).
    [Fact]
    public async Task DotnetBuild_PicksUpDeployScripts_AndExcludesThemFromModel()
    {
        var repoRoot = FindRepoRoot();

        var tempDir = Directory.CreateTempSubdirectory("squill-e2e-deployscripts");
        try
        {
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

            await File.WriteAllTextAsync(
                Path.Combine(tempDir.FullName, "PreDeploy.sql"),
                "SELECT 'pre-deploy marker';",
                TestContext.Current.CancellationToken);

            // Deliberately DDL: if this were compiled into the model it would add a table.
            await File.WriteAllTextAsync(
                Path.Combine(tempDir.FullName, "PostDeploy.sql"),
                "CREATE TABLE IF NOT EXISTS seed_marker (id integer);",
                TestContext.Current.CancellationToken);

            var (exitCode, output) = await RunDotnetBuild(tempDir.FullName, "TestDb.squillproj");

            Assert.True(exitCode == 0, $"dotnet build should succeed. Output:\n{output}");

            var dacpacPath = Path.Combine(tempDir.FullName, "bin", "Debug", "TestDb.dacpac");
            await using var stream = File.OpenRead(dacpacPath);
            var (metadata, model) =
                await DacpacSerializer.Deserialize(stream, TestContext.Current.CancellationToken);

            Assert.Contains("pre-deploy marker", metadata.PreDeployScript);
            Assert.Contains("seed_marker", metadata.PostDeployScript);

            // The declared table is in the model; the post-deploy script's table is not.
            Assert.Contains(model.Elements, e => e.Name?.Contains("widget", StringComparison.OrdinalIgnoreCase) == true);
            Assert.DoesNotContain(
                model.Elements,
                e => e.Name?.Contains("seed_marker", StringComparison.OrdinalIgnoreCase) == true);
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
