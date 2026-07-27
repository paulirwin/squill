using System.Reflection;
using Squill.Core;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Tests modeling of non-constant column <c>DEFAULT</c>s (issue #124) — the
/// <c>CURRENT_TIMESTAMP</c> defaults on Sakila's ubiquitous <c>last_update</c> columns.
///
/// Unlike Postgres, which preserves the spelling it was given, both engines collapse every
/// synonym (<c>CURRENT_TIMESTAMP</c>, <c>NOW()</c>, <c>current_timestamp</c>) into a single
/// stored default — but spell it differently: MySQL reports <c>CURRENT_TIMESTAMP</c> while
/// MariaDB reports <c>current_timestamp()</c>. Since one provider serves both engines and the
/// model hash must match either way, every one of those spellings folds to one canonical
/// token.
/// </summary>
public class MariaDbFunctionDefaultTests
{
    private static readonly Type DefaultValueType =
        typeof(MariaDbElementTypes).Assembly
            .GetType("Squill.Provider.MariaDb.MariaDbDefaultValue", throwOnError: true)!;

    private static string? FromDatabaseText(string? text, bool isCharacterColumn = false)
        => (string?)DefaultValueType
            .GetMethod("FromDatabaseText", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [text, isCharacterColumn]);

    private static async Task<Element> ColumnOfAsync(string columnSql)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile,
            $"CREATE TABLE t (id int PRIMARY KEY, c {columnSql});"));

        var result = await new ParserWorkspaceModelBuilder(workspace, new AntlrMariaDbParser())
            .ExtractModelAsync(TestContext.Current.CancellationToken);

        var table = Assert.Single(result.Model.Elements, e => e.Type == MariaDbElementTypes.SqlTable);

        return table.Relationships
            .Single(r => r.Name == MariaDbRelationshipNames.Columns)
            .Entries.OfType<Element>()
            .Single(c => SqlName.UnqualifiedOf((string)c.Name!) == "c");
    }

    private static async Task<string?> DefaultOfAsync(string columnSql)
        => (await ColumnOfAsync(columnSql)).GetProperty<string>(MariaDbPropertyNames.DefaultValue);

    private static async Task<bool?> OnUpdateOfAsync(string columnSql)
        => (await ColumnOfAsync(columnSql))
            .GetProperty<bool?>(MariaDbPropertyNames.OnUpdateCurrentTimestamp);

    [Theory]
    // Every synonym the source may be written with folds to one canonical token, because the
    // engines themselves do not preserve which one was written.
    [InlineData("timestamp DEFAULT CURRENT_TIMESTAMP")]
    [InlineData("timestamp DEFAULT current_timestamp")]
    [InlineData("datetime DEFAULT NOW()")]
    [InlineData("datetime DEFAULT now()")]
    public async Task CurrentTimestampDefault_IsModeledWithCanonicalToken(string columnSql)
    {
        Assert.Equal("CURRENT_TIMESTAMP", await DefaultOfAsync(columnSql));
    }

    [Theory]
    // MySQL's reported spelling.
    [InlineData("CURRENT_TIMESTAMP")]
    // MariaDB's reported spelling for the very same default.
    [InlineData("current_timestamp()")]
    public void DatabaseText_FromEitherEngine_CanonicalizesIdentically(string stored)
    {
        Assert.Equal("CURRENT_TIMESTAMP", FromDatabaseText(stored));
    }

    /// <summary>
    /// The parser side and both engines' extractor sides must agree, or a Sakila deploy would
    /// see a phantom column change on every run.
    /// </summary>
    [Fact]
    public async Task ParsedAndBothEngineForms_AgreeOnOneToken()
    {
        var parsed = await DefaultOfAsync("timestamp DEFAULT CURRENT_TIMESTAMP");

        Assert.Equal(parsed, FromDatabaseText("CURRENT_TIMESTAMP"));
        Assert.Equal(parsed, FromDatabaseText("current_timestamp()"));
    }

    [Fact]
    public async Task CurrentTimestampDefault_NoLongerWarns()
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Event.sql", FileKind.Compile, """
CREATE TABLE event
(
    id int PRIMARY KEY,
    last_update timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP
);
"""));

        var result = await new ParserWorkspaceModelBuilder(workspace, new AntlrMariaDbParser())
            .ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task ConstantDefault_StillModeled()
    {
        Assert.Equal("0", await DefaultOfAsync("int DEFAULT 0"));
        Assert.Equal("'active'", await DefaultOfAsync("varchar(20) DEFAULT 'active'"));
    }

    /// <summary>
    /// A boolean default was previously unmodeled on this provider, so Sakila's
    /// <c>active boolean NOT NULL DEFAULT true</c> warned even though it is a plain constant.
    /// <c>boolean</c> is an alias for <c>tinyint(1)</c> on both engines and the default comes
    /// back as <c>1</c> / <c>0</c>, so it canonicalizes to the number rather than the keyword.
    /// </summary>
    [Theory]
    [InlineData("boolean DEFAULT true", "1")]
    [InlineData("boolean DEFAULT TRUE", "1")]
    [InlineData("boolean DEFAULT false", "0")]
    [InlineData("boolean DEFAULT FALSE", "0")]
    public async Task BooleanDefault_IsModeledAsTheStoredNumber(string columnSql, string expected)
    {
        Assert.Equal(expected, await DefaultOfAsync(columnSql));
    }

    /// <summary>
    /// <c>ON UPDATE CURRENT_TIMESTAMP</c> sits in the same grammar production as the default
    /// itself (<c>currentTimestamp (ON UPDATE currentTimestamp)?</c>). Taking that whole rule's
    /// text ran the two together into <c>CURRENT_TIMESTAMPONUPDATECURRENT_TIMESTAMP</c>, so
    /// even the plain <c>DEFAULT</c> half went unmodeled on every Sakila <c>last_update</c>
    /// column. The two parts are now read separately.
    /// </summary>
    [Fact]
    public async Task DefaultWithOnUpdate_ModelsBothParts()
    {
        Assert.Equal("CURRENT_TIMESTAMP",
            await DefaultOfAsync("timestamp DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP"));

        Assert.True(await OnUpdateOfAsync(
            "timestamp DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP"));
    }

    [Fact]
    public async Task DefaultWithoutOnUpdate_DoesNotSetTheFlag()
    {
        Assert.Null(await OnUpdateOfAsync("timestamp DEFAULT CURRENT_TIMESTAMP"));
    }

    [Fact]
    public async Task DefaultWithOnUpdate_NoLongerWarns()
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Actor.sql", FileKind.Compile, """
CREATE TABLE actor
(
    actor_id    int PRIMARY KEY,
    last_update timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);
"""));

        var result = await new ParserWorkspaceModelBuilder(workspace, new AntlrMariaDbParser())
            .ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// A fractional-seconds default (<c>CURRENT_TIMESTAMP(3)</c>) is deliberately left
    /// unmodeled: the two engines report it with different spellings and it is not needed by
    /// the sample schemas, so it keeps warning rather than risking a phantom diff.
    /// </summary>
    [Fact]
    public void FractionalPrecisionDefault_IsNotModeled()
    {
        Assert.Null(FromDatabaseText("current_timestamp(3)"));
        Assert.Null(FromDatabaseText("CURRENT_TIMESTAMP(3)"));
    }
}
