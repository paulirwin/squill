using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    // array_expr      : OPEN_BRACKET (expr_list | array_expr_list)? CLOSE_BRACKET
    // array_expr_list : array_expr (COMMA array_expr)*
    //
    // Both an element list (`ARRAY[1, 2]`) and a nested one (`ARRAY[[1, 2], [3, 4]]`) map to
    // the same node, since a nested array is just an array whose elements are arrays.
    public override SyntaxNode VisitArray_expr(PostgreSQLParser.Array_exprContext context)
    {
        if (context.expr_list() is { } exprList)
        {
            return new ArrayExpression(
                exprList.a_expr().Select(RequireExpression).ToArray());
        }

        if (context.array_expr_list() is { } nested)
        {
            return new ArrayExpression(
                nested.array_expr()
                    .Select(i => VisitArray_expr(i) as Expression
                                 ?? throw new PostgresParseException(
                                     "Unable to parse nested array element"))
                    .ToArray());
        }

        // `ARRAY[]` — legal in the grammar, and legal to Postgres only with a cast
        // (`ARRAY[]::int[]`). Modeled as the empty array it is rather than refused.
        return new ArrayExpression([]);
    }
}
