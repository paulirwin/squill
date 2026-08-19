using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// How the build treats the <c>CREATE TABLE</c> storage clauses that used to be parsed and then
/// silently dropped (issue #206). Each decision below was measured against a live PostgreSQL 18
/// rather than read off the grammar, as CLAUDE.md requires:
///
/// <list type="bullet">
/// <item><c>TABLESPACE</c> and <c>USING &lt;method&gt;</c> are accepted when they name the
/// default and rejected otherwise. Measured: a table in <c>pg_default</c> records
/// <c>reltablespace = 0</c> exactly as one with no clause does, and a table declared
/// <c>USING heap</c> records the same <c>relam</c> as one with no clause, so the default spelling
/// is a genuine no-op that can be dropped without losing anything. Any other value is a real
/// placement decision the model cannot carry, and deploying <c>USING columnar</c> as a heap table
/// is precisely the silent behavioural swap the issue was filed over.</item>
/// <item><c>WITH (...)</c> storage parameters warn SQ1002. Unlike the two above these genuinely
/// persist -- <c>fillfactor = 70</c> comes back in <c>pg_class.reloptions</c> -- but nothing
/// extracts a table's <c>reloptions</c> yet, so modeling them would re-diff on every deploy.
/// Warned rather than rejected because, also measured, every one of them is a performance knob:
/// a table that ignores its <c>fillfactor</c> still holds the same rows with the same
/// constraints, which is not true of a tablespace or an access method.</item>
/// </list>
///
/// <para>
/// The <c>TABLESPACE</c> rule deliberately matches the one <c>CREATE INDEX</c> has enforced since
/// issue #160. The inconsistency the issue calls out -- the index spelling throwing while the
/// table spelling silently succeeded -- is resolved by moving the table to the index's rule, not
/// the other way round.
/// </para>
/// </summary>
public class PostgresTableStorageClauseTests
{
    private static ParserWorkspaceModelBuilder BuilderFor(string sql)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        return new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser());
    }

    private static Task<BuildResult> BuildAsync(string sql)
        => BuilderFor(sql).ExtractModelAsync(TestContext.Current.CancellationToken);

    /// <summary>
    /// A rejection surfaces as the source-anchored <see cref="SqlSourceException"/> the builder
    /// wraps every <see cref="NotSupportedException"/> in, so the build error points at the
    /// declaration rather than at Squill.
    /// </summary>
    private static async Task<SqlSourceException> RejectedAsync(string sql)
    {
        var exception = await Assert.ThrowsAsync<SqlSourceException>(() => BuildAsync(sql));

        Assert.IsType<NotSupportedException>(exception.InnerException);

        return exception;
    }

    [Fact]
    public async Task Tablespace_Default_IsAcceptedWithoutWarning()
    {
        var result = await BuildAsync("CREATE TABLE t (id integer) TABLESPACE pg_default;");

        Assert.Contains(result.Model.Elements, e => e.Type == PostgresElementTypes.SqlTable);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Tablespace_NonDefault_IsRejected()
    {
        var exception = await RejectedAsync("CREATE TABLE t (id integer) TABLESPACE fast_ssd;");

        Assert.Contains("fast_ssd", exception.Message);
        Assert.Contains("Table 't'", exception.Message);
    }

    /// <summary>
    /// The point of the issue's inconsistency note: the same tablespace that a CREATE INDEX has
    /// always refused must not be quietly accepted just because it was written on a table.
    /// </summary>
    [Fact]
    public async Task Tablespace_NonDefault_IsRejectedForATableJustAsItIsForAnIndex()
    {
        var onTable = await RejectedAsync("CREATE TABLE t (id integer) TABLESPACE fast_ssd;");
        var onIndex = await RejectedAsync(
            "CREATE TABLE t (id integer);\nCREATE INDEX ix ON t (id) TABLESPACE fast_ssd;");

        Assert.Contains("pg_default", onTable.Message);
        Assert.Contains("pg_default", onIndex.Message);
    }

    [Fact]
    public async Task AccessMethod_Heap_IsAcceptedWithoutWarning()
    {
        var result = await BuildAsync("CREATE TABLE t (id integer) USING heap;");

        Assert.Contains(result.Model.Elements, e => e.Type == PostgresElementTypes.SqlTable);
        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// The clause the issue calls the most consequential. A warning would not be enough: the
    /// table would still deploy, as a heap table, which is a different storage engine from the
    /// one declared.
    /// </summary>
    [Fact]
    public async Task AccessMethod_NonDefault_IsRejected()
    {
        var exception = await RejectedAsync("CREATE TABLE t (id integer) USING columnar;");

        Assert.Contains("columnar", exception.Message);
    }

    [Fact]
    public async Task AccessMethod_IsCaseInsensitive()
    {
        // Postgres folds an unquoted identifier to lower case, so HEAP names the default method
        // and must be accepted on the same terms as heap.
        var result = await BuildAsync("CREATE TABLE t (id integer) USING HEAP;");

        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// A quoted identifier is case-sensitive in PostgreSQL, so <c>"HEAP"</c> does not name the
    /// default access method the way unquoted <c>HEAP</c> does. Measured on PostgreSQL 18.4,
    /// <c>CREATE TABLE t (id int) USING "HEAP"</c> fails with <c>access method "HEAP" does not
    /// exist</c> — so folding its case here would let a build accept a statement no server will
    /// ever run, moving the failure from the build to the deploy.
    /// </summary>
    [Fact]
    public async Task AccessMethod_QuotedNonDefaultCase_IsRejected()
    {
        var exception = await RejectedAsync("""CREATE TABLE t (id integer) USING "HEAP";""");

        Assert.Contains("HEAP", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of the same rule: quoting the default in its own case still names the
    /// default. Measured, a table declared <c>USING "heap"</c> records the same <c>relam</c> as
    /// one with no clause, so it stays a no-op.
    /// </summary>
    [Fact]
    public async Task AccessMethod_QuotedDefault_IsAccepted()
    {
        var result = await BuildAsync("""CREATE TABLE t (id integer) USING "heap";""");

        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// The tablespace side of the same case-sensitivity rule. Measured on PostgreSQL 18.4,
    /// <c>TABLESPACE "PG_DEFAULT"</c> fails with <c>tablespace "PG_DEFAULT" does not exist</c>.
    /// </summary>
    [Fact]
    public async Task Tablespace_QuotedNonDefaultCase_IsRejected()
    {
        var exception = await RejectedAsync("""CREATE TABLE t (id integer) TABLESPACE "PG_DEFAULT";""");

        Assert.Contains("PG_DEFAULT", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tablespace_QuotedDefault_IsAccepted()
    {
        var result = await BuildAsync("""CREATE TABLE t (id integer) TABLESPACE "pg_default";""");

        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// Unquoted identifiers are folded by the server, so the upper-case spelling of the
    /// tablespace default names the default and must keep building.
    /// </summary>
    [Fact]
    public async Task Tablespace_UnquotedUpperCase_IsAccepted()
    {
        var result = await BuildAsync("CREATE TABLE t (id integer) TABLESPACE PG_DEFAULT;");

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task StorageParameters_WarnAndTheTableStillBuilds()
    {
        var result = await BuildAsync("CREATE TABLE t (id integer) WITH (fillfactor = 70);");

        // The table is still modeled -- a dropped performance knob is not fatal.
        Assert.Contains(result.Model.Elements, e => e.Type == PostgresElementTypes.SqlTable);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SQ1002", warning.Code);
        Assert.Equal("Test.sql", warning.SourceFile);
        Assert.Contains("fillfactor", warning.Message);
        Assert.Contains("'t'", warning.Message);
    }

    [Fact]
    public async Task StorageParameters_EachOneIsNamedInTheWarning()
    {
        var result = await BuildAsync(
            "CREATE TABLE t (id integer) WITH (fillfactor = 70, autovacuum_enabled = false);");

        var warning = Assert.Single(result.Warnings);

        Assert.Contains("fillfactor", warning.Message);
        Assert.Contains("autovacuum_enabled", warning.Message);
    }

    /// <summary>
    /// A <c>toast.</c> parameter is the namespaced spelling, which the shared reloptions reader
    /// rejoins into one dotted name. Measured on PostgreSQL 18, it lands on the table's TOAST
    /// relation rather than the table's own reloptions -- unmodeled either way, so it warns.
    /// </summary>
    [Fact]
    public async Task StorageParameters_QualifiedToastParameter_Warns()
    {
        var result = await BuildAsync(
            "CREATE TABLE t (id integer, v text) WITH (toast.autovacuum_enabled = false);");

        var warning = Assert.Single(result.Warnings);

        Assert.Equal("SQ1002", warning.Code);
        Assert.Contains("toast.autovacuum_enabled", warning.Message);
    }

    /// <summary>
    /// <c>WITHOUT OIDS</c> is the other <c>optwith</c> alternative and declares no parameter at
    /// all. PostgreSQL 12 removed the OID columns it controlled, so the clause is accepted purely
    /// for compatibility and there is nothing to warn about -- warning would train the reader to
    /// ignore the diagnostic.
    /// </summary>
    [Fact]
    public async Task WithoutOids_DoesNotWarn()
    {
        var result = await BuildAsync("CREATE TABLE t (id integer) WITHOUT OIDS;");

        Assert.Contains(result.Model.Elements, e => e.Type == PostgresElementTypes.SqlTable);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task NoStorageClauses_DoNotWarn()
    {
        var result = await BuildAsync("CREATE TABLE t (id integer);");

        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// The accepted default spellings must not merely avoid warning, they must produce the same
    /// model as their absence -- otherwise the table would re-diff on every deploy against a
    /// database that reports the default.
    /// </summary>
    [Fact]
    public async Task DefaultSpellings_HashTheSameAsOmittingThem()
    {
        var omitted = await BuildAsync("CREATE TABLE t (id integer);");
        var spelled = await BuildAsync(
            "CREATE TABLE t (id integer) USING heap TABLESPACE pg_default;");

        Assert.True(HashUtility.HashesEqual(omitted.Model.Hash, spelled.Model.Hash));
    }
}
