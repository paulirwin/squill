namespace Squill.PostgresParser.Syntax;

/// <summary>
/// The argument mode of a routine parameter. PostgreSQL's default when no mode is
/// written is <see cref="In"/>, which is why it is the first (default) member.
/// </summary>
public enum ParameterMode
{
    In,
    Out,
    InOut,
    Variadic,
}
