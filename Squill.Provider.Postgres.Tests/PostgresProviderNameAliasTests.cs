using Squill.Core;
using Squill.Dacpac;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Every spelling of the provider name that <see cref="PostgresSquillProvider.Matches"/> accepts
/// must also build (issue #200 review). The two sides are resolved separately: the host picks the
/// provider with <c>Matches</c>, then <c>BuildModelCoreAsync</c> resolves a
/// <see cref="DatabaseSchemaProvider"/> from the same raw name through
/// <see cref="DatabaseSchemaProviderRegistry"/>, which compares against
/// <c>PostgresqlDatabaseSchemaProvider.ProviderName</c> exactly.
///
/// A name the first accepts and the second does not is the worst combination: the project selects
/// a provider, then fails partway through the build with a message about an unsupported target
/// <em>version</em>, which is not what went wrong.
/// </summary>
public class PostgresProviderNameAliasTests
{
    private static Workspace EmptyWorkspace() => new();

    [Theory]
    [InlineData("Postgresql")]
    [InlineData("PostgreSQL")]
    [InlineData("Postgres")]
    [InlineData("postgres")]
    public async Task EveryAcceptedProviderName_Builds(string providerName)
    {
        var provider = new PostgresSquillProvider();

        // The precondition: this is a name the host would route to this provider.
        Assert.True(
            provider.Matches(providerName),
            $"'{providerName}' is not accepted by Matches, so this case proves nothing.");

        var metadata = new ModelMetadata { ProviderName = providerName };

        // The build must not throw. Before the fix "Postgres" reached
        // DatabaseSchemaProviderRegistry.ResolveLatest, which found no exact match and threw
        // UnsupportedTargetVersionException.
        var result = await provider.BuildModelAsync(
            EmptyWorkspace(), metadata, TestContext.Current.CancellationToken);

        Assert.NotNull(result.Model);
    }

    /// <summary>
    /// The same agreement stated directly, so a future alias added to one side alone fails here
    /// rather than in a build. A name Matches accepts must resolve to a schema provider.
    /// </summary>
    [Theory]
    [InlineData("Postgresql")]
    [InlineData("PostgreSQL")]
    [InlineData("Postgres")]
    public void EveryAcceptedProviderName_ResolvesASchemaProvider(string providerName)
    {
        Assert.True(new PostgresSquillProvider().Matches(providerName));

        var schemaProvider = DacpacBuilder.SchemaProviderFor(providerName, targetVersion: null);

        Assert.Equal("Postgresql", schemaProvider.ProviderName);
    }
}
