namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A <c>TRIM([BOTH | LEADING | TRAILING] [characters FROM] source)</c> (issue #140).
///
/// The optional characters-to-trim argument is separated by <c>FROM</c> rather than a comma,
/// so it is carried apart from <see cref="Sources"/>. <see cref="Sources"/> is a list because
/// the grammar admits the comma form <c>TRIM(source, characters)</c> as well.
/// </summary>
public class TrimExpression : Expression
{
    public TrimExpression(TrimSide side, Expression? characters, IList<Expression> sources)
    {
        Side = side;
        Characters = characters;
        Sources = sources;
    }

    public TrimSide Side { get; }

    /// <summary>
    /// The characters to trim, written before <c>FROM</c>, or <c>null</c> when only a source
    /// was given (in which case whitespace is trimmed).
    /// </summary>
    public Expression? Characters { get; }

    public IList<Expression> Sources { get; }
}
