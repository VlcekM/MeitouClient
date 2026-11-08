#!/usr/bin/env bash
# Builds meitou_ngx.dll, which DLSS on Windows (and under Proton) loads when frame generation is on (src/Meitou.Game/Rendering/Upscalers/Ngx.cs): the NGX
# client library of NVIDIA's DLSS SDK, its static nvsdk_ngx_s.lib (static C runtime) linked as is into a DLL that exports the functions Ngx.cs calls
# (no source of ours). The library is downloaded at the pinned SDK version and checked against its SHA-256; neither it nor the result is in the
# repository. Cross-built on Linux: needs curl, clang-cl, lld-link, and the Microsoft C runtime and Windows SDK import libraries, which
# `xwin --accept-license --cache-dir ~/.cache/xwin/dl splat --output ~/.cache/xwin/splat` (https://github.com/Jake-Shadle/xwin) fetches; the
# folder is XWIN_SPLAT (default ~/.cache/xwin/splat). Building it means accepting Microsoft's licence for those files.
#   build-ngx-shim-win.sh [output folder, default the repository's dlss-runtime] [download cache, default a temporary folder, removed after]
set -eu
version=310.9.16
sha256=7b9a5239c1145af5f645ab472aa066f9ea79db36ac819562312aae69d65f3765
out=${1:-"$(dirname "$0")/../../dlss-runtime"}
if [ -n "${2:-}" ]; then cache=$2; else cache=$(mktemp -d); trap 'rm -rf "$cache"' EXIT; fi
splat=${XWIN_SPLAT:-$HOME/.cache/xwin/splat}
[ -d "$splat/crt/lib/x86_64" ] || { echo "no xwin CRT in $splat (see the header)" >&2; exit 1; }
mkdir -p "$out" "$cache"
lib="$cache/nvsdk_ngx_s-$version.lib"
if [ ! -f "$lib" ]; then
  curl -fsSL -o "$lib.part" "https://github.com/NVIDIA/DLSS/raw/v$version/lib/Windows_x86_64/x64/nvsdk_ngx_s.lib"
  mv "$lib.part" "$lib"
fi
echo "$sha256  $lib" | sha256sum -c --quiet -
exports=()
for f in NVSDK_NGX_VULKAN_{Init_with_ProjectID,Shutdown1,GetCapabilityParameters,AllocateParameters,DestroyParameters,CreateFeature1,ReleaseFeature,EvaluateFeature_C,RequiredExtensions} \
         NVSDK_NGX_Parameter_{SetI,SetUI,SetF,SetVoidPointer,GetI,GetUI}; do exports+=("-export:$f"); done
# The archive brings its C++ runtime parts and the static C runtime (/MT) with it; lld-link needs only the folders of the xwin splat.
lld-link -dll -nologo -out:"$out/meitou_ngx.dll" -machine:x64 -defaultlib:libcmt -defaultlib:libucrt -defaultlib:libvcruntime -defaultlib:libcpmt \
  -defaultlib:kernel32 -defaultlib:advapi32 -defaultlib:user32 \
  -libpath:"$splat/crt/lib/x86_64" -libpath:"$splat/sdk/lib/um/x86_64" -libpath:"$splat/sdk/lib/ucrt/x86_64" \
  -wholearchive:"$lib" "${exports[@]}" 2>&1 | head -40
[ -f "$out/meitou_ngx.dll" ] || { echo "link failed" >&2; exit 1; }
rm -f "${out}/meitou_ngx.lib"
echo "built $out/meitou_ngx.dll (NVIDIA DLSS SDK $version)"
