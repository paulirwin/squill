using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    // a_expr_and : a_expr_between (AND a_expr_between)*
    public override SyntaxNode VisitA_expr_and(PostgreSQLParser.A_expr_andContext context)
    {
        if (context.AND() is null or { Length: 0 })
        {
            return VisitA_expr_between(context.a_expr_between()[0]);
        }

        return VisitBinaryExpression<PostgreSQLParser.A_expr_betweenContext>(
            context.children,
            VisitA_expr_between,
            op => op switch
            {
                PostgreSQLLexer.AND => PostgresBuiltInBinaryOperator.And,
                _ => throw new PostgresParseException("Unexpected operator"),
            }
        );
    }
}
