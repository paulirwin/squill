using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    // a_expr_and : a_expr_in (AND a_expr_in)*
    //
    // Ordinarily a left-associative AND chain, but this rule also has to finish a BETWEEN.
    // The grammar's a_expr_like takes only one right operand, so `c BETWEEN 1 AND 5` parses
    // as `(c BETWEEN 1) AND 5` and the upper bound arrives here as the next conjunct.
    //
    // So a conjunct is consumed as a bound whenever the operand immediately to its left is an
    // unfinished BETWEEN — which, in `a BETWEEN 1 AND 2 AND b BETWEEN 3 AND 4`, is not the
    // accumulated left side but the most recent operand. Tracking that operand separately is
    // what keeps each BETWEEN taking exactly its own bound.
    public override SyntaxNode VisitA_expr_and(PostgreSQLParser.A_expr_andContext context)
    {
        var operands = context.a_expr_in();

        if (VisitA_expr_in(operands[0]) is not Expression first)
        {
            throw new PostgresParseException("Unable to parse AND expression operand");
        }

        // The conjuncts settled so far, and the trailing operand that a following conjunct
        // could still complete. `pending` is null once it has been folded into `result`.
        Expression? result = first is PartialBetweenExpression ? null : first;
        var pending = first as PartialBetweenExpression;

        for (var i = 1; i < operands.Length; i++)
        {
            if (VisitA_expr_in(operands[i]) is not Expression next)
            {
                throw new PostgresParseException("Unable to parse AND expression operand");
            }

            if (pending is not null)
            {
                // This operand is the BETWEEN's upper bound, not a conjunct of its own.
                result = Conjoin(result, pending.WithUpper(next));
                pending = null;
                continue;
            }

            if (next is PartialBetweenExpression partial)
            {
                // Its bound is the next operand; hold it until then.
                pending = partial;
                continue;
            }

            result = Conjoin(result, next);
        }

        if (pending is not null || result is null)
        {
            // A BETWEEN whose AND never arrived: `WHERE c BETWEEN 1`. Postgres rejects this
            // too; failing here keeps an incomplete node from reaching the model.
            throw new PostgresParseException(
                "BETWEEN is missing its upper bound; expected AND after the lower bound");
        }

        return result;
    }

    private static Expression Conjoin(Expression? left, Expression right)
        => left is null
            ? right
            : new BinaryExpression(
                left,
                new BuiltInOperator(PostgresBuiltInBinaryOperator.And),
                right);
}
