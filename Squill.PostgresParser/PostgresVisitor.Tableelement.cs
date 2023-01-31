using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitTableelement(PostgreSQLParser.TableelementContext context)
    {
        if (context.columnDef() is { } columnDefContext)
        {
            return VisitColumnDef(columnDefContext);
        }

        if (context.tableconstraint() is { } tableconstraint)
        {
            return VisitTableconstraint(tableconstraint);
        }

        throw new NotImplementedException("Table LIKE elements not yet supported");
    }
}