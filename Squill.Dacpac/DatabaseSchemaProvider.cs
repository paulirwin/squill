namespace Squill.Dacpac;

/// <summary>
/// A database schema provider identifying the target engine and major version a DACPAC is
/// built for, mirroring SSDT's <c>DatabaseSchemaProvider</c> types (e.g.
/// <c>Sql160DatabaseSchemaProvider</c>). Each supported major version of each engine is a
/// concrete, reflection-discoverable subclass in that engine's provider assembly, named
/// <c>&lt;Provider&gt;&lt;MajorVersion&gt;DatabaseSchemaProvider</c> (e.g.
/// <c>Postgresql16DatabaseSchemaProvider</c>). The <see cref="DspName"/> recorded on the
/// <c>model.xml</c> root is the subclass's fully-qualified type name, so it lines up with the
/// real type and can be resolved back by <see cref="DatabaseSchemaProviderRegistry"/>.
/// </summary>
public abstract class DatabaseSchemaProvider
{
    /// <summary>
    /// Creates the canonical, version-floor-less instance for a major. This is the constructor
    /// <see cref="DatabaseSchemaProviderRegistry"/> discovers by reflection, and the instance it
    /// caches and hands to every caller.
    /// </summary>
    protected DatabaseSchemaProvider()
    {
    }

    /// <summary>
    /// Creates an instance carrying the build's declared target floor, for gating features that
    /// arrived in a point release. Returns a distinct object rather than mutating a shared one:
    /// the registry caches a single instance per major, so a settable floor would be
    /// process-global mutable state — order-dependent across concurrent builds, and capable of
    /// changing the catalog SQL an extractor builds partway through a run.
    /// </summary>
    protected DatabaseSchemaProvider(TargetVersion? targetVersion)
    {
        TargetVersion = targetVersion;
    }

    /// <summary>
    /// The provider name recorded in the DACPAC metadata (e.g. <c>Postgresql</c>, <c>MariaDb</c>,
    /// <c>MySql</c>). Matches the value <see cref="SquillProviderRegistry"/> resolves on.
    /// </summary>
    public abstract string ProviderName { get; }

    /// <summary>
    /// Whether this provider answers to <paramref name="candidate"/>. Defaults to a
    /// case-insensitive match on <see cref="ProviderName"/>; an engine whose
    /// <see cref="ISquillProvider.Matches"/> accepts alternative spellings overrides this to
    /// accept the same set.
    ///
    /// The two must agree. The host resolves the provider with
    /// <see cref="ISquillProvider.Matches"/> and then resolves a schema provider from the same
    /// raw name through <see cref="DatabaseSchemaProviderRegistry"/> — so a name only the first
    /// accepts selects a provider and then fails partway through the build, reporting an
    /// unsupported target <em>version</em> for what is really an accepted provider alias.
    /// </summary>
    public virtual bool MatchesProviderName(string candidate)
        => string.Equals(ProviderName, candidate, StringComparison.OrdinalIgnoreCase);

    /// <summary>The target engine major version this provider represents (e.g. <c>16</c>).</summary>
    public abstract int MajorVersion { get; }

    /// <summary>
    /// The build's declared target floor, when it stated one. <c>null</c> on the registry's
    /// canonical per-major instances, which describe an engine major rather than any particular
    /// project's target; those fall back to this major's <c>.0.0</c>, the same floor a bare major
    /// names.
    /// </summary>
    public TargetVersion? TargetVersion { get; }

    /// <summary>
    /// The floor this provider gates against: the declared target when there is one, otherwise
    /// this major's oldest release.
    /// </summary>
    public TargetVersion Floor => TargetVersion ?? new TargetVersion(MajorVersion, 0, 0);

    /// <summary>
    /// Whether a feature introduced in the given release is available on <em>every</em> server
    /// the declared floor permits. That is the only question a build-time gate can soundly
    /// answer, because the floor has no ceiling: the target admits arbitrarily new servers, so a
    /// construct counts as usable only if it exists at the floor itself.
    ///
    /// <para>
    /// The patch component is not optional padding here — most of the DDL this gating exists for
    /// arrived in patch releases (MySQL functional index keys in 8.0.13, enforced <c>CHECK</c>
    /// constraints in 8.0.16), so a gate that stopped at the minor could not state its own
    /// threshold.
    /// </para>
    ///
    /// <para>
    /// Deliberately not the inverse question ("was it removed later?"), which a floor cannot
    /// express — see issue #188.
    /// </para>
    /// </summary>
    public bool SupportsFeatureFrom(int introducedInMajor, int introducedInMinor, int introducedInPatch = 0)
        => Floor >= new TargetVersion(introducedInMajor, introducedInMinor, introducedInPatch);

    /// <summary>
    /// The longest identifier the engine honours, in the unit <see cref="MeasureIdentifier"/>
    /// counts. Every engine imposes one, so a name over it never survives a deploy intact —
    /// which is why it is a build error (<c>SQ0005</c>) rather than something discovered at
    /// deploy time.
    ///
    /// <para>
    /// What the engines do with an over-long name differs, and neither outcome is acceptable:
    /// MariaDB and MySQL <em>reject</em> it (<c>ERROR 1059</c>) partway through the script,
    /// while PostgreSQL <em>silently truncates</em> it, so the object deploys under a name the
    /// model never predicted and re-diffs on every deploy thereafter. Truncation is the worse
    /// of the two, since nothing announces it.
    /// </para>
    ///
    /// <para>
    /// The engines disagree on the unit as well as the number, which is why the limit and its
    /// measurement are declared together: PostgreSQL's <c>NAMEDATALEN - 1</c> is 63
    /// <em>bytes</em>, MariaDB and MySQL cap at 64 <em>characters</em>. Comparing one engine's
    /// number under the other's unit is wrong in both directions — it would reject valid
    /// multi-byte MariaDB source, and accept Postgres source the server truncates.
    /// </para>
    /// </summary>
    public abstract int MaxIdentifierLength { get; }

    /// <summary>
    /// Measures an identifier in the unit <see cref="MaxIdentifierLength"/> is expressed in.
    /// See that property for why the unit is engine-specific.
    /// </summary>
    public abstract int MeasureIdentifier(string identifier);

    /// <summary>
    /// The DSP name written to the <c>DataSchemaModel</c> root of <c>model.xml</c> — the
    /// concrete type's fully-qualified name (e.g.
    /// <c>Squill.Provider.Postgres.Postgresql16DatabaseSchemaProvider</c>). Using the real type
    /// name means the value is a live reference to a type that exists, discoverable by
    /// reflection, rather than a string that must be parsed.
    /// </summary>
    public string DspName => GetType().FullName
        ?? throw new InvalidOperationException(
            $"Schema provider type {GetType().Name} has no full name.");
}
