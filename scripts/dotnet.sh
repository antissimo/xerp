#!/usr/bin/env bash
# Runs `dotnet <args>` inside the .NET SDK container, because the host has no .NET SDK.
#
#   scripts/dotnet.sh build
#   scripts/dotnet.sh test
#
# What it takes to make integration tests (Testcontainers) work from inside the container:
#   --network host                          ports published by test containers are reachable on localhost
#   -v /var/run/docker.sock                 Testcontainers talks to the host Docker daemon
#   --group-add <gid of the socket>         the container runs as the host user, not root, so it needs
#                                           the socket's group to be allowed to use it
#   TESTCONTAINERS_HOST_OVERRIDE=localhost  otherwise Testcontainers, seeing it runs in a container,
#                                           would try the bridge gateway address
# The container runs with the host uid/gid so bin/ and obj/ are never root-owned. HOME (NuGet cache,
# dotnet tool state) is a host directory outside the repository, kept between runs.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
image="${XERP_DOTNET_IMAGE:-mcr.microsoft.com/dotnet/sdk:10.0}"
cache="${XERP_DOTNET_HOME:-${XDG_CACHE_HOME:-$HOME/.cache}/xerp-dotnet}"
sock="${XERP_DOCKER_SOCK:-/var/run/docker.sock}"
mkdir -p "$cache"

args=(--rm --network host
  --user "$(id -u):$(id -g)"
  -e HOME=/dotnet-home
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1
  -e DOTNET_NOLOGO=1
  -e DOTNET_CLI_UI_LANGUAGE=en
  -v "$cache":/dotnet-home
  -v "$repo":"$repo"
  -w "$repo")

if [ -S "$sock" ]; then
  args+=(-v "$sock":/var/run/docker.sock
    --group-add "$(stat -c %g "$sock")"
    -e TESTCONTAINERS_HOST_OVERRIDE=localhost)
fi
if [ -t 0 ] && [ -t 1 ]; then args+=(-it); fi

exec docker run "${args[@]}" "$image" dotnet "$@"
