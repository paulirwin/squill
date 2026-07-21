using Squill.Core;
using Squill.Dacpac;
using Squill.Provider.MariaDb;

namespace Squill.IntegrationTests.MariaDb;

/// <summary>
/// End-to-end tests of the MariaDB DACPAC deploy path — the exact code path the
/// <c>squill deploy</c> / <c>squill script</c> verbs use — against a real MariaDB or MySQL
/// server. Builds a real <c>.dacpac</c> the way the build path does, then deploys it into a
/// fresh database and asserts the deployed schema matches the DACPAC's model.
/// </summary>
public abstract class MariaDbDacpacDeployTests
{
    protected abstract MariaDbLikeFixture Fixture { get; }

    private const string SchemaSql = """
        CREATE TABLE customer
        (
            id   int NOT NULL AUTO_INCREMENT PRIMARY KEY,
            name varchar(100) NOT NULL
        );
        CREATE TABLE orders
        (
            id          int NOT NULL AUTO_INCREMENT PRIMARY KEY,
            customer_id int NOT NULL,
            total       decimal(10, 2) NOT NULL DEFAULT 0,
            CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id) REFERENCES customer (id)
        );
        """;

    private async Task<string> BuildDacpacAsync(
        string directory, CancellationToken ct, int? targetMajorVersion = null)
    {
        var sqlPath = Path.Combine(directory, "schema.sql");
        await File.WriteAllTextAsync(sqlPath, SchemaSql, ct);

        var workspace = DacpacBuilder.CreateWorkspace(new[] { sqlPath });
        var metadata = new ModelMetadata
        {
            Name = "TestDb",
            Version = "1.0.0.0",
            ProviderName = Fixture.ProviderName,
            TargetMajorVersion = targetMajorVersion,
        };

        var dacpacPath = Path.Combine(directory, "TestDb.dacpac");
        await DacpacBuilder.BuildToFileAsync(workspace, metadata, dacpacPath, ct);

        return dacpacPath;
    }

    [Fact]
    public async Task DeployFromFile_DeploysDacpacSchema_ToTargetDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-mariadb-deploy");

        try
        {
            var dacpacPath = await BuildDacpacAsync(tempDir.FullName, ct);

            Model dacpacModel;
            await using (var stream = File.OpenRead(dacpacPath))
            {
                (_, dacpacModel) = await DacpacSerializer.Deserialize(stream, ct);
            }

            IDatabaseProvider provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
            var targetDbName = $"squill_deploy_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var progressMessages = new List<string>();
                var progress = new CollectingProgress(progressMessages);

                var result = await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, Fixture.ConnectionString, targetDbName, dryRun: false,
                    progress: progress, cancellationToken: ct);

                Assert.True(result.WasExecuted, $"[{Fixture.EngineName}] deploy should execute the script.");
                Assert.False(string.IsNullOrWhiteSpace(result.Script));

                Assert.Contains(progressMessages, m => m.Contains("Creating table") && m.Contains("customer"));
                Assert.Contains(progressMessages, m => m.Contains("Creating table") && m.Contains("orders"));

                var deployedModel = await provider
                    .CreateDatabaseModelBuilder(createdDb)
                    .ExtractModelAsync(ct);

                Assert.Equal(ElementHashMultiset(dacpacModel), ElementHashMultiset(deployedModel));
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

    [Fact]
    public async Task Deploy_DryRun_ScriptsChanges_ButDoesNotExecute()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-mariadb-dryrun");

        try
        {
            var dacpacPath = await BuildDacpacAsync(tempDir.FullName, ct);

            IDatabaseProvider provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
            var targetDbName = $"squill_deploy_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var result = await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, Fixture.ConnectionString, targetDbName, dryRun: true,
                    cancellationToken: ct);

                Assert.False(result.WasExecuted, $"[{Fixture.EngineName}] a dry run must not execute.");
                Assert.False(string.IsNullOrWhiteSpace(result.Script));

                var deployedModel = await provider
                    .CreateDatabaseModelBuilder(createdDb)
                    .ExtractModelAsync(ct);

                Assert.DoesNotContain(deployedModel.Elements, e => e.Type == MariaDbElementTypes.SqlTable);
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

    // A DACPAC targeting a version the server satisfies deploys normally (issue #39).
    [Fact]
    public async Task Deploy_SucceedsWhenServerMeetsTargetVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-mariadb-version-ok");

        try
        {
            // The oldest supported version is met by any current test container.
            var dacpacPath = await BuildDacpacAsync(
                tempDir.FullName, ct, targetMajorVersion: Fixture.LowestSupportedMajor);

            IDatabaseProvider provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
            var targetDbName = $"squill_deploy_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var result = await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, Fixture.ConnectionString, targetDbName, dryRun: false,
                    cancellationToken: ct);

                Assert.True(result.WasExecuted);

                var deployedModel = await provider
                    .CreateDatabaseModelBuilder(createdDb)
                    .ExtractModelAsync(ct);
                Assert.Contains(deployedModel.Elements, e => e.Type == MariaDbElementTypes.SqlTable);
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

    // A stable, order-independent fingerprint of a model's elements, so two models with the
    // same objects match regardless of element order.
    private static IEnumerable<string> ElementHashMultiset(Model model)
        => model.Elements.Select(e => Convert.ToHexString(e.Hash)).OrderBy(h => h, StringComparer.Ordinal);

    private sealed class CollectingProgress(List<string> messages) : IProgress<string>
    {
        public void Report(string value) => messages.Add(value);
    }
}

public sealed class MariaDbDacpacDeployTestsMariaDb(MariaDbFixture fixture)
    : MariaDbDacpacDeployTests, IClassFixture<MariaDbFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

public sealed class MariaDbDacpacDeployTestsMySql(MySqlFixture fixture)
    : MariaDbDacpacDeployTests, IClassFixture<MySqlFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}
