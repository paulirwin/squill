using Antlr4.Runtime.Tree;
using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitA_expr_qual_op(PostgreSQLParser.A_expr_qual_opContext context)
    {
        if (context.qual_op() is not { Length: > 0 })
        {
            return VisitA_expr_unary_qualop(context.a_expr_unary_qualop()[0]);
        }

        // `a op b op c` — a left-associative chain of general (non-mathop) operators, e.g.
        // string concatenation (`a || b || c`) or a JSON accessor. The grammar interleaves
        // operands and operators, so walking the children in order keeps them paired up
        // without relying on the two arrays lining up positionally.
        Expression? result = null;
        CustomOperator? pendingOperator = null;

        foreach (var child in context.children)
        {
            switch (child)
            {
                case PostgreSQLParser.A_expr_unary_qualopContext operand:
                {
                    if (VisitA_expr_unary_qualop(operand) is not Expression expression)
                    {
                        throw new PostgresParseException(
                            "Unable to parse operand of an operator expression");
                    }

                    if (result is null)
                    {
                        result = expression;
                    }
                    else
                    {
                        if (pendingOperator is null)
                        {
                            throw new PostgresParseException(
                                "Unexpected parse order from an operator expression");
                        }

                        result = new BinaryExpression(result, pendingOperator, expression);
                        pendingOperator = null;
                    }

                    break;
                }

                case PostgreSQLParser.Qual_opContext qualOp:
                    pendingOperator = MapQualifiedOperator(qualOp);
                    break;

                // The grammar admits no other children here; anything else means the
                // grammar changed out from under this visitor.
                default:
                    throw new PostgresParseException(
                        $"Unexpected child of an operator expression: {child.GetType()}");
            }
        }

        if (result is null || pendingOperator is not null)
        {
            throw new PostgresParseException("Unexpected parse order from an operator expression");
        }

        return result;
    }

    // `qual_op` is either a bare operator token (`||`, `->>`, a user-defined operator) or the
    // fully-qualified `OPERATOR(schema.op)` form. Both are carried verbatim as written.
    private static CustomOperator MapQualifiedOperator(PostgreSQLParser.Qual_opContext context)
        => new(context.GetText());
}
