namespace Squill.Provider.Postgres;

public static class PostgresElementTypes
{
    // TODO.PI: move common ones to the Core assembly
    public const string SqlTable = nameof(SqlTable);
    public const string SqlSimpleColumn = nameof(SqlSimpleColumn);
    public const string SqlTypeSpecifier = nameof(SqlTypeSpecifier);
    public const string SqlPrimaryKeyConstraint = nameof(SqlPrimaryKeyConstraint);
    public const string SqlIndexedColumnSpecification = nameof(SqlIndexedColumnSpecification);
}