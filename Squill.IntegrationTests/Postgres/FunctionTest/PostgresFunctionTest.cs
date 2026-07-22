using Squill.Core;
using Squill.Dacpac;
using Squill.PostgresParser;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.FunctionTest;

// Full round trip for functions (issue #81): parse SQL into a model, publish it into a fresh
// database, re-extract, and assert the functions survive and the model hashes match. This
// proves the parsed and extracted representations agree — the verbatim body, the normalized
// argument signature, the return type, and the volatility/strictness facets are recorded
// identically on both sides. It then calls a function to prove the emitted DDL is executable.
public class PostgresFunctionTest : PostgresIntegrationTestBase
{
    [Fact]
    public async Task FunctionRoundTrip_ModelHashesMatchAfterPublish()
    {
        var ct = TestContext.Current.CancellationToken;
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var workspace = new Workspace();
        workspace.Files.Add(new EmbeddedResourceFile(
            "Squill.IntegrationTests.Postgres.FunctionTest.Functions.sql", FileKind.Compile));

        var model = (await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(ct)).Model;

        AssertFunctions(model);

        var testDb = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var emptyModel = await dbModelBuilder.ExtractModelAsync(ct);

            var comparison = SchemaCompare.Compare(provider, model, emptyModel);
            await testDb.PublishAsync(comparison, ct);

            var publishedModel = await dbModelBuilder.ExtractModelAsync(ct);

            AssertFunctions(publishedModel);

            Assert.True(
                HashUtility.HashesEqual(model.Hash, publishedModel.Hash),
                "Parsed and extracted model hashes do not match");

            // Redeploying the same source must be a no-op: if any facet of a function did not
            // round-trip, the comparison would produce a spurious delta here.
            var republish = SchemaCompare.Compare(provider, model, publishedModel);
            Assert.Empty(republish.Deltas);

            await AssertFunctionIsCallableAsync(testDb, ct);
        }
        finally
        {
            await testDb.DropAsync(ct);
        }
    }

    // Deploys the functions through the exact code path `squill deploy` uses (build a real
    // .dacpac, then DacpacDeployer.DeployFromFileAsync), and asserts the per-object progress
    // names each function with its friendly label ("function") rather than the raw element
    // type name ("SqlFunction").
    [Fact]
    public async Task DeployFromFile_ReportsFunctionsWithFriendlyLabel()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-function-deploy");

        try
        {
            var schema = await new EmbeddedResourceFile(
                    "Squill.IntegrationTests.Postgres.FunctionTest.Functions.sql", FileKind.Compile)
                .ReadAllTextAsync(ct);
            var sqlPath = Path.Combine(tempDir.FullName, "Schema.sql");
            await File.WriteAllTextAsync(sqlPath, schema, ct);

            var dacpacPath = Path.Combine(tempDir.FullName, "bin", "Functions.dacpac");
            var workspace = DacpacBuilder.CreateWorkspace([sqlPath]);
            var metadata = new ModelMetadata { ProviderName = "Postgresql", Name = "Functions" };
            await DacpacBuilder.BuildToFileAsync(workspace, metadata, dacpacPath, ct);

            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_fn_deploy_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var progressMessages = new List<string>();
                var progress = new CollectingProgress(progressMessages);

                var result = await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, ConnectionString, targetDbName, dryRun: false,
                    progress: progress, cancellationToken: ct);

                Assert.True(result.WasExecuted);

                // The friendly label is used, not the raw "SqlFunction" element type.
                Assert.Contains(progressMessages, m => m.Contains("Creating function"));
                Assert.DoesNotContain(progressMessages, m => m.Contains("SqlFunction"));
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

    private static void AssertFunctions(Model model)
    {
        var functions = model.Elements
            .Where(i => i.Type == PostgresElementTypes.SqlFunction)
            .ToList();

        // Five declarations: widget_count, cheap_widget_ids, widget_name, and add_tax's two
        // overloads.
        Assert.Equal(5, functions.Count);

        // The overloads round-trip as distinct objects.
        Assert.Contains(functions, f => f.Name as string == "public.add_tax(numeric,numeric)");
        Assert.Contains(functions, f => f.Name as string == "public.add_tax(integer)");
    }

    private static async Task AssertFunctionIsCallableAsync(IDatabase database, CancellationToken ct)
    {
        await database.ConnectAsync(ct);

        await database.RunScriptAsync(
            "INSERT INTO widgets (id, name, price) VALUES (1, 'a', 10.00), (2, 'b', 20.00);",
            cancellationToken: ct);

        await using var reader = await database.RunScriptReaderAsync(
            "SELECT widget_count();", cancellationToken: ct);

        Assert.True(await reader.ReadAsync(ct), "Query returned no rows");
        Assert.Equal(2L, reader.GetInt64(0));
    }

    // An IProgress<string> that records every reported message, so tests can assert on the
    // deploy's per-object progress output.
    private sealed class CollectingProgress(List<string> messages) : IProgress<string>
    {
        public void Report(string value) => messages.Add(value);
    }
}
