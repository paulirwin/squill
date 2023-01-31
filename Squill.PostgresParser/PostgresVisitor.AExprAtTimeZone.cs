using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitA_expr_at_time_zone(PostgreSQLParser.A_expr_at_time_zoneContext context)
    {
        if (context.AT() is not null)
        {
            throw new NotImplementedException("AT TIME ZONE not yet supported");
        }

        return VisitA_expr_collate(context.a_expr_collate());
    }
}