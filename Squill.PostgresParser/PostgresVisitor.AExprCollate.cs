using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    // a_expr_collate : a_expr_typecast (COLLATE any_name)?
    public override SyntaxNode VisitA_expr_collate(PostgreSQLParser.A_expr_collateContext context)
    {
        var expression = VisitA_expr_typecast(context.a_expr_typecast());

        if (context.COLLATE() is null)
        {
            return expression;
        }

        if (expression is not Expression operand)
        {
            throw new PostgresParseException("Unable to parse COLLATE expression operand");
        }

        return new CollateExpression(operand, ParseAnyName(context.any_name()));
    }
}
