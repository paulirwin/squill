using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    // in_expr
    //   : select_with_parens               # in_expr_select
    //   | OPEN_PAREN expr_list CLOSE_PAREN # in_expr_list
    //
    // The list form is mapped to an ArrayExpression, because that is the shape PostgreSQL
    // stores an IN predicate as — `q IN (1, 2)` comes back from pg_get_constraintdef as
    // `q = ANY (ARRAY[1, 2])` (measured). Carrying the operands as an array rather than as a
    // distinct "IN list" node is what lets the two spellings reduce to one canonical token
    // (issue #170).
    public override SyntaxNode VisitIn_expr_list(PostgreSQLParser.In_expr_listContext context)
        => new ArrayExpression(
            context.expr_list().a_expr().Select(RequireExpression).ToArray());

    // The subquery form. A predicate whose meaning depends on other rows cannot be compared
    // against a target schema, so it is refused rather than mapped to something that would
    // look comparable — matching how the visitor treats every other unmapped construct.
    public override SyntaxNode VisitIn_expr_select(PostgreSQLParser.In_expr_selectContext context)
        => throw new NotImplementedException(
            "IN (SELECT ...) is not supported: a subquery predicate cannot be compared against "
            + "the target schema. Use an explicit value list.");
}
