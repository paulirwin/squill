using Squill.Dacpac;
using Squill.Provider.Postgres;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Pins PostgreSQL's answer to the universal identifier-limit capability (issue #163).
///
/// <para>
/// Unlike the MariaDB family's 64 <em>characters</em>, PostgreSQL's limit is
/// <c>NAMEDATALEN - 1</c> = 63 <em>bytes</em>. Both the number and the unit differ, which is
/// why <see cref="DatabaseSchemaProvider.MeasureIdentifier"/> travels with
/// <see cref="DatabaseSchemaProvider.MaxIdentifierLength"/> rather than each caller assuming
/// one: measuring Postgres identifiers in characters would accept names the server silently
/// truncates, and measuring MariaDB's in bytes would reject valid multi-byte ones.
/// </para>
/// </summary>
public class SchemaProviderIdentifierLimitTests
{
    [Fact]
    public void Postgres_Caps63Bytes()
    {
        var provider = new Postgresql18DatabaseSchemaProvider();

        Assert.Equal(63, provider.MaxIdentifierLength);
    }

    /// <summary>
    /// Bytes, not characters: 32 two-byte characters is 32 characters but 64 bytes, so it is
    /// over a limit that a character-based measurement would report as comfortably under.
    /// </summary>
    [Fact]
    public void Postgres_MeasuresBytesNotCharacters()
    {
        var provider = new Postgresql18DatabaseSchemaProvider();

        var name = new string('é', 32);

        Assert.Equal(32, name.Length);
        Assert.Equal(64, provider.MeasureIdentifier(name));
        Assert.True(provider.MeasureIdentifier(name) > provider.MaxIdentifierLength);
    }

    /// <summary>
    /// The limit is engine-wide, not per-major: every supported major must answer identically,
    /// or a project pinning an older target would validate names differently from one that
    /// does not pin.
    /// </summary>
    [Fact]
    public void EveryMajor_AgreesOnTheLimit()
    {
        var postgresProviders = DatabaseSchemaProviderRegistry.All
            .OfType<PostgresqlDatabaseSchemaProvider>()
            .ToList();

        Assert.NotEmpty(postgresProviders);
        Assert.Single(postgresProviders.Select(p => p.MaxIdentifierLength).Distinct());
        Assert.Single(postgresProviders.Select(p => p.MeasureIdentifier("abc")).Distinct());
    }
}
