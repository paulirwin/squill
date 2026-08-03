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

    /// <summary>
    /// The internal aliases PostgreSQL accepts for its built-in types (issue #197). None is a
    /// keyword in the grammar, so before the fix each was modeled as written while the catalog
    /// reported only the spelled-out name — the two never hash-matched and the column re-diffed on
    /// every deploy. Each spelled-out form is already covered above; what these prove is that the
    /// abbreviation produces the identical model.
    /// </summary>
    [Theory]
    [InlineData("int2")]
    [InlineData("int4")]
    [InlineData("int8")]
    [InlineData("float4")]
    [InlineData("float8")]
    [InlineData("bool")]
    [InlineData("varbit")]
    [InlineData("varbit(8)")]
    [InlineData("timetz")]
    [InlineData("timestamptz")]
    public async Task TypeNameAliases_RoundTrip(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);

    /// <summary>
    /// An alias as an array element type, which is a separate path through the visitor: the
    /// element type is resolved inside the array rather than at the top level.
    /// </summary>
    [Theory]
    [InlineData("int8[]")]
    [InlineData("timestamptz[]")]
    public async Task TypeNameAliasArrays_RoundTrip(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);

    /// <summary>
    /// <c>bpchar(n)</c> is the one <c>bpchar</c> spelling that is genuinely an alias: measured on
    /// postgres:latest, a <c>bpchar(4)</c> column is rendered <c>character(4)</c> by
    /// <c>format_type()</c> and reported as <c>character</c> with length 4, exactly as a column
    /// declared <c>character(4)</c> is (issue #197).
    ///
    /// <para>
    /// Bare <c>bpchar</c> is deliberately absent, and is <em>not</em> an alias for bare
    /// <c>character</c>: measured, a bare <c>character</c> column is <c>character(1)</c> while a
    /// bare <c>bpchar</c> column is unbounded, with <c>format_type()</c> reporting <c>bpchar</c>.
    /// It does not round-trip today — extraction reports it as <c>character</c> with no length,
    /// a shape the parser has no way to declare — but that is a pre-existing gap in the unbounded
    /// character types rather than anything the alias work touches, and folding it into
    /// <c>character</c> would model a column as a type the server would disagree with.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("bpchar(4)")]
    public async Task Bpchar_RoundTrips(string columnType)
        => await AssertColumnRoundTripsAsync(columnType, TestContext.Current.CancellationToken);
}
