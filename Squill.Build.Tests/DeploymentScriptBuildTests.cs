using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Squill.Dacpac;
using Task = System.Threading.Tasks.Task;

namespace Squill.Build.Tests;

/// <summary>
/// Covers pre/post-deployment scripts flowing from MSBuild items into the DACPAC
/// (issue #67). Multiple script files concatenate in item order, and the scripts
/// must not contribute elements to the schema model.
/// </summary>
public class DeploymentScriptBuildTests
{
    private const string SampleSchema = """
CREATE TABLE Foo
(
    id integer PRIMARY KEY,
    name varchar(100) NOT NULL
);
""";

    [Fact]
    public async Task Execute_RecordsPreAndPostDeployScripts()
    {
        var tempDir = Directory.CreateTempSubdirectory("squill-deployscript-test");
        try
        {
            var sqlPath = Path.Combine(tempDir.FullName, "Foo.sql");
            await File.WriteAllTextAsync(sqlPath, SampleSchema, TestContext.Current.CancellationToken);

            var prePath = Path.Combine(tempDir.FullName, "PreDeploy.sql");
            await File.WriteAllTextAsync(prePath, "SELECT 'pre';", TestContext.Current.CancellationToken);

            var postPath = Path.Combine(tempDir.FullName, "PostDeploy.sql");
            await File.WriteAllTextAsync(postPath, "SELECT 'post';", TestContext.Current.CancellationToken);

            var outputPath = Path.Combine(tempDir.FullName, "bin", "Sample.dacpac");
            var engine = new StubBuildEngine();
            var task = new BuildDacpacTask
            {
                BuildEngine = engine,
                SourceFiles = [new TaskItem(sqlPath)],
                PreDeployFiles = [new TaskItem(prePath)],
                PostDeployFiles = [new TaskItem(postPath)],
                OutputPath = outputPath,
            };

            Assert.True(task.Execute(), $"Errors: {string.Join("; ", engine.Errors.Select(e => e.Message))}");

            await using var stream = File.OpenRead(outputPath);
            var (metadata, _) =
                await DacpacSerializer.Deserialize(stream, TestContext.Current.CancellationToken);

            Assert.Contains("SELECT 'pre';", metadata.PreDeployScript);
            Assert.Contains("SELECT 'post';", metadata.PostDeployScript);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Execute_WithMultipleScriptFiles_ConcatenatesInItemOrder()
    {
        var tempDir = Directory.CreateTempSubdirectory("squill-deployscript-test");
        try
        {
            var sqlPath = Path.Combine(tempDir.FullName, "Foo.sql");
            await File.WriteAllTextAsync(sqlPath, SampleSchema, TestContext.Current.CancellationToken);

            var firstPath = Path.Combine(tempDir.FullName, "Seed1.sql");
            await File.WriteAllTextAsync(firstPath, "SELECT 1;", TestContext.Current.CancellationToken);

            var secondPath = Path.Combine(tempDir.FullName, "Seed2.sql");
            await File.WriteAllTextAsync(secondPath, "SELECT 2;", TestContext.Current.CancellationToken);

            var outputPath = Path.Combine(tempDir.FullName, "bin", "Sample.dacpac");
            var task = new BuildDacpacTask
            {
                BuildEngine = new StubBuildEngine(),
                SourceFiles = [new TaskItem(sqlPath)],
                PostDeployFiles = [new TaskItem(firstPath), new TaskItem(secondPath)],
                OutputPath = outputPath,
            };

            Assert.True(task.Execute());

            await using var stream = File.OpenRead(outputPath);
            var (metadata, _) =
                await DacpacSerializer.Deserialize(stream, TestContext.Current.CancellationToken);

            var first = metadata.PostDeployScript.IndexOf("SELECT 1;", StringComparison.Ordinal);
            var second = metadata.PostDeployScript.IndexOf("SELECT 2;", StringComparison.Ordinal);

            Assert.True(first >= 0 && second >= 0, "Both scripts should be present.");
            Assert.True(first < second, "Scripts must concatenate in item order.");
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Execute_DeployScripts_DoNotContributeToModel()
    {
        var tempDir = Directory.CreateTempSubdirectory("squill-deployscript-test");
        try
        {
            var sqlPath = Path.Combine(tempDir.FullName, "Foo.sql");
            await File.WriteAllTextAsync(sqlPath, SampleSchema, TestContext.Current.CancellationToken);

            // A post-deploy script containing DDL must not be parsed into the model —
            // it is imperative script text, not a declaration.
            var postPath = Path.Combine(tempDir.FullName, "PostDeploy.sql");
            await File.WriteAllTextAsync(
                postPath, "CREATE TABLE ShouldNotBeInModel (id integer);",
                TestContext.Current.CancellationToken);

            var outputPath = Path.Combine(tempDir.FullName, "bin", "Sample.dacpac");
            var task = new BuildDacpacTask
            {
                BuildEngine = new StubBuildEngine(),
                SourceFiles = [new TaskItem(sqlPath)],
                PostDeployFiles = [new TaskItem(postPath)],
                OutputPath = outputPath,
            };

            Assert.True(task.Execute());

            await using var stream = File.OpenRead(outputPath);
            var (_, model) =
                await DacpacSerializer.Deserialize(stream, TestContext.Current.CancellationToken);

            Assert.DoesNotContain(
                model.Elements,
                e => e.Name?.Contains("ShouldNotBeInModel", StringComparison.OrdinalIgnoreCase) == true);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Execute_WithNoScriptFiles_LeavesScriptsEmpty()
    {
        var tempDir = Directory.CreateTempSubdirectory("squill-deployscript-test");
        try
        {
            var sqlPath = Path.Combine(tempDir.FullName, "Foo.sql");
            await File.WriteAllTextAsync(sqlPath, SampleSchema, TestContext.Current.CancellationToken);

            var outputPath = Path.Combine(tempDir.FullName, "bin", "Sample.dacpac");
            var task = new BuildDacpacTask
            {
                BuildEngine = new StubBuildEngine(),
                SourceFiles = [new TaskItem(sqlPath)],
                OutputPath = outputPath,
            };

            Assert.True(task.Execute());

            await using var stream = File.OpenRead(outputPath);
            var (metadata, _) =
                await DacpacSerializer.Deserialize(stream, TestContext.Current.CancellationToken);

            Assert.Equal(string.Empty, metadata.PreDeployScript);
            Assert.Equal(string.Empty, metadata.PostDeployScript);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Execute_WithMissingScriptFile_FailsWithLoggedError()
    {
        var tempDir = Directory.CreateTempSubdirectory("squill-deployscript-test");
        try
        {
            var sqlPath = Path.Combine(tempDir.FullName, "Foo.sql");
            File.WriteAllText(sqlPath, SampleSchema);

            var engine = new StubBuildEngine();
            var task = new BuildDacpacTask
            {
                BuildEngine = engine,
                SourceFiles = [new TaskItem(sqlPath)],
                PostDeployFiles = [new TaskItem(Path.Combine(tempDir.FullName, "Missing.sql"))],
                OutputPath = Path.Combine(tempDir.FullName, "bin", "Sample.dacpac"),
            };

            Assert.False(task.Execute(), "A missing deploy script should fail the build.");
            Assert.NotEmpty(engine.Errors);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    // Minimal IBuildEngine that records logged diagnostics so tests can assert on them.
    private sealed class StubBuildEngine : IBuildEngine
    {
        public List<BuildErrorEventArgs> Errors { get; } = [];

        public List<BuildWarningEventArgs> Warnings { get; } = [];

        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e);

        public void LogWarningEvent(BuildWarningEventArgs e) => Warnings.Add(e);

        public void LogMessageEvent(BuildMessageEventArgs e) { }

        public void LogCustomEvent(CustomBuildEventArgs e) { }

        public bool BuildProjectFile(
            string projectFileName,
            string[] targetNames,
            System.Collections.IDictionary globalProperties,
            System.Collections.IDictionary targetOutputs) => true;

        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => string.Empty;
    }
}
