using System.Globalization;

namespace Squill.Provider.MariaDb;

/// <summary>
/// Canonicalizes a column <c>DEFAULT</c> to a stable string so the two model builders — the
/// parser (<see cref="ParserWorkspaceModelBuilder"/>) and the database extractor
/// (<see cref="MariaDbDatabaseModelBuilder"/>) — agree, and the Merkle hash of a parsed
/// model matches the hash of the same schema extracted from MariaDB.
///
/// Only constant literals are modeled: integers, numerics, and single-quoted strings. A
/// function default (<c>CURRENT_TIMESTAMP</c>, <c>NOW()</c>, …) or <c>DEFAULT NULL</c> is
/// not modeled — the methods return <c>null</c>, so the default is left off the model
/// rather than modeled as something that could not round-trip.
/// <list type="bullet">
///   <item>integer / numeric → the numeric text (<c>0</c>, <c>-5</c>, <c>1.50</c>)</item>
///   <item>string → <c>'value'</c> (single-quoted, <c>''</c>-escaped)</item>
/// </list>
/// </summary>
internal static class MariaDbDefaultValue
{
    /// <summary>
    /// The canonical form of a default written as a raw literal token in source (already
    /// unwrapped by the visitor), or <c>null</c> if it is not a modeled constant literal.
    /// A quoted string keeps its quotes; a bare number is normalized; anything else (a
    /// function call, NULL) returns <c>null</c>.
    /// </summary>
    public static string? FromSourceToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var text = token.Trim();

        // A single- or double-quoted string literal. MariaDB canonicalizes to single quotes.
        if (text.Length >= 2 && (text[0] == '\'' || text[0] == '"') && text[^1] == text[0])
        {
            var inner = text[1..^1];

            // Unescape the source quote style, then re-escape as a single-quoted literal.
            inner = text[0] == '\''
                ? inner.Replace("''", "'").Replace("\\'", "'")
                : inner.Replace("\"\"", "\"").Replace("\\\"", "\"");

            return "'" + inner.Replace("'", "''") + "'";
        }

        return NormalizeNumericText(text);
    }

    /// <summary>
    /// The canonical form of a database <c>COLUMN_DEFAULT</c> text, or <c>null</c> if it is
    /// absent or not a modeled constant literal.
    /// </summary>
    public static string? FromDatabaseText(string? columnDefault)
    {
        if (string.IsNullOrWhiteSpace(columnDefault))
        {
            return null;
        }

        var text = columnDefault.Trim();

        // MariaDB reports NULL as the string "NULL" for a nullable column with no explicit
        // default; treat it as unmodeled.
        if (string.Equals(text, "NULL", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Modern MariaDB wraps a string default in single quotes in information_schema.
        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'')
        {
            return text;
        }

        return NormalizeNumericText(text);
    }

    // Renders a canonical default back to SQL. The canonical form is already valid SQL.
    public static string ToSql(string canonical) => canonical;

    // Returns the invariant text of a numeric literal, or null if it isn't one. Preserves
    // the written scale (1.50 stays 1.50), matching how MariaDB stores numeric defaults.
    private static string? NormalizeNumericText(string text)
    {
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
        {
            return l.ToString(CultureInfo.InvariantCulture);
        }

        if (decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out _))
        {
            return text;
        }

        return null;
    }
}
