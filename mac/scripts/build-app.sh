#!/bin/bash
# Builds AgentGauge.app, an updater ZIP and an installation DMG from the Swift package.
#
#   ./scripts/build-app.sh                 universal release build, ad-hoc signed
#   ARCHS=native ./scripts/build-app.sh    faster single-architecture build
#   SIGN_IDENTITY="Developer ID Application: …" ./scripts/build-app.sh
#
# The version comes from ../Gauge.csproj so both platforms ship one product version.
# An ad-hoc signature changes on every build, which makes macOS ask again for keychain
# access to Claude Code's credentials after an update; a stable signing identity avoids that.
set -euo pipefail

cd "$(dirname "$0")/.."
ROOT="$(pwd)"
BUILD_DIR="$ROOT/build"
APP="$BUILD_DIR/AgentGauge.app"

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/../Gauge.csproj" | head -n 1)"
VERSION="${VERSION:-0.0.0}"
BUILD_NUMBER="${BUILD_NUMBER:-$(git -C "$ROOT" rev-list --count HEAD 2>/dev/null || echo 1)}"

ARCH_FLAGS=(--arch arm64 --arch x86_64)
if [[ "${ARCHS:-universal}" == "native" ]]; then
    ARCH_FLAGS=()
fi

swift build -c release "${ARCH_FLAGS[@]+"${ARCH_FLAGS[@]}"}"
BIN_DIR="$(swift build -c release "${ARCH_FLAGS[@]+"${ARCH_FLAGS[@]}"}" --show-bin-path)"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN_DIR/AgentGauge" "$APP/Contents/MacOS/AgentGauge"

# SwiftPM records the deployment target as the linked SDK version (LC_BUILD_VERSION sdk
# 14.0). AppKit decides the window design from that number, so a binary claiming the
# macOS 14 SDK runs in compatibility mode on macOS 26+: no Liquid Glass panel, small
# window corners. Stamp the SDK the binary was actually built against.
MIN_OS="$(plutil -extract LSMinimumSystemVersion raw "$ROOT/Resources/Info.plist")"
SDK_VERSION="$(xcrun --sdk macosx --show-sdk-version)"
vtool -set-build-version macos "$MIN_OS" "$SDK_VERSION" -replace \
    -output "$APP/Contents/MacOS/AgentGauge" "$APP/Contents/MacOS/AgentGauge"
sed -e "s/__VERSION__/$VERSION/" -e "s/__BUILD__/$BUILD_NUMBER/" \
    "$ROOT/Resources/Info.plist" > "$APP/Contents/Info.plist"

# Empty lproj folders declare the supported languages, so macOS lists AgentGauge under
# System Settings › Language & Region › Applications and honours a per-app choice.
for language in en ko ja; do
    mkdir -p "$APP/Contents/Resources/$language.lproj"
    printf '/* AgentGauge */\n' > "$APP/Contents/Resources/$language.lproj/InfoPlist.strings"
done

# Menu bar icons: the Windows tray gauges (needle per usage level). NSImage reads the ICO's
# size frames directly; the app falls back to an SF Symbol if they are missing.
for icon in gauge_icon gauge_icon_70 gauge_icon_90; do
    if [[ -f "$ROOT/../Assets/$icon.ico" ]]; then
        cp "$ROOT/../Assets/$icon.ico" "$APP/Contents/Resources/$icon.ico"
    fi
done

# Tool logos for the menu bar ring (a chosen tool's logo inside its 5-hour/weekly arcs).
# Shared with the Windows tray; a missing logo draws the ring alone.
if [[ -d "$ROOT/../Assets/ToolLogos" ]]; then
    mkdir -p "$APP/Contents/Resources/ToolLogos"
    cp "$ROOT/../Assets/ToolLogos/"*.svg "$APP/Contents/Resources/ToolLogos/"
fi

# App icon from the Windows icon, best effort: a missing icon must not fail the build.
ICON_SOURCE="$ROOT/../Assets/gauge_appicon.ico"
if [[ -f "$ICON_SOURCE" ]] && command -v iconutil >/dev/null; then
    WORK="$(mktemp -d)"
    if sips -s format png "$ICON_SOURCE" --out "$WORK/source.png" >/dev/null 2>&1; then
        SIZE="$(sips -g pixelWidth "$WORK/source.png" | awk '/pixelWidth/ { print $2 }')"
        ICONSET="$WORK/AppIcon.iconset"
        mkdir -p "$ICONSET"
        for base in 16 32 128 256 512; do
            for scale in 1 2; do
                pixels=$((base * scale))
                if (( pixels <= SIZE )); then
                    suffix=""; [[ $scale == 2 ]] && suffix="@2x"
                    sips -z "$pixels" "$pixels" "$WORK/source.png" --out "$ICONSET/icon_${base}x${base}${suffix}.png" >/dev/null
                fi
            done
        done
        iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns" || echo "warning: icon not generated"
    fi
    rm -rf "$WORK"
fi

codesign --force --options runtime --timestamp=none --sign "${SIGN_IDENTITY:--}" "$APP"
codesign --verify --strict "$APP"

(cd "$BUILD_DIR" && rm -f AgentGauge-macOS.zip && ditto -c -k --keepParent AgentGauge.app AgentGauge-macOS.zip)
bash "$ROOT/scripts/build-dmg.sh" "$APP" "$BUILD_DIR/AgentGauge-macOS.dmg"
echo "Built $APP ($VERSION, build $BUILD_NUMBER)"
