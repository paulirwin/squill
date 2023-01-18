using Antlr4.Runtime;
using Squill.Core;

namespace Squill.Provider.Postgres.AntlrParser;

public class AntlrParserWorkspaceModelBuilder : IDatabaseModelBuilder
{
    private readonly Workspace _workspace;

    public AntlrParserWorkspaceModelBuilder(Workspace workspace)
    {
        _workspace = workspace;
    }

    public async Task<Model> ExtractModelAsync(CancellationToken cancellationToken = default)
    {
        foreach (var file in _workspace.Files)
        {
            var text = await file.ReadAllTextAsync(cancellationToken);
            var input = new AntlrInputStream(text);
            var lexer = new PostgresLexer(input);
            var tokenStream = new CommonTokenStream(lexer);
            var parser = new PostgresParser(tokenStream);
            var visitor = new PostgresVisitor();

            parser.ErrorHandler = new BailErrorStrategy();
            
            var root = visitor.Visit(parser.root());
        }

        throw new NotImplementedException("Need to transform syntax into a model");
    }
}