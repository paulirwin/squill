using Squill.Core;
using Squill.MariaDbParser;
using Squill.Provider.MariaDb;

namespace Squill.IntegrationTests.MariaDb;

/// <summary>
/// Exhaustive end-to-end round-trip coverage for MariaDB/MySQL column data types (issue #97):
/// each type is declared in a table, published into a fresh database, re-extracted, and
/// asserted to hash-match the parser-built model — proving the DDL we generate is valid,
/// executable SQL and that the parser and DB model builders agree on the type's shape. Each
/// scenario runs once against MariaDB and once against MySQL via the concrete subclasses.
///
/// Types are grounded in the MariaDB data-types documentation:
/// https://mariadb.com/kb/en/data-types/ (and the MySQL equivalent
/// https://dev.mysql.com/doc/refman/8.0/en/data-types.html).
/// </summary>
public abstract class MariaDbDataTypeRoundTripTests
{
    protected abstract MariaDbLikeFixture Fixture { get; }

    private async Task AssertColumnRoundTripsAsync(string columnType, CancellationToken cancellationToken)
        => await RoundTripHarness.AssertRoundTripAsync(
            new MariaDbDatabaseProvider(Fixture.ConnectionString),
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrMariaDbParser(), (MariaDbFamilyDatabaseSchemaProvider)Fixture.SchemaProvider),
            $"CREATE TABLE t (c {columnType});",
            Fixture.EngineName,
            assertRedeployNoOp: true,
            cancellationToken);

    // Integer types: https://mariadb.com/kb/en/integer-types/
    [Theory]
    [InlineData("tinyint")]
    [InlineData("smallint")]
    [InlineData("mediumint")]
    [InlineData("int")]
    [InlineData("integer")]
    [InlineData("bigint")]
    [InlineData("int unsigned")]
    [InlineData("bigint unsigned")]
    public async Task IntegerTypes_RoundTrip(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);

    // Fixed-point and floating-point: https://mariadb.com/kb/en/fixed-point-and-floating-point/
    [Theory]
    [InlineData("decimal(10, 2)")]
    [InlineData("numeric(10, 2)")]
    [InlineData("float")]
    [InlineData("double")]
    public async Task DecimalAndFloatTypes_RoundTrip(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);

    // String types: https://mariadb.com/kb/en/string-data-types/
    [Theory]
    [InlineData("char(10)")]
    [InlineData("varchar(255)")]
    [InlineData("tinytext")]
    [InlineData("text")]
    [InlineData("mediumtext")]
    [InlineData("longtext")]
    public async Task StringTypes_RoundTrip(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);

    // Binary/BLOB types: https://mariadb.com/kb/en/binary-data-types/
    [Theory]
    [InlineData("binary(16)")]
    [InlineData("varbinary(255)")]
    [InlineData("tinyblob")]
    [InlineData("blob")]
    [InlineData("mediumblob")]
    [InlineData("longblob")]
    public async Task BinaryTypes_RoundTrip(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);

    // Date and time types: https://mariadb.com/kb/en/date-and-time-data-types/
    [Theory]
    [InlineData("date")]
    [InlineData("datetime")]
    [InlineData("timestamp")]
    [InlineData("time")]
    [InlineData("year")]
    public async Task DateTimeTypes_RoundTrip(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);

    // Enum and set: https://mariadb.com/kb/en/enum/ and https://mariadb.com/kb/en/set-data-type/
    [Theory]
    [InlineData("enum('a','b','c')")]
    [InlineData("set('x','y','z')")]
    public async Task CollectionTypes_RoundTrip(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);

    // Bit: https://mariadb.com/kb/en/bit/
    [Theory]
    [InlineData("bit(8)")]
    public async Task BitType_RoundTrips(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);

    // Boolean is a synonym for tinyint(1) on both engines; a bare BOOL is stored (and
    // reported by information_schema) as tinyint(1). https://mariadb.com/kb/en/boolean/
    [Theory]
    [InlineData("bool")]
    [InlineData("boolean")]
    public async Task BooleanType_RoundTrips(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);

    // JSON: MariaDB stores JSON columns as longtext (with a CHECK constraint); MySQL keeps a
    // distinct json type. https://mariadb.com/kb/en/json-data-type/
    [Theory]
    [InlineData("json")]
    public async Task JsonType_RoundTrips(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);
}

// ---- Per-engine bindings: each scenario runs once against MariaDB and once against MySQL. ----

public sealed class MariaDbDataTypeRoundTripTestsMariaDb(MariaDbFixture fixture)
    : MariaDbDataTypeRoundTripTests, IClassFixture<MariaDbFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

public sealed class MariaDbDataTypeRoundTripTestsMySql(MySqlFixture fixture)
    : MariaDbDataTypeRoundTripTests, IClassFixture<MySqlFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}
