namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A <c>CREATE TYPE name AS (field type, ...)</c> statement (issue #122) — a composite type.
///
/// PostgreSQL models a composite type as a standalone, declared object whose attributes are
/// an ordered list of name/type pairs. Attribute order is significant: it fixes the field
/// order of the row values of the type, so it is preserved rather than sorted.
///
/// Distinct from the row type PostgreSQL creates implicitly for every table, which is not a
/// declared object and is never modeled.
/// </summary>
public class CreateCompositeTypeStatement : Statement
{
    public CreateCompositeTypeStatement(QualifiedName name)
    {
        Name = name;
    }

    public QualifiedName Name { get; }

    /// <summary>
    /// The composite type's attributes, in declaration order.
    /// </summary>
    public IList<CompositeTypeAttribute> Attributes { get; } = new List<CompositeTypeAttribute>();
}

/// <summary>
/// One <c>field type</c> pair of a composite type's attribute list.
/// </summary>
public class CompositeTypeAttribute : SyntaxNode
{
    public CompositeTypeAttribute(Identifier name, DataType dataType)
    {
        Name = name;
        DataType = dataType;
    }

    public Identifier Name { get; }

    public DataType DataType { get; }
}
