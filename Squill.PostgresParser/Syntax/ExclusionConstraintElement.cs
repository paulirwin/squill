namespace Squill.PostgresParser.Syntax;

/// <summary>
/// One <c>key WITH operator</c> pair inside an <c>EXCLUDE</c> constraint (issue #212):
/// the index key to compare, and the operator two rows' values of that key are compared with.
///
/// The key half is an <see cref="IndexElement"/> because the grammar's
/// <c>exclusionconstraintelem</c> is literally <c>index_elem WITH ...</c>: an exclusion key
/// accepts everything an index key does, including an expression, an operator class, a
/// collation and an ordering. Reusing the node keeps the two spellings of the same key on one
/// representation rather than letting them drift.
/// </summary>
public class ExclusionConstraintElement : SyntaxNode
{
    public ExclusionConstraintElement(IndexElement key, QualifiedName @operator)
    {
        Key = key;
        Operator = @operator;
    }

    /// <summary>
    /// The index key this element excludes on.
    /// </summary>
    public IndexElement Key { get; }

    /// <summary>
    /// The comparison operator, e.g. <c>=</c> or <c>&amp;&amp;</c>. A constraint is violated
    /// when every element's operator returns true for a pair of rows.
    ///
    /// A <see cref="QualifiedName"/> because both spellings the grammar accepts may name a
    /// schema: the bare <c>WITH =</c> and the explicit <c>WITH OPERATOR(myops.===)</c>.
    /// Measured, PostgreSQL reports an operator resolved in <c>pg_catalog</c> unqualified and
    /// any other one qualified, so the two spellings of a built-in operator converge on the
    /// bare name rather than being kept apart.
    /// </summary>
    public QualifiedName Operator { get; }
}
