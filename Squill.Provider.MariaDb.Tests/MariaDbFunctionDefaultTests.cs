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
///
/// The rest of the time family (issue #147) is where that stops holding: <c>LOCALTIME</c>,
/// <c>LOCALTIMESTAMP</c>, <c>CURDATE</c> and <c>CURTIME</c> mean genuinely different things on
/// each engine, so the same source canonicalizes differently depending on the target — which is
/// why these tests pin both engines rather than one shared answer.
/// </summary>
public class MariaDbFunctionDefaultTests
{
    private static readonly Type DefaultValueType =
        typeof(MariaDbElementTypes).Assembly
            .GetType("Squill.Provider.MariaDb.MariaDbDefaultValue", throwOnError: true)!;

    // The engine under test defaults to MariaDB; the MySQL cases pass MySql explicitly.
    private static readonly MariaDbFamilyDatabaseSchemaProvider MariaDb =
        new MariaDb12DatabaseSchemaProvider();

    private static readonly MariaDbFamilyDatabaseSchemaProvider MySql =
        new MySql9DatabaseSchemaProvider();

    private static string? FromDatabaseText(
        string? text,
        bool isCharacterColumn = false,
        MariaDbFamilyDatabaseSchemaProvider? engine = null)
        => (string?)DefaultValueType
            .GetMethod("FromDatabaseText", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [text, engine ?? MariaDb, isCharacterColumn]);

    private static async Task<Element> ColumnOfAsync(
        string columnSql, MariaDbFamilyDatabaseSchemaProvider? engine = null)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile,
            $"CREATE TABLE t (id int PRIMARY KEY, c {columnSql});"));

        var result = await new ParserWorkspaceModelBuilder(workspace, new AntlrMariaDbParser(), engine ?? MariaDb)
            .ExtractModelAsync(TestContext.Current.CancellationToken);

        var table = Assert.Single(result.Model.Elements, e => e.Type == MariaDbElementTypes.SqlTable);

        return table.Relationships
            .Single(r => r.Name == MariaDbRelationshipNames.Columns)
            .Entries.OfType<Element>()
            .Single(c => SqlName.UnqualifiedOf((string)c.Name!) == "c");
    }

    private static async Task<string?> DefaultOfAsync(
        string columnSql, MariaDbFamilyDatabaseSchemaProvider? engine = null)
        => (await ColumnOfAsync(columnSql, engine))
            .GetProperty<string>(MariaDbPropertyNames.DefaultValue);

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

        var result = await new ParserWorkspaceModelBuilder(workspace, new AntlrMariaDbParser(), MariaDb)
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

        var result = await new ParserWorkspaceModelBuilder(workspace, new AntlrMariaDbParser(), MariaDb)
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

        var result = await new ParserWorkspaceModelBuilder(workspace, new AntlrMariaDbParser(), MariaDb)
            .ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// The rest of the grammar's <c>currentTimestamp</c> family — <c>LOCALTIME</c>,
    /// <c>LOCALTIMESTAMP</c>, <c>CURDATE</c>, <c>CURTIME</c> — is modeled as of issue #147, but
    /// each keeps its <em>own</em> token on MariaDB rather than folding into the
    /// current-timestamp one. Measured against <c>mariadb:latest</c>: <c>DEFAULT LOCALTIME</c>
    /// is stored as <c>curtime()</c> — a time of day, not a timestamp — and
    /// <c>DEFAULT LOCALTIMESTAMP</c> as <c>localtimestamp()</c>. Folding either into
    /// <c>CURRENT_TIMESTAMP</c> would produce a parsed default that never matches the extracted
    /// one, i.e. a column that re-diffs on every deploy forever.
    /// </summary>
    [Theory]
    [InlineData("datetime DEFAULT LOCALTIME", "CURTIME()")]
    [InlineData("datetime DEFAULT LOCALTIMESTAMP", "LOCALTIMESTAMP()")]
    [InlineData("date DEFAULT CURDATE()", "CURDATE()")]
    [InlineData("time DEFAULT CURTIME()", "CURTIME()")]
    [InlineData("date DEFAULT CURRENT_DATE", "CURDATE()")]
    [InlineData("time DEFAULT CURRENT_TIME", "CURTIME()")]
    public async Task MariaDbTimeFunctions_KeepTheirOwnToken(string columnSql, string expected)
    {
        Assert.Equal(expected, await DefaultOfAsync(columnSql));
    }

    [Theory]
    // None of these may collapse onto the current-timestamp token on MariaDB.
    [InlineData("datetime DEFAULT LOCALTIME")]
    [InlineData("datetime DEFAULT LOCALTIMESTAMP")]
    [InlineData("date DEFAULT CURDATE()")]
    [InlineData("time DEFAULT CURTIME()")]
    public async Task MariaDbTimeFunctions_AreNotFoldedIntoCurrentTimestamp(string columnSql)
    {
        Assert.NotEqual("CURRENT_TIMESTAMP", await DefaultOfAsync(columnSql));
    }

    /// <summary>
    /// The canonical token is the engine's own reported spelling, so a model parsed from source
    /// hash-matches one extracted from the database. Verified against <c>mariadb:latest</c>:
    /// re-applying the stored form as DDL stores it unchanged, so a redeploy is a no-op.
    /// </summary>
    [Theory]
    [InlineData("curtime()", "CURTIME()")]
    [InlineData("localtimestamp()", "LOCALTIMESTAMP()")]
    [InlineData("curdate()", "CURDATE()")]
    public void MariaDbStoredForms_CanonicalizeToTheSameToken(string stored, string expected)
    {
        Assert.Equal(expected, FromDatabaseText(stored));
    }

    /// <summary>
    /// The parser side and the MariaDB extractor side must agree on one token per function, or
    /// every one of these columns would show a phantom change on each deploy.
    /// </summary>
    [Theory]
    [InlineData("datetime DEFAULT LOCALTIME", "curtime()")]
    [InlineData("datetime DEFAULT LOCALTIMESTAMP", "localtimestamp()")]
    [InlineData("date DEFAULT CURDATE()", "curdate()")]
    [InlineData("time DEFAULT CURTIME()", "curtime()")]
    public async Task ParsedAndMariaDbStoredForms_Agree(string columnSql, string stored)
    {
        Assert.Equal(await DefaultOfAsync(columnSql), FromDatabaseText(stored));
    }

    /// <summary>
    /// The precision-carrying forms keep their precision, exactly as the current-timestamp form
    /// does (issue #144). Measured: a <c>datetime(3) DEFAULT LOCALTIMESTAMP(3)</c> comes back as
    /// <c>localtimestamp(3)</c>.
    /// </summary>
    [Theory]
    [InlineData("datetime(3) DEFAULT LOCALTIMESTAMP(3)", "LOCALTIMESTAMP(3)")]
    [InlineData("time(3) DEFAULT CURTIME(3)", "CURTIME(3)")]
    public async Task MariaDbTimeFunctions_KeepTheirPrecision(string columnSql, string expected)
    {
        Assert.Equal(expected, await DefaultOfAsync(columnSql));
    }

    [Theory]
    [InlineData("curtime(3)", "CURTIME(3)")]
    [InlineData("localtimestamp(3)", "LOCALTIMESTAMP(3)")]
    public void MariaDbPrecisionCarryingStoredForms_Canonicalize(string stored, string expected)
    {
        Assert.Equal(expected, FromDatabaseText(stored));
    }

    /// <summary>
    /// On MySQL the very same source means something different: per
    /// <see href="https://dev.mysql.com/doc/refman/8.4/en/timestamp-initialization.html">its
    /// docs</see> and measured against <c>mysql:latest</c>, <c>LOCALTIME</c> and
    /// <c>LOCALTIMESTAMP</c> are true <c>CURRENT_TIMESTAMP</c> synonyms and are stored — and
    /// reported — as <c>CURRENT_TIMESTAMP</c>. This is the whole reason the engine is a required
    /// input rather than a default (issue #147).
    /// </summary>
    [Theory]
    [InlineData("datetime DEFAULT LOCALTIME")]
    [InlineData("datetime DEFAULT LOCALTIMESTAMP")]
    public async Task OnMySql_LocaltimeFamily_FoldsIntoCurrentTimestamp(string columnSql)
    {
        Assert.Equal("CURRENT_TIMESTAMP", await DefaultOfAsync(columnSql, MySql));
    }

    /// <summary>
    /// The same two spellings on MariaDB must NOT fold — the direct contrast that a single
    /// shared token could not express.
    /// </summary>
    [Theory]
    [InlineData("datetime DEFAULT LOCALTIME", "CURTIME()")]
    [InlineData("datetime DEFAULT LOCALTIMESTAMP", "LOCALTIMESTAMP()")]
    public async Task TheSameSource_CanonicalizesDifferentlyPerEngine(string columnSql, string onMariaDb)
    {
        Assert.Equal(onMariaDb, await DefaultOfAsync(columnSql, MariaDb));
        Assert.Equal("CURRENT_TIMESTAMP", await DefaultOfAsync(columnSql, MySql));
    }

    /// <summary>
    /// MySQL rejects <c>CURDATE()</c> / <c>CURTIME()</c> in a <c>DEFAULT</c> outright — measured,
    /// it is a syntax error, not merely an invalid value — so they stay unmodeled there and are
    /// reported at build time rather than deployed into a failing script.
    /// </summary>
    [Theory]
    [InlineData("date DEFAULT CURDATE()")]
    [InlineData("time DEFAULT CURTIME()")]
    [InlineData("date DEFAULT CURRENT_DATE")]
    public async Task OnMySql_CurdateAndCurtime_AreNotModeled(string columnSql)
    {
        Assert.Null(await DefaultOfAsync(columnSql, MySql));
    }

    /// <summary>
    /// MySQL reports every one of its accepted synonyms as the bare keyword, so the extractor
    /// side agrees with the parser side there too.
    /// </summary>
    [Fact]
    public async Task OnMySql_ParsedAndStoredForms_Agree()
    {
        var parsed = await DefaultOfAsync("datetime DEFAULT LOCALTIME", MySql);

        Assert.Equal(parsed, FromDatabaseText("CURRENT_TIMESTAMP", engine: MySql));
    }

    /// <summary>
    /// Only the current-timestamp family is valid in <c>ON UPDATE</c> position; both engines
    /// reject the others there. Modeling one would emit a clause that cannot be deployed, so it
    /// is left unmodeled and warned about instead.
    /// </summary>
    [Theory]
    [InlineData("datetime DEFAULT CURRENT_TIMESTAMP ON UPDATE LOCALTIME")]
    [InlineData("datetime DEFAULT CURRENT_TIMESTAMP ON UPDATE CURTIME()")]
    [InlineData("datetime DEFAULT CURRENT_TIMESTAMP ON UPDATE CURDATE()")]
    public async Task NonTimestampOnUpdate_IsNotModeled(string columnSql)
    {
        Assert.Null(await OnUpdateOfAsync(columnSql));
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
