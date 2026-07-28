namespace Squill.PostgresParser.Syntax;

public class CreateExtensionStatement : Statement
{
    public CreateExtensionStatement(Identifier name, bool ifNotExists)
    {
        Name = name;
        IfNotExists = ifNotExists;
    }

    public Identifier Name { get; }

    public bool IfNotExists { get; }

    /// <summary>
    /// The optional schema the extension's objects are installed into
    /// (SCHEMA clause). Null when unspecified.
    /// </summary>
    public Identifier? Schema { get; set; }

    /// <summary>
    /// The optional version requested via the VERSION clause. Null when unspecified.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Whether <c>CASCADE</c> was given, installing any extensions this one depends on.
    /// Carried but not modeled (issue #143): it describes how to *install* the extension, not
    /// the installed object, so there is nothing in the catalog to compare it against. The
    /// dependencies it would install are objects in their own right and should be declared.
    /// </summary>
    public bool Cascade { get; set; }

    /// <summary>
    /// The version named by a <c>FROM old_version</c> clause, used to upgrade a pre-9.1
    /// "unpackaged" module into a real extension. Null when unspecified. Like
    /// <see cref="Cascade"/>, a one-shot installation instruction rather than a property of
    /// the resulting extension, so it is carried but not modeled.
    /// </summary>
    public string? FromVersion { get; set; }
}
