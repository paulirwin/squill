using Squill.PostgresParser.Syntax;
using Squill.TestFramework;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// The trailing <c>createstmt</c> clauses that parsed and were then never read by the visitor,
/// so each vanished with no error or warning (issue #206): <c>USING &lt;access method&gt;</c>,
/// <c>WITH (...)</c> storage parameters, and <c>TABLESPACE</c>. All three are now carried on the
/// syntax tree so the provider can decide, per clause, between accepting, warning and rejecting.
///
/// <para>
/// The fourth clause the issue lists, <c>ON COMMIT</c>, is deliberately not carried: it is legal
/// only on a temporary table. Measured against PostgreSQL 18, a non-temporary table declaring it
/// is refused by the server itself ("ON COMMIT can only be used on temporary tables"), and a
/// temporary one is refused by Squill's own build (issue #204). There is no reachable path on
/// which carrying it would change an outcome.
/// </para>
/// </summary>
public class CreateTableStorageClauseTests
{
    private static CreateTableStatement ParseOne(string text)
        => ParseAssertions.Single<CreateTableStatement>(new AntlrPostgresParser().Parse(text).Statements);

    [Fact]
    public void CreateTable_NoStorageClauses_LeavesThemAllNull()
    {
        var createTable = ParseOne("CREATE TABLE t (id integer);");

        Assert.Null(createTable.AccessMethod);
        Assert.Null(createTable.TableSpace);
        Assert.Empty(createTable.WithOptions);
    }

    [Fact]
    public void CreateTable_Using_CarriesTheAccessMethod()
    {
        var createTable = ParseOne("CREATE TABLE t (id integer) USING columnar;");

        Assert.Equal("columnar", createTable.AccessMethod?.Name);
    }

    [Fact]
    public void CreateTable_Tablespace_CarriesTheName()
    {
        var createTable = ParseOne("CREATE TABLE t (id integer) TABLESPACE fast_ssd;");

        Assert.Equal("fast_ssd", createTable.TableSpace?.Name);
    }

    [Fact]
    public void CreateTable_With_CarriesEachStorageParameter()
    {
        var createTable = ParseOne(
            "CREATE TABLE t (id integer) WITH (fillfactor = 70, autovacuum_enabled = false);");

        Assert.Collection(
            createTable.WithOptions,
            o =>
            {
                Assert.Equal("fillfactor", o.Name);
                Assert.Equal("70", o.Value);
            },
            o =>
            {
                Assert.Equal("autovacuum_enabled", o.Name);
                Assert.Equal("false", o.Value);
            });
    }

    /// <summary>
    /// A <c>toast.</c>-prefixed parameter is qualified, so it exercises the dotted spelling the
    /// storage-parameter rule also admits. Measured on PostgreSQL 18, it lands on the table's
    /// TOAST relation rather than the table's own <c>reloptions</c>, which is why it is still a
    /// real storage decision worth reporting rather than dropping.
    /// </summary>
    [Fact]
    public void CreateTable_With_CarriesAQualifiedStorageParameter()
    {
        var createTable = ParseOne(
            "CREATE TABLE t (id integer, v text) WITH (toast.autovacuum_enabled = false);");

        var option = Assert.Single(createTable.WithOptions);

        Assert.Equal("toast.autovacuum_enabled", option.Name);
        Assert.Equal("false", option.Value);
    }

    /// <summary>
    /// A bare parameter with no <c>= value</c> is legal and means "on"; it must not be dropped
    /// just because the value half is absent.
    /// </summary>
    [Fact]
    public void CreateTable_With_CarriesAValuelessStorageParameter()
    {
        var createTable = ParseOne("CREATE TABLE t (id integer) WITH (user_catalog_table);");

        var option = Assert.Single(createTable.WithOptions);

        Assert.Equal("user_catalog_table", option.Name);
        Assert.Null(option.Value);
    }

    /// <summary>
    /// <c>optwith</c>'s other alternative. <c>WITHOUT OIDS</c> declares no storage parameter at
    /// all, and since PostgreSQL 12 removed OID columns it is accepted purely for compatibility,
    /// so it must leave the option list empty rather than inventing an entry.
    /// </summary>
    [Fact]
    public void CreateTable_WithoutOids_CarriesNoStorageParameters()
    {
        var createTable = ParseOne("CREATE TABLE t (id integer) WITHOUT OIDS;");

        Assert.Empty(createTable.WithOptions);
    }

    /// <summary>
    /// All three clauses together, in the order <c>createstmt</c> requires, to prove reading one
    /// does not consume another.
    /// </summary>
    [Fact]
    public void CreateTable_AllStorageClauses_AreCarriedTogether()
    {
        var createTable = ParseOne(
            "CREATE TABLE t (id integer) USING heap WITH (fillfactor = 70) TABLESPACE pg_default;");

        Assert.Equal("heap", createTable.AccessMethod?.Name);
        Assert.Equal("pg_default", createTable.TableSpace?.Name);

        var option = Assert.Single(createTable.WithOptions);
        Assert.Equal("fillfactor", option.Name);
        Assert.Equal("70", option.Value);
    }
}
