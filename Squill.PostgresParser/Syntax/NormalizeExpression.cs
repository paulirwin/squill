namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A <c>NORMALIZE(text [, form])</c> Unicode normalization (issue #140). The form is one of
/// the bare keywords <c>NFC</c> / <c>NFD</c> / <c>NFKC</c> / <c>NFKD</c> — not a string
/// literal — so it is carried as text rather than as an argument expression.
/// </summary>
public class NormalizeExpression : Expression
{
    public NormalizeExpression(Expression expression, string? form)
    {
        Expression = expression;
        Form = form;
    }

    public Expression Expression { get; }

    /// <summary>The normalization form keyword, or <c>null</c> when omitted (Postgres uses NFC).</summary>
    public string? Form { get; }
}
