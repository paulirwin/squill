using System.Globalization;

namespace Squill.Dacpac;

/// <summary>
/// The engine version a DACPAC is built for: a <em>floor</em>, unbounded above. A target of
/// <c>8.0</c> means "must work on any server at or after 8.0" — 8.0, 8.4, 9.x and later all
/// satisfy it. There is no ceiling, so a construct removed in some later major is deliberately
/// not something this type can express (see issue #188).
///
/// <para>
/// An unspecified <see cref="Minor"/> means <c>.0</c>, the <em>oldest</em> patch of that major,
/// because that is the weakest assumption a floor can make: every 8.x server satisfies a floor
/// of 8.0, so nothing is wrongly rejected. This is deliberately the opposite of the rule for an
/// absent <em>major</em>, which resolves to the latest supported one. The asymmetry is a
/// consequence of what each mistake costs: guessing the minor too high turns a valid deploy into
/// a hard failure against a server that would have worked, while guessing it too low only costs a
/// warning the author can act on.
/// </para>
/// </summary>
/// <param name="Major">The major component (e.g. <c>8</c>).</param>
/// <param name="Minor">The minor component; <c>0</c> when the source named only a major.</param>
/// <param name="Patch">
/// The patch component; <c>0</c> when the source named no patch. Carried because most of the
/// MySQL and MariaDB DDL surface this gating exists for arrived in patch releases —
/// <c>8.0.13</c>, <c>8.0.16</c>, <c>10.5.3</c> — so a two-component floor could not express its
/// own thresholds.
/// </param>
public readonly record struct TargetVersion(int Major, int Minor, int Patch = 0)
    : IComparable<TargetVersion>
{
    /// <summary>
    /// Parses a <c>SquillTargetVersion</c> value: a bare major (<c>"8"</c>), a dotted version
    /// (<c>"8.4"</c>), or a full one (<c>"8.0.13"</c>). Components the source omits are <c>0</c>,
    /// per the floor rule above. Blank input yields <c>null</c>, meaning no version constraint.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The value is not a version number. Malformed input is rejected rather than coerced,
    /// since a silently misread floor would gate features against a version nobody chose.
    /// </exception>
    public static TargetVersion? Parse(string? targetVersion)
    {
        if (string.IsNullOrWhiteSpace(targetVersion))
        {
            return null;
        }

        var parts = targetVersion.Trim().Split('.');

        // A fourth component is not a version this tool understands; accepting it would mean
        // silently ignoring something the author wrote deliberately.
        if (parts.Length > 3)
        {
            throw Invalid(targetVersion);
        }

        var components = new int[3];

        for (var i = 0; i < parts.Length; i++)
        {
            if (!TryParseComponent(parts[i], out components[i]))
            {
                throw Invalid(targetVersion);
            }
        }

        // Omitted components are 0 — see the type remarks for why a missing component resolves
        // to the oldest release rather than the newest.
        return new TargetVersion(components[0], components[1], components[2]);
    }

    private static bool TryParseComponent(string text, out int value)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static ArgumentException Invalid(string targetVersion)
        => new($"SquillTargetVersion '{targetVersion}' is not a valid version number "
               + "(expected a major version like '16', or a dotted version like '16.2').");

    /// <summary>
    /// Orders by major then minor, which is what makes a floor comparable to a server version:
    /// the deploy check is exactly <c>server &lt; required</c>.
    /// </summary>
    public int CompareTo(TargetVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(TargetVersion left, TargetVersion right)
        => left.CompareTo(right) < 0;

    public static bool operator >(TargetVersion left, TargetVersion right)
        => left.CompareTo(right) > 0;

    public static bool operator <=(TargetVersion left, TargetVersion right)
        => left.CompareTo(right) <= 0;

    public static bool operator >=(TargetVersion left, TargetVersion right)
        => left.CompareTo(right) >= 0;

    /// <summary>
    /// Renders as <c>major.minor</c> (e.g. <c>8.4</c>), extended to <c>major.minor.patch</c> when
    /// a patch is present (e.g. <c>8.0.13</c>). A zero patch is left off so the common case reads
    /// the way authors write it, and so existing diagnostics keep their present wording.
    /// </summary>
    public override string ToString()
        => Patch == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}")
            : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}
