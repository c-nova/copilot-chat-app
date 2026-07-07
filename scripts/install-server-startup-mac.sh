#!/usr/bin/env bash
#
# Registers the copilot-chat-app server as a per-user macOS LaunchAgent, so it starts
# automatically at login and gets restarted by launchd if it ever crashes/exits - "server-like"
# behavior without needing a third-party process manager.
#
# This only affects the CURRENT user's login session (~/Library/LaunchAgents), not a system-wide
# daemon - matching the "runs as long as you're logged in" model most dev machines want, and not
# requiring sudo/admin rights to install.
#
# Usage:
#   ./scripts/install-server-startup-mac.sh              # install + start now
#   ./scripts/install-server-startup-mac.sh --uninstall   # stop + remove
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SERVER_DIR="$ROOT/server"
LABEL="com.copilotchatapp.server"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
LOG="$HOME/Library/Logs/CopilotChatServer.log"

UNINSTALL=false
for arg in "$@"; do
  case "$arg" in
    --uninstall) UNINSTALL=true ;;
    *)
      echo "Unknown argument: $arg" >&2
      echo "Usage: $0 [--uninstall]" >&2
      exit 1
      ;;
  esac
done

step() { echo "==> $1"; }

if [ "$UNINSTALL" = true ]; then
  step "Stopping and removing the LaunchAgent ($LABEL)..."
  launchctl unload -w "$PLIST" 2>/dev/null || true
  rm -f "$PLIST"
  echo "Done. (Log file left at $LOG if you want to keep it; delete it manually if not.)"
  exit 0
fi

if [ ! -f "$SERVER_DIR/dist/index.js" ]; then
  echo "server/dist/index.js not found - build the server first, e.g.:" >&2
  echo "  ./scripts/build-mac.sh" >&2
  exit 1
fi
if [ ! -f "$SERVER_DIR/.env" ]; then
  echo "server/.env not found - set it up first (copy server/.env.example, set AUTH_TOKEN), e.g.:" >&2
  echo "  ./scripts/build-mac.sh" >&2
  exit 1
fi

NODE_PATH="$(command -v node)"
if [ -z "$NODE_PATH" ]; then
  echo "node not found on PATH." >&2
  exit 1
fi

step "Writing $PLIST..."
mkdir -p "$HOME/Library/LaunchAgents" "$HOME/Library/Logs"
cat > "$PLIST" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>$LABEL</string>
    <key>ProgramArguments</key>
    <array>
        <string>$NODE_PATH</string>
        <string>$SERVER_DIR/dist/index.js</string>
    </array>
    <key>WorkingDirectory</key>
    <string>$SERVER_DIR</string>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>StandardOutPath</key>
    <string>$LOG</string>
    <key>StandardErrorPath</key>
    <string>$LOG</string>
</dict>
</plist>
EOF

step "Loading it (starts the server now, and at every future login)..."
launchctl unload -w "$PLIST" 2>/dev/null || true
launchctl load -w "$PLIST"

echo ""
echo "Installed and started. Useful commands:"
echo "  launchctl list | grep $LABEL         # check it's running (a PID means it's up)"
echo "  tail -f \"$LOG\"                       # follow server logs"
echo "  ./scripts/install-server-startup-mac.sh --uninstall   # stop + remove"
echo ""
echo "Note: with KeepAlive enabled, launchd restarts the server if it exits for any reason -"
echo "to actually stop it (not just have it come right back), use --uninstall rather than"
echo "'launchctl stop $LABEL'."
