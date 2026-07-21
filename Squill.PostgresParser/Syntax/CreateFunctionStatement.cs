namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A <c>CREATE FUNCTION</c> statement. Functions are not modeled yet, but they share the
/// <c>createfunctionstmt</c> grammar rule with procedures, so one is parsed into this
/// marker rather than rejected outright — that way the model builder can report it as a
/// diagnostic anchored at the statement's source position, like any other unsupported
/// construct, instead of an exception with no file or line.
/// </summary>
public class CreateFunctionStatement : Statement
{
    public CreateFunctionStatement(QualifiedName name)
    {
        Name = name;
    }

    public QualifiedName Name { get; }
}
