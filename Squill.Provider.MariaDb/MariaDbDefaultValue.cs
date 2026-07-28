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
/// The rest of the time family — <c>LOCALTIME</c>, <c>LOCALTIMESTAMP</c>, <c>CURDATE()</c>,
/// <c>CURTIME()</c> — is modeled as of issue #147, and is the reason every entry point here
/// requires a <see cref="MariaDbEngine"/>. These are where the two engines stop agreeing, so
/// there is no one canonical token that works for both; see <see cref="MariaDbEngine"/> for the
/// measured matrix. On MariaDB each keeps its own token reflecting its own distinct stored form;
/// on MySQL <c>LOCALTIME</c>/<c>LOCALTIMESTAMP</c> are true synonyms that fold into
/// <c>CURRENT_TIMESTAMP</c>, and <c>CURDATE</c>/<c>CURTIME</c> are not valid defaults at all.
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
    /// "the current timestamp, to whole seconds" <em>on either engine</em>. Compared
    /// case-insensitively.
    ///
    /// Narrower than the grammar's <c>currentTimestamp</c> rule, which also admits
    /// <c>LOCALTIME</c>, <c>LOCALTIMESTAMP</c>, <c>CURDATE</c> and <c>CURTIME</c>. Those are
    /// engine-dependent and handled separately (see <see cref="MySqlCurrentTimestampNames"/>
    /// and <see cref="MariaDbOwnTokenNames"/>), because on MariaDB they are not synonyms at
    /// all: it stores <c>DEFAULT LOCALTIME</c> as <c>curtime()</c> — a time of day, not a
    /// timestamp. Folding those in unconditionally would mean a parsed default that never
    /// matches the extracted one on MariaDB — a permanent phantom diff (issue #147).
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
    /// The additional spellings that are true <c>CURRENT_TIMESTAMP</c> synonyms <em>on MySQL
    /// only</em>. Measured against <c>mysql:latest</c>: a <c>datetime</c> column declaring
    /// <c>DEFAULT LOCALTIME</c> or <c>DEFAULT LOCALTIMESTAMP</c> is reported back as plain
    /// <c>CURRENT_TIMESTAMP</c>, exactly as if it had been written that way — matching
    /// <see href="https://dev.mysql.com/doc/refman/8.4/en/timestamp-initialization.html">MySQL's
    /// documented behaviour</see>. MariaDB does the opposite and gives each its own stored form.
    /// </summary>
    private static readonly HashSet<string> MySqlCurrentTimestampNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "LOCALTIME",
            "LOCALTIMESTAMP",
        };

    /// <summary>
    /// The time functions MariaDB stores under their own name rather than folding into
    /// <c>current_timestamp()</c>, mapped from every source spelling to the canonical token.
    /// The token is the engine's own reported spelling, which is also valid DDL and — verified
    /// against <c>mariadb:latest</c> — is stored unchanged when re-applied, so a redeploy of an
    /// unchanged column is a no-op.
    ///
    /// Measured mapping (source → stored):
    /// <c>LOCALTIME</c> and <c>CURRENT_TIME</c> → <c>curtime()</c>;
    /// <c>LOCALTIMESTAMP</c> → <c>localtimestamp()</c>;
    /// <c>CURRENT_DATE</c> → <c>curdate()</c>.
    /// Note <c>LOCALTIME</c> collapses onto <c>curtime()</c> — a <em>time of day</em> — which is
    /// why it can never share a token with the current-timestamp family.
    ///
    /// None of these is valid in <c>ON UPDATE</c> position on either engine, which is enforced
    /// separately by <see cref="CanonicalOnUpdate"/>.
    /// </summary>
    private static readonly Dictionary<string, string> MariaDbOwnTokenNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["LOCALTIME"] = "CURTIME",
            ["CURRENT_TIME"] = "CURTIME",
            ["CURTIME"] = "CURTIME",
            ["LOCALTIMESTAMP"] = "LOCALTIMESTAMP",
            ["CURRENT_DATE"] = "CURDATE",
            ["CURDATE"] = "CURDATE",
        };

    /// <summary>
    /// Names from <see cref="MariaDbOwnTokenNames"/> that are only valid <em>with</em>
    /// parentheses, so they are not accepted as a bare keyword.
    /// </summary>
    private static readonly HashSet<string> ParenthesizedOnlyNames =
        new(StringComparer.OrdinalIgnoreCase) { "CURDATE", "CURTIME" };

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
    internal static string? CanonicalCurrentTimestamp(string? text, MariaDbEngine engine)
    {
        if (text is null)
        {
            return null;
        }

        var compact = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

        var open = compact.IndexOf('(');

        // The bare-keyword forms. A name that is only ever a function (NOW, CURDATE, CURTIME)
        // is not valid unparenthesized and so is rejected here — measured: MariaDB answers
        // "Unknown column 'CURDATE' in 'DEFAULT'" for a bare CURDATE.
        if (open < 0)
        {
            return string.Equals(compact, CurrentTimestamp, StringComparison.OrdinalIgnoreCase)
                ? CurrentTimestamp
                : CanonicalBareKeyword(compact, engine);
        }

        if (compact[^1] != ')')
        {
            return null;
        }

        var canonicalName = CanonicalName(compact[..open], engine);

        if (canonicalName is null)
        {
            return null;
        }

        var precision = compact[(open + 1)..^1];

        // "current_timestamp()" / "now()" / "curtime()" — no precision at all.
        if (precision.Length == 0)
        {
            return BareForm(canonicalName);
        }

        // Only a plain non-negative integer is a precision either engine produces; anything
        // else is left unmodeled rather than echoed back.
        if (!int.TryParse(precision, NumberStyles.None, CultureInfo.InvariantCulture, out var digits))
        {
            return null;
        }

        return digits == 0 ? BareForm(canonicalName) : $"{canonicalName}({digits})";
    }

    // The canonical token for a bare (unparenthesized) keyword other than CURRENT_TIMESTAMP
    // itself. On MySQL, LOCALTIME/LOCALTIMESTAMP are true synonyms and fold in. On MariaDB they
    // are distinct functions with their own stored forms, as are CURRENT_DATE/CURRENT_TIME.
    private static string? CanonicalBareKeyword(string compact, MariaDbEngine engine)
    {
        if (engine == MariaDbEngine.MySql)
        {
            return MySqlCurrentTimestampNames.Contains(compact) ? CurrentTimestamp : null;
        }

        // MariaDB: each keeps its own token. CURDATE and CURTIME are excluded because they are
        // function-only spellings — measured, a bare CURDATE is "Unknown column 'CURDATE' in
        // 'DEFAULT'". Their keyword equivalents CURRENT_DATE / CURRENT_TIME are the valid bare
        // forms, and map to the same tokens.
        if (ParenthesizedOnlyNames.Contains(compact))
        {
            return null;
        }

        return MariaDbOwnTokenNames.TryGetValue(compact, out var token) ? BareForm(token) : null;
    }

    // The canonical base name for a parenthesized call, or null if the function is not one this
    // engine models as a default.
    private static string? CanonicalName(string name, MariaDbEngine engine)
    {
        if (CurrentTimestampNames.Contains(name))
        {
            return CurrentTimestamp;
        }

        if (engine == MariaDbEngine.MySql)
        {
            // MySQL folds LOCALTIME()/LOCALTIMESTAMP() in, and rejects CURDATE()/CURTIME() as
            // a default outright (a syntax error, not merely an invalid value), so those stay
            // unmodeled and are reported at build time instead.
            return MySqlCurrentTimestampNames.Contains(name) ? CurrentTimestamp : null;
        }

        return MariaDbOwnTokenNames.GetValueOrDefault(name);
    }

    // Renders the no-precision form of a canonical base name. CURRENT_TIMESTAMP is spelled as
    // the bare keyword (valid on both engines and how MySQL reports it); the MariaDB-only
    // functions keep their empty parentheses, which is both how MariaDB reports them and the
    // only valid way to write them.
    private static string BareForm(string canonicalName) =>
        canonicalName == CurrentTimestamp ? CurrentTimestamp : $"{canonicalName}()";

    /// <summary>
    /// Whether a default text, from either source or a catalog, is a current-timestamp default
    /// in any of its modeled forms.
    /// </summary>
    internal static bool IsCurrentTimestamp(string? text, MariaDbEngine engine) =>
        CanonicalCurrentTimestamp(text, engine) is not null;

    /// <summary>
    /// The canonical token for an <c>ON UPDATE</c> clause, or <c>null</c> if it is not one that
    /// can be modeled. Narrower than a <c>DEFAULT</c>: only the current-timestamp family is
    /// valid in this position. Measured against both engines, <c>ON UPDATE CURDATE()</c>,
    /// <c>CURTIME()</c> and (on MariaDB) <c>LOCALTIME</c> are rejected outright, so accepting
    /// them here would model a clause that cannot be deployed.
    /// </summary>
    internal static string? CanonicalOnUpdate(string? text, MariaDbEngine engine)
    {
        if (CanonicalCurrentTimestamp(text, engine) is not { } canonical)
        {
            return null;
        }

        // Only the current-timestamp token (bare or precision-carrying) is valid here; the
        // MariaDB-only function tokens are not.
        return canonical.StartsWith(CurrentTimestamp, StringComparison.Ordinal) ? canonical : null;
    }

    /// <summary>
    /// The canonical form of a default written as a raw literal token in source (already
    /// unwrapped by the visitor), or <c>null</c> if it is not a modeled constant literal.
    /// A quoted string keeps its quotes; a bare number is normalized; anything else (a
    /// function call, NULL) returns <c>null</c>.
    /// </summary>
    public static string? FromSourceToken(string? token, MariaDbEngine engine)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var text = token.Trim();

        // Checked before the quoted-string and numeric paths, which it cannot be confused
        // with: an unquoted keyword or function call.
        if (CanonicalCurrentTimestamp(text, engine) is { } currentTimestamp)
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
    public static string? FromDatabaseText(
        string? columnDefault, MariaDbEngine engine, bool isCharacterColumn = false)
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
        if (CanonicalCurrentTimestamp(text, engine) is { } currentTimestamp)
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
