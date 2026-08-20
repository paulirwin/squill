using Squill.MariaDbParser.Syntax;
using Squill.TestFramework;

namespace Squill.MariaDbParser.Tests;

/// <summary>
/// Parser-level tests for the <c>indexOption</c> clauses of issue #211. The mapper used to
/// iterate the option list solely to recover a trailing <c>USING</c>, dropping everything else
/// without a diagnostic.
///
/// Which of these reach the model is decided by measurement against a live server and is
/// asserted at the model level; the parser's job is only to surface them so the model builder
/// can either record one or warn about it. Notably <c>WITH PARSER</c> and
/// <c>KEY_BLOCK_SIZE</c> are captured here but deliberately not modeled; see
/// Squill.Provider.MariaDb.Tests for why.
/// </summary>
public class IndexOptionTests
{
    private static CreateIndexStatement ParseOne(string text)
        => ParseAssertions.Single<CreateIndexStatement>(new AntlrMariaDbParser().Parse(text).Statements);

    [Fact]
    public void IndexComment_IsCaptured()
    {
        var index = ParseOne("CREATE INDEX ix ON t (a) COMMENT 'why this index exists';");

        Assert.Equal("why this index exists", index.Comment);
    }

    [Fact]
    public void NoIndexComment_LeavesCommentNull()
    {
        Assert.Null(ParseOne("CREATE INDEX ix ON t (a);").Comment);
    }

    /// <summary>
    /// MySQL's spelling. An invisible index is ignored by the optimizer, so dropping the
    /// keyword silently changes which query plans the server can choose.
    /// </summary>
    [Fact]
    public void Invisible_IsCaptured()
    {
        var index = ParseOne("CREATE INDEX ix ON t (a) INVISIBLE;");

        Assert.True(index.IsInvisible);
    }

    [Fact]
    public void Visible_IsCapturedAsNotInvisible()
    {
        var index = ParseOne("CREATE INDEX ix ON t (a) VISIBLE;");

        Assert.False(index.IsInvisible);
    }

    /// <summary>
    /// MariaDB's spelling of the same idea. Measured, the two are not interchangeable: MariaDB
    /// rejects INVISIBLE with a syntax error and MySQL rejects IGNORED, so each engine's model
    /// has to script its own keyword.
    /// </summary>
    [Fact]
    public void Ignored_IsCaptured()
    {
        var index = ParseOne("CREATE INDEX ix ON t (a) IGNORED;");

        Assert.True(index.IsIgnored);
    }

    [Fact]
    public void NotIgnored_IsCapturedAsNotIgnored()
    {
        var index = ParseOne("CREATE INDEX ix ON t (a) NOT IGNORED;");

        Assert.False(index.IsIgnored);
    }

    [Fact]
    public void NoVisibilityClause_LeavesBothNull()
    {
        var index = ParseOne("CREATE INDEX ix ON t (a);");

        Assert.Null(index.IsInvisible);
        Assert.Null(index.IsIgnored);
    }

    [Fact]
    public void WithParser_IsCaptured()
    {
        var index = ParseOne("CREATE FULLTEXT INDEX ix ON t (body) WITH PARSER ngram;");

        Assert.Equal("ngram", index.ParserName);
    }

    [Fact]
    public void KeyBlockSize_IsCaptured()
    {
        var index = ParseOne("CREATE INDEX ix ON t (a) KEY_BLOCK_SIZE=8;");

        Assert.Equal("8", index.KeyBlockSize);
    }

    /// <summary>
    /// The options are a repeated list, so several may be written together, and a trailing
    /// USING has to keep working alongside them.
    /// </summary>
    [Fact]
    public void SeveralOptions_AreAllCaptured()
    {
        var index = ParseOne(
            "CREATE INDEX ix ON t (a) USING BTREE COMMENT 'note' INVISIBLE KEY_BLOCK_SIZE=4;");

        Assert.Equal("BTREE", index.IndexMethod);
        Assert.Equal("note", index.Comment);
        Assert.True(index.IsInvisible);
        Assert.Equal("4", index.KeyBlockSize);
    }

    [Fact]
    public void InlineIndex_CapturesOptionsToo()
    {
        var parser = new AntlrMariaDbParser();
        var root = parser.Parse(
            "CREATE TABLE t (a INT, INDEX ix (a) COMMENT 'inline' INVISIBLE);");

        var table = ParseAssertions.Single<CreateTableStatement>(root.Statements);
        var index = Assert.Single(table.Elements.OfType<IndexTableConstraint>());

        Assert.Equal("inline", index.Comment);
        Assert.True(index.IsInvisible);
    }
}
