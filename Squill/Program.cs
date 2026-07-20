using System.CommandLine;
using System.Reflection;
using Squill.Core;
using Squill.Provider.Postgres;

var version = typeof(Program).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

var rootCommand = new RootCommand("Squill — declarative, cross-platform SQL deployment.");

// `squill` with no subcommand prints the version, preserving the previous behavior.
rootCommand.SetAction(_ =>
{
    Console.WriteLine($"Squill v{version}");
    return 0;
});

rootCommand.Subcommands.Add(BuildDeployCommand());

return rootCommand.Parse(args).Invoke();

static Command BuildDeployCommand()
{
    var dacpacArgument = new Argument<FileInfo>("dacpac")
    {
        Description = "Path to the .dacpac file to deploy."
    };

    var connectionStringOption = new Option<string>("--connection-string", "-c")
    {
        Description = "Npgsql connection string for the target PostgreSQL server.",
        Required = true
    };

    var targetDatabaseOption = new Option<string?>("--target-database", "-d")
    {
        Description =
            "Name of the database to deploy into. Defaults to the connection string's Database."
    };

    var dryRunOption = new Option<bool>("--dry-run")
    {
        Description = "Print the SQL that would be run without executing it against the database."
    };

    // Table rebuilds are allowed by default (like SSDT). Passing this flag disallows them,
    // so a change that can only be deployed by rebuilding a table fails instead — useful
    // for guarding large, transactional tables against a costly, unintended rebuild.
    var disallowTableRebuildOption = new Option<bool>("--disallow-table-rebuild")
    {
        Description =
            "Fail rather than rebuild a table when a change can't be applied with an "
            + "in-place ALTER. Table rebuilds are allowed by default."
    };

    // Dropping standalone objects (tables, indexes, extensions) not in the DACPAC is off
    // by default, matching SSDT's DropObjectsNotInSource — dropping objects is destructive.
    var dropObjectsNotInSourceOption = new Option<bool>("--drop-objects-not-in-source")
    {
        Description =
            "Drop standalone objects (tables, indexes, extensions) present in the target "
            + "database but not in the DACPAC. Off by default."
    };

    // Deployment is blocked on possible data loss by default, matching SSDT's
    // BlockOnPossibleDataLoss. Passing this flag allows data-losing changes to proceed.
    var allowDataLossOption = new Option<bool>("--allow-data-loss")
    {
        Description =
            "Allow changes that may cause data loss (dropping a table or column, or "
            + "rebuilding a table). By default the deploy is blocked on possible data loss."
    };

    var deployCommand = new Command(
        "deploy", "Deploy a DACPAC to a target PostgreSQL database.")
    {
        dacpacArgument,
        connectionStringOption,
        targetDatabaseOption,
        dryRunOption,
        disallowTableRebuildOption,
        dropObjectsNotInSourceOption,
        allowDataLossOption
    };

    deployCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var dacpac = parseResult.GetValue(dacpacArgument)!;
        var connectionString = parseResult.GetValue(connectionStringOption)!;
        var targetDatabase = parseResult.GetValue(targetDatabaseOption);
        var dryRun = parseResult.GetValue(dryRunOption);

        var options = new DeployOptions
        {
            AllowTableRebuild = !parseResult.GetValue(disallowTableRebuildOption),
            DropObjectsNotInSource = parseResult.GetValue(dropObjectsNotInSourceOption),
            BlockOnPossibleDataLoss = !parseResult.GetValue(allowDataLossOption),
        };

        if (!dacpac.Exists)
        {
            Console.Error.WriteLine($"DACPAC file not found: {dacpac.FullName}");
            return 1;
        }

        // Report each deploy step to the console as it happens, so the user sees what's
        // being done (like sqlpackage) rather than a single terminal message. Suppressed
        // on a dry run, where the full script is printed instead. A synchronous sink is
        // used rather than System.Progress<T> so lines print in order on the console
        // (Progress<T> has no SynchronizationContext to marshal to in a console app and
        // would report on thread-pool threads, racing the final message).
        IProgress<string>? progress = dryRun
            ? null
            : new SynchronousProgress(Console.WriteLine);

        try
        {
            var result = await DacpacDeployer.DeployFromFileAsync(
                dacpac.FullName, connectionString, targetDatabase, dryRun, progress,
                options, cancellationToken);

            if (string.IsNullOrEmpty(result.Script))
            {
                Console.WriteLine("Target database already matches the DACPAC; nothing to deploy.");
                return 0;
            }

            if (dryRun)
            {
                Console.WriteLine("-- Dry run: the following script would be executed:");
                Console.WriteLine(result.Script);
            }
            else
            {
                Console.WriteLine("Deployment complete.");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Deployment failed: {ex.Message}");
            return 1;
        }
    });

    return deployCommand;
}

// An IProgress<string> that invokes its callback synchronously on the reporting thread,
// so progress lines print to the console in the order they are reported.
file sealed class SynchronousProgress(Action<string> report) : IProgress<string>
{
    public void Report(string value) => report(value);
}
