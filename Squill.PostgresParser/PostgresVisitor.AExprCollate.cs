using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitA_expr_collate(PostgreSQLParser.A_expr_collateContext context)
    {
        if (context.COLLATE() is not null)
        {
            throw new NotImplementedException("COLLATE not yet supported");
        }

        return VisitA_expr_typecast(context.a_expr_typecast());
    }
}