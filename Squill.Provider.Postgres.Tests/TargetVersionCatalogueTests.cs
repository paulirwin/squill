using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Extends the target-version catalogue beyond the single entry #185 shipped (issue #191).
///
/// <para>
/// Both boundaries here were measured against pinned servers rather than taken from the release
/// notes, per CLAUDE.md's "measured against a live server, never inferred" rule. Non-decimal
/// integer literals are rejected by 14 and 15 and accepted by 16; a negative numeric scale is
/// rejected by 14 and accepted by 15. The release notes alone would not have distinguished
/// "introduced in 16" from "rejected by 15", and for the literals those are different claims —
/// PostgreSQL 14 does not even fail on <c>SELECT 0x1f</c>, it silently returns <c>0</c>, having
/// lexed the literal as <c>0</c> with an alias.
/// </para>
/// </summary>
public class TargetVersionCatalogueTests
{
    private static ParserWorkspaceModelBuilder BuilderFor(
        PostgresqlDatabaseSchemaProvider schemaProvider, params (string Name, string Sql)[] files)
    {
        var workspace = new Workspace();
        foreach (var (name, sql) in files)
        {
            workspace.Files.Add(new InMemoryStringFile(name, FileKind.Compile, sql));
        }

        return new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser(), schemaProvider);
    }

    private const string HexDefaultSql = """
CREATE TABLE flags
(
    id integer PRIMARY KEY,
    mask integer DEFAULT 0x19
);
""";

    [Theory]
    [InlineData("0x19")]
    [InlineData("0o17")]
    [InlineData("0b101")]
    public async Task NonDecimalLiteral_OnOlderTarget_Warns(string literal)
    {
        var sql = $"CREATE TABLE flags (id integer PRIMARY KEY, mask integer DEFAULT {literal});";

        var builder = BuilderFor(
            new Postgresql15DatabaseSchemaProvider(), ("Flags.sql", sql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(SqlSourceDiagnostic.FeatureNotInTargetVersion, warning.Code);
        Assert.Equal("Flags.sql", warning.SourceFile);

        // The literal as written is quoted back, so the author can find it in the file.
        Assert.Contains(literal, warning.Message);

        // Both the version that introduced it and the version being targeted, since the fix is
        // to pick one of them.
        Assert.Contains("16", warning.Message);
        Assert.Contains("15", warning.Message);
    }

    [Fact]
    public async Task NonDecimalLiteral_OnIntroducingTarget_DoesNotWarn()
    {
        // 16 introduced them, and the comparison is >=, so 16 itself must be silent.
        var builder = BuilderFor(
            new Postgresql16DatabaseSchemaProvider(), ("Flags.sql", HexDefaultSql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// The warning does not change what is built. What is built is the DECIMAL value, because
    /// that is what PostgreSQL stores: measured on 16, <c>DEFAULT 0x19</c> reads back out of
    /// <c>information_schema.columns.column_default</c> as <c>25</c>, and <c>CHECK (m &lt; 0x19)</c>
    /// reads back as <c>CHECK ((m &lt; 25))</c>. The engine normalizes the radix away, so
    /// modeling the source spelling would make every deploy see a phantom change.
    ///
    /// <para>
    /// This is the opposite of the <c>now()</c> / <c>CURRENT_TIMESTAMP</c> case, where Postgres
    /// preserves the spelling it was given and each spelling therefore gets its own token. The
    /// rule is the same in both — model whatever survives the round trip — and only the engine's
    /// behaviour differs.
    /// </para>
    /// </summary>
    [Fact]
    public async Task NonDecimalLiteral_IsModeledAsItsDecimalValue()
    {
        var builder = BuilderFor(
            new Postgresql15DatabaseSchemaProvider(), ("Flags.sql", HexDefaultSql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var table = Assert.Single(
            result.Model.Elements, e => e.Type == PostgresElementTypes.SqlTable);

        var column = table.Relationships
            .Single(r => r.Name == PostgresRelationshipNames.Columns)
            .Entries.OfType<Element>()
            .Single(c => SqlName.UnqualifiedOf((string)c.Name!) == "mask");

        Assert.Equal("25", column.GetProperty<string>(PostgresPropertyNames.DefaultValue));
    }

    [Fact]
    public async Task DecimalLiteral_OnOldestTarget_DoesNotWarn()
    {
        // The check keys on the spelling, not the value: the same number written in decimal is
        // ordinary SQL on every supported version.
        const string sql =
            "CREATE TABLE flags (id integer PRIMARY KEY, mask integer DEFAULT 25);";

        var builder = BuilderFor(new Postgresql14DatabaseSchemaProvider(), ("Flags.sql", sql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// A string constant that merely contains "0x" is not a non-decimal literal. This is the
    /// case a text scan over the statement would get wrong, and the reason the check reads the
    /// radix the parser recorded from the token type instead.
    /// </summary>
    [Fact]
    public async Task StringContainingHexPrefix_DoesNotWarn()
    {
        const string sql =
            "CREATE TABLE flags (id integer PRIMARY KEY, label text DEFAULT '0x19');";

        var builder = BuilderFor(new Postgresql14DatabaseSchemaProvider(), ("Flags.sql", sql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// A non-decimal literal in a CHECK predicate is the same gate as one in a DEFAULT: the
    /// construct is the literal, not the clause it sits in.
    /// </summary>
    [Fact]
    public async Task NonDecimalLiteral_InCheckConstraint_Warns()
    {
        const string sql =
            "CREATE TABLE flags (id integer PRIMARY KEY, mask integer CHECK (mask < 0x19));";

        var builder = BuilderFor(new Postgresql15DatabaseSchemaProvider(), ("Flags.sql", sql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(SqlSourceDiagnostic.FeatureNotInTargetVersion, warning.Code);
        Assert.Contains("0x19", warning.Message);
    }

    /// <summary>
    /// A negative numeric scale (<c>numeric(4, -2)</c>) arrived in PostgreSQL 15 — measured:
    /// 14 rejects it with "NUMERIC scale -2 must be between 0 and precision 4", 15 accepts it.
    /// It is nonetheless NOT a version-catalogue entry, because it cannot round-trip at all.
    ///
    /// <para>
    /// Measured on 16: <c>numeric(4,-2)</c> reads back out of
    /// <c>information_schema.columns.numeric_scale</c> as <b>2046</b>, not <c>-2</c> — the view
    /// exposes an unsigned reading of the typmod. <c>PostgresDatabaseModelBuilder</c> reads that
    /// column, so a modeled <c>-2</c> would compare unequal to the <c>2046</c> extracted from the
    /// database and the column would re-diff on every single deploy.
    /// </para>
    ///
    /// <para>
    /// So this follows the rule CLAUDE.md sets for defaults: anything that cannot make the round
    /// trip stays unmodeled rather than being modeled wrongly. It is rejected outright rather
    /// than warned about, because unlike an unmodeled DEFAULT the scale is not droppable —
    /// deploying <c>numeric(4)</c> in place of <c>numeric(4,-2)</c> would silently store
    /// different numbers than the source asked for.
    /// </para>
    /// </summary>
    [Fact]
    public async Task NegativeNumericScale_IsRejectedWithASourceAnchoredError()
    {
        const string sql =
            "CREATE TABLE readings (id integer PRIMARY KEY, rounded numeric(4, -2));";

        var builder = BuilderFor(new Postgresql16DatabaseSchemaProvider(), ("Readings.sql", sql));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        // Anchored at the source, not a bare InvalidOperationException with a stack trace: the
        // author needs to be told which column in which file, like every other build error.
        Assert.Equal("Readings.sql", ex.SourceFile);
        Assert.Contains("rounded", ex.Message);
        Assert.Contains("scale", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A column is not the only way a <c>numeric(4, -2)</c> can reach the model, so the rejection
    /// is raised in two places: at the column, where the name is known and can be quoted, and in
    /// the shared type-specifier builder that domains and composite-type attributes also go
    /// through. Without the second, either of these would model a scale that cannot round-trip.
    /// </summary>
    [Theory]
    [InlineData("CREATE DOMAIN rounded_amount AS numeric(4, -2);")]
    [InlineData("CREATE TYPE reading AS (rounded numeric(4, -2));")]
    public async Task NegativeNumericScale_IsRejectedOutsideOfColumnsToo(string sql)
    {
        var builder = BuilderFor(new Postgresql16DatabaseSchemaProvider(), ("Types.sql", sql));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Types.sql", ex.SourceFile);
        Assert.Contains("scale", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonNegativeNumericScale_IsUnaffected()
    {
        const string sql =
            "CREATE TABLE readings (id integer PRIMARY KEY, amount numeric(8, 2));";

        var builder = BuilderFor(new Postgresql16DatabaseSchemaProvider(), ("Readings.sql", sql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);

        var table = Assert.Single(
            result.Model.Elements, e => e.Type == PostgresElementTypes.SqlTable);

        var column = table.Relationships
            .Single(r => r.Name == PostgresRelationshipNames.Columns)
            .Entries.OfType<Element>()
            .Single(c => SqlName.UnqualifiedOf((string)c.Name!) == "amount");

        var typeSpec = Assert.Single(
            column.Relationships.Single(r => r.Name == PostgresRelationshipNames.TypeSpecifier)
                .Entries.OfType<Element>());

        Assert.Equal(2L, typeSpec.GetProperty<long>(PostgresPropertyNames.Scale));
    }

    /// <summary>
    /// A generated column's expression, which is the case where the warning does the most work.
    /// A DEFAULT is canonicalized to the value PostgreSQL stores (<c>0x19</c> becomes <c>25</c>)
    /// on its way into the model, so what is deployed is portable whatever the source said. A
    /// generation expression is rendered back out by <c>ExpressionSqlRenderer</c>, which emits the
    /// literal's source spelling — so the <c>CREATE TABLE</c> Squill scripts contains <c>0x19</c>
    /// verbatim, and against a PostgreSQL 15 server that statement fails.
    ///
    /// <para>
    /// The round trip is unaffected either way: measured on 16, the stored
    /// <c>generation_expression</c> comes back as <c>(m + 25)</c>, so the engine normalizes the
    /// radix away exactly as it does for a DEFAULT. It is the CREATE that breaks, not the compare.
    /// </para>
    /// </summary>
    [Fact]
    public async Task NonDecimalLiteral_InGeneratedColumn_Warns()
    {
        const string sql = """
CREATE TABLE flags
(
    id integer PRIMARY KEY,
    mask integer,
    scaled integer GENERATED ALWAYS AS (mask + 0x19) STORED
);
""";

        var builder = BuilderFor(new Postgresql15DatabaseSchemaProvider(), ("Flags.sql", sql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(SqlSourceDiagnostic.FeatureNotInTargetVersion, warning.Code);
        Assert.Contains("0x19", warning.Message);
        Assert.Contains("scaled", warning.Message);
    }

    /// <summary>
    /// A non-decimal literal in an index predicate, which is the third place an arbitrary
    /// expression reaches the model.
    /// </summary>
    [Fact]
    public async Task NonDecimalLiteral_InIndexPredicate_Warns()
    {
        const string sql = """
CREATE TABLE flags (id integer PRIMARY KEY, mask integer);

CREATE INDEX ix_flags_mask ON flags (mask) WHERE mask > 0x19;
""";

        var builder = BuilderFor(new Postgresql15DatabaseSchemaProvider(), ("Flags.sql", sql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(SqlSourceDiagnostic.FeatureNotInTargetVersion, warning.Code);
        Assert.Contains("0x19", warning.Message);
    }
}
