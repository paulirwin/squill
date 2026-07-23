namespace Squill.Core;

/// <summary>
/// The <see cref="Relationship"/> names shared across providers. Providers reference these
/// shared names (typically by forwarding their own constant to the value here) and add
/// only their provider-specific relationships on top (e.g. the Postgres <c>Schema</c>
/// relationship, which has no MariaDB analog).
/// </summary>
public abstract class SqlRelationshipNames
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
    // A trigger's target table. The trigger is created after this table, and (where the
    // engine scopes a trigger's name to its table rather than a schema) takes its schema
    // from it.
    public const string TriggerTable = nameof(TriggerTable);
}
