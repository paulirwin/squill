namespace Squill.Provider.MariaDb;

/// <summary>
/// The element <see cref="Squill.Core.Element.Type"/> discriminators for the MariaDB
/// provider. These mirror the SSDT-style element type names shared across providers so
/// a DACPAC model stays provider-neutral; MariaDB simply omits the Postgres-only
/// <c>SqlSchema</c> / <c>SqlExtension</c> types, since a MariaDB "schema" is the database
/// itself and it has no extension concept.
/// </summary>
public static class MariaDbElementTypes
{
    public const string SqlTable = nameof(SqlTable);
    public const string SqlSimpleColumn = nameof(SqlSimpleColumn);
    public const string SqlTypeSpecifier = nameof(SqlTypeSpecifier);
    public const string SqlPrimaryKeyConstraint = nameof(SqlPrimaryKeyConstraint);
    public const string SqlIndexedColumnSpecification = nameof(SqlIndexedColumnSpecification);
    public const string SqlIndex = nameof(SqlIndex);
    public const string SqlForeignKeyConstraint = nameof(SqlForeignKeyConstraint);
    public const string SqlProcedure = nameof(SqlProcedure);
    public const string SqlView = nameof(SqlView);
    public const string SqlViewColumn = nameof(SqlViewColumn);
}
