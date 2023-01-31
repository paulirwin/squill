using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitA_expr_unary_qualop(PostgreSQLParser.A_expr_unary_qualopContext context)
    {
        if (context.qual_op() is not null)
        {
            throw new NotImplementedException("qual_op not yet supported");
        }

        return VisitA_expr_add(context.a_expr_add());
    }
}