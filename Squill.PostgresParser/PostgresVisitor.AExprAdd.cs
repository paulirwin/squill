using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitA_expr_add(PostgreSQLParser.A_expr_addContext context)
    {
        if (context.a_expr_mul() is { Length: 1 })
        {
            return VisitA_expr_mul(context.a_expr_mul()[0]);
        }

        return VisitBinaryExpression<PostgreSQLParser.A_expr_mulContext>(
            context.children,
            VisitA_expr_mul,
            op => op switch
            {
                PostgreSQLLexer.MINUS => PostgresBuiltInBinaryOperator.Subtraction,
                PostgreSQLLexer.PLUS => PostgresBuiltInBinaryOperator.Addition,
                _ => throw new PostgresParseException("Unexpected operator"),
            }
        );
    }
}