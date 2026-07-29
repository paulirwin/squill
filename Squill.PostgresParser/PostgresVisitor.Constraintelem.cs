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

            return At(WithConstraintAttributes(new CheckTableConstraint(checkExpression), context),
                context);
        }

        if (context.PRIMARY() is not null && context.KEY() is not null)
        {
            // Two spellings: PRIMARY KEY (columnlist), or PRIMARY KEY USING INDEX ix, which
            // promotes an existing unique index and so declares no columns of its own. The
            // latter is carried but not modeled — the provider warns (issue #143).
            if (context.columnlist() is not { } columnlist)
            {
                return At(WithConstraintAttributes(new PrimaryKeyTableConstraint([])
                {
                    UsingIndex = ParseExistingIndexName(context.existingindex()),
                }, context), context);
            }

            return At(
                WithConstraintAttributes(
                    new PrimaryKeyTableConstraint(ParseColumnList(columnlist)), context),
                context);
        }

        if (context.UNIQUE() is not null)
        {
            // UNIQUE (columnlist) or UNIQUE USING INDEX ix, mirroring PRIMARY KEY above.
            if (context.columnlist() is not { } uniqueColumnlist)
            {
                return At(WithConstraintAttributes(new UniqueTableConstraint([])
                {
                    UsingIndex = ParseExistingIndexName(context.existingindex()),
                }, context), context);
            }

            return At(
                WithConstraintAttributes(
                    new UniqueTableConstraint(ParseColumnList(uniqueColumnlist)), context),
                context);
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

            return At(WithConstraintAttributes(new ForeignKeyTableConstraint(
                columns,
                referencedTable,
                referencedColumns,
                onDelete,
                onUpdate), context), context);
        }

        throw new NotImplementedException("Table constraint type not yet implemented");
    }

    /// <summary>
    /// Applies the trailing <c>constraintattributespec</c> — the DEFERRABLE / INITIALLY clauses
    /// every <c>constraintelem</c> alternative ends in (issue #160). Reading it is what stops a
    /// table-level <c>DEFERRABLE INITIALLY DEFERRED</c> from being parsed and then dropped.
    /// </summary>
    private static T WithConstraintAttributes<T>(T constraint,
        PostgreSQLParser.ConstraintelemContext context)
        where T : TableConstraint
    {
        // constraintattributespec : constraintattributeElem* — it matches empty, so a
        // constraint with no attributes yields a present-but-childless context, not a null one.
        if (context.constraintattributespec() is not { } spec)
        {
            return constraint;
        }

        bool? deferrable = null;
        bool? initiallyDeferred = null;

        foreach (var elem in spec.constraintattributeElem())
        {
            // The rule also carries NOT VALID and NO INHERIT, which are not deferrability at
            // all. Gate on the DEFERRABLE / INITIALLY keywords rather than on NOT, or the NOT
            // of NOT VALID reads as NOT DEFERRABLE.
            if (elem.DEFERRABLE() is not null)
            {
                deferrable = elem.NOT() is null;
            }
            else if (elem.INITIALLY() is not null)
            {
                initiallyDeferred = elem.DEFERRED() is not null;
            }
        }

        // INITIALLY DEFERRED implies DEFERRABLE: PostgreSQL rejects pairing it with NOT
        // DEFERRABLE, and pg_constraint reports condeferrable = true for it. Collapsing the
        // implication here means both spellings hand the model builder the same answer.
        constraint.IsDeferrable = deferrable ?? initiallyDeferred ?? false;
        constraint.IsInitiallyDeferred = initiallyDeferred ?? false;

        return constraint;
    }

    // existingindex : USING INDEX name
    private Identifier ParseExistingIndexName(PostgreSQLParser.ExistingindexContext? context)
    {
        if (context?.name() is not { } name)
        {
            throw new PostgresParseException(
                "Expected a PRIMARY KEY / UNIQUE constraint to declare either a column list or "
                + "USING INDEX");
        }

        if (VisitName(name) is not Identifier indexName)
        {
            throw new PostgresParseException("Unable to parse USING INDEX index name");
        }

        return indexName;
    }
}