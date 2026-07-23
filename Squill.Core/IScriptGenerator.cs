namespace Squill.Core;

/// <summary>
/// Generates the deployment SQL for a schema comparison. Each provider has its own
/// implementation emitting its engine's DDL, but the deploy orchestration only needs these
/// two entry points, so it depends on this interface rather than a concrete generator.
/// </summary>
public interface IScriptGenerator
{
    /// <summary>The full deployment script for every delta in <paramref name="comparison"/>.</summary>
    string GenerateScript(SchemaComparison comparison);

    /// <summary>The script for a single <paramref name="delta"/>, run one delta at a time on deploy.</summary>
    string GenerateScriptForDelta(SchemaDelta delta);
}
