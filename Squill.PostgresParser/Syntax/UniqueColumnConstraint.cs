namespace Squill.PostgresParser.Syntax;

/// <summary>
/// An inline column-level uniqueness constraint: <c>col type UNIQUE</c>.
/// </summary>
public class UniqueColumnConstraint : ColumnConstraint
{
    public UniqueColumnConstraint(string text)
        : base(text)
    {
    }
}
