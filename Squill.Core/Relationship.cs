namespace Squill.Core;

public class Relationship
{
    public Relationship(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public IList<IRelationshipEntry> Entries { get; } = new List<IRelationshipEntry>();
}