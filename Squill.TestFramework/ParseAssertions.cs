using Xunit;

namespace Squill.TestFramework;

/// <summary>
/// Assertions over a parser's output, shared by the parser test projects. Both providers'
/// parsers produce their own <c>Root</c> with a list of statements (from unrelated syntax
/// namespaces), so these helpers work over the statement sequence rather than a concrete
/// <c>Root</c> type.
/// </summary>
public static class ParseAssertions
{
    /// <summary>
    /// Asserts that <paramref name="statements"/> contains exactly one statement and that it is
    /// of type <typeparamref name="TStatement"/>, returning it. This is the shared form of the
    /// per-file <c>ParseOne</c> helpers: <c>Assert.IsType&lt;T&gt;(Assert.Single(root.Statements))</c>.
    /// </summary>
    public static TStatement Single<TStatement>(IEnumerable<object> statements)
        => Assert.IsType<TStatement>(Assert.Single(statements));
}
