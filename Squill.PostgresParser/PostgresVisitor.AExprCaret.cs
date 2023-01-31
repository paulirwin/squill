using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitA_expr_caret(PostgreSQLParser.A_expr_caretContext context)
    {
        if (context.CARET() is not null)
        {
            throw new NotImplementedException("Caret operator is not yet implemented");
        }

        return VisitA_expr_unary_sign(context.a_expr_unary_sign());
    }
}