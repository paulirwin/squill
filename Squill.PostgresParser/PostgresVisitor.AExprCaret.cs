using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    // a_expr_caret : a_expr_unary_sign (CARET a_expr)?
    //
    // The right operand is a full a_expr rather than the next tier down, so the tail
    // over-captures: `c ^ 2 > 4` arrives as `c ^ (2 > 4)`. Postgres binds `^` tighter than a
    // comparison, so the operand is rebalanced back into the correct association.
    public override SyntaxNode VisitA_expr_caret(PostgreSQLParser.A_expr_caretContext context)
    {
        var expression = VisitA_expr_unary_sign(context.a_expr_unary_sign());

        if (context.CARET() is null)
        {
            return expression;
        }

        if (expression is not Expression left)
        {
            throw new PostgresParseException("Unable to parse exponentiation left operand");
        }

        if (VisitA_expr(context.a_expr()) is not Expression right)
        {
            throw new PostgresParseException("Unable to parse exponentiation right operand");
        }

        return RebalanceRightOperand(
            right,
            exponent => new BinaryExpression(
                left,
                new BuiltInOperator(PostgresBuiltInBinaryOperator.Exponentiation),
                exponent));
    }
}
