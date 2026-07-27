namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A typed string literal — the <c>type 'literal'</c> spelling, e.g. <c>interval '1 day'</c>
/// or <c>timestamp '2020-01-01'</c> (issue #141).
///
/// The type prefix is part of the constant's meaning, not decoration: <c>interval '1 day'</c>
/// and the bare string <c>'1 day'</c> are different values. It is carried here so the literal
/// renders back out with its type, rather than silently degrading to an untyped string.
/// </summary>
public class TypedLiteralExpression : Expression
{
    public TypedLiteralExpression(string typeName, LiteralExpression literal, string? modifier = null)
    {
        TypeName = typeName;
        Literal = literal;
        Modifier = modifier;
    }

    /// <summary>The type name exactly as written, e.g. <c>interval</c>.</summary>
    public string TypeName { get; }

    public LiteralExpression Literal { get; }

    /// <summary>
    /// A trailing interval qualifier such as <c>DAY</c> or <c>YEAR TO MONTH</c>, as written,
    /// or <c>null</c> when there is none.
    /// </summary>
    public string? Modifier { get; }
}
