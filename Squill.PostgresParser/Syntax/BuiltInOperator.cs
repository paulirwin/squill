namespace Squill.PostgresParser.Syntax;

public class BuiltInOperator : Operator
{
    public BuiltInOperator(PostgresBuiltInBinaryOperator @operator)
    {
        Operator = @operator;
    }

    public PostgresBuiltInBinaryOperator Operator { get; }
}