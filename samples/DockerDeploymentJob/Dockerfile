# Sample deployment-job image.
#
# This is what a consumer builds on top of the Squill base image: FROM the
# published image (which already has the `squill` CLI + .NET runtime on the
# PATH), COPY in a built .dacpac, and set a `squill deploy` command. The result
# is a self-contained image whose whole job is to deploy one database.
#
# Build (from the repo root, after building the Postgres sample so its .dacpac
# exists — see this folder's README):
#
#   docker build -f samples/DockerDeploymentJob/Dockerfile \
#     -t my-db-deploy samples/PostgresSampleDatabase
#
# Run, pointing at your target database:
#
#   docker run --rm -e CONN_STR="Host=db;Username=postgres;Password=postgres;Database=mydb" \
#     my-db-deploy

FROM ghcr.io/paulirwin/squill:latest

WORKDIR /app

# Copy the built DACPAC into the image. The build context is the sample project
# folder, so this path is relative to samples/PostgresSampleDatabase.
COPY bin/Debug/PostgresSampleDatabase.dacpac /app/my_db.dacpac

# Deploy on container start. Uses the shell form of CMD (a bare string, not a
# JSON array) so Docker runs it via `/bin/sh -c` and expands the CONN_STR
# environment variable, which is supplied at run time.
CMD squill deploy /app/my_db.dacpac --connection-string "$CONN_STR"
