#!/bin/bash
# Builds CashVoice for macOS (universal: Apple Silicon + Intel) and packages CashVoice-mac.pkg.
#   mac/build.sh <version>
# Needs Xcode command line tools. Output: mac/build/CashVoice-mac.pkg
set -euo pipefail

VERSION="${1:-0.0.0}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="$ROOT/mac"
OUT="$SRC/build"
ARCHS=(arm64 x86_64)

rm -rf "$OUT"
mkdir -p "$OUT"

echo "== DSP checks (native arch)"
clang++ -std=c++17 -O2 "$ROOT/apo/test/dsp_test.cpp" -o "$OUT/dsp_test"
"$OUT/dsp_test"

echo "== Driver"
DRV="$OUT/CashVoiceDriver.driver"
mkdir -p "$DRV/Contents/MacOS"
for a in "${ARCHS[@]}"; do
  clang++ -std=c++17 -O2 -Wall -arch "$a" -mmacosx-version-min=11.0 -bundle \
    -framework CoreAudio -framework CoreFoundation \
    "$SRC/Driver/CashVoiceDriver.cpp" -o "$OUT/driver-$a"
done
lipo -create "$OUT"/driver-arm64 "$OUT"/driver-x86_64 -output "$DRV/Contents/MacOS/CashVoiceDriver"
sed "s/__VERSION__/$VERSION/g" "$SRC/Driver/Info.plist" > "$DRV/Contents/Info.plist"
codesign --force --sign - "$DRV"

echo "== App"
APP="$OUT/CashVoice.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
for a in "${ARCHS[@]}"; do
  clang++ -std=c++17 -O2 -Wall -arch "$a" -mmacosx-version-min=13.0 \
    -c "$SRC/App/Core/CashVoiceCore.cpp" -o "$OUT/core-$a.o"
  swiftc -O -parse-as-library -target "$a-apple-macos13.0" \
    -import-objc-header "$SRC/App/Bridge.h" \
    "$SRC"/App/Sources/*.swift "$OUT/core-$a.o" \
    -lc++ -framework CoreAudio -framework AudioToolbox -framework AVFoundation -framework Carbon \
    -framework SwiftUI -framework AppKit -framework ServiceManagement \
    -o "$OUT/app-$a"
done
lipo -create "$OUT"/app-arm64 "$OUT"/app-x86_64 -output "$APP/Contents/MacOS/CashVoice"
sed "s/__VERSION__/$VERSION/g" "$SRC/App/Info.plist" > "$APP/Contents/Info.plist"

# Icon: 1024 px PNG -> .icns
ICONSET="$OUT/AppIcon.iconset"
mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
  sips -z $s $s "$SRC/App/AppIcon.png" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  sips -z $((s*2)) $((s*2)) "$SRC/App/AppIcon.png" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"
# LT Superior (SIL OFL), the same files the Windows app bundles; registered via ATSApplicationFontsPath.
mkdir -p "$APP/Contents/Resources/Fonts"
cp "$ROOT"/app/Fonts/LTSuperior-*.ttf "$ROOT"/app/Fonts/LTSuperior-OFL.txt "$APP/Contents/Resources/Fonts/"
codesign --force --sign - "$APP"

echo "== Package"
PKGROOT="$OUT/pkgroot"
mkdir -p "$PKGROOT/Applications" "$PKGROOT/Library/Audio/Plug-Ins/HAL"
cp -R "$APP" "$PKGROOT/Applications/"
cp -R "$DRV" "$PKGROOT/Library/Audio/Plug-Ins/HAL/"
# Always install to /Applications (don't "relocate" to some other copy the installer finds).
pkgbuild --analyze --root "$PKGROOT" "$OUT/components.plist"
n=$(/usr/libexec/PlistBuddy -c "Print" "$OUT/components.plist" | grep -c "BundleIsRelocatable" || true)
for ((i=0; i<n; i++)); do
  /usr/libexec/PlistBuddy -c "Set :$i:BundleIsRelocatable false" "$OUT/components.plist" 2>/dev/null || true
done
chmod +x "$SRC/pkg/scripts/postinstall"
pkgbuild --root "$PKGROOT" --component-plist "$OUT/components.plist" \
  --identifier com.cashroy.CashVoice --version "$VERSION" \
  --scripts "$SRC/pkg/scripts" --install-location / \
  "$OUT/CashVoice-mac.pkg"

ls -la "$OUT"/*.pkg
lipo -info "$APP/Contents/MacOS/CashVoice" "$DRV/Contents/MacOS/CashVoiceDriver"
