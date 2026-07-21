namespace Squill.Provider.MariaDb;

/// <summary>
/// The <see cref="Squill.Core.Relationship"/> names for the MariaDB provider. Mirrors the
/// Postgres relationship names for the shapes MariaDB shares (columns, type specifier,
/// index/PK column specifications, foreign keys), minus the schema relationship (MariaDB
/// objects are not schema-scoped within a database).
/// </summary>
public static class MariaDbRelationshipNames
{
    public const string Columns = nameof(Columns);
    public const string TypeSpecifier = nameof(TypeSpecifier);
    public const string Type = nameof(Type);
    public const string ColumnSpecifications = nameof(ColumnSpecifications);
    public const string DefiningTable = nameof(DefiningTable);
    public const string Column = nameof(Column);
    public const string IndexedObject = nameof(IndexedObject);
    public const string ForeignKeyColumns = nameof(ForeignKeyColumns);
    public const string ForeignTable = nameof(ForeignTable);
    public const string ForeignColumns = nameof(ForeignColumns);
}
