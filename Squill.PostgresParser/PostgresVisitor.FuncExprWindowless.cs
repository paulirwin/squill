using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    /// <summary>
    /// <c>func_expr_windowless</c> is <c>func_expr</c> minus the window clauses; it is the
    /// form used where a window makes no sense (an index expression, a generated column, a
    /// default). Its two alternatives are the same as <c>func_expr</c>'s, so it dispatches the
    /// same way rather than relying on the base visitor's default child walk.
    /// </summary>
    public override SyntaxNode VisitFunc_expr_windowless(
        PostgreSQLParser.Func_expr_windowlessContext context)
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
