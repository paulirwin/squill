using System.Globalization;
using Squill.PostgresParser.Syntax;

namespace Squill.Provider.Postgres;

/// <summary>
/// Canonicalizes a column <c>DEFAULT</c> to a stable string so the two model builders —
/// the parser (<see cref="ParserWorkspaceModelBuilder"/>) and the database extractor
/// (<see cref="PostgresDatabaseModelBuilder"/>) — agree, and the Merkle hash of a parsed
/// model matches the hash of the same schema extracted from Postgres.
///
/// Only constant literals are modeled: integers, numerics, booleans, and single-quoted
/// strings. Postgres canonicalizes stored defaults inconsistently (a bare <c>0</c> but
/// <c>'-5'::integer</c> and <c>'active'::character varying</c>), so both a parsed
/// expression and the database's <c>column_default</c> text are reduced to the same
/// canonical token:
/// <list type="bullet">
///   <item>integer / numeric → the numeric text (<c>0</c>, <c>-5</c>, <c>1.50</c>)</item>
///   <item>boolean → <c>true</c> / <c>false</c></item>
///   <item>string → <c>'value'</c> (single-quoted, <c>''</c>-escaped)</item>
/// </list>
/// A small allowlist of well-known niladic function defaults is also modeled (issue #124),
/// covering the <c>now()</c> that Pagila's <c>last_update</c> columns use. Postgres stores
/// such a default with the spelling it was written with — <c>now()</c> stays <c>now()</c> and
/// <c>CURRENT_TIMESTAMP</c> stays <c>CURRENT_TIMESTAMP</c>; it is not rewritten into the other
/// — while normalizing case, whitespace and an explicit <c>pg_catalog.</c> prefix. So each
/// supported spelling maps to its own canonical token rather than being folded together.
///
/// The allowlist is deliberately narrow: it holds only argument-less functions whose stored
/// form is known to be reproduced verbatim. An arbitrary call is left unmodeled because
/// Postgres may rewrite it (adding argument casts, resolving the schema), which would break
/// the round trip. The <c>nextval(...)</c> of a serial column is excluded for the same reason
/// and because serial-ness is already modeled on the column. Any other non-constant
/// expression, and <c>DEFAULT NULL</c>, remain unmodeled — <see cref="FromExpression"/> and
/// <see cref="FromDatabaseText"/> return <c>null</c>, so the default is left off the model
/// rather than modeled as something that could not round-trip.
/// </summary>
internal static class PostgresDefaultValue
{
    /// <summary>
    /// Argument-less function defaults that Postgres stores verbatim, mapped from the
    /// lower-cased name to the canonical token. Each entry has been verified against a live
    /// server to come back exactly as spelled here, which is what lets the parsed and
    /// extracted models hash identically.
    ///
    /// Keyword forms such as <c>CURRENT_TIMESTAMP</c> are recognized on the database side
    /// (see <see cref="FromDatabaseText"/>) but cannot yet be written in source: the parser
    /// does not implement the <c>func_expr_common_subexpr</c> grammar rule they take. They
    /// are listed here so the extractor recognizes a database that already has one.
    /// </summary>
    private static readonly Dictionary<string, string> SupportedFunctionDefaults =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["now()"] = "now()",
            ["gen_random_uuid()"] = "gen_random_uuid()",
            ["current_timestamp"] = "CURRENT_TIMESTAMP",
            ["current_date"] = "CURRENT_DATE",
            ["current_time"] = "CURRENT_TIME",
            ["localtimestamp"] = "LOCALTIMESTAMP",
        };

    /// <summary>
    /// The canonical form of a parsed <c>DEFAULT</c> expression, or <c>null</c> if it is
    /// neither a modeled constant literal nor an allowlisted function default.
    /// </summary>
    public static string? FromExpression(Expression expression)
    {
        switch (expression)
        {
            case LiteralExpression literal:
                return FromLiteralValue(literal.Value);

            // An allowlisted argument-less function default, e.g. DEFAULT now(). Postgres
            // resolves an explicit pg_catalog. prefix away when it stores the default, so
            // strip it here to match.
            case FunctionApplicationExpression { Arguments.Count: 0 } function
                when SupportedFunctionDefaults.TryGetValue(
                    StripCatalogPrefix(function.Name) + "()", out var canonical):
                return canonical;

            // A negated numeric literal, e.g. DEFAULT -5. Postgres stores this as the cast
            // '-5'::integer, which FromDatabaseText reduces to the same token.
            case UnaryExpression { Operator: PostgresBuiltInUnaryOperator.Negate } negate
                when FromNumericValue(negate.Expression) is { } inner:
                return "-" + inner;

            // A leading + is a no-op sign on a numeric literal, e.g. DEFAULT +5. Postgres
            // stores that one as the parenthesized (+ 5) rather than as a cast.
            case UnaryExpression { Operator: PostgresBuiltInUnaryOperator.Plus } plus
                when FromNumericValue(plus.Expression) is { } inner:
                return inner;

            default:
                return null;
        }
    }

    /// <summary>
    /// The canonical form of a database <c>column_default</c> text, or <c>null</c> if it is
    /// absent or not a modeled constant literal. Strips Postgres's <c>::type</c> cast and
    /// classifies the underlying literal.
    /// </summary>
    public static string? FromDatabaseText(string? columnDefault)
    {
        if (string.IsNullOrWhiteSpace(columnDefault))
        {
            return null;
        }

        var trimmed = columnDefault.Trim();

        // Checked before the cast strip, which is only meaningful for a constant and would
        // otherwise mangle a function call. Postgres normalizes case, inner whitespace and the
        // pg_catalog. prefix itself, so a direct allowlist lookup suffices.
        if (SupportedFunctionDefaults.TryGetValue(StripCatalogPrefix(trimmed), out var function))
        {
            return function;
        }

        // A source DEFAULT +5 is stored as the parenthesized, space-separated (+ 5) rather than
        // as the '5'::integer cast an unsigned constant gets (issue #139). DEFAULT -5 does take
        // the cast form, but handle the (- 5) spelling too so both signs normalize identically.
        if (StripOuterSign(trimmed) is { } signed)
        {
            return signed;
        }

        var text = StripCast(trimmed);

        // A string literal survives the cast strip as a single-quoted token.
        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'')
        {
            return text;
        }

        if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
        {
            return "true";
        }

        if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
        {
            return "false";
        }

        return NormalizeNumericText(text);
    }

    // Renders a canonical default back to SQL. The canonical form is already valid SQL
    // (a numeric, true/false, or a single-quoted string), so it is emitted verbatim.
    public static string ToSql(string canonical) => canonical;

    private static string? FromLiteralValue(object value) => value switch
    {
        bool b => b ? "true" : "false",
        long l => l.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        string s => "'" + s.Replace("'", "''") + "'",
        _ => null,
    };

    // Removes an explicit pg_catalog. schema qualifier, which Postgres resolves away when it
    // stores a default: a source DEFAULT pg_catalog.now() comes back as plain now().
    private static string StripCatalogPrefix(string name) =>
        name.StartsWith("pg_catalog.", StringComparison.OrdinalIgnoreCase)
            ? name["pg_catalog.".Length..]
            : name;

    // The canonical form of a numeric literal (possibly wrapped in an outer sign), or null.
    private static string? FromNumericValue(Expression expression) =>
        expression is LiteralExpression { Value: long or int or decimal } literal
            ? FromLiteralValue(literal.Value)
            : null;

    // The canonical form of Postgres's parenthesized signed-operand spelling, e.g. (+ 5) → 5 and
    // (- 5) → -5, or null if this isn't that shape. Only a bare numeric operand is accepted: a
    // sign applied to anything else is an expression we don't model.
    private static string? StripOuterSign(string text)
    {
        if (text.Length < 4 || text[0] != '(' || text[^1] != ')')
        {
            return null;
        }

        var inner = text[1..^1].TrimStart();

        if (inner.Length < 2 || (inner[0] != '+' && inner[0] != '-'))
        {
            return null;
        }

        if (NormalizeNumericText(inner[1..].Trim()) is not { } number)
        {
            return null;
        }

        return inner[0] == '-' ? "-" + number : number;
    }

    // Removes a trailing ::type cast that Postgres adds to a stored default, e.g.
    // 'active'::character varying → 'active', '-5'::integer → -5. A cast only follows a
    // constant here; leave anything else (a function call) untouched.
    private static string StripCast(string text)
    {
        var castIndex = text.IndexOf("::", StringComparison.Ordinal);

        if (castIndex < 0)
        {
            return text;
        }

        var value = text[..castIndex].Trim();

        // Postgres quotes a cast constant even when it was an unquoted number in the
        // source ('-5'::integer). Unwrap that so it normalizes as a number below.
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\''
            && !text[(castIndex + 2)..].Contains("char", StringComparison.OrdinalIgnoreCase)
            && !text[(castIndex + 2)..].Contains("text", StringComparison.OrdinalIgnoreCase))
        {
            var unquoted = value[1..^1];

            if (NormalizeNumericText(unquoted) is { } number)
            {
                return number;
            }
        }

        return value;
    }

    // Returns the invariant text of a numeric literal, or null if it isn't one. Reparsing
    // and re-rendering collapses trivia (leading +, surrounding spaces) but preserves the
    // written scale (1.50 stays 1.50), matching Postgres, which stores numeric defaults
    // with their literal scale.
    private static string? NormalizeNumericText(string text)
    {
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
        {
            return l.ToString(CultureInfo.InvariantCulture);
        }

        // Keep the literal decimal text (scale and all) once we've confirmed it parses as
        // a number, so 1.50 and 1.5 stay distinct exactly as Postgres stores them.
        if (decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out _))
        {
            return text;
        }

        return null;
    }
}
