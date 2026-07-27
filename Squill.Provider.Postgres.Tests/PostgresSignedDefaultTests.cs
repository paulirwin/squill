using System.Reflection;
using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Modeling of a signed numeric column <c>DEFAULT</c> (issue #139) — <c>DEFAULT -5</c> and
/// <c>DEFAULT +5</c>. The parser previously threw on these outright, so the source form was
/// unreachable even though the database extractor already handled the stored <c>'-5'::integer</c>.
///
/// Both halves must land on the same canonical token, since the sign is written one way in
/// source and stored another way by Postgres: <c>-5</c> comes back as <c>'-5'::integer</c>,
/// but <c>+5</c> comes back as <c>(+ 5)</c>.
/// </summary>
public class PostgresSignedDefaultTests
{
    // PostgresDefaultValue is internal; reach it reflectively rather than widening its
    // accessibility just for a test.
    private static readonly MethodInfo FromDatabaseTextMethod =
        typeof(PostgresElementTypes).Assembly
            .GetType("Squill.Provider.Postgres.PostgresDefaultValue", throwOnError: true)!
            .GetMethod("FromDatabaseText", BindingFlags.Public | BindingFlags.Static)!;

    private static string? FromDatabaseText(string? text)
        => (string?)FromDatabaseTextMethod.Invoke(null, [text]);

    private static async Task<string?> DefaultOfAsync(string columnSql)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile,
            $"CREATE TABLE t (id integer PRIMARY KEY, c {columnSql});"));

        var builder = new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser());
        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var table = Assert.Single(result.Model.Elements, e => e.Type == PostgresElementTypes.SqlTable);

        var column = table.Relationships
            .Single(r => r.Name == PostgresRelationshipNames.Columns)
            .Entries.OfType<Element>()
            .Single(c => SqlName.UnqualifiedOf((string)c.Name!) == "c");

        return column.GetProperty<string>(PostgresPropertyNames.DefaultValue);
    }

    [Theory]
    [InlineData("integer DEFAULT -5", "-5")]
    [InlineData("numeric DEFAULT -1.5", "-1.5")]
    // A leading + is a no-op sign, so it canonicalizes to the bare number.
    [InlineData("integer DEFAULT +5", "5")]
    public async Task SignedDefault_FromSource_IsModeled(string columnSql, string expected)
    {
        Assert.Equal(expected, await DefaultOfAsync(columnSql));
    }

    [Theory]
    // Exactly as Postgres reports them, verified against a live server.
    [InlineData("'-5'::integer", "-5")]
    [InlineData("'-1.5'::numeric", "-1.5")]
    // DEFAULT +5 is stored in this parenthesized, space-separated form rather than as a cast.
    [InlineData("(+ 5)", "5")]
    [InlineData("(- 5)", "-5")]
    public void SignedDefault_FromDatabase_Canonicalizes(string stored, string expected)
    {
        Assert.Equal(expected, FromDatabaseText(stored));
    }

    [Fact]
    public async Task SignedDefault_DoesNotWarn()
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile,
            "CREATE TABLE t (id integer PRIMARY KEY, c integer DEFAULT -5);"));

        var builder = new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser());
        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
    }

    [Theory]
    // A sign applied to something that isn't a bare numeric literal cannot be trusted to
    // round-trip, so it stays unmodeled rather than being guessed at.
    [InlineData("(- (a + b))")]
    [InlineData("(+ now())")]
    public void SignedNonNumeric_FromDatabase_IsNotModeled(string stored)
    {
        Assert.Null(FromDatabaseText(stored));
    }
}
