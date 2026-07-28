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

        AddColumnConstraints(columnDef, context.colquallist());

        return columnDef;
    }

    /// <summary>
    /// Parses a <c>colquallist</c> onto a column. Shared by an ordinary <c>columnDef</c> and a
    /// typed table's <c>columnOptions</c>, which carries constraints but no type.
    /// </summary>
    private void AddColumnConstraints(ColumnDefinition columnDef, PostgreSQLParser.ColquallistContext? context)
    {
        if (context is { } colquallist
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
                    // COLLATE and DEFERRABLE / INITIALLY DEFERRED land here. Tracked by
                    // issue #159, not #143.
                    throw new NotImplementedException("DEFERRABLE, DEFERRED, IMMEDIATE, and COLLATE not yet supported");
                }
            }
        }
    }
}