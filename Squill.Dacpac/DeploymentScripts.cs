using System.Text;

namespace Squill.Dacpac;

/// <summary>
/// Assembles the full deployment script from a DACPAC's pre-deployment script, the
/// SQL generated from the schema diff, and its post-deployment script (issue #67).
/// Shared by the providers so <c>squill script</c> emits the same ordering that
/// <c>squill deploy</c> executes, whichever database engine is targeted.
/// </summary>
public static class DeploymentScripts
{
    /// <summary>
    /// Concatenates the three phases in execution order, separated by banner comments
    /// so the emitted script reads as three distinct sections. Empty phases are omitted;
    /// when all three are empty the result is an empty string.
    /// </summary>
    public static string Compose(string preDeployScript, string schemaScript, string postDeployScript)
    {
        var builder = new StringBuilder();

        AppendSection(builder, "Pre-Deployment Script", preDeployScript);
        AppendSection(builder, "Schema Changes", schemaScript);
        AppendSection(builder, "Post-Deployment Script", postDeployScript);

        return builder.ToString();
    }

    private static void AppendSection(StringBuilder builder, string title, string script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        builder.Append("/* ---- ").Append(title).AppendLine(" ---- */");
        builder.AppendLine(script.TrimEnd());
    }
}
