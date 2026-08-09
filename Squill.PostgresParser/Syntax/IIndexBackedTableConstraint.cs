namespace Squill.PostgresParser.Syntax;

/// <summary>
/// The clauses a constraint backed by an index accepts beyond its key columns (issue #210):
/// <c>INCLUDE (...)</c>, <c>WITH (...)</c> storage parameters and
/// <c>USING INDEX TABLESPACE</c>.
///
/// Implemented by <see cref="PrimaryKeyTableConstraint"/> and
/// <see cref="UniqueTableConstraint"/>, the two <c>constraintelem</c> alternatives the grammar
/// attaches <c>c_include_ definition_? optconstablespace?</c> to. An interface rather than
/// members on <see cref="TableConstraint"/> because a CHECK or FOREIGN KEY is not backed by an
/// index and accepts none of them, unlike the DEFERRABLE spec every alternative shares.
///
/// Each corresponds to a facet <c>CREATE INDEX</c> already models, so both spellings of the
/// same declaration converge on one representation instead of behaving differently.
/// </summary>
public interface IIndexBackedTableConstraint
{
    /// <summary>
    /// The covering columns of an <c>INCLUDE (...)</c> clause. Empty when absent.
    ///
    /// Stored in the backing index without being part of its key, so they satisfy index-only
    /// scans without participating in uniqueness. Note they DO participate in the name
    /// PostgreSQL derives for an unnamed constraint: measured,
    /// <c>UNIQUE (a, b) INCLUDE (c)</c> is named <c>&lt;table&gt;_a_b_c_key</c>.
    /// </summary>
    IList<Identifier> IncludeColumns { get; }

    /// <summary>
    /// Storage parameters from a <c>WITH (...)</c> clause, e.g. <c>fillfactor = 70</c>. Empty
    /// when absent.
    ///
    /// Unlike INCLUDE these are not part of the constraint's own definition: measured, they do
    /// not appear in <c>pg_get_constraintdef</c> at all and live on the backing index's
    /// <c>pg_class.reloptions</c>.
    /// </summary>
    IList<IndexWithOption> WithOptions { get; }

    /// <summary>
    /// The tablespace the backing index is created in
    /// (<c>USING INDEX TABLESPACE fast_ssd</c>), or null for the default.
    /// </summary>
    Identifier? TableSpace { get; set; }
}
