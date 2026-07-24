namespace Squill.Core;

/// <summary>
/// A change to an existing domain that Squill cannot script (issue #122).
///
/// PostgreSQL's <c>ALTER DOMAIN</c> can change a domain's default, its NOT NULL and its
/// constraints — but <em>not</em> its base type; there is no <c>ALTER DOMAIN ... TYPE</c>.
/// Changing the base type means dropping and recreating the domain, which fails while any
/// column is typed as it, so it cannot be done without rewriting those columns.
///
/// A domain's CHECK predicate is deliberately not diffed either: PostgreSQL rewrites the
/// predicate when it stores it, so a declared expression can never text-match the extracted
/// one (see <c>PostgresDatabaseDependencyAnalyzer.ParticipatesInIdentity</c>). So the only
/// thing that brings a domain here is its base type.
///
/// The delta exists so the diff records the change and the script generator can report it
/// precisely, rather than the comparison throwing a bare <c>NotImplementedException</c> naming
/// only the element type.
/// </summary>
public class AlterDomainTypeDelta : SchemaDelta
{
    public AlterDomainTypeDelta(Element sourceElement, Element targetElement)
    {
        SourceElement = sourceElement;
        TargetElement = targetElement;
    }

    /// <summary>The domain element from the source (desired) model.</summary>
    public Element SourceElement { get; }

    /// <summary>The domain element as it currently exists in the target database.</summary>
    public Element TargetElement { get; }
}
