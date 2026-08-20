using Squill.Core;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Model-level tests for CREATE SEQUENCE (issue #218). Sequences reached the builder as an
/// UnmodeledStatement before this, so a declared sequence warned and then never reached the
/// DACPAC at all.
///
/// The defaults asserted here are measured against mariadb:latest via SHOW CREATE SEQUENCE,
/// not taken from the Postgres provider, which models the same concept with different values
/// (CACHE 1000 vs 1, and bounds one short of the int64 extremes).
/// </summary>
public class CreateSequenceModelTests
{
    private static async Task<(Model Model, IReadOnlyList<SqlSourceDiagnostic> Warnings)> BuildAsync(
        string sql, MariaDbFamilyDatabaseSchemaProvider? engine = null)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Sequence.sql", FileKind.Compile, sql));

        var result = await new ParserWorkspaceModelBuilder(
                workspace, new AntlrMariaDbParser(), engine ?? new MariaDb12DatabaseSchemaProvider())
            .ExtractModelAsync(TestContext.Current.CancellationToken);

        return (result.Model, result.Warnings);
    }

    private static Element SingleSequence(Model model)
        => model.Elements.Single(e => e.Type == MariaDbElementTypes.SqlSequence);

    private static string? ColumnDefault(Model model, string columnName)
        => model.Elements
            .Single(e => e.Type == MariaDbElementTypes.SqlTable)
            .Relationships.Single(r => r.Name == MariaDbRelationshipNames.Columns)
            .Entries.OfType<Element>()
            .Single(c => SqlName.UnqualifiedOf((string)c.Name!) == columnName)
            .GetProperty<string>(MariaDbPropertyNames.DefaultValue);

    [Fact]
    public async Task Sequence_IsModeledAndDoesNotWarn()
    {
        var (model, warnings) = await BuildAsync("CREATE SEQUENCE order_seq;");

        Assert.Equal("order_seq", SqlName.UnqualifiedOf((string)SingleSequence(model).Name!));
        Assert.Empty(warnings);
    }

    /// <summary>
    /// The whole point of the omit-when-default convention: the backing table always reports
    /// every option with its default filled in, so a bare sequence must record none of them or
    /// the parsed model could never hash-match the extracted one.
    /// </summary>
    [Fact]
    public async Task Sequence_Bare_RecordsNoOptions()
    {
        var (model, _) = await BuildAsync("CREATE SEQUENCE s;");

        Assert.Empty(SingleSequence(model).Properties);
    }

    [Fact]
    public async Task Sequence_AllOptions_AreModeled()
    {
        var (model, _) = await BuildAsync(
            "CREATE SEQUENCE s INCREMENT BY 5 MINVALUE 10 MAXVALUE 1000 "
            + "START WITH 20 CACHE 50 CYCLE;");

        var sequence = SingleSequence(model);

        Assert.Equal(5L, sequence.GetProperty<long?>(MariaDbPropertyNames.Increment));
        Assert.Equal(10L, sequence.GetProperty<long?>(MariaDbPropertyNames.MinValue));
        Assert.Equal(1000L, sequence.GetProperty<long?>(MariaDbPropertyNames.MaxValue));
        Assert.Equal(20L, sequence.GetProperty<long?>(MariaDbPropertyNames.StartValue));
        Assert.Equal(50L, sequence.GetProperty<long?>(MariaDbPropertyNames.CacheSize));
        Assert.True(sequence.GetProperty<bool?>(MariaDbPropertyNames.IsCycling));
    }

    /// <summary>
    /// Declaring a default explicitly must be indistinguishable from omitting it, since the
    /// extractor cannot tell the two apart.
    /// </summary>
    [Theory]
    [InlineData("CREATE SEQUENCE s INCREMENT BY 1;")]
    [InlineData("CREATE SEQUENCE s CACHE 1000;")]
    [InlineData("CREATE SEQUENCE s NOCYCLE;")]
    [InlineData("CREATE SEQUENCE s START WITH 1;")]
    [InlineData("CREATE SEQUENCE s MINVALUE 1;")]
    [InlineData("CREATE SEQUENCE s MAXVALUE 9223372036854775806;")]
    public async Task Sequence_ExplicitDefault_RecordsNothing(string sql)
    {
        var (model, _) = await BuildAsync(sql);

        Assert.Empty(SingleSequence(model).Properties);
    }

    /// <summary>
    /// The measured MariaDB default is 1000. Asserted directly because copying the Postgres
    /// provider's value of 1 would make every bare sequence record CacheSize and re-diff.
    /// </summary>
    [Fact]
    public async Task Sequence_CacheDifferingFromTheEngineDefault_IsModeled()
    {
        var (model, _) = await BuildAsync("CREATE SEQUENCE s CACHE 1;");

        Assert.Equal(1L, SingleSequence(model).GetProperty<long?>(MariaDbPropertyNames.CacheSize));
    }

    /// <summary>NOCACHE is cache_size 0, which differs from the default and so is recorded.</summary>
    [Theory]
    [InlineData("CREATE SEQUENCE s NOCACHE;")]
    [InlineData("CREATE SEQUENCE s CACHE 0;")]
    public async Task Sequence_NoCache_IsModeledAsZero(string sql)
    {
        var (model, _) = await BuildAsync(sql);

        Assert.Equal(0L, SingleSequence(model).GetProperty<long?>(MariaDbPropertyNames.CacheSize));
    }

    /// <summary>
    /// NO MINVALUE asks for the type default, which is what omitting the clause means, so it
    /// records nothing rather than a sentinel.
    /// </summary>
    [Theory]
    [InlineData("CREATE SEQUENCE s NO MINVALUE;")]
    [InlineData("CREATE SEQUENCE s NO MAXVALUE;")]
    public async Task Sequence_NoBound_RecordsNothing(string sql)
    {
        var (model, _) = await BuildAsync(sql);

        Assert.Empty(SingleSequence(model).Properties);
    }

    /// <summary>
    /// MySQL has no sequence object: CREATE SEQUENCE is a syntax error there (measured), so a
    /// build targeting it must fail rather than emit DDL the server would reject.
    /// </summary>
    [Fact]
    public async Task Sequence_TargetingMySql_IsABuildError()
    {
        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => BuildAsync("CREATE SEQUENCE s;", new MySql9DatabaseSchemaProvider()));

        Assert.Contains("sequence", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sequence_TargetingMariaDb_IsNotAnError()
    {
        var (model, warnings) = await BuildAsync(
            "CREATE SEQUENCE s;", new MariaDb10DatabaseSchemaProvider());

        Assert.Single(model.Elements, e => e.Type == MariaDbElementTypes.SqlSequence);
        Assert.Empty(warnings);
    }

    /// <summary>
    /// Two sequences of the same name are a mistake in the source, matching how duplicate
    /// tables and events are already treated.
    /// </summary>
    [Fact]
    public async Task Sequence_Duplicate_IsABuildError()
    {
        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => BuildAsync("CREATE SEQUENCE s; CREATE SEQUENCE s;"));

        Assert.Equal(SqlSourceException.DuplicateDefinition, ex.Code);
    }

    // ---- NEXTVAL column defaults ----

    /// <summary>
    /// All three spellings are stored by the server as one form (measured:
    /// <c>nextval(`db`.`seq`)</c> for each), so all three must reach the same canonical token
    /// or a table would re-diff depending on how its default happened to be written.
    /// </summary>
    [Theory]
    [InlineData("NEXTVAL(s)")]
    [InlineData("nextval(s)")]
    public async Task NextValueDefault_IsModeled(string spelling)
    {
        var (model, warnings) = await BuildAsync(
            $"CREATE SEQUENCE s; CREATE TABLE t (id bigint NOT NULL DEFAULT {spelling});");

        Assert.Equal("NEXTVAL(`s`)", ColumnDefault(model, "id"));
        Assert.Empty(warnings);
    }

    /// <summary>
    /// <c>NEXT VALUE FOR s</c> is the third spelling the server accepts (measured: it stores
    /// the same nextval form as the other two), but the vendored grammar has no alternative
    /// for it in a column default, so it fails to parse. A gap in the grammar rather than in
    /// MariaDB, and closing it needs an upstream change.
    ///
    /// <para>
    /// Pinned as a build error on purpose, for the same reason as the descending sequence:
    /// failing loudly beats deploying a column with no default at all. The canonicalizer does
    /// recognize the spelling, so this test is what will fail, and say so, once the grammar
    /// can express it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task NextValueForSpelling_IsAGrammarGapAndFailsToParse()
    {
        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => BuildAsync(
                "CREATE SEQUENCE s; CREATE TABLE t (id bigint NOT NULL DEFAULT NEXT VALUE FOR s);"));

        Assert.Contains("Syntax error", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The database qualifier is dropped. It names the environment the schema happens to live
    /// in, and a DACPAC is environment-neutral, so keeping it would make a project built
    /// against one database re-diff against the same schema deployed under another name.
    /// </summary>
    [Fact]
    public async Task NextValueDefault_DropsTheDatabaseQualifier()
    {
        var (model, _) = await BuildAsync(
            "CREATE SEQUENCE s; CREATE TABLE t (id bigint NOT NULL DEFAULT NEXTVAL(shop.s));");

        Assert.Equal("NEXTVAL(`s`)", ColumnDefault(model, "id"));
    }

    /// <summary>
    /// A sequence must be created before a table whose column draws from it, so it sorts ahead
    /// of the tables regardless of the order the source declares them in.
    /// </summary>
    [Fact]
    public async Task Sequence_IsOrderedBeforeTables_EvenWhenDeclaredAfter()
    {
        var (model, _) = await BuildAsync(
            "CREATE TABLE t (id bigint NOT NULL DEFAULT NEXTVAL(s)); CREATE SEQUENCE s;");

        Assert.Equal(MariaDbElementTypes.SqlSequence, model.Elements[0].Type);
    }

    /// <summary>
    /// RESTART parses but the server rejects it on CREATE (measured: it is an ALTER-only
    /// option), so it must not reach the DACPAC as if it had been applied.
    /// </summary>
    [Fact]
    public async Task Sequence_Restart_IsABuildError()
    {
        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => BuildAsync("CREATE SEQUENCE s RESTART WITH 5;"));

        Assert.Contains("RESTART", ex.Message, StringComparison.Ordinal);
    }
}
