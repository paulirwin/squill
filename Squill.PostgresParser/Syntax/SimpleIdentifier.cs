namespace Squill.PostgresParser.Syntax;

public class SimpleIdentifier : Identifier
{
    public SimpleIdentifier(string name, bool isQuoted = false, bool isUnicodeQuoted = false)
    {
        Name = name;
        IsUnicodeQuoted = isUnicodeQuoted;
        IsQuoted = isQuoted;
    }

    public override string Name { get; }

    public bool IsQuoted { get; }

    public bool IsUnicodeQuoted { get; }
}