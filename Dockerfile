# Squill CLI base image.
#
# Multi-stage build: compile with the .NET SDK, then ship only the published
# output on top of the smaller ASP.NET-free runtime image. The resulting image
# has `squill` on the PATH, so downstream deployment-job images can simply do:
#
#   FROM ghcr.io/paulirwin/squill:latest
#   COPY path/to/my_db.dacpac /app/
#   CMD ["squill", "deploy", "/app/my_db.dacpac", "--connection-string", "$CONN_STR"]
#
# Pin the major .NET version to match the projects' TargetFramework (net10.0).

ARG DOTNET_VERSION=10.0

# ---- build stage -------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# Restore first, against just the project/solution files, so the restore layer is
# cached and only re-runs when dependencies change rather than on every source edit.
COPY Squill.slnx ./
COPY Squill/*.csproj Squill/
COPY Squill.Core/*.csproj Squill.Core/
COPY Squill.Dacpac/*.csproj Squill.Dacpac/
COPY Squill.Provider.Postgres/*.csproj Squill.Provider.Postgres/
COPY Squill.Provider.MariaDb/*.csproj Squill.Provider.MariaDb/
COPY Squill.PostgresParser/*.csproj Squill.PostgresParser/
COPY Squill.MariaDbParser/*.csproj Squill.MariaDbParser/
RUN dotnet restore Squill/Squill.csproj

# Copy the rest of the sources and publish the CLI. Framework-dependent (the
# default) keeps the image small since the runtime is already in the base image.
COPY . .
RUN dotnet publish Squill/Squill.csproj -c Release -o /app --no-restore

# ---- runtime stage -----------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime:${DOTNET_VERSION} AS runtime
WORKDIR /app

COPY --from=build /app ./

# Expose the CLI as `squill` on the PATH. The published host executable is named
# after the project (Squill); symlink a lowercase `squill` for the documented UX.
RUN ln -s /app/Squill /usr/local/bin/squill

# Default to printing help so `docker run <image>` with no args is informative;
# deployment images override this with their own `squill deploy ...` CMD.
ENTRYPOINT ["squill"]
CMD ["--help"]
