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
    public const string SqlView = nameof(SqlView);
    public const string SqlViewColumn = nameof(SqlViewColumn);
    // User-defined types (issue #75): a CREATE TYPE ... AS ENUM and a CREATE DOMAIN. Both are
    // top-level, standalone, declared objects a column's type may reference.
    public const string SqlEnumType = nameof(SqlEnumType);
    public const string SqlDomain = nameof(SqlDomain);
    // A CREATE FUNCTION (issue #81). Like a procedure, but with a return type and
    // volatility/strictness; both live in pg_proc, distinguished by prokind.
    public const string SqlFunction = nameof(SqlFunction);
}