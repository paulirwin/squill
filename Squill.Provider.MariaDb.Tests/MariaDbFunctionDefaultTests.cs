using System.Reflection;
using Squill.Core;
using Squill.MariaDbParser;
using Squill.MariaDbParser.Syntax;

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

    private static async Task<string?> OnUpdateOfAsync(string columnSql)
        => (await ColumnOfAsync(columnSql))
            .GetProperty<string>(MariaDbPropertyNames.OnUpdateCurrentTimestamp);

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

        Assert.Equal("CURRENT_TIMESTAMP", await OnUpdateOfAsync(
            "timestamp DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP"));
    }

    [Fact]
    public async Task DefaultWithoutOnUpdate_DoesNotSetTheFlag()
    {
        Assert.Null(await OnUpdateOfAsync("timestamp DEFAULT CURRENT_TIMESTAMP"));
    }

    /// <summary>
    /// The grammar's <c>ON UPDATE currentTimestamp</c> takes a whole <c>currentTimestamp</c>
    /// rule, so it can carry a fractional-seconds precision, and the canonical token carries it
    /// through (issue #144). Flattening it to a bare <c>ON UPDATE CURRENT_TIMESTAMP</c> would
    /// deploy something other than what the user wrote — and MySQL rejects that outright when it
    /// disagrees with the column's own precision.
    /// </summary>
    [Fact]
    public async Task OnUpdateWithPrecision_KeepsThePrecision()
    {
        Assert.Equal("CURRENT_TIMESTAMP(3)", await OnUpdateOfAsync(
            "datetime(3) DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3)"));
    }

    [Fact]
    public async Task PrecisionCarryingDefaultAndOnUpdate_NoLongerWarn()
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("T.sql", FileKind.Compile, """
CREATE TABLE t
(
    id int PRIMARY KEY,
    a  datetime(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3)
);
"""));

        var result = await new ParserWorkspaceModelBuilder(workspace, new AntlrMariaDbParser())
            .ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// The precision is preserved in the canonical token, and every synonym spelling carrying
    /// the same precision folds to it — <c>NOW(3)</c> is stored exactly as
    /// <c>CURRENT_TIMESTAMP(3)</c> is.
    /// </summary>
    [Theory]
    [InlineData("datetime(3) DEFAULT CURRENT_TIMESTAMP(3)", "CURRENT_TIMESTAMP(3)")]
    [InlineData("datetime(3) DEFAULT current_timestamp(3)", "CURRENT_TIMESTAMP(3)")]
    [InlineData("datetime(3) DEFAULT NOW(3)", "CURRENT_TIMESTAMP(3)")]
    [InlineData("datetime(6) DEFAULT CURRENT_TIMESTAMP(6)", "CURRENT_TIMESTAMP(6)")]
    [InlineData("datetime(1) DEFAULT CURRENT_TIMESTAMP(1)", "CURRENT_TIMESTAMP(1)")]
    public async Task PrecisionCarryingDefault_KeepsThePrecision(string columnSql, string expected)
    {
        Assert.Equal(expected, await DefaultOfAsync(columnSql));
    }

    /// <summary>
    /// Both engines' reported spellings of the same precision-carrying default must canonicalize
    /// to the one token the parser produces, or a deploy would see a phantom column change.
    /// Measured: MySQL reports <c>CURRENT_TIMESTAMP(3)</c>, MariaDB <c>current_timestamp(3)</c>.
    /// </summary>
    [Fact]
    public async Task PrecisionCarryingDatabaseText_FromEitherEngine_AgreesWithTheParser()
    {
        var parsed = await DefaultOfAsync("datetime(3) DEFAULT CURRENT_TIMESTAMP(3)");

        Assert.Equal("CURRENT_TIMESTAMP(3)", parsed);
        Assert.Equal(parsed, FromDatabaseText("CURRENT_TIMESTAMP(3)"));
        Assert.Equal(parsed, FromDatabaseText("current_timestamp(3)"));
    }

    /// <summary>
    /// Precision zero is not a distinct form: measured against both live engines, a
    /// <c>datetime(0)</c> column declaring <c>DEFAULT CURRENT_TIMESTAMP(0) ON UPDATE
    /// CURRENT_TIMESTAMP(0)</c> comes back reported exactly as the bare form does (and the
    /// column type itself loses its <c>(0)</c>). So <c>(0)</c> must fold to the bare token in
    /// both positions, or a source that spells it out would re-diff on every deploy.
    /// </summary>
    [Fact]
    public async Task PrecisionZero_FoldsToTheBareToken()
    {
        Assert.Equal("CURRENT_TIMESTAMP",
            await DefaultOfAsync("datetime(0) DEFAULT CURRENT_TIMESTAMP(0)"));
        Assert.Equal("CURRENT_TIMESTAMP", await OnUpdateOfAsync(
            "datetime(0) DEFAULT CURRENT_TIMESTAMP(0) ON UPDATE CURRENT_TIMESTAMP(0)"));

        Assert.Equal("CURRENT_TIMESTAMP", FromDatabaseText("current_timestamp(0)"));
        Assert.Equal("CURRENT_TIMESTAMP", FromDatabaseText("CURRENT_TIMESTAMP(0)"));
    }

    /// <summary>
    /// The parser must carry the ON UPDATE token through verbatim rather than reducing it to a
    /// flag, so the provider is the one deciding what it can model.
    /// </summary>
    [Fact]
    public void Parser_CarriesOnUpdateTokenVerbatim()
    {
        var root = new AntlrMariaDbParser().Parse(
            "CREATE TABLE t (a datetime(3) DEFAULT CURRENT_TIMESTAMP(3) ON UPDATE CURRENT_TIMESTAMP(3));");

        var column = ((CreateTableStatement)root.Statements[0])
            .Elements.OfType<ColumnDefinition>().Single();
        var @default = column.Constraints.OfType<DefaultColumnConstraint>().Single();

        Assert.Equal("CURRENT_TIMESTAMP(3)", @default.Token);
        Assert.Equal("CURRENT_TIMESTAMP(3)", @default.OnUpdateToken);
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
    /// The grammar's <c>currentTimestamp</c> rule also admits <c>LOCALTIME</c>,
    /// <c>LOCALTIMESTAMP</c>, <c>CURDATE</c> and <c>CURTIME</c>, but those are not synonyms for
    /// the current timestamp and must not fold into its canonical token. Measured against a
    /// live MariaDB, <c>DEFAULT LOCALTIME</c> is stored as <c>curtime()</c> — a time of day,
    /// not a timestamp — and <c>DEFAULT LOCALTIMESTAMP</c> as <c>localtimestamp()</c>. Folding
    /// either in would produce a parsed default that never matches the extracted one, i.e. a
    /// column that re-diffs on every deploy forever.
    /// </summary>
    [Theory]
    [InlineData("datetime DEFAULT LOCALTIME")]
    [InlineData("datetime DEFAULT LOCALTIMESTAMP")]
    [InlineData("date DEFAULT CURDATE()")]
    [InlineData("time DEFAULT CURTIME()")]
    public async Task NonSynonymTimeFunctions_AreNotFoldedIntoCurrentTimestamp(string columnSql)
    {
        Assert.Null(await DefaultOfAsync(columnSql));
    }

    [Theory]
    // The forms MariaDB actually stores for those defaults — none may canonicalize to the
    // current-timestamp token.
    [InlineData("curtime()")]
    [InlineData("localtimestamp()")]
    [InlineData("curdate()")]
    public void NonSynonymStoredForms_DoNotCanonicalizeToCurrentTimestamp(string stored)
    {
        Assert.NotEqual("CURRENT_TIMESTAMP", FromDatabaseText(stored));
    }

    /// <summary>
    /// Carrying a precision must not widen the set of functions that fold into the
    /// current-timestamp token: <c>LOCALTIME(3)</c> and friends are still not synonyms, and
    /// MariaDB stores them as their own functions.
    /// </summary>
    [Theory]
    [InlineData("datetime(3) DEFAULT LOCALTIME(3)")]
    [InlineData("datetime(3) DEFAULT LOCALTIMESTAMP(3)")]
    [InlineData("time(3) DEFAULT CURTIME(3)")]
    public async Task PrecisionCarryingNonSynonyms_AreStillNotModeled(string columnSql)
    {
        Assert.Null(await DefaultOfAsync(columnSql));
    }

    [Theory]
    [InlineData("curtime(3)")]
    [InlineData("localtimestamp(3)")]
    public void PrecisionCarryingNonSynonymStoredForms_DoNotCanonicalize(string stored)
    {
        Assert.Null(FromDatabaseText(stored));
    }

    /// <summary>
    /// A precision must be a plain integer. Anything else in the parentheses is not a form
    /// either engine produces, so it stays unmodeled rather than being echoed back blindly.
    /// </summary>
    [Theory]
    [InlineData("current_timestamp(x)")]
    [InlineData("current_timestamp(3, 4)")]
    [InlineData("current_timestamp(-1)")]
    public void MalformedPrecision_IsNotModeled(string stored)
    {
        Assert.Null(FromDatabaseText(stored));
    }
}
