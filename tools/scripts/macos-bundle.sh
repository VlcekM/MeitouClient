#!/bin/sh
# Makes a macOS app bundle: <app>/Contents/Info.plist and the icon in <app>/Contents/Resources. The caller puts the program's files in
# <app>/Contents/MacOS. A release signs each Mach-O file on its own beforehand: the bundle itself is not signed (docs/macos-port.md "App bundle").
# The game's bundle declares Game Mode, which needs it; the launcher's (--launcher, for tools/scripts/meitou-dev-macos.sh) gives a checkout's
# launcher its name and icon. Running again rewrites the Info.plist and keeps an up-to-date icon.
#
# Usage: macos-bundle.sh [--launcher] <app> <version> <minimum macOS>
#   --launcher     the launcher's bundle instead of the game's
#   app            the bundle to make, named after its executable (MeitouClient.app: Meitou.Game's AssemblyName; meitou-launcher.app)
#   version        the release's version; its numeric part (0.23.1 of 0.23.1-nightly.20261125) is the bundle's version
#   minimum macOS  LSMinimumSystemVersion: 14.0 for the standard builds (.NET 11), 13.0 for the legacy ones

set -eu

launcher=0
if [ "${1:-}" = --launcher ]; then
    launcher=1
    shift
fi
[ $# -eq 3 ] || { sed -n '2,/^$/p' "$0" | sed 's/^# \{0,1\}//' >&2; exit 2; }
app=${1%/}
executable=$(basename "$app" .app)
version=$2
minimum=$3

repo=$(cd "$(dirname "$0")/../.." && pwd)
icon="$repo/assets/branding/meitou-icon-2048.png"
numeric=$(printf '%s\n' "$version" | sed -E 's/^([0-9]+(\.[0-9]+){0,2}).*/\1/')
case "$numeric" in [0-9]*) ;; *) numeric=0.0.0 ;; esac

# The identifiers are the Linux ones: the game's Wayland app ID (WaylandDesktop.cs), the launcher's desktop entry (docs/launcher.md).
if [ "$launcher" = 1 ]; then
    identifier=wiki.kenshi.meitou.launcher
    name="Meitou Launcher"
    game=""
else
    identifier=wiki.kenshi.meitou
    name=Meitou
    game='  <key>LSApplicationCategoryType</key><string>public.app-category.role-playing-games</string>
  <key>LSSupportsGameMode</key><true/>
'
fi

mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key><string>$identifier</string>
  <key>CFBundleName</key><string>$name</string>
  <key>CFBundleDisplayName</key><string>$name</string>
  <key>CFBundleExecutable</key><string>$executable</string>
  <key>CFBundleIconFile</key><string>meitou</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleShortVersionString</key><string>$numeric</string>
  <key>CFBundleVersion</key><string>$numeric</string>
  <key>LSMinimumSystemVersion</key><string>$minimum</string>
$game  <key>NSHighResolutionCapable</key><true/>
  <key>NSPrincipalClass</key><string>NSApplication</string>
</dict>
</plist>
EOF
plutil -lint -s "$app/Contents/Info.plist"

# The icon: the 2048 px branding image scaled to the iconset's sizes.
icns="$app/Contents/Resources/meitou.icns"
[ "$icns" -nt "$icon" ] && exit 0
iconset=$(mktemp -d)/meitou.iconset
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$icon" --out "$iconset/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z "$double" "$double" "$icon" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns -o "$icns" "$iconset"
rm -rf "$(dirname "$iconset")"
