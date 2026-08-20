namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A reference to a column, optionally qualified: <c>price</c>, <c>film.price</c>,
/// <c>NEW.price</c>.
/// </summary>
/// <remarks>
/// <see cref="Identifier"/> is the LAST segment, the column itself, so the common unqualified
/// case reads the same as it always has and every existing consumer keeps working.
/// <see cref="Qualifiers"/> carries the leading segments in source order, empty when there are
/// none.
///
/// The qualifier is not decoration. It only reaches a modeled construct through a trigger's
/// <c>WHEN</c> predicate (issue #214). Measured, PostgreSQL strips a table qualifier out of a
/// CHECK constraint (<c>CHECK (t.val &gt; 0)</c> is stored as <c>CHECK ((val &gt; 0))</c>) but
/// keeps <c>new</c>/<c>old</c> in a trigger condition. Dropping it there would make
/// <c>NEW.a</c> and <c>OLD.a</c> indistinguishable, which inverts what the predicate means.
/// </remarks>
public class ColumnReferenceExpression : Expression
{
    public ColumnReferenceExpression(Identifier identifier)
    {
        Identifier = identifier;
    }

    public ColumnReferenceExpression(IEnumerable<Identifier> segments)
    {
        var list = segments.ToList();

        if (list.Count == 0)
        {
            throw new ArgumentException(
                "A column reference needs at least one segment", nameof(segments));
        }

        Identifier = list[^1];
        Qualifiers = list[..^1];
    }

    /// <summary>The column's own name: the final segment of a qualified reference.</summary>
    public Identifier Identifier { get; }

    /// <summary>
    /// The leading segments of a qualified reference, in source order, empty when the
    /// reference is bare.
    /// </summary>
    public IReadOnlyList<Identifier> Qualifiers { get; } = [];

    /// <summary>The full dotted name, qualifiers included.</summary>
    public IEnumerable<Identifier> Segments => [.. Qualifiers, Identifier];
}
