namespace Squill.Core;

public class TemporaryDatabaseModelBuilder
{
    private readonly IDatabaseProvider _provider;

    public TemporaryDatabaseModelBuilder(IDatabaseProvider provider)
    {
        _provider = provider;
    }

    public async Task<Model> BuildModelAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        await using var database = await _provider.CreateTemporaryModelDatabaseAsync(cancellationToken);

        await database.ConnectAsync(cancellationToken);

        var modelBuilder = _provider.CreateDatabaseModelBuilder(database);

        Model model;
        
        try
        {
            foreach (var file in workspace.Files.Where(i => i.Kind == FileKind.Compile))
            {
                var sql = await file.ReadAllTextAsync(cancellationToken);

                await database.RunScriptAsync(sql, cancellationToken: cancellationToken);
            }
            
            model = await modelBuilder.ExtractModelAsync(cancellationToken);
        }
        finally
        {
            await database.DropAsync(cancellationToken);
        }

        return model;
    }
}