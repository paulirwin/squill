using Npgsql;
using Squill.Core;

namespace Squill.Provider.Postgres;

public class PostgresDatabaseProvider : IDatabaseProvider
{
    private readonly string _connectionString;

    public PostgresDatabaseProvider(string connectionString)
    {
        _connectionString = connectionString;
    }

    public Task<IDatabase> CreateTemporaryModelDatabaseAsync(CancellationToken cancellationToken)
    {
        var dbName = $"squill_model_{Guid.NewGuid():n}";

        return CreateDatabaseAsync(dbName, cancellationToken);
    }

    public async Task<IDatabase> CreateDatabaseAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name} WITH OWNER = postgres", conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        return new PostgresDatabase(_connectionString, name);
    }

    public IDatabaseModelBuilder CreateDatabaseModelBuilder(IDatabase database)
    {
        return new PostgresDatabaseModelBuilder(database);
    }

    public bool IsDependentElementType(string type)
    {
        return type == PostgresElementTypes.SqlPrimaryKeyConstraint;
    }

    public IList<Element>? GetDependentElements(Element sourceElement, Model model)
    {
        if (sourceElement.Type != PostgresElementTypes.SqlTable
            || sourceElement.Name is not string tableName)
        {
            return null;
        }

        var deps = new List<Element>();

        foreach (var pkConstraint in model.Elements.Where(i => i.Type.Equals(PostgresElementTypes.SqlPrimaryKeyConstraint)))
        {
            var definingTable = pkConstraint.GetRelationship(PostgresRelationshipNames.DefiningTable);
            var reference = definingTable?.GetReference(tableName);

            if (reference != null)
            {
                deps.Add(pkConstraint);
            }
        }
        
        return deps;
    }
}