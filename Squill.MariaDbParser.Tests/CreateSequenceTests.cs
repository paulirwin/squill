using Squill.MariaDbParser.Syntax;
using Squill.TestFramework;

namespace Squill.MariaDbParser.Tests;

/// <summary>
/// Parser-level tests for CREATE SEQUENCE (issue #218), asserting the syntax tree the mapper
/// produces. Model-level concerns (element shape, omit-when-default, script generation) are
/// covered in Squill.Provider.MariaDb.Tests. Mirrors <see cref="CreateEventTests"/>.
///
/// Sequences are MariaDB-only: MySQL rejects the statement with a syntax error (measured on
/// mysql:latest), which is a model-layer concern rather than a parser one, since the parser
/// has no target engine in front of it.
/// </summary>
public class CreateSequenceTests
{
    private static CreateSequenceStatement ParseOne(string text)
        => ParseAssertions.Single<CreateSequenceStatement>(new AntlrMariaDbParser().Parse(text).Statements);

    [Fact]
    public void CreateSequence_Bare_LeavesEveryOptionUnset()
    {
        var statement = ParseOne("CREATE SEQUENCE order_seq;");

        Assert.Equal("order_seq", statement.Name.Name);
        Assert.Null(statement.Increment);
        Assert.Null(statement.MinValue);
        Assert.Null(statement.MaxValue);
        Assert.Null(statement.StartValue);
        Assert.Null(statement.CacheSize);
        Assert.Null(statement.IsCycling);
    }

    [Fact]
    public void CreateSequence_Qualified_CapturesSchema()
    {
        var statement = ParseOne("CREATE SEQUENCE shop.order_seq;");

        Assert.Equal(["shop", "order_seq"], statement.Name.Segments.Select(s => s.Name));
        Assert.Equal("order_seq", statement.Name.Name);
    }

    [Fact]
    public void CreateSequence_AllOptions()
    {
        var statement = ParseOne(
            "CREATE SEQUENCE s INCREMENT BY 5 MINVALUE 10 MAXVALUE 1000 "
            + "START WITH 20 CACHE 50 CYCLE;");

        Assert.Equal(5, statement.Increment);
        Assert.Equal(10, statement.MinValue);
        Assert.Equal(1000, statement.MaxValue);
        Assert.Equal(20, statement.StartValue);
        Assert.Equal(50, statement.CacheSize);
        Assert.True(statement.IsCycling);
    }

    /// <summary>
    /// Every option has an '=' spelling and most have a keyword-less one. They are the same
    /// option to the server, so they must reach the same syntax tree.
    /// </summary>
    [Theory]
    [InlineData("CREATE SEQUENCE s INCREMENT BY 5;")]
    [InlineData("CREATE SEQUENCE s INCREMENT = 5;")]
    [InlineData("CREATE SEQUENCE s INCREMENT 5;")]
    public void CreateSequence_IncrementSpellings_AreEquivalent(string sql)
        => Assert.Equal(5, ParseOne(sql).Increment);

    [Theory]
    [InlineData("CREATE SEQUENCE s START WITH 7;")]
    [InlineData("CREATE SEQUENCE s START = 7;")]
    [InlineData("CREATE SEQUENCE s START 7;")]
    public void CreateSequence_StartSpellings_AreEquivalent(string sql)
        => Assert.Equal(7, ParseOne(sql).StartValue);

    [Theory]
    [InlineData("CREATE SEQUENCE s MINVALUE 3;")]
    [InlineData("CREATE SEQUENCE s MINVALUE = 3;")]
    public void CreateSequence_MinValueSpellings_AreEquivalent(string sql)
        => Assert.Equal(3, ParseOne(sql).MinValue);

    /// <summary>
    /// A descending sequence cannot be expressed: <c>sequenceSpec</c> takes a
    /// <c>decimalLiteral</c>, which admits no sign, and the minus is not a separate token
    /// there either. The server accepts <c>INCREMENT BY -1</c> (measured), so this is a gap in
    /// the vendored grammar rather than in MariaDB, and closing it needs an upstream change.
    ///
    /// <para>
    /// Pinned as a parse error on purpose. Failing the build is the right outcome while the
    /// construct cannot be represented: the alternative is dropping the sign and deploying an
    /// <em>ascending</em> sequence, which is a silently wrong schema rather than a stopped
    /// build. When the grammar gains a signed literal this test is what will fail and say so.
    /// </para>
    /// </summary>
    [Fact]
    public void CreateSequence_NegativeIncrement_IsAGrammarGapAndFailsToParse()
    {
        var exception = Assert.Throws<MariaDbParseException>(
            () => new AntlrMariaDbParser().Parse("CREATE SEQUENCE s INCREMENT BY -1;"));

        Assert.Contains("INCREMENT BY -", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// NOCACHE and CACHE 0 are the same thing to the server (measured: both report
    /// cache_size 0), so both parse to a cache size of zero rather than one to zero and the
    /// other to "unset".
    /// </summary>
    [Theory]
    [InlineData("CREATE SEQUENCE s NOCACHE;")]
    [InlineData("CREATE SEQUENCE s CACHE 0;")]
    public void CreateSequence_NoCache_IsZero(string sql)
        => Assert.Equal(0, ParseOne(sql).CacheSize);

    [Theory]
    [InlineData("CREATE SEQUENCE s CYCLE;", true)]
    [InlineData("CREATE SEQUENCE s NOCYCLE;", false)]
    public void CreateSequence_CycleSpellings(string sql, bool expected)
        => Assert.Equal(expected, ParseOne(sql).IsCycling);

    /// <summary>
    /// NO MINVALUE / NO MAXVALUE ask for the type default, which is what an omitted clause
    /// already means (measured: a sequence declaring them is byte-identical to a bare one).
    /// They therefore leave the bound unset rather than recording a sentinel.
    /// </summary>
    [Theory]
    [InlineData("CREATE SEQUENCE s NO MINVALUE;")]
    [InlineData("CREATE SEQUENCE s NOMINVALUE;")]
    public void CreateSequence_NoMinValue_LeavesBoundUnset(string sql)
        => Assert.Null(ParseOne(sql).MinValue);

    [Theory]
    [InlineData("CREATE SEQUENCE s NO MAXVALUE;")]
    [InlineData("CREATE SEQUENCE s NOMAXVALUE;")]
    public void CreateSequence_NoMaxValue_LeavesBoundUnset(string sql)
        => Assert.Null(ParseOne(sql).MaxValue);

    [Fact]
    public void CreateSequence_IfNotExists_Parses()
        => Assert.Equal("s", ParseOne("CREATE SEQUENCE IF NOT EXISTS s;").Name.Name);

    /// <summary>
    /// The server rejects a repeated option outright ("Option 'START' used twice in
    /// statement", measured), so this is invalid SQL rather than a last-wins case. The parser
    /// is deliberately more permissive than the engine here: rejecting it would mean encoding
    /// a server rule in the grammar, which is not ours to edit. Asserted so the leniency is a
    /// recorded decision rather than an accident, and so a future change to it is visible.
    /// </summary>
    [Fact]
    public void CreateSequence_RepeatedOption_IsAcceptedByTheParserAndTakesTheLast()
        => Assert.Equal(9, ParseOne("CREATE SEQUENCE s START WITH 3 START WITH 9;").StartValue);
}
