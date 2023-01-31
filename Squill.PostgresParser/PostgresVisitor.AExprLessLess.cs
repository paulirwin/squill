using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitA_expr_lessless(PostgreSQLParser.A_expr_lesslessContext context)
    {
        if (context.LESS_LESS() is null or { Length: 0 }
            && context.GREATER_GREATER() is null or { Length: 0 })
        {
            return VisitA_expr_or(context.a_expr_or()[0]);
        }

        return VisitBinaryExpression<PostgreSQLParser.A_expr_orContext>(
            context.children,
            VisitA_expr_or,
            op => op switch
            {
                PostgreSQLLexer.LESS_LESS => PostgresBuiltInBinaryOperator.LeftShift,
                PostgreSQLLexer.GREATER_GREATER => PostgresBuiltInBinaryOperator.RightShift,
                _ => throw new PostgresParseException("Unexpected operator"),
            }
        );
    }
}