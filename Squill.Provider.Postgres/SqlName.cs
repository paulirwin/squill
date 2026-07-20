namespace Squill.Provider.Postgres;

/// <summary>
/// A canonical, quoted PostgreSQL object name made of one or more identifier
/// segments (e.g. schema, object, column). Rendering is centralized here so every
/// model builder produces identical names — a prerequisite for comparing a model
/// built from parsed SQL against one extracted from a live database.
/// </summary>
public sealed class SqlName : IEquatable<SqlName>
{
    private readonly string[] _segments;

    private SqlName(string[] segments)
    {
        if (segments.Length == 0)
        {
            throw new ArgumentException("A SqlName must have at least one segment", nameof(segments));
        }

        _segments = segments;
    }

    /// <summary>Creates a name from one or more identifier segments (e.g. schema, object).</summary>
    public static SqlName Object(params string[] segments) => new(segments);

    /// <summary>Returns a new name with an additional trailing segment (e.g. a column of this table).</summary>
    public SqlName Child(string segment) => new([.._segments, segment]);

    /// <summary>The last segment, unquoted — for contexts that need the bare identifier.</summary>
    public string UnqualifiedName => _segments[^1];

    /// <summary>The last segment, quoted but not schema-qualified (e.g. "title").</summary>
    public string Quoted => $"\"{_segments[^1]}\"";

    /// <summary>The fully qualified, quoted rendering (e.g. "public"."film"."title").</summary>
    public override string ToString() => string.Join('.', _segments.Select(s => $"\"{s}\""));

    /// <summary>
    /// Parses a canonical rendered name (as stored in a model element's Name) and
    /// returns its last segment, unquoted. Used where only the bare identifier is
    /// valid, e.g. a column name inside a CREATE TABLE body.
    /// </summary>
    public static string UnqualifiedOf(string canonical)
    {
        var lastSegment = canonical.Split('.')[^1];

        return lastSegment.StartsWith('"') && lastSegment.EndsWith('"')
            ? lastSegment[1..^1]
            : lastSegment;
    }

    public static implicit operator string(SqlName name) => name.ToString();

    public bool Equals(SqlName? other) => other is not null && _segments.AsSpan().SequenceEqual(other._segments);

    public override bool Equals(object? obj) => obj is SqlName other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();

        foreach (var segment in _segments)
        {
            hash.Add(segment);
        }

        return hash.ToHashCode();
    }
}
