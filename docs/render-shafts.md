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

Placed fog volumes receive their own shadowed scattering, respecting plane/sphere/cylinder
geometry and colour/additive/alpha. A volume's sun share is the length-weighted average of every slice its visible path
(near to `near + 0.7/density`) crosses: up to the slice count of reads per volume pixel. A few fixed depths instead stamp
one copy of a shadow's outline per depth across the volume (**Observed** 2026-11-05: "slices" in a dust storm sphere; robot pit
block, offscreen: four depths differ from a 32-depth reference by up to 21/255, the slice walk by up to 4/255).
Cloud cover reduces appropriate sun contribution. With `gihybrid`'s
`clouds` part each sample also takes the cloud shadow (`InjectClouded`, one map read; look and cost **Unknown**). Air layer is an engine near-ground term,
separate from original distance haze, with day/key-light-dependent strength.

## Temporal

With a temporal upscaler the injection's samples move every frame (`uShaftRange.w`); left to the upscaler, TAA (which clamps its history to
the picture's neighbourhood) showed the noise as a shimmer in motion, DLSS less, and no temporal AA a still pattern. The grid averages them
itself (`LightShaftShaders.Resolve`): each cell's centre is taken into last frame's grid (its camera and slices), the filtered sun share read
there (bilinear in the slice, linear between slices), clamped to this frame's 3x3 neighbourhood in the slice and blended at
`LightShafts.TemporalBlend` (0.1) of this frame's; no history on a cut (a frame skipped, the eye 2000 units on). The raw injection goes to its own
target and the filtered atlas to `shaftInjected`, which every reader keeps using (two targets swapped each frame). Vulkan chain only.
**Observed** (the live main menu, TAA, a capture a second): the fine grain in consecutive frames' difference over the shafts gone, broad
changes (clouds, light) left; its high-pass measure 1.72 to 1.58 (the rest is TAA's own jitter on edges).

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

## Water and limits

Water-horizon handling must distinguish opaque far water, transmitted floor and sky; reconstruct
world positions with correct near/far depth and water plane. A wrong surface depth creates
horizon strips or darkens clear water. Test low sun, fog blocks, water/shore, night and clock cuts.
Inspect inject/integrate/scatter/apply separately (`MEITOU_SHAFT_STAMPS=1`); fog-hidden work and
no-volume views have different costs. **Observed** paired-view results are view-specific, not a
universal GPU saving. Temporal/froxel approximations remain limitations.
