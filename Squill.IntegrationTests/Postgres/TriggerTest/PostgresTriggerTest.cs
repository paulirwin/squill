using Squill.Core;
using Squill.Dacpac;
using Squill.PostgresParser;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.TriggerTest;

// Full round trip for triggers (issue #83): parse SQL into a model, publish it into a fresh
// database, re-extract, and assert the triggers survive and the model hashes match. This proves
// the parsed and extracted representations agree — the timing, events, level, target table and
// executed function (with its arguments) are recorded identically on both sides. It then fires a
// trigger to prove the emitted DDL is executable.
public class PostgresTriggerTest : PostgresIntegrationTestBase
{
    [Fact]
    public async Task TriggerRoundTrip_ModelHashesMatchAfterPublish()
    {
        var ct = TestContext.Current.CancellationToken;
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var workspace = new Workspace();
        workspace.Files.Add(new EmbeddedResourceFile(
            "Squill.IntegrationTests.Postgres.TriggerTest.Trigger.sql", FileKind.Compile));

        var model = (await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(ct)).Model;

        AssertTriggers(model);

        var testDb = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var emptyModel = await dbModelBuilder.ExtractModelAsync(ct);

            var comparison = SchemaCompare.Compare(provider, model, emptyModel);
            await testDb.PublishAsync(comparison, ct);

            var publishedModel = await dbModelBuilder.ExtractModelAsync(ct);

            AssertTriggers(publishedModel);

            Assert.True(
                HashUtility.HashesEqual(model.Hash, publishedModel.Hash),
                "Parsed and extracted model hashes do not match");

            // Redeploying the same source must be a no-op: if any facet of a trigger did not
            // round-trip, the comparison would produce a spurious delta here.
            var republish = SchemaCompare.Compare(provider, model, publishedModel);
            Assert.Empty(republish.Deltas);

            await AssertTriggerFiresAsync(testDb, ct);
        }
        finally
        {
            await testDb.DropAsync(ct);
        }
    }

    // Deploys the trigger through the exact code path `squill deploy` uses (build a real
    // .dacpac, then DacpacDeployer.DeployFromFileAsync), and asserts the per-object progress
    // names the trigger with its friendly label ("trigger") rather than the raw element type.
    [Fact]
    public async Task DeployFromFile_ReportsTriggerWithFriendlyLabel()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-trigger-deploy");

        try
        {
            var schema = await new EmbeddedResourceFile(
                    "Squill.IntegrationTests.Postgres.TriggerTest.Trigger.sql", FileKind.Compile)
                .ReadAllTextAsync(ct);
            var sqlPath = Path.Combine(tempDir.FullName, "Schema.sql");
            await File.WriteAllTextAsync(sqlPath, schema, ct);

            var dacpacPath = Path.Combine(tempDir.FullName, "bin", "Trigger.dacpac");
            var workspace = DacpacBuilder.CreateWorkspace([sqlPath]);
            var metadata = new ModelMetadata { ProviderName = "Postgresql", Name = "Trigger" };
            await DacpacBuilder.BuildToFileAsync(workspace, metadata, dacpacPath, ct);

            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_trigger_deploy_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var progressMessages = new List<string>();
                var progress = new CollectingProgress(progressMessages);

                var result = await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, ConnectionString, targetDbName, dryRun: false,
                    progress: progress, cancellationToken: ct);

                Assert.True(result.WasExecuted);

                // The friendly label is used, not the raw "SqlTrigger" element type.
                Assert.Contains(progressMessages, m => m.Contains("Creating trigger"));
                Assert.DoesNotContain(progressMessages, m => m.Contains("SqlTrigger"));
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

    private static void AssertTriggers(Model model)
    {
        var triggers = model.Elements
            .Where(i => i.Type == PostgresElementTypes.SqlTrigger)
            .ToList();

        Assert.Equal(2, triggers.Count);

        var bumpVersion = Assert.Single(triggers, t => (string?)t.Name == "public.film.bump_version");
        Assert.Equal("BEFORE", bumpVersion.GetProperty<string>(PostgresPropertyNames.Timing));
        Assert.Equal("UPDATE", bumpVersion.GetProperty<string>(PostgresPropertyNames.Events));
        Assert.Equal("ROW", bumpVersion.GetProperty<string>(PostgresPropertyNames.Level));
        Assert.Equal("bump_version", bumpVersion.GetProperty<string>(PostgresPropertyNames.TriggerFunction));
        Assert.Equal("", bumpVersion.GetProperty<string>(PostgresPropertyNames.FunctionArguments));

        var fulltext = Assert.Single(triggers, t => (string?)t.Name == "public.film.film_fulltext_trigger");
        Assert.Equal("INSERT OR UPDATE", fulltext.GetProperty<string>(PostgresPropertyNames.Events));
        Assert.Equal("tsvector_update_trigger",
            fulltext.GetProperty<string>(PostgresPropertyNames.TriggerFunction));
        Assert.Equal("fulltext, pg_catalog.english, title, description",
            fulltext.GetProperty<string>(PostgresPropertyNames.FunctionArguments));
    }

    // Inserts and updates a row, then asserts both triggers fired: the fulltext column was
    // populated on insert, and the version counter was bumped on update.
    private static async Task AssertTriggerFiresAsync(IDatabase database, CancellationToken ct)
    {
        await database.ConnectAsync(ct);

        await database.RunScriptAsync(
            "INSERT INTO film (film_id, title, description, version) "
            + "VALUES (1, 'Inception', 'A dream within a dream', 1);",
            cancellationToken: ct);

        // The fulltext trigger populated the tsvector on insert.
        await using (var reader = await database.RunScriptReaderAsync(
                         "SELECT fulltext IS NOT NULL AS has_fulltext FROM film WHERE film_id = 1;",
                         cancellationToken: ct))
        {
            Assert.True(await reader.ReadAsync(ct), "Query returned no rows");
            Assert.True(reader.GetBoolean(0), "fulltext trigger did not populate the tsvector");
        }

        await database.RunScriptAsync(
            "UPDATE film SET title = 'Inception (2010)' WHERE film_id = 1;",
            cancellationToken: ct);

        // The bump_version trigger incremented the version counter on update.
        await using (var reader = await database.RunScriptReaderAsync(
                         "SELECT version FROM film WHERE film_id = 1;",
                         cancellationToken: ct))
        {
            Assert.True(await reader.ReadAsync(ct), "Query returned no rows");
            Assert.Equal(2, reader.GetInt32(0));
        }
    }

    // An IProgress<string> that records every reported message, so tests can assert on the
    // deploy's per-object progress output.
    private sealed class CollectingProgress(List<string> messages) : IProgress<string>
    {
        public void Report(string value) => messages.Add(value);
    }
}
