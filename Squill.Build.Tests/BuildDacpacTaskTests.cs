using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Squill.Core;
using Squill.Dacpac;
using Squill.Provider.Postgres;
using Task = System.Threading.Tasks.Task;

namespace Squill.Build.Tests;

public class BuildDacpacTaskTests
{
    private const string SampleSchema = """
CREATE TABLE Foo
(
    id integer PRIMARY KEY,
    name varchar(100) NOT NULL
);
""";

    [Fact]
    public async Task Execute_WritesDacpacFileForSourceSql()
    {
        var tempDir = Directory.CreateTempSubdirectory("squill-buildtask-test");
        try
        {
            var sqlPath = Path.Combine(tempDir.FullName, "Foo.sql");
            await File.WriteAllTextAsync(sqlPath, SampleSchema, TestContext.Current.CancellationToken);

            var outputPath = Path.Combine(tempDir.FullName, "bin", "Sample.dacpac");

            var engine = new StubBuildEngine();
            var task = new BuildDacpacTask
            {
                BuildEngine = engine,
                SourceFiles = [new TaskItem(sqlPath)],
                OutputPath = outputPath,
                ProviderName = "Postgresql",
                DacName = "Sample",
                DacVersion = "2.0.0.0",
            };

            var result = task.Execute();

            Assert.True(result, $"Task should succeed. Errors: {string.Join("; ", engine.Errors)}");
            Assert.Empty(engine.Errors);
            Assert.True(File.Exists(outputPath), "DACPAC should be written to the output path.");

            await using var stream = File.OpenRead(outputPath);
            var (metadata, model) =
                await DacpacSerializer.Deserialize(stream, TestContext.Current.CancellationToken);

            Assert.Equal("Postgresql", metadata.ProviderName);
            Assert.Equal("Sample", metadata.Name);
            Assert.Equal("2.0.0.0", metadata.Version);
            Assert.Contains(model.Elements, e => e.Type == PostgresElementTypes.SqlTable);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Execute_RecordsTargetVersionInDacpac()
    {
        var tempDir = Directory.CreateTempSubdirectory("squill-buildtask-test");
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
                ProviderName = "Postgresql",
                // A dotted value keeps only the major component.
                TargetVersion = "16.2",
            };

            Assert.True(task.Execute());

            await using var stream = File.OpenRead(outputPath);
            var (metadata, _) =
                await DacpacSerializer.Deserialize(stream, TestContext.Current.CancellationToken);

            Assert.Equal(16, metadata.TargetMajorVersion);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Execute_WithoutTargetVersion_LeavesItUnconstrained()
    {
        var tempDir = Directory.CreateTempSubdirectory("squill-buildtask-test");
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

            Assert.Null(metadata.TargetMajorVersion);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Execute_WithNonNumericTargetVersion_FailsWithLoggedError()
    {
        var tempDir = Directory.CreateTempSubdirectory("squill-buildtask-test");
        try
        {
            var sqlPath = Path.Combine(tempDir.FullName, "Foo.sql");
            File.WriteAllText(sqlPath, SampleSchema);

            var engine = new StubBuildEngine();
            var task = new BuildDacpacTask
            {
                BuildEngine = engine,
                SourceFiles = [new TaskItem(sqlPath)],
                OutputPath = Path.Combine(tempDir.FullName, "bin", "Sample.dacpac"),
                TargetVersion = "not-a-version",
            };

            Assert.False(task.Execute(), "Task should fail for an invalid target version.");
            Assert.NotEmpty(engine.Errors);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Execute_BuildsSameModelAsDacpacBuilder()
    {
        var tempDir = Directory.CreateTempSubdirectory("squill-buildtask-test");
        try
        {
            var sqlPath = Path.Combine(tempDir.FullName, "Foo.sql");
            await File.WriteAllTextAsync(sqlPath, SampleSchema, TestContext.Current.CancellationToken);

            // The model produced through the shared builder is the source of truth.
            var workspace = DacpacBuilder.CreateWorkspace([sqlPath]);
            var expected = await DacpacBuilder.BuildModelAsync(workspace, TestContext.Current.CancellationToken);

            var outputPath = Path.Combine(tempDir.FullName, "bin", "Sample.dacpac");
            var task = new BuildDacpacTask
            {
                BuildEngine = new StubBuildEngine(),
                SourceFiles = [new TaskItem(sqlPath)],
                OutputPath = outputPath,
            };

            Assert.True(task.Execute());

            await using var stream = File.OpenRead(outputPath);
            var (_, actual) = await DacpacSerializer.Deserialize(stream, TestContext.Current.CancellationToken);

            Assert.True(
                HashUtility.HashesEqual(expected.Hash, actual.Hash),
                "Task-built DACPAC must hash-match the DacpacBuilder model.");
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Execute_WithInvalidSql_FailsWithLoggedError()
    {
        var tempDir = Directory.CreateTempSubdirectory("squill-buildtask-test");
        try
        {
            var sqlPath = Path.Combine(tempDir.FullName, "Bad.sql");
            File.WriteAllText(sqlPath, "this is not valid sql;");

            var engine = new StubBuildEngine();
            var task = new BuildDacpacTask
            {
                BuildEngine = engine,
                SourceFiles = [new TaskItem(sqlPath)],
                OutputPath = Path.Combine(tempDir.FullName, "bin", "Bad.dacpac"),
            };

            var result = task.Execute();

            Assert.False(result, "Task should fail for invalid SQL.");
            Assert.NotEmpty(engine.Errors);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    // Minimal IBuildEngine that records logged errors so tests can assert on them.
    private sealed class StubBuildEngine : IBuildEngine
    {
        public List<string> Errors { get; } = [];

        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e.Message ?? string.Empty);

        public void LogWarningEvent(BuildWarningEventArgs e) { }

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
