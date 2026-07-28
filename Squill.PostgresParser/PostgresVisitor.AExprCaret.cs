using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    // a_expr_caret : a_expr_unary_sign (CARET a_expr_unary_sign)?
    //
    // The right operand is the tier below rather than a full a_expr, so the rule no longer
    // over-captures and `c ^ 2 > 4` associates as PostgreSQL does — `(c ^ 2) > 4`.
    public override SyntaxNode VisitA_expr_caret(PostgreSQLParser.A_expr_caretContext context)
    {
        var operands = context.a_expr_unary_sign();

        if (context.CARET() is null)
        {
            return VisitA_expr_unary_sign(operands[0]);
        }

        if (VisitA_expr_unary_sign(operands[0]) is not Expression left
            || VisitA_expr_unary_sign(operands[1]) is not Expression right)
        {
            throw new PostgresParseException("Unable to parse exponentiation operand");
        }

        return new BinaryExpression(
            left,
            new BuiltInOperator(PostgresBuiltInBinaryOperator.Exponentiation),
            right);
    }
}
