# Light shafts

Meitou `shafts` shadows haze/placed fog and adds an air layer; no original-game counterpart.
`LightShafts`, `LightShaftShaders`, PostOptions and WorldFrame own the effect.
Original haze: [sky](formats/sky.md); volume formulas: [fogfeatures](formats/fogfeatures.md).

## Composition and grid

For original haze mix `colour*(1-alpha)+rgb*alpha`, shadowed output is
`colour*(1-alpha)+rgb*alpha*(1-k*(1-L))`, subtracting `k*(1-L)*rgb*alpha`.
`atmoApplyHaze(0,eye,point)` supplies the haze contribution. L is sun visibility integrated
along the view; k is strength (default0.8) times sun/(sun+sky) luminance share. Fade sun below
horizon; do not darken night ambient or double-apply haze over the sky.

Froxel injection samples cascaded sun visibility; integration/air scatter share a compute scan
walking each column once. Distances and projection must match the scene depth's reconstruction (`depthViewZ`).
Prepared depth/shadow/global constants and barriers precede compute/apply. Half-resolution
scene-layer integration needs depth-aware fallback at discontinuities.

**Quality** (`Light shafts quality`, High by default): High uses 160 cells across, 64 depth
slices and 2 shadow samples per cell; Medium 128 x 48 x 2; Low 96 x 32 x 1. The apply pass stays
at the render size; the setting changes the froxel grid and sample count, not the effect's
strength. The Low quality preset turns the shafts off (the `Light shafts` switch) rather than
relying on the low tier. `--ab shafts-half` is a separate experiment for the merged apply pass,
not a quality tier.

## Temporal

### Fog shadow after the upscaler

The placed fog volumes' beams (their sun shadow, `fogVolumeLight`) went through the temporal upscaler's history with the motion of the surface behind
them, so under TAA and DLSS they trailed the camera for about twenty frames: **Verified** with the viewer at Obedience (`--at -77245,-57315 --time 8
--distance 500 --pitch 15`, fog islands; `--orbit-step 1 --pan-frames 10 --sequence 40`: the last moving frame against the settled one at the same
pose, shafts on minus off). Without AA the moving frame matched (0.24 mean, 99th percentile 1.0, the noise floor); with TAA 0.62 / 3.7 and DLSS
0.67 / 4.0, decaying over twenty frames. The haze's own shafts were not the cause: with the volumes' shadow off the shafts' contribution was 0.09 of 3.5.
With a temporal upscaler and the merged layers (`PostProcess.FogShadowAfterUpscale`; `MEITOU_FOG_SHADOW_LATE=0` turns it off) the merged pass blends
the volumes unshadowed, `FogShadowHalf` (half size, the volumes' march with `FOG_VOLUME_DELTA`: the shadow kept apart in `fogAddLit`) writes
shadowed minus unshadowed, and `CompositeLateFogShadow` adds it to the upscaler's output: four render pixels round each display pixel (the jitter
undone), each upsampled by its distance, weighted bilinearly, then a temporal filter of its own (last frame's, reprojected by the closest one's
motion, clamped to the four's range, blended at 0.1) for the depth edges, into a target of its own (the TAA's output is its next history).
After: TAA 0.24 / 1.0 (the noise floor), DLSS 0.39 / 2.0. The settled picture against the old path: 0.135 mean, 184 pixels of 921600 over 8/255,
all on silhouettes against the fogged sky (the edge a little sharper). Not moved: the haze's shafts and air layer, which stay in the upscaled picture.
