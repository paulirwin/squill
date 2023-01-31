using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitA_expr_in(PostgreSQLParser.A_expr_inContext context)
    {
        if (context.IN_P() is null)
        {
            return VisitA_expr_unary_not(context.a_expr_unary_not());
        }

        bool not = context.NOT() is not null;

        if (VisitA_expr_unary_not(context.a_expr_unary_not()) is not Expression left)
        {
            throw new PostgresParseException("Unable to parse IN expression left operand");
        }

        // NOTE: using base Visit method because of named in_expr branches
        // TODO: should we assert this is a more specific type i.e. InExpression?
        if (Visit(context.in_expr()) is not Expression right)
        {
            throw new PostgresParseException("Unable to parse IN expression right operand");
        }

        return new BinaryExpression(
            left,
            new BuiltInOperator(not ? PostgresBuiltInBinaryOperator.NotIn : PostgresBuiltInBinaryOperator.In),
            right
        );
    }
}