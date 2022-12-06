using System.Collections;

namespace Squill.Core;

public class Relationship : IHashable, IEnumerable<IRelationshipEntry>
{
    public Relationship(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public IList<IRelationshipEntry> Entries { get; } = new List<IRelationshipEntry>();

    public void Add(IRelationshipEntry entry) => Entries.Add(entry);

    public byte[] Hash => HashUtility.Concat(HashUtility.Compute(Name), HashUtility.Compute(Entries));

    public IEnumerator<IRelationshipEntry> GetEnumerator() => Entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public Reference? GetReference(string name, string? externalSource = null) =>
        Entries
            .OfType<Reference>()
            .SingleOrDefault(i => i.Name.Equals(name)
                                  && string.Equals(i.ExternalSource, externalSource));

    public Element? GetElement(string type) => Entries.OfType<Element>().SingleOrDefault(i => i.Type.Equals(type));
}