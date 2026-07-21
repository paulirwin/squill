using Antlr4.Runtime;
using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    // Stamps a node with the 1-based line/column where its source context starts, so later
    // phases (model building, reference validation) can report diagnostics that point back
    // into the source file (issue #53).
    private static T At<T>(T node, ParserRuleContext context) where T : SyntaxNode
    {
        node.Line = context.Start.Line;
        node.Column = context.Start.Column + 1;
        return node;
    }
}
