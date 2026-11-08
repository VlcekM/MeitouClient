#!/usr/bin/env bash
# Builds libamd_fidelityfx_vk.so, which FSR 3 upscaling and frame generation on Linux load (src/Meitou.Game/Rendering/Upscalers): an unofficial
# Linux build of AMD's FidelityFX SDK 1.1.4 (MIT), made from AMD's source at the pinned tag plus tools/patches/fidelityfx-sdk-1.1.4-linux.patch.
# The source is cloned at the pinned commit (checked) and built in a temporary folder; neither it nor the result is in the repository.
# AMD's LICENSE.txt is copied beside the library as LICENSE-FidelityFX.txt. Needs git, cmake, ninja, g++, glslangValidator and the Vulkan
# development package (the loader to link). Its headers are overridden by Khronos' at a pinned tag (cloned too): AMD's swapchain code needs 1.3.237
# or newer, which older distributions (Ubuntu 22.04: 1.3.204) lack.
#   build-ffx-linux.sh [output folder, default the repository's dlss-runtime] [work folder, default a temporary folder, removed after]
set -euo pipefail
tag=v1.1.4
commit=c6efa6bf7f2027b3ec94f28578bb5965eabb9e55
vk_tag=v1.3.296
vk_commit=29f979ee5aa58b7b005f805ea8df7a855c39ff37
here=$(cd -- "$(dirname -- "$0")" && pwd)
patch="$here/../patches/fidelityfx-sdk-1.1.4-linux.patch"
out=${1:-"$here/../../dlss-runtime"}
missing=0
need() { if ! command -v "$1" >/dev/null 2>&1; then echo "missing: $1 ($2)" >&2; missing=1; fi; }
need git "to fetch AMD's source"
need cmake "to configure the build"
need ninja "the build generator (package ninja or ninja-build)"
need g++ "a C++ compiler (package gcc-c++ or build-essential)"
need glslangValidator "compiles the shaders (package glslang, glslang-tools or glslang-dev)"
if ! printf '#include <vulkan/vulkan.h>\n' | ${CXX:-g++} -x c++ -fsyntax-only - >/dev/null 2>&1; then
  echo "missing: Vulkan headers (package libvulkan-dev, vulkan-devel or vulkan-headers) and the loader" >&2; missing=1
fi
[ -f "$patch" ] || { echo "missing: $patch" >&2; missing=1; }
[ "$missing" = 0 ] || exit 1
if [ -n "${2:-}" ]; then work=$2; else work=$(mktemp -d); trap 'rm -rf "$work"' EXIT; fi
mkdir -p "$out" "$work"
out=$(cd -- "$out" && pwd)
src="$work/FidelityFX-SDK"
rm -rf "$src"
git clone --quiet --depth 1 --branch "$tag" https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK "$src"
[ "$(git -C "$src" rev-parse HEAD)" = "$commit" ] || { echo "tag $tag is not commit $commit; refusing to build" >&2; exit 1; }
git -C "$src" apply "$patch"
rm -rf "$work/Vulkan-Headers"
git clone --quiet --depth 1 --branch "$vk_tag" https://github.com/KhronosGroup/Vulkan-Headers "$work/Vulkan-Headers"
[ "$(git -C "$work/Vulkan-Headers" rev-parse HEAD)" = "$vk_commit" ] || { echo "Vulkan-Headers $vk_tag is not commit $vk_commit; refusing to build" >&2; exit 1; }
export CXXFLAGS="${CXXFLAGS:-} -isystem $work/Vulkan-Headers/include" CFLAGS="${CFLAGS:-} -isystem $work/Vulkan-Headers/include"
"$src/linux/build.sh" "$work/build"
cp "$work/build/libamd_fidelityfx_vk.so" "$out/"
cp "$src/LICENSE.txt" "$out/LICENSE-FidelityFX.txt"
echo "built $out/libamd_fidelityfx_vk.so (unofficial Linux build of AMD FidelityFX SDK ${tag#v})"
