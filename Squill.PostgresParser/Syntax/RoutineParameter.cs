namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A single parameter of a routine — its optional name, its mode (IN/OUT/INOUT/VARIADIC)
/// and its declared type. A parameter may be unnamed (<c>CREATE PROCEDURE p(integer)</c>),
/// in which case <see cref="Name"/> is null.
/// </summary>
public class RoutineParameter : SyntaxNode
{
    public RoutineParameter(Identifier? name, ParameterMode mode, DataType dataType)
    {
        Name = name;
        Mode = mode;
        DataType = dataType;
    }

    public Identifier? Name { get; }

    public ParameterMode Mode { get; }

    public DataType DataType { get; }

    /// <summary>
    /// The raw source text of the parameter's DEFAULT expression, or null when it has none.
    /// A default affects how the routine may be called, not its identity, so it is carried
    /// for scripting only.
    /// </summary>
    public string? DefaultExpression { get; set; }
}
