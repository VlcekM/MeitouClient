# Post-processing

`PostProcess*.cs`, `PostProcessShaders`, upscalers and `WorldFrame` own the HDR chain.
Original exposure/FXAA/haze rules: [formats/post-processing.md](formats/post-processing.md),
[sky](formats/sky.md), [weather](formats/weather.md). UI/CLI controls: [viewer.md](viewer.md).

## Frame contract

Scene shaders write unclamped HDR. Colour/depth/motion targets belong to PostProcess; scenes
must preserve requested attachments and near/far depth history. Scene rendering is at render
scale; reconstruction precedes display-sized composite/exposure/grading. Single sampling with
TAA/FSR/DLSS; jitter scene/sky, never shadow maps or reused reflections. Fade/dither/noise must
agree across colour/depth/velocity passes. Transparent layers do not overwrite opaque depth.

Faithful uses the game's FXAA, exposure, haze and flat ambient where selected. Meitou switches
independently select SSAO, TAA/upscalers, tone/colour/night grading, particle quality and GI.
Do not clamp before HDR lighting/composite. Texture mip bias log2(render/display)-1 with any temporal method.
Motion uses previous camera/depth plus moving-object/grass paths; near/far depth reconstruction
must use the corresponding projection. `sun-motion` adds clock-driven sky motion near the sun.
Reset temporal histories after cuts, target resize or incompatible setting changes.
DLSS (the default) where Streamline reports it unsupported runs FSR, then TAA. TAA zeroes NaN/Inf
in its inputs and output: GLSL `max` need not drop NaN (AMD keeps it), and one in the history spreads.
For FSR/DLSS the merged velocity kernel zeroes them in the scene colour in place and in the motion
(Observed: the FSR fallback on an RX 9070 XT still flickered without it).

Game/viewer builds and publishes copy the Streamline/DLSS DLLs and FSR's `amd_fidelityfx_vk.dll` that
are present in `MEITOU_STREAMLINE_PATH` or the git-ignored root `dlss-runtime/`;
`-p:StreamlineRuntimePath=` overrides the source. Without them a dev build logs
"not found" and runs TAA (a release folder's DLLs can be copied there). Release packaging fetches its own.

TAA: 3x3 Gaussian exp(-2.29 d^2) with d in display pixels (upscaling doesn't widen it), Catmull-Rom history,
YCoCg variance clip, blend 0.1. Upscaler sharpness: FSR's RCAS; after TAA and DLSS (DLSS ignores it) the
composite pushes each pixel from its 4 neighbours' mean by 2 x sharpness, clamped to their range (no halos;
the history stays unsharpened). Measured (Hub, 1080p, mean |Laplacian|): no AA 16.6, TAA 12.5 before
and 23.1 after (bias -1, display-pixel kernel, sharpen), DLSS 26.6.

## SSAO, fog and layers

SSAO reads near depth; character mask controls character occlusion (default strength0.25).
`ssao-mask-skip` may omit a mask read only when no character was drawn. The taps read the mask from a half-size R8 copy of the scene colour's
alpha (`SsaoMask`), not the scene colour: **Observed** (Nsight, 2560x1440) SSAO 0.35 ms, L2-bound, 0.2 ms of it the scattered scene-colour reads;
GPU frame -0.15 ms, the picture within 4 levels. Sky/far-depth edges and
transparent water/particles require explicit exclusion/compositing rules.
`layer-merge`, `fog-half`, `shafts-half`, `layers-lean` combine shafts apply, placed fog and SSAO,
using reduced targets plus depth-aware reconstruction/mask fallback. A texel that does not fit
must use the full-resolution path. [Fog volumes](formats/fogfeatures.md), [shafts](render-shafts.md).
`post-merge` fuses velocity/exposure compute groups while preserving ordering and dependencies.

Fog shading rate (`FogShadingRate`): build tile rates from completed scene depth/fog; frame N's
R8_UINT rate image drives frame N+1 opaque scene shading. Requires device fragment-shading-rate
support; absent support uses normal shading. Excludes reflection, shadow, particles and post.
Coarse rates can alter fine cut-outs and temporal output; it is a quality/performance tradeoff.

## Colour, night and particles

Game HDR exposure is the reference; tone mapping/grading are Meitou controls, not original facts.
Night grading weights scotopic luminance (Jensen2001 night-sky model evaluated at sRGB primaries)
using measured scene luminance/adaptation; daylight contribution must fade to zero. Night red
terrain can be original ambient-map colour rather than lamp/emissive input. Haze follows weather
and sun; Meitou default strength0.5, game1, slider0..3.

Weather particle resolution: off/quarter/half/auto/full; density multiplies emission and changes
appearance/population, not just shading cost. Reduced-resolution particles test against a
checkerboard of each block's nearest/farthest depth, and both the composite and the reactive mask
upsample by depth match; a farthest-only reduction let dust behind thin characters bleed onto
their silhouettes, flickering under jitter. Texture anisotropy caps mipmapped samplers, distinct from texture
resolution/LOD bias. [Particle simulation](formats/particle-universe.md).

## SMAA

Meitou's non-temporal alternative to FXAA (`--smaa`, or anti-aliasing "SMAA" in settings). It runs on the
composite's LDR picture instead of FXAA, before the heat haze, and only when no upscaler runs. Passes
(`PostProcessShaders.Smaa*`): luma edges (threshold 0.1, local contrast adaptation 2); blending weights;
neighbourhood blend. The weights use SMAA 1x's orthogonal L/U/Z revectorization (16-pixel search per side,
U shapes smoothed below 32 pixels). Unlike reference SMAA, it computes areas analytically instead of using
AreaTex/SearchTex. It has no diagonal or corner patterns, as SMAA's low/medium presets.
**Observed**: 0.6 ms at 1280×720 on the low-end laptop; smooths the stair-steps on The Hub's walls and ridges.

## Tried: UI depth stamp (removed)

Writing depth 0 under fully opaque UI (22% of the HUD screen, `UiPainter.OpaqueCoverage`) so the
scene is not shaded there, with the exposure leaving those pixels out and the layers passes
discarding, gave **no measurable gain** (RTX 4070, 1594x896 DLSS, paused fixed view, gpu ms off
6.8/7.0 vs on 6.8-7.3, noise ~0.3 ms). The scene stage is probably not fragment-bound there;
**Unknown:** whether it helps at a higher render resolution, or whether early depth rejection
took effect (the full-screen stamp upper bound was not run). Scene targets hold the picture
upside down, so depth rectangles must be mirrored in Y.

## Validation

**Verified** merged-pass checks: Faithful/FXAA and TAA reference images byte-identical in the
named swamp screenshot test; DLSS/FSR outputs require baseline-noise comparison. Other layer,
mask, half-resolution and fog-rate changes are not blanket pixel-parity claims. Compare still
and moving views at matched time/weather/upscaler. [bench.md](bench.md).
**Limits:** near-only SSAO, screen-space information, temporal disocclusion, coarse fog shading,
and lost GI specular occlusion on resolved surfaces; see feature docs for uncertainty.
