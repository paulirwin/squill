using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitConstraintelem(PostgreSQLParser.ConstraintelemContext context)
    {
        if (context.CHECK() is not null)
        {
            if (VisitA_expr(context.a_expr()) is not Expression checkExpression)
            {
                throw new PostgresParseException("Unable to parse CHECK constraint expression");
            }

            return At(new CheckTableConstraint(checkExpression), context);
        }

        if (context.PRIMARY() is not null && context.KEY() is not null)
        {
            // PRIMARY KEY (columnlist) — the parenthesized form. The USING-index form
            // (existingindex) has no columnlist and is not yet supported.
            if (context.columnlist() is not { } columnlist)
            {
                throw new NotImplementedException("PRIMARY KEY USING INDEX form not yet supported");
            }

            return At(new PrimaryKeyTableConstraint(ParseColumnList(columnlist)), context);
        }

        if (context.UNIQUE() is not null)
        {
            // UNIQUE (columnlist) — the parenthesized form. The USING-index form
            // (existingindex) has no columnlist and is not yet supported, mirroring
            // PRIMARY KEY above.
            if (context.columnlist() is not { } uniqueColumnlist)
            {
                throw new NotImplementedException("UNIQUE USING INDEX form not yet supported");
            }

            return At(new UniqueTableConstraint(ParseColumnList(uniqueColumnlist)), context);
        }

        if (context.FOREIGN() is not null && context.KEY() is not null)
        {
            // FOREIGN KEY (cols) REFERENCES table (cols) key_match key_actions.
            // The first columnlist is the referencing columns; opt_column_list is the
            // referenced columns.
            var columns = ParseColumnList(context.columnlist());

            if (VisitQualified_name(context.qualified_name()) is not QualifiedName referencedTable)
            {
                throw new PostgresParseException("Unable to parse FOREIGN KEY target table");
            }

            var referencedColumns = ParseOptColumnList(context.column_list_());

            var (onDelete, onUpdate) = ParseKeyActions(context.key_actions());

            return At(new ForeignKeyTableConstraint(
                columns,
                referencedTable,
                referencedColumns,
                onDelete,
                onUpdate), context);
        }

        throw new NotImplementedException("Table constraint type not yet implemented");
    }
}