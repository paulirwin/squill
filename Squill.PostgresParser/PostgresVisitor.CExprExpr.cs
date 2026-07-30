using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitC_expr_expr(PostgreSQLParser.C_expr_exprContext context)
    {
        if (context.func_expr() is { } funcExpr)
        {
            return VisitFunc_expr(funcExpr);
        }

        if (context.aexprconst() is { } aexprconst)
        {
            return VisitAexprconst(aexprconst);
        }

        if (context.columnref() is { } columnref)
        {
            return VisitColumnref(columnref);
        }

        // ARRAY[...] — the form an IN predicate is stored as (issue #170). Checked before the
        // a_expr branch below: `ARRAY` binds its own operand, so a parenthesized expression
        // inside one must not be mistaken for the whole alternative.
        if (context.ARRAY() is not null)
        {
            if (context.array_expr() is not { } arrayExpr)
            {
                // The only other ARRAY alternative is `ARRAY (SELECT ...)`, whose value
                // depends on other rows and so cannot be compared against a target schema.
                throw new NotImplementedException(
                    "ARRAY (SELECT ...) is not supported: a subquery's value cannot be "
                    + "compared against the target schema. Use an explicit element list.");
            }

            return VisitArray_expr(arrayExpr);
        }

        if (context.a_expr() is { } aExpr)
        {
            if (VisitA_expr(aExpr) is not Expression expression)
            {
                throw new PostgresParseException("Unable to parse parenthesized expression");
            }

            // `(c).x`, `(a)[1]` — field selection or subscripting on the parenthesized
            // expression. The parentheses belong to the accessor here rather than being
            // grouping, so this is not wrapped in a ParenthesizedExpression.
            if (context.opt_indirection()?.indirection_el() is { Length: > 0 } indirection)
            {
                return new IndirectionExpression(
                    expression,
                    indirection.Select(SourceText).ToList());
            }

            return new ParenthesizedExpression(expression);
        }

        throw new NotImplementedException("c_expr_expr expression alternate not yet supported");
    }
}