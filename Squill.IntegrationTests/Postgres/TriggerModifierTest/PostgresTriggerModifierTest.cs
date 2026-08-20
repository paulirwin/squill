using Squill.Core;
using Squill.PostgresParser;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.TriggerModifierTest;

// Full round trip for the CREATE TRIGGER declaration modifiers (issue #214): WHEN, UPDATE OF,
// REFERENCING transition tables, and CREATE CONSTRAINT TRIGGER. Each used to throw, so a schema
// declaring one could not be built at all.
//
// Two things are proven here that a unit test cannot. First, that the parsed and extracted
// models agree: PostgreSQL rewrites a WHEN predicate when it stores it, so the canonical form
// has to bridge the two spellings or the trigger is dropped and recreated on every deploy.
// Second, that the modifiers actually take effect. A trigger that is created but fires on the
// wrong statements is worse than one that fails to deploy, so the assertions below are about
// firing behaviour rather than about the DDL being accepted.
public class PostgresTriggerModifierTest : PostgresIntegrationTestBase
{
    private const string FixtureResource =
        "Squill.IntegrationTests.Postgres.TriggerModifierTest.TriggerModifiers.sql";

    [Fact]
    public async Task TriggerModifierRoundTrip_ModelHashesMatchAfterPublish()
    {
        var ct = TestContext.Current.CancellationToken;
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var workspace = new Workspace();
        workspace.Files.Add(new EmbeddedResourceFile(FixtureResource, FileKind.Compile));

        var model = (await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(ct)).Model;

        AssertModifiers(model);

        var testDb = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var comparison = SchemaCompare.Compare(
                provider, model, await dbModelBuilder.ExtractModelAsync(ct));
            await testDb.PublishAsync(comparison, ct);

            var publishedModel = await dbModelBuilder.ExtractModelAsync(ct);

            // The same assertions against the EXTRACTED model: every modifier has to survive
            // the trip through the catalog, not merely be parsed correctly.
            AssertModifiers(publishedModel);

            Assert.True(
                HashUtility.HashesEqual(model.Hash, publishedModel.Hash),
                "Parsed and extracted model hashes do not match");

            // Redeploying the same source must be a no-op. If any modifier failed to
            // round-trip, this is where it shows up as a spurious drop-and-recreate.
            var republish = SchemaCompare.Compare(provider, model, publishedModel);
            Assert.Empty(republish.Deltas);

            await AssertFiringBehaviourAsync(testDb, ct);
        }
        finally
        {
            await testDb.DropAsync(ct);
        }
    }

    private static void AssertModifiers(Model model)
    {
        var triggers = model.Elements
            .Where(i => i.Type == PostgresElementTypes.SqlTrigger)
            .ToList();

        Assert.Equal(4, triggers.Count);

        // WHEN: the raw predicate is kept for scripting, the canonical one for comparison.
        // Only the canonical form is asserted, because the raw text legitimately differs
        // between the two sides -- that is the whole reason the canonical form exists.
        var titleChanged = Assert.Single(
            triggers, t => (string?)t.Name == "public.film.title_changed");
        Assert.Equal(
            "(old.title IS DISTINCT FROM new.title)",
            titleChanged.GetProperty<string>(PostgresPropertyNames.NormalizedWhenCondition));

        var ratingTouched = Assert.Single(
            triggers, t => (string?)t.Name == "public.film.rating_touched");
        Assert.Equal(
            "rating", ratingTouched.GetProperty<string>(PostgresPropertyNames.UpdateOfColumns));
        Assert.Equal("UPDATE", ratingTouched.GetProperty<string>(PostgresPropertyNames.Events));

        var statementAudit = Assert.Single(
            triggers, t => (string?)t.Name == "public.film.statement_audit");
        Assert.Equal(
            "before_rows",
            statementAudit.GetProperty<string>(PostgresPropertyNames.OldTransitionTable));
        Assert.Equal(
            "after_rows",
            statementAudit.GetProperty<string>(PostgresPropertyNames.NewTransitionTable));
        Assert.Equal("STATEMENT", statementAudit.GetProperty<string>(PostgresPropertyNames.Level));

        var deferredAudit = Assert.Single(
            triggers, t => (string?)t.Name == "public.film.deferred_audit");
        Assert.True(
            deferredAudit.GetProperty<bool?>(PostgresPropertyNames.IsConstraintTrigger) == true);
        Assert.True(deferredAudit.GetProperty<bool?>(PostgresPropertyNames.IsDeferrable) == true);
        Assert.True(
            deferredAudit.GetProperty<bool?>(PostgresPropertyNames.IsInitiallyDeferred) == true);

        // The plain facets are untouched by the modifiers.
        Assert.Equal("AFTER", titleChanged.GetProperty<string>(PostgresPropertyNames.Timing));
        Assert.Equal("ROW", titleChanged.GetProperty<string>(PostgresPropertyNames.Level));
    }

    // Every expectation below was measured against a live PostgreSQL before being asserted.
    private static async Task AssertFiringBehaviourAsync(IDatabase database, CancellationToken ct)
    {
        await database.ConnectAsync(ct);

        await database.RunScriptAsync(
            "INSERT INTO film (film_id, title, rating, version) "
            + "VALUES (1, 'Inception', 'PG-13', 1);",
            cancellationToken: ct);

        // The deferred constraint trigger fires on insert (at commit, but the statement above
        // is its own transaction, so it has fired by the time the next query runs).
        Assert.Equal(["deferred_audit"], await ReadFiringsAsync(database, ct));
        await ClearAsync(database, ct);

        // A real title change: the WHEN predicate holds, so title_changed fires. The
        // statement-level trigger fires for any UPDATE. rating_touched does NOT, because the
        // SET list does not name rating.
        await database.RunScriptAsync(
            "UPDATE film SET title = 'Inception (2010)' WHERE film_id = 1;",
            cancellationToken: ct);
        Assert.Equal(["statement_audit", "title_changed"], await ReadFiringsAsync(database, ct));
        await ClearAsync(database, ct);

        // Setting the title to the value it already has: the WHEN predicate is false, so
        // title_changed must NOT fire. This is the assertion the WHEN clause exists for -- a
        // dropped WHEN would still deploy and would still fire, just always.
        await database.RunScriptAsync(
            "UPDATE film SET title = 'Inception (2010)' WHERE film_id = 1;",
            cancellationToken: ct);
        Assert.Equal(["statement_audit"], await ReadFiringsAsync(database, ct));
        await ClearAsync(database, ct);

        // Naming rating in the SET list fires rating_touched even though the value is
        // unchanged: UPDATE OF keys off the columns named, not off the values written.
        await database.RunScriptAsync(
            "UPDATE film SET rating = rating WHERE film_id = 1;",
            cancellationToken: ct);
        Assert.Equal(["rating_touched", "statement_audit"], await ReadFiringsAsync(database, ct));
    }

    private static async Task<List<string>> ReadFiringsAsync(
        IDatabase database, CancellationToken ct)
    {
        var sources = new List<string>();

        await using var reader = await database.RunScriptReaderAsync(
            "SELECT source FROM audit_log ORDER BY source;", cancellationToken: ct);

        while (await reader.ReadAsync(ct))
        {
            sources.Add(reader.GetString(0));
        }

        return sources;
    }

    private static Task ClearAsync(IDatabase database, CancellationToken ct)
        => database.RunScriptAsync("DELETE FROM audit_log;", cancellationToken: ct);
}
