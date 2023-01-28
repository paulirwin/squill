namespace Squill.PostgresParser.Syntax;

public class CheckTableConstraint : TableConstraint
{
    public CheckTableConstraint(Expression expression)
    {
        Expression = expression;
    }

    public Expression Expression { get; }
}