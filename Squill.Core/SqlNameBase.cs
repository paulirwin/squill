namespace Squill.Core;

/// <summary>
/// The provider-agnostic core of a canonical SQL object name: an ordered list of identifier
/// segments (e.g. schema, object, column) with equality, hashing, and canonical (unquoted,
/// dot-joined) rendering. Quoting is the only provider-specific concern, so it is deferred to
/// <see cref="QuoteChar"/>; everything the model comparison depends on (the canonical form,
/// equality, and hash) is quote-independent and lives here.
///
/// Providers derive a concrete <c>SqlName</c> from this using the curiously-recurring pattern
/// (<c>SqlName : SqlNameBase&lt;SqlName&gt;</c>) so that <see cref="Child"/> / <see cref="Sibling"/>
/// return the provider's own type, and supply the quote character plus the static factory
/// entry points.
/// </summary>
/// <typeparam name="TSelf">The concrete provider name type.</typeparam>
public abstract class SqlNameBase<TSelf> : IEquatable<SqlNameBase<TSelf>>
    where TSelf : SqlNameBase<TSelf>
{
    private readonly string[] _segments;

    protected SqlNameBase(string[] segments)
    {
        if (segments.Length == 0)
        {
            throw new ArgumentException("A SqlName must have at least one segment", nameof(segments));
        }

        _segments = segments;
    }

    /// <summary>The identifier-quote character for this provider (e.g. '"' or '`').</summary>
    protected abstract char QuoteChar { get; }

    /// <summary>Creates a new instance of the concrete name type from raw segments.</summary>
    protected abstract TSelf Create(string[] segments);

    /// <summary>Returns a new name with an additional trailing segment (e.g. a column of this table).</summary>
    public TSelf Child(string segment) => Create([.._segments, segment]);

    /// <summary>
    /// Returns a new name sharing this name's qualifier but with a different final
    /// segment (e.g. an index in the same namespace as its table).
    /// </summary>
    public TSelf Sibling(string segment) => Create([.._segments[..^1], segment]);

    /// <summary>The last segment, unquoted — for contexts that need the bare identifier.</summary>
    public string UnqualifiedName => _segments[^1];

    /// <summary>The last segment, quoted but not qualified (e.g. "title" or `title`).</summary>
    public string QuotedUnqualified => $"{QuoteChar}{_segments[^1]}{QuoteChar}";

    /// <summary>The fully qualified, quoted SQL rendering (e.g. "public"."film"."title").</summary>
    public string Sql => string.Join('.', _segments.Select(s => $"{QuoteChar}{s}{QuoteChar}"));

    /// <summary>The canonical, unquoted, dot-joined rendering (e.g. public.film.title).</summary>
    public override string ToString() => string.Join('.', _segments);

    public bool Equals(SqlNameBase<TSelf>? other) =>
        other is not null && _segments.AsSpan().SequenceEqual(other._segments);

    public override bool Equals(object? obj) => obj is SqlNameBase<TSelf> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();

        foreach (var segment in _segments)
        {
            hash.Add(segment);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Parses a canonical (unquoted, dot-joined) name — as stored in a model element's Name —
    /// and returns its last segment. Quote-independent, so it is shared here as a static helper
    /// providers can expose under their own type.
    /// </summary>
    public static string UnqualifiedOf(string canonical) => canonical.Split('.')[^1];
}
