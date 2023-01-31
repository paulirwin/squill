using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitA_expr_compare(PostgreSQLParser.A_expr_compareContext context)
    {
        if (context.subquery_Op() is not null)
        {
            throw new NotImplementedException("Subquery_op not yet supported for compare expression");
        }

        if (context.a_expr_like() is { Length: 1 })
        {
            return VisitA_expr_like(context.a_expr_like()[0]);
        }

        PostgresBuiltInBinaryOperator op;

        if (context.LT() is not null)
        {
            op = PostgresBuiltInBinaryOperator.LessThan;
        }
        else if (context.GT() is not null)
        {
            op = PostgresBuiltInBinaryOperator.GreaterThan;
        }
        else if (context.EQUAL() is not null)
        {
            op = PostgresBuiltInBinaryOperator.Equal;
        }
        else if (context.LESS_EQUALS() is not null)
        {
            op = PostgresBuiltInBinaryOperator.LessThanEqual;
        }
        else if (context.GREATER_EQUALS() is not null)
        {
            op = PostgresBuiltInBinaryOperator.GreaterThanEqual;
        }
        else if (context.NOT_EQUALS() is not null)
        {
            op = PostgresBuiltInBinaryOperator.NotEqual;
        }
        else
        {
            throw new PostgresParseException("Unexpected binary operator in compare expression");
        }

        if (VisitA_expr_like(context.a_expr_like()[0]) is not Expression left
            || VisitA_expr_like(context.a_expr_like()[1]) is not Expression right)
        {
            throw new PostgresParseException("Unable to parse left or right side of compare expression");
        }

        return new BinaryExpression(
            left,
            new BuiltInOperator(op),
            right
        );
    }
}