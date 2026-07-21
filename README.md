# Squill
A declarative, cross-platform, database-independent SQL deployment tool, inspired by and (soon) compatible with SQL Server Data Tools (SSDT).

## Motivation

> [!NOTE]
> This was written back in 2022 when I started this project. Since then, SSDT is now (mostly) available cross-platform. However, its lack of PostgreSQL support remains my primary motivation.

I believe that, with a few exceptions, declarative database deployments are superior to migrations. 
Declarative meaning: you express your SQL schema in terms of what the desired state should be (as `CREATE` statements, with no `ALTER` or `DROP` statements), and the deployment tool determines what changes need to be made to the target database to make it match your desired schema.
Instead of being strings in source code, each database object (i.e. tables, stored procedures, and so on) gets its own `.sql` file on disk, which can and should be committed to the git repo.
New tables become new files added to source control; dropped tables get removed from source control (but are still available in history).
New columns are just new lines added to the file; altered columns are changed lines.
This allows for seeing the full history of changes to a table in git history, including `git blame` support.
Merge conflicts are rare, and easy to resolve when they do happen, because you (usually) do not have to consider the migration history or migration versioning.
Two people can independently add non-conflicting columns to a table without having to worry about the order of migrations.
This also makes i.e. stored procedure code managed in source control just like your regular application code today.
You can do pre- and post-deployment scripts to handle things like preparing for a deployment or seeding data.

This has been a successful approach for me for a significant part of my career, via [SQL Server Data Tools (SSDT)](https://learn.microsoft.com/en-us/sql/ssdt/sql-server-data-tools?view=sql-server-ver16) and [DACPAC deployments](https://learn.microsoft.com/en-us/sql/relational-databases/data-tier-applications/data-tier-applications?view=sql-server-ver16) to SQL Server.
(I will use SSDT from here on to represent the SSDT Visual Studio and cross-platform tooling, DACPAC deployments, and SQL Server Database Projects interchangeably.)
I have introduced this Microsoft-supported approach to several clients over the years with great success and only minimal issues that require manual intervention.
My professional opinion is that this approach results in less manual intervention and other issues (like having to deal with merge conflicts of migration versions) than migrations.

Unfortunately, SSDT is primarily Windows-only and (in my opinion, unnecessarily) tightly coupled to SQL Server.
The Visual Studio Code and Azure Data Studio extensions are starting to help with the cross-platform aspect, but are still sub-par compared to the Windows-only Visual Studio experience.
I also run an M1-based Mac, and the new Arm64 builds of Visual Studio do not yet support SSDT either.
And finally, the SQL Server-only limitation prevents me from using this great technique on PostgreSQL or MySQL.

My goal for this project is first and foremost to introduce SSDT-like compilation (code-first) and DACPAC deployments to non-SQL-Server relational databases, primarily PostgreSQL and MySQL.
A requirement for this is that the tooling must work cross-platform on macOS, Linux, and Windows.
Some other secondary goals include compatibility with SQL Server (such that a DACPAC built with Squill is otherwise identical to one built by SSDT and could be deployed via Microsoft tooling, or vice versa), supporting a hybrid declarative/migration approach to allow for the few cases where migrations are a better choice, SQL generation via schema comparison (aka database-first), and IDE support.
Note that heavily SQL-Server-specific functionality like CLR assemblies might not be in scope anytime soon, as that functionality is well supported already by SSDT.

## Installation

The Squill CLI is distributed as a [.NET tool](https://learn.microsoft.com/en-us/dotnet/core/tools/global-tools). Install it globally to get a `squill` command on your PATH:

```sh
dotnet tool install --global Squill
```

Then use `squill deploy` and `squill script` to apply a DACPAC to a target database. See [the CLI readme](Squill/README.md) for usage.

## Architecture

There are two phases for taking a Squill project from source code to updating your target database: building the DACPAC, and deploying the DACPAC.

Building the DACPAC involves reading the declarative SQL, validating it, creating a schema "model" in-memory, and serializing this model to a DACPAC file.
Note that building the DACPAC is not affected by the state of the target database; the target might not even exist yet.
It is a complete representation of the desired state of the database schema.
This DACPAC file can then be passed to someone for manual deployment, or produced as a build artifact.
In a CI/CD setup, this step would be done in the "build" phase of your pipeline, as the output is environment-neutral (and supports single-build, multiple-deploy).

Deploying the DACPAC involves deserializing the model, validating it, extracting a model of the target database, comparing the models for changes, scripting the changes as SQL statements, and running the script.
In a CI/CD setup, this step would be done in the "release" phase of your pipeline, providing a different target database connection string for each environment.

Schema comparison therefore is as simple as diffing two models, where either the source or target could be a DACPAC file, a Squill project in your repo, or an actual database.

My first prototype of this creates a temporary database on a target Postgres server, runs the declarative scripts (i.e. `CREATE TABLE`) which implicitly validates them (as the scripts would fail to run against this temporary database if they are invalid), then extracts the model from this temporary database to perform the diff.
However, I realized that this approach has a few problems: it requires a database server (which could theoretically be embedded a la SQL Server LocalDb), the scripts must be run in order if there are dependencies like foreign keys, and circular foreign keys would be a challenge.
I plan on experimenting with a prototype of using ANTLR to parse the SQL text to determine what is in each file and create a dependency graph. 
This should remove the requirement for a temporary database/server, prevent having to order your scripts, and allow for circular foreign keys - at the expense of a significant increase in development effort.

To compare models, I am currently using a rudimentary Merkle tree approach with SHA256 hashes of each node's leaf properties or children. 
If the top-level model hashes match, we know the models are equivalent and do not need any changes.
Currently only top-level object diffing is supported (i.e. whether a table exists or not), but theoretically we can use this approach to walk the trees to see where exactly the hashes differ, to understand what needs to be updated.
Work will continue on improving the diffing algorithm which is currently very naïve and brute-force.
