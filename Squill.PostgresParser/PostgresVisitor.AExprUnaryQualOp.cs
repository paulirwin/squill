using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    // a_expr_unary_qualop : qual_op? a_expr_add
    //
    // A prefix application of a general operator — one outside the fixed sign tokens handled
    // by a_expr_unary_sign, e.g. absolute value (`@`) or a user-defined operator.
    public override SyntaxNode VisitA_expr_unary_qualop(PostgreSQLParser.A_expr_unary_qualopContext context)
    {
        var expression = VisitA_expr_add(context.a_expr_add());

        if (context.qual_op() is not { } qualOp)
        {
            return expression;
        }

        if (expression is not Expression operand)
        {
            throw new PostgresParseException("Unable to parse unary operator expression operand");
        }

        return new CustomUnaryExpression(MapQualifiedOperator(qualOp), operand);
    }
}
