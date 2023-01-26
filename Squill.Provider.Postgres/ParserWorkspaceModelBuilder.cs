using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres;

public class ParserWorkspaceModelBuilder : IDatabaseModelBuilder
{
    private readonly Workspace _workspace;
    private readonly IPostgresParser _postgresParser;

    public ParserWorkspaceModelBuilder(Workspace workspace, IPostgresParser postgresParser)
    {
        _workspace = workspace;
        _postgresParser = postgresParser;
    }

    public async Task<Model> ExtractModelAsync(CancellationToken cancellationToken = default)
    {
        foreach (var file in _workspace.Files)
        {
            var text = await file.ReadAllTextAsync(cancellationToken);

            var root = _postgresParser.Parse(text);
        }

        throw new NotImplementedException("Need to transform syntax into a model");
    }
}