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

        // The base Visit is used because in_expr has named branches; the list branch maps to
        // an ArrayExpression and the subquery branch refuses (issue #170).
        //
        // The IN spelling is kept here rather than rewritten into `= ANY (ARRAY[…])`: the
        // parser's job is to preserve what was written, and folding the two spellings together
        // here would lose the source form. The engine's rewrite is applied by the expression
        // normalizer instead, which is where measured engine behaviour belongs.
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