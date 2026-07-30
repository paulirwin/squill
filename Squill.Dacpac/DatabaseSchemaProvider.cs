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
    /// The provider name recorded in the DACPAC metadata (e.g. <c>Postgresql</c>, <c>MariaDb</c>,
    /// <c>MySql</c>). Matches the value <see cref="SquillProviderRegistry"/> resolves on.
    /// </summary>
    public abstract string ProviderName { get; }

    /// <summary>The target engine major version this provider represents (e.g. <c>16</c>).</summary>
    public abstract int MajorVersion { get; }

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
