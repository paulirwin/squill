namespace Squill.Core;

public class Property : IHashable
{
    public Property(string name, object? value, bool participatesInIdentity = true)
    {
        Name = name;
        Value = value;
        ParticipatesInIdentity = participatesInIdentity;
    }

    public string Name { get; }

    // TODO.PI: should this be a string?
    public object? Value { get; }

    /// <summary>
    /// Whether this property is part of the element's identity, and so contributes to its
    /// hash. Almost every property does.
    ///
    /// A property opts out when it carries something needed to script the object but which
    /// the target database cannot report back in the same form — a view's query, which
    /// PostgreSQL and MariaDB both rewrite when they store it. Hashing such a property
    /// would make the element differ from its deployed counterpart on every comparison, so
    /// the object would be redeployed forever. Excluding it keeps a model extracted from a
    /// database hash-comparable with one parsed from source.
    /// </summary>
    public bool ParticipatesInIdentity { get; }

    public override string ToString() => $"{Name}={Value ?? "null"}";

    public byte[] Hash => HashUtility.Compute(Name, Value?.ToString() ?? "null");
}
