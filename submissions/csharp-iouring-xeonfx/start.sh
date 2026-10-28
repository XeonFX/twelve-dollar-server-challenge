#!/usr/bin/env bash
# Runs the server in the foreground; HOST, PORT, SQLITE_PATH and JWT_SECRET come from the environment.
set -euo pipefail
exec "$(dirname "$0")/bin/app/server"
