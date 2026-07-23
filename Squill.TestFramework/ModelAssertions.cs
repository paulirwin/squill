using Squill.Core;

namespace Squill.TestFramework;

/// <summary>
/// Assertion and diagnostic helpers for comparing <see cref="Model"/>s in tests, shared
/// across the provider, parser, and integration test tiers.
/// </summary>
public static class ModelAssertions
{
    /// <summary>
    /// Each top-level element as <c>Type:Name</c> in model order, so an ordering mismatch
    /// reports what actually differs rather than just "hashes do not match".
    /// </summary>
    public static string Describe(Model model)
        => string.Join(" | ", model.Elements.Select(i => $"{i.Type}:{i.Name}"));

    /// <summary>
    /// The sorted multiset of each top-level element's hash, as hex strings so the collection
    /// compares by value. Order-independent by construction — the parser and the database
    /// model builder emit elements in different orders, so two models with the same objects
    /// produce the same fingerprint regardless of element order.
    /// </summary>
    public static IReadOnlyList<string> ElementHashMultiset(Model model)
        => model.Elements
            .Select(e => Convert.ToHexString(e.Hash))
            .OrderBy(h => h, StringComparer.Ordinal)
            .ToList();
}
