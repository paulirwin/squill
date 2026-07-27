namespace Squill.PostgresParser.Syntax;

/// <summary>
/// An <c>EXTRACT(field FROM source)</c> (issue #140). The field is a keyword or identifier
/// (<c>YEAR</c>, <c>MONTH</c>, <c>epoch</c>, …) rather than an expression, so it is carried as
/// text and cannot be modeled as an ordinary function argument.
/// </summary>
public class ExtractExpression : Expression
{
    public ExtractExpression(string field, Expression source)
    {
        Field = field;
        Source = source;
    }

    public string Field { get; }

    public Expression Source { get; }
}
