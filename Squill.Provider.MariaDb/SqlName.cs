using Squill.Core;

namespace Squill.Provider.MariaDb;

/// <summary>
/// A canonical MariaDB object name made of one or more identifier segments (e.g. table,
/// column). The segment logic, equality, hashing, and canonical rendering live in
/// <see cref="SqlNameBase{TSelf}"/>; this type supplies only MariaDB's backtick identifier
/// quoting and the static factory entry points.
///
/// The canonical (in-memory) form is unquoted and dot-joined (e.g. film.title); quoting is a
/// SQL-serialization concern, produced on demand via <see cref="SqlNameBase{TSelf}.Sql"/> and
/// <see cref="SqlNameBase{TSelf}.QuotedUnqualified"/>.
/// </summary>
public sealed class SqlName : SqlNameBase<SqlName>
{
    private SqlName(string[] segments) : base(segments)
    {
    }

    protected override char QuoteChar => '`';

    protected override SqlName Create(string[] segments) => new(segments);

    /// <summary>Creates a name from one or more identifier segments (e.g. table, column).</summary>
    public static SqlName Object(params string[] segments) => new(segments);

    /// <summary>Parses a canonical (unquoted, dot-joined) rendering back into a SqlName.</summary>
    public static SqlName Parse(string canonical) => new(canonical.Split('.'));

    /// <summary>
    /// Parses a canonical (unquoted, dot-joined) name — as stored in a model element's Name —
    /// and returns its last segment. Used where only the bare identifier is needed, e.g. a
    /// column name inside a CREATE TABLE body.
    /// </summary>
    public static new string UnqualifiedOf(string canonical) => SqlNameBase<SqlName>.UnqualifiedOf(canonical);

    public static implicit operator string(SqlName name) => name.ToString();
}
