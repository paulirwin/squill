using Squill.Core;
using Squill.Dacpac;
using Squill.Provider.MariaDb;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.MariaDb;

/// <summary>
/// Exercises the provider-dispatch path the CLI uses: a DACPAC labeled with a MariaDB
/// provider name is deployed via <see cref="DacpacProviderDispatch"/> against a registry
/// holding both the Postgres and MariaDB providers, and must be routed to the MariaDB
/// provider and deploy successfully to a real MariaDB/MySQL database.
/// </summary>
public abstract class MariaDbProviderDispatchTests
{
    protected abstract MariaDbLikeFixture Fixture { get; }

    // The registry the CLI builds: both providers registered, dispatch chooses by name.
    private static SquillProviderRegistry Registry() => new SquillProviderRegistry()
        .Register(new PostgresSquillProvider())
        .Register(new MariaDbSquillProvider());

    private const string SchemaSql = """
        CREATE TABLE widget
        (
            id   int NOT NULL AUTO_INCREMENT PRIMARY KEY,
            name varchar(100) NOT NULL
        );
        """;

    [Theory]
    [InlineData("MariaDb")]
    [InlineData("MySql")]
    public async Task Deploy_RoutesByDacpacProviderName_ToMariaDbProvider(string providerName)
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-dispatch");

        try
        {
            // Build a DACPAC labeled with the given MariaDB-family provider name, the way the
            // MSBuild task does for a project whose SquillProviderName is MariaDb or MySql.
            var sqlPath = Path.Combine(tempDir.FullName, "schema.sql");
            await File.WriteAllTextAsync(sqlPath, SchemaSql, ct);

            var workspace = new Workspace();
            workspace.Files.Add(new FileSystemFile(sqlPath, FileKind.Compile));

            var metadata = new ModelMetadata { Name = "Dispatch", ProviderName = providerName };

            var model = (await new MariaDbSquillProvider()
                .BuildModelAsync(workspace, metadata, ct)).Model;

            var dacpacPath = Path.Combine(tempDir.FullName, "Dispatch.dacpac");
            await using (var stream = File.Create(dacpacPath))
            {
                await DacpacSerializer.Serialize(metadata, model, stream, ct);
            }

            // Deploy via the dispatch path — the registry must route this DACPAC to the
            // MariaDB provider based on its recorded provider name.
            var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
            var targetDbName = $"squill_dispatch_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var result = await DacpacProviderDispatch.DeployFromFileAsync(
                    Registry(), dacpacPath, Fixture.ConnectionString, targetDbName,
                    dryRun: false, cancellationToken: ct);

                Assert.True(result.WasExecuted, $"[{Fixture.EngineName}] deploy should execute.");

                var deployedModel = await provider
                    .CreateDatabaseModelBuilder(createdDb)
                    .ExtractModelAsync(ct);

                Assert.Contains(deployedModel.Elements,
                    e => e.Type == MariaDbElementTypes.SqlTable && e.Name == "widget");
            }
            finally
            {
                await createdDb.DropAsync(ct);
            }
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}

public sealed class MariaDbProviderDispatchTestsMariaDb(MariaDbFixture fixture)
    : MariaDbProviderDispatchTests, IClassFixture<MariaDbFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

public sealed class MariaDbProviderDispatchTestsMySql(MySqlFixture fixture)
    : MariaDbProviderDispatchTests, IClassFixture<MySqlFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}
