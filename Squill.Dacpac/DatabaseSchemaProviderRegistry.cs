using System.Reflection;

namespace Squill.Dacpac;

/// <summary>
/// Discovers the concrete <see cref="DatabaseSchemaProvider"/> types (one per supported major
/// version of each engine) across the loaded assemblies by reflection, and resolves them by
/// DSP name or by provider-name + major-version. The provider assemblies define the types;
/// this registry finds them without <see cref="Squill.Dacpac"/> referencing any provider,
/// keeping the dependency graph acyclic. Discovery is done once and cached.
/// </summary>
public static class DatabaseSchemaProviderRegistry
{
    private static readonly Lazy<IReadOnlyList<DatabaseSchemaProvider>> Providers =
        new(Discover);

    /// <summary>All discovered schema providers, one per supported engine major version.</summary>
    public static IReadOnlyList<DatabaseSchemaProvider> All => Providers.Value;

    /// <summary>
    /// Resolves the schema provider whose <see cref="DatabaseSchemaProvider.DspName"/> equals
    /// <paramref name="dspName"/>, or throws <see cref="UnsupportedTargetVersionException"/> if
    /// none matches (e.g. an end-of-life or not-yet-supported version).
    /// </summary>
    public static DatabaseSchemaProvider ResolveByDspName(string dspName)
    {
        var provider = All.FirstOrDefault(
            p => string.Equals(p.DspName, dspName, StringComparison.Ordinal));

        return provider
            ?? throw new UnsupportedTargetVersionException(dspName, KnownDspNames());
    }

    /// <summary>
    /// Resolves the schema provider for a provider name and target major version, or throws
    /// <see cref="UnsupportedTargetVersionException"/> if that engine/version is not supported.
    /// </summary>
    public static DatabaseSchemaProvider Resolve(string providerName, int majorVersion)
    {
        var provider = All.FirstOrDefault(p =>
            string.Equals(p.ProviderName, providerName, StringComparison.OrdinalIgnoreCase)
            && p.MajorVersion == majorVersion);

        return provider
            ?? throw new UnsupportedTargetVersionException(providerName, majorVersion, KnownDspNames());
    }

    /// <summary>The DSP names of all discovered providers, for diagnostics.</summary>
    public static IReadOnlyList<string> KnownDspNames()
        => All.Select(p => p.DspName).OrderBy(n => n, StringComparer.Ordinal).ToList();

    private static IReadOnlyList<DatabaseSchemaProvider> Discover()
    {
        var results = new List<DatabaseSchemaProvider>();

        foreach (var assembly in LoadedAndReferencedAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // A partially loadable assembly still contributes the types that did load.
                types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
            }

            foreach (var type in types)
            {
                if (type is { IsAbstract: false, IsClass: true }
                    && typeof(DatabaseSchemaProvider).IsAssignableFrom(type)
                    && type.GetConstructor(Type.EmptyTypes) is not null)
                {
                    results.Add((DatabaseSchemaProvider)Activator.CreateInstance(type)!);
                }
            }
        }

        return results;
    }

    // The set of assemblies to scan for schema-provider types. .NET loads assemblies lazily —
    // a referenced provider assembly may not be loaded yet if none of its types have been used
    // (as in a deserialize-only path). So we start from the loaded set and force-load the
    // Squill provider assemblies they reference, so discovery does not depend on load order.
    private static IEnumerable<Assembly> LoadedAndReferencedAssemblies()
    {
        var seen = new Dictionary<string, Assembly>(StringComparer.Ordinal);

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            seen.TryAdd(assembly.FullName ?? assembly.GetName().Name ?? string.Empty, assembly);
        }

        // Force-load referenced Squill.Provider.* assemblies that aren't loaded yet.
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (reference.Name?.StartsWith("Squill.Provider.", StringComparison.Ordinal) != true
                    || seen.ContainsKey(reference.FullName))
                {
                    continue;
                }

                try
                {
                    var loaded = Assembly.Load(reference);
                    seen[loaded.FullName ?? reference.FullName] = loaded;
                }
                catch (Exception ex) when (ex is FileNotFoundException or BadImageFormatException)
                {
                    // A referenced provider assembly that cannot be loaded simply contributes
                    // no types; discovery proceeds with whatever did load.
                }
            }
        }

        return seen.Values;
    }
}

/// <summary>
/// Thrown when a DACPAC records a target engine version that has no supported
/// <see cref="DatabaseSchemaProvider"/> type — an end-of-life version, or one newer than this
/// build of Squill knows about. Callers must target a supported version.
/// </summary>
public sealed class UnsupportedTargetVersionException : Exception
{
    public UnsupportedTargetVersionException(string dspName, IReadOnlyList<string> knownDspNames)
        : base($"No supported database schema provider matches the DACPAC's target platform "
               + $"'{dspName}'. It may be end-of-life or newer than this build supports. "
               + $"Supported: {Format(knownDspNames)}.")
    {
        KnownDspNames = knownDspNames;
    }

    public UnsupportedTargetVersionException(
        string providerName, int majorVersion, IReadOnlyList<string> knownDspNames)
        : base($"{providerName} major version {majorVersion} is not a supported target platform. "
               + $"It may be end-of-life or newer than this build supports. "
               + $"Supported: {Format(knownDspNames)}.")
    {
        KnownDspNames = knownDspNames;
    }

    public IReadOnlyList<string> KnownDspNames { get; }

    private static string Format(IReadOnlyList<string> names)
        => names.Count > 0 ? string.Join(", ", names) : "(none)";
}
