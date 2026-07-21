namespace Squill.Dacpac.Tests;

/// <summary>
/// Covers assembly of the full deployment script from its three phases (issue #67).
/// </summary>
public class DeploymentScriptsTests
{
    [Fact]
    public void Compose_OrdersPreSchemaPost()
    {
        var result = DeploymentScripts.Compose("SELECT 'pre';", "CREATE TABLE foo (id int);", "SELECT 'post';");

        var pre = result.IndexOf("SELECT 'pre';", StringComparison.Ordinal);
        var schema = result.IndexOf("CREATE TABLE foo", StringComparison.Ordinal);
        var post = result.IndexOf("SELECT 'post';", StringComparison.Ordinal);

        Assert.True(pre >= 0 && schema >= 0 && post >= 0, "All three phases should be present.");
        Assert.True(pre < schema, "Pre-deployment must come before the schema changes.");
        Assert.True(schema < post, "Post-deployment must come after the schema changes.");
    }

    [Fact]
    public void Compose_WithNoScripts_ReturnsSchemaScriptOnly()
    {
        var result = DeploymentScripts.Compose(string.Empty, "CREATE TABLE foo (id int);", string.Empty);

        Assert.Contains("CREATE TABLE foo (id int);", result);
        Assert.DoesNotContain("Pre-Deployment", result);
        Assert.DoesNotContain("Post-Deployment", result);
    }

    [Fact]
    public void Compose_WithEmptySchemaScript_StillEmitsDeployScripts()
    {
        // A deploy that changes no schema must still emit its seed scripts.
        var result = DeploymentScripts.Compose("SELECT 'pre';", string.Empty, "SELECT 'post';");

        Assert.Contains("SELECT 'pre';", result);
        Assert.Contains("SELECT 'post';", result);
    }

    [Fact]
    public void Compose_WithAllEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, DeploymentScripts.Compose(string.Empty, string.Empty, string.Empty));
    }

    [Fact]
    public void Compose_LabelsEachSection()
    {
        var result = DeploymentScripts.Compose("SELECT 1;", "CREATE TABLE foo (id int);", "SELECT 2;");

        Assert.Contains("Pre-Deployment Script", result);
        Assert.Contains("Schema Changes", result);
        Assert.Contains("Post-Deployment Script", result);
    }
}
