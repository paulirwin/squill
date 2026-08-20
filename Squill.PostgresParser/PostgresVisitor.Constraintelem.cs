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
                    WithIndexOptions(
                        new PrimaryKeyTableConstraint(ParseColumnList(columnlist)), context),
                    context),
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
                    WithIndexOptions(
                        new UniqueTableConstraint(ParseColumnList(uniqueColumnlist)), context),
                    context),
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
                onUpdate)
            {
                MatchType = ParseKeyMatch(context.key_match()),
            }, context), context);
        }

        if (context.EXCLUDE() is not null)
        {
            // EXCLUDE access_method_clause? (exclusionconstraintlist) c_include_? definition_?
            //   optconstablespace? exclusionwhereclause? constraintattributespec
            //
            // The alternative parsed all along; the visitor simply had no branch for it, so a
            // table declaring one hit the terminal throw below and could not be built at all
            // (issue #212).
            var elements = context.exclusionconstraintlist().exclusionconstraintelem()
                .Select(ParseExclusionConstraintElement);

            var exclusion = new ExclusionTableConstraint(elements);

            // USING <method>. Optional here and in the engine; measured, an omitted method is
            // reported back as `USING btree`, so the absence is carried as null and defaulted
            // by the model layer rather than being invented here.
            if (context.access_method_clause()?.name() is { } accessMethodName)
            {
                if (VisitName(accessMethodName) is not Identifier accessMethod)
                {
                    throw new PostgresParseException(
                        "Unable to parse the access method of an EXCLUDE constraint");
                }

                exclusion.AccessMethod = accessMethod;
            }

            // WHERE (predicate) restricts which rows participate. Unlike a CHECK it rejects
            // nothing itself, so it is kept distinct from one.
            if (context.exclusionwhereclause()?.a_expr() is { } whereExpr)
            {
                if (Visit(whereExpr) is not Expression whereClause)
                {
                    throw new PostgresParseException(
                        "Unable to parse the WHERE predicate of an EXCLUDE constraint");
                }

                exclusion.WhereClause = whereClause;
            }

            return At(
                WithConstraintAttributes(WithIndexOptions(exclusion, context), context),
                context);
        }

        throw new NotImplementedException("Table constraint type not yet implemented");
    }

    /// <summary>
    /// Reads the index-shaped clauses a PRIMARY KEY, UNIQUE or EXCLUDE constraint accepts
    /// alongside its key columns (issues #210 and #212): <c>c_include_</c>, <c>definition_</c>
    /// and <c>optconstablespace</c>. All three parsed and were then discarded, so the same
    /// declaration behaved differently depending on whether it was written as a constraint or
    /// as a CREATE INDEX -- which already reads all three. EXCLUDE shares them because the
    /// grammar hangs the same three off its alternative too, and it is likewise index-backed.
    ///
    /// <c>nulls_distinct</c> is deliberately not read here: the grammar threads it into
    /// <c>indexstmt</c> only, so the constraint spelling does not parse at all and there is
    /// nothing to read (issue #187, blocked on a grammar re-vendor).
    /// </summary>
    private T WithIndexOptions<T>(T constraint, PostgreSQLParser.ConstraintelemContext context)
        where T : TableConstraint, IIndexBackedTableConstraint
    {
        // INCLUDE (cols). Plain column names here, unlike CREATE INDEX's include_, which reuses
        // index_elem -- so these parse through ParseColumnList rather than the index visitor.
        if (context.c_include_()?.columnlist() is { } includeColumns)
        {
            foreach (var column in ParseColumnList(includeColumns))
            {
                constraint.IncludeColumns.Add(column);
            }
        }

        // WITH (name = value, ...). definition_ : WITH definition, and definition's def_elem is
        // `colLabel (EQUAL def_arg)?` -- the same name/value shape CREATE INDEX's reloption_elem
        // has, minus the namespaced `ns.option` alternative, so there is no namespace case to
        // reject here.
        if (context.definition_()?.definition()?.def_list() is { } defList)
        {
            foreach (var defElem in defList.def_elem())
            {
                constraint.WithOptions.Add(new IndexWithOption(
                    defElem.colLabel().GetText(), defElem.def_arg()?.GetText()));
            }
        }

        // USING INDEX TABLESPACE name.
        if (context.optconstablespace()?.name() is { } tablespaceName)
        {
            if (VisitName(tablespaceName) is not Identifier tablespace)
            {
                throw new PostgresParseException(
                    "Unable to parse USING INDEX TABLESPACE name for constraint");
            }

            constraint.TableSpace = tablespace;
        }

        return constraint;
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
            // Each alternative is gated on its own distinguishing keyword rather than on NOT,
            // which two of them share: NOT DEFERRABLE and NOT VALID mean unrelated things, so
            // testing NOT alone would read one as the other.
            if (elem.DEFERRABLE() is not null)
            {
                deferrable = elem.NOT() is null;
            }
            else if (elem.INITIALLY() is not null)
            {
                initiallyDeferred = elem.DEFERRED() is not null;
            }
            else if (elem.VALID() is not null)
            {
                // NOT VALID (issue #205). Carried so the provider can warn SQ1002; not modeled,
                // because CREATE TABLE accepts and ignores it -- see TableConstraint.IsNotValid.
                constraint.IsNotValid = true;
            }
            else if (elem.INHERIT() is not null && constraint is CheckTableConstraint check)
            {
                // NO INHERIT (issue #205). The table-level CHECK alternative has no
                // no_inherit_ of its own, so this list is the only route by which it arrives.
                // PostgreSQL accepts it on CHECK constraints only.
                check.IsNoInherit = true;
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

    /// <summary>
    /// One <c>index_elem WITH operator</c> pair of an EXCLUDE constraint (issue #212).
    ///
    /// The key half reuses <see cref="VisitIndex_elem"/>, since the grammar's
    /// <c>exclusionconstraintelem</c> is literally <c>index_elem WITH ...</c> -- an exclusion
    /// key accepts an expression, an operator class, a collation and an ordering exactly as an
    /// index key does.
    /// </summary>
    private ExclusionConstraintElement ParseExclusionConstraintElement(
        PostgreSQLParser.ExclusionconstraintelemContext context)
    {
        if (VisitIndex_elem(context.index_elem()) is not IndexElement key)
        {
            throw new PostgresParseException("Unable to parse the key of an EXCLUDE constraint");
        }

        // Two spellings reach the same place: the bare `WITH =` and the explicit
        // `WITH OPERATOR(schema.=)`, which exists so an operator shadowed by another schema's
        // can be named unambiguously. The grammar gives the second its own any_operator, so
        // whichever is present is the one to read.
        var anyOperator = context.any_operator();

        if (anyOperator is null)
        {
            throw new PostgresParseException(
                "Unable to parse the operator of an EXCLUDE constraint element");
        }

        return At(new ExclusionConstraintElement(key, ParseAnyOperator(anyOperator)), context);
    }

    /// <summary>
    /// <c>any_operator : (colid DOT)* all_op</c> -- an operator name, optionally qualified by
    /// the schema it lives in.
    /// </summary>
    private QualifiedName ParseAnyOperator(PostgreSQLParser.Any_operatorContext context)
    {
        var segments = new List<Identifier>();

        foreach (var colid in context.colid())
        {
            if (VisitColid(colid) is not Identifier segment)
            {
                throw new PostgresParseException("Unable to parse an operator's schema name");
            }

            segments.Add(segment);
        }

        // The operator token itself is punctuation, not an identifier, so it is taken as
        // written rather than run through the identifier rules -- quoting or case-folding `&&`
        // would change what it means.
        segments.Add(new SimpleIdentifier(context.all_op().GetText()));

        return At(new QualifiedName(segments), context);
    }
}
