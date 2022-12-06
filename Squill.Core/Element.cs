namespace Squill.Core;

public class Element : IRelationshipEntry
{
    public Element(string type)
    {
        Type = type;
    }

    public string Type { get; }

    public string? Name { get; set; }

    public IList<Relationship> Relationships { get; } = new List<Relationship>();

    public IList<Property> Properties { get; } = new List<Property>();

    public override string ToString() => $"{Type} {Name ?? "(anonymous)"}";

    public T GetRequiredProperty<T>(string name)
    {
        return GetProperty<T>(name)
               ?? throw new InvalidOperationException("Required property not found: {name}");
    }
    
    public T? GetProperty<T>(string name)
    {
        return (T?)Properties.FirstOrDefault(i => i.Name == name)?.Value;
    }

    public byte[] Hash => HashUtility.Concat(
        HashUtility.Compute(Type, Name ?? "null"),
        HashUtility.Compute(Relationships),
        HashUtility.Compute(Properties)
    );


    public Relationship? GetRelationship(string name) => Relationships.SingleOrDefault(i => i.Name.Equals(name));
}