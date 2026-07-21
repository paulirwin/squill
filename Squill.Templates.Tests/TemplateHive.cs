using System.Diagnostics;
using Task = System.Threading.Tasks.Task;

namespace Squill.Templates.Tests;

/// <summary>
/// Installs the Squill <c>dotnet new</c> template into an isolated, temporary template hive
/// (via <c>--debug:custom-hive</c>) so tests can run <c>dotnet new squill</c> without touching
/// the machine-global template store. Dispose removes the hive and any scaffolded output.
/// </summary>
public sealed class TemplateHive : IDisposable
{
    private readonly string _root;

    public string HivePath { get; }

    private TemplateHive(string root, string hivePath)
    {
        _root = root;
        HivePath = hivePath;
    }

    /// <summary>Install the template from its source folder into a fresh isolated hive.</summary>
    public static async Task<TemplateHive> InstallAsync(string repoRoot)
    {
        var root = Directory.CreateTempSubdirectory("squill-tmpl").FullName;
        var hivePath = Path.Combine(root, "hive");
        Directory.CreateDirectory(hivePath);

        var templateDir = Path.Combine(repoRoot, "Squill.Templates", "templates", "squillproj");

        var (exit, output) = await RunDotnet(root,
            "new", "install", templateDir, "--debug:custom-hive", hivePath);
        if (exit != 0)
        {
            Directory.Delete(root, recursive: true);
            throw new InvalidOperationException($"Failed to install Squill template. Output:\n{output}");
        }

        return new TemplateHive(root, hivePath);
    }

    /// <summary>A fresh scaffold-output directory under this hive's temp root.</summary>
    public string NewTempDir(string label)
    {
        var dir = Path.Combine(_root, $"out-{label}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Run <c>dotnet new</c> against this isolated hive. The <c>--debug:custom-hive</c> flag
    /// must be repeated on every <c>dotnet new</c> invocation, not just the install, or the
    /// template resolves from the machine-global store instead.
    /// </summary>
    public Task<(int ExitCode, string Output)> RunNewAsync(string workingDir, params string[] args)
    {
        var withHive = new List<string>(args) { "--debug:custom-hive", HivePath };
        return RunDotnet(workingDir, withHive.ToArray());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone; nothing to clean up.
        }
    }

    private static async Task<(int ExitCode, string Output)> RunDotnet(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, stdout + stderr);
    }
}
