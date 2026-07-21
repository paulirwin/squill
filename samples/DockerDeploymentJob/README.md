# Docker deployment-job sample

Shows how to build a database **deployment-job image** on top of the published
Squill base image ([`ghcr.io/paulirwin/squill`](https://github.com/paulirwin/squill/pkgs/container/squill)).
The image bundles a built `.dacpac` and runs `squill deploy` on start, so
deploying your schema in CI/CD becomes "run this container against the target
database."

See [`Dockerfile`](Dockerfile) — it's a three-line consumer image: `FROM` the
Squill base image, `COPY` in a `.dacpac`, and set a `squill deploy` command.

## Try it

The Dockerfile copies the DACPAC produced by the [PostgreSQL
sample](../PostgresSampleDatabase), so build that first:

```sh
# From the repo root — produces samples/PostgresSampleDatabase/bin/Debug/PostgresSampleDatabase.dacpac
dotnet build samples/PostgresSampleDatabase/PostgresSampleDatabase.squillproj

# Build the deployment-job image (build context = the sample project folder)
docker build -f samples/DockerDeploymentJob/Dockerfile \
  -t my-db-deploy samples/PostgresSampleDatabase

# Run it against your target database
docker run --rm \
  -e CONN_STR="Host=localhost;Username=postgres;Password=postgres;Database=mydb" \
  my-db-deploy
```

Add `--dry-run` to preview the SQL without executing it:

```sh
docker run --rm -e CONN_STR="..." my-db-deploy \
  squill deploy /app/my_db.dacpac --connection-string "$CONN_STR" --dry-run
```

(The trailing arguments override the image's default `CMD`.)
