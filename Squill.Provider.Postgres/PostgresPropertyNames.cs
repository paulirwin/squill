using Squill.Core;

namespace Squill.Provider.Postgres;

/// <summary>
/// The <see cref="Property"/> keys for the Postgres provider. Inherits the shared
/// <see cref="SqlPropertyNames"/> vocabulary (IsNullable, Length, DefaultValue, …) and adds
/// the Postgres-only properties (identity/sequence options, operator classes, enum/domain
/// and aggregate/trigger facets) on top.
/// </summary>
public sealed class PostgresPropertyNames : SqlPropertyNames
{
    public const string NullsFirst = nameof(NullsFirst);
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
    // A standalone sequence's AS type (issue #122), stored only when it is not the bigint
    // default. It shares StartValue/Increment/MinValue/MaxValue/CacheSize/IsCycling above
    // with identity columns, but needs its own type property: an identity column's sequence
    // takes the column's type, while a declared sequence names its own.
    public const string SequenceDataType = nameof(SequenceDataType);
    public const string FilterPredicate = nameof(FilterPredicate);
    public const string Version = nameof(Version);
    // PostgreSQL terminology: an index element may specify an operator class (opclass),
    // and CREATE INDEX ... WITH (...) carries storage parameters. See
    // https://www.postgresql.org/docs/current/sql-createindex.html
    public const string OperatorClass = nameof(OperatorClass);
    public const string StorageParameters = nameof(StorageParameters);
    // Stored procedures (issue #41). ArgumentTypes carries the argument signature so
    // overloads are distinct objects. Body is the routine source held verbatim, as
    // PostgreSQL stores it in pg_proc.prosrc.
    public const string ArgumentTypes = nameof(ArgumentTypes);
    public const string Language = nameof(Language);
    public const string IsSecurityDefiner = nameof(IsSecurityDefiner);
    // User-defined types (issue #75). An enum type carries its labels in declaration order
    // (their significant sort order) as an ordered list. A domain carries the canonical text
    // of its CHECK constraint expression (inherited as CheckExpression from
    // SqlPropertyNames, which a table CHECK constraint also uses).
    public const string Labels = nameof(Labels);
    // Functions (issue #81). ReturnsSet is true for RETURNS SETOF. Volatility is one of
    // "IMMUTABLE"/"STABLE"/"VOLATILE" (stored only when not the VOLATILE default); IsStrict
    // is stored only when true (CALLED ON NULL INPUT is the default).
    public const string ReturnsSet = nameof(ReturnsSet);
    public const string Volatility = nameof(Volatility);
    public const string IsStrict = nameof(IsStrict);
    // Aggregates (issue #82). StateFunction is the state transition function name (SFUNC),
    // schema-qualified as pg_proc reports it; StateType is the canonical accumulator type
    // name (STYPE, as format_type reports it).
    public const string StateFunction = nameof(StateFunction);
    public const string StateType = nameof(StateType);
    // Triggers (issue #83). Events is the OR'd event list rendered canonically (e.g.
    // "INSERT OR UPDATE") in a fixed order; Level is "ROW"/"STATEMENT". TriggerFunction is
    // the schema-qualified function name and FunctionArguments is the comma-joined literal
    // argument list (empty when there are none).
    public const string Events = nameof(Events);
    public const string Level = nameof(Level);
    public const string TriggerFunction = nameof(TriggerFunction);
    public const string FunctionArguments = nameof(FunctionArguments);
}
