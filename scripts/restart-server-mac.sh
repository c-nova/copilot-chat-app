#!/usr/bin/env bash
# Restarts the installed per-user copilot-chat-app LaunchAgent on macOS.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SERVER_DIR="$ROOT/server"
LABEL="com.copilotchatapp.server"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
LOG="$HOME/Library/Logs/CopilotChatServer.log"
DOMAIN="gui/$(id -u)"
PORT_LINE="$(grep -E '^PORT=[0-9]+$' "$SERVER_DIR/.env" 2>/dev/null | head -1 || true)"
PORT="${PORT_LINE#PORT=}"
PORT="${PORT:-5219}"

if [ ! -f "$SERVER_DIR/dist/index.js" ]; then
  echo "server/dist/index.js not found. Build the server first with ./scripts/build-mac.sh." >&2
  exit 1
fi
if [ ! -f "$SERVER_DIR/.env" ]; then
  echo "server/.env not found. Configure the server before restarting it." >&2
  exit 1
fi
if [ ! -f "$PLIST" ]; then
  echo "Server auto-start is not installed. Run ./scripts/install-server-startup-mac.sh first." >&2
  exit 1
fi

echo "==> Restarting Copilot chat server ($LABEL)..."
if launchctl print "$DOMAIN/$LABEL" >/dev/null 2>&1; then
  if ! launchctl kickstart -k "$DOMAIN/$LABEL"; then
    echo "kickstart failed; reloading the LaunchAgent instead..." >&2
    launchctl unload -w "$PLIST" 2>/dev/null || true
    launchctl load -w "$PLIST"
  fi
else
  launchctl unload -w "$PLIST" 2>/dev/null || true
  launchctl load -w "$PLIST"
fi

for _attempt in {1..100}; do
  if nc -z 127.0.0.1 "$PORT" >/dev/null 2>&1; then
    echo "Server is listening on port $PORT."
    echo "Restarted LaunchAgent '$LABEL'."
    echo "Log: $LOG"
    exit 0
  fi
  sleep 0.1
done

echo "Server did not start listening on port $PORT within 10 seconds. Check $LOG." >&2
exit 1