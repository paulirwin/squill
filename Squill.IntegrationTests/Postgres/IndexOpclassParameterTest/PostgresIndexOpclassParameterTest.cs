using Squill.Core;
using Squill.PostgresParser;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.IndexOpclassParameterTest;

// Full round trip for parameterized operator classes (issue #211). The visitor read only the
// first alternative of index_elem_options, so a key written `tsv tsvector_ops(siglen=256)` lost
// both the parameters and the opclass name.
//
// What a unit test cannot prove is the half that makes this deployable. Measured, the opclass in
// this fixture is the type's DEFAULT opclass, so the extractor's opcdefault suppression would
// drop its name -- and PostgreSQL rejects the parameters without a name ("column siglen does not
// exist"). Only a real server shows that, which is why the round trip below matters more than
// the parse.
public class PostgresIndexOpclassParameterTest : PostgresIntegrationTestBase
{
    private const string FixtureResource =
        "Squill.IntegrationTests.Postgres.IndexOpclassParameterTest.OpclassParameters.sql";

    [Fact]
    public async Task OpclassParameterRoundTrip_ModelHashesMatchAfterPublish()
    {
        var ct = TestContext.Current.CancellationToken;
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var workspace = new Workspace();
        workspace.Files.Add(new EmbeddedResourceFile(FixtureResource, FileKind.Compile));

        var model = (await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(ct)).Model;

        AssertOpclasses(model);

        var testDb = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var comparison = SchemaCompare.Compare(
                provider, model, await dbModelBuilder.ExtractModelAsync(ct));
            await testDb.PublishAsync(comparison, ct);

            var publishedModel = await dbModelBuilder.ExtractModelAsync(ct);

            // The same assertions against the EXTRACTED model: the parameters have to survive
            // the trip through pg_attribute.attoptions, not merely be parsed correctly.
            AssertOpclasses(publishedModel);

            Assert.True(
                HashUtility.HashesEqual(model.Hash, publishedModel.Hash),
                "Parsed and extracted model hashes do not match");

            // Redeploying the same source must be a no-op. If the parameters or the opclass name
            // failed to round-trip, this is where it shows up as a spurious drop-and-recreate.
            var republish = SchemaCompare.Compare(provider, model, publishedModel);
            Assert.Empty(republish.Deltas);
        }
        finally
        {
            await testDb.DropAsync(ct);
        }
    }

    private static void AssertOpclasses(Model model)
    {
        var indexes = model.Elements
            .Where(e => e.Type == PostgresElementTypes.SqlIndex)
            .ToList();

        var parameterized = Assert.Single(indexes, i => i.Name == "idx_docs_tsv_siglen");
        var parameterizedKey = SingleKey(parameterized);

        // The name is kept even though it is the type's default opclass, because the parameters
        // cannot be written without it.
        Assert.Equal(
            "tsvector_ops",
            parameterizedKey.GetProperty<string>(PostgresPropertyNames.OperatorClass));
        Assert.Equal(
            "siglen=256",
            parameterizedKey.GetProperty<string>(PostgresPropertyNames.OperatorClassParameters));

        // The same default opclass WITHOUT parameters stays suppressed, which is what keeps
        // every ordinary index from re-diffing.
        var plain = Assert.Single(indexes, i => i.Name == "idx_docs_tsv_plain");
        var plainKey = SingleKey(plain);

        Assert.Null(plainKey.GetProperty<string>(PostgresPropertyNames.OperatorClass));
        Assert.Null(plainKey.GetProperty<string>(PostgresPropertyNames.OperatorClassParameters));

        // A non-default opclass is named, and carries no parameters.
        var pattern = Assert.Single(indexes, i => i.Name == "idx_docs_body_pattern");
        var patternKey = SingleKey(pattern);

        Assert.Equal(
            "text_pattern_ops",
            patternKey.GetProperty<string>(PostgresPropertyNames.OperatorClass));
        Assert.Null(patternKey.GetProperty<string>(PostgresPropertyNames.OperatorClassParameters));
    }

    private static Element SingleKey(Element index)
        => Assert.Single(
            index.GetRelationship(PostgresRelationshipNames.ColumnSpecifications)!
                .Entries.OfType<Element>());
}
