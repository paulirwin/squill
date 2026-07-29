namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A <c>CREATE COLLATION</c> (issue #159), a DEFINE-family statement.
///
/// PostgreSQL resolves the declared items into a fixed set of catalog facets, and does not
/// retain how they were written: measured against postgres:latest,
/// <c>CREATE COLLATION x FROM "POSIX"</c> and
/// <c>CREATE COLLATION x (LOCALE = 'POSIX', PROVIDER = libc)</c> store byte-identical rows.
/// The model therefore carries the resolved facets rather than the source spelling, and the
/// <c>FROM</c> form is normalized into them at parse time — otherwise one of the two spellings
/// would re-diff on every deploy.
/// </summary>
public class CreateCollationStatement : Statement
{
    public CreateCollationStatement(QualifiedName name)
    {
        Name = name;
    }

    public QualifiedName Name { get; }

    /// <summary>
    /// The collation this one copies, for the <c>CREATE COLLATION x FROM y</c> form. The model
    /// builder resolves it against the other declared collations rather than storing it: the
    /// catalog keeps no record of the copy.
    /// </summary>
    public QualifiedName? CopiedFrom { get; set; }

    /// <summary>"libc" or "icu". Null when not declared, which PostgreSQL resolves to libc.</summary>
    public string? Provider { get; set; }

    /// <summary>
    /// The LOCALE item, which sets both <c>LC_COLLATE</c> and <c>LC_CTYPE</c> for the libc
    /// provider, and <c>colllocale</c> for icu.
    /// </summary>
    public string? Locale { get; set; }

    public string? LcCollate { get; set; }

    public string? LcCtype { get; set; }

    /// <summary>False for <c>DETERMINISTIC = false</c>; null when not declared (true).</summary>
    public bool? Deterministic { get; set; }
}
