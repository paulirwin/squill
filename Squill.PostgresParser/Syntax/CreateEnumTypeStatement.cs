namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A <c>CREATE TYPE name AS ENUM ('a', 'b', ...)</c> statement. PostgreSQL models an enum
/// as a first-class, ordered user-defined type whose allowed values (labels) are fixed at
/// declaration time. The label order is significant — it defines the type's sort order — so
/// it is preserved. Squill treats the type as a declared, standalone object (like a schema
/// or extension) that must exist before any column that references it.
/// </summary>
public class CreateEnumTypeStatement : Statement
{
    public CreateEnumTypeStatement(QualifiedName name, IReadOnlyList<string> labels)
    {
        Name = name;
        Labels = labels;
    }

    public QualifiedName Name { get; }

    /// <summary>
    /// The enum's labels, in declaration order (their significant sort order).
    /// </summary>
    public IReadOnlyList<string> Labels { get; }
}
