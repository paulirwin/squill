namespace Squill.PostgresParser.Syntax;

public class IndexElement : SyntaxNode
{
    public IndexElement(Expression expression,
        IndexElementDirection? direction,
        IndexElementNullOrder? nullOrder,
        QualifiedName? operatorClass = null,
        QualifiedName? collation = null)
    {
        Expression = expression;
        Direction = direction;
        NullOrder = nullOrder;
        OperatorClass = operatorClass;
        Collation = collation;
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
}