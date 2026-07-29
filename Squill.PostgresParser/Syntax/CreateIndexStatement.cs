namespace Squill.PostgresParser.Syntax;

public class CreateIndexStatement : Statement
{
    public CreateIndexStatement(Identifier? name,
        RelationExpression onRelation,
        bool unique,
        bool concurrently,
        bool ifNotExists,
        Identifier? usingMethod)
    {
        Name = name;
        Unique = unique;
        Concurrently = concurrently;
        IfNotExists = ifNotExists;
        OnRelation = onRelation;
        UsingMethod = usingMethod;
    }

    public Identifier? Name { get; }

    public bool Unique { get; }

    public bool Concurrently { get; }

    public bool IfNotExists { get; }

    public RelationExpression OnRelation { get; }

    public Identifier? UsingMethod { get; }

    /// <summary>
    /// The optional WHERE predicate that makes this a partial (filtered) index.
    /// Null for a full index.
    /// </summary>
    public Expression? WhereClause { get; set; }

    public IList<IndexElement> Elements { get; } = new List<IndexElement>();

    /// <summary>
    /// The covering columns of an <c>INCLUDE (...)</c> clause (issue #160). Empty when absent.
    ///
    /// Stored in the index but not part of its key: they satisfy index-only scans without
    /// participating in ordering or, on a unique index, in uniqueness. The grammar reuses
    /// <c>index_elem</c> here, so these parse identically to key columns even though PostgreSQL
    /// rejects an ordering or opclass on one.
    /// </summary>
    public IList<IndexElement> IncludeElements { get; } = new List<IndexElement>();

    /// <summary>
    /// Whether the index was declared <c>NULLS NOT DISTINCT</c> (PostgreSQL 15+, issue #160),
    /// which makes NULLs collide with each other instead of being all-distinct.
    ///
    /// Meaningful only on a unique index, and the inverse of the default — so dropping it, as
    /// the visitor used to, deploys an index with the opposite uniqueness semantics from the
    /// one the source declared.
    /// </summary>
    public bool NullsNotDistinct { get; set; }

    /// <summary>
    /// The tablespace from an optional <c>TABLESPACE</c> clause. Null when absent.
    /// </summary>
    public Identifier? TableSpace { get; set; }

    /// <summary>
    /// The storage parameters from an optional WITH (...) clause, e.g. the
    /// <c>m</c> and <c>ef_construction</c> of an HNSW index. Empty when absent.
    /// </summary>
    public IList<IndexWithOption> WithOptions { get; } = new List<IndexWithOption>();
}