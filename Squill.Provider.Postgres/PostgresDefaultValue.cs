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
/// A function default (e.g. <c>now()</c>, the <c>nextval(...)</c> of a serial column) or
/// any other non-constant expression is not modeled — <see cref="FromExpression"/> and
/// <see cref="FromDatabaseText"/> return <c>null</c>, so the default is left off the model
/// rather than modeled as something that could not round-trip. A <c>DEFAULT NULL</c> is
/// likewise not modeled, since Postgres stores no default for it.
/// </summary>
internal static class PostgresDefaultValue
{
    /// <summary>
    /// The canonical form of a parsed <c>DEFAULT</c> expression, or <c>null</c> if it is
    /// not a modeled constant literal.
    /// </summary>
    public static string? FromExpression(Expression expression)
    {
        switch (expression)
        {
            case LiteralExpression literal:
                return FromLiteralValue(literal.Value);

            // A negated numeric literal, e.g. DEFAULT -5. (The b_expr parser does not yet
            // produce a unary sign in a DEFAULT position, so this arm is currently reached
            // only from the database side; it is kept so a future parser change works.)
            case UnaryExpression { Operator: PostgresBuiltInUnaryOperator.Negate } negate
                when FromNumericValue(negate.Expression) is { } inner:
                return "-" + inner;

            // A leading + is a no-op sign on a numeric literal, e.g. DEFAULT +5.
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

        var text = StripCast(columnDefault.Trim());

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

    // The canonical form of a numeric literal (possibly wrapped in an outer sign), or null.
    private static string? FromNumericValue(Expression expression) =>
        expression is LiteralExpression { Value: long or int or decimal } literal
            ? FromLiteralValue(literal.Value)
            : null;

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
