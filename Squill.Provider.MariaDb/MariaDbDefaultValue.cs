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
    /// absent or not a modeled constant literal. <paramref name="isCharacterColumn"/> lets a
    /// string default on a character column be recognized even on MySQL, which — unlike
    /// MariaDB — reports a string default unquoted (e.g. <c>active</c> rather than
    /// <c>'active'</c>).
    /// </summary>
    public static string? FromDatabaseText(string? columnDefault, bool isCharacterColumn = false)
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

        // A string default already wrapped in single quotes (MariaDB's information_schema
        // form) is canonical as-is.
        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'')
        {
            return text;
        }

        // On a character column, MySQL reports a string literal default unquoted. Treat the
        // bare value as a string literal and re-quote it, unless it is an expression default
        // MySQL surfaces literally (e.g. CURRENT_TIMESTAMP, or a parenthesized expression),
        // which is not a modeled constant.
        if (isCharacterColumn && !IsExpressionDefault(text))
        {
            return "'" + text.Replace("'", "''") + "'";
        }

        return NormalizeNumericText(text);
    }

    // Whether a bare database default text is a non-literal expression (a function call such
    // as CURRENT_TIMESTAMP / NOW(), or a parenthesized expression) rather than a string
    // literal, so it is left unmodeled.
    private static bool IsExpressionDefault(string text)
        => text.StartsWith('(')
            || text.Contains('(')
            || string.Equals(text, "CURRENT_TIMESTAMP", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "current_timestamp()", StringComparison.OrdinalIgnoreCase);

    // Renders a canonical default back to SQL. The canonical form is already valid SQL.
    public static string ToSql(string canonical) => canonical;

    // Returns a canonical invariant text of a numeric literal, or null if it isn't one.
    // Trailing fractional zeros are trimmed so both sides agree: a source DEFAULT 0 on a
    // decimal(12,4) column and the database's reported '0.0000' both canonicalize to "0"
    // (and 1.50 → 1.5), since MariaDB pads a numeric default to the column's scale.
    private static string? NormalizeNumericText(string text)
    {
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
        {
            return l.ToString(CultureInfo.InvariantCulture);
        }

        if (!decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        // Trim trailing zeros after the decimal point, and a trailing bare decimal point, so
        // the scale MariaDB pads to (e.g. 0.0000) collapses to the same canonical form the
        // source literal (0) produces.
        var trimmed = text.Contains('.')
            ? text.TrimEnd('0').TrimEnd('.')
            : text;

        return trimmed.Length == 0 || trimmed == "-" ? "0" : trimmed;
    }
}
