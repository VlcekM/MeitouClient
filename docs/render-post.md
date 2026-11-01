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
DLSS (the default) where Streamline (Windows) or NGX (Linux) reports it unsupported runs FSR, then TAA. TAA zeroes NaN/Inf
in its inputs and output: GLSL `max` need not drop NaN (AMD keeps it), and one in the history spreads.
For FSR/DLSS the merged velocity kernel zeroes them in the scene colour in place and in the motion
(Observed: the FSR fallback on an RX 9070 XT still flickered without it).

Game/viewer builds and publishes copy the Streamline/DLSS DLLs and FSR's `amd_fidelityfx_vk.dll` that
are present in `MEITOU_DLSS_PATH` (else the older `MEITOU_STREAMLINE_PATH`) or the git-ignored root `dlss-runtime/`;
`-p:StreamlineRuntimePath=` overrides the source. Without them a dev build logs
"not found" and runs TAA (a release folder's DLLs can be copied there). Release packaging fetches its own.

TAA: 3x3 Gaussian exp(-2.29 d^2) with d in display pixels (upscaling doesn't widen it), Catmull-Rom history,
YCoCg variance clip, blend 0.1. Upscaler sharpness: FSR's RCAS; after TAA and DLSS (DLSS ignores it) the
composite pushes each pixel from its 4 neighbours' mean by 2 x sharpness, clamped to their range (no halos;
the history stays unsharpened). Measured (Hub, 1080p, mean |Laplacian|): no AA 16.6, TAA 12.5 before
and 23.1 after (bias -1, display-pixel kernel, sharpen), DLSS 26.6.

Linux runs DLSS through NGX (`Upscalers/Ngx.cs`): NVIDIA's NGX client library (the DLSS SDK's static `libnvsdk_ngx.a`,
built into `libmeitou_ngx.so` by `tools/scripts/build-ngx-shim.sh`) over the driver's `libnvidia-ngx.so.1`, with the DLSS
model `libnvidia-ngx-dlss.so.<version>`; each file is looked for in `MEITOU_DLSS_PATH`, then beside the executable, and
NGX gets the same two folders to find the model. The client library is linked on Ubuntu 22.04 for releases, so it
loads on glibc 2.35 and newer; on an older glibc the game logs the loader's error and runs without DLSS. FSR on Linux needs our build of the FidelityFX library ("Frame generation"). `MEITOU_NGX_LOG=1` prints NGX's whole log (its verbose level).
NGX is loaded only when the model and the client library are in one of those folders and `MEITOU_DLSS` is not `0`, and
handed only a device on NVIDIA's own driver (not NVK); without DLSS (other GPU, no NVIDIA driver, no libffi) it is shut
down at once and the game runs on. Builds copy both to the output folder, never into a publish; the Linux release zip
carries them with NVIDIA's licence (`package-release.ps1`). NGX's data folder is `~/.local/share/meitou/ngx`
(`XDG_DATA_HOME`); its log goes to the game's log, its own log file is off.
**Observed** (driver 615.78.08, DLSS SDK 310.9.16, RTX 4080 SUPER, headless `--screenshot --upscaler dlss`): called straight
from managed code, `NVSDK_NGX_VULKAN_Init_with_ProjectID` aborts (`std::logic_error`, "basic_string::_M_construct null not
valid"), and the same call through libffi (as from Python's ctypes) succeeds, so it goes through libffi; why is **Unknown**
(libffi passes the arguments as a direct call does); the other calls work directly. `NVSDK_NGX_VULKAN_RequiredExtensions`
asks for instance `VK_KHR_get_physical_device_properties2` and device `VK_NVX_binary_import`, `VK_NVX_image_view_handle`,
`VK_EXT_buffer_device_address`, `VK_KHR_push_descriptor`; the device enables buffer device address as the Vulkan 1.2
feature instead of that extension (no Vulkan validation errors from DLSS). Conventions as FSR: jitter in render pixels as is, motion
scaled by −render size; inputs need sampled use, the output storage use, all left in GENERAL.
**Observed** (Linux, DLSS 310.9.16, RTX 4080 SUPER): preset 0 (`MEITOU_DLSS_PRESET` unset) gives the guide's defaults,
K for Quality, M for Performance, L for Ultra Performance: each headless screenshot is byte-identical to the one with
that preset set and differs from K's (repeat runs are identical).

## Frame generation

The `FrameGeneration` setting (off by default, read at start-up) presents through AMD's FSR 3 frame-interpolation swapchain
(`Upscalers/FfxSwapchain.cs`) and interpolates one frame between each two rendered ones (`Upscalers/FrameGeneration.cs`), on top
of TAA, FSR or DLSS. Library: `amd_fidelityfx_vk.dll` (AMD's prebuilt, Windows/Proton) or `libamd_fidelityfx_vk.so` (Linux:
`tools/scripts/build-ffx-linux.sh`, AMD's SDK 1.1.4 source plus `tools/patches/fidelityfx-sdk-1.1.4-linux.patch`; AMD's
swapchain code needs Vulkan headers 1.3.237+, so the script uses Khronos' 1.3.296). Struct layouts and type ids follow the SDK
1.1.4 headers (`ffx_api.h`, `ffx_api_vk.h`, `ffx_framegeneration.h`).

- **Interface:** overlays draw into their own transparent RGBA8 layer (`UiLayer`, premultiplied alpha). Without frame generation it is
  composited onto the world into a top-down final picture that the present copies as is. With it the present shows the world alone
  (flipped) and the layer is registered with AMD's swapchain every frame (it forgets it at each present), flipped top-down, so it is laid
  over generated frames too. Cost of the separate layer: about 0.1 ms GPU (**Observed**, Iris Xe, play bench, frame generation off).
- **Inputs:** depth (`upscaleDepth`, now also made with TAA) and motion are bottom-up, so they are blitted flipped; motion scale
  (-render width, +render height), jitter y negated, view-space-to-metres 0.1 as for the upscalers.
- **Frame id:** steps by 1 per rendered frame; any other step restarts the interpolation (`ffx_framegeneration.h`), so it skips one
  after a cut, after frames without frame generation and on a new context. Off under the loading screen and for screenshots.
- **Generation rect:** left zero, which the SDK's provider turns into the whole display (**Verified**, SDK 1.1.4
  `ffx_provider_framegeneration.cpp`).
- **Layouts:** at the end of the present the replacement back buffer is left in shader-read layout, which AMD's UI composition
  samples (**Observed**: validation VUID-vkCmdDraw-None-09600 otherwise).
- **Teardown:** the frame-generation context is destroyed before the swapchain context, also when a failed swapchain remake falls
  back to the plain swapchain.
- **Queues:** AMD's swapchain takes four queues (game, present, image acquire, async compute); we take them all from the graphics
  family and need at least 3. **Observed** (Iris Xe, Windows, 2026-10-30): the family has 1 queue, so frame generation stays off
  with a log line. Typical AMD and Intel drivers also expose one graphics queue (**Unknown** here: untested); AMD's samples take
  present and async compute from other families.
- **DLSS:** Streamline must see every present, which AMD's swapchain owns, so on Windows with frame generation on DLSS goes through
  NGX (`meitou_ngx.dll`, the SDK's static `nvsdk_ngx_s.lib` linked into a DLL by `package-release.ps1` or
  `tools/scripts/build-ngx-shim-win.sh`) and Streamline is not loaded; without the FidelityFX library Streamline stays.
- **AMD SDK bugs** the validation layer reports, fixed in the Linux patch only (AMD's Windows DLL keeps them): line width 0 in the
  swapchain's pipelines, a descriptor pool size list that drops `STORAGE_BUFFER` (**Observed**, PR #133).
- **Performance** (**Observed**, single runs, RTX 3070, PR #133): about 1.4 ms more per rendered frame (Proton, TAA),
  presented fps 1.5-1.75x that without it; for about 8 s after it starts the present is slow (31-60 fps).

## SSAO, fog and layers

SSAO reads near depth; character mask controls character occlusion (default strength0.25).
`ssao-mask-skip` may omit a mask read only when no character was drawn. The taps read the mask from a half-size R8 copy of the scene colour's
alpha (`SsaoMask`), not the scene colour: **Observed** (Nsight, 2560x1440) SSAO 0.35 ms, L2-bound, 0.2 ms of it the scattered scene-colour reads;
GPU frame -0.15 ms, the picture within 4 levels. Sky/far-depth edges and
transparent water/particles require explicit exclusion/compositing rules. The water writes no depth, so a surface below the
water plane fades out of the occlusion over the water's last 2 units along the ray, as the water's edge does
([water](render-water.md) "Edges"). Passes rebuild view depth from the projection's 0..1 clip depth (`uClipDepthZeroToOne`:
Vulkan stores half of it plus a half, Metal the clip depth); GL's -1..1 formula puts points about twice too far on Vulkan.
SSAO: radius 6 units or 3 % of the distance, fade from 1600 to 6600 units.
`layer-merge`, `fog-half`, `shafts-half`, `layers-lean` combine shafts apply, placed fog and SSAO,
using reduced targets plus depth-aware reconstruction/mask fallback. A texel that does not fit
must use the full-resolution path. [Fog volumes](formats/fogfeatures.md), [shafts](render-shafts.md).
`post-merge` fuses velocity/exposure compute groups while preserving ordering and dependencies.

## Fog

The `fog` switch (`PostOptions.MeitouFog`) changes the particle sprites only; the haze and weather fog are [sky](formats/sky.md)'s,
the fog volumes [fogfeatures](formats/fogfeatures.md)'s (their half size is `fog-half`, above, whatever the switch).
The game's sprites: **Verified** (the install's `materials/forward/basic.hlsl` `basic_fp`, `particles.hlsl`, the particle
materials): its particle materials use `Basic_Texture_FP` / `Basic_Texture_Clipped_FP_HLSL`, texture times colour times vertex
colour, depth-tested with no fade, except `Kenshi_mist_cloud` (the `fogislands`, `islandfog`, `Kenshi_Mist_Cloud`,
`Kenshi_Rising_Steam(_slow)` and `SkyBeam` scripts), whose `Particle_Blend_Depth` scales alpha by `saturate(w / 10)` near the eye and by
`saturate((scene - 100 - w) / 100)` before a surface (gbuffer depth; 0 within 100 units, full from 200). Faithful draws every
sprite hard-edged, so those mist sprites are not the game's yet (**Unknown** how visible). The game draws fog volumes and particles
into the full-size HDR target (**Verified**, `materials/compositors/main.compositor` `Lighting_HDR`, queues 82 to 85).
Meitou fades each sprite over its last stretch before the surface behind it: a quarter of its larger
side, 2 to 300 units (0.2 to 30 m), on alpha (alpha sprites), colour (additive) or towards white (modulate); the reactive mask
fades the same. A draw cannot read the depth it tests against, so the fade reads a colour copy: an R32F copy of the scene depth
for full-size sprites (`PrepareSoftParticleDepth`, only when some are drawn) and, for the low-resolution ones, the depth pass's
checkerboard written to an R32F target beside it.
**Observed** (the Eye's storm, quarter-size particles): the row of dark beads where the dust meets a dune ridge is fainter.
The Metal chain has no copy: its sprites stay hard-edged.

Fog shading rate (`FogShadingRate`): build tile rates from completed scene depth/fog; frame N's
R8_UINT rate image drives frame N+1 opaque scene shading. Requires device fragment-shading-rate
support; absent support uses normal shading. Excludes reflection, shadow, particles and post.
Coarse rates can alter fine cut-outs and temporal output; it is a quality/performance tradeoff.

## Colour, night and particles

Game HDR exposure is the reference; tone mapping/grading are Meitou controls, not original facts.
Both auto-exposure ping-pong targets are seeded before smoothing starts, and the adaptation reads
the previous frame's output rather than the target it is writing; either mistake flickers dark/bright.
Night grading weights scotopic luminance (Jensen2001 night-sky model evaluated at sRGB primaries)
using measured scene luminance/adaptation; daylight contribution must fade to zero. Night red
terrain can be original ambient-map colour rather than lamp/emissive input. Haze follows weather
and sun; Meitou default strength0.5, game1, slider0..3.

Weather particle resolution: off/quarter/half/auto/full; density multiplies emission and changes
appearance/population, not just shading cost. Reduced-resolution particles test against a
checkerboard of each block's nearest/farthest depth, and both the composite and the reactive mask
upsample by depth match; a farthest-only reduction let dust behind thin characters bleed onto
their silhouettes, flickering under jitter. Where the four nearest texels span more than 4% in depth, the upsample is a joint
bilateral filter over the 4x4 texels round the pixel (radial kernel to 2 texels, times the depth match): the four alone gave
one or two matching texels in turn along an edge, a row of beads two texels apart (**Observed**, the Eye's storm at quarter
size: the beads gone, a smooth band; elsewhere the picture within 12/255). With a vendor upscaler (DLSS, FSR) the low-resolution particles are
blended over its output instead of the scene it upscales (`PostProcess.ParticlesAfterUpscale`, half then quarter size, the composite reading
the render-size depth at `uScale` render pixels per output pixel), drawn with the unjittered projection, and left out of the reactive mask:
DLSS took drifting see-through dust as standing still (its default preset ignores the reactive mask) and trailed it in rows of streaks
(motion vectors for the dust left about half of them). Over the unjittered output the pixel's depth for the depth match is the nearest
of the 2 x 2 render texels round it (`uFootprint`), not the one nearest jittered texel; **Unknown** whether that stops dust edges
shimmering against near silhouettes (not yet looked at). A vendor upscaler that fails that frame gets them in the scene before the TAA. **Observed** (a Hub
storm, DLSS): the dust the same as blended in the scene (mean difference 1.2/255, the dust's shapes only). TAA keeps them in the scene: its
output is its next history. Texture anisotropy caps mipmapped samplers, distinct from texture
resolution/LOD bias. [Particle simulation](formats/particle-universe.md).

Particle coverage (FSR's reactive mask, DLSS's bias hint, TAA's motion alpha): alpha, or brightness times alpha for additive
sprites, depth-tested. Rain (`CAMERA_RAIN`/`CAMERA_ACID_RAIN`) counts 30x, saturating; fire (placed effects' additive draws)
as it is, capped at 0.9 (AMD's guidance); the rest as it is. Rain and fire stay full size at every particle setting.
**Observed** (FSR and TAA at 1080p): 30x on every sprite leaves the scene behind dust grainy under TAA and blurred under FSR;
without its gain rain's streaks fade; a distant torch a few pixels high flickers at 30x.

## Bloom

An optional effect (`PostOptions.Bloom`, Off | On), off in the Meitou look as in the game (magnitude 0), with its strength under it, 0..0.25 (0.13 by
default; KenshiNative's 0.04); the example look "Hot and dry" turns it on. `--bloom <s>` sets the strength and turns it on for one run.
Port of KenshiNative's `docs/features/bloom-rewrite.md`, which has the derivation and its checks.
`PostProcess.Bloom.cs` runs after the upscaler on the picture the composite reads; no history.
The composite mixes, `lerp(scene, psf ⊗ scene, strength)`, in linear light before exposure, so a
uniform image is unchanged. The scene is gamma-encoded (no sRGB step), so the prefilter decodes
each texel (power 2.2) before a 4x4 box to 1/2, with a firefly ceiling of 64 linear units
(`c (1 + (L/F)^4)^-1/4`). Then Jimenez 13-tap downs to 1/128 (stops before a side drops under 4)
and 3x3 tent ups `U_i = w_i L_i + tent(U_i+1)` into the 1/4 level. Weights for levels 2..7:
0.41405, 0.21190, 0.10986, 0.10064, 0.04074, 0.12281 (sum 1; a missing level's weight goes to the
coarsest). They are KenshiNative's fit to the eye's photopic PSF (Spencer et al. 1995) at a 45°
fovY. The composite reads the 1/4 level through a 4-tap cubic B-spline (no square around small lights).
R11G11B10 targets. **Observed** (Hub, 02:00, 1600x900, DLSS): a round halo centred on the lamp
at 0.25, frame mean linear luminance within 2.5% of off; GPU 0.07 ms on the run's average.

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
