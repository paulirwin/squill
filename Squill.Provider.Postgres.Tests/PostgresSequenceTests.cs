using Squill.Core;
using Squill.PostgresParser;
using Squill.TestFramework;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Docker-free tests over standalone <c>CREATE SEQUENCE</c> (issue #122): the model shape a
/// declared sequence produces, how it is scripted, and how a change to one is diffed.
///
/// The central convention under test is that an option equal to the PostgreSQL default is not
/// stored. The catalog always reports every option with defaults filled in, so a parsed model
/// could only ever hash-match an extracted one if both omit the defaults — the same
/// omit-when-default rule identity columns already follow.
/// </summary>
public class PostgresSequenceTests
{
    private static readonly PostgresDatabaseProvider Provider = new("Host=unused");

    private static Task<Model> ParseModelAsync(string sql)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrPostgresParser()),
            TestContext.Current.CancellationToken);

    private static async Task<(SchemaComparison Comparison, string Sql)> DiffAsync(
        string sourceSql, string targetSql)
    {
        var source = await ParseModelAsync(sourceSql);
        var target = await ParseModelAsync(targetSql);

        var comparison = SchemaCompare.Compare(Provider, source, target);

        return (comparison, new PostgresScriptGenerator().GenerateScript(comparison));
    }

    private static async Task<Element> SingleSequenceAsync(string sql)
    {
        var model = await ParseModelAsync(sql);

        return Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlSequence);
    }

    // ---- Model shape ----

    [Fact]
    public async Task Sequence_BareDeclaration_StoresNoOptionProperties()
    {
        var sequence = await SingleSequenceAsync("CREATE SEQUENCE order_number;");

        Assert.Equal("order_number", sequence.Name);
        Assert.Equal("public", PostgresModelFactory.GetSchema(sequence));

        // Every option was left at its default, so none is stored.
        Assert.Null(sequence.GetProperty<long?>(PostgresPropertyNames.StartValue));
        Assert.Null(sequence.GetProperty<long?>(PostgresPropertyNames.Increment));
        Assert.Null(sequence.GetProperty<long?>(PostgresPropertyNames.MinValue));
        Assert.Null(sequence.GetProperty<long?>(PostgresPropertyNames.MaxValue));
        Assert.Null(sequence.GetProperty<long?>(PostgresPropertyNames.CacheSize));
        Assert.Null(sequence.GetProperty<bool?>(PostgresPropertyNames.IsCycling));
    }

    // A standalone sequence defaults to bigint — unlike an identity column's sequence, which
    // takes the column's type. The declared type is stored only when it is not that default.
    [Fact]
    public async Task Sequence_DefaultDataType_IsNotStored()
    {
        var implicitType = await SingleSequenceAsync("CREATE SEQUENCE s;");
        var explicitType = await SingleSequenceAsync("CREATE SEQUENCE s AS bigint;");

        Assert.Null(implicitType.GetProperty<string>(PostgresPropertyNames.SequenceDataType));
        Assert.Null(explicitType.GetProperty<string>(PostgresPropertyNames.SequenceDataType));
    }

    [Fact]
    public async Task Sequence_NonDefaultDataType_IsStored()
    {
        var sequence = await SingleSequenceAsync("CREATE SEQUENCE s AS integer;");

        Assert.Equal("integer", sequence.GetProperty<string>(PostgresPropertyNames.SequenceDataType));
    }

    [Fact]
    public async Task Sequence_ExplicitOptions_AreStored()
    {
        var sequence = await SingleSequenceAsync(
            "CREATE SEQUENCE s START WITH 100 INCREMENT BY 5 MINVALUE 10 MAXVALUE 5000 CACHE 20 CYCLE;");

        Assert.Equal(100, sequence.GetProperty<long?>(PostgresPropertyNames.StartValue));
        Assert.Equal(5, sequence.GetProperty<long?>(PostgresPropertyNames.Increment));
        Assert.Equal(10, sequence.GetProperty<long?>(PostgresPropertyNames.MinValue));
        Assert.Equal(5000, sequence.GetProperty<long?>(PostgresPropertyNames.MaxValue));
        Assert.Equal(20, sequence.GetProperty<long?>(PostgresPropertyNames.CacheSize));
        Assert.Equal(true, sequence.GetProperty<bool?>(PostgresPropertyNames.IsCycling));
    }

    // Writing an option that happens to equal the default must produce the same model as
    // omitting it — otherwise a schema that spells out its defaults would redeploy forever.
    [Fact]
    public async Task Sequence_ExplicitlyDefaultOptions_HashAsIfOmitted()
    {
        var (comparison, _) = await DiffAsync(
            "CREATE SEQUENCE s START WITH 1 INCREMENT BY 1 MINVALUE 1 CACHE 1 NO CYCLE;",
            "CREATE SEQUENCE s;");

        Assert.Empty(comparison.Deltas);
    }

    // A descending sequence has different defaults (start and maxvalue -1, minvalue = type
    // min), so the direction must be honored when deciding what to omit.
    [Fact]
    public async Task Sequence_DescendingDefaults_AreOmitted()
    {
        var sequence = await SingleSequenceAsync(
            "CREATE SEQUENCE s INCREMENT BY -1 START WITH -1 MAXVALUE -1;");

        // The increment is not the default (1) so it is stored; start and maxvalue equal the
        // descending default and are not.
        Assert.Equal(-1, sequence.GetProperty<long?>(PostgresPropertyNames.Increment));
        Assert.Null(sequence.GetProperty<long?>(PostgresPropertyNames.StartValue));
        Assert.Null(sequence.GetProperty<long?>(PostgresPropertyNames.MaxValue));
    }

    // The bound defaults depend on the declared type, so `AS integer MAXVALUE 2147483647` is
    // the default for an integer sequence but a real value for a bigint one.
    [Fact]
    public async Task Sequence_TypeDependentBoundDefault_IsOmitted()
    {
        var integerSequence = await SingleSequenceAsync(
            "CREATE SEQUENCE s AS integer MAXVALUE 2147483647;");
        var bigintSequence = await SingleSequenceAsync(
            "CREATE SEQUENCE s MAXVALUE 2147483647;");

        Assert.Null(integerSequence.GetProperty<long?>(PostgresPropertyNames.MaxValue));
        Assert.Equal(2147483647, bigintSequence.GetProperty<long?>(PostgresPropertyNames.MaxValue));
    }

    // ---- Scripting ----

    [Fact]
    public async Task Sequence_Create_EmitsCreateSequence()
    {
        var (_, sql) = await DiffAsync("CREATE SEQUENCE order_number;", "");

        Assert.Contains("CREATE SEQUENCE \"order_number\";", sql);
    }

    [Fact]
    public async Task Sequence_CreateWithOptions_EmitsEveryStoredOption()
    {
        var (_, sql) = await DiffAsync(
            "CREATE SEQUENCE s AS integer START WITH 100 INCREMENT BY 5 MINVALUE 10 MAXVALUE 5000 CACHE 20 CYCLE;",
            "");

        Assert.Contains("AS integer", sql);
        Assert.Contains("INCREMENT BY 5", sql);
        Assert.Contains("MINVALUE 10", sql);
        Assert.Contains("MAXVALUE 5000", sql);
        Assert.Contains("START WITH 100", sql);
        Assert.Contains("CACHE 20", sql);
        Assert.Contains("CYCLE", sql);
    }

    [Fact]
    public async Task Sequence_SchemaQualifiesNonPublicSchema()
    {
        var (_, sql) = await DiffAsync(
            """
            CREATE SCHEMA inventory;
            CREATE SEQUENCE inventory.order_number;
            """,
            "CREATE SCHEMA inventory;");

        Assert.Contains("CREATE SEQUENCE \"inventory\".\"order_number\";", sql);
    }

    // ---- Diffing ----

    [Fact]
    public async Task Sequence_Unchanged_ProducesNoDelta()
    {
        var (comparison, _) = await DiffAsync(
            "CREATE SEQUENCE s INCREMENT BY 5;", "CREATE SEQUENCE s INCREMENT BY 5;");

        Assert.Empty(comparison.Deltas);
    }

    // A sequence's options are all alterable in place, and it may already be feeding a column
    // default, so a changed sequence is an ALTER rather than a drop and recreate — which would
    // reset the counter and break anything drawing from it.
    [Fact]
    public async Task Sequence_ChangedOption_EmitsAlterSequenceNotDropCreate()
    {
        var (_, sql) = await DiffAsync(
            "CREATE SEQUENCE s INCREMENT BY 5;", "CREATE SEQUENCE s INCREMENT BY 2;");

        Assert.Contains("ALTER SEQUENCE \"s\" INCREMENT BY 5;", sql);
        Assert.DoesNotContain("DROP SEQUENCE", sql);
        Assert.DoesNotContain("CREATE SEQUENCE", sql);
    }

    // An option dropped from the declaration must be actively reset to its default, not merely
    // left alone: the deployed sequence still carries the old value.
    [Fact]
    public async Task Sequence_RemovedOption_ResetsToDefault()
    {
        var (_, sql) = await DiffAsync(
            "CREATE SEQUENCE s;", "CREATE SEQUENCE s INCREMENT BY 5 MAXVALUE 900 CYCLE;");

        Assert.Contains("INCREMENT BY 1", sql);
        Assert.Contains("NO MAXVALUE", sql);
        Assert.Contains("NO CYCLE", sql);
    }

    [Fact]
    public async Task Sequence_Dropped_EmitsDropSequence()
    {
        var source = await ParseModelAsync("");
        var target = await ParseModelAsync("CREATE SEQUENCE s;");

        var comparison = SchemaCompare.Compare(
            Provider, source, target, new DeployOptions { DropObjectsNotInSource = true });

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("DROP SEQUENCE IF EXISTS \"s\";", sql);
    }
}
