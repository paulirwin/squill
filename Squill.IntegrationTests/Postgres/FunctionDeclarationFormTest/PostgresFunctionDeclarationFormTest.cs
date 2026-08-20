using Squill.Core;
using Squill.PostgresParser;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.FunctionDeclarationFormTest;

// Full round trip for the CREATE FUNCTION declaration forms added by issue #213: RETURNS
// TABLE, a function whose result comes only from its OUT parameters, and SET configuration
// clauses on both a function and a procedure.
//
// The round trip is what this proves, and it is the only thing that can: each of these forms
// is stored by PostgreSQL as something other than what was written -- RETURNS TABLE becomes
// TABLE-mode arguments, an OUT-only function's return type is derived from its parameters,
// and a SET clause is folded into a canonical proconfig entry. A unit test can only assert
// what Squill believes; publishing and re-extracting is what shows the belief is right, and
// the empty-republish assertion is what catches a facet that round-trips to something
// almost-but-not-quite equal.
public class PostgresFunctionDeclarationFormTest : PostgresIntegrationTestBase
{
    private const string SourceResource =
        "Squill.IntegrationTests.Postgres.FunctionDeclarationFormTest.FunctionDeclarationForms.sql";

    [Fact]
    public async Task DeclarationFormRoundTrip_ModelHashesMatchAfterPublish()
    {
        var ct = TestContext.Current.CancellationToken;
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var workspace = new Workspace();
        workspace.Files.Add(new EmbeddedResourceFile(SourceResource, FileKind.Compile));

        var buildResult = await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(ct);

        // None of these declarations uses an unmodelable form, so a clean build is expected;
        // a warning here would mean a facet was being dropped that the round trip below
        // would then wrongly appear to confirm.
        Assert.Empty(buildResult.Warnings);

        var model = buildResult.Model;

        AssertRoutines(model);

        var testDb = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var emptyModel = await dbModelBuilder.ExtractModelAsync(ct);

            await testDb.PublishAsync(SchemaCompare.Compare(provider, model, emptyModel), ct);

            var publishedModel = await dbModelBuilder.ExtractModelAsync(ct);

            AssertRoutines(publishedModel);

            Assert.True(
                HashUtility.HashesEqual(model.Hash, publishedModel.Hash),
                "Parsed and extracted model hashes do not match");

            // Redeploying the same source must be a no-op. This is the assertion that fails
            // if a SET clause is re-emitted in a spelling PostgreSQL stores differently, or
            // if a RETURNS TABLE column comes back as anything but a TABLE-mode argument.
            Assert.Empty(SchemaCompare.Compare(provider, model, publishedModel).Deltas);

            await AssertRoutinesBehaveAsync(testDb, ct);
        }
        finally
        {
            await testDb.DropAsync(ct);
        }
    }

    private static void AssertRoutines(Model model)
    {
        var functions = model.Elements
            .Where(i => i.Type == PostgresElementTypes.SqlFunction)
            .ToList();

        Assert.Equal(6, functions.Count);

        // A RETURNS TABLE function's identity signature holds only its IN parameters, and
        // its columns arrive as TABLE-mode arguments.
        var summary = Assert.Single(functions, i => i.Name as string == "public.order_summary(text)");

        Assert.Equal(
            "IN p_customer text, TABLE id integer, TABLE total numeric",
            summary.GetProperty<string>(PostgresPropertyNames.Arguments));
        Assert.Equal("record", summary.GetProperty<string>(PostgresPropertyNames.ReturnType));
        Assert.True(summary.GetProperty<bool?>(PostgresPropertyNames.ReturnsSet));

        // A one-column RETURNS TABLE returns that column's own type, not `record`.
        var ids = Assert.Single(functions, i => i.Name as string == "public.order_ids()");

        Assert.Equal("integer", ids.GetProperty<string>(PostgresPropertyNames.ReturnType));
        Assert.True(ids.GetProperty<bool?>(PostgresPropertyNames.ReturnsSet));

        // One OUT parameter and no RETURNS: the result type is that parameter's.
        var count = Assert.Single(functions, i => i.Name as string == "public.order_count()");

        Assert.Equal("bigint", count.GetProperty<string>(PostgresPropertyNames.ReturnType));
        Assert.Equal("OUT total bigint", count.GetProperty<string>(PostgresPropertyNames.Arguments));

        // Two OUT parameters instead give `record`.
        var totals = Assert.Single(functions, i => i.Name as string == "public.order_totals()");

        Assert.Equal("record", totals.GetProperty<string>(PostgresPropertyNames.ReturnType));

        // The SET clause is stored exactly as proconfig holds it.
        var hardened = Assert.Single(functions, i => i.Name as string == "public.hardened_total()");

        Assert.Equal(
            "search_path=pg_catalog, pg_temp",
            hardened.GetProperty<string>(PostgresPropertyNames.Configuration));
        Assert.True(hardened.GetProperty<bool?>(PostgresPropertyNames.IsSecurityDefiner));

        // Two SET clauses stay in declaration order, joined the way proconfig is flattened.
        var tuned = Assert.Single(functions, i => i.Name as string == "public.tuned_count()");

        Assert.Equal(
            string.Join(
                PostgresModelFactory.RoutineConfigurationSeparator,
                "enable_seqscan=off",
                "work_mem=32MB"),
            tuned.GetProperty<string>(PostgresPropertyNames.Configuration));

        // A procedure stores proconfig too.
        var procedure = Assert.Single(
            model.Elements,
            i => i.Type == PostgresElementTypes.SqlProcedure);

        Assert.Equal(
            "search_path=pg_catalog, pg_temp",
            procedure.GetProperty<string>(PostgresPropertyNames.Configuration));
    }

    // Proves the emitted DDL is not merely accepted but behaves: a RETURNS TABLE function
    // yields its declared columns, an OUT-only function returns its derived scalar, and the
    // hardened function still resolves `public.orders` through its restricted search_path.
    private static async Task AssertRoutinesBehaveAsync(IDatabase database, CancellationToken ct)
    {
        await database.ConnectAsync(ct);

        await database.RunScriptAsync(
            """
            INSERT INTO orders (id, customer, total)
            VALUES (1, 'ada', 10.00), (2, 'ada', 20.00), (3, 'grace', 5.00);
            """,
            cancellationToken: ct);

        await using (var reader = await database.RunScriptReaderAsync(
            "SELECT id, total FROM order_summary('ada') ORDER BY id;", cancellationToken: ct))
        {
            Assert.True(await reader.ReadAsync(ct), "order_summary returned no rows");
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal(10.00m, reader.GetDecimal(1));

            Assert.True(await reader.ReadAsync(ct), "order_summary returned only one row");
            Assert.Equal(2, reader.GetInt32(0));
        }

        await using (var reader = await database.RunScriptReaderAsync(
            "SELECT order_count();", cancellationToken: ct))
        {
            Assert.True(await reader.ReadAsync(ct), "order_count returned no rows");
            Assert.Equal(3L, reader.GetInt64(0));
        }

        await using (var reader = await database.RunScriptReaderAsync(
            "SELECT hardened_total();", cancellationToken: ct))
        {
            Assert.True(await reader.ReadAsync(ct), "hardened_total returned no rows");
            Assert.Equal(35.00m, reader.GetDecimal(0));
        }
    }
}
