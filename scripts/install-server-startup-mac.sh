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

# Resolve node the same way your normal interactive Terminal would, rather than trusting whatever
# shell happens to be running this script. If multiple node installs exist on PATH (e.g. an old
# /usr/local/bin/node left over from a previous install, alongside a newer Homebrew one), a plain
# `command -v node` here can resolve to a DIFFERENT one than what `node --version` gives you day to
# day, depending on which shell/PATH order launched this script (observed: bash resolving an old
# Node 18 here while the default interactive zsh resolves a newer Homebrew Node 23) - and
# launchd would then run the server on that wrong, possibly-too-old node forever. Forcing
# resolution through an interactive login zsh (macOS's default shell, so it loads ~/.zshrc etc.)
# matches what you'd get by just typing `node` in Terminal.
NODE_PATH="$(zsh -ilc 'command -v node' 2>/dev/null | tail -1)"
if [ -z "$NODE_PATH" ]; then
  NODE_PATH="$(command -v node)"
fi
if [ -z "$NODE_PATH" ]; then
  echo "node not found on PATH." >&2
  exit 1
fi
step "Using node: $NODE_PATH ($("$NODE_PATH" --version 2>&1))"
if [ "$(command -v node 2>/dev/null)" != "$NODE_PATH" ] && command -v node >/dev/null 2>&1; then
  echo "  (Note: this differs from what 'command -v node' resolves to in the current shell -" >&2
  echo "  $(command -v node). Multiple node installs detected; using the interactive-zsh one above.)" >&2
fi

# LaunchAgents run with a minimal PATH (just /usr/bin:/bin:/usr/sbin:/sbin) - NOT your shell's full
# PATH - so the server's own child process spawns (running the `copilot` CLI itself) would fail
# with "spawn copilot ENOENT" even though `copilot` works fine when you run the server manually
# from a terminal. Explicitly set PATH to node's own directory (where `copilot` almost certainly
# also lives, e.g. Homebrew's /opt/homebrew/bin) plus the standard system dirs.
NODE_DIR="$(dirname "$NODE_PATH")"
LAUNCHD_PATH="$NODE_DIR:/usr/local/bin:/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin"

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
    <key>EnvironmentVariables</key>
    <dict>
        <key>PATH</key>
        <string>$LAUNCHD_PATH</string>
    </dict>
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
