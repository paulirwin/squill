namespace Squill.Core;

public class Property
{
    public Property(string name, object? value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }

    // TODO.PI: should this be a string?
    public object? Value { get; }

    public override string ToString() => $"{Name}={Value ?? "null"}";
}