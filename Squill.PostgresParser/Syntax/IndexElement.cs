namespace Squill.PostgresParser.Syntax;

public class IndexElement : SyntaxNode
{
    public IndexElement(Expression expression,
        IndexElementDirection? direction,
        IndexElementNullOrder? nullOrder,
        QualifiedName? operatorClass = null,
        QualifiedName? collation = null,
        IEnumerable<IndexWithOption>? operatorClassParameters = null)
    {
        Expression = expression;
        Direction = direction;
        NullOrder = nullOrder;
        OperatorClass = operatorClass;
        Collation = collation;

        if (operatorClassParameters is not null)
        {
            foreach (var parameter in operatorClassParameters)
            {
                OperatorClassParameters.Add(parameter);
            }
        }
    }

    public Expression Expression { get; }

    public IndexElementDirection? Direction { get; }

    public IndexElementNullOrder? NullOrder { get; }

    /// <summary>
    /// The operator class for this index element (e.g. <c>vector_cosine_ops</c> on an
    /// HNSW index). Null when the default operator class for the type is used.
    ///
    /// Carried as a <see cref="QualifiedName"/> because the grammar's <c>class_</c> is an
    /// <c>any_name</c>: a user may write <c>pg_catalog.text_pattern_ops</c> to disambiguate an
    /// opclass shadowed by one in another schema (issue #160).
    /// </summary>
    public QualifiedName? OperatorClass { get; }

    /// <summary>
    /// The collation this index element sorts and compares by, from a per-key-column
    /// <c>COLLATE "collation"</c> (issue #160). Null when the column's own collation is used.
    ///
    /// A <c>collate_</c> is part of <c>index_elem_options</c> alongside the operator class and
    /// the ordering keywords, and was the one of the four the visitor never read — so an index
    /// deployed with the default collation while the source declared another, and neither side
    /// of the round trip noticed. Distinct from <see cref="CollateColumnConstraint"/>, which
    /// declares a collation on a table column rather than on an index key.
    /// </summary>
    public QualifiedName? Collation { get; }

    /// <summary>
    /// The parameters of a parameterized operator class (PostgreSQL 13+), e.g. the
    /// <c>siglen = 256</c> in <c>gist (tsv tsvector_ops(siglen=256))</c>. Empty when the opclass
    /// takes none (issue #211).
    /// </summary>
    /// <remarks>
    /// These arrive on the second alternative of <c>index_elem_options</c>, which the visitor
    /// never read, so before #211 a parameterized key lost the parameters <em>and</em> the
    /// <see cref="OperatorClass"/>, since both live on that alternative.
    ///
    /// Measured, PostgreSQL rejects the parameters without an explicit opclass name
    /// (<c>gist (tsv (siglen=256))</c> fails with "column siglen does not exist"), so the name
    /// must be scripted alongside them even when it is the type's default opclass, which is
    /// why the extractor cannot suppress it on <c>opcdefault</c> the way it does for a bare one.
    ///
    /// Shares <see cref="IndexWithOption"/> with the index-level <c>WITH (...)</c> clause: both
    /// are free-form name/value pairs whose meaning is defined by the access method, and the
    /// catalog reports them in the same <c>name=value</c> spelling.
    /// </remarks>
    public IList<IndexWithOption> OperatorClassParameters { get; } = new List<IndexWithOption>();
}
