using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitA_expr_unary_sign(PostgreSQLParser.A_expr_unary_signContext context)
    {
        if (context.MINUS() is null && context.PLUS() is null)
        {
            return VisitA_expr_at_time_zone(context.a_expr_at_time_zone());
        }

        PostgresBuiltInUnaryOperator op;

        if (context.MINUS() is not null)
        {
            op = PostgresBuiltInUnaryOperator.Negate;
        }
        else if (context.PLUS() is not null)
        {
            op = PostgresBuiltInUnaryOperator.Plus;
        }
        else
        {
            throw new PostgresParseException("Unexpected unary operator");
        }

        if (VisitA_expr_at_time_zone(context.a_expr_at_time_zone()) is not Expression expr)
        {
            throw new PostgresParseException("Unable to parse unary expression");
        }

        return new UnaryExpression(op, expr);
    }
}