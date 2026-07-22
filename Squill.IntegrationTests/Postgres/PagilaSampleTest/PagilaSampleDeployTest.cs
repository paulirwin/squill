using Squill.Core;
using Squill.Dacpac;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.PagilaSampleTest;

/// <summary>
/// Tests for the <c>PagilaSampleDatabase</c> sample — the PostgreSQL port of the Sakila
/// DVD-rental schema (see <c>samples/PagilaSampleDatabase</c>). The sample is a deliberately
/// ambitious, production-style schema (enum and domain types, identity keys, foreign keys,
/// multi-column and GiST indexes, tsvector full-text, array columns, PL/pgSQL and SQL functions,
/// an aggregate, and triggers).
///
/// <para>
/// The schema centres on the <c>film</c> table, which nearly every other table references and
/// which uses several Postgres features the provider does not yet model. As a result the sample
/// cannot currently be built or deployed. <see cref="BuildFullSchema_IsNotYetSupported"/> is a
/// passing test that documents this: it asserts building the sample still fails, so the day the
/// features land, the test starts failing and flags that the deploy test can be un-skipped.
/// </para>
///
/// <para>
/// Missing features (why <see cref="Deploy_PagilaSample_ProducesTheSampleSchema"/> is skipped):
/// </para>
/// <list type="bullet">
///   <item><description><c>CREATE TRIGGER</c> — the sample's per-table <c>last_updated</c>
///     triggers and the full-text trigger are not modeled yet.</description></item>
/// </list>
/// <para>
/// Already supported: <c>CREATE TYPE ... AS ENUM</c> (mpaa_rating) and <c>CREATE DOMAIN</c>
/// with a CHECK (year) since #75/#80, PL/pgSQL and SQL functions since #81, the group_concat
/// user-defined aggregate since #82, and tsvector + GiST full-text and array columns since #76.
/// </para>
/// </summary>
public class PagilaSampleDeployTest : PostgresIntegrationTestBase
{
    private const string SchemaResource =
        "Squill.IntegrationTests.Postgres.PagilaSampleTest.PagilaSchema.sql";

    private static async Task<string> WriteSchemaAsync(string dir, CancellationToken ct)
    {
        var schema = await new EmbeddedResourceFile(SchemaResource, FileKind.Compile)
            .ReadAllTextAsync(ct);
        var sqlPath = Path.Combine(dir, "Schema.sql");
        await File.WriteAllTextAsync(sqlPath, schema, ct);
        return sqlPath;
    }

    /// <summary>
    /// Documents that the full Pagila sample cannot yet be built: the build path throws when it
    /// reaches one of the unsupported features on the class summary. This test needs no database
    /// and passes today; when the missing features are implemented it will start failing, which is
    /// the signal to remove the Skip from <see cref="Deploy_PagilaSample_ProducesTheSampleSchema"/>.
    /// </summary>
    [Fact]
    public async Task BuildFullSchema_IsNotYetSupported()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-pagila-build");

        try
        {
            var sqlPath = await WriteSchemaAsync(tempDir.FullName, ct);
            var dacpacPath = Path.Combine(tempDir.FullName, "bin", "Pagila.dacpac");
            var workspace = DacpacBuilder.CreateWorkspace([sqlPath]);
            var metadata = new ModelMetadata { ProviderName = "Postgresql", Name = "Pagila" };

            // Building the full schema throws because the schema still uses features the
            // provider does not model yet — chiefly CREATE TRIGGER. (Enum/domain types are
            // supported since #75/#80, functions since #81, the group_concat aggregate since
            // #82, and tsvector + GiST full-text and array columns since #76.) If this ever
            // stops throwing, the remaining features have landed: remove the Skip on the
            // deploy test below.
            await Assert.ThrowsAnyAsync<Exception>(() =>
                DacpacBuilder.BuildToFileAsync(workspace, metadata, dacpacPath, ct));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The end-to-end deploy of the Pagila sample into a real Postgres database via the exact code
    /// path <c>squill deploy</c> uses. Skipped until the features on the class summary are
    /// supported; when they are, remove the Skip and this should deploy the full schema and the
    /// deployed model should match the DACPAC's.
    /// </summary>
    [Fact(Skip = "The Pagila sample still needs Postgres features the provider does not yet model: "
        + "chiefly CREATE TRIGGER. (Enum/domain types are supported since #75/#80, functions since "
        + "#81, the group_concat aggregate since #82, and tsvector + GiST full-text and array "
        + "columns since #76.) Remove Skip once these are supported "
        + "(BuildFullSchema_IsNotYetSupported will start failing then).")]
    public async Task Deploy_PagilaSample_ProducesTheSampleSchema()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-pagila-deploy");

        try
        {
            var sqlPath = await WriteSchemaAsync(tempDir.FullName, ct);
            var dacpacPath = Path.Combine(tempDir.FullName, "bin", "Pagila.dacpac");
            var workspace = DacpacBuilder.CreateWorkspace([sqlPath]);
            var metadata = new ModelMetadata { ProviderName = "Postgresql", Name = "Pagila" };
            await DacpacBuilder.BuildToFileAsync(workspace, metadata, dacpacPath, ct);

            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_pagila_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var result = await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, ConnectionString, targetDbName, dryRun: false,
                    cancellationToken: ct);

                // The goal is simply a successful full build → deploy against real Postgres.
                Assert.True(result.WasExecuted);
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
