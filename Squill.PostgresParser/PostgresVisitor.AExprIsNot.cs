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

        // Only the boolean/null IS forms are supported; the more exotic tails
        // (IS [NOT] DISTINCT FROM, IS [NOT] OF (...), DOCUMENT, NORMALIZED) are not
        // yet modeled and would silently mis-render if we pretended otherwise.
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
                "Support for IS (NOT) DISTINCT FROM / OF / DOCUMENT / NORMALIZED not yet implemented");
        }

        return new UnaryExpression(op, expr);
    }
}
