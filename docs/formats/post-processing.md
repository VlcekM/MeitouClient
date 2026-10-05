# Post-processing

What Kenshi does to the lit scene before it reaches the screen, and what `meitou-viewer --world` does
([viewer.md](../viewer.md#post-processing)). Sources are the shipped scripts in `data/materials/compositors/` and
`data/materials/post/` (read for facts only; none of their code is used), `settings.cfg`, `kenshi.cfg` and the
strings of `kenshi_x64.exe`. Labels as in [../README.md](../README.md).

## Pipeline order

**Verified** (`compositors/workspace.compositor`, `main.compositor`, `compositors.cfg`): the `Kenshi_Main` workspace is a
deferred renderer with these nodes, in this order:

1. `Water_Reflection`: 512x512 `PF_A8R8G8B8` reflection texture (a second camera, everything up to render queue 60).
2. `GBuffer`: queues 20 to 80 into three render targets (two `A8R8G8B8`, one `FLOAT32_R` depth), no FSAA; cleared to
   (0, 0.5, 0, 1) (the Deferred clear colour #008000, "resolves to black"). The G-buffer's normals and depth feed SSAO and heat haze.
3. `Lighting_HDR`: **`rt_hdr` is `PF_R11G11B10_FLOAT`** at the target size. Sky and moons (queues 1 to 10), then the custom
   `MainLight` pass (the deferred lighting), water (81 to 82), a full-screen `AtmosphereFogMaterial` quad, then fog volumes
   and particles (82 to 85).
4. `Resolve_HDR`: luminance measure, adaptation, bright-pass, bloom blur, then **`HDR/Composite`** into `rt_input`
   (exposure, bloom, an optional gamma step; see below).
5. Extra nodes listed in `compositors/compositors.cfg`, in order, each reading the previous output: `FXAA`, `HeatHaze`
   (`#SSAO` and `#RTWDebug` are commented out). They run on `A8R8G8B8` buffers, i.e. after the HDR composite, on LDR.
6. `Debug` (queues 87 to 89) and the GUI.

The scene is lit in HDR, so brightness is not clamped until the composite writes the LDR result.

## Exposure and tone mapping (Verified)

`post/hdrfp4.hlsl` + `hdrutils.hlsl` + `hdr.material`. Parameters (`shared_params HDRSettings`, comment in the file: "attached
to sliders"):

| Parameter | Default |
| --- | --- |
| `EXPOSURE_KEY` | 0.55 |
| `MIN_LUMINANCE` / `MAX_LUMINANCE` | 0.8 / 0.96 (script defaults; the exe overrides both, below) |
| `AUTOEXP_ADAPTATION_RATE` | 0.5 |
| `BLOOM_THRESHOLD` | 6.0 |
| `BLOOM_MAGNITUDE` | **0.0** |
| `BLOOM_BLUR_SIGMA` | 0.8 |

- **Average luminance**: Rec. 601 weights (0.299, 0.587, 0.114), floored at 0.0001; the HDR image is reduced by a 2x2
  luminance downsample to 128x128 and 3x3 box downsamples through 64, 16, 4 down to 1x1, **in linear space** (a plain average,
  not a log average, although the value is stored as `log` in the 1x1 target).
- **The band at run time** (**Verified**, `kenshi_x64.exe` sky creation and sky update, 2026-10-05): the exe overwrites the
  script defaults. `MAX_LUMINANCE` = CONSTANTS `exposure max`, set once; `MIN_LUMINANCE` = `exposure min · lerp(night darkness, 1,
  saturate(5 · sunY))`, set every frame (sunY the sun's height). With GLOBAL CONSTANTS' 0.8 / 1.2 / 0.35 (after all mods; the same in the base game's
  own load order) the day band is **[0.8, 1.2]** and the night's floor drops to 0.28. Details: [lighting.md](lighting.md).
- **Adaptation**: `adapted = last + (current - last) * (1 - exp(-frameTime * rate))` with rate 0.5 (a time constant of about 2 s),
  then **clamped to [MIN_LUMINANCE, MAX_LUMINANCE]**.
- **Exposure**: `scale = max(EXPOSURE_KEY / adapted, 0.001)`: by day 0.55 / 0.8 to 0.55 / 1.2 = **x0.69 down to x0.46**, at
  night up to 0.55 / 0.28 = x1.96. A day scene of mean luminance under 0.8 sits at x0.6875; a bright one (a sunlit desert
  floor) darkens towards x0.46. The earlier reading of this section (a band fixed at [0.8, 0.96], a nearly constant gain) took the
  script defaults for the run-time values.
- **Tone-mapping curve: none.** `ToneMap()` is exposure only. A Hable-style filmic function (`ToneMapFilmicALU`, constants
  `a`, `b`) is present in the file but its call is commented out. Values over 1 are clipped by the LDR target.
- **Gamma**: `hdr_to_gamma = false` and `hdr_disable = false` (constants in `common/constants.hlsl`); `kenshi.cfg` has
  `sRGB Gamma Conversion=No`. So there is no `pow(1/2.2)` and no sRGB conversion anywhere in the chain: lighting is done in
  the same non-linear "display-referred" space as the textures.
- `hdr.hlsl` (the older `COLOR`-semantics variant with `BRIGHT_LIMITER`) is not referenced by `hdr.material`
  (it uses `hdrfp4.hlsl`): **Observed** dead code.

## Bloom (Verified code, Observed that it is off)

- The "bright pass" is not a threshold. It is the 3x3-downsampled scene (at quarter size, `R11G11B10_FLOAT`) multiplied by
  `2^(exposure - BLOOM_THRESHOLD)`, i.e. exposure scale / 64 with the default threshold 6, so it only passes the very brightest
  HDR values. It is blurred with a 12-tap Gaussian (separable, sigma 0.8, `BloomBlurV` then `H`, at quarter size) and
  **added** after exposure, scaled by `BLOOM_MAGNITUDE`.
- **`BLOOM_MAGNITUDE` defaults to 0.0**, and `settings.cfg` / `kenshi.cfg` have no key for it, so bloom is off in a stock install
  (**Observed**: the string "Bloom" exists in `kenshi_x64.exe`, which suggests a developer or mod slider; its UI is **Unknown**.
  "exposure min" / "exposure max" are the CONSTANTS fields that set the luminance band, above).

## FXAA (Verified)

`post/FXAA.hlsl` includes `Fxaa3_11.h` (Timothy Lottes' FXAA 3.11, PC/HLSL4): quality preset 12, **green as luma**
(`FXAA_GREEN_AS_LUMA`), `fxaaSubpix` 0.75, `fxaaEdgeThreshold` 0.166, `fxaaEdgeThresholdMin` 0.0833. The input is bilinear with
border addressing, so FXAA runs on the final LDR image. `settings.cfg` `FXAA=1` is the toggle (the options screen has "FXAA" and
"HeatHaze" entries: `options.cpp` messages in `locale/en_GB/LC_MESSAGES/main.pot`). `kenshi.cfg` has `FSAA=1`: no hardware
multisampling at all.

## SSAO (Verified shipped, Verified disabled)

`post/AO.hlsl` + `SSAO_Test.material`: 16 Poisson-disc taps, a screen-space radius `filterRadius` (0.001, 0.001) in
texture units, `distanceThreshold` 0.5, depth from the G-buffer (view-space position rebuilt along camera corner rays), the
stored world normal rotated into view space. Occlusion = mean of `max(dot(n, dir), 0) * (1 - smoothstep(thr, 2 thr, distance))`;
the result **multiplies the finished (LDR) scene**. The node is **commented out in `compositors.cfg`**, and there is no
settings key for it: not part of the shipped look (the material is called `SSAO_Test`).

## Heat haze (Verified)

A screen-space shimmer over the finished LDR picture. Sources: `post/heathaze.hlsl` and `post/heathaze.compositor` (the
material), `compositors/post.compositor` (the node), `compositors/compositors.cfg` (the order), `common/common.program`
(`SharedSkyParams`), the `deferred/*.hlsl` G-buffer writers, and in `kenshi_x64.exe` the compositor setup FUN_1403ea7b0 /
FUN_140814500 / FUN_140813870, the options screen FUN_1403f0260, the settings writer FUN_1403eca90, the default texture
filtering FUN_140815b40, the sky update FUN_14066f190 (`gameTime`) and the weather manager FUN_1409e8f70 / FUN_1409ea410
(`heatHaze`, [weather.md](weather.md#heat-haze-verified-decompiled-fun_1409e8f70-fun_1409ea410)).

**Place in the chain** (**Verified**, the scripts): node `HeatHaze` in `post.compositor` draws one full-screen quad with material
`HeatHaze`, image input = the previous node's output (`rt_input`), and input 2 = `global_gbuffer` target 2 (the `FLOAT32_R` depth).
`compositors.cfg` lists `FXAA` then `HeatHaze`, so the haze reads FXAA's result and is the last post effect before the GUI. (A
`compositor HeatHaze` block at the end of `heathaze.compositor` with an R11G11B10 `rt_full` is commented out: dead.)

**Setting** (**Verified (decompiled)**): every node named in `compositors.cfg` (lines starting `#` or `;` are skipped; a mod's
copy is merged with the base list) is switched on or off by a `settings.cfg` line `<node name>=<bool>` read in FUN_1403ea7b0;
**without such a line the node is on**. The flag is stored on the node definition (FUN_140813870 copies it, FUN_140814500 checks
the node exists and has two outputs and logs "Post processing compositior ... is invalid" otherwise). The options screen
(FUN_1403f0260) adds one checkbox per listed node, so `FXAA` and `HeatHaze` appear there by their node names, and FUN_1403eca90
writes them back. The shipped `settings.cfg` has `HeatHaze=1`. The character editor's workspace sets `heatHaze` to 0
(FUN_1405f4dd0).

**Inputs** (**Verified**, `heathaze.compositor`):

| Unit | Texture | Sampling |
| --- | --- | --- |
| 0 `flow` | `materials/FlowHAZE.dds` (2048², BC1, 12 mips) | not set in the material: Ogre's default, which the game sets to **anisotropic, anisotropy 16** (FUN_140815b40: `MaterialManager::setDefaultTextureFiltering(TFO_ANISOTROPIC)` through its vtable slot 37, then `setDefaultAnisotropy(16)`; **Verified (decompiled)**, slot checked against OgreMain's RTTI table), wrap addressing (Ogre's default; **Observed**) |
| 1 `perturbation` | `materials/Perturber.dds` (2048², BC1, 12 mips; a tangent-space normal map) | the same default |
| 2 `depth` | G-buffer target 2 | `filtering none`, clamp |
| 3 `base` | the previous node's picture | `filtering bilinear`, clamp |

Uniforms: `gameTime` and `heatHaze`, both from `SharedSkyParams`. `gameTime` is the sky controller's total game hours minus
the value at load (FUN_14066f190; [game-loop.md](../game/game-loop.md)), so it stops when the game is paused.

**The shader** (**Verified**, read for facts; screen coordinates are the D3D quad's, v from the top):

- Flow: `direction = flow(uv · 3.341).rg · 2 − 1` (not normalised; the flow map tiles 3.341 times across the screen).
- Three layers of the normal map at `uv · 7.341`, the second and third offset by (0.1, 0.3) and (0.4, 0.7) in texture units. Layer
  *k* has the phase `t = frac(gameTime · 100 + {0, 0.33, 0.66})`, samples at `+ direction · t` (speed 1) and is weighted by the
  triangle `1 − |2t − 1|` (0 at the jump, 1 half way), so the scroll resets unseen. One cycle is 0.01 game hours = 36 game
  seconds = **1.09 real seconds at game speed 1** (0.22 s at speed 5; [game-loop.md](../game/game-loop.md) for the clock rate).
- The layers' `rgb · 2 − 1` are swizzled `xzy` and added to (0, 1, 0); then the middle component is zeroed, so only the sum of the
  maps' **red** and **green** remains, and it is normalised. **The offset is a unit direction**: its length does not depend on the
  maps, only its direction does. (A zero sum would normalise a zero vector, undefined; the maps make that practically impossible.)
- Length: `0.002 · heatHaze · saturate(6 · depth)` in screen units (0.2 % of the width and of the height, so 3.2 × 1.8 pixels at
  1600 × 900 with `heatHaze` 1), where `depth` is the G-buffer's: **distance from the eye / `farClip`** (`writeDepth(length(worldPos
  − cameraPos) / farClip)` in every deferred shader; `farClip` = 50000 by default, the camera's far plane, [sky.md](sky.md)), with 0
  (nothing drawn: the sky, the clear value) replaced by 1. So the shimmer grows linearly up to full at **8333 units** and is full on
  the sky. Water and particles are forward-drawn and not in the G-buffer, so over water the amplitude is that of the ground beneath
  (or full where none was drawn) (**Observed**, from the pass order).
- Output: `mix(image(uv + o), image(uv + 0.7 · o), 0.5)`: the mean of the taps at **1 and 0.7** of the offset (the earlier reading
  here, "0.5 and 0.7", took the blend weight for a tap), bilinear and clamped to the edge.

`heatHaze` itself: [weather.md](weather.md#heat-haze-verified-decompiled-fun_1409e8f70-fun_1409ea410) (the camera region's
weather's `heat haze` × its strength × the sun factor, moving at 1/3 per second); which weathers and regions have it is listed
there.

**The viewer** (`PostProcess.RunHeatHaze`, `PostProcessShaders.HeatHaze`, written from the facts above): the same pass with the
two maps loaded from the install at start (BC1 uploaded with the files' own 12 mips; trilinear, anisotropy 16, repeat), run last,
after FXAA (when no temporal upscaler runs) or after the composite (with TAA / FSR / DLSS: on the upscaled, exposed LDR picture,
the game's input; before the upscaler its history would reject or smear the shimmer, and the game has no such stage). Depth: the
near depth slice's depth buffer, linearised and turned into the distance along the view ray, over D (`--haze-distance`, 50000);
beyond the near slice (20000+) and on the sky the amplitude is 1, as in the game (8333 < 20000, so the clear hides no ramp). The
lookups use the game's screen orientation (v from the top), the offset is flipped back. Differences: a zero direction sum leaves
the pixel in place, and the viewer's water writes depth, so over water the amplitude follows the water surface's distance, not the
sea floor's. When `heatHaze` is 0 (or `--no-heat-haze`) the pass is skipped, which equals the game's
pass at 0 (both taps then land on texel centres). Options and costs: [viewer.md](../viewer.md#post-processing).

## Absent (Verified against everything in `data/materials/post` and `compositors`)

No colour grading and no LUT in the shipped chain (`kelvin_to_scale_lut.png` is read by `Composite` only when the constant
`debug_lit_temperature` is true, which it is not; `exposure_key_lut.png` is commented out). No depth of field, film grain,
vignette, sharpening, motion blur or lens flare. `fog.hlsl` / `atmospherefog.hlsl` are scene fog and sky scattering, not post
effects (see [terrain.md](terrain.md#atmosphere-verified-constants-observed-shader)). Dithering: none.

## What the viewer does

In game-sky mode (the default) the viewer's scene shaders light in the game's HDR units ([lighting.md](lighting.md)), so the
composite applies Kenshi's exposure: the scene's mean Rec. 601 luminance (a 4×4-tap reduction into a 256² R32F target, then its
mip chain to 1×1: a plain linear mean like the game's, by a different reduction), adapted at rate 0.5 per second, clamped
to this frame's `[MIN_LUMINANCE, MAX_LUMINANCE]` from the CONSTANTS record, and `0.55 / adapted` multiplies the image
(`PostProcess.AutoExposure`). Screenshots adapt at once (`InstantAdaptation`) and print the mean, the adapted value and the
scale. `--exposure` multiplies on top (default 1). With `--simple-sky` the old display-referred shaders are back and the
exposure is the constant `--exposure` alone. Nothing is clamped before the composite (RGBA16F scene buffer).
The band is the game's at every camera height; the viewer adds no guard of its own. The white overview shots from far above the
game's camera heights were not the exposure's doing: the game's haze formula gave values in the thousands there (a mean luminance
of 715, the band then holds the scale at its floor ×0.46), fixed at the source ([sky.md](sky.md#haze-distance-fog-how-vanilla-does-it));
the same views now measure 0.65 to 0.88 by day and stay finite at dawn, sunset, dusk and night.
Details, options and costs: [viewer.md](../viewer.md#post-processing).

| Stage | Faithful (`--post kenshi`, `--faithful all`) | Meitou (the default) | Basis |
| --- | --- | --- | --- |
| Scene buffer | RGBA16F, single-sample | none | Kenshi: R11G11B10F, no FSAA |
| Exposure | Kenshi's auto exposure (game sky); x1 with `--simple-sky` | `--exposure` multiplier | Kenshi: 0.55 / adapted, band from CONSTANTS |
| Curve | clamp at 1 | none (removed) | Kenshi: none (clip) |
| SSAO | off | on (12 taps, half resolution, from depth) | Kenshi: shipped, disabled |
| Bloom | off | none (removed) | Kenshi: magnitude 0 |
| Grading, vignette | none | none (removed) | Kenshi: none |
| FXAA | FXAA 3.11 quality, green as luma, subpix 0.75, thresholds 0.166 / 0.0833, preset-12 search steps, on the LDR composite (Faithful anti-aliasing; `--no-fxaa`) | TAA, FSR or DLSS instead (Meitou anti-aliasing) | Kenshi: FXAA 3.11, 0.75 |
| Dither | off | on | not in Kenshi |
| Heat haze | the game's, after FXAA (`--no-heat-haze`; `--heat-haze <x>` replaces the weather's field) | the same, after the composite when a temporal upscaler runs | Kenshi: `HeatHaze` node, on by default |
