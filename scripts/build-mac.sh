#!/usr/bin/env bash
#
# Builds the copilot-chat-app server and the Mac Catalyst client in one go.
#
# - Installs server dependencies and builds the TypeScript server (server/dist/*.js).
# - Creates server/.env from server/.env.example on first run (you still need to edit
#   AUTH_TOKEN yourself before starting the server for real).
# - Builds the Mac Catalyst (net10.0-maccatalyst) client. Requires Xcode to be installed.
#
# Usage:
#   ./scripts/build-mac.sh          # build only
#   ./scripts/build-mac.sh --run    # build, then start the server (background) and run the client
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUN=false
for arg in "$@"; do
  case "$arg" in
    --run) RUN=true ;;
    *)
      echo "Unknown argument: $arg" >&2
      echo "Usage: $0 [--run]" >&2
      exit 1
      ;;
  esac
done

step() { echo "==> $1"; }

step "Installing server dependencies..."
cd "$ROOT/server"
npm install
if [ ! -f .env ]; then
  cp .env.example .env
  echo "Created server/.env from .env.example - edit AUTH_TOKEN (and BROWSE_ROOTS/WORK_DIR if needed) before starting the server for real!"
fi

step "Building server (TypeScript -> server/dist)..."
npm run build

step "Building Mac Catalyst client (net10.0-maccatalyst)..."
cd "$ROOT/client/CopilotChatApp"
dotnet build -f net10.0-maccatalyst

echo ""
echo "Build complete."

if [ "$RUN" = true ]; then
  step "Starting server in the background..."
  cd "$ROOT/server"
  npm start &
  SERVER_PID=$!
  trap 'echo "==> Stopping server (pid $SERVER_PID)..."; kill "$SERVER_PID" 2>/dev/null || true' EXIT

  step "Running Mac Catalyst client..."
  cd "$ROOT/client/CopilotChatApp"
  dotnet build -t:Run -f net10.0-maccatalyst
else
  echo "Run with --run to also start the server and launch the client, e.g.:"
  echo "  ./scripts/build-mac.sh --run"
fi
