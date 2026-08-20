using Squill.Core;
using Squill.PostgresParser;
using Squill.TestFramework;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Docker-free tests over parameterized operator classes (PostgreSQL 13+, issue #211): the model
/// shape a parameterized index key produces and how it is scripted back out.
///
/// The parameters and the opclass name arrive together on one grammar alternative, so before
/// #211 both were lost. Losing the name is the more consequential half: measured, PostgreSQL
/// rejects the parameters without an explicit class name, so an index that did reach the model
/// would have scripted to DDL the server refuses.
/// </summary>
public class PostgresOpclassParameterModelTests
{
    private static readonly PostgresDatabaseProvider Provider = new("Host=unused");

    private const string DocsTable = """
CREATE TABLE docs
(
    id   integer PRIMARY KEY,
    body text,
    tsv  tsvector
);
""";

    private static Task<Model> ParseModelAsync(string sql)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrPostgresParser()),
            TestContext.Current.CancellationToken);

    private static async Task<Element> SingleIndexAsync(string indexSql)
    {
        var model = await ParseModelAsync($"{DocsTable}\n{indexSql}");

        return Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlIndex);
    }

    private static async Task<string> ScriptAsync(string sql)
    {
        var source = await ParseModelAsync($"{DocsTable}\n{sql}");
        var comparison = SchemaCompare.Compare(Provider, source, new Model());

        return new PostgresScriptGenerator().GenerateScript(comparison);
    }

    private static Element SingleKeyColumn(Element index)
        => Assert.Single(
            index.GetRelationship(PostgresRelationshipNames.ColumnSpecifications)!
                .Entries.OfType<Element>());

    [Fact]
    public async Task OpclassParameters_AreStoredOnTheKeyColumn()
    {
        var index = await SingleIndexAsync(
            "CREATE INDEX ix_docs_tsv ON docs USING gist (tsv tsvector_ops(siglen=256));");

        Assert.Equal(
            "siglen=256",
            SingleKeyColumn(index)
                .GetProperty<string>(PostgresPropertyNames.OperatorClassParameters));
    }

    /// <summary>
    /// The name has to survive alongside the parameters, since it is what makes the scripted
    /// DDL legal.
    /// </summary>
    [Fact]
    public async Task OpclassParameters_KeepTheOperatorClassName()
    {
        var index = await SingleIndexAsync(
            "CREATE INDEX ix_docs_tsv ON docs USING gist (tsv tsvector_ops(siglen=256));");

        Assert.Equal(
            "tsvector_ops",
            SingleKeyColumn(index).GetProperty<string>(PostgresPropertyNames.OperatorClass));
    }

    /// <summary>
    /// The load-bearing half of the omit-when-default convention: an index whose opclass takes
    /// no parameters must carry no parameters property, or it could never hash-match a model
    /// extracted from a catalog that reports attoptions NULL for the same key.
    /// </summary>
    [Fact]
    public async Task OpclassWithoutParameters_StoresNoParameters()
    {
        var index = await SingleIndexAsync(
            "CREATE INDEX ix_docs_tsv ON docs USING gist (tsv tsvector_ops);");

        Assert.Null(
            SingleKeyColumn(index)
                .GetProperty<string>(PostgresPropertyNames.OperatorClassParameters));
    }

    [Fact]
    public async Task MultipleOpclassParameters_AreStoredInDeclarationOrder()
    {
        var index = await SingleIndexAsync(
            "CREATE INDEX ix_docs_tsv ON docs USING gist (tsv tsvector_ops(siglen=256, x=1));");

        Assert.Equal(
            "siglen=256, x=1",
            SingleKeyColumn(index)
                .GetProperty<string>(PostgresPropertyNames.OperatorClassParameters));
    }

    /// <summary>
    /// The catalog reports an opclass unqualified, so a schema-qualified source spelling must
    /// reduce to the same bare name even on the parameterized alternative.
    /// </summary>
    [Fact]
    public async Task SchemaQualifiedParameterizedOpclass_IsStoredUnqualified()
    {
        var index = await SingleIndexAsync(
            """
            CREATE INDEX ix_docs_tsv ON docs
                USING gist (tsv pg_catalog.tsvector_ops(siglen=256));
            """);

        var key = SingleKeyColumn(index);

        Assert.Equal("tsvector_ops", key.GetProperty<string>(PostgresPropertyNames.OperatorClass));
        Assert.Equal(
            "siglen=256",
            key.GetProperty<string>(PostgresPropertyNames.OperatorClassParameters));
    }

    [Fact]
    public async Task OpclassParameters_AreScriptedAfterTheClassName()
    {
        var script = await ScriptAsync(
            "CREATE INDEX ix_docs_tsv ON docs USING gist (tsv tsvector_ops(siglen=256));");

        Assert.Contains("\"tsv\" tsvector_ops (siglen=256)", script);
    }

    [Fact]
    public async Task OpclassWithoutParameters_ScriptsWithoutParentheses()
    {
        var script = await ScriptAsync(
            "CREATE INDEX ix_docs_tsv ON docs USING gist (tsv tsvector_ops);");

        Assert.Contains("\"tsv\" tsvector_ops", script);
        Assert.DoesNotContain("tsvector_ops (", script);
    }

    /// <summary>
    /// Changing a parameter must change the hash, or altering siglen would deploy as a no-op.
    /// </summary>
    [Fact]
    public async Task ChangingAnOpclassParameter_ChangesTheHash()
    {
        var a = await SingleIndexAsync(
            "CREATE INDEX ix_docs_tsv ON docs USING gist (tsv tsvector_ops(siglen=256));");
        var b = await SingleIndexAsync(
            "CREATE INDEX ix_docs_tsv ON docs USING gist (tsv tsvector_ops(siglen=124));");

        Assert.False(HashUtility.HashesEqual(a.Hash, b.Hash));
    }
}
