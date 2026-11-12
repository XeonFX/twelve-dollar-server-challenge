#!/usr/bin/env bash
# Runs the server in the foreground; HOST, PORT, SQLITE_PATH and JWT_SECRET come from the environment.
set -euo pipefail
app="$(dirname "$0")/bin/app"
# only a JIT build (build.sh with RUNTIME=jit) has this file
if [ -f "$app/dotnet-root" ]; then export DOTNET_ROOT="$(cat "$app/dotnet-root")"; fi
exec "$app/server"
