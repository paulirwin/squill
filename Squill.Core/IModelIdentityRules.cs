namespace Squill.Core;

/// <summary>
/// The provider's rules for which properties take part in an element's identity — that is,
/// which contribute to its hash (see <see cref="Property.ParticipatesInIdentity"/>).
///
/// <para>
/// This exists because the flag cannot be carried in the DACPAC. <c>model.xml</c> aims to stay
/// byte-compatible with SSDT-built packages, so we cannot add an attribute of our own for it,
/// and a property read back from a DACPAC would otherwise default to participating. That is not
/// a loss of information, though: whether a property participates is a static fact about the
/// element type — a domain's CHECK text and a view's query never participate, in any model —
/// so the provider can simply restate the rule when a model is loaded.
/// </para>
///
/// <para>
/// Implementations must be pure and depend only on the two names given, never on model state.
/// </para>
/// </summary>
public interface IModelIdentityRules
{
    /// <summary>
    /// Whether a property named <paramref name="propertyName"/> on an element of type
    /// <paramref name="elementType"/> takes part in that element's identity. Almost every
    /// property does, so implementations return <c>true</c> unless they recognize a specific
    /// exclusion.
    /// </summary>
    bool ParticipatesInIdentity(string elementType, string propertyName);
}
