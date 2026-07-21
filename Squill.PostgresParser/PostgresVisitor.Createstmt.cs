using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitCreatestmt(PostgreSQLParser.CreatestmtContext context)
    {
        // TODO: support opttemp
        // TODO: support if not exists
        // TODO: support OF and PARTITION OF

        if (context.qualified_name().Length == 0)
        {
            throw new PostgresParseException("Expected CREATE TABLE statement to have a qualified name");
        }

        if (VisitQualified_name(context.qualified_name()[0]) is not QualifiedName qualifiedName)
        {
            throw new PostgresParseException("Unable to parse qualified name for CREATE TABLE statement");
        }

        if (context.opttableelementlist() is not { } opttableelementlist)
        {
            throw new NotImplementedException("OF and PARTITION OF not yet supported for CREATE TABLE statements");
        }

        var createTable = At(new CreateTableStatement(qualifiedName), context);

        if (opttableelementlist.tableelementlist() is { } tableelementlist)
        {
            foreach (var tableelementContext in tableelementlist.tableelement())
            {
                var tableElementNode = VisitTableelement(tableelementContext);

                if (tableElementNode is not ITableElement tableElement)
                {
                    throw new PostgresParseException("Unable to parse table element");
                }

                createTable.Elements.Add(tableElement);
            }
        }

        if (context.optinherit()?.INHERITS() is not null)
        {
            foreach (var inheritQualName in context.optinherit().qualified_name_list().qualified_name())
            {
                if (VisitQualified_name(inheritQualName) is not QualifiedName inherits)
                {
                    throw new PostgresParseException("Unable to parse table INHERITS clause");
                }

                createTable.Inherits.Add(inherits);
            }
        }

        return createTable;
    }
}