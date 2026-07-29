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
                else if (colconstraint.COLLATE() is not null
                         && colconstraint.any_name() is { } collationName)
                {
                    // `COLLATE any_name` is a colconstraint alternative of its own rather than a
                    // colconstraintelem, so it never reaches VisitColconstraintelem (issue #159).
                    columnDef.Constraints.Add(At(
                        new CollateColumnConstraint(colconstraint.GetText(), ParseAnyName(collationName)),
                        colconstraint));
                }
                else if (colconstraint.constraintattr() is { } constraintattr)
                {
                    columnDef.Constraints.Add(ParseConstraintAttribute(constraintattr));
                }
                else
                {
                    throw new PostgresParseException(
                        $"Unrecognized column constraint '{colconstraint.GetText()}'");
                }
            }
        }
    }

    /// <summary>
    /// Parses a <c>constraintattr</c>: <c>DEFERRABLE</c>, <c>NOT DEFERRABLE</c>,
    /// <c>INITIALLY DEFERRED</c> or <c>INITIALLY IMMEDIATE</c>. Each is a separate alternative,
    /// so a node states one facet and leaves the other null.
    /// </summary>
    private ConstraintAttributeColumnConstraint ParseConstraintAttribute(
        PostgreSQLParser.ConstraintattrContext context)
    {
        if (context.DEFERRABLE() is not null)
        {
            return At(new ConstraintAttributeColumnConstraint(
                context.GetText(), deferrable: context.NOT() is null, initiallyDeferred: null), context);
        }

        if (context.INITIALLY() is not null)
        {
            return At(new ConstraintAttributeColumnConstraint(
                context.GetText(), deferrable: null,
                initiallyDeferred: context.DEFERRED() is not null), context);
        }

        throw new PostgresParseException(
            $"Unrecognized constraint attribute '{context.GetText()}'");
    }
}