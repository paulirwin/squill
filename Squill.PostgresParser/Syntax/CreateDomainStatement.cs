namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A <c>CREATE DOMAIN name AS &lt;type&gt; [constraints]</c> statement. A domain is a base type
/// constrained by (optionally named) column constraints — most commonly a <c>CHECK</c> that
/// every value stored in a column of the domain must satisfy. Squill treats the domain as a
/// declared, standalone object that must exist before any column that references it.
/// </summary>
public class CreateDomainStatement : Statement
{
    public CreateDomainStatement(QualifiedName name, DataType dataType)
    {
        Name = name;
        DataType = dataType;
    }

    public QualifiedName Name { get; }

    /// <summary>
    /// The domain's underlying base type (the <c>AS &lt;type&gt;</c> clause).
    /// </summary>
    public DataType DataType { get; }

    /// <summary>
    /// The domain's constraints (e.g. <c>NOT NULL</c>, <c>CHECK (...)</c>), in declaration
    /// order. Reuses the column-constraint model since a domain's constraints share the
    /// <c>colquallist</c> grammar with a table column's.
    /// </summary>
    public IList<ColumnConstraint> Constraints { get; } = new List<ColumnConstraint>();
}
