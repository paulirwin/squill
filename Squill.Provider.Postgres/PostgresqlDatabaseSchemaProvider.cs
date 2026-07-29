using Squill.Dacpac;

namespace Squill.Provider.Postgres;

/// <summary>
/// Base for the per-major-version PostgreSQL schema providers. Fixes the
/// <see cref="DatabaseSchemaProvider.ProviderName"/> so each concrete version subclass need
/// only declare its <see cref="DatabaseSchemaProvider.MajorVersion"/>. This type is abstract,
/// so it is not itself discovered by <see cref="DatabaseSchemaProviderRegistry"/>; the
/// concrete subclasses remain distinct types whose full names are recorded in DACPACs.
/// </summary>
public abstract class PostgresqlDatabaseSchemaProvider : DatabaseSchemaProvider
{
    public override string ProviderName => "Postgresql";

    /// <summary>
    /// Whether <c>ALTER TABLE … ALTER COLUMN … SET EXPRESSION AS (…)</c> is available, which
    /// redefines a generated column's expression and recomputes the stored rows in one statement
    /// (issue #156).
    ///
    /// Measured: accepted on <c>postgres:18</c>, and a <em>syntax error</em> on
    /// <c>postgres:16</c> — not merely an unsupported option — so a build targeting an older
    /// major must reach the same end state by rebuilding the column instead.
    /// </summary>
    public abstract bool SupportsSetExpression { get; }
}
