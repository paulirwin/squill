namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A standalone <c>CREATE SEQUENCE name [options]</c> statement (issue #122). A sequence is a
/// first-class, independently named object — distinct from the sequence PostgreSQL creates
/// implicitly behind a <c>serial</c> or identity column, which is owned by that column and is
/// modeled as part of it (see <see cref="IdentityColumnConstraint"/>).
///
/// Every option is nullable so the model builder can tell "not specified" from "specified as
/// the value that happens to be the default": only options actually written are carried, and
/// the builder drops the ones equal to the PostgreSQL default so a parsed model and one
/// extracted from the catalog hash-match.
/// </summary>
public class CreateSequenceStatement : Statement
{
    public CreateSequenceStatement(QualifiedName name, bool ifNotExists)
    {
        Name = name;
        IfNotExists = ifNotExists;
    }

    public QualifiedName Name { get; }

    public bool IfNotExists { get; }

    /// <summary>
    /// The <c>AS &lt;type&gt;</c> clause, which bounds the sequence. PostgreSQL defaults to
    /// <c>bigint</c> — note this differs from an identity column, whose sequence takes the
    /// column's own type.
    /// </summary>
    public DataType? DataType { get; set; }

    public long? StartValue { get; set; }

    public long? Increment { get; set; }

    public long? MinValue { get; set; }

    public long? MaxValue { get; set; }

    public long? CacheSize { get; set; }

    /// <summary>
    /// <c>CYCLE</c> (true) or an explicit <c>NO CYCLE</c> (false); null when neither was
    /// written. <c>NO CYCLE</c> is also the default, so the two are equivalent in practice,
    /// but the distinction is preserved here to keep the syntax tree faithful to the source.
    /// </summary>
    public bool? IsCycling { get; set; }
}
