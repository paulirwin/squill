namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A function's volatility category, describing how its result depends on its inputs and
/// the database state. PostgreSQL's default when none is written is <see cref="Volatile"/>.
/// See https://www.postgresql.org/docs/current/xfunc-volatility.html
/// </summary>
public enum FunctionVolatility
{
    Volatile,
    Stable,
    Immutable,
}
