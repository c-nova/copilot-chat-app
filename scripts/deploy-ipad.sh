#!/usr/bin/env bash
#
# Builds the CopilotChatApp iOS client and deploys it to a physical iPad/iPhone.
#
# Defaults to a Release ("prod") build, which is optimized and lighter on battery
# than the Debug build. Use --debug to build the Debug configuration instead.
#
# Requirements:
#   - Xcode installed, iPad/iPhone connected via USB (Wi-Fi deploy tends to hang).
#   - Developer Mode enabled on the device
#     (Settings -> Privacy & Security -> Developer Mode).
#   - A valid provisioning profile for com.companyname.copilotchatapp that includes
#     the target device's UDID, installed in ~/Library/MobileDevice/Provisioning Profiles/.
#     Free "Personal Team" profiles expire after ~7 days; regenerate via the Xcode
#     dummy project when it lapses (see repo memory: ios_device_deploy).
#
# Usage:
#   ./scripts/deploy-ipad.sh                 # Release build only
#   ./scripts/deploy-ipad.sh --run           # Release build, then install & launch on device
#   ./scripts/deploy-ipad.sh --iphone --run  # Release build, then install & launch on iPhone
#   ./scripts/deploy-ipad.sh --ipad --run    # Release build, then install & launch on iPad
#   ./scripts/deploy-ipad.sh --debug --run   # Debug build, then install & launch
#   ./scripts/deploy-ipad.sh --clean --run   # wipe obj/bin first, then build & launch
#
# Override the device or signing identity without editing this file:
#   IOS_DEVICE_UDID=000081XX-XXXXXXXXXXXXXXXX ./scripts/deploy-ipad.sh --run
#   IOS_CODESIGN_KEY="Apple Development: you@example.com (XXXXXXXXXX)" ./scripts/deploy-ipad.sh --run
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT_DIR="$ROOT/client/CopilotChatApp"

# --- Configurable via environment -----------------------------------------------
DEVICE_UDID="${IOS_DEVICE_UDID:-}"
CODESIGN_KEY="${IOS_CODESIGN_KEY:-}"
DEVICE_KIND="iPad"

# --- Argument parsing ------------------------------------------------------------
CONFIG="Release"
CLEAN=false
RUN=false
for arg in "$@"; do
  case "$arg" in
    --debug)   CONFIG="Debug" ;;
    --release) CONFIG="Release" ;;
    --clean)   CLEAN=true ;;
    --run|--deploy) RUN=true ;;
    --ipad)    DEVICE_KIND="iPad" ;;
    --iphone)  DEVICE_KIND="iPhone" ;;
    -h|--help)
      grep '^#' "$0" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    *)
      echo "Unknown argument: $arg" >&2
      echo "Usage: $0 [--debug|--release] [--clean] [--run] [--ipad|--iphone]" >&2
      exit 1
      ;;
  esac
done

step() { echo "==> $1"; }

if [ -z "$DEVICE_UDID" ]; then
  DEVICE_UDID="$(xcrun xctrace list devices 2>/dev/null \
    | grep "$DEVICE_KIND" \
    | grep -v Simulator \
    | sed -nE 's/^.*\(([0-9A-F-]{20,})\)$/\1/p' \
    | head -1)"
fi

if [ "$RUN" = true ] && [ -z "$DEVICE_UDID" ]; then
  echo "No connected $DEVICE_KIND found. Connect it via USB or set IOS_DEVICE_UDID." >&2
  exit 1
fi

SIGNING_ARGS=(-p:CodesignProvision="Automatic")
if [ -n "$CODESIGN_KEY" ]; then
  SIGNING_ARGS+=(-p:CodesignKey="$CODESIGN_KEY")
fi

FRAMEWORK="net10.0-ios"
RID="ios-arm64"
OUT_DIR="$PROJECT_DIR/bin/$CONFIG/$FRAMEWORK/$RID/CopilotChatApp.app"

cd "$PROJECT_DIR"

if [ "$CLEAN" = true ]; then
  step "Cleaning obj/$CONFIG/$FRAMEWORK and bin/$CONFIG/$FRAMEWORK..."
  rm -rf "obj/$CONFIG/$FRAMEWORK" "bin/$CONFIG/$FRAMEWORK"
fi

step "Building iOS client ($CONFIG, $RID)..."
dotnet build "$PROJECT_DIR/CopilotChatApp.csproj" \
  -c "$CONFIG" \
  -f "$FRAMEWORK" \
  -p:RuntimeIdentifier="$RID" \
  "${SIGNING_ARGS[@]}"

echo ""
step "Build complete: $OUT_DIR"

if [ "$RUN" != true ]; then
  echo ""
  echo "Add --run to install and launch on the device, e.g.:"
  echo "  ./scripts/deploy-ipad.sh --run"
  exit 0
fi

echo ""
step "Installing & launching on device $DEVICE_UDID..."
echo "    (This stays in the foreground streaming console output until the app exits."
echo "     Press Ctrl+C to detach - the app keeps running on the device. An MSB3073"
echo "     error after Ctrl+C is expected and harmless.)"
echo ""

# Note: pass the UDID WITHOUT any ':v2:identifier=' prefix - that form hangs on
# 'Please connect the device' (see repo memory: ios_device_deploy).
dotnet build "$PROJECT_DIR/CopilotChatApp.csproj" \
  -t:Run \
  -c "$CONFIG" \
  -f "$FRAMEWORK" \
  -p:RuntimeIdentifier="$RID" \
  "${SIGNING_ARGS[@]}" \
  -p:_DeviceName="$DEVICE_UDID"

echo ""
echo "If the app was freshly re-signed, the first launch may fail with CoreDeviceError"
echo "10002 until you trust the developer on the device:"
echo "  Settings -> General -> VPN & Device Management -> tap the developer app -> Trust."
