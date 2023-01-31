using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitA_expr_qual_op(PostgreSQLParser.A_expr_qual_opContext context)
    {
        if (context.qual_op() is { Length: > 0 })
        {
            throw new NotImplementedException("qual_op not yet supported");
        }

        return VisitA_expr_unary_qualop(context.a_expr_unary_qualop()[0]);
    }
}