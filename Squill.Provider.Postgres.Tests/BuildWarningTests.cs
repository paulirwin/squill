using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Tests the non-fatal diagnostics channel (issue #61): constructs that are declared in the
/// source but not carried into the model do not fail the build, but are reported as SQ1002
/// warnings so the gap is visible rather than silent.
/// </summary>
public class BuildWarningTests
{
    private static ParserWorkspaceModelBuilder BuilderFor(params (string Name, string Sql)[] files)
    {
        var workspace = new Workspace();
        foreach (var (name, sql) in files)
        {
            workspace.Files.Add(new InMemoryStringFile(name, FileKind.Compile, sql));
        }

        return new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser());
    }

    [Fact]
    public async Task FunctionDefault_WarnsAndStillBuilds()
    {
        // An arbitrary function call is not allowlisted (issue #124) and stays unmodeled,
        // because Postgres may rewrite its stored form and it could not round-trip.
        const string sql = """
CREATE TABLE event
(
    id integer PRIMARY KEY,
    created_at integer DEFAULT some_custom_fn(1)
);
""";
        var builder = BuilderFor(("Event.sql", sql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        // The build succeeds — a dropped default is not fatal.
        Assert.Contains(result.Model.Elements, e => e.Type == PostgresElementTypes.SqlTable);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SQ1002", warning.Code);
        Assert.Equal("Event.sql", warning.SourceFile);
        Assert.Equal(4, warning.Line);
        Assert.Contains("created_at", warning.Message);
    }

    [Fact]
    public async Task MultipleFunctionDefaults_EachWarn()
    {
        const string sql = """
CREATE TABLE event
(
    id integer PRIMARY KEY,
    created_at integer DEFAULT some_custom_fn(1),
    updated_at integer DEFAULT other_custom_fn(2)
);
""";
        var builder = BuilderFor(("Event.sql", sql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Warnings.Count);
        Assert.All(result.Warnings, w => Assert.Equal("SQ1002", w.Code));
        Assert.Contains(result.Warnings, w => w.Message.Contains("created_at"));
        Assert.Contains(result.Warnings, w => w.Message.Contains("updated_at"));
    }

    [Fact]
    public async Task ConstantDefault_DoesNotWarn()
    {
        const string sql = """
CREATE TABLE event
(
    id integer PRIMARY KEY,
    count integer DEFAULT 0
);
""";
        var builder = BuilderFor(("Event.sql", sql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// CHECK constraints are modeled as of issue #120, so they no longer warn as an
    /// unmodeled construct — they are carried into the model and deployed.
    /// </summary>
    [Fact]
    public async Task CheckConstraint_DoesNotWarn()
    {
        const string sql = """
CREATE TABLE product
(
    id integer PRIMARY KEY,
    price integer,
    CONSTRAINT ck_price CHECK (price > 0)
);
""";
        var builder = BuilderFor(("Product.sql", sql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task CleanSource_ProducesNoWarnings()
    {
        var builder = BuilderFor(
            ("Author.sql", "CREATE TABLE author (id integer PRIMARY KEY, name varchar(50) NOT NULL);"),
            ("Book.sql", "CREATE TABLE book (id integer PRIMARY KEY, author_id integer REFERENCES author (id));"));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
    }

    // Issue #143: statement forms that now parse but are deliberately not carried into the
    // model. Each warns rather than throwing, so the rest of the project still builds — and
    // rather than deploying something that differs from what was declared.

    /// <summary>
    /// A typed table's columns belong to its composite type, which the model has no way to
    /// express, so the table is not emitted at all — only the warning.
    /// </summary>
    [Fact]
    public async Task TypedTable_WarnsAndIsNotModeled()
    {
        const string sql = """
CREATE TYPE employee_type AS (id integer, name text);
CREATE TABLE employees OF employee_type;
""";
        var builder = BuilderFor(("Employees.sql", sql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SQ1002", warning.Code);
        Assert.Equal("Employees.sql", warning.SourceFile);
        Assert.Contains("employees", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OF", warning.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(result.Model.Elements,
            e => e.Type == PostgresElementTypes.SqlTable && e.Name?.Contains("employees") == true);
    }

    [Fact]
    public async Task PartitionChild_WarnsAndIsNotModeled()
    {
        const string sql = """
CREATE TABLE measurement_y2024 PARTITION OF measurement
    FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');
""";
        var builder = BuilderFor(("Measurement.sql", sql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SQ1002", warning.Code);
        Assert.Contains("PARTITION OF", warning.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(result.Model.Elements,
            e => e.Type == PostgresElementTypes.SqlTable && e.Name?.Contains("measurement_y2024") == true);
    }

    /// <summary>
    /// A partitioned *parent* is different from the child: it declares its own columns, so it
    /// would model and deploy quite happily — as an ordinary, unpartitioned table. A warning
    /// is not enough for a divergence that large, so it is a build error until partitioning is
    /// modeled (issue #143).
    /// </summary>
    [Fact]
    public async Task PartitionedParent_IsABuildError()
    {
        const string sql =
            "CREATE TABLE measurement (logdate date NOT NULL, peaktemp integer) PARTITION BY RANGE (logdate);";
        var builder = BuilderFor(("Measurement.sql", sql));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Contains("PARTITION BY", ex.Message, StringComparison.Ordinal);
        Assert.Equal("Measurement.sql", ex.SourceFile);
    }

    /// <summary>
    /// A temporary table is the same failure mode as a partitioned parent (issue #204): it
    /// models and deploys perfectly happily, as a *permanent* table, which is not what the
    /// source declares. A temp table lives only as long as the session that created it, so it
    /// can never be part of a schema a deploy converges on. Postgres already rejects TEMP on
    /// a sequence and a view; this is the same policy applied to the table it was missing on.
    /// </summary>
    [Theory]
    [InlineData("TEMP")]
    [InlineData("TEMPORARY")]
    [InlineData("LOCAL TEMPORARY")]
    [InlineData("GLOBAL TEMPORARY")]
    public async Task TemporaryTable_IsABuildError(string modifier)
    {
        var builder = BuilderFor(("Scratch.sql", $"CREATE {modifier} TABLE scratch (id integer);"));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Contains("scratch", ex.Message, StringComparison.Ordinal);
        Assert.Contains("temporary", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Scratch.sql", ex.SourceFile);
    }

    /// <summary>
    /// UNLOGGED is rejected alongside TEMPORARY rather than modeled. It is not session-scoped,
    /// so it is a weaker case than TEMP, but the table's contents do not survive a crash and
    /// nothing in the model can express the distinction, so deploying it logged would again
    /// be different semantics than the source declares. Rejecting states the gap out loud; if
    /// UNLOGGED is later modeled as a table property, this becomes a build that succeeds.
    /// </summary>
    [Fact]
    public async Task UnloggedTable_IsABuildError()
    {
        var builder = BuilderFor(("Staging.sql", "CREATE UNLOGGED TABLE staging (id integer);"));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Contains("staging", ex.Message, StringComparison.Ordinal);
        Assert.Contains("UNLOGGED", ex.Message, StringComparison.Ordinal);
        Assert.Equal("Staging.sql", ex.SourceFile);
    }

    /// <summary>
    /// The error must point at the offending statement, not merely at the file: a
    /// NotImplementedException thrown from inside the visitor would surface with no position
    /// at all (see ProcessFile), which is why the rejection lives in the provider.
    /// </summary>
    [Fact]
    public async Task TemporaryTable_ErrorIsAnchoredToTheStatement()
    {
        const string sql = """
CREATE TABLE keeper (id integer PRIMARY KEY);

CREATE TEMPORARY TABLE scratch (id integer);
""";
        var builder = BuilderFor(("Mixed.sql", sql));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Mixed.sql", ex.SourceFile);
        Assert.Equal(3, ex.Line);
    }

    /// <summary>
    /// The rejection keys off the modifier, not the word "temp" appearing in a name: an
    /// ordinary table called <c>temp_readings</c> is a perfectly good permanent table.
    /// </summary>
    [Fact]
    public async Task OrdinaryTableNamedTemp_StillBuilds()
    {
        var builder = BuilderFor(("Readings.sql", "CREATE TABLE temp_readings (id integer PRIMARY KEY);"));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Contains(result.Model.Elements,
            e => e.Type == PostgresElementTypes.SqlTable && e.Name?.Contains("temp_readings") == true);
    }

    [Fact]
    public async Task PrimaryKeyUsingIndex_WarnsAndTheConstraintIsNotModeled()
    {
        const string sql = """
CREATE TABLE t
(
    id integer,
    CONSTRAINT pk_t PRIMARY KEY USING INDEX ix_t_id
);
""";
        var builder = BuilderFor(("T.sql", sql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SQ1002", warning.Code);
        Assert.Contains("USING INDEX", warning.Message, StringComparison.Ordinal);

        // The table itself is still modeled — only the constraint is dropped.
        Assert.Contains(result.Model.Elements, e => e.Type == PostgresElementTypes.SqlTable);
        Assert.DoesNotContain(result.Model.Elements, e => e.Type == PostgresElementTypes.SqlPrimaryKeyConstraint);
    }

    [Fact]
    public async Task UniqueUsingIndex_Warns()
    {
        const string sql = """
CREATE TABLE t
(
    email text,
    UNIQUE USING INDEX ix_t_email
);
""";
        var builder = BuilderFor(("T.sql", sql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SQ1002", warning.Code);
        Assert.Contains("USING INDEX", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The schema is modeled as usual; only the role is dropped, since Squill does not manage
    /// roles.
    /// </summary>
    [Fact]
    public async Task SchemaAuthorization_WarnsButTheSchemaIsStillModeled()
    {
        var builder = BuilderFor(("Staging.sql", "CREATE SCHEMA staging AUTHORIZATION joe;"));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SQ1002", warning.Code);
        Assert.Contains("AUTHORIZATION", warning.Message, StringComparison.Ordinal);

        Assert.Contains(result.Model.Elements, e => e.Type == PostgresElementTypes.SqlSchema);
    }

    /// <summary>
    /// A non-constant role behaves exactly as a named one when the schema is named (issue #166):
    /// the schema is modeled under its own stable name and only the ownership is dropped, with
    /// the token reported in the warning so it is clear what was not modeled.
    ///
    /// <para>
    /// This is what makes the named form safe where the name-less form is not. The element's
    /// name is <c>staging</c> whoever deploys it, so it matches the name extracted from the
    /// target and the schema neither re-creates nor — under <c>DropObjectsNotInSource</c> —
    /// gets dropped as undeclared.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("CURRENT_USER")]
    [InlineData("SESSION_USER")]
    public async Task SchemaAuthorization_NonConstantRole_WarnsButTheSchemaIsStillModeled(
        string role)
    {
        var builder = BuilderFor(
            ("Staging.sql", $"CREATE SCHEMA staging AUTHORIZATION {role};"));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SQ1002", warning.Code);
        Assert.Contains(role, warning.Message, StringComparison.Ordinal);

        var schema = Assert.Single(
            result.Model.Elements, e => e.Type == PostgresElementTypes.SqlSchema);

        // The stable name, not the role token — this is the whole point of the named form.
        Assert.Equal("staging", schema.Name?.ToString());

        // And the role really is dropped rather than carried into the model, which is what
        // makes the warning honest. Asserted as hash equality against the same schema declared
        // with no AUTHORIZATION at all: that covers properties, relationships and annotations
        // together, where inspecting one collection would pass vacuously if the role leaked
        // through another (or if the collection is simply always empty).
        var plain = await BuilderFor(("Plain.sql", "CREATE SCHEMA staging;"))
            .ExtractModelAsync(TestContext.Current.CancellationToken);

        var plainSchema = Assert.Single(
            plain.Model.Elements, e => e.Type == PostgresElementTypes.SqlSchema);

        Assert.True(
            HashUtility.HashesEqual(schema.Hash, plainSchema.Hash),
            $"AUTHORIZATION {role} left a trace on the schema element: it does not hash-match "
            + "the same schema declared without an AUTHORIZATION clause.");
    }

    /// <summary>
    /// A quoted role is a role <em>name</em> even when it spells a keyword — confirmed on the
    /// server, where <c>AUTHORIZATION "current_user"</c> gives ownership to a role of that name
    /// rather than to the deploying one. The warning reports it as written, quotes included, so
    /// it is unambiguous which of the two the source meant.
    /// </summary>
    [Fact]
    public async Task SchemaAuthorization_QuotedRoleSpellingAKeyword_IsReportedAsWritten()
    {
        var builder = BuilderFor(
            ("Staging.sql", "CREATE SCHEMA staging AUTHORIZATION \"current_user\";"));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SQ1002", warning.Code);
        Assert.Contains("\"current_user\"", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// CASCADE is honored on deploy rather than dropped (issue #143), so it does not warn.
    /// Dropping it would build cleanly and then fail on deploy, because the dependency it
    /// exists to install would be missing.
    /// </summary>
    [Fact]
    public async Task ExtensionCascade_DoesNotWarnAndIsCarried()
    {
        var builder = BuilderFor(("Ext.sql", "CREATE EXTENSION earthdistance CASCADE;"));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);

        var extension = Assert.Single(result.Model.Elements,
            e => e.Type == PostgresElementTypes.SqlExtension);

        Assert.True(extension.GetProperty<bool?>(PostgresPropertyNames.Cascade));
    }

    /// <summary>
    /// CASCADE describes how to deploy, not what was deployed — the catalog records no trace
    /// of it — so it must not contribute to the element's hash. If it did, a source model with
    /// CASCADE would never match the same extension extracted from a database, and the
    /// extension would be redeployed forever.
    /// </summary>
    [Fact]
    public async Task ExtensionCascade_DoesNotAffectTheElementHash()
    {
        var withCascade = await BuilderFor(("Ext.sql", "CREATE EXTENSION cube CASCADE;"))
            .ExtractModelAsync(TestContext.Current.CancellationToken);
        var without = await BuilderFor(("Ext.sql", "CREATE EXTENSION cube;"))
            .ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.True(HashUtility.HashesEqual(withCascade.Model.Hash, without.Model.Hash));
    }

    [Fact]
    public async Task ExtensionFromVersion_Warns()
    {
        var builder = BuilderFor(("Ext.sql", "CREATE EXTENSION hstore FROM unpackaged;"));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SQ1002", warning.Code);
        Assert.Contains("FROM", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>An ordinary extension must not warn.</summary>
    [Fact]
    public async Task PlainExtension_DoesNotWarn()
    {
        var builder = BuilderFor(("Ext.sql", "CREATE EXTENSION citext WITH SCHEMA public VERSION '1.6';"));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
    }
}
