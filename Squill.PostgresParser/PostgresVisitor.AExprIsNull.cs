using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitA_expr_isnull(PostgreSQLParser.A_expr_isnullContext context)
    {
        if (VisitA_expr_is_not(context.a_expr_is_not()) is not Expression expr)
        {
            throw new PostgresParseException("Unable to parse ISNULL/NOTNULL expression");
        }

        PostgresBuiltInUnaryOperator op;

        if (context.ISNULL() is not null)
        {
            op = PostgresBuiltInUnaryOperator.IsNull;
        }
        else if (context.NOTNULL() is not null)
        {
            op = PostgresBuiltInUnaryOperator.NotNull;
        }
        else
        {
            return expr;
        }

        return new UnaryExpression(op, expr);
    }
}