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

## Heat haze (Verified, not reproduced)

`post/heathaze.hlsl`, node `HeatHaze` (settings key `HeatHaze=1`): a screen-space refraction. It perturbs the screen UV by
a flow-map-driven, animated normal-map offset (three layers, scaled 0.002 x `heatHaze` x saturate(6 x depth)), blends two
taps (0.5 and 0.7 of the offset) and reads the finished LDR image. `heatHaze` is a per-biome or weather parameter whose source
is **Unknown**. Our viewer does not implement it.

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

| Stage | `kenshi` preset (default) | Effect available on top | Basis |
| --- | --- | --- | --- |
| Scene buffer | RGBA16F, 4x MSAA | 1, 2 or 8 samples | Kenshi: R11G11B10F, no FSAA |
| Exposure | Kenshi's auto exposure (game sky); x1 with `--simple-sky` | `--exposure` multiplier | Kenshi: 0.55 / adapted, band from CONSTANTS |
| Curve | clamp at 1 | exponential shoulder above 0.8 (identity below) | Kenshi: none (clip) |
| SSAO | off | on (12 taps, half resolution, from depth) | Kenshi: shipped, disabled |
| Bloom | off | on (13-tap mip chain, threshold 1) | Kenshi: magnitude 0 |
| Grading | off | off (optional saturation / contrast) | Kenshi: none |
| FXAA | not implemented: 4x MSAA smooths edges instead | none | Kenshi: FXAA 3.11, 0.75 |
| Dither | off | on | not in Kenshi |
