#!/bin/bash
# Package an existing signed app without changing the bundle used by the ZIP updater.
set -euo pipefail

APP="${1:?Usage: build-dmg.sh /path/to/AgentGauge.app /path/to/output.dmg}"
OUTPUT="${2:?An output DMG path is required}"
codesign --verify --strict "$APP"
WORK="$(mktemp -d)"
MOUNT="$WORK/mounted"
cleanup() {
    if mount | grep -Fq " on $MOUNT "; then
        hdiutil detach "$MOUNT" -quiet || true
    fi
    rm -rf "$WORK"
}
trap cleanup EXIT
mkdir -p "$WORK/staging" "$(dirname "$OUTPUT")"
ditto "$APP" "$WORK/staging/AgentGauge.app"
ln -s /Applications "$WORK/staging/Applications"
hdiutil create -volname AgentGauge -srcfolder "$WORK/staging" -format UDZO -ov "$OUTPUT"
hdiutil verify "$OUTPUT"
hdiutil attach "$OUTPUT" -mountpoint "$MOUNT" -readonly -nobrowse -quiet
codesign --verify --strict "$MOUNT/AgentGauge.app"
diff -qr "$APP" "$MOUNT/AgentGauge.app"
test "$(readlink "$MOUNT/Applications")" = /Applications
hdiutil detach "$MOUNT" -quiet
echo "Verified $OUTPUT"
