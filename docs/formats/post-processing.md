# Original post-processing

Source: compositors/workspace/main/post files, post HDR/FXAA/haze shaders and exe settings/sky
writers. Tables/expression scopes retained. Engine alternatives: [render-post](../render-post.md).

## Chain and exposure

**Verified** reflection512² A8R8G8B8 → three-target GBuffer → R11G11B10 HDR lighting
(sky/moons,lighting,water,haze,fog/particles) → luminance/adaptation/bloom/composite → FXAA →
HeatHaze → debug/GUI. HDR unclamped until LDR composite. SSAO/RTW-debug nodes commented out.
Linear mean luminance Rec601 weights0.299/0.587/0.114, floor0.0001; downsample128→64→16→4→1,
not log mean despite final log storage. Adapt=last+(current-last)*(1-exp(-dt*0.5)), then bound.
Runtime min=exposure-min*lerp(night-darkness,1,saturate(5sunY)), max=exposure-max;
base day0.8..1.2, night floor0.28. Scale=max(0.55/adapt,0.001). No filmic curve/gamma/sRGB step.
Bloom magnitude0; shipped blur code is not evidence bloom is active.

## Heat haze

**Verified** last post effect reads FXAA output and FLOAT32_R scene depth, flow FlowHAZE.dds,
Perturber.dds, gameTime and weather heatHaze. Node setting defaults on when absent; settings/UI
writer uses compositor names, comments skipped. Character editor sets strength0.
Flow and perturbation use default anisotropic16/wrap; depth point/clamp, base bilinear/clamp.
Shimmer animates in game hours, strength from weather/sun, depth falloff has capped reach
(about8333 units at standard settings). Do not reinterpret apparent view-distance scale as
unbounded effect. Paused motion and strength have different clocks; [weather](weather.md).

No discovered default motion blur, DOF, vignette, chromatic aberration or HDR tone curve in the
surveyed post/compositor files. Absence is scoped to those sources, not all possible mods.

### Exposure and tone mapping (Verified)

| Parameter | Default |
| --- | --- |
| `EXPOSURE_KEY` | 0.55 |
| `MIN_LUMINANCE` / `MAX_LUMINANCE` | 0.8 / 0.96 (script defaults; the exe overrides both, below) |
| `AUTOEXP_ADAPTATION_RATE` | 0.5 |
| `BLOOM_THRESHOLD` | 6.0 |
| `BLOOM_MAGNITUDE` | **0.0** |
| `BLOOM_BLUR_SIGMA` | 0.8 |

### Heat haze (Verified)

**Inputs** (**Verified**, `heathaze.compositor`):

| Unit | Texture | Sampling |
| --- | --- | --- |
| 0 `flow` | `materials/FlowHAZE.dds` (2048², BC1, 12 mips) | not set in the material: Ogre's default, which the game sets to **anisotropic, anisotropy 16** (FUN_140815b40: `MaterialManager::setDefaultTextureFiltering(TFO_ANISOTROPIC)` through its vtable slot 37, then `setDefaultAnisotropy(16)`; **Verified (decompiled)**, slot checked against OgreMain's RTTI table), wrap addressing (Ogre's default; **Observed**) |
| 1 `perturbation` | `materials/Perturber.dds` (2048², BC1, 12 mips; a tangent-space normal map) | the same default |
| 2 `depth` | G-buffer target 2 | `filtering none`, clamp |
| 3 `base` | the previous node's picture | `filtering bilinear`, clamp |

### What the viewer does

| Stage | Faithful (`--post kenshi`, `--faithful all`) | Meitou (the default) | Basis |
| --- | --- | --- | --- |
| Scene buffer | RGBA16F, single-sample | none | Kenshi: R11G11B10F, no FSAA |
| Exposure | Kenshi's auto exposure (game sky) | exposure multiplier (Tab slider, keys) | Kenshi: 0.55 / adapted, band from CONSTANTS |
| Curve | clamp at 1 | clamp at 1; shoulder or aces (the Tab slider; the `--tonemap` options were removed) as options (not a switch) | Kenshi: none (clip) |
| SSAO | off | on (12 taps, half resolution, from depth) | Kenshi: shipped, disabled |
| SSAO on characters | n/a (no SSAO) | the occlusion is scaled to 0.25 on the characters' own pixels (`PostOptions.SsaoCharacterStrength`, a constant; Meitou feature, no game counterpart; **Observed** on a `--crowd` view, no game reference) | Kenshi: none |
| Bloom | off | off; an option a look preset or the slider sets (point spread function, [render-post.md](../render-post.md#bloom)) | Kenshi: magnitude 0 |
| Grading, vignette | none | grading (Tab slider; saturation 1.06, contrast 1.05 as constants; the `--grade`, `--saturation` and `--contrast` options were removed); no vignette | Kenshi: none |
| FXAA | FXAA 3.11 quality, green as luma, subpix 0.75, thresholds 0.166 / 0.0833, preset-12 search steps, on the LDR composite (Faithful anti-aliasing; `--no-fxaa`) | TAA, FSR or DLSS instead (Meitou anti-aliasing) | Kenshi: FXAA 3.11, 0.75 |
| Dither | off | on | not in Kenshi |
| Night grading | none (the game has no grading; night is the day's colours at the exposure's night floor, see [render-post.md](../render-post.md) "Night grading" for why the ground reads orange) | scotopic blue shift by the scene's mean luminance (`nightgrade` switch, Tab slider; the `--night-grade` options were removed); the viewer's own | Kenshi: none (**Verified**: the composite is exposure only) |
| Heat haze | the game's, after FXAA (`--no-heat-haze`) | at half strength (`heathaze` switch, Tab slider), after the composite when a temporal upscaler runs | Kenshi: `HeatHaze` node, on by default |

## Expression reference

- Exposure and tone mapping: `adapted = last + (current - last) * (1 - exp(-frameTime * rate))`; `scale = max(EXPOSURE_KEY / adapted, 0.001)`; `hdr_to_gamma = false`; `hdr_disable = false`; `sRGB Gamma Conversion=No`
- FXAA: `FXAA=1`; `FSAA=1`
- SSAO: `ao = 1 - (1 - ao) * visibility`
- Heat haze: `<node name>=<bool>`; `HeatHaze=1`; `direction = flow(uv · 3.341).rg · 2 − 1`; `t = frac(gameTime · 100 + {0, 0.33, 0.66})`; `depth == 0 → 1`; offset `· saturate(depth · 6) · 0.002 · heatHaze`, depth = G-buffer distance / far clip (**Verified**, shipped `post/heathaze.hlsl`)
- What the viewer does: `=`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- Exposure and tone mapping (Verified)
- Bloom (Verified code, Observed that it is off)
- FXAA (Verified)
- SSAO (Verified shipped, Verified disabled)
- Heat haze (Verified)
- Absent (Verified against everything in `data/materials/post` and `compositors`)
