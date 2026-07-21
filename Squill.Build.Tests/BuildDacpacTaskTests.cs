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

            Assert.True(result, $"Task should succeed. Errors: {string.Join("; ", engine.Errors.Select(e => e.Message))}");
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
            var expected = (await DacpacBuilder.BuildModelAsync(workspace, TestContext.Current.CancellationToken)).Model;

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
    public void Execute_WithInvalidSql_LogsErrorWithFileLineAndColumn()
    {
        var tempDir = Directory.CreateTempSubdirectory("squill-buildtask-test");
        try
        {
            var sqlPath = Path.Combine(tempDir.FullName, "Bad.sql");
            // The syntax error is on line 2 so the test proves real positions flow through.
            File.WriteAllText(sqlPath, "CREATE TABLE foo (id integer PRIMARY KEY);\nCREATE bogus;\n");

            var engine = new StubBuildEngine();
            var task = new BuildDacpacTask
            {
                BuildEngine = engine,
                SourceFiles = [new TaskItem(sqlPath)],
                OutputPath = Path.Combine(tempDir.FullName, "bin", "Bad.dacpac"),
            };

            var result = task.Execute();

            Assert.False(result, "Task should fail for invalid SQL.");
            var error = Assert.Single(engine.Errors);
            Assert.Equal(sqlPath, error.File);
            Assert.Equal(2, error.LineNumber);
            Assert.True(error.ColumnNumber >= 1, "Column should be 1-based.");
            Assert.Equal("SQ0001", error.Code);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Execute_WithUnresolvedForeignKey_LogsErrorWithSourcePosition()
    {
        var tempDir = Directory.CreateTempSubdirectory("squill-buildtask-test");
        try
        {
            var sqlPath = Path.Combine(tempDir.FullName, "Book.sql");
            File.WriteAllText(sqlPath, """
CREATE TABLE book
(
    id integer PRIMARY KEY,
    author_id integer REFERENCES author (id)
);
""");

            var engine = new StubBuildEngine();
            var task = new BuildDacpacTask
            {
                BuildEngine = engine,
                SourceFiles = [new TaskItem(sqlPath)],
                OutputPath = Path.Combine(tempDir.FullName, "bin", "Book.dacpac"),
            };

            Assert.False(task.Execute(), "Task should fail for an unresolved foreign key reference.");

            var error = Assert.Single(engine.Errors);
            Assert.Equal(sqlPath, error.File);
            Assert.Equal(4, error.LineNumber);
            Assert.Equal("SQ0002", error.Code);
            Assert.Contains("author", error.Message);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Execute_WithMultipleUnresolvedForeignKeys_LogsEachError()
    {
        var tempDir = Directory.CreateTempSubdirectory("squill-buildtask-test");
        try
        {
            var bookPath = Path.Combine(tempDir.FullName, "Book.sql");
            File.WriteAllText(bookPath,
                "CREATE TABLE book (id integer PRIMARY KEY, author_id integer REFERENCES author (id));");

            var reviewPath = Path.Combine(tempDir.FullName, "Review.sql");
            File.WriteAllText(reviewPath,
                "CREATE TABLE review (id integer PRIMARY KEY, reviewer_id integer REFERENCES reviewer (id));");

            var engine = new StubBuildEngine();
            var task = new BuildDacpacTask
            {
                BuildEngine = engine,
                SourceFiles = [new TaskItem(bookPath), new TaskItem(reviewPath)],
                OutputPath = Path.Combine(tempDir.FullName, "bin", "Book.dacpac"),
            };

            Assert.False(task.Execute());

            Assert.Equal(2, engine.Errors.Count);
            Assert.Contains(engine.Errors, e => e.File == bookPath);
            Assert.Contains(engine.Errors, e => e.File == reviewPath);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    // Minimal IBuildEngine that records logged errors/warnings so tests can assert on
    // them — including the file/line/column metadata MSBuild diagnostics carry.
    [Fact]
    public async Task Execute_ReportsUnmodeledConstructAsCodedWarning()
    {
        // A build warning has to reach MSBuild with a code and a source position, or
        // NoWarn / WarningsAsErrors cannot act on it and the IDE cannot navigate to it
        // (issue #61).
        const string schema = """
CREATE TABLE Event
(
    id integer PRIMARY KEY,
    created_at timestamp DEFAULT now()
);
""";
        var tempDir = Directory.CreateTempSubdirectory("squill-buildtask-warn");
        try
        {
            var sqlPath = Path.Combine(tempDir.FullName, "Event.sql");
            await File.WriteAllTextAsync(sqlPath, schema, TestContext.Current.CancellationToken);

            var outputPath = Path.Combine(tempDir.FullName, "bin", "Sample.dacpac");

            var engine = new StubBuildEngine();
            var task = new BuildDacpacTask
            {
                BuildEngine = engine,
                SourceFiles = [new TaskItem(sqlPath)],
                OutputPath = outputPath,
                ProviderName = "Postgresql",
            };

            var result = task.Execute();

            // A warning must not fail the build.
            Assert.True(result, "An unmodeled construct is a warning, not an error.");
            Assert.Empty(engine.Errors);
            Assert.True(File.Exists(outputPath), "The DACPAC should still be written.");

            var warning = Assert.Single(engine.Warnings);
            Assert.Equal("SQ1002", warning.Code);
            Assert.Equal(sqlPath, warning.File);
            Assert.Equal(4, warning.LineNumber);
            Assert.Contains("created_at", warning.Message);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Execute_NoSourceFiles_WarnsWithSuppressibleCode()
    {
        var tempDir = Directory.CreateTempSubdirectory("squill-buildtask-nosource");
        try
        {
            var engine = new StubBuildEngine();
            var task = new BuildDacpacTask
            {
                BuildEngine = engine,
                SourceFiles = [],
                OutputPath = Path.Combine(tempDir.FullName, "bin", "Empty.dacpac"),
                ProviderName = "Postgresql",
            };

            Assert.True(task.Execute());

            var warning = Assert.Single(engine.Warnings);
            Assert.Equal("SQ1001", warning.Code);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Execute_ReportsErrorsFromEveryFileInOneBuild()
    {
        // Failing fast on the first bad file meant one rebuild per broken file; every file's
        // errors should surface together (issue #61).
        var tempDir = Directory.CreateTempSubdirectory("squill-buildtask-multierror");
        try
        {
            var firstPath = Path.Combine(tempDir.FullName, "A.sql");
            var secondPath = Path.Combine(tempDir.FullName, "B.sql");

            await File.WriteAllTextAsync(firstPath, "CREATE bogus;", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(secondPath, "CREATE alsobogus;", TestContext.Current.CancellationToken);

            var engine = new StubBuildEngine();
            var task = new BuildDacpacTask
            {
                BuildEngine = engine,
                SourceFiles = [new TaskItem(firstPath), new TaskItem(secondPath)],
                OutputPath = Path.Combine(tempDir.FullName, "bin", "Sample.dacpac"),
                ProviderName = "Postgresql",
            };

            Assert.False(task.Execute());

            Assert.Equal(2, engine.Errors.Count);
            Assert.Contains(engine.Errors, e => e.File == firstPath);
            Assert.Contains(engine.Errors, e => e.File == secondPath);
            Assert.All(engine.Errors, e => Assert.Equal("SQ0001", e.Code));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

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
