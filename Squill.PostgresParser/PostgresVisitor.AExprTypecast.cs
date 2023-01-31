using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitA_expr_typecast(PostgreSQLParser.A_expr_typecastContext context)
    {
        if (context.TYPECAST() is null or { Length: 0 })
        {
            return Visit(context.c_expr()) ?? throw new PostgresParseException("Unable to parse expression");
        }

        TypecastExpression? expression = null;

        foreach (var typename in context.typename())
        {
            if (VisitTypename(typename) is not DataType dataType)
            {
                throw new PostgresParseException("Unable to parse data type for typecast");
            }

            if (expression != null)
            {
                expression = new TypecastExpression(expression, dataType);
            }
            else
            {
                var startExprNode = Visit(context.c_expr());

                if (startExprNode is not Expression startExpression)
                {
                    throw new PostgresParseException("Unable to parse expression for typecast");
                }

                expression = new TypecastExpression(startExpression, dataType);
            }
        }

        if (expression == null)
        {
            throw new PostgresParseException("Unable to parse typecast expression");
        }

        return expression;
    }
}