namespace Squill.PostgresParser.Syntax;

public class DefaultColumnConstraint : ColumnConstraint
{
    public DefaultColumnConstraint(string text, Expression expression) 
        : base(text)
    {
        Expression = expression;
    }

    public Expression Expression { get; }
}