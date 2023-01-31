using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitA_expr_is_not(PostgreSQLParser.A_expr_is_notContext context)
    {
        if (context.IS() is not null)
        {
            throw new NotImplementedException("Support for IS (NOT) not yet implemented");
        }

        return VisitA_expr_compare(context.a_expr_compare());
    }
}