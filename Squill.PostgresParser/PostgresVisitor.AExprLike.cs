using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    // a_expr_like
    //   : a_expr_qual_op (NOT? (LIKE | ILIKE | SIMILAR TO | BETWEEN SYMMETRIC?)
    //                     a_expr_qual_op opt_escape)?
    //   ;
    public override SyntaxNode VisitA_expr_like(PostgreSQLParser.A_expr_likeContext context)
    {
        var operands = context.a_expr_qual_op();

        if (operands.Length < 2)
        {
            return VisitA_expr_qual_op(operands[0]);
        }

        if (VisitA_expr_qual_op(operands[0]) is not Expression left
            || VisitA_expr_qual_op(operands[1]) is not Expression right)
        {
            throw new PostgresParseException(
                "Unable to parse operand of a pattern-match or range expression");
        }

        bool negated = context.NOT() is not null;

        // BETWEEN's upper bound is not in this rule: `c BETWEEN 1 AND 5` parses as
        // `(c BETWEEN 1) AND 5`, so only the lower bound is available here. Hand a partial
        // node up to VisitA_expr_and, which owns the `AND 5` and completes it.
        if (context.BETWEEN() is not null)
        {
            return new PartialBetweenExpression(
                left, right, negated, context.SYMMETRIC() is not null);
        }

        var op = MapPatternMatchOperator(context, negated);

        // ESCAPE names the character that escapes a wildcard in the pattern, so it changes
        // what the predicate matches and cannot be dropped. Only the form that has one needs
        // the dedicated node; without it a plain binary expression says everything.
        if (context.opt_escape()?.a_expr() is { } escape)
        {
            if (VisitA_expr(escape) is not Expression escapeExpression)
            {
                throw new PostgresParseException("Unable to parse ESCAPE operand");
            }

            return new LikeExpression(left, op, right, escapeExpression);
        }

        return new BinaryExpression(left, new BuiltInOperator(op), right);
    }

    private static PostgresBuiltInBinaryOperator MapPatternMatchOperator(
        PostgreSQLParser.A_expr_likeContext context, bool negated)
    {
        if (context.LIKE() is not null)
        {
            return negated
                ? PostgresBuiltInBinaryOperator.NotLike
                : PostgresBuiltInBinaryOperator.Like;
        }

        if (context.ILIKE() is not null)
        {
            return negated
                ? PostgresBuiltInBinaryOperator.NotILike
                : PostgresBuiltInBinaryOperator.ILike;
        }

        if (context.SIMILAR() is not null)
        {
            return negated
                ? PostgresBuiltInBinaryOperator.NotSimilarTo
                : PostgresBuiltInBinaryOperator.SimilarTo;
        }

        // The grammar admits no fourth alternative, so reaching here means it changed out
        // from under this visitor.
        throw new PostgresParseException(
            "Unexpected operator in a pattern-match expression");
    }
}
