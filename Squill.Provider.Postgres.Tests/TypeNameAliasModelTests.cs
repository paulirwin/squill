using Squill.Core;
using Squill.PostgresParser;
using Squill.TestFramework;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Docker-free coverage that PostgreSQL's internal type-name aliases reach the model as the type
/// they name (issue #197).
///
/// <para>
/// The defect this pins is a re-diff, so the assertion is a comparison rather than a property
/// read: a model built from the abbreviation and one built from the spelled-out form must be
/// indistinguishable. Since extraction reports only the spelled-out name, a model that differs
/// from the spelled-out one is exactly a model that will not hash-match the database.
/// </para>
/// </summary>
public class TypeNameAliasModelTests
{
    private static readonly PostgresDatabaseProvider Provider = new("Host=unused");

    private static Task<Model> ParseModelAsync(string sql)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrPostgresParser()),
            TestContext.Current.CancellationToken);

    /// <summary>
    /// Two models of the same table, one declared with the alias and one spelled out, diff to
    /// nothing. This is the round trip's precondition: extraction only ever produces the
    /// spelled-out form.
    /// </summary>
    [Theory]
    [InlineData("int2", "smallint")]
    [InlineData("int4", "integer")]
    [InlineData("int8", "bigint")]
    [InlineData("float4", "real")]
    [InlineData("float8", "double precision")]
    [InlineData("bool", "boolean")]
    [InlineData("varbit", "bit varying")]
    [InlineData("varbit(8)", "bit varying(8)")]
    [InlineData("timetz", "time with time zone")]
    [InlineData("timestamptz", "timestamp with time zone")]
    [InlineData("int8[]", "bigint[]")]
    [InlineData("timestamptz[]", "timestamp with time zone[]")]
    public async Task AliasAndSpelledOutForm_ProduceTheSameModel(string alias, string spelledOut)
    {
        var aliasModel = await ParseModelAsync($"CREATE TABLE t (c {alias});");
        var spelledOutModel = await ParseModelAsync($"CREATE TABLE t (c {spelledOut});");

        var comparison = SchemaCompare.Compare(Provider, aliasModel, spelledOutModel);

        Assert.Empty(comparison.Deltas);
    }

    /// <summary>
    /// The serial aliases name the serial shorthands, so they must match the serial spelling
    /// rather than the bare integer type: <c>serial8</c> is <c>bigserial</c>, not <c>bigint</c>.
    /// Collapsing it to the integer would drop the backing sequence.
    /// </summary>
    [Theory]
    [InlineData("serial2", "smallserial")]
    [InlineData("serial4", "serial")]
    [InlineData("serial8", "bigserial")]
    public async Task SerialAlias_MatchesTheSerialSpelling(string alias, string spelledOut)
    {
        var aliasModel = await ParseModelAsync($"CREATE TABLE t (c {alias});");
        var spelledOutModel = await ParseModelAsync($"CREATE TABLE t (c {spelledOut});");

        var comparison = SchemaCompare.Compare(Provider, aliasModel, spelledOutModel);

        Assert.Empty(comparison.Deltas);
    }

    /// <summary>
    /// A serial alias keeps its sequence: modeled against the plain integer of the same width it
    /// must still differ, or the identity would have been silently dropped.
    /// </summary>
    [Theory]
    [InlineData("serial8", "bigint")]
    [InlineData("serial4", "integer")]
    public async Task SerialAlias_IsNotTheSameAsThePlainInteger(string serialAlias, string integerType)
    {
        var serialModel = await ParseModelAsync($"CREATE TABLE t (c {serialAlias});");
        var integerModel = await ParseModelAsync($"CREATE TABLE t (c {integerType});");

        var comparison = SchemaCompare.Compare(Provider, serialModel, integerModel);

        Assert.NotEmpty(comparison.Deltas);
    }

    /// <summary>
    /// A range type declared over an aliased subtype models the same as one declared over the
    /// spelled-out subtype. PostgreSQL's own CREATE TYPE ... AS RANGE documentation writes
    /// SUBTYPE = float8, so this is the spelling users are most likely to copy.
    /// </summary>
    [Theory]
    [InlineData("float8", "double precision")]
    [InlineData("int8", "bigint")]
    [InlineData("timestamptz", "timestamp with time zone")]
    // `int` is a grammar keyword rather than a generic type name, so it never reaches the alias
    // table at all -- the numeric rule resolves it. Pinned here because the range-subtype path
    // used to carry its own alias map that listed it, and this is what makes dropping that map
    // safe rather than merely plausible.
    [InlineData("int", "integer")]
    public async Task RangeSubtypeAlias_ProducesTheSameModel(string alias, string spelledOut)
    {
        var aliasModel = await ParseModelAsync($"CREATE TYPE r AS RANGE (SUBTYPE = {alias});");
        var spelledOutModel = await ParseModelAsync($"CREATE TYPE r AS RANGE (SUBTYPE = {spelledOut});");

        var comparison = SchemaCompare.Compare(Provider, aliasModel, spelledOutModel);

        Assert.Empty(comparison.Deltas);
    }

    /// <summary>
    /// <c>bpchar(n)</c> models identically to <c>character(n)</c>: measured on postgres:latest,
    /// <c>format_type()</c> renders a <c>bpchar(4)</c> column as <c>character(4)</c>.
    /// </summary>
    [Fact]
    public async Task BpcharWithLength_ProducesTheSameModelAsCharacter()
    {
        var bpcharModel = await ParseModelAsync("CREATE TABLE t (c bpchar(4));");
        var characterModel = await ParseModelAsync("CREATE TABLE t (c character(4));");

        var comparison = SchemaCompare.Compare(Provider, bpcharModel, characterModel);

        Assert.Empty(comparison.Deltas);
    }

    /// <summary>
    /// A bare <c>bpchar</c> is not an alias for a bare <c>character</c> and must keep diffing
    /// against it. Measured on postgres:latest, a bare <c>character</c> column is
    /// <c>character(1)</c> while a bare <c>bpchar</c> column is unbounded, so treating them as one
    /// type would model a column as something the server would not agree with.
    /// </summary>
    [Fact]
    public async Task BareBpchar_IsNotFoldedIntoCharacter()
    {
        var bpcharModel = await ParseModelAsync("CREATE TABLE t (c bpchar);");
        var characterModel = await ParseModelAsync("CREATE TABLE t (c character);");

        var comparison = SchemaCompare.Compare(Provider, bpcharModel, characterModel);

        Assert.NotEmpty(comparison.Deltas);
    }
}
