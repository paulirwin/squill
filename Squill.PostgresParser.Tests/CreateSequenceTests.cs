using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// Standalone <c>CREATE SEQUENCE</c> parsing (issue #122). Distinct from the sequence options
/// on an identity column, which produce an <see cref="IdentityColumnConstraint"/>: this is the
/// first-class, independently named sequence object.
/// </summary>
public class CreateSequenceTests
{
    private static CreateSequenceStatement ParseOne(string text)
    {
        var root = new AntlrPostgresParser().Parse(text);

        return Assert.IsType<CreateSequenceStatement>(Assert.Single(root.Statements));
    }

    [Fact]
    public void CreateSequence_BareName_HasNoOptions()
    {
        var statement = ParseOne("CREATE SEQUENCE order_number;");

        Assert.Equal("order_number", statement.Name.ToString());
        Assert.Null(statement.StartValue);
        Assert.Null(statement.Increment);
        Assert.Null(statement.MinValue);
        Assert.Null(statement.MaxValue);
        Assert.Null(statement.CacheSize);
        Assert.Null(statement.IsCycling);
        Assert.Null(statement.DataType);
    }

    [Fact]
    public void CreateSequence_SchemaQualified()
    {
        var statement = ParseOne("CREATE SEQUENCE inventory.order_number;");

        Assert.Equal("inventory.order_number", statement.Name.ToString());
    }

    [Fact]
    public void CreateSequence_AllOptions()
    {
        var statement = ParseOne(
            """
            CREATE SEQUENCE order_number
                AS integer
                START WITH 100
                INCREMENT BY 5
                MINVALUE 10
                MAXVALUE 5000
                CACHE 20
                CYCLE;
            """);

        Assert.Equal(100, statement.StartValue);
        Assert.Equal(5, statement.Increment);
        Assert.Equal(10, statement.MinValue);
        Assert.Equal(5000, statement.MaxValue);
        Assert.Equal(20, statement.CacheSize);
        Assert.True(statement.IsCycling);
        var dataType = Assert.IsType<BuiltInDataType>(statement.DataType);
        Assert.Equal(PostgresBuiltInDataType.Integer, dataType.Type);
    }

    // START/INCREMENT accept the optional WITH/BY noise words; both spellings must parse the
    // same, since a user may write either.
    [Fact]
    public void CreateSequence_OptionalNoiseWordsAreEquivalent()
    {
        var withNoise = ParseOne("CREATE SEQUENCE s START WITH 7 INCREMENT BY 3;");
        var without = ParseOne("CREATE SEQUENCE s START 7 INCREMENT 3;");

        Assert.Equal(withNoise.StartValue, without.StartValue);
        Assert.Equal(withNoise.Increment, without.Increment);
        Assert.Equal(7, without.StartValue);
        Assert.Equal(3, without.Increment);
    }

    // A descending sequence is spelled with a negative increment, so the sign must survive.
    [Fact]
    public void CreateSequence_NegativeIncrement()
    {
        var statement = ParseOne("CREATE SEQUENCE countdown INCREMENT -1;");

        Assert.Equal(-1, statement.Increment);
    }

    // NO MINVALUE / NO MAXVALUE select the default bound — the same meaning as omitting the
    // option — so they are parsed and discarded. NO CYCLE is the default too, but is recorded
    // explicitly as false so an explicitly non-cycling sequence is distinguishable from one
    // that said nothing (the model builder treats both alike; the parser stays faithful).
    [Fact]
    public void CreateSequence_NoOptions()
    {
        var statement = ParseOne("CREATE SEQUENCE s NO MINVALUE NO MAXVALUE NO CYCLE;");

        Assert.Null(statement.MinValue);
        Assert.Null(statement.MaxValue);
        Assert.False(statement.IsCycling);
    }

    [Fact]
    public void CreateSequence_IfNotExists()
    {
        var statement = ParseOne("CREATE SEQUENCE IF NOT EXISTS order_number;");

        Assert.True(statement.IfNotExists);
        Assert.Equal("order_number", statement.Name.ToString());
    }

    // A temporary sequence is session-scoped, so it can never be part of a declared schema.
    [Fact]
    public void CreateSequence_Temporary_IsRejected()
    {
        var parser = new AntlrPostgresParser();

        Assert.ThrowsAny<Exception>(() => parser.Parse("CREATE TEMPORARY SEQUENCE s;"));
    }

    // OWNED BY ties the sequence's lifetime to a column, which is exactly how the sequence
    // behind a serial column is created. Squill cannot tell such a sequence apart from a
    // declared one when reading the catalog, so it is rejected in source with a message that
    // points at the alternative rather than silently deploying something that never converges.
    [Fact]
    public void CreateSequence_OwnedBy_IsRejectedWithActionableMessage()
    {
        var parser = new AntlrPostgresParser();

        var ex = Assert.ThrowsAny<Exception>(
            () => parser.Parse("CREATE SEQUENCE s OWNED BY t.c;"));

        Assert.Contains("OWNED BY", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // RESTART is a runtime operation on an existing sequence, not part of a declaration.
    [Fact]
    public void CreateSequence_Restart_IsRejected()
    {
        var parser = new AntlrPostgresParser();

        Assert.ThrowsAny<Exception>(() => parser.Parse("CREATE SEQUENCE s RESTART WITH 5;"));
    }
}
