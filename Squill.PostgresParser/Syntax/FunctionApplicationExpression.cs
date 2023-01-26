namespace Squill.PostgresParser.Syntax;

public class FunctionApplicationExpression : Expression
{
    public FunctionApplicationExpression(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public IList<FunctionArgument> Arguments { get; } = new List<FunctionArgument>();
}