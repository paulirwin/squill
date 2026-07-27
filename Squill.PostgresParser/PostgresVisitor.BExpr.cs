using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode? VisitB_expr(PostgreSQLParser.B_exprContext context)
    {
        if (context.c_expr() is { } cExpr)
        {
            return Visit(cExpr);
        }

        if (context.TYPECAST() is not null)
        {
            if (VisitB_expr(context.b_expr()[0]) is not Expression expression)
            {
                throw new PostgresParseException("Unable to parse typecast expression");
            }

            if (VisitTypename(context.typename()) is not DataType dataType)
            {
                throw new PostgresParseException("Unable to parse typecast typename");
            }

            return new TypecastExpression(expression, dataType);
        }

        // A leading sign on a single operand, e.g. DEFAULT -5 (issue #139). Checked before the
        // binary arm, which the two-operand forms take; here there is only one b_expr child.
        if (context.b_expr() is { Length: 1 } signedExpression
            && (context.MINUS() is not null || context.PLUS() is not null))
        {
            if (VisitB_expr(signedExpression[0]) is not Expression operand)
            {
                throw new PostgresParseException("Unable to parse unary expression");
            }

            var unaryOperator = context.MINUS() is not null
                ? PostgresBuiltInUnaryOperator.Negate
                : PostgresBuiltInUnaryOperator.Plus;

            return new UnaryExpression(unaryOperator, operand);
        }

        if (context.b_expr() is { Length: 2 } binaryExpression)
        {
            if (VisitB_expr(binaryExpression[0]) is not Expression left)
            {
                throw new PostgresParseException("Unable to parse left side of binary expression");
            }

            if (VisitB_expr(binaryExpression[1]) is not Expression right)
            {
                throw new PostgresParseException("Unable to parse right side of binary expression");
            }

            PostgresBuiltInBinaryOperator builtInOperator;

            if (context.CARET() is not null)
            {
                builtInOperator = PostgresBuiltInBinaryOperator.Exponentiation;
            }
            else if (context.STAR() is not null)
            {
                builtInOperator = PostgresBuiltInBinaryOperator.Multiplication;
            }
            else if (context.SLASH() is not null)
            {
                builtInOperator = PostgresBuiltInBinaryOperator.Division;
            }
            else if (context.PERCENT() is not null)
            {
                builtInOperator = PostgresBuiltInBinaryOperator.Modulo;
            }
            else if (context.PLUS() is not null)
            {
                builtInOperator = PostgresBuiltInBinaryOperator.Addition;
            }
            else if (context.MINUS() is not null)
            {
                builtInOperator = PostgresBuiltInBinaryOperator.Subtraction;
            }
            else if (context.LT() is not null)
            {
                builtInOperator = PostgresBuiltInBinaryOperator.LessThan;
            }
            else if (context.LESS_EQUALS() is not null)
            {
                builtInOperator = PostgresBuiltInBinaryOperator.LessThanEqual;
            }
            else if (context.GT() is not null)
            {
                builtInOperator = PostgresBuiltInBinaryOperator.GreaterThan;
            }
            else if (context.GREATER_EQUALS() is not null)
            {
                builtInOperator = PostgresBuiltInBinaryOperator.GreaterThanEqual;
            }
            else if (context.EQUAL() is not null)
            {
                builtInOperator = PostgresBuiltInBinaryOperator.Equal;
            }
            else if (context.NOT_EQUALS() is not null)
            {
                builtInOperator = PostgresBuiltInBinaryOperator.NotEqual;
            }
            else
            {
                throw new NotImplementedException("Other operator types not yet supported");
            }

            return new BinaryExpression(left, new BuiltInOperator(builtInOperator), right);
        }

        throw new NotImplementedException("b_expr expression alternate not yet supported");
    }
}