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

        parser.ErrorHandler = new BailErrorStrategy();
            
        var root = visitor.Visit(parser.root());

        if (root is not Root rootNode)
        {
            throw new InvalidOperationException($"Expected a Root to be returned by the Antlr visitor, got a {root?.GetType().ToString() ?? "null"} object");
        }

        return rootNode;
    }
}