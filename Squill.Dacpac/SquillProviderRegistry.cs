namespace Squill.Dacpac;

/// <summary>
/// A registry of the <see cref="ISquillProvider"/>s a host knows about, resolved by provider
/// name. The registry is populated by the host (the CLI or the MSBuild task) — which
/// references the concrete provider assemblies — so that <see cref="Squill.Dacpac"/> itself
/// does not depend on any provider, keeping the dependency graph acyclic.
/// </summary>
public sealed class SquillProviderRegistry
{
    private readonly List<ISquillProvider> _providers = new();

    /// <summary>Registers a provider. Later registrations take precedence on a name clash.</summary>
    public SquillProviderRegistry Register(ISquillProvider provider)
    {
        _providers.Add(provider);
        return this;
    }

    /// <summary>
    /// Resolves the provider that answers to <paramref name="providerName"/>, or throws a
    /// <see cref="SquillProviderNotFoundException"/> naming the known providers if none does.
    /// The most recently registered matching provider wins.
    /// </summary>
    public ISquillProvider Resolve(string providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            throw new SquillProviderNotFoundException(providerName, KnownNames());
        }

        for (var i = _providers.Count - 1; i >= 0; i--)
        {
            if (_providers[i].Matches(providerName))
            {
                return _providers[i];
            }
        }

        throw new SquillProviderNotFoundException(providerName, KnownNames());
    }

    /// <summary>The canonical names of the registered providers, for diagnostics.</summary>
    public IReadOnlyList<string> KnownNames() => _providers.Select(p => p.Name).ToList();
}

/// <summary>
/// Thrown when a DACPAC (or project) names a provider that no registered
/// <see cref="ISquillProvider"/> answers to.
/// </summary>
public sealed class SquillProviderNotFoundException : Exception
{
    public SquillProviderNotFoundException(string providerName, IReadOnlyList<string> knownNames)
        : base(BuildMessage(providerName, knownNames))
    {
        ProviderName = providerName;
        KnownNames = knownNames;
    }

    public string ProviderName { get; }

    public IReadOnlyList<string> KnownNames { get; }

    private static string BuildMessage(string providerName, IReadOnlyList<string> knownNames)
    {
        var known = knownNames.Count > 0 ? string.Join(", ", knownNames) : "(none)";
        var named = string.IsNullOrWhiteSpace(providerName) ? "(empty)" : $"'{providerName}'";

        return $"No Squill provider is registered for provider name {named}. "
            + $"Known providers: {known}.";
    }
}
