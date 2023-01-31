using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitA_expr_or(PostgreSQLParser.A_expr_orContext context)
    {
        if (context.OR() is null or { Length: 0 })
        {
            return VisitA_expr_and(context.a_expr_and()[0]);
        }

        return VisitBinaryExpression<PostgreSQLParser.A_expr_andContext>(
            context.children,
            VisitA_expr_and,
            op => op switch
            {
                PostgreSQLLexer.OR => PostgresBuiltInBinaryOperator.Or,
                _ => throw new PostgresParseException("Unexpected operator"),
            }
        );
    }
}