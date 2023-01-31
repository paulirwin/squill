using Antlr4.Runtime.Tree;
using Squill.PostgresParser.Syntax;
using Expression = Squill.PostgresParser.Syntax.Expression;

// ReSharper disable StringLiteralTypo
// ReSharper disable CommentTypo
// ReSharper disable IdentifierTypo

namespace Squill.PostgresParser;

public partial class PostgresVisitor : PostgreSQLParserBaseVisitor<SyntaxNode?>
{
    public override SyntaxNode VisitRoot(PostgreSQLParser.RootContext context)
    {
        var root = new Root();
        
        foreach (var stmtContext in context.stmtblock().stmtmulti().stmt())
        {
            var stmt = VisitStmt(stmtContext);

            if (stmt is not Statement statement)
            {
                throw new PostgresParseException("Expected VisitStmt to return a Statement");
            }
            
            root.Statements.Add(statement);
        }

        return root;
    }

    private static BinaryExpression VisitBinaryExpression<TNextContext>(
        IEnumerable<IParseTree> children, 
        Func<TNextContext, SyntaxNode?> visitFunc,
        Func<int, PostgresBuiltInBinaryOperator> opLookup)
    {
        var parts = new Queue<object>();

        foreach (var child in children)
        {
            if (child is TNextContext nextExpr)
            {
                if (visitFunc(nextExpr) is not Expression expr)
                {
                    throw new PostgresParseException("Unable to parse binary expression operand");
                }

                parts.Enqueue(expr);
            }
            else if (child is ITerminalNode lexerNode)
            {
                var op = opLookup(lexerNode.Symbol.Type);
                parts.Enqueue(op);   
            }
            else
            {
                throw new PostgresParseException($"Unexpected child of binary operator: {child.GetType()}");
            }
        }

        if (parts.Count < 3)
        {
            throw new PostgresParseException("Somehow ended up with less than two expressions and one operator for a binary operator");
        }

        if (parts.Dequeue() is not Expression startLeft
            || parts.Dequeue() is not PostgresBuiltInBinaryOperator startOp
            || parts.Dequeue() is not Expression startRight)
        {
            throw new PostgresParseException("Unexpected parse order from binary expression");
        }

        var binary = new BinaryExpression(
            startLeft,
            new BuiltInOperator(startOp),
            startRight);

        while (parts.TryDequeue(out var nextPart))
        {
            if (nextPart is not PostgresBuiltInBinaryOperator nextOp
                || !parts.TryDequeue(out var nextNextPart)
                || nextNextPart is not Expression nextExpr)
            {
                throw new PostgresParseException("Unexpected parse order from binary expression");
            }
            
            binary = new BinaryExpression(
                binary, 
                new BuiltInOperator(nextOp),
                nextExpr);
        }
        
        return binary;
    }
}