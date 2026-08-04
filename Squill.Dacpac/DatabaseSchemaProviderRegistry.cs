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
            p.MatchesProviderName(providerName)
            && p.MajorVersion == majorVersion);

        return provider
            ?? throw new UnsupportedTargetVersionException(providerName, majorVersion, KnownDspNames());
    }

    /// <summary>
    /// Resolves the schema provider for a full target version, carrying anything below the major
    /// onto the returned instance so point-release feature gates can consult it.
    ///
    /// <para>
    /// A target that names a non-zero minor or patch yields a <em>fresh</em> instance rather than
    /// the cached singleton: the cached one is shared by every caller, so writing a build's target
    /// onto it would leak into unrelated builds. A <c>null</c> target, or one that is exactly the
    /// major's <c>.0.0</c>, carries nothing the canonical instance does not already describe, so
    /// it needs no per-build state and reuses the cached instance.
    /// </para>
    /// </summary>
    public static DatabaseSchemaProvider Resolve(string providerName, TargetVersion? targetVersion)
    {
        if (targetVersion is not { } version)
        {
            return ResolveLatest(providerName);
        }

        var canonical = Resolve(providerName, version.Major);

        // A floor at the major's oldest release is what the canonical instance already describes,
        // so there is nothing per-build to carry and the shared instance is correct.
        return version is { Minor: 0, Patch: 0 }
            ? canonical
            : WithTargetVersion(canonical, version);
    }

    /// <summary>
    /// Builds a copy of <paramref name="provider"/> carrying <paramref name="targetVersion"/>, via
    /// the subclass's <c>(TargetVersion?)</c> constructor. A provider that has not added one keeps
    /// working at its major's oldest release rather than failing the build, since the missing
    /// constructor only means that engine has no point-release gates yet.
    /// </summary>
    private static DatabaseSchemaProvider WithTargetVersion(
        DatabaseSchemaProvider provider, TargetVersion targetVersion)
    {
        var constructor = provider.GetType().GetConstructor([typeof(TargetVersion?)]);

        if (constructor is null)
        {
            return provider;
        }

        return (DatabaseSchemaProvider)constructor.Invoke([(TargetVersion?)targetVersion]);
    }

    /// <summary>
    /// Resolves the schema provider for a provider name and an <em>optional</em> target major
    /// version: the exact version when one is given, otherwise the latest supported major for
    /// that engine.
    ///
    /// Every build has a schema provider, so engine capabilities are always answerable without
    /// a null check or a fallback path. Defaulting to the latest supported version means an
    /// unconstrained project behaves as if it targets a current server, which is what declaring
    /// no minimum version means. Note this does <em>not</em> stamp a <c>DspName</c> into
    /// <c>model.xml</c> — an unconstrained DACPAC still records no target platform, so the
    /// default is a build-time convenience, not a version constraint imposed on deploy.
    /// </summary>
    public static DatabaseSchemaProvider Resolve(string providerName, int? majorVersion)
        => majorVersion is { } major
            ? Resolve(providerName, major)
            : ResolveLatest(providerName);

    /// <summary>
    /// Resolves the highest supported major version for an engine, or throws
    /// <see cref="UnsupportedTargetVersionException"/> if the engine is unknown.
    /// </summary>
    public static DatabaseSchemaProvider ResolveLatest(string providerName)
    {
        var provider = All
            .Where(p => p.MatchesProviderName(providerName))
            .MaxBy(p => p.MajorVersion);

        // Reuses the DSP-name overload's phrasing: for an unknown engine there is no version to
        // name, and the supported-list it prints is what the caller needs either way.
        return provider
            ?? throw new UnsupportedTargetVersionException(providerName, KnownDspNames());
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
