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
