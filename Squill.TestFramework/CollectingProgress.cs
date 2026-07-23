namespace Squill.TestFramework;

/// <summary>
/// An <see cref="IProgress{T}"/> that records every reported message, so tests can assert on
/// the progress a deployer surfaces.
/// </summary>
public sealed class CollectingProgress(List<string> messages) : IProgress<string>
{
    public void Report(string value) => messages.Add(value);
}
