using Antlr4.Runtime;
using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public class AntlrPostgresParser : IPostgresParser
{
    public Root Parse(string text)
    {
        var input = new CaseChangingCharStream(new AntlrInputStream(text), upper: true);
        var lexer = new PostgreSQLLexer(input);
        var tokenStream = new CommonTokenStream(lexer);
        var parser = new PostgreSQLParser(tokenStream);
        var visitor = new PostgresVisitor();

        // ANTLR's default listeners print errors to the console; collect them instead so a
        // syntax error surfaces as a PostgresParseException carrying the error's 1-based
        // line/column, which hosts (e.g. the MSBuild task) report as source diagnostics.
        var errors = new SyntaxErrorCollectingListener();
        lexer.RemoveErrorListeners();
        lexer.AddErrorListener(errors);
        parser.RemoveErrorListeners();
        parser.AddErrorListener(errors);

        var tree = parser.root();

        if (errors.Errors.Count > 0)
        {
            // Report the first error: with recovery a single mistake often cascades, so
            // later entries are usually noise. The visitor must not run over a tree with
            // error nodes — it would fail on the missing pieces with worse messages.
            var (message, line, column) = errors.Errors[0];

            throw new PostgresParseException($"Syntax error: {message}", line, column);
        }

        var root = visitor.Visit(tree);

        if (root is not Root rootNode)
        {
            throw new InvalidOperationException($"Expected a Root to be returned by the Antlr visitor, got a {root?.GetType().ToString() ?? "null"} object");
        }

        return rootNode;
    }
}
