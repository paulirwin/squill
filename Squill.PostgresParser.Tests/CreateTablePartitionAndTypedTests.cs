using Squill.PostgresParser.Syntax;
using Squill.TestFramework;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// The <c>CREATE TABLE</c> forms that are neither the ordinary parenthesized element list nor
/// covered elsewhere: the typed-table form (<c>OF a_type</c>), the partition-child form
/// (<c>PARTITION OF parent FOR VALUES ...</c>), and the partitioned-parent form
/// (<c>PARTITION BY ...</c>) — issue #143.
///
/// All three parse and carry their clauses on the syntax tree. None is modeled: a partition
/// child's shape is owned by its parent, a typed table's by its composite type, and Squill's
/// model has no notion of either relationship. The provider reports each as an SQ1002
/// unmodeled-construct warning rather than throwing, so the rest of the project still builds.
///
/// <c>PARTITION BY</c> is the odd one out — it already parsed before #143, because
/// <c>optpartitionspec</c> was simply never read. That is the silent-drop failure mode
/// CLAUDE.md warns about under #141, so it is now carried and warned about like the others.
/// </summary>
public class CreateTablePartitionAndTypedTests
{
    private static CreateTableStatement ParseOne(string text)
        => ParseAssertions.Single<CreateTableStatement>(new AntlrPostgresParser().Parse(text).Statements);

    [Fact]
    public void CreateTable_OfType_CarriesTheTypeName()
    {
        var createTable = ParseOne("CREATE TABLE employees OF employee_type;");

        Assert.Equal("employees", createTable.Name.ToString());
        Assert.Equal("employee_type", createTable.OfType?.ToString());
        Assert.Null(createTable.PartitionOf);
        Assert.Empty(createTable.Elements);
    }

    [Fact]
    public void CreateTable_OfType_SchemaQualified()
    {
        var createTable = ParseOne("CREATE TABLE hr.employees OF hr.employee_type;");

        Assert.Equal("hr.employees", createTable.Name.ToString());
        Assert.Equal("hr.employee_type", createTable.OfType?.ToString());
    }

    /// <summary>
    /// A typed table may still constrain its inherited columns, so the element list is parsed
    /// as usual when present.
    /// </summary>
    [Fact]
    public void CreateTable_OfType_WithColumnConstraints()
    {
        var createTable = ParseOne("CREATE TABLE employees OF employee_type (id WITH OPTIONS PRIMARY KEY);");

        Assert.Equal("employee_type", createTable.OfType?.ToString());

        var column = Assert.IsType<ColumnDefinition>(Assert.Single(createTable.Elements));
        Assert.Equal("id", column.Name.Name);
        Assert.IsType<PrimaryKeyColumnConstraint>(Assert.Single(column.Constraints));
    }

    [Fact]
    public void CreateTable_PartitionOf_ForValuesFromTo()
    {
        var createTable = ParseOne("""
CREATE TABLE measurement_y2024 PARTITION OF measurement
    FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');
""");

        Assert.Equal("measurement_y2024", createTable.Name.ToString());
        Assert.Equal("measurement", createTable.PartitionOf?.ToString());
        Assert.NotNull(createTable.PartitionBound);
        Assert.Contains("FROM", createTable.PartitionBound, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateTable_PartitionOf_ForValuesIn()
    {
        var createTable = ParseOne("""
CREATE TABLE cities_east PARTITION OF cities FOR VALUES IN ('NY', 'MA');
""");

        Assert.Equal("cities", createTable.PartitionOf?.ToString());
        Assert.NotNull(createTable.PartitionBound);
        Assert.Contains("IN", createTable.PartitionBound, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateTable_PartitionOf_ForValuesWithHash()
    {
        var createTable = ParseOne("""
CREATE TABLE orders_p0 PARTITION OF orders FOR VALUES WITH (MODULUS 4, REMAINDER 0);
""");

        Assert.Equal("orders", createTable.PartitionOf?.ToString());
        Assert.NotNull(createTable.PartitionBound);
        Assert.Contains("MODULUS", createTable.PartitionBound, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateTable_PartitionOf_Default()
    {
        var createTable = ParseOne("CREATE TABLE cities_other PARTITION OF cities DEFAULT;");

        Assert.Equal("cities", createTable.PartitionOf?.ToString());
        Assert.Equal("DEFAULT", createTable.PartitionBound, ignoreCase: true);
    }

    /// <summary>
    /// The partitioned-parent form. This parsed before #143 too, but the PARTITION BY clause
    /// was dropped on the floor — so a partitioned parent deployed as an ordinary table.
    /// </summary>
    [Fact]
    public void CreateTable_PartitionBy_CarriesThePartitionSpec()
    {
        var createTable = ParseOne("""
CREATE TABLE measurement
(
    logdate date NOT NULL,
    peaktemp integer
) PARTITION BY RANGE (logdate);
""");

        Assert.Equal("measurement", createTable.Name.ToString());
        Assert.Equal(2, createTable.Elements.Count);
        Assert.NotNull(createTable.PartitionBy);
        Assert.Contains("RANGE", createTable.PartitionBy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("logdate", createTable.PartitionBy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateTable_PartitionBy_List()
    {
        var createTable = ParseOne("""
CREATE TABLE cities (name text NOT NULL, state char(2)) PARTITION BY LIST (state);
""");

        Assert.NotNull(createTable.PartitionBy);
        Assert.Contains("LIST", createTable.PartitionBy, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An ordinary table carries none of these, so nothing warns for the common case.</summary>
    [Fact]
    public void CreateTable_Ordinary_CarriesNoPartitionOrTypeClause()
    {
        var createTable = ParseOne("CREATE TABLE t (id integer PRIMARY KEY);");

        Assert.Null(createTable.OfType);
        Assert.Null(createTable.PartitionOf);
        Assert.Null(createTable.PartitionBound);
        Assert.Null(createTable.PartitionBy);
    }
}
