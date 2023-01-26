namespace Squill.PostgresParser.Syntax;

public class FunctionArgument
{
    public FunctionArgument(Expression expression, string? paramName = null)
    {
        Expression = expression;
        ParamName = paramName;
    }

    public string? ParamName { get; }
    
    public Expression Expression { get; }
}