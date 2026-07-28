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

        // An operator class (e.g. vector_cosine_ops) may follow the column. The grammar
        // exposes it as class_ within index_elem_options; only the plain class_ form (not
        // the reloptions form) is supported here.
        Identifier? operatorClass = null;

        if (options.class_()?.any_name() is { } opClassName)
        {
            if (opClassName.attrs() is not null)
            {
                throw new NotImplementedException("Schema-qualified index operator classes are not yet supported");
            }

            if (VisitColid(opClassName.colid()) is not Identifier opClassIdentifier)
            {
                throw new PostgresParseException("Unable to parse index operator class");
            }

            operatorClass = opClassIdentifier;
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
            throw new NotImplementedException(
                "Support for function expressions in CREATE INDEX statements not yet implemented");
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

        return new IndexElement(expr, direction, nullOrder, operatorClass);
    }
}