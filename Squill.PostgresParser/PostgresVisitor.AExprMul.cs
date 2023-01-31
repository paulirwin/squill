using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitA_expr_mul(PostgreSQLParser.A_expr_mulContext context)
    {
        if (context.a_expr_caret() is { Length: 1 })
        {
            return VisitA_expr_caret(context.a_expr_caret()[0]);
        }

        return VisitBinaryExpression<PostgreSQLParser.A_expr_caretContext>(
            context.children,
            VisitA_expr_caret,
            op => op switch
            {
                PostgreSQLLexer.STAR => PostgresBuiltInBinaryOperator.Multiplication,
                PostgreSQLLexer.SLASH => PostgresBuiltInBinaryOperator.Division,
                PostgreSQLLexer.PERCENT => PostgresBuiltInBinaryOperator.Modulo,
                _ => throw new PostgresParseException("Unexpected operator"),
            }
        );
    }
}