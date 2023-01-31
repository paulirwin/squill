using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitC_expr_expr(PostgreSQLParser.C_expr_exprContext context)
    {
        if (context.func_expr() is { } funcExpr)
        {
            return VisitFunc_expr(funcExpr);
        }

        if (context.aexprconst() is { } aexprconst)
        {
            return VisitAexprconst(aexprconst);
        }

        if (context.columnref() is { } columnref)
        {
            return VisitColumnref(columnref);
        }

        if (context.a_expr() is { } aExpr)
        {
            if (context.opt_indirection()?.ChildCount is > 0)
            {
                throw new NotImplementedException("Indirection after parenthesized expressions not yet supported");
            }

            if (VisitA_expr(aExpr) is not Expression expression)
            {
                throw new PostgresParseException("Unable to parse parenthesized expression");
            }

            return new ParenthesizedExpression(expression);
        }

        throw new NotImplementedException("c_expr_expr expression alternate not yet supported");
    }
}