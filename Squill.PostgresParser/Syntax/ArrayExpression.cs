namespace Squill.PostgresParser.Syntax;

/// <summary>
/// An array constructor — <c>ARRAY[1, 2, 3]</c>, or a nested <c>ARRAY[[1, 2], [3, 4]]</c>.
///
/// <para>
/// Element order is carried as written and never sorted: PostgreSQL stores an array constructor
/// in the order it was given (measured — <c>q IN (2, 1)</c> comes back as <c>ARRAY[2, 1]</c>),
/// so reordering is a real change to the predicate rather than a spelling difference.
/// </para>
/// </summary>
public class ArrayExpression(IReadOnlyList<Expression> elements) : Expression
{
    public IReadOnlyList<Expression> Elements { get; } = elements;
}
