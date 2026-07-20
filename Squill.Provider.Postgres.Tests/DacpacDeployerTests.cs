using Squill.Core;
using Squill.Dacpac;

namespace Squill.Provider.Postgres.Tests;

public class DacpacDeployerTests
{
    private const string SampleSchema = """
CREATE TABLE Foo
(
    id integer PRIMARY KEY,
    name varchar(100) NOT NULL
);
""";

    // Full deploy behavior is covered by the integration tests against real Postgres.
    // This unit test pins the input-validation contract that needs no database: if the
    // connection string carries no Database and no target database is passed, the deploy
    // fails fast with a clear message rather than connecting to an unknown catalog.
    [Fact]
    public async Task DeployAsync_Throws_WhenNoDatabaseInConnectionStringAndNoTarget()
    {
        await using var dacpac = await BuildDacpacStreamAsync(TestContext.Current.CancellationToken);

        // A syntactically valid connection string with no Database keyword.
        const string connectionStringWithoutDatabase = "Host=localhost;Username=postgres";

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            DacpacDeployer.DeployAsync(
                dacpac,
                connectionStringWithoutDatabase,
                targetDatabaseName: null,
                dryRun: false,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("--target-database", ex.Message);
    }

    // The script path shares the same fail-fast validation as deploy: with no Database in
    // the connection string and no explicit target, scripting cannot know which catalog to
    // read, so it throws with a clear message before connecting. ScriptFromFileAsync reads
    // from a file, so exercise DeployAsync (its underlying dry-run path) with the stream.
    [Fact]
    public async Task ScriptPath_Throws_WhenNoDatabaseInConnectionStringAndNoTarget()
    {
        await using var dacpac = await BuildDacpacStreamAsync(TestContext.Current.CancellationToken);

        const string connectionStringWithoutDatabase = "Host=localhost;Username=postgres";

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            DacpacDeployer.DeployAsync(
                dacpac,
                connectionStringWithoutDatabase,
                targetDatabaseName: null,
                dryRun: true,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("--target-database", ex.Message);
    }

    private static async Task<MemoryStream> BuildDacpacStreamAsync(CancellationToken ct)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Foo.sql", FileKind.Compile, SampleSchema));

        var metadata = new ModelMetadata { ProviderName = "Postgresql" };

        var stream = new MemoryStream();
        await DacpacBuilder.BuildAsync(workspace, metadata, stream, ct);
        stream.Position = 0;

        return stream;
    }
}
