#!/usr/bin/env bash
# Builds libmeitou_ngx.so, which DLSS on Linux loads (src/Meitou.Game/Rendering/Upscalers/Ngx.cs): the NGX client library of NVIDIA's DLSS SDK,
# its static libnvsdk_ngx.a linked as is into a shared library (no source of ours). The archive is downloaded at the pinned SDK version and
# checked against its SHA-256; neither it nor the result is in the repository. Needs curl and g++ (on Fedora and RHEL also libstdc++-static).
#   build-ngx-shim.sh [output folder, default the repository's dlss-runtime] [download cache, default a temporary folder, removed after]
set -eu
version=310.9.16
sha256=d3c50bb0d9624fc35e502a8e929b8fc1e2b505017ebdb84c0d5b627e11a235df
out=${1:-"$(dirname "$0")/../../dlss-runtime"}
if [ -n "${2:-}" ]; then cache=$2; else cache=$(mktemp -d); trap 'rm -rf "$cache"' EXIT; fi
mkdir -p "$out" "$cache"
archive="$cache/libnvsdk_ngx-$version.a"
if [ ! -f "$archive" ]; then
  curl -fsSL -o "$archive.part" "https://github.com/NVIDIA/DLSS/raw/v$version/lib/Linux_x86_64/libnvsdk_ngx.a"
  mv "$archive.part" "$archive"
fi
echo "$sha256  $archive" | sha256sum -c --quiet -
# The archive's C++ runtime is linked in and kept private (--exclude-libs), so its calls never bind to another libstdc++ in the process.
g++ -shared -fPIC -o "$out/libmeitou_ngx.so" -Wl,--whole-archive "$archive" -Wl,--no-whole-archive \
  -Wl,--exclude-libs,libstdc++.a:libgcc.a:libgcc_eh.a -ldl -static-libstdc++ -static-libgcc
echo "built $out/libmeitou_ngx.so (NVIDIA DLSS SDK $version)"
