using Squill.Core;
using Squill.MariaDbParser;
using Squill.Provider.MariaDb;

namespace Squill.IntegrationTests.MariaDb;

/// <summary>
/// End-to-end round trips for the type modifiers that used to be parsed and discarded
/// (issue #217). Each scenario runs once against MariaDB and once against MySQL.
///
/// <para>
/// The round trip is the whole point here, because every one of these is stored by the engines
/// as something other than what was written. A <c>CHARACTER SET</c> and a <c>BINARY</c> suffix
/// both become a collation; <c>LONG VARBINARY</c> becomes <c>mediumblob</c>; a <c>VECTOR(3)</c>
/// reports its dimension in <c>COLUMN_TYPE</c> but its byte count in
/// <c>CHARACTER_MAXIMUM_LENGTH</c>. A unit test can only assert what Squill believes about any
/// of that. Publishing, re-extracting and asserting the redeploy is a no-op is what shows the
/// belief matches the server.
/// </para>
/// </summary>
public abstract class MariaDbTypeModifierTests
{
    protected abstract MariaDbLikeFixture Fixture { get; }

    private async Task AssertRoundTripsAsync(string sql, CancellationToken cancellationToken)
        => await RoundTripHarness.AssertRoundTripAsync(
            new MariaDbDatabaseProvider(Fixture.ConnectionString),
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrMariaDbParser(), Fixture.SchemaProviderOf()),
            sql,
            Fixture.EngineName,
            assertRedeployNoOp: true,
            cancellationToken);

    private Task AssertColumnRoundTripsAsync(string columnType, CancellationToken cancellationToken)
        => AssertRoundTripsAsync($"CREATE TABLE t (c {columnType});", cancellationToken);

    /// <summary>
    /// A per-column character set. This is the case the issue called out as a correctness
    /// problem rather than a fidelity one: the charset decides the collation, the collation
    /// decides comparison and sort order, and before this the clause was dropped with no
    /// diagnostic, so the deployed column compared differently from the declared one.
    /// </summary>
    [Theory]
    [InlineData("varchar(10) CHARACTER SET latin1")]
    [InlineData("varchar(10) CHARACTER SET ascii")]
    [InlineData("char(5) CHARACTER SET latin1")]
    [InlineData("text CHARACTER SET latin1")]
    // Quoted spellings name the same character set as the bare one.
    [InlineData("varchar(10) CHARACTER SET 'latin1'")]
    public async Task CharacterSet_RoundTrips(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);

    /// <summary>
    /// A character set alongside an explicit collation of the same family. The collation wins,
    /// and naming both must not double-resolve into something neither engine reports.
    /// </summary>
    [Theory]
    [InlineData("varchar(10) CHARACTER SET latin1 COLLATE latin1_bin")]
    [InlineData("char(5) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin")]
    public async Task CharacterSetWithCollate_RoundTrips(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);

    /// <summary>
    /// The BINARY suffix, which selects the binary collation of whichever character set is in
    /// play rather than naming a type or a charset of its own.
    /// </summary>
    [Theory]
    [InlineData("varchar(10) BINARY")]
    [InlineData("char(5) BINARY")]
    [InlineData("varchar(10) CHARACTER SET latin1 BINARY")]
    public async Task BinarySuffix_RoundTrips(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);

    /// <summary>
    /// A BINARY suffix in a table whose own COLLATE changes what it resolves against. This is
    /// the case that proves the resolution reads the table's character set rather than assuming
    /// the engine default: here the column must come back <c>latin1_bin</c>, not
    /// <c>utf8mb4_bin</c>.
    /// </summary>
    [Fact]
    public async Task BinarySuffix_InATableWithItsOwnCollation_RoundTrips()
        => await AssertRoundTripsAsync(
            """
            CREATE TABLE t (c varchar(10) BINARY)
            COLLATE=latin1_general_ci;
            """,
            TestContext.Current.CancellationToken);

    /// <summary>
    /// A column whose character set differs from the table's, which is the arrangement a
    /// per-column charset exists for. Both the table-level and column-level values have to
    /// survive, and the column's must not be mistaken for the one it inherits.
    /// </summary>
    [Fact]
    public async Task CharacterSet_DifferingFromTheTable_RoundTrips()
        => await AssertRoundTripsAsync(
            """
            CREATE TABLE t
            (
                inherits_table varchar(10),
                declares_own varchar(10) CHARACTER SET ascii
            )
            COLLATE=latin1_general_ci;
            """,
            TestContext.Current.CancellationToken);

    /// <summary>
    /// <c>LONG</c>, <c>LONG VARCHAR</c> and <c>LONG VARBINARY</c>. Before this these modeled as
    /// the bare token <c>long</c>, which is not a type either engine has: the generated DDL was
    /// rejected outright, so this scenario could not deploy at all.
    /// </summary>
    [Theory]
    [InlineData("LONG")]
    [InlineData("LONG VARCHAR")]
    [InlineData("LONG VARBINARY")]
    public async Task LongTypes_RoundTrip(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);

    /// <summary>
    /// The character and binary <c>LONG</c> spellings in one table, which is what proves they
    /// are not collapsed onto each other: a blob column deployed as text would still round-trip
    /// on its own, because both sides would agree on the same wrong answer.
    /// </summary>
    [Fact]
    public async Task LongTypes_CharacterAndBinaryTogether_RoundTrip()
        => await AssertRoundTripsAsync(
            "CREATE TABLE t (a LONG VARCHAR, b LONG VARBINARY);",
            TestContext.Current.CancellationToken);

    /// <summary>
    /// A vector's dimension. The trap this covers is the catalog reporting
    /// <c>CHARACTER_MAXIMUM_LENGTH</c> as the storage size in bytes (measured: 12 for a
    /// <c>VECTOR(3)</c>), so reading the dimension the way a varchar's length is read would
    /// re-diff on every deploy, which only the no-op redeploy assertion catches.
    /// </summary>
    [Theory]
    [InlineData("VECTOR(3)")]
    [InlineData("VECTOR(1536)")]
    public async Task Vector_RoundTrips(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);
}

// ---- Per-engine bindings: each scenario runs once against MariaDB and once against MySQL. ----

public sealed class MariaDbTypeModifierTestsMariaDb(MariaDbFixture fixture)
    : MariaDbTypeModifierTests, IClassFixture<MariaDbFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

public sealed class MariaDbTypeModifierTestsMySql(MySqlFixture fixture)
    : MariaDbTypeModifierTests, IClassFixture<MySqlFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}
