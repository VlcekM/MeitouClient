#!/bin/sh
# Makes the macOS app bundle around the game: <app>/Contents/Info.plist and the icon in <app>/Contents/Resources. The caller puts the published
# files (the game, the viewer, meitou-tools, the runtime and settings-schema.json) in <app>/Contents/MacOS, each Mach-O file signed on its own
# beforehand: the bundle itself is not signed (docs/macos-port.md "App bundle"). Game Mode needs the bundle's Info.plist.
#
# Usage: macos-bundle.sh <app> <version> <minimum macOS>
#   app            the bundle to make, named after the game's executable (MeitouClient.app: Meitou.Game's AssemblyName)
#   version        the release's version; its numeric part (0.23.1 of 0.23.1-nightly.20261125) is the bundle's version
#   minimum macOS  LSMinimumSystemVersion: 14.0 for the standard builds (.NET 11), 13.0 for the legacy ones

set -eu

[ $# -eq 3 ] || { sed -n '2,/^$/p' "$0" | sed 's/^# \{0,1\}//' >&2; exit 2; }
app=${1%/}
executable=$(basename "$app" .app)
version=$2
minimum=$3

repo=$(cd "$(dirname "$0")/../.." && pwd)
numeric=$(printf '%s\n' "$version" | sed -E 's/^([0-9]+(\.[0-9]+){0,2}).*/\1/')
case "$numeric" in [0-9]*) ;; *) numeric=0.0.0 ;; esac

mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

# The identifier is the game's Wayland app ID (WaylandDesktop.cs).
cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key><string>wiki.kenshi.meitou</string>
  <key>CFBundleName</key><string>Meitou</string>
  <key>CFBundleDisplayName</key><string>Meitou</string>
  <key>CFBundleExecutable</key><string>$executable</string>
  <key>CFBundleIconFile</key><string>meitou</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleShortVersionString</key><string>$numeric</string>
  <key>CFBundleVersion</key><string>$numeric</string>
  <key>LSMinimumSystemVersion</key><string>$minimum</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.role-playing-games</string>
  <key>LSSupportsGameMode</key><true/>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSPrincipalClass</key><string>NSApplication</string>
</dict>
</plist>
EOF
plutil -lint -s "$app/Contents/Info.plist"

# The icon: the 2048 px branding image scaled to the iconset's sizes.
iconset=$(mktemp -d)/meitou.iconset
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$repo/assets/branding/meitou-icon-2048.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z "$double" "$double" "$repo/assets/branding/meitou-icon-2048.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns -o "$app/Contents/Resources/meitou.icns" "$iconset"
rm -rf "$(dirname "$iconset")"
