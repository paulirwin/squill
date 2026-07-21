namespace Squill.Provider.MariaDb;

/// <summary>
/// The <see cref="Squill.Core.Property"/> keys for the MariaDB provider. A subset of the
/// Postgres property names — MariaDB models the same column/index facets but expresses
/// auto-increment (rather than Postgres identity) via <see cref="IsAutoIncrement"/>, and
/// has no operator-class or extension-version concept.
/// </summary>
public static class MariaDbPropertyNames
{
    public const string IsNullable = nameof(IsNullable);
    public const string Length = nameof(Length);
    public const string Precision = nameof(Precision);
    public const string Scale = nameof(Scale);
    public const string IsUnsigned = nameof(IsUnsigned);
    public const string IsUnique = nameof(IsUnique);
    public const string IndexMethod = nameof(IndexMethod);
    public const string IsAscending = nameof(IsAscending);
    public const string DeleteAction = nameof(DeleteAction);
    public const string UpdateAction = nameof(UpdateAction);
    public const string IsAutoIncrement = nameof(IsAutoIncrement);
    public const string DefaultValue = nameof(DefaultValue);
}
