# Light shafts

Meitou `shafts` shadows haze/placed fog and adds an air layer; no original-game counterpart.
`PostProcess.Shafts.cs`, `LightShaftShaders`, PostOptions and WorldFrame own the effect.
Original haze: [sky](formats/sky.md); volume formulas: [fogfeatures](formats/fogfeatures.md).

## Composition and grid

For original haze mix `colour*(1-alpha)+rgb*alpha`, shadowed output is
`colour*(1-alpha)+rgb*alpha*(1-k*(1-L))`, subtracting `k*(1-L)*rgb*alpha`.
`atmoApplyHaze(0,eye,point)` supplies the haze contribution. L is sun visibility integrated
along the view; k is strength (default0.8) times sun/(sun+sky) luminance share. Fade sun below
horizon; do not darken night ambient or double-apply haze over the sky.

Froxel injection samples cascaded sun visibility; integration/air scatter share a compute scan
walking each column once. Distances, projection and depth slices must match scene reconstruction.
Prepared depth/shadow/global constants and barriers precede compute/apply. Half-resolution
scene-layer integration needs depth-aware fallback at discontinuities.

**Quality** (`Light shafts quality`, High by default): High uses 160 cells across, 64 depth
slices and 2 shadow samples per cell; Medium 128 x 48 x 2; Low 96 x 32 x 1. The apply pass stays
at the render size; the setting changes the froxel grid and sample count, not the effect's
strength. The Low quality preset turns the shafts off (the `Light shafts` switch) rather than
relying on the low tier. `--ab shafts-half` is a separate experiment for the merged apply pass,
not a quality tier.

Placed fog volumes receive their own shadowed scattering, respecting plane/sphere/cylinder
geometry and colour/additive/alpha. Cloud cover reduces appropriate sun contribution; actual
volumetric cloud-shadow-map sampling is incomplete. Air layer is an engine near-ground term,
separate from original distance haze, with day/key-light-dependent strength.

## Temporal

With a temporal upscaler the injection's samples move every frame (`uShaftRange.w`); left to the upscaler, TAA (which clamps its history to
the picture's neighbourhood) showed the noise as a shimmer in motion, DLSS less, and no temporal AA a still pattern. The grid averages them
itself (`LightShaftShaders.Resolve`): each cell's centre is taken into last frame's grid (its camera and slices), the filtered sun share read
there (bilinear in the slice, linear between slices), clamped to this frame's 3x3 neighbourhood in the slice and blended at
`ShaftTemporalBlend` (0.1) of this frame's; no history on a cut (a frame skipped, the eye 2000 units on). The raw injection goes to its own
target and the filtered atlas to `shaftInjected`, which every reader keeps using (two targets swapped each frame). Vulkan chain only.
**Observed** (the live main menu, TAA, a capture a second): the fine grain in consecutive frames' difference over the shafts gone, broad
changes (clouds, light) left; its high-pass measure 1.72 to 1.58 (the rest is TAA's own jitter on edges).

## Water and limits

Water-horizon handling must distinguish opaque far water, transmitted floor and sky; reconstruct
world positions with correct near/far depth and water plane. A wrong surface depth creates
horizon strips or darkens clear water. Test low sun, fog blocks, water/shore, night and clock cuts.
Inspect inject/integrate/scatter/apply separately (`MEITOU_SHAFT_STAMPS=1`); fog-hidden work and
no-volume views have different costs. **Observed** paired-view results are view-specific, not a
universal GPU saving. Temporal/froxel approximations and cloud/GI integration remain limitations.
