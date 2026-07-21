namespace Squill.Core;

/// <summary>
/// The result of building a <see cref="Model"/> from declarative SQL source: the model
/// itself plus any non-fatal diagnostics collected on the way (issue #61). Errors are still
/// thrown — a build that produces a result always succeeded — but warnings have no other
/// channel out of a provider, so they ride along here for the host to report.
/// </summary>
/// <param name="Model">The model built from the workspace.</param>
/// <param name="Warnings">Warnings collected during the build; empty when there are none.</param>
public sealed record BuildResult(Model Model, IReadOnlyList<SqlSourceDiagnostic> Warnings)
{
    /// <summary>A result carrying no warnings.</summary>
    public BuildResult(Model model)
        : this(model, [])
    {
    }
}
