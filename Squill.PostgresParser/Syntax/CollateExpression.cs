namespace Squill.PostgresParser.Syntax;

/// <summary>
/// An <c>expr COLLATE collation</c> (issue #141), which applies a collation to its operand.
///
/// Distinct from <see cref="CollationForExpression"/>, which is the <c>COLLATION FOR (expr)</c>
/// function that *reads* an operand's collation rather than applying one.
/// </summary>
public class CollateExpression : Expression
{
    public CollateExpression(Expression expression, QualifiedName collation)
    {
        Expression = expression;
        Collation = collation;
    }

    public Expression Expression { get; }

    public QualifiedName Collation { get; }
}
