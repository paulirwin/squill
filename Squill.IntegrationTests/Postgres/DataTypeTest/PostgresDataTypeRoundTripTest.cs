using Squill.Core;
using Squill.PostgresParser;
using Squill.Provider.Postgres;
using Squill.TestFramework;

namespace Squill.IntegrationTests.Postgres.DataTypeTest;

/// <summary>
/// Exhaustive end-to-end round-trip coverage for PostgreSQL column data types (issue #97):
/// each type is declared in a table, published into a real Postgres database, re-extracted,
/// and asserted to hash-match the parser-built model — proving the DDL we generate is valid,
/// executable Postgres and that the parser and DB model builders agree on the type's shape.
///
/// Types are grounded in the PostgreSQL data-type documentation:
/// https://www.postgresql.org/docs/current/datatype.html
/// </summary>
public class PostgresDataTypeRoundTripTest : PostgresIntegrationTestBase
{
    private async Task AssertColumnRoundTripsAsync(string columnType, CancellationToken cancellationToken)
        => await RoundTripHarness.AssertRoundTripAsync(
            new PostgresDatabaseProvider(ConnectionString),
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrPostgresParser()),
            $"CREATE TABLE t (c {columnType});",
            "postgres",
            assertRedeployNoOp: true,
            cancellationToken);

    // Numeric types: https://www.postgresql.org/docs/current/datatype-numeric.html
    [Theory]
    [InlineData("smallint")]
    [InlineData("integer")]
    [InlineData("bigint")]
    [InlineData("numeric")]
    [InlineData("numeric(10, 2)")]
    [InlineData("real")]
    [InlineData("double precision")]
    public async Task NumericTypes_RoundTrip(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);

    // Character types: https://www.postgresql.org/docs/current/datatype-character.html
    [Theory]
    [InlineData("varchar")]
    [InlineData("varchar(255)")]
    [InlineData("char(10)")]
    [InlineData("text")]
    public async Task CharacterTypes_RoundTrip(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);

    // Boolean type: https://www.postgresql.org/docs/current/datatype-boolean.html
    [Theory]
    [InlineData("boolean")]
    public async Task BooleanType_RoundTrips(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);

    // Date/time types: https://www.postgresql.org/docs/current/datatype-datetime.html
    [Theory]
    [InlineData("date")]
    [InlineData("time")]
    [InlineData("time with time zone")]
    [InlineData("timestamp")]
    [InlineData("timestamp with time zone")]
    [InlineData("interval")]
    public async Task DateTimeTypes_RoundTrip(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);

    // Bit-string types: https://www.postgresql.org/docs/current/datatype-bit.html
    [Theory]
    [InlineData("bit")]
    [InlineData("bit(8)")]
    [InlineData("bit varying")]
    [InlineData("bit varying(16)")]
    public async Task BitStringTypes_RoundTrip(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);

    // Binary data: https://www.postgresql.org/docs/current/datatype-binary.html
    [Theory]
    [InlineData("bytea")]
    public async Task BinaryType_RoundTrips(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);

    // UUID: https://www.postgresql.org/docs/current/datatype-uuid.html
    [Theory]
    [InlineData("uuid")]
    public async Task UuidType_RoundTrips(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);

    // JSON types: https://www.postgresql.org/docs/current/datatype-json.html
    [Theory]
    [InlineData("json")]
    [InlineData("jsonb")]
    public async Task JsonTypes_RoundTrip(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);

    // XML: https://www.postgresql.org/docs/current/datatype-xml.html
    [Theory]
    [InlineData("xml")]
    public async Task XmlType_RoundTrips(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);

    // Monetary: https://www.postgresql.org/docs/current/datatype-money.html
    [Theory]
    [InlineData("money")]
    public async Task MoneyType_RoundTrips(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);

    // Network address types: https://www.postgresql.org/docs/current/datatype-net-types.html
    [Theory]
    [InlineData("inet")]
    [InlineData("cidr")]
    [InlineData("macaddr")]
    [InlineData("macaddr8")]
    public async Task NetworkTypes_RoundTrip(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);

    // Geometric types: https://www.postgresql.org/docs/current/datatype-geometric.html
    [Theory]
    [InlineData("point")]
    [InlineData("line")]
    [InlineData("lseg")]
    [InlineData("box")]
    [InlineData("path")]
    [InlineData("polygon")]
    [InlineData("circle")]
    public async Task GeometricTypes_RoundTrip(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);

    // Text-search types: https://www.postgresql.org/docs/current/datatype-textsearch.html
    [Theory]
    [InlineData("tsvector")]
    [InlineData("tsquery")]
    public async Task TextSearchTypes_RoundTrip(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);

    // Array types: https://www.postgresql.org/docs/current/arrays.html
    [Theory]
    [InlineData("integer[]")]
    [InlineData("text[]")]
    [InlineData("varchar[]")]
    [InlineData("numeric(10, 2)[]")]
    public async Task ArrayTypes_RoundTrip(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);
}
