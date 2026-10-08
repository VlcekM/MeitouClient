#!/usr/bin/env bash
# Renderer parity views: parity.sh <viewer exe> <out dir> [extra viewer options...]
# Always --weather Default (the scheduler is the viewer default; the baseline was rendered without weather); extra options after it win.
# Writes <out>/<view>.png for the five reference views at 13:00 and 02:00 (offscreen, 1600x900, on Vulkan, the only backend).
# Compare with parity-compare.sh against a baseline folder: max 0 per view; with --upscaler fsr up to the base against itself (renderer-native owner decision 13).
set -u
exe="$1"; out="$2"; shift 2
mkdir -p "$out"
views=(
  'hub|--town|The Hub|--distance|40000|--pitch|3'
  'rock|--at|-51468,-14324|--yaw|95|--pitch|2|--distance|300'
  'portnorth|--town|Port North'
  'zone14_30|--zone|14,30'
  'forest|--at|-37582,-80684|--yaw|-70.5|--pitch|6.1|--distance|10588'
)
for t in 13 2; do
  for v in "${views[@]}"; do
    IFS='|' read -r -a parts <<< "$v"
    name="${parts[0]}"; opts=("${parts[@]:1}")
    "$exe" --world "${opts[@]}" --time "$t" --size 1600x900 --weather Default --screenshot "$out/${name}_t$t.png" "$@" > "$out/${name}_t$t.log" 2>&1 \
      || echo "FAILED $name t$t (see $out/${name}_t$t.log)"
  done
done
