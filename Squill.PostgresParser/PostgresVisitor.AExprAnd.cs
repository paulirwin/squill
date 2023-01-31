using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitA_expr_and(PostgreSQLParser.A_expr_andContext context)
    {
        if (context.AND() is null or { Length: 0 })
        {
            return VisitA_expr_in(context.a_expr_in()[0]);
        }

        return VisitBinaryExpression<PostgreSQLParser.A_expr_inContext>(
            context.children,
            VisitA_expr_in,
            op => op switch
            {
                PostgreSQLLexer.AND => PostgresBuiltInBinaryOperator.And,
                _ => throw new PostgresParseException("Unexpected operator"),
            }
        );
    }
}