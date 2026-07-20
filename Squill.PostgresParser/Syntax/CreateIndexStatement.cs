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
    /// The storage parameters from an optional WITH (...) clause, e.g. the
    /// <c>m</c> and <c>ef_construction</c> of an HNSW index. Empty when absent.
    /// </summary>
    public IList<IndexWithOption> WithOptions { get; } = new List<IndexWithOption>();
}