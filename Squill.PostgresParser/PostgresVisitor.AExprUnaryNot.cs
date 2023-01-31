using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitA_expr_unary_not(PostgreSQLParser.A_expr_unary_notContext context)
    {
        if (VisitA_expr_isnull(context.a_expr_isnull()) is not Expression expr)
        {
            throw new PostgresParseException("Unable to parse unary NOT expression");
        }

        if (context.NOT() is null)
        {
            return expr;
        }

        return new UnaryExpression(PostgresBuiltInUnaryOperator.Not, expr);
    }
}