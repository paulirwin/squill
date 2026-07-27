using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    /// <summary>
    /// Repairs the operand of a grammar rule that recurses to a full <c>a_expr</c> on its
    /// right and so captures more than it should (issue #141).
    ///
    /// Both <c>a_expr_caret</c> (<c>a_expr_unary_sign (CARET a_expr)?</c>) and
    /// <c>a_expr_at_time_zone</c> (<c>a_expr_collate (AT TIME ZONE a_expr)?</c>) are written
    /// this way, so <c>c ^ 2 &gt; 4</c> parses as <c>c ^ (2 &gt; 4)</c> — the tail swallows the
    /// lower-precedence comparison that should have stayed above it. Postgres binds both
    /// <c>^</c> and <c>AT TIME ZONE</c> tighter than a comparison, so the parse tree is simply
    /// wrong, and left alone would render back out as a differently-meaning predicate.
    ///
    /// The shape is recoverable without touching the grammar: the operand that truly belongs
    /// to the tight operator is the leftmost leaf of the over-captured subtree, so this splits
    /// that leaf off, hands it to <paramref name="buildTight"/>, and grafts the result back
    /// into the leaf's place — rotating the tree into the association Postgres would have
    /// produced.
    /// </summary>
    private static Expression RebalanceRightOperand(
        Expression captured, Func<Expression, Expression> buildTight)
    {
        // Nothing was over-captured: the tail is a single operand already.
        if (captured is not BinaryExpression binary)
        {
            return buildTight(captured);
        }

        // Only a binary chain is descended into. A parenthesized operand arrives as a
        // ParenthesizedExpression and so returns above — explicit grouping the author wrote
        // binds as written and must not be taken apart.
        return new BinaryExpression(
            RebalanceRightOperand(binary.Left, buildTight),
            binary.Operator,
            binary.Right);
    }
}
