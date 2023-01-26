namespace Squill.PostgresParser.Syntax;

public class LiteralExpression : Expression
{
    public LiteralExpression(string text, object value)
    {
        Text = text;
        Value = value;
    }

    public string Text { get; }
    
    public object Value { get; }
}