using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitA_expr_like(PostgreSQLParser.A_expr_likeContext context)
    {
        if (context.LIKE() is not null
            || context.ILIKE() is not null
            || context.SIMILAR() is not null
            || context.BETWEEN() is not null)
        {
            throw new NotImplementedException("LIKE/ILIKE/SIMILAR/BETWEEN not yet supported");
        }

        return VisitA_expr_qual_op(context.a_expr_qual_op()[0]);
    }
}