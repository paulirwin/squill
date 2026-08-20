using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitIndex_elem(PostgreSQLParser.Index_elemContext context)
    {
        // index_elem_options : collate_? class_? asc_desc_? nulls_order_?
        //                    | collate_? any_name reloptions asc_desc_? nulls_order_?
        // Each clause is optional, so an absent one is a null context rather than a rule
        // matching empty.
        var options = context.index_elem_options();

        IndexElementDirection? direction = null;

        if (options.asc_desc_() is { } ascDesc)
        {
            direction = ascDesc.ASC() is not null
                ? IndexElementDirection.Asc
                : IndexElementDirection.Desc;
        }

        IndexElementNullOrder? nullOrder = null;

        if (options.nulls_order_() is { } nullsOrder)
        {
            nullOrder = nullsOrder.FIRST_P() is not null
                ? IndexElementNullOrder.NullsFirst
                : IndexElementNullOrder.NullsLast;
        }

        // A per-key COLLATE precedes the operator class in index_elem_options. It was the one
        // clause of the four this visitor never read (issue #160), so an index silently sorted
        // by the column's own collation rather than the declared one.
        QualifiedName? collation = null;

        if (options.collate_()?.any_name() is { } collationName)
        {
            collation = ParseAnyName(collationName);
        }

        // An operator class (e.g. vector_cosine_ops) may follow the column. A user may
        // schema-qualify it (pg_catalog.text_pattern_ops) to disambiguate one shadowed by
        // another schema's.
        //
        // It reaches us by either alternative of index_elem_options. Alternative 1 spells it
        // class_; alternative 2 is the parameterized form (PostgreSQL 13+), where the same name
        // arrives as a bare any_name carrying a reloptions payload. Only the first was read
        // before (issue #211), so a parameterized key lost the parameters *and* the class name
        // itself, which is the half that makes the emitted DDL undeployable: measured,
        // PostgreSQL rejects `gist (tsv (siglen=256))` with "column siglen does not exist".
        QualifiedName? operatorClass = null;
        var operatorClassParameters = new List<IndexWithOption>();

        if (options.class_()?.any_name() is { } opClassName)
        {
            operatorClass = ParseAnyName(opClassName);
        }
        else if (options.any_name() is { } parameterizedClassName)
        {
            operatorClass = ParseAnyName(parameterizedClassName);

            if (options.reloptions()?.reloption_list() is { } reloptionList)
            {
                AddStorageParameters(reloptionList, operatorClassParameters);
            }
        }

        Expression expr;

        if (context.colid() is { } colid)
        {
            if (VisitColid(colid) is not Identifier colidIdentifier)
            {
                throw new PostgresParseException("Unable to parse column identifier");
            }

            expr = new ColumnReferenceExpression(colidIdentifier);
        }
        else if (context.func_expr_windowless() is { } funcExprWindowless)
        {
            // A bare call — CREATE INDEX ix ON people (lower(name)) — is an expression index.
            // Its parenthesized spelling, ((lower(name))), takes the a_expr alternative below
            // instead, so the two arrive here by different routes (issue #160).
            if (Visit(funcExprWindowless) is not Expression funcExpr)
            {
                throw new PostgresParseException(
                    "Unable to parse function expression in CREATE INDEX statement");
            }

            expr = funcExpr;
        }
        else if (context.a_expr() is { } aExprContext)
        {
            if (Visit(aExprContext) is not Expression aExpr)
            {
                throw new PostgresParseException("Unable to parse expression in CREATE INDEX statement");
            }

            expr = aExpr;
        }
        else
        {
            throw new InvalidOperationException("Unexpected alternate for index element");
        }

        return new IndexElement(
            expr, direction, nullOrder, operatorClass, collation, operatorClassParameters);
    }
}