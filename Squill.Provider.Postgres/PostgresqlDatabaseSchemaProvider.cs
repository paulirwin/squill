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
    protected PostgresqlDatabaseSchemaProvider()
    {
    }

    protected PostgresqlDatabaseSchemaProvider(TargetVersion? targetVersion)
        : base(targetVersion)
    {
    }

    public override string ProviderName => "Postgresql";

    /// <summary>
    /// PostgreSQL's limit is <c>NAMEDATALEN - 1</c> = 63 <em>bytes</em>, not characters, and it
    /// does not reject a longer identifier — it silently truncates to fit. Truncation is the
    /// worse failure of the two: the object deploys under a name the model never predicted and
    /// re-diffs on every deploy thereafter, where MariaDB's outright rejection at least stops.
    /// </summary>
    public sealed override int MaxIdentifierLength => 63;

    /// <inheritdoc />
    public sealed override int MeasureIdentifier(string identifier)
        => System.Text.Encoding.UTF8.GetByteCount(identifier);

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
