namespace Squill.PostgresParser.Syntax;

public class LiteralExpression : Expression
{
    public LiteralExpression(string text, object value)
        : this(text, value, IntegerLiteralRadix.Decimal)
    {
    }

    public LiteralExpression(string text, object value, IntegerLiteralRadix radix)
    {
        Text = text;
        Value = value;
        Radix = radix;
    }

    public string Text { get; }

    public object Value { get; }

    /// <summary>
    /// The base this literal was written in. <see cref="IntegerLiteralRadix.Decimal"/> for
    /// ordinary integers and for every literal that is not an integer, so reading this never
    /// requires establishing the literal's kind first.
    ///
    /// <para>
    /// The value is unaffected by the radix — <c>0x19</c> carries the same <see cref="Value"/> as
    /// <c>25</c>. What differs is the spelling, which is what <see cref="Text"/> preserves and
    /// what PostgreSQL 16 introduced, so this is the signal the target-version check reads
    /// (issue #191).
    /// </para>
    /// </summary>
    public IntegerLiteralRadix Radix { get; }
}
