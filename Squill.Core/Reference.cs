namespace Squill.Core;

public class Reference : IRelationshipEntry
{
    public Reference(string name)
    {
        Name = name;
    }
    
    public string Name { get; }
    
    public string? ExternalSource { get; set; }
}