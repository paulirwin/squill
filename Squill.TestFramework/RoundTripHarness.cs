using Squill.Core;
using Xunit;

namespace Squill.TestFramework;

/// <summary>
/// The shared "parse → publish → re-extract → assert hash-equal" round-trip harness, in a
/// try/finally that always drops the temporary database. This is the single most-copied
/// integration-test scaffold. Provider-agnostic: the caller supplies an
/// <see cref="IDatabaseProvider"/> and the already-parsed source <see cref="Model"/>.
/// </summary>
public static class RoundTripHarness
{
    /// <summary>
    /// Creates a fresh database, publishes the diff between <paramref name="parsedModel"/> and
    /// the empty database into it, re-extracts the database model, and asserts it hash-matches
    /// the parsed model. Returns the re-extracted model so callers can make further assertions
    /// against what was actually deployed.
    /// </summary>
    /// <param name="provider">The database provider to deploy against.</param>
    /// <param name="parsedModel">The model parsed from declarative SQL.</param>
    /// <param name="engineName">A label for assertion messages (e.g. the engine name).</param>
    /// <param name="assertRedeployNoOp">
    /// When true, also asserts that comparing the source against the re-extracted model yields
    /// no deltas — i.e. redeploying the same source is a no-op.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public static async Task<Model> AssertRoundTripAsync(
        IDatabaseProvider provider,
        Model parsedModel,
        string engineName,
        bool assertRedeployNoOp = false,
        CancellationToken cancellationToken = default)
    {
        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", cancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var targetModel = await dbModelBuilder.ExtractModelAsync(cancellationToken);

            await testDb.PublishAsync(
                SchemaCompare.Compare(provider, parsedModel, targetModel), cancellationToken);

            var newModel = await dbModelBuilder.ExtractModelAsync(cancellationToken);

            Assert.True(
                HashUtility.HashesEqual(parsedModel.Hash, newModel.Hash),
                $"[{engineName}] Parsed and extracted model hashes do not match.\n"
                + $"Parsed:    {ModelAssertions.Describe(parsedModel)}\n"
                + $"Extracted: {ModelAssertions.Describe(newModel)}");

            if (assertRedeployNoOp)
            {
                // Redeploying the same source must be a no-op.
                Assert.Empty(SchemaCompare.Compare(provider, parsedModel, newModel).Deltas);
            }

            // The model as extracted from the database, so a caller's extra assertions are
            // made against what was actually deployed rather than against the source.
            return newModel;
        }
        finally
        {
            await testDb.DropAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Parses <paramref name="sql"/> with the supplied provider's model builder and round-trips
    /// it through a fresh database, asserting the parsed and re-extracted models hash-match. This
    /// is the "build model, then round-trip" composition each provider's data-type test declared
    /// as its own private helper — the only per-provider parts are the model-builder factory and
    /// the engine label.
    /// </summary>
    /// <param name="provider">The database provider to deploy against.</param>
    /// <param name="modelBuilderFactory">Builds the provider's ANTLR-backed workspace model builder.</param>
    /// <param name="sql">The declarative SQL to parse and deploy.</param>
    /// <param name="engineName">A label for assertion messages (e.g. the engine name).</param>
    /// <param name="assertRedeployNoOp">
    /// When true, also asserts that redeploying the same source against the re-extracted model is
    /// a no-op.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public static async Task<Model> AssertRoundTripAsync(
        IDatabaseProvider provider,
        Func<Workspace, IWorkspaceModelBuilder> modelBuilderFactory,
        string sql,
        string engineName,
        bool assertRedeployNoOp = false,
        CancellationToken cancellationToken = default)
    {
        var model = await WorkspaceModelBuilding.BuildModelAsync(sql, modelBuilderFactory, cancellationToken);

        return await AssertRoundTripAsync(
            provider, model, engineName, assertRedeployNoOp, cancellationToken);
    }
}
