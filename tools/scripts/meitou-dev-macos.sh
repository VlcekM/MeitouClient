#!/bin/sh
# Builds this checkout and runs it on macOS (Apple silicon), the game directly or through the launcher.
#
# Usage: meitou-dev-macos.sh [options] [--] [args...]
#   (default)    Builds the game (Release) and runs it from its build folder; args go to the game. Metal unless args name --renderer.
#   --launcher   Builds the game (Release) and the launcher (Debug), copies the game into the launcher's versions/ folder as the dev
#                version (newest, so Play uses it, and the Versions and Benchmark pages list it) with the settings schema a release
#                ships, then runs the launcher. Remaining args go to the launcher; game arguments belong in its Launch arguments.
#   --no-build   Skips the builds (the last ones are used).
#   --remove     Removes the dev version from versions/ and exits.
#   -h, --help   This text.
#
# Environment (all optional):
#   MEITOU_REPO         the checkout (default: the one this script is in, following a symlink to it)
#   MEITOU_DEV_VERSION  the dev version's name (default 99.0.0-dev)
#   MEITOU_DATA_DIR     the launcher's data folder (default ~/Library/Application Support/Meitou)
#   MEITOU_INSTALL_DIR  the folder holding versions/ (default: the launcher's install folder from launcher.json, else the data folder)
#   MEITOU_GAME_CONFIG  the game's build configuration (default Release; Debug is far too slow to judge speed by)
#   DOTNET_ROOT         a .NET install to use (default ~/.dotnet when it has one, else the dotnet on PATH); the repo's global.json picks the SDK
#   HOMEBREW_PREFIX     where Homebrew's GLFW is (default /opt/homebrew); the direct run puts its lib/ on DYLD_LIBRARY_PATH
#   KENSHI_PATH         the Kenshi install; unset, the game looks for meitou.local.json or asks
# Any other MEITOU_* variable reaches the game as set.

set -eu

mode=game
build=1
remove=0
while [ $# -gt 0 ]; do
    case "$1" in
        --launcher) mode=launcher ;;
        --no-build) build=0 ;;
        --remove) remove=1 ;;
        -h|--help) sed -n '2,/^$/p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        --) shift; break ;;
        *) break ;;
    esac
    shift
done

# The checkout: this script's, with symlinks (a copy in ~/.local/bin) followed.
here=$0
while [ -h "$here" ]; do
    dir=$(cd "$(dirname "$here")" && pwd)
    here=$(readlink "$here")
    case "$here" in /*) ;; *) here="$dir/$here" ;; esac
done
REPO=${MEITOU_REPO:-$(cd "$(dirname "$here")/../.." && pwd)}
[ -f "$REPO/MeitouClient.slnx" ] || { echo "meitou-dev-macos: $REPO is not a checkout (no MeitouClient.slnx); set MEITOU_REPO" >&2; exit 1; }

VERSION=${MEITOU_DEV_VERSION:-99.0.0-dev}
CONFIG=${MEITOU_GAME_CONFIG:-Release}
DATA=${MEITOU_DATA_DIR:-"$HOME/Library/Application Support/Meitou"}
BUILD="$REPO/src/Meitou.Game/bin/$CONFIG/net11.0"

# The folder the launcher keeps versions/ in: its install folder setting, else the data folder.
install_dir() {
    if [ -n "${MEITOU_INSTALL_DIR:-}" ]; then printf '%s\n' "$MEITOU_INSTALL_DIR"; return; fi
    folder=$(plutil -extract InstallFolder raw -o - "$DATA/launcher.json" 2>/dev/null || true)
    folder=${folder:-$DATA}
    printf '%s\n' "${folder%/}"
}
DEV="$(install_dir)/versions/$VERSION"

if [ "$remove" = 1 ]; then
    rm -rf "$DEV"
    echo "removed $DEV"
    exit 0
fi

# A .NET 11 SDK installed with dotnet-install lives in ~/.dotnet, ahead of Homebrew's older one.
if [ -z "${DOTNET_ROOT:-}" ] && [ -x "$HOME/.dotnet/dotnet" ]; then export DOTNET_ROOT="$HOME/.dotnet"; fi
if [ -n "${DOTNET_ROOT:-}" ]; then PATH="$DOTNET_ROOT:$PATH"; export PATH; fi
command -v dotnet >/dev/null 2>&1 || { echo "meitou-dev-macos: no dotnet found; install the .NET 11 SDK or set DOTNET_ROOT" >&2; exit 1; }

# Builds run in the repo, where global.json selects the SDK.
build_game() { (cd "$REPO" && dotnet build -c "$CONFIG" src/Meitou.Game --nologo -v q); }
build_launcher() { (cd "$REPO" && dotnet build -c Debug launcher/Meitou.Launcher --nologo -v q); }

if [ "$mode" = launcher ]; then
    # The launcher must not run with Homebrew on the dyld path: its libpng/libjpeg/libtiff shadow the system ones and crash
    # ImageIO (SIGBUS) when CoreText draws an emoji from the news. It finds MoltenVK in Homebrew by itself.
    unset DYLD_LIBRARY_PATH

    if [ "$build" = 1 ]; then build_game && build_launcher || exit 1; fi
    [ -x "$BUILD/MeitouClient" ] || { echo "meitou-dev-macos: no game build in $BUILD (run without --no-build)" >&2; exit 1; }

    # The game as an installed version: its folder, and the schema the Graphics page and the Metal check read.
    mkdir -p "$DEV"
    rsync -a --delete "$BUILD/" "$DEV/"
    "$DEV/MeitouClient" --settings-schema "$DEV/settings-schema.json"
    echo "installed $VERSION in $DEV"

    cd "$REPO"
    exec dotnet run -c Debug --no-build --project launcher/Meitou.Launcher -- "$@"
fi

# Direct run: Homebrew's GLFW is found through the dyld path.
export DYLD_LIBRARY_PATH="${HOMEBREW_PREFIX:-/opt/homebrew}/lib${DYLD_LIBRARY_PATH:+:$DYLD_LIBRARY_PATH}"
if [ "$build" = 1 ]; then build_game || exit 1; fi
[ -x "$BUILD/MeitouClient" ] || { echo "meitou-dev-macos: no game build in $BUILD (run without --no-build)" >&2; exit 1; }

# The game defaults to Vulkan, which macOS lacks: Metal comes first, and a --renderer in the arguments overrides it.
renderer="--renderer metal"
for arg in "$@"; do
    case "$arg" in --renderer|--renderer=*) renderer="" ;; esac
done

cd "$BUILD"
# shellcheck disable=SC2086
exec "$BUILD/MeitouClient" $renderer "$@"
