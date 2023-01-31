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

        throw new NotImplementedException("Support for func_expr_common_subexpr not yet implemented");
    }
}