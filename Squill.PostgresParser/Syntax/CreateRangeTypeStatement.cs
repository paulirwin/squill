namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A <c>CREATE TYPE name AS RANGE (SUBTYPE = ..., ...)</c> statement (issue #122) — a range
/// type over an ordered subtype.
///
/// <c>SUBTYPE</c> is the only required item and is what gives the type its identity; the
/// remaining items (<c>SUBTYPE_OPCLASS</c>, <c>COLLATION</c>, <c>CANONICAL</c>,
/// <c>SUBTYPE_DIFF</c>, <c>MULTIRANGE_TYPE_NAME</c>) are optional refinements.
///
/// PostgreSQL also creates a companion <em>multirange</em> type for every range type. That is
/// implicit, not declared, and is never modeled as its own object.
/// </summary>
public class CreateRangeTypeStatement : Statement
{
    public CreateRangeTypeStatement(QualifiedName name, DataType subtype)
    {
        Name = name;
        Subtype = subtype;
    }

    public QualifiedName Name { get; }

    /// <summary>The <c>SUBTYPE</c> the range is built over.</summary>
    public DataType Subtype { get; }

    /// <summary>
    /// The <c>SUBTYPE_OPCLASS</c>, when one is named. PostgreSQL falls back to the subtype's
    /// default operator class, so null means "the default".
    /// </summary>
    public string? SubtypeOperatorClass { get; set; }

    /// <summary>The <c>COLLATION</c>, when one is named.</summary>
    public string? Collation { get; set; }
}
