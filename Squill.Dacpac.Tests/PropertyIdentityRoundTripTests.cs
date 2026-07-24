using Squill.Core;

namespace Squill.Dacpac.Tests;

/// <summary>
/// A property that opts out of its element's identity (<see cref="Property.ParticipatesInIdentity"/>)
/// must still be opted out after the model has been through a DACPAC (issue #122).
///
/// <para>
/// The flag is deliberately not serialized: <c>model.xml</c> aims to stay byte-compatible with
/// SSDT-built DACPACs, so we cannot invent an attribute for it. It is instead a static rule of
/// the element type — a domain's CHECK text and a view's query never participate, for every
/// model — which the provider supplies via <see cref="IModelIdentityRules"/> and the reader
/// re-applies on load.
/// </para>
///
/// <para>
/// Without this, a deserialized domain's CheckExpression came back participating in the hash and
/// so could never match the predicate PostgreSQL reports back, making every redeploy of a schema
/// with a domain fail with "Altering an existing SqlDomain is not yet supported."
/// </para>
/// </summary>
public class PropertyIdentityRoundTripTests
{
    // Mirrors the real provider rule: a domain's CHECK text and a view's query are excluded.
    private sealed class TestIdentityRules : IModelIdentityRules
    {
        public bool ParticipatesInIdentity(string elementType, string propertyName)
            => (elementType, propertyName) is not (("SqlDomain", "CheckExpression") or ("SqlView", "Definition"));
    }

    private static async Task<Model> RoundTripAsync(Model model, IModelIdentityRules? rules)
    {
        var metadata = new ModelMetadata { ProviderName = "Postgresql", Name = "Test" };

        await using var stream = new MemoryStream();
        await DacpacSerializer.Serialize(metadata, model, stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        var (_, result) = await DacpacSerializer.Deserialize(
            stream, rules, TestContext.Current.CancellationToken);

        return result;
    }

    [Fact]
    public async Task Deserialize_WithIdentityRules_RestoresNonParticipatingProperty()
    {
        var model = new Model();
        model.Elements.Add(new Element("SqlDomain")
        {
            Name = "public.year",
            Properties =
            {
                new Property("CheckExpression", "VALUE >= 1901 AND VALUE <= 2155",
                    participatesInIdentity: false),
            },
        });

        var result = await RoundTripAsync(model, new TestIdentityRules());

        var domain = Assert.Single(result.Elements);
        var check = Assert.Single(domain.Properties);
        Assert.Equal("CheckExpression", check.Name);
        Assert.False(check.ParticipatesInIdentity);
    }

    /// <summary>
    /// The point of the flag: two domains differing only in CHECK text — as a declared predicate
    /// and the rewritten one PostgreSQL reports back — hash equal after a round trip, so the diff
    /// engine sees no change and the redeploy is a no-op.
    /// </summary>
    [Fact]
    public async Task Deserialize_WithIdentityRules_ExcludedPropertyDoesNotAffectHash()
    {
        static Model DomainWithCheck(string check)
        {
            var model = new Model();
            model.Elements.Add(new Element("SqlDomain")
            {
                Name = "public.year",
                Properties =
                {
                    new Property("CheckExpression", check, participatesInIdentity: false),
                },
            });
            return model;
        }

        var declared = await RoundTripAsync(
            DomainWithCheck("VALUE >= 1901 AND VALUE <= 2155"), new TestIdentityRules());

        // What pg_get_constraintdef reports back for the same predicate.
        var rewritten = DomainWithCheck("((VALUE >= 1901) AND (VALUE <= 2155))");

        Assert.True(HashUtility.HashesEqual(declared.Hash, rewritten.Hash),
            "a non-participating property must not contribute to the model hash after a round trip");
    }

    /// <summary>
    /// A property the rules say nothing about keeps the default: it participates.
    /// </summary>
    [Fact]
    public async Task Deserialize_WithIdentityRules_OrdinaryPropertyStillParticipates()
    {
        var model = new Model();
        model.Elements.Add(new Element("SqlDomain")
        {
            Name = "public.year",
            Properties = { new Property("Length", 10) },
        });

        var result = await RoundTripAsync(model, new TestIdentityRules());

        var property = Assert.Single(Assert.Single(result.Elements).Properties);
        Assert.True(property.ParticipatesInIdentity);
    }

    /// <summary>
    /// With no rules supplied the reader cannot know better, so everything participates. This
    /// documents the pre-existing behaviour that callers without a provider still get.
    /// </summary>
    [Fact]
    public async Task Deserialize_WithoutIdentityRules_EverythingParticipates()
    {
        var model = new Model();
        model.Elements.Add(new Element("SqlDomain")
        {
            Name = "public.year",
            Properties =
            {
                new Property("CheckExpression", "VALUE > 0", participatesInIdentity: false),
            },
        });

        var result = await RoundTripAsync(model, rules: null);

        var property = Assert.Single(Assert.Single(result.Elements).Properties);
        Assert.True(property.ParticipatesInIdentity);
    }

    /// <summary>
    /// The rules apply to elements nested inside a relationship entry too (a view's columns, a
    /// table's constraints), not only top-level elements.
    /// </summary>
    [Fact]
    public async Task Deserialize_WithIdentityRules_AppliesToNestedElements()
    {
        var model = new Model();
        model.Elements.Add(new Element("SqlTable")
        {
            Name = "public.films",
            Relationships =
            {
                new Relationship("Views")
                {
                    new Element("SqlView")
                    {
                        Name = "public.film_list",
                        Properties =
                        {
                            new Property("Definition", "SELECT 1", participatesInIdentity: false),
                        },
                    },
                },
            },
        });

        var result = await RoundTripAsync(model, new TestIdentityRules());

        var nested = Assert.IsType<Element>(
            Assert.Single(Assert.Single(Assert.Single(result.Elements).Relationships)));
        Assert.False(Assert.Single(nested.Properties).ParticipatesInIdentity);
    }
}
