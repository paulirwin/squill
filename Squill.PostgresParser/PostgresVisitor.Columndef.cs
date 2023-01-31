using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitColumnDef(PostgreSQLParser.ColumnDefContext context)
    {
        if (VisitColid(context.colid()) is not Identifier name)
        {
            throw new PostgresParseException("Unable to parse column name");
        }

        if (VisitTypename(context.typename()) is not DataType dataType)
        {
            throw new PostgresParseException("Unable to parse table element data type");
        }

        // TODO: support OPTIONS

        var columnDef = new ColumnDefinition(name, dataType);

        if (context.colquallist() is { } colquallist
            && colquallist.colconstraint() is { Length: > 0 } colconstraints)
        {
            foreach (var colconstraint in colconstraints)
            {
                if (colconstraint.CONSTRAINT() is not null
                    && colconstraint.name() is { } nameContext)
                {
                    if (VisitColconstraintelem(colconstraint.colconstraintelem()) is not ColumnConstraint
                        innerConstraint)
                    {
                        throw new PostgresParseException(
                            "Expected VisitColconstraintelem to return a ColumnConstraint");
                    }

                    // TODO: support quoted identifiers properly etc. instead of just calling nameContext.GetText()
                    columnDef.Constraints.Add(new NamedColumnConstraint(colconstraint.GetText(), nameContext.GetText(),
                        innerConstraint));
                }
                else if (colconstraint.colconstraintelem() is { } colconstraintelem)
                {
                    if (VisitColconstraintelem(colconstraintelem) is not ColumnConstraint columnConstraint)
                    {
                        throw new PostgresParseException(
                            "Expected VisitColconstraintelem to return a ColumnConstraint");
                    }

                    columnDef.Constraints.Add(columnConstraint);
                }
                else
                {
                    // TODO: support these constraint types
                    throw new NotImplementedException("DEFERRABLE, DEFERRED, IMMEDIATE, and COLLATE not yet supported");
                }
            }
        }

        return columnDef;
    }
}