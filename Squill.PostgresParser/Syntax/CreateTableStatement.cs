namespace Squill.PostgresParser.Syntax;

public class CreateTableStatement : Statement
{
    public CreateTableStatement(QualifiedName name)
    {
        Name = name;
    }

    public QualifiedName Name { get; }

    public IList<ITableElement> Elements { get; } = new List<ITableElement>();

    public IList<QualifiedName> Inherits { get; } = new List<QualifiedName>();

    /// <summary>
    /// The composite type this table takes its shape from (<c>CREATE TABLE t OF a_type</c>),
    /// or null for an ordinary table. Not modeled — the column set belongs to the type, so
    /// the provider reports it as an unmodeled construct (issue #143).
    /// </summary>
    public QualifiedName? OfType { get; set; }

    /// <summary>
    /// The parent this table is a partition of (<c>CREATE TABLE c PARTITION OF p</c>), or null
    /// when this is not a partition child. Not modeled, for the same reason as
    /// <see cref="OfType"/>: the shape is the parent's.
    /// </summary>
    public QualifiedName? PartitionOf { get; set; }

    /// <summary>
    /// The partition bound of a <see cref="PartitionOf"/> child, carried verbatim from the
    /// source (<c>FOR VALUES FROM (...) TO (...)</c>, <c>FOR VALUES IN (...)</c>,
    /// <c>FOR VALUES WITH (...)</c> or <c>DEFAULT</c>). Text rather than a parsed structure:
    /// nothing models it, and reproducing the source spelling is all that would ever be needed.
    /// </summary>
    public string? PartitionBound { get; set; }

    /// <summary>
    /// The partitioning strategy of a partitioned parent (<c>PARTITION BY RANGE (logdate)</c>),
    /// carried verbatim, or null for an unpartitioned table. This clause parsed before issue
    /// #143 but was never read, so a partitioned parent silently deployed as an ordinary table;
    /// it is now carried so the provider can warn.
    /// </summary>
    public string? PartitionBy { get; set; }

    /// <summary>
    /// The persistence modifier written before <c>TABLE</c>, carried verbatim as written, or
    /// null for an ordinary persistent table. <c>opttemp</c> admits <c>TEMPORARY</c> and
    /// <c>TEMP</c>, either of those prefixed <c>LOCAL</c> or <c>GLOBAL</c>, and
    /// <c>UNLOGGED</c>. Carried rather than acted on here so the provider can reject it
    /// against the statement's position; a throw from the visitor happens before there is a
    /// statement to anchor to (issue #204).
    /// </summary>
    public string? Persistence { get; set; }
}