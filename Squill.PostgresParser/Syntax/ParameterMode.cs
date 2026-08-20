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

    /// <summary>
    /// A column of a <c>RETURNS TABLE (...)</c> list. PostgreSQL does not treat this as a
    /// separate concept from an OUT parameter at call time, but it does store it as its own
    /// argument mode (<c>proargmodes</c> 't'), so it must be kept distinct to round-trip.
    /// </summary>
    Table,
}
