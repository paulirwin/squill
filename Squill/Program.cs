using System.CommandLine;
using System.Reflection;
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

    var deployCommand = new Command(
        "deploy", "Deploy a DACPAC to a target PostgreSQL database.")
    {
        dacpacArgument,
        connectionStringOption,
        targetDatabaseOption,
        dryRunOption
    };

    deployCommand.SetAction(async (parseResult, cancellationToken) =>
    {
        var dacpac = parseResult.GetValue(dacpacArgument)!;
        var connectionString = parseResult.GetValue(connectionStringOption)!;
        var targetDatabase = parseResult.GetValue(targetDatabaseOption);
        var dryRun = parseResult.GetValue(dryRunOption);

        if (!dacpac.Exists)
        {
            Console.Error.WriteLine($"DACPAC file not found: {dacpac.FullName}");
            return 1;
        }

        try
        {
            var result = await DacpacDeployer.DeployFromFileAsync(
                dacpac.FullName, connectionString, targetDatabase, dryRun, cancellationToken);

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
