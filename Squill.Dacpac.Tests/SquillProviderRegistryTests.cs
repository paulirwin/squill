using Squill.Core;
using Squill.Dacpac;

namespace Squill.Dacpac.Tests;

public class SquillProviderRegistryTests
{
    // A minimal fake provider that matches a fixed set of names, for testing resolution
    // without pulling in a concrete database provider.
    private sealed class FakeProvider(string name, params string[] aliases) : ISquillProvider
    {
        private readonly HashSet<string> _names =
            new(aliases.Append(name), StringComparer.OrdinalIgnoreCase);

        public string Name => name;

        public bool Matches(string providerName) => _names.Contains(providerName);

        public Task<BuildResult> BuildModelAsync(
            Workspace workspace, ModelMetadata metadata, CancellationToken cancellationToken = default)
            => Task.FromResult(new BuildResult(new Model()));

        public Task<DeployResult> DeployAsync(Stream dacpacStream, string connectionString,
            string? targetDatabaseName, bool dryRun, IProgress<string>? progress,
            DeployOptions? options, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployResult(string.Empty, false));

        public Task<DeployResult> ScriptAsync(Stream dacpacStream, string connectionString,
            string? targetDatabaseName, IProgress<string>? progress, DeployOptions? options,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployResult(string.Empty, false));
    }

    [Fact]
    public void Resolve_MatchesByCanonicalName()
    {
        var registry = new SquillProviderRegistry()
            .Register(new FakeProvider("Postgresql"))
            .Register(new FakeProvider("MariaDb", "MySql"));

        Assert.Equal("Postgresql", registry.Resolve("Postgresql").Name);
        Assert.Equal("MariaDb", registry.Resolve("MariaDb").Name);
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        var registry = new SquillProviderRegistry()
            .Register(new FakeProvider("MariaDb", "MySql"));

        Assert.Equal("MariaDb", registry.Resolve("mariadb").Name);
        Assert.Equal("MariaDb", registry.Resolve("MYSQL").Name);
    }

    [Fact]
    public void Resolve_MatchesByAlias()
    {
        var registry = new SquillProviderRegistry()
            .Register(new FakeProvider("MariaDb", "MySql"));

        // Both MariaDb and MySql select the one MariaDB provider.
        Assert.Equal("MariaDb", registry.Resolve("MySql").Name);
    }

    [Fact]
    public void Resolve_UnknownName_ThrowsWithKnownNames()
    {
        var registry = new SquillProviderRegistry()
            .Register(new FakeProvider("Postgresql"))
            .Register(new FakeProvider("MariaDb", "MySql"));

        var ex = Assert.Throws<SquillProviderNotFoundException>(() => registry.Resolve("Oracle"));

        Assert.Equal("Oracle", ex.ProviderName);
        Assert.Contains("Postgresql", ex.KnownNames);
        Assert.Contains("MariaDb", ex.KnownNames);
        Assert.Contains("Oracle", ex.Message);
    }

    [Fact]
    public void Resolve_EmptyName_Throws()
    {
        var registry = new SquillProviderRegistry()
            .Register(new FakeProvider("Postgresql"));

        Assert.Throws<SquillProviderNotFoundException>(() => registry.Resolve(""));
    }

    [Fact]
    public void Resolve_LaterRegistrationWins_OnNameClash()
    {
        var first = new FakeProvider("Dup");
        var second = new FakeProvider("Dup");

        var registry = new SquillProviderRegistry()
            .Register(first)
            .Register(second);

        Assert.Same(second, registry.Resolve("Dup"));
    }
}
