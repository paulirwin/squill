using System.Globalization;

namespace Squill.Provider.MariaDb;

/// <summary>
/// Canonicalizes a column <c>DEFAULT</c> to a stable string so the two model builders — the
/// parser (<see cref="ParserWorkspaceModelBuilder"/>) and the database extractor
/// (<see cref="MariaDbDatabaseModelBuilder"/>) — agree, and the Merkle hash of a parsed
/// model matches the hash of the same schema extracted from MariaDB.
///
/// Constant literals are modeled: integers, numerics, booleans, and single-quoted strings.
/// <list type="bullet">
///   <item>integer / numeric → the numeric text (<c>0</c>, <c>-5</c>, <c>1.50</c>)</item>
///   <item>boolean → <c>1</c> / <c>0</c>, since <c>boolean</c> is an alias for
///     <c>tinyint(1)</c> on both engines and that is how the default comes back</item>
///   <item>string → <c>'value'</c> (single-quoted, <c>''</c>-escaped)</item>
/// </list>
///
/// The current-timestamp default on Sakila's ubiquitous <c>last_update</c> columns is modeled
/// too (issue #124). Unlike Postgres, which preserves the spelling it was given, both engines
/// collapse every synonym — <c>CURRENT_TIMESTAMP</c>, <c>NOW()</c>, <c>current_timestamp</c> —
/// into one stored default, but then report it differently: MySQL as <c>CURRENT_TIMESTAMP</c>
/// and MariaDB as <c>current_timestamp()</c>. Because one provider serves both engines and the
/// Merkle hash must match either way, all of those spellings fold to the single canonical
/// token <c>CURRENT_TIMESTAMP</c>.
///
/// The fractional-seconds variant (<c>CURRENT_TIMESTAMP(3)</c>) is modeled too (issue #144),
/// as the canonical token with its precision appended — <c>CURRENT_TIMESTAMP(3)</c>. The same
/// case-folding and synonym-folding applies, so MySQL's <c>CURRENT_TIMESTAMP(3)</c>, MariaDB's
/// <c>current_timestamp(3)</c> and a source <c>NOW(3)</c> all reduce to it. The precision is
/// kept rather than dropped because the two are not interchangeable: MySQL rejects an
/// <c>ON UPDATE</c> precision that disagrees with its column's.
///
/// Any other function default, and <c>DEFAULT NULL</c>, stay unmodeled — the methods return
/// <c>null</c>, so the default is left off the model rather than modeled as something that
/// could not round-trip.
/// </summary>
internal static class MariaDbDefaultValue
{
    /// <summary>
    /// The single canonical token every current-timestamp spelling folds to. Chosen as the
    /// keyword form because it is valid DDL on both engines.
    /// </summary>
    private const string CurrentTimestamp = "CURRENT_TIMESTAMP";

    /// <summary>
    /// The spellings — across source, MySQL's catalog and MariaDB's catalog — that all mean
    /// "the current timestamp, to whole seconds". Compared case-insensitively.
    ///
    /// Deliberately narrow, and narrower than the grammar's <c>currentTimestamp</c> rule, which
    /// also admits <c>LOCALTIME</c>, <c>LOCALTIMESTAMP</c>, <c>CURDATE</c> and <c>CURTIME</c>.
    /// Those are *not* synonyms here: MariaDB stores <c>DEFAULT LOCALTIME</c> as
    /// <c>curtime()</c> (a time of day, not a timestamp) and <c>DEFAULT LOCALTIMESTAMP</c> as
    /// <c>localtimestamp()</c>, neither of which comes back as <c>current_timestamp()</c>.
    /// Folding them in here would mean a parsed default that never matches the extracted one —
    /// a permanent phantom diff. They are left unmodeled instead (issue #147).
    ///
    /// A form carrying a fractional-seconds precision folds to the same base name and keeps its
    /// precision (issue #144); see <see cref="CanonicalCurrentTimestamp"/>.
    /// </summary>
    private static readonly HashSet<string> CurrentTimestampNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CURRENT_TIMESTAMP",
            "NOW",
        };

    /// <summary>
    /// The canonical current-timestamp token for a default text from either source or a
    /// catalog — <c>CURRENT_TIMESTAMP</c>, or <c>CURRENT_TIMESTAMP(n)</c> for the
    /// fractional-seconds form — or <c>null</c> if the text is not one at all.
    ///
    /// Inner whitespace is ignored so <c>now( )</c> and <c>current_timestamp (3)</c> match.
    ///
    /// A precision of <c>0</c> folds to the bare token, and empty parentheses do too. That is
    /// measured behaviour, not tidiness: a <c>datetime(0)</c> column declaring
    /// <c>CURRENT_TIMESTAMP(0)</c> is reported by both engines exactly as the bare form is (the
    /// column type drops its <c>(0)</c> as well), so keeping the <c>(0)</c> here would make that
    /// column re-diff on every deploy.
    /// </summary>
    internal static string? CanonicalCurrentTimestamp(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var compact = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

        var open = compact.IndexOf('(');

        if (open < 0)
        {
            // A bare keyword: CURRENT_TIMESTAMP. NOW is a function and is never valid without
            // parentheses, so it is not accepted in this form.
            return string.Equals(compact, CurrentTimestamp, StringComparison.OrdinalIgnoreCase)
                ? CurrentTimestamp
                : null;
        }

        if (compact[^1] != ')' || !CurrentTimestampNames.Contains(compact[..open]))
        {
            return null;
        }

        var precision = compact[(open + 1)..^1];

        // "current_timestamp()" / "now()" — no precision at all.
        if (precision.Length == 0)
        {
            return CurrentTimestamp;
        }

        // Only a plain non-negative integer is a precision either engine produces; anything
        // else is left unmodeled rather than echoed back.
        if (!int.TryParse(precision, NumberStyles.None, CultureInfo.InvariantCulture, out var digits))
        {
            return null;
        }

        return digits == 0 ? CurrentTimestamp : $"{CurrentTimestamp}({digits})";
    }

    /// <summary>
    /// Whether a default text, from either source or a catalog, is a current-timestamp default
    /// in any of its modeled forms.
    /// </summary>
    internal static bool IsCurrentTimestamp(string? text) =>
        CanonicalCurrentTimestamp(text) is not null;

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

        // Checked before the quoted-string and numeric paths, which it cannot be confused
        // with: an unquoted keyword or function call.
        if (CanonicalCurrentTimestamp(text) is { } currentTimestamp)
        {
            return currentTimestamp;
        }

        // boolean is an alias for tinyint(1) on both engines, and a TRUE/FALSE default is
        // stored as 1/0 — so canonicalize to the number the catalog will report back.
        if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
        {
            return "1";
        }

        if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
        {
            return "0";
        }

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

        // Checked before the character-column path below, which would otherwise re-quote
        // MySQL's bare CURRENT_TIMESTAMP into a string literal, and before the expression
        // rejection, which discards anything containing parentheses.
        if (CanonicalCurrentTimestamp(text) is { } currentTimestamp)
        {
            return currentTimestamp;
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

    // Whether a bare database default text is a non-literal expression (a function call, or a
    // parenthesized expression) rather than a string literal, so it is left unmodeled. The
    // current-timestamp spellings are recognized before this is reached and never get here.
    private static bool IsExpressionDefault(string text)
        => text.Contains('(')
            || string.Equals(text, "CURRENT_TIMESTAMP", StringComparison.OrdinalIgnoreCase);

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
