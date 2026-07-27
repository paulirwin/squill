using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitFunc_expr(PostgreSQLParser.Func_exprContext context)
    {
        if (context.func_application() is { } funcApplication)
        {
            return VisitFunc_application(funcApplication);
        }

        if (context.func_expr_common_subexpr() is { } commonSubexpr)
        {
            return VisitFunc_expr_common_subexpr(commonSubexpr);
        }

        throw new PostgresParseException("Unable to parse function expression");
    }
}