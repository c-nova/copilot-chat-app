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
#   ./scripts/build-mac.sh              # build only (Debug, run from source)
#   ./scripts/build-mac.sh --run        # build, then start the server (background) and run the client
#   ./scripts/build-mac.sh --package    # build a distributable .pkg installer (Release) instead
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUN=false
PACKAGE=false
for arg in "$@"; do
  case "$arg" in
    --run) RUN=true ;;
    --package) PACKAGE=true ;;
    *)
      echo "Unknown argument: $arg" >&2
      echo "Usage: $0 [--run|--package]" >&2
      exit 1
      ;;
  esac
done
if [ "$RUN" = true ] && [ "$PACKAGE" = true ]; then
  echo "--run and --package can't be used together - pick one." >&2
  exit 1
fi

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

if [ "$PACKAGE" = true ]; then
  step "Publishing Mac Catalyst client as an installer package (Release)..."
  cd "$ROOT/client/CopilotChatApp"
  dotnet publish -f net10.0-maccatalyst -c Release
  PKG=$(find "bin/Release/net10.0-maccatalyst/publish" -maxdepth 1 -name "*.pkg" -print -quit)
  echo ""
  echo "Package built: $ROOT/client/CopilotChatApp/$PKG"
  echo ""
  echo "This .pkg is unsigned. Double-clicking it on THIS Mac (the one that built it) should just"
  echo "work with no Gatekeeper warning, since a locally-built file has no com.apple.quarantine"
  echo "attribute. If you copy it to a different Mac and macOS blocks it, either use System Settings"
  echo "-> Privacy & Security -> \"Open Anyway\", or strip the flag first:"
  echo "  xattr -dr com.apple.quarantine \"$PKG\""
  exit 0
fi

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
  echo "Run with --run to also start the server and launch the client, or --package to build an"
  echo "installer .pkg instead, e.g.:"
  echo "  ./scripts/build-mac.sh --run"
  echo "  ./scripts/build-mac.sh --package"
fi
