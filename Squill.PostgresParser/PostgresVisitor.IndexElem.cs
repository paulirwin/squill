using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitIndex_elem(PostgreSQLParser.Index_elemContext context)
    {
        IndexElementDirection? direction = null;

        if (context.index_elem_options().opt_asc_desc().ASC() is not null)
        {
            direction = IndexElementDirection.Asc;
        }
        else if (context.index_elem_options().opt_asc_desc().DESC() is not null)
        {
            direction = IndexElementDirection.Desc;
        }

        IndexElementNullOrder? nullOrder = null;

        if (context.index_elem_options().opt_nulls_order().FIRST_P() is not null)
        {
            nullOrder = IndexElementNullOrder.NullsFirst;
        }
        else if (context.index_elem_options().opt_nulls_order().LAST_P() is not null)
        {
            nullOrder = IndexElementNullOrder.NullsLast;
        }

        // An operator class (e.g. vector_cosine_ops) may follow the column. The grammar
        // exposes it as opt_class within index_elem_options; only the plain opt_class
        // form (not the reloptions form) is supported here.
        Identifier? operatorClass = null;

        if (context.index_elem_options().opt_class()?.any_name() is { } opClassName)
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