namespace Squill.Provider.Postgres;

public static class PostgresPropertyNames
{
    public const string IsNullable = nameof(IsNullable);
    public const string Length = nameof(Length);
    public const string Precision = nameof(Precision);
    public const string Scale = nameof(Scale);
    public const string IsUnique = nameof(IsUnique);
    public const string IndexMethod = nameof(IndexMethod);
    public const string IsAscending = nameof(IsAscending);
    public const string NullsFirst = nameof(NullsFirst);
    public const string DeleteAction = nameof(DeleteAction);
    public const string UpdateAction = nameof(UpdateAction);
    public const string IsIdentity = nameof(IsIdentity);
    public const string IdentityGeneration = nameof(IdentityGeneration);
    // Identity sequence options (issue #13), stored only when they differ from the
    // Postgres default for the column's type and sequence direction — see
    // PostgresIdentitySequenceDefaults. Names follow SSDT's SqlSequence properties.
    public const string StartValue = nameof(StartValue);
    public const string Increment = nameof(Increment);
    public const string MinValue = nameof(MinValue);
    public const string MaxValue = nameof(MaxValue);
    public const string CacheSize = nameof(CacheSize);
    public const string IsCycling = nameof(IsCycling);
    // The canonical form of a column DEFAULT constant literal (see PostgresDefaultValue).
    public const string DefaultValue = nameof(DefaultValue);
    public const string FilterPredicate = nameof(FilterPredicate);
    public const string Version = nameof(Version);
    // PostgreSQL terminology: an index element may specify an operator class (opclass),
    // and CREATE INDEX ... WITH (...) carries storage parameters. See
    // https://www.postgresql.org/docs/current/sql-createindex.html
    public const string OperatorClass = nameof(OperatorClass);
    public const string StorageParameters = nameof(StorageParameters);
}