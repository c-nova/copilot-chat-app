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
SERVER_LAUNCH_AGENT_LABEL="com.copilotchatapp.server"
SERVER_LAUNCH_AGENT_RESTARTED=false
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
# npm 11 can rewrite platform metadata for optional native packages even though `npm ci` is
# documented as frozen (observed with @github/copilot's Darwin/Linux/Musl packages). Preserve the
# tracked lockfile around the install: npm still consumes it for an exact install, but generated
# platform churn never leaks into the working tree.
LOCKFILE_BACKUP="$(mktemp)"
cp package-lock.json "$LOCKFILE_BACKUP"
if npm ci; then
  :
else
  exit_code=$?
  cp "$LOCKFILE_BACKUP" package-lock.json
  rm -f "$LOCKFILE_BACKUP"
  exit "$exit_code"
fi
if ! cmp -s "$LOCKFILE_BACKUP" package-lock.json; then
  cp "$LOCKFILE_BACKUP" package-lock.json
  echo "Restored package-lock.json after npm platform-metadata churn."
fi
rm -f "$LOCKFILE_BACKUP"
if [ ! -f .env ]; then
  cp .env.example .env
  echo "Created server/.env from .env.example - edit AUTH_TOKEN (and BROWSE_ROOTS/WORK_DIR if needed) before starting the server for real!"
fi

step "Building server (TypeScript -> server/dist)..."
npm run build

# An installed LaunchAgent keeps the already-loaded JavaScript in memory even after dist changes.
# Restart it after every successful server build so newly added protocol handlers take effect
# immediately instead of leaving clients talking to a stale process until the next login/reboot.
if launchctl print "gui/$(id -u)/$SERVER_LAUNCH_AGENT_LABEL" >/dev/null 2>&1; then
  step "Restarting installed server LaunchAgent..."
  launchctl kickstart -k "gui/$(id -u)/$SERVER_LAUNCH_AGENT_LABEL"
  SERVER_LAUNCH_AGENT_RESTARTED=true
fi

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
  if [ "$SERVER_LAUNCH_AGENT_RESTARTED" = true ]; then
    step "Using the installed server LaunchAgent."
  else
    step "Starting server in the background..."
    cd "$ROOT/server"
    npm start &
    SERVER_PID=$!
    trap 'echo "==> Stopping server (pid $SERVER_PID)..."; kill "$SERVER_PID" 2>/dev/null || true' EXIT
  fi

  step "Running Mac Catalyst client..."
  cd "$ROOT/client/CopilotChatApp"
  dotnet build -t:Run -f net10.0-maccatalyst
else
  echo "Run with --run to also start the server and launch the client, or --package to build an"
  echo "installer .pkg instead, e.g.:"
  echo "  ./scripts/build-mac.sh --run"
  echo "  ./scripts/build-mac.sh --package"
fi
