# Squill CLI

A declarative, cross-platform, database-independent SQL deployment tool inspired by
SSDT/DACPAC, targeting PostgreSQL and MariaDB/MySQL.

## Install

```sh
dotnet tool install --global Squill
```

This puts a `squill` command on your PATH. To update or uninstall later:

```sh
dotnet tool update --global Squill
dotnet tool uninstall --global Squill
```

## Usage

`squill` deploys a DACPAC (built from your declarative `.sql` files) against a target
database. The provider — PostgreSQL or MariaDB/MySQL — is chosen from the provider name
recorded in the DACPAC at build time, so the same commands target either.

```sh
# Apply a DACPAC to a target database.
squill deploy MyDatabase.dacpac --connection-string "<ADO.NET connection string>"

# Generate the deployment script without running it.
squill script MyDatabase.dacpac --connection-string "<ADO.NET connection string>" --output deploy.sql
```

Run `squill deploy --help` or `squill script --help` for the full set of options.
