using Squill.Core;
using Squill.Dacpac;
using Squill.PostgresParser;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.AggregateTest;

// Full round trip for aggregates (issue #82): parse SQL into a model, publish it into a fresh
// database, re-extract, and assert the aggregate survives and the model hashes match. This
// proves the parsed and extracted representations agree — the input signature, the SFUNC
// (state function) and the STYPE (state type) are recorded identically on both sides. It then
// uses the aggregate to prove the emitted DDL is executable.
public class PostgresAggregateTest : PostgresIntegrationTestBase
{
    [Fact]
    public async Task AggregateRoundTrip_ModelHashesMatchAfterPublish()
    {
        var ct = TestContext.Current.CancellationToken;
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var workspace = new Workspace();
        workspace.Files.Add(new EmbeddedResourceFile(
            "Squill.IntegrationTests.Postgres.AggregateTest.Aggregate.sql", FileKind.Compile));

        var model = (await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(ct)).Model;

        AssertAggregate(model);

        var testDb = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var emptyModel = await dbModelBuilder.ExtractModelAsync(ct);

            var comparison = SchemaCompare.Compare(provider, model, emptyModel);
            await testDb.PublishAsync(comparison, ct);

            var publishedModel = await dbModelBuilder.ExtractModelAsync(ct);

            AssertAggregate(publishedModel);

            Assert.True(
                HashUtility.HashesEqual(model.Hash, publishedModel.Hash),
                "Parsed and extracted model hashes do not match");

            // Redeploying the same source must be a no-op: if any facet of the aggregate did
            // not round-trip, the comparison would produce a spurious delta here.
            var republish = SchemaCompare.Compare(provider, model, publishedModel);
            Assert.Empty(republish.Deltas);

            await AssertAggregateIsCallableAsync(testDb, ct);
        }
        finally
        {
            await testDb.DropAsync(ct);
        }
    }

    // Deploys the aggregate through the exact code path `squill deploy` uses (build a real
    // .dacpac, then DacpacDeployer.DeployFromFileAsync), and asserts the per-object progress
    // names the aggregate with its friendly label ("aggregate") rather than the raw element
    // type name ("SqlAggregate").
    [Fact]
    public async Task DeployFromFile_ReportsAggregateWithFriendlyLabel()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-aggregate-deploy");

        try
        {
            var schema = await new EmbeddedResourceFile(
                    "Squill.IntegrationTests.Postgres.AggregateTest.Aggregate.sql", FileKind.Compile)
                .ReadAllTextAsync(ct);
            var sqlPath = Path.Combine(tempDir.FullName, "Schema.sql");
            await File.WriteAllTextAsync(sqlPath, schema, ct);

            var dacpacPath = Path.Combine(tempDir.FullName, "bin", "Aggregate.dacpac");
            var workspace = DacpacBuilder.CreateWorkspace([sqlPath]);
            var metadata = new ModelMetadata { ProviderName = "Postgresql", Name = "Aggregate" };
            await DacpacBuilder.BuildToFileAsync(workspace, metadata, dacpacPath, ct);

            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_agg_deploy_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var progressMessages = new List<string>();
                var progress = new CollectingProgress(progressMessages);

                var result = await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, ConnectionString, targetDbName, dryRun: false,
                    progress: progress, cancellationToken: ct);

                Assert.True(result.WasExecuted);

                // The friendly label is used, not the raw "SqlAggregate" element type.
                Assert.Contains(progressMessages, m => m.Contains("Creating aggregate"));
                Assert.DoesNotContain(progressMessages, m => m.Contains("SqlAggregate"));
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

    private static void AssertAggregate(Model model)
    {
        var aggregate = Assert.Single(
            model.Elements, i => i.Type == PostgresElementTypes.SqlAggregate);

        Assert.Equal("public.group_concat(text)", aggregate.Name);
        Assert.Equal("public._group_concat", aggregate.GetProperty<string>(PostgresPropertyNames.StateFunction));
        Assert.Equal("text", aggregate.GetProperty<string>(PostgresPropertyNames.StateType));
    }

    private static async Task AssertAggregateIsCallableAsync(IDatabase database, CancellationToken ct)
    {
        await database.ConnectAsync(ct);

        await database.RunScriptAsync(
            "INSERT INTO tags (id, label) VALUES (1, 'red'), (2, 'green'), (3, 'blue');",
            cancellationToken: ct);

        await using var reader = await database.RunScriptReaderAsync(
            "SELECT group_concat(label) FROM (SELECT label FROM tags ORDER BY id) t;",
            cancellationToken: ct);

        Assert.True(await reader.ReadAsync(ct), "Query returned no rows");
        Assert.Equal("red, green, blue", reader.GetString(0));
    }

    // An IProgress<string> that records every reported message, so tests can assert on the
    // deploy's per-object progress output.
    private sealed class CollectingProgress(List<string> messages) : IProgress<string>
    {
        public void Report(string value) => messages.Add(value);
    }
}
