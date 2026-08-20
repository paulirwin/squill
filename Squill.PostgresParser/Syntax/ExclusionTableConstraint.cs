namespace Squill.PostgresParser.Syntax;

/// <summary>
/// An exclusion constraint: <c>EXCLUDE USING gist (room WITH =, during WITH &amp;&amp;)</c>
/// (issue #212).
///
/// Generalises UNIQUE. Where UNIQUE forbids two rows whose keys are equal, EXCLUDE forbids any
/// pair of rows for which every element's operator returns true, so the canonical use is
/// preventing overlapping ranges. There is no other declarative way to express that in
/// PostgreSQL.
///
/// Backed by an index like a primary key or unique constraint, hence
/// <see cref="IIndexBackedTableConstraint"/>: the grammar hangs the same
/// <c>c_include_ definition_? optconstablespace?</c> off this alternative.
/// </summary>
public class ExclusionTableConstraint : TableConstraint, IIndexBackedTableConstraint
{
    public ExclusionTableConstraint(IEnumerable<ExclusionConstraintElement> elements)
    {
        Elements = elements.ToList();
    }

    /// <summary>
    /// The <c>key WITH operator</c> pairs, in declaration order.
    /// </summary>
    public IReadOnlyList<ExclusionConstraintElement> Elements { get; }

    /// <summary>
    /// The index access method from <c>USING &lt;method&gt;</c>, or null when omitted.
    ///
    /// Measured: PostgreSQL always reports one back (an omitted method comes back as
    /// <c>USING btree</c>), so the model defaults it rather than storing the absence, which
    /// would otherwise make every bare EXCLUDE re-diff on every deploy.
    /// </summary>
    public Identifier? AccessMethod { get; set; }

    /// <summary>
    /// The <c>WHERE (...)</c> predicate restricting which rows the constraint applies to, or
    /// null when it applies to all of them. Distinct from a CHECK: this selects the rows
    /// participating, it does not itself reject any.
    /// </summary>
    public Expression? WhereClause { get; set; }

    /// <inheritdoc />
    public IList<Identifier> IncludeColumns { get; } = new List<Identifier>();

    /// <inheritdoc />
    public IList<IndexWithOption> WithOptions { get; } = new List<IndexWithOption>();

    /// <inheritdoc />
    public Identifier? TableSpace { get; set; }
}
