namespace Squill.PostgresParser.Syntax;

/// <summary>
/// An <c>expr AT TIME ZONE zone</c> (issue #141), which converts a timestamp between
/// with- and without-time-zone representations using the named zone.
/// </summary>
public class AtTimeZoneExpression : Expression
{
    public AtTimeZoneExpression(Expression expression, Expression timeZone)
    {
        Expression = expression;
        TimeZone = timeZone;
    }

    public Expression Expression { get; }

    public Expression TimeZone { get; }
}
