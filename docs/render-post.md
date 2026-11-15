# Post-processing

`PostProcess*.cs`, `PostProcessShaders`, upscalers and `WorldFrame` own the HDR chain.
Original exposure/FXAA/haze rules: [formats/post-processing.md](formats/post-processing.md),
[sky](formats/sky.md), [weather](formats/weather.md). UI/CLI controls: [viewer.md](viewer.md).

## Frame contract

Scene shaders write unclamped HDR. Colour/depth/motion targets belong to PostProcess; scenes
must preserve requested attachments and the scene depth's history (one depth, [renderer-native](renderer-native.md) "Depth"). Scene rendering is at render
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

## Frame generation

The `FrameGeneration` setting (off by default, read at start-up) picks whose by `FrameGenerationVendor` (FSR 3 by default, or DLSS: "DLSS frame
generation" below). FSR 3 presents through AMD's frame-interpolation swapchain
(`Upscalers/FfxSwapchain.cs`) and interpolates one frame between each two rendered ones (`Upscalers/FrameGeneration.cs`), on top
of TAA, FSR or DLSS. Library: `amd_fidelityfx_vk.dll` (AMD's prebuilt, Windows/Proton) or `libamd_fidelityfx_vk.so` (Linux:
`tools/scripts/build-ffx-linux.sh`, AMD's SDK 1.1.4 source plus `tools/patches/fidelityfx-sdk-1.1.4-linux.patch`; AMD's
swapchain code needs Vulkan headers 1.3.237+, so the script uses Khronos' 1.3.296). Struct layouts and type ids follow the SDK
1.1.4 headers (`ffx_api.h`, `ffx_api_vk.h`, `ffx_framegeneration.h`).

- **Inputs:** depth (`upscaleDepth`, now also made with TAA) and motion are bottom-up, so they are blitted flipped; motion scale
  (-render width, +render height), jitter y negated, view-space-to-metres 0.1 as for the upscalers.
- **Frame id:** steps by 1 per rendered frame; any other step restarts the interpolation (`ffx_framegeneration.h`), so it skips one
  after a cut, after frames without frame generation and on a new context. Off under the loading screen and for screenshots.
- **Generation rect:** left zero, which the SDK's provider turns into the whole display (**Verified**, SDK 1.1.4
  `ffx_provider_framegeneration.cpp`).
- **Teardown:** the frame-generation context is destroyed before the swapchain context, also when a failed swapchain remake falls
  back to the plain swapchain.
- **DLSS:** Streamline must see every present, which AMD's swapchain owns, so on Windows with frame generation on DLSS goes through
  NGX (`meitou_ngx.dll`, the SDK's static `nvsdk_ngx_s.lib` linked into a DLL by `package-release.ps1` or
  `tools/scripts/build-ngx-shim-win.sh`) and Streamline is not loaded; without the FidelityFX library Streamline stays.

### DLSS frame generation

NVIDIA's DLSS-G through Streamline 2.14.16 (Windows; `Upscalers/DlssFrameGeneration.cs`, guide: Streamline's `ProgrammingGuideDLSS_G.md`,
`ProgrammingGuideManualHooking.md`, `ProgrammingGuideReflex.md`). One generated frame per rendered one: multi-frame generation is D3D12 only.
- **Loading:** with the switch on and DLSS as the vendor (read at start-up; a change in the settings waits for a restart) `slInit` loads DLSS, DLSS-G, Reflex and PCL (`sl.dlss_g.dll`, `nvngx_dlssg.dll`, `sl.reflex.dll`, `sl.pcl.dll` beside
  the interposer; missing ones are logged and frame generation stays off). Each feature's `slGetFeatureRequirements` is unioned: extensions,
  1.2/1.3 features, graphics and compute queues (created after ours in the graphics family; `VulkanInfo` gets the first index after ours). Where
  `slIsFeatureSupported` fails for DLSS-G, Reflex or PCL, DLSS-G is unloaded (`slSetFeatureLoaded`): loaded, it makes the swapchain render off-screen
  even while off.
- **Hooks** (manual hooking, `sl_hooks.h`): swapchain create/destroy/images/acquire, present and `vkDeviceWaitIdle` go through the interposer's
  proxies; Streamline owns the real swapchain and presents from its own queue. Its `vkCreateWin32SurfaceKHR` hook records the window behind a
  surface and calls through an instance table only `slSetVulkanInfo` fills (**Verified**, 2.14.16 `sl.interposer/vulkan/wrapper.cpp`; headless
  probe: an access violation before, a surface after), so the window's surface is made again through it once the device is handed over and
  replaces the first one (`VulkanDevice.ReplaceSurface`).
- **Frame:** one frame token per rendered frame (`Streamline.BeginFrame` at the top of the game loop: `slReflexSleep`, then PCL markers simulation
  start/end, render submit start/end, present start/end); DLSS upscaling uses the same token. Reflex low latency is on while DLSS-G is loaded.
- **Inputs** (all `eValidUntilPresent`): depth and motion copied flipped at the render size; the backbuffer before the interface (hudless) and
  the interface layer (premultiplied RGBA8, `UIColorAndAlpha`) copied flipped at the display size. The window shows the finished picture.
  User interface recomposition on; `eRetainResourcesWhenOff`; mode off under the loading screen and for screenshots.
- **State:** `slDLSSGGetState` about once a second; status failures (resolution too low, Reflex not detected, constants invalid ...) are logged
  when they change. `numFramesActuallyPresented` is the frames presented for the last rendered one (1 = none generated, 2 = one), not a count
  since the previous call; the stats panel shows it as `x2 shown` beside the rendered rate.
- **Pointer:** with any frame generation the game's pointer is the window's cursor (its image cropped from its sheet at the drawn size, hot spot
  on the mouse; `GameHost.SystemPointer`), not drawn in the interface: there it reaches the screen a held-back frame late and is interpolated.

## Contact shadows

The `contact` switch (`PostOptions.ContactShadows`, Meitou, off by default: what the screen does not show casts nothing; never with the sun shadows Off or
traced, whose rays hold them): short sun shadows from the scene depth at the render size, for what the cascades' texels are too coarse for (armour plates,
straps, a boot on the ground, the ground's own relief). One full-screen pass (`PostProcessShaders.Contact`):

- The march (`contactHit`): the ray to the sun is walked on the screen a pixel at a time, at most 48 steps (two pixels a step past 48 pixels; cut short past
  96), over at most 8 units, its depth exact at each pixel (1 / depth is linear on the screen). No noise: the same picture every frame. What hides the ray
  stands above the point's own plane by two pixels' size (the normal rebuilt from the depth), so a surface never shadows itself at any view angle, and has the
  ray within 0.6 units (+1 % of the view depth) behind it, so a near object does not shadow all that lies far behind it on the screen. The shadow fades over
  the last 40 % of the reach, and from 1500 to 2500 units from the eye. Only where the rebuilt normal faces the sun.
- With the GI resolve (`PostProcess.ContactSun`): the lit fragments write their sun term, cascades included, apart (location 2, the traced sun's circuit;
  `uGiParams.z` bit 8, `GiProbes.SunApart`), and the pass, run over the opaque scene before the water (`RunContactSun`), adds it back times what the march
  leaves, through the haze. The contact shadows then take the sun alone: nothing of the ambient, the lamps or the bounced light, and nothing in cascade shadow.
- Without it: the pass multiplies the scene colour (`ApplyContactShadows`, after the occlusion) by `1 - hit x the sun's share of the surface's light`
  (`sun / (sun + ambient)`, with the cascades and cloud shadow in the sun term), with the occlusion's air and water visibility.

## HDR output

### GTAO

The `gtao` switch (`PostOptions.Gtao`, off by default, off in every preset) replaces the SSAO gather while `ao` is on; with it off `Ssao` runs unchanged.
Port of KenshiNative's `fx_gtao_ps` (Jimenez et al. 2016), adapted to a forward scene. Own programs (`GtaoShaders`, `PostProcess.Gtao.cs`); Vulkan only, the Metal chain ignores the switch.

- **Normals**: the lit fragment's shading normal from the surface target when the GI resolve is on, for the fragments that wrote it (terrain, objects, buildings);
  else from depth (foliage, grass, characters, impostors, or no GI resolve).
- **Same rules as SSAO**: character occluder share and mask, `SsaoBlur`, distance fade, air and water visibility, 1 on the sky.
- **Apply** (`Apply`): full size, after the merged layers: the merged pass's factor is grey, so `layer-merge` runs with its occlusion off and this pass multiplies over it,
  which is the old order (shafts, fog, occlusion). Four nearest occlusion texels by depth, none near: open. Then KenshiNative's curve (power 1.6, strength 0.55) and the multi-bounce fit
  per channel of the surface target's albedo (`GtaoAlbedo` where none was written). A caller without `ApplySsao` gets the grey blurred occlusion in the composite.
- **Quality** (`--gtao-quality`, Tab slider): 0 half size, 2 x 4; 1 half size, 3 x 6 (default); 2 render size, 3 x 6. Full size remakes the occlusion's targets at that size.
  With a temporal upscaler the noise moves per frame.

## Fog

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

## Camera effects order

Upscaler, depth of field, exposure, bloom, composite. A lens acts on the light before the sensor responds, so the depth of field runs on the
HDR picture and comes before the exposure's measure, which then reads the frame the player sees. Bloom is the glare of what reached the sensor, so
it comes after, and a defocused light keeps its glow.
Each writes its own target: the upscaler's output is the next frame's history. The world's overlays (UI, panels) draw later in their own layer.

It runs only in a whole view of a scene (`PostOptions.CameraEffectsHeld`, `ViewWindow`), in the free camera, on a camera the cinema holds, with its editor (F6) open
or recording (`PostOptions.CinematicView`, set per frame by the game), never over the title. "Camera effects in the strategy camera" (`CameraEffectsEverywhere`, off)
adds that camera. Vulkan only; Metal ignores the options.

## Depth of field

Optional (`dof`, `PostOptions.DepthOfField`, off in every look). Port of KenshiNative's `docs/features/dof-rewrite.md`, without its sparkle, `PostProcess.DepthOfField.cs`:
at the picture's size after the upscaler, in linear light, no history. Half-size prepare (RGBA16F, CoC in alpha, the nearest depth of four), near CoC tiles and dilation, far
gather, near gather (premultiplied), full-size composite: far field by the full-size CoC with a CoC-weighted upsample, near field over it. Four rings at most.

The CoC is a thin lens's, not a slider's radius (KenshiNative's). A full-frame sensor, 24 mm high (`ThinLens`), and the camera's vertical field of view give the focal length f;
with f-number N and focus s, the disc's diameter on the sensor at depth z is `f^2 / (N (s - f)) * |z - s| / z`, the sky at infinity, in half-size pixels at most 1.5 % of the
height and 24. 10 world units are a metre (see "Fog" above). Radius at infinity at 1080p and f/1, half-size pixels:

| lens | focus 5 m | focus 20 m |
|---|---|---|
| 50° (25.7 mm, the default lens) | 1.5 | 0.4 |
| 20° (68 mm) | 10.5 | 2.6 |

So at the default lens a far focus is nearly sharp, as a real lens would be: the blur asks for a long lens, a near focus or a wide aperture (`Depth of field aperture`, f/0.25 to
f/22, 1 by default; below about f/1 no real lens goes).

## Validation

**Verified** merged-pass checks: Faithful/FXAA and TAA reference images byte-identical in the
named swamp screenshot test; DLSS/FSR outputs require baseline-noise comparison. Other layer,
mask, half-resolution and fog-rate changes are not blanket pixel-parity claims. Compare still
and moving views at matched time/weather/upscaler. [bench.md](bench.md).
**Limits:** screen-space information, temporal disocclusion, coarse fog shading,
and lost GI specular occlusion on resolved surfaces without `ssr`; see feature docs for uncertainty.
