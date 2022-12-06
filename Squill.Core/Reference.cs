namespace Squill.Core;

public class Reference : IRelationshipEntry, IHashable
{
    public Reference(string name)
    {
        Name = name;
    }
    
    public string Name { get; }
    
    public string? ExternalSource { get; set; }

    public byte[] Hash => HashUtility.Compute(Name, ExternalSource ?? "null");
}