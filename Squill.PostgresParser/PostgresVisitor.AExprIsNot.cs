using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitA_expr_is_not(PostgreSQLParser.A_expr_is_notContext context)
    {
        if (VisitA_expr_compare(context.a_expr_compare()) is not Expression expr)
        {
            throw new PostgresParseException("Unable to parse IS expression operand");
        }

        // No trailing IS clause: this is just the underlying comparison expression.
        if (context.IS() is null)
        {
            return expr;
        }

        bool negated = context.NOT() is not null;

        // IS [NOT] DISTINCT FROM is a null-safe inequality and takes a right operand, so it is
        // a binary operator rather than one of the unary IS tails below (issue #214). It is the
        // idiom a trigger WHEN clause is normally written with, since NEW/OLD may be null.
        if (context.DISTINCT() is not null)
        {
            if (context.a_expr() is not { } rightContext
                || VisitA_expr(rightContext) is not Expression right)
            {
                throw new PostgresParseException(
                    "Unable to parse IS DISTINCT FROM right operand");
            }

            var distinct = new BinaryExpression(
                expr,
                new BuiltInOperator(PostgresBuiltInBinaryOperator.IsDistinctFrom),
                right);

            // The negated form has no stored spelling of its own: measured, PostgreSQL stores
            // `a IS NOT DISTINCT FROM b` as `NOT (a IS DISTINCT FROM b)`. Building that shape
            // here means the declared and extracted spellings are already the same tree.
            return negated
                ? new UnaryExpression(
                    PostgresBuiltInUnaryOperator.Not,
                    new ParenthesizedExpression(distinct))
                : distinct;
        }

        // Only the boolean/null IS forms are supported; the remaining tails
        // (IS [NOT] OF (...), DOCUMENT, NORMALIZED) are not yet modeled and would silently
        // mis-render if we pretended otherwise.
        PostgresBuiltInUnaryOperator op;

        if (context.NULL_P() is not null)
        {
            op = negated ? PostgresBuiltInUnaryOperator.IsNotNull : PostgresBuiltInUnaryOperator.IsNullKeyword;
        }
        else if (context.TRUE_P() is not null)
        {
            op = negated ? PostgresBuiltInUnaryOperator.IsNotTrue : PostgresBuiltInUnaryOperator.IsTrue;
        }
        else if (context.FALSE_P() is not null)
        {
            op = negated ? PostgresBuiltInUnaryOperator.IsNotFalse : PostgresBuiltInUnaryOperator.IsFalse;
        }
        else if (context.UNKNOWN() is not null)
        {
            op = negated ? PostgresBuiltInUnaryOperator.IsNotUnknown : PostgresBuiltInUnaryOperator.IsUnknown;
        }
        else
        {
            throw new NotImplementedException(
                "Support for IS (NOT) OF / DOCUMENT / NORMALIZED not yet implemented");
        }

        return new UnaryExpression(op, expr);
    }
}
