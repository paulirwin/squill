namespace Squill.Provider.Postgres;

public static class PostgresElementTypes
{
    // TODO.PI: move common ones to the Core assembly
    public const string SqlTable = nameof(SqlTable);
    public const string SqlSimpleColumn = nameof(SqlSimpleColumn);
    public const string SqlTypeSpecifier = nameof(SqlTypeSpecifier);
    public const string SqlPrimaryKeyConstraint = nameof(SqlPrimaryKeyConstraint);
    public const string SqlIndexedColumnSpecification = nameof(SqlIndexedColumnSpecification);
    public const string SqlIndex = nameof(SqlIndex);
    public const string SqlForeignKeyConstraint = nameof(SqlForeignKeyConstraint);
    public const string SqlExtension = nameof(SqlExtension);
    public const string SqlSchema = nameof(SqlSchema);
    public const string SqlProcedure = nameof(SqlProcedure);
}