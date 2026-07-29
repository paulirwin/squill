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
    // Constraint deferrability (issue #159). A constraint is NOT DEFERRABLE INITIALLY IMMEDIATE
    // unless declared otherwise, and pg_constraint reports condeferrable/condeferred as plain
    // booleans, so each is stored only when true — matching the catalog's default and keeping a
    // declared constraint hash-matched with an extracted one. INITIALLY DEFERRED implies
    // DEFERRABLE (PostgreSQL rejects the combination without it).
    public const string IsDeferrable = nameof(IsDeferrable);
    public const string IsInitiallyDeferred = nameof(IsInitiallyDeferred);
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
    // CASCADE on CREATE EXTENSION (issue #143): install this extension's own dependencies
    // along with it. Deploy-time behaviour rather than state — the catalog records no trace
    // of how an extension came to be installed — so it is scripted but excluded from the
    // element's identity, or every comparison against a real database would see a difference
    // that can never be reconciled.
    public const string Cascade = nameof(Cascade);
    // PostgreSQL terminology: an index element may specify an operator class (opclass),
    // and CREATE INDEX ... WITH (...) carries storage parameters. See
    // https://www.postgresql.org/docs/current/sql-createindex.html
    public const string OperatorClass = nameof(OperatorClass);
    public const string StorageParameters = nameof(StorageParameters);
    // NULLS NOT DISTINCT on a unique index (PostgreSQL 15+, issue #160). Stored only when true,
    // matching pg_index.indnullsnotdistinct, which is a plain boolean defaulting to false — so
    // an ordinary index carries no property and does not re-diff. The column does not exist
    // before PostgreSQL 15, so extraction reads it through to_jsonb() rather than by name.
    public const string NullsNotDistinct = nameof(NullsNotDistinct);
    // An expression index key — CREATE INDEX ix ON people (lower(name)) — issue #160. The key
    // is an expression rather than a column, so the spec carries text instead of a Column
    // relationship. Split for the same reason as a generated column's expression: PostgreSQL
    // rewrites what it is given, so only the canonical form can take part in comparison, while
    // the raw spelling is kept for scripting.
    public const string KeyExpression = nameof(KeyExpression);
    public const string NormalizedKeyExpression = nameof(NormalizedKeyExpression);
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
    // Range types (issue #122). Subtype is the canonical name of the type the range is built
    // over, as format_type reports it. SubtypeOperatorClass and Collation are stored only
    // when they are not the subtype's default, so a declared range hash-matches an extracted
    // one — the catalog always reports a resolved opclass.
    public const string Subtype = nameof(Subtype);
    public const string SubtypeOperatorClass = nameof(SubtypeOperatorClass);
    // Also a column facet (issue #159): a column-level COLLATE. Stored only when the collation
    // is not the column type's default, for the same reason as above — pg_attribute.attcollation
    // reports a resolved collation ("default", oid 100) for every collatable column, so storing
    // it unconditionally would make every text column re-diff on every deploy.
    public const string Collation = nameof(Collation);
    // Collations (issue #159). Provider is "libc" or "icu". A libc collation resolves its
    // locale into LcCollate/LcCtype and an icu one into Locale, which is why all three exist
    // and only the ones pg_collation actually populated are stored. IsDeterministic is stored
    // only when false (deterministic is the default).
    public const string Provider = nameof(Provider);
    public const string Locale = nameof(Locale);
    public const string LcCollate = nameof(LcCollate);
    public const string LcCtype = nameof(LcCtype);
    public const string IsDeterministic = nameof(IsDeterministic);
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
