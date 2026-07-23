namespace Squill.Core;

/// <summary>
/// The provider-agnostic dispatch for turning a schema comparison into DDL. The two public
/// entry points are shared: <see cref="GenerateScript"/> concatenates the per-delta scripts,
/// and <see cref="GenerateScriptForDelta"/> routes each delta type to a provider hook. The
/// emitted SQL text is entirely engine-specific (quoting, identity vs auto-increment,
/// CREATE OR REPLACE vs DROP+CREATE, …), so every emitter is an abstract hook the provider
/// supplies.
///
/// <see cref="GenerateAlterExtensionScript"/> is the one delta a provider may not handle
/// (extensions are Postgres-only); its base implementation throws, matching the original
/// fall-through, and only Postgres overrides it.
/// </summary>
public abstract class ScriptGeneratorBase : IScriptGenerator
{
    /// <summary>
    /// Generates a single script covering every delta in the comparison, in order, with a blank
    /// line between steps so the generated (or previewed) script is easier to read.
    /// </summary>
    public string GenerateScript(SchemaComparison comparison)
    {
        var sb = new System.Text.StringBuilder();

        foreach (var delta in comparison.Deltas)
        {
            sb.Append(GenerateScriptForDelta(delta));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public string GenerateScriptForDelta(SchemaDelta delta) => delta switch
    {
        CreateDelta create => GenerateCreateScript(create),
        AlterDelta alter => GenerateAlterScript(alter),
        RebuildTableDelta rebuild => GenerateRebuildScript(rebuild),
        DropDelta drop => GenerateDropScript(drop),
        RecreateDelta recreate => GenerateRecreateScript(recreate),
        AlterExtensionVersionDelta alterExtension => GenerateAlterExtensionScript(alterExtension),
        AddConstraintDelta addConstraint => GenerateAddConstraintScript(addConstraint),
        _ => throw new NotImplementedException(
            $"Generating a script for {delta.GetType().Name} is not supported."),
    };

    protected abstract string GenerateCreateScript(CreateDelta delta);

    protected abstract string GenerateAlterScript(AlterDelta delta);

    protected abstract string GenerateRebuildScript(RebuildTableDelta delta);

    protected abstract string GenerateDropScript(DropDelta delta);

    protected abstract string GenerateRecreateScript(RecreateDelta delta);

    protected abstract string GenerateAddConstraintScript(AddConstraintDelta delta);

    /// <summary>
    /// Emits an extension version update. Extensions are Postgres-only, so the base throws;
    /// only the Postgres generator overrides this. A provider that never produces an
    /// <see cref="AlterExtensionVersionDelta"/> never reaches it.
    /// </summary>
    protected virtual string GenerateAlterExtensionScript(AlterExtensionVersionDelta delta)
        => throw new NotImplementedException(
            $"This provider does not support {nameof(AlterExtensionVersionDelta)}.");
}
