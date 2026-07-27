using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    // a_expr_between : a_expr_in (NOT? BETWEEN SYMMETRIC? a_expr_in AND a_expr_in)?
    //
    // The rule carries both bounds, so the `AND` here belongs to the BETWEEN rather than
    // being a boolean conjunction — which matches how PostgreSQL's own grammar treats it
    // (`a_expr BETWEEN opt_asymmetric b_expr AND a_expr %prec BETWEEN`).
    public override SyntaxNode VisitA_expr_between(PostgreSQLParser.A_expr_betweenContext context)
    {
        var operands = context.a_expr_in();

        if (context.BETWEEN() is null)
        {
            return VisitA_expr_in(operands[0]);
        }

        if (VisitA_expr_in(operands[0]) is not Expression operand
            || VisitA_expr_in(operands[1]) is not Expression lower
            || VisitA_expr_in(operands[2]) is not Expression upper)
        {
            throw new PostgresParseException("Unable to parse operand of a BETWEEN expression");
        }

        return new BetweenExpression(
            operand,
            lower,
            upper,
            context.NOT() is not null,
            context.SYMMETRIC() is not null);
    }
}
