using System.Text;

namespace Squill.Core;

/// <summary>
/// The provider-agnostic dispatch for turning a schema comparison into DDL. The two public
/// entry points are shared: <see cref="GenerateScript"/> concatenates the per-delta scripts,
/// and <see cref="GenerateScriptForDelta"/> routes each delta type to a provider hook. The
/// emitted SQL text is entirely engine-specific (quoting, identity vs auto-increment,
/// CREATE OR REPLACE vs DROP+CREATE, …), so every emitter is an abstract hook the provider
/// supplies.
///
/// <see cref="GenerateAlterExtensionScript"/>, <see cref="GenerateAlterEnumTypeScript"/> and
/// <see cref="GenerateAlterDomainTypeScript"/> are the deltas a provider may not handle
/// (extensions, enums and domains are Postgres-only); their base implementations throw,
/// matching the original fall-through, and only Postgres overrides them.
/// </summary>
public abstract class ScriptGeneratorBase : IScriptGenerator
{
    /// <summary>
    /// Generates a single script covering every delta in the comparison, in order, with a blank
    /// line between steps so the generated (or previewed) script is easier to read.
    /// </summary>
    public string GenerateScript(SchemaComparison comparison)
    {
        var sb = new System.Text.StringBuilder();

        foreach (var delta in comparison.Deltas)
        {
            sb.Append(GenerateScriptForDelta(delta));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public string GenerateScriptForDelta(SchemaDelta delta) => delta switch
    {
        CreateDelta create => GenerateCreateScript(create),
        AlterDelta alter => GenerateAlterScript(alter),
        RebuildTableDelta rebuild => GenerateRebuildScript(rebuild),
        DropDelta drop => GenerateDropScript(drop),
        RecreateDelta recreate => GenerateRecreateScript(recreate),
        AlterExtensionVersionDelta alterExtension => GenerateAlterExtensionScript(alterExtension),
        AlterEnumTypeDelta alterEnum => GenerateAlterEnumTypeScript(alterEnum),
        AlterDomainTypeDelta alterDomain => GenerateAlterDomainTypeScript(alterDomain),
        AddConstraintDelta addConstraint => GenerateAddConstraintScript(addConstraint),
        _ => throw new NotImplementedException(
            $"Generating a script for {delta.GetType().Name} is not supported."),
    };

    protected abstract string GenerateCreateScript(CreateDelta delta);

    protected abstract string GenerateAlterScript(AlterDelta delta);

    protected abstract string GenerateRebuildScript(RebuildTableDelta delta);

    protected abstract string GenerateDropScript(DropDelta delta);

    protected abstract string GenerateRecreateScript(RecreateDelta delta);

    protected abstract string GenerateAddConstraintScript(AddConstraintDelta delta);

    /// <summary>
    /// Emits an extension version update. Extensions are Postgres-only, so the base throws;
    /// only the Postgres generator overrides this. A provider that never produces an
    /// <see cref="AlterExtensionVersionDelta"/> never reaches it.
    /// </summary>
    protected virtual string GenerateAlterExtensionScript(AlterExtensionVersionDelta delta)
        => throw new NotImplementedException(
            $"This provider does not support {nameof(AlterExtensionVersionDelta)}.");

    // Enum types and domains are Postgres-only, so — like extensions above — the base throws
    // and only Postgres overrides (issue #122).
    protected virtual string GenerateAlterEnumTypeScript(AlterEnumTypeDelta delta)
        => throw new NotImplementedException(
            $"This provider does not support {nameof(AlterEnumTypeDelta)}.");

    protected virtual string GenerateAlterDomainTypeScript(AlterDomainTypeDelta delta)
        => throw new NotImplementedException(
            $"This provider does not support {nameof(AlterDomainTypeDelta)}.");

    // ---- Shared rebuild orchestration ----
    //
    // The overall rebuild *flow* (rename aside, drop/recreate inbound FKs, copy data, drop the
    // original) is provider-specific enough — Postgres wraps it in a transaction, casts on type
    // change, and handles identity sequences; MariaDB does none of that — that GenerateRebuildScript
    // stays per-provider. But three pieces are structurally identical modulo small hooks, so they
    // live here: the rename-aside name derivation and the inbound-FK drop/recreate emission.

    // The suffix appended to rename an object aside during a rebuild.
    protected const string RebuildAsideSuffix = "__squill_rebuild_old";

    /// <summary>
    /// The DDL verb that drops an inbound foreign key: <c>DROP CONSTRAINT</c> on Postgres,
    /// <c>DROP FOREIGN KEY</c> on MariaDB.
    /// </summary>
    protected abstract string ForeignKeyDropVerb { get; }

    /// <summary>
    /// Renders the quoted defining-table name of an inbound foreign key, applying any provider
    /// schema-qualification rules (Postgres elides the <c>public</c> schema; MariaDB has none).
    /// </summary>
    protected abstract string QuoteForeignKeyDefiningTable(string referencedName);

    /// <summary>
    /// The <c>CONSTRAINT ... FOREIGN KEY ... REFERENCES ...</c> clause used to recreate an inbound
    /// foreign key after a rebuild. Provider-specific (quoting, action rendering).
    /// </summary>
    protected abstract string GetForeignKeyClause(Element foreignKey);

    /// <summary>
    /// A rename-aside name for a rebuilt object, derived from its bare name and guaranteed to stay
    /// within the provider's identifier-length limit. When the base name plus the suffix fits, it
    /// is used verbatim. Otherwise the base is truncated and a short deterministic hash of the full
    /// base is folded in, so two long names that share a truncated prefix still get distinct aside
    /// names rather than silently colliding after truncation.
    ///
    /// Parameterized by the measure function and length limit so each provider can expose a
    /// <c>static</c> <c>RebuildAsideName</c> — which its unit tests call without constructing a
    /// generator, and which <c>GenerateRebuildScript</c> calls — that delegates here. Postgres
    /// measures UTF-8 bytes against a 63-byte limit; MariaDB measures characters against 64.
    /// </summary>
    protected static string ComputeRebuildAsideName(
        string baseName, Func<string, int> measure, int maxLength)
    {
        var candidate = baseName + RebuildAsideSuffix;

        if (measure(candidate) <= maxLength)
        {
            return candidate;
        }

        // 8 hex chars of a stable hash disambiguate names that truncate to the same prefix.
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(baseName)))[..8];

        // Reserve room for "_" + hash + suffix, then take as many leading characters of the base
        // as fit. Base names here are ASCII identifiers, so char length equals byte length; clamp
        // defensively in case of a multi-byte name.
        var reserved = 1 + hash.Length + RebuildAsideSuffix.Length;
        var keep = Math.Max(0, maxLength - reserved);
        var truncatedBase = baseName.Length > keep ? baseName[..keep] : baseName;

        return $"{truncatedBase}_{hash}{RebuildAsideSuffix}";
    }

    /// <summary>
    /// Emits <c>ALTER TABLE &lt;defining&gt; &lt;drop-verb&gt; &lt;fk&gt;;</c> for each inbound FK,
    /// so the referenced table can be renamed aside and dropped during a rebuild. A trailing blank
    /// line follows the block when any FK was emitted.
    /// </summary>
    protected void AppendInboundForeignKeyDrops(StringBuilder sb, IList<Element> inboundForeignKeys)
    {
        if (inboundForeignKeys.Count == 0)
        {
            return;
        }

        foreach (var fk in inboundForeignKeys)
        {
            var (definingTable, fkName) = InboundForeignKeyNames(fk);

            sb.Append("ALTER TABLE ").Append(definingTable)
                .Append(' ').Append(ForeignKeyDropVerb).Append(' ').Append(fkName).AppendLine(";");
        }

        sb.AppendLine();
    }

    /// <summary>
    /// Emits <c>ALTER TABLE &lt;defining&gt; ADD &lt;fk-clause&gt;;</c> for each inbound FK,
    /// recreating it after the rebuild. Callers guard the leading blank line, so unlike
    /// <see cref="AppendInboundForeignKeyDrops"/> this appends no trailing blank line.
    /// </summary>
    protected void AppendInboundForeignKeyRecreates(StringBuilder sb, IList<Element> inboundForeignKeys)
    {
        foreach (var fk in inboundForeignKeys)
        {
            var (definingTable, _) = InboundForeignKeyNames(fk);

            sb.Append("ALTER TABLE ").Append(definingTable)
                .Append(" ADD ").Append(GetForeignKeyClause(fk)).AppendLine(";");
        }
    }

    /// <summary>
    /// The (quoted defining-table name, quoted constraint name) for an inbound FK, used to drop and
    /// recreate it around a rebuild.
    /// </summary>
    protected (string DefiningTable, string ConstraintName) InboundForeignKeyNames(Element fk)
    {
        if (fk.Name is not string fkName)
        {
            throw new ArgumentException("Foreign keys must have names");
        }

        var definingTableRef = fk.GetRelationship(SqlRelationshipNames.DefiningTable)
            ?.Entries.OfType<Reference>().SingleOrDefault()
            ?? throw new InvalidOperationException($"Foreign key {fkName} has no defining table");

        return (QuoteForeignKeyDefiningTable(definingTableRef.Name), QuoteConstraintName(fkName));
    }

    /// <summary>
    /// The quoted, unqualified rendering of a constraint name for use in the inbound-FK drop/add
    /// statements. Provider-specific because the quote character differs.
    /// </summary>
    protected abstract string QuoteConstraintName(string constraintName);
}
