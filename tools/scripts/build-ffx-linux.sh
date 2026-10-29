#!/usr/bin/env bash
# Builds libamd_fidelityfx_vk.so, which FSR on Linux loads (src/Meitou.Rendering/Upscalers/FsrUpscaler.cs): AMD's FidelityFX API library,
# which AMD ships prebuilt for Windows only, from the FidelityFX SDK's source (MIT): the SDK release zip (the one package-release.ps1 takes
# the Windows DLL from), downloaded at a pinned version and checked against its SHA-256, with ffx-linux.patch (beside this script), which
# ports it off Windows. The SDK needs Vulkan headers 1.3.250 or newer, which Ubuntu 22.04 and Debian 12 lack, so Khronos's are fetched the
# same way. Neither the sources nor the result are in the repository.
# Needs curl, unzip, tar, git, cmake, ninja, g++, glslangValidator and the Vulkan loader (Arch: vulkan-icd-loader glslang ninja;
# Debian/Ubuntu: libvulkan-dev glslang-tools ninja-build).
#   build-ffx-linux.sh [output folder, default MEITOU_DLSS_PATH, MEITOU_STREAMLINE_PATH or the repository's dlss-runtime] [download cache, default a temporary folder, removed after]
set -euo pipefail
version=1.1.4
sha256=0216556bfb0e243cec30004a2a98d38f4e3f7406cb7938e3c1b85c758e95d952
vk_headers=1.3.275
vk_sha256=7161da645dbd33fd4ea61eec08e0d77389a640010acbf4afc00234f84df9b314
patch=$(CDPATH= cd -- "$(dirname "$0")" && pwd)/ffx-linux.patch
out=${1:-${MEITOU_DLSS_PATH:-${MEITOU_STREAMLINE_PATH:-"$(dirname "$0")/../../dlss-runtime"}}}
src=$(mktemp -d)
if [ -n "${2:-}" ]; then cache=$2; trap 'rm -rf "$src"' EXIT; else cache=$(mktemp -d); trap 'rm -rf "$src" "$cache"' EXIT; fi
mkdir -p "$out" "$cache"
out=$(CDPATH= cd -- "$out" && pwd); cache=$(CDPATH= cd -- "$cache" && pwd)

fetch() { # file url sha256: a cached file that fails the check is downloaded again; a download is checked before it goes into the cache
  if [ -f "$cache/$1" ] && ! echo "$3  $cache/$1" | sha256sum -c --quiet - 2>/dev/null; then rm -f "$cache/$1"; fi
  if [ ! -f "$cache/$1" ]; then
    curl -fsSL -o "$cache/$1.part" "$2"
    echo "$3  $cache/$1.part" | sha256sum -c --quiet - || { rm -f "$cache/$1.part"; exit 1; }
    mv "$cache/$1.part" "$cache/$1"
  fi
}
fetch "FidelityFX-SDK-v$version.zip" "https://github.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/releases/download/v$version/FidelityFX-SDK-v$version.zip" $sha256
fetch "Vulkan-Headers-$vk_headers.tar.gz" "https://github.com/KhronosGroup/Vulkan-Headers/archive/refs/tags/v$vk_headers.tar.gz" $vk_sha256

cd "$src"
# Its own repository, so git apply works on these paths even when the temporary folder is inside another work tree.
git init -q .
unzip -q "$cache/FidelityFX-SDK-v$version.zip" common.cmake 'ffx-api/*' 'sdk/*' -x 'ffx-api/bin/*'
# The zip has CRLF text files; ffx-linux.patch is made against LF.
{ grep -rlIZ $'\r' . || [ $? -eq 1 ]; } | xargs -0r sed -i 's/\r$//'
git -c apply.whitespace=nowarn apply "$patch"
tar -xzf "$cache/Vulkan-Headers-$vk_headers.tar.gz" "Vulkan-Headers-$vk_headers/include"

cmake -S sdk/tools/ffx_shader_compiler -B sdk/tools/ffx_shader_compiler/build -G Ninja -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_HOME_DIRECTORY="$PWD/sdk/tools/ffx_shader_compiler" -DCMAKE_POLICY_VERSION_MINIMUM=3.5
cmake --build sdk/tools/ffx_shader_compiler/build
export FFX_SC_EXECUTABLE="$PWD/sdk/tools/ffx_shader_compiler/bin/FidelityFX_SC"
cmake -S ffx-api -B ffx-api/build -G Ninja -DCMAKE_BUILD_TYPE=Release -DFFX_API_BACKEND=VK_X64 -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
  -DVulkan_INCLUDE_DIR="$PWD/Vulkan-Headers-$vk_headers/include" \
  -DCMAKE_HOME_DIRECTORY="$PWD/ffx-api" -DCMAKE_POLICY_VERSION_MINIMUM=3.5
cmake --build ffx-api/build
cp ffx-api/build/libamd_fidelityfx_vk.so "$out/"
echo "built $out/libamd_fidelityfx_vk.so (AMD FidelityFX SDK $version)"
