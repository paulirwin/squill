namespace Squill.Core;

/// <summary>
/// The <see cref="Element.Type"/> discriminators shared across providers. These mirror
/// SSDT-style element type names so a DACPAC model stays provider-neutral: a table is a
/// <c>SqlTable</c> whether it came from PostgreSQL or MariaDB. Providers reference these
/// shared names (typically by forwarding their own constant to the value here) and add
/// only their provider-specific element types on top.
/// </summary>
public abstract class SqlElementTypes
{
    public const string SqlTable = nameof(SqlTable);
    public const string SqlSimpleColumn = nameof(SqlSimpleColumn);
    public const string SqlTypeSpecifier = nameof(SqlTypeSpecifier);
    public const string SqlPrimaryKeyConstraint = nameof(SqlPrimaryKeyConstraint);
    // A UNIQUE constraint. Distinct from a unique SqlIndex: Postgres records one in
    // pg_constraint and it can back a foreign key. Providers that express uniqueness only
    // as an index (MariaDB, where a UNIQUE KEY is an index) simply never emit this type.
    public const string SqlUniqueConstraint = nameof(SqlUniqueConstraint);
    public const string SqlIndexedColumnSpecification = nameof(SqlIndexedColumnSpecification);
    public const string SqlIndex = nameof(SqlIndex);
    public const string SqlForeignKeyConstraint = nameof(SqlForeignKeyConstraint);
    public const string SqlProcedure = nameof(SqlProcedure);
    public const string SqlView = nameof(SqlView);
    public const string SqlViewColumn = nameof(SqlViewColumn);
    // A CREATE FUNCTION: a routine with a return type. Both providers model one.
    public const string SqlFunction = nameof(SqlFunction);
    // A CREATE TRIGGER: attaches to a table and fires on an event.
    public const string SqlTrigger = nameof(SqlTrigger);
}
