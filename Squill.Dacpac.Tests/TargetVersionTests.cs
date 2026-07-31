using Squill.Dacpac;

namespace Squill.Dacpac.Tests;

/// <summary>
/// Covers the floor semantics of a parsed <c>SquillTargetVersion</c> (issue #189): a bare major
/// means <c>.0</c>, a dotted value keeps its minor, and versions order as (major, minor) tuples.
/// </summary>
public class TargetVersionTests
{
    [Theory]
    [InlineData("8", 8, 0, 0)]
    [InlineData("8.4", 8, 4, 0)]
    [InlineData("8.0", 8, 0, 0)]
    [InlineData("16.2", 16, 2, 0)]
    [InlineData("10.11", 10, 11, 0)]
    // The thresholds this gating exists for are patch-level: MySQL functional index keys arrived
    // in 8.0.13, enforced CHECK constraints in 8.0.16, MariaDB RENAME COLUMN in 10.5.3.
    [InlineData("8.0.13", 8, 0, 13)]
    [InlineData("8.0.16", 8, 0, 16)]
    [InlineData("10.5.3", 10, 5, 3)]
    // Surrounding whitespace is an MSBuild property artefact, not author intent.
    [InlineData("  8.4  ", 8, 4, 0)]
    public void Parse_ReadsAllComponents(
        string input, int expectedMajor, int expectedMinor, int expectedPatch)
    {
        var version = TargetVersion.Parse(input);

        Assert.NotNull(version);
        Assert.Equal(expectedMajor, version.Value.Major);
        Assert.Equal(expectedMinor, version.Value.Minor);
        Assert.Equal(expectedPatch, version.Value.Patch);
    }

    /// <summary>
    /// The decision this issue turns on: an unspecified minor is the <em>oldest</em> patch, so a
    /// bare major is indistinguishable from naming <c>.0</c> outright.
    /// </summary>
    [Fact]
    public void Parse_BareMajor_IsTheSameAsPointZero()
    {
        Assert.Equal(TargetVersion.Parse("8.0"), TargetVersion.Parse("8"));
        Assert.Equal(TargetVersion.Parse("8.0.0"), TargetVersion.Parse("8"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Parse_Blank_MeansUnconstrained(string? input)
    {
        Assert.Null(TargetVersion.Parse(input));
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("8.x")]
    [InlineData("x.4")]
    [InlineData("-8")]
    [InlineData("8.-4")]
    [InlineData("8,4")]
    [InlineData(".")]
    [InlineData("8.0.x")]
    // A fourth component is not a version this tool understands; accepting it would silently
    // ignore something the author wrote deliberately.
    [InlineData("8.0.13.1")]
    public void Parse_Malformed_Throws(string input)
    {
        var ex = Assert.Throws<ArgumentException>(() => TargetVersion.Parse(input));
        Assert.Contains(input, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_OrdersByMajorThenMinorThenPatch()
    {
        Assert.True(TargetVersion.Parse("8.0") < TargetVersion.Parse("8.4"));
        Assert.True(TargetVersion.Parse("8.4") < TargetVersion.Parse("9.0"));
        // A floor is unbounded above, so a later major always clears an earlier one regardless
        // of how high the earlier major's minor climbs.
        Assert.True(TargetVersion.Parse("8.99") < TargetVersion.Parse("9.0"));
        Assert.True(TargetVersion.Parse("8.4") >= TargetVersion.Parse("8.4"));

        // The patch is the component most of the real thresholds live on.
        Assert.True(TargetVersion.Parse("8.0.0") < TargetVersion.Parse("8.0.13"));
        Assert.True(TargetVersion.Parse("8.0.13") < TargetVersion.Parse("8.0.16"));
        Assert.True(TargetVersion.Parse("8.0.99") < TargetVersion.Parse("8.1"));
        Assert.True(TargetVersion.Parse("8.0.13") >= TargetVersion.Parse("8.0.13"));
    }

    /// <summary>
    /// Components compare as numbers, not as text. Every pair below is one where lexicographic
    /// ordering gives the <em>opposite</em> answer: a longer number starting with a smaller digit
    /// sorts first as text but is larger numerically, so <c>"8.9"</c> sorts after <c>"8.10"</c>
    /// (<c>'9' &gt; '1'</c>) while <c>8.9 &lt; 8.10</c>. A floor compared as text would refuse
    /// deploys to servers that satisfy it.
    ///
    /// <para>
    /// Pinned separately from the ordering test above, whose cases all happen to agree under both
    /// rules and so could not catch the regression. Note a trailing zero is <em>not</em> such a
    /// case — <c>"8.9"</c> is a prefix of <c>"8.90"</c>, so text and numeric ordering agree there;
    /// see <see cref="Compare_TrailingZeroIsADistinctVersion"/> for what that pair does pin.
    /// </para>
    /// </summary>
    [Theory]
    // Two digits vs one: numerically 10 > 9, lexicographically "8.9" > "8.10".
    [InlineData("8.9", "8.10")]
    [InlineData("8.0.9", "8.0.10")]
    // Wider gap, same trap: numerically 90 > 9, lexicographically "8.9" > "8.90"... but only
    // once the shorter string is no longer a prefix, hence 8.9 against 8.19 rather than 8.90.
    [InlineData("8.9", "8.19")]
    [InlineData("8.0.9", "8.0.19")]
    // The real MariaDB case: 10.5 and 10.11 are both shipped majors.
    [InlineData("10.5", "10.11")]
    // Majors, where the same trap applies: numerically 10 > 9, lexicographically "9" > "10".
    [InlineData("9.0", "10.0")]
    public void Compare_IsNumeric_NotLexicographic(string lower, string higher)
    {
        var low = TargetVersion.Parse(lower);
        var high = TargetVersion.Parse(higher);

        Assert.True(low < high, $"Expected {lower} < {higher}.");
        Assert.True(high > low, $"Expected {higher} > {lower}.");

        // Guard against the ordering being right only because ToString() was compared: the
        // rendered forms sort the other way round, so this would fail under text comparison.
        Assert.True(
            string.CompareOrdinal(lower, higher) > 0,
            $"Test case is not discriminating: '{lower}' must sort after '{higher}' as text.");
    }

    /// <summary>
    /// <c>8.90</c> is greater than <c>8.9</c>: a trailing zero changes the component's magnitude
    /// (90 against 9), so the two are different floors rather than two spellings of one.
    ///
    /// <para>
    /// Text ordering happens to agree here, since the shorter string is a prefix of the longer —
    /// which is why this pair lives here rather than in
    /// <see cref="Compare_IsNumeric_NotLexicographic"/>. What it pins instead is that a component
    /// is read as a whole number: a parser that took only the leading digit, or that treated the
    /// trailing zero as insignificant, would call these equal.
    /// </para>
    /// </summary>
    [Fact]
    public void Compare_TrailingZeroIsADistinctVersion()
    {
        var lower = TargetVersion.Parse("8.9")!.Value;
        var higher = TargetVersion.Parse("8.90")!.Value;

        // Asserted both ways round so a CompareTo that returned a constant cannot pass.
        Assert.True(higher > lower, "8.90 must be greater than 8.9.");
        Assert.True(lower < higher, "8.9 must be less than 8.90.");
        Assert.NotEqual(lower, higher);

        Assert.Equal(9, lower.Minor);
        Assert.Equal(90, higher.Minor);

        // The same on the patch component.
        var lowerPatch = TargetVersion.Parse("8.0.9")!.Value;
        var higherPatch = TargetVersion.Parse("8.0.90")!.Value;

        Assert.True(higherPatch > lowerPatch, "8.0.90 must be greater than 8.0.9.");
        Assert.Equal(9, lowerPatch.Patch);
        Assert.Equal(90, higherPatch.Patch);

        // A *leading* zero, by contrast, does not change the value: 8.09 and 8.9 are one floor.
        Assert.Equal(TargetVersion.Parse("8.9"), TargetVersion.Parse("8.09"));
    }

    [Theory]
    [InlineData(8, 0, 0, "8.0")]
    [InlineData(8, 4, 0, "8.4")]
    [InlineData(16, 2, 0, "16.2")]
    // A zero patch is left off so the common case reads the way authors write it.
    [InlineData(8, 0, 13, "8.0.13")]
    [InlineData(10, 5, 3, "10.5.3")]
    public void ToString_RendersDotted(int major, int minor, int patch, string expected)
    {
        Assert.Equal(expected, new TargetVersion(major, minor, patch).ToString());
    }
}
