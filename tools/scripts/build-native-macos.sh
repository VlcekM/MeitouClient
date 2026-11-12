#!/usr/bin/env bash
# Build the bundled OpenAL Soft and GLFW versions for Ventura instead of Sonoma.
set -euo pipefail
rid="${1:?usage: build-native-macos.sh osx-x64|osx-arm64}"
case "$rid" in
  osx-x64) arch=x86_64 ;;
  osx-arm64) arch=arm64 ;;
  *) echo "Unsupported runtime: $rid" >&2; exit 1 ;;
esac
root="$(cd "$(dirname "$0")/../.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
# OpenAL Soft 1.23.1 (LGPL-2.0-or-later), pinned to the upstream tag's commit.
curl --fail --location --retry 3 \
  https://github.com/kcat/openal-soft/archive/d3875f333fb6abe2f39d82caca329414871ae53b.tar.gz \
  | tar -xz -C "$work" --strip-components=1
cmake -S "$work" -B "$work/build" -DCMAKE_BUILD_TYPE=Release -DCMAKE_POLICY_VERSION_MINIMUM=3.5 \
  -DCMAKE_OSX_ARCHITECTURES="$arch" -DCMAKE_OSX_DEPLOYMENT_TARGET=13.0 \
  -DALSOFT_UTILS=OFF -DALSOFT_EXAMPLES=OFF -DALSOFT_TESTS=OFF
cmake --build "$work/build" --config Release --parallel 3
mkdir -p "$root/macos-runtime/$rid"
cp -L "$work/build/libopenal.dylib" "$root/macos-runtime/$rid/libopenal.dylib"
cp "$work/COPYING" "$root/macos-runtime/$rid/LICENSE-OpenAL-Soft.txt"

# GLFW 3.4 (zlib licence), the same version as Ultz.Native.GLFW.
mkdir "$work/glfw"
curl --fail --location --retry 3 \
  https://github.com/glfw/glfw/archive/7b6aead9fb88b3623e3b3725ebb42670cbe4c579.tar.gz \
  | tar -xz -C "$work/glfw" --strip-components=1
cmake -S "$work/glfw" -B "$work/glfw/build" -DCMAKE_BUILD_TYPE=Release -DCMAKE_POLICY_VERSION_MINIMUM=3.5 \
  -DCMAKE_OSX_ARCHITECTURES="$arch" -DCMAKE_OSX_DEPLOYMENT_TARGET=13.0 \
  -DBUILD_SHARED_LIBS=ON -DGLFW_BUILD_DOCS=OFF -DGLFW_BUILD_TESTS=OFF -DGLFW_BUILD_EXAMPLES=OFF
cmake --build "$work/glfw/build" --config Release --parallel 3
cp -L "$work/glfw/build/src/libglfw.3.dylib" "$root/macos-runtime/$rid/libglfw.3.dylib"
cp "$work/glfw/LICENSE.md" "$root/macos-runtime/$rid/LICENSE-GLFW.txt"
