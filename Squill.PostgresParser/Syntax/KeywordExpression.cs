namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A niladic keyword expression — <c>CURRENT_TIMESTAMP</c>, <c>CURRENT_DATE</c>,
/// <c>CURRENT_USER</c>, <c>SESSION_USER</c> and friends (issue #140).
///
/// These are not function applications: they take no parentheses, and Postgres stores a
/// <c>DEFAULT CURRENT_TIMESTAMP</c> with that exact spelling rather than rewriting it to
/// <c>now()</c>. Modeling them separately from <see cref="FunctionApplicationExpression"/>
/// is what lets the spelling survive the round trip.
///
/// <see cref="Keyword"/> is normalized to upper case; <see cref="Precision"/> carries the
/// optional fractional-seconds argument the time keywords accept
/// (<c>CURRENT_TIMESTAMP(3)</c>).
/// </summary>
public class KeywordExpression : Expression
{
    public KeywordExpression(string keyword, int? precision = null)
    {
        Keyword = keyword;
        Precision = precision;
    }

    public string Keyword { get; }

    /// <summary>
    /// The fractional-seconds precision of a time keyword, or <c>null</c> when the keyword
    /// was written bare. Only <c>CURRENT_TIME</c>, <c>CURRENT_TIMESTAMP</c>,
    /// <c>LOCALTIME</c> and <c>LOCALTIMESTAMP</c> accept one.
    /// </summary>
    public int? Precision { get; }
}
