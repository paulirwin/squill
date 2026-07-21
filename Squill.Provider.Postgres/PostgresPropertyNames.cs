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
    // Stored procedures (issue #41). A procedure element's Name carries its argument
    // signature so overloads are distinct objects, so the bare name and the signature are
    // also stored separately for scripting. Body is the routine source held verbatim, as
    // PostgreSQL stores it in pg_proc.prosrc.
    public const string RoutineName = nameof(RoutineName);
    public const string ArgumentTypes = nameof(ArgumentTypes);
    public const string Arguments = nameof(Arguments);
    public const string Language = nameof(Language);
    public const string Body = nameof(Body);
    public const string IsSecurityDefiner = nameof(IsSecurityDefiner);
    // Views (issue #42). A view's query is carried for scripting only and never takes part
    // in comparison: every engine rewrites the query it is given (PostgreSQL reformats it
    // through pg_get_viewdef), so a declared body can never hash-match an extracted one. A
    // view's modeled identity is its name and column list instead — see
    // PostgresModelFactory.CreateView and PostgresDatabaseDependencyAnalyzer.NormalizeForComparison.
    public const string Definition = nameof(Definition);
}