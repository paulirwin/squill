namespace Squill.PostgresParser.Syntax;

/// <summary>
/// An operator that is not one of the fixed <see cref="PostgresBuiltInBinaryOperator"/>
/// tokens the grammar spells out — it arrives as a generic <c>Operator</c> token. This
/// covers PostgreSQL's open-ended operator vocabulary: string concatenation (<c>||</c>),
/// the JSON accessors (<c>-&gt;</c>, <c>-&gt;&gt;</c>, <c>#&gt;</c>), pattern and range
/// operators, and any operator a user or extension defines.
///
/// The symbol is carried verbatim so it can be rendered back out unchanged; Squill never
/// needs to interpret its semantics, only reproduce the expression.
/// </summary>
public class CustomOperator : Operator
{
    public CustomOperator(string symbol)
    {
        Symbol = symbol;
    }

    /// <summary>
    /// The operator exactly as written in the source, e.g. <c>||</c>.
    /// </summary>
    public string Symbol { get; }
}
