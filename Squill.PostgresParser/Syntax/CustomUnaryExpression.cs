namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A prefix application of an operator outside the fixed <see cref="PostgresBuiltInUnaryOperator"/>
/// set — a user-defined or extension operator, or one of the built-ins the grammar carries as a
/// generic operator token such as absolute value (<c>@</c>) (issue #141).
///
/// Distinct from <see cref="UnaryExpression"/>, whose operator is one the parser recognizes by
/// name; here the symbol is carried verbatim so it renders back out exactly as written.
/// </summary>
public class CustomUnaryExpression : Expression
{
    public CustomUnaryExpression(CustomOperator op, Expression expression)
    {
        Operator = op;
        Expression = expression;
    }

    public CustomOperator Operator { get; }

    public Expression Expression { get; }
}
