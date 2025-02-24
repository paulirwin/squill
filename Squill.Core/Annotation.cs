namespace Squill.Core;

public class Annotation
{
    public Annotation(string type)
    {
        Type = type;
    }

    public string Type { get; }

    public int? Disambiguator { get; set; }

    public Element? AttachedElement { get; set; }
}