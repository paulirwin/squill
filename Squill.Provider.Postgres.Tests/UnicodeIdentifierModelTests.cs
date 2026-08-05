using Squill.Core;
using Squill.PostgresParser;
using Squill.TestFramework;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// A unicode-quoted identifier carrying a doubled escape must reach the model — and the DDL
/// Squill generates — as the name PostgreSQL actually stores. Measured against postgres:latest:
/// <c>CREATE TABLE U&amp;"a\\b"</c> creates a table named <c>a\b</c>, three characters.
///
/// Parsing it correctly is not enough on its own: the value the parser produces has to be what
/// the extractor reads back from the catalog, or the object re-diffs on every deploy.
/// </summary>
public class UnicodeIdentifierModelTests
{
    private static Task<Model> ParseModelAsync(string sql)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrPostgresParser()),
            TestContext.Current.CancellationToken);

    /// <summary>
    /// The collapsed name is what lands in the model, so it matches what the catalog reports.
    /// </summary>
    [Fact]
    public async Task DoubledEscape_ModelsTheCollapsedName()
    {
        var model = await ParseModelAsync("""CREATE TABLE U&"a\\b" (id integer);""");

        var table = Assert.Single(
            model.Elements, i => i.Type == PostgresElementTypes.SqlTable);

        Assert.Equal("""a\b""", table.Name?.ToString());
    }

    /// <summary>
    /// The same name spelled three ways — unicode-quoted with a doubled escape, unicode-quoted
    /// with a redeclared escape, and plainly quoted — must all model identically. An extracted
    /// model only ever produces the plain spelling, so anything else re-diffs forever.
    /// </summary>
    [Fact]
    public async Task EverySpellingOfTheSameName_ModelsIdentically()
    {
        var doubledBackslash = await ParseModelAsync("""CREATE TABLE U&"a\\b" (id integer);""");
        var customEscape = await ParseModelAsync(
            """CREATE TABLE U&"a\b" UESCAPE '!' (id integer);""");
        var plainQuoted = await ParseModelAsync("""CREATE TABLE "a\b" (id integer);""");

        Assert.True(
            HashUtility.HashesEqual(doubledBackslash.Hash, plainQuoted.Hash),
            "A doubled escape must model as the single character the server stores.");

        Assert.True(
            HashUtility.HashesEqual(customEscape.Hash, plainQuoted.Hash),
            "With the escape redeclared, a backslash is an ordinary character.");
    }

    /// <summary>
    /// The generated DDL must name the table what the server would have named it, quoted so the
    /// backslash survives. Emitting the raw four-character body would create a different table.
    /// </summary>
    [Fact]
    public async Task DoubledEscape_ScriptsTheCollapsedName()
    {
        var model = await ParseModelAsync("""CREATE TABLE U&"a\\b" (id integer);""");

        var comparison = SchemaCompare.Compare(
            new PostgresDatabaseProvider("Host=unused"), model, new Model());

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("""CREATE TABLE "a\b" """.TrimEnd(), sql);
        Assert.DoesNotContain("""a\\b""", sql);
    }
}
