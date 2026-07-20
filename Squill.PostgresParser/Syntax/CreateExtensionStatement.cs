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
}
