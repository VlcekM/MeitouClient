# Sky and atmosphere

Original-game research: SkyX_x64.dll, Kenshi sky controller, shipped SkyX/moon/atmosphere shaders
and merged CONSTANTS/WEATHER. Evidence scopes/formulas/tables follow; Meitou extensions are
separate engine choices, implemented in SkyAtmosphere/AtmosphereShaders and [render-sky](../render-sky.md).

## Original sky

**Verified** scattering setup: inner9.77501, outer10.2963, Rayleigh0.0022, Mie0.000675, sun30,
wavelengths(0.57,0.48,0.44), g=-0.991, exposure1.4, samples4. Green0.48 overwrites the initial
0.54; exposure is1.4, not0.48. HDR O'Neil/SkyX integral sums Rayleigh/Mie optical depth;
Rayleigh phase0.75*(1+0.5*cos²), Cornette-Shanks Mie. Sign follows pixel-to-eye convention;
view-direction convention uses positive g. Night glow=(0.05,0.05,0.1)*(2-0.75*saturate(-sunY))
*(1-height)^3 where brightest channel<0.1. Stars add0.35+saturate(-sunY*0.45).

**Verified** stars: azimuthal-equidistant UV, zenith(0.4,0.4), horizon radius0.4 on4096² texture;
linear/no mip, wrap. Drift translates both axes by0.05/game-hour, not rotation; X/Z mapping
and identification of the speed global are **Observed**. Planets use planet01 sphere radius
25.8721, fixed normalized directions/scale/spin in table; directions never orbit. Angular radius
asin(radius*scale*tan0.01deg), minute-based world-Y spin. Moon2 overlaps Moon; clouds draw in
front. Texture*max(dot(normal,realSun),0)+Rayleigh scattering; night side black, depth at far plane.

**Verified** global latitude54, sunrise5, sunset23, days/year100, night-darkness0.35 after overrides.
Day tests (`IsDay`, the daylight ramp) use the raw CONSTANTS sunrise and sunset; a sunset not after the sunrise means no daytime. The 6 / 20 fallback only feeds the sun direction (**Verified**, `FUN_14066e380`; `SkyClock.FromDatabase`).
Exposure composite=0.55/adapted with adapted bounded by night-adjusted min0.8/max1.2; no gamma
or tone curve. Sun colour from SkyX towards sun, blue+0.04, divided by4*exposure, black below
sunY=-0.2. Original sun billboard existence remains **Unknown**.

## Haze and weather

Original main haze is post/atmospherefog, not SkyX_Fog2. World-to-dome scale70000; distance
ramp from0.06D to0.6D, D derives view-distance. Weather fog follows its own distance/ease curve;
retain nested order rather than replacing all air with one physical exponential. Camera-height
bands and atmospheric colour inputs matter. WEATHER sky colour/cloud density transition30s;
fog and region blending: [weather](weather.md). The skydome, clouds and scene share HDR units.

Meitou atmosphere/planetshine/night-air/star improvements have separate switches and code
constants; they are not original-game formulas. Preserve real versus reflected eye, clock,
sun/planet key and shared haze/lighting inputs across scene shaders. [Clouds](clouds.md),
[lighting](lighting.md), [post](post-processing.md), [renderer sky](../render-sky.md).

### What draws it (Observed: `data/materials/SkyX`)

| Effect | Files | Notes |
|---|---|---|
| Skydome | `SkyX_Skydome.hlsl` | O'Neil's scattering evaluated per vertex (see below), phase functions per pixel, night glow, starfield |
| Fog | `SkyX_Fog.hlsl`, `SkyX_Fog2.hlsl` | `skyXFog2`: the same integral per pixel for scene fog (see below) |
| Moon | `SkyX_Moon.hlsl`, `SkyX_Moon.png`, `SkyX_MoonHalo.png` | a textured quad with a phase mask and a halo; **never drawn by Kenshi** (below) |
| Clouds | `SkyX_Clouds.hlsl`, `Clouds.dds`, `CloudsNormal.dds`, `CloudsTile.dds` | a planar layer: the view ray hits a plane, the texture's red channel is the density; all of it in [clouds.md](clouds.md) |
| Volumetric clouds, lightning, ground | `SkyX_VolClouds*.hlsl`, `SkyX_Lightning.hlsl`, `SkyX_Ground.hlsl`, `Noise.dds` | not used by the game (**Verified**: the exe imports nothing of SkyX's `VCloudsManager`, [clouds.md](clouds.md)) nor by the viewer |

### Planets (Verified (decompiled): the sky creation FUN_14066e380, the planet constructor FUN_14066c920, the update FUN_140670ed0; `materials/forward/moon.material` and `moon.hlsl`)

The sky creation makes two objects of `planet01.mesh` (a UV sphere, every vertex at radius 25.8721, `u = 0.2685 − atan2(z, x) / 2π`,
`v = acos(y) / π`, **Verified** against every vertex, test `NightSkyTests`) with these arguments:

| Material (texture) | Direction (normalised) | Scale | Spin (rad / game day) | Angular radius | Elevation |
|---|---|---|---|---|---|
| `Moon` (`moon_HI.dds`) | (1, 0.3, −1) | 35 | 1 | 9.09° (18.2° across) | 12.0° |
| `Moon2` (`moon2_HI.dds`) | (1, 0.14, −0.7) | 10 | 4.731 | 2.59° (5.2° across) | 6.5° |

### The scattering model (Verified: SkyX shader source and the recorded exe constants)

Derived from the game's options (inner 9.77501, outer 10.2963, Rayleigh 0.0022, Mie 0.000675, sun intensity 30, wavelengths
(0.57, **0.48**, 0.44), g −0.991, exposure **1.4**, 4 samples; the shader's parameters follow the comments of `SkyX.material`).
**Verified** 2026-10-04 (`kenshi_x64.exe` sky setup, the function that creates the SkyX sky and cloud layer, with
`SkyX_x64.dll`'s `AtmosphereManager::_update`): the game builds the wavelength vector (0.57, 0.54, 0.44) in the options struct, then stores 0.48 over the green wavelength (offset 0x1c, which `_update` reads as
`WaveLength.y` for `uInvWaveLength`) and 1.4 into the exposure (0x28, `uExposure`). Earlier notes read the 0.48 as the exposure
and kept the green 0.54; that was wrong. The viewer now uses the game's values throughout (`SkyAtmosphere.WaveLength` = (0.57, 0.48,
0.44), `SkyAtmosphere.Exposure` = 1.4; a test pins them). The table:

| Quantity | Value |
|---|---|
| thickness `outer − inner` | 0.5213 |
| `uScale = 1 / thickness` | 1.918 |
| `uScaleDepth = thickness / 2` | 0.2607 |
| `uScaleOverScaleDepth` | 7.359, so the density scale height is 0.1359 (the planet is 71.9 scale heights across its radius, the air 3.84 deep) |
| `invλ⁴` per channel | (9.473, 18.84, 26.68) |
| Rayleigh depth straight up from sea level, `Kr · 4π · invλ⁴ · scaleDepth` | (0.0683, 0.1358, 0.1924) |
| Mie depth straight up, `Km · 4π · scaleDepth` | 0.0022 |
| camera height | `inner + heightPosition · thickness`, heightPosition 0.01: an eye near sea level |

### WEATHER records (Observed: the base game's and the loaded mods' records, 2026-10-04)

| Field | Type | Meaning (Observed) |
|---|---|---|
| `sky color mult` | int, packed 0xRRGGBB | white in clear weather, `FCAC7C` (orange) in dust storms, `C6C6FF` (cool) in misty rain, `9F8777` in black rain. The sky controller fades to it over 30 s and multiplies only the two SkyX colours behind `zenithLight` (+Z) and `nadirLight` (+Y) with it, i.e. the cloud light and `horizonClouds` (**Verified (decompiled)**, the sky update; no other reader was searched, so whether anything tints the skydome itself is still open) |
| `fog enabled`, `fog distance min` / `max`, `fog color` | bool, floats, int RRGGBB | the distance where the weather fog is complete; by the wind between min and max only when `fog wind min` ≠ `fog wind max`, else `max(min, max)` (**Verified (decompiled)**, the WEATHER loader). In the base game only "misty rain" has two wind values, so every weather's fog is complete at its `fog distance max` (dust storms 25000, "Sand stream" 20000; [weather.md](weather.md#wind-and-camera-transitions)); the colour is sand (`E9CB9E`) or white |
| `clouds density` | float | 0 clear, 1 overcast, clamped to 0..1 by the sky controller (`SkyBeam` has 20); drives the cloud layer ([clouds.md](clouds.md)) |

## Expression reference

- Planets: the sky creation FUN_14066e380, the planet constructor FUN_14066c920, the update FUN_140670ed0; `materials/forward/moon.material` and `moon.hlsl`): `u = 0.2685 − atan2(z, x) / 2π`; `v = acos(y) / π`
- The scattering model: `uScale = 1 / thickness`; `uScaleDepth = thickness / 2`; `g = −0.991`; `g = +0.991`; `nightmult = saturate(1 − max · 10)`
- Time and the CONSTANTS record: `MIN_LUMINANCE = exposure min · lerp(night darkness, 1, saturate(5 sunY))`
- Haze: how vanilla does it: `D = view distance × 10`; `pFogParams = (D, D · k1, min(D, D · k2), 0)`; `fogLevel = saturate((distance - pFogParams.y) / (pFogParams.z - pFogParams.y))`; `l = (dx² + dz²) / R²`; `y = 0`; `amount = saturate(distance * fogDensity)`; `colour = lerp(atmosphere, global, global.a)`; `alpha = saturate(atmosphere.a + global.a)`; `d = lerp(fog distance min, fog distance max, saturate((wind - fog wind min) / (fog wind max - fog wind min)))`; `fogDensity = 1 / d`; `distance = farClip`
- Planetshine: `E / E_sun = A · (Ω / π) · Φ(α)`; `Ω = 2π (1 − cos r)`; `Φ(α) = (sin α + (π − α) cos α) / π`; `cos α = −sun · towardsPlanet`; `0.96 · 1.2 · 0.2 = 0.23`
- Night air: `NightAir.Colour(planetLight) = Airglow + 0.25 · luminance(planetLight) · (0.8, 1.0, 1.4)`; `col = mix(col, planet, coverage)`; `veil = NightAir.PlanetVeil`; `atmoNightAir(d) = colour · weight · exp(−max(d.y, 0) / 0.2)`
- Using the atmosphere in a new shader: `colour = atmoApply(colour, eyePosition, worldPosition)`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- What draws it (Observed: `data/materials/SkyX`)
- Stars (Verified (decompiled): `SkyX_x64.dll` `MeshManager::updateGeometry`, `SkyX::update`, `SkyX_Skydome.hlsl`, the sky update FUN_14066f190)
- Planets (Verified (decompiled): the sky creation FUN_14066e380, the planet constructor FUN_14066c920, the update FUN_140670ed0; `materials/forward/moon.material` and `moon.hlsl`)
- The scattering model (Verified: SkyX shader source and the recorded exe constants)
- Time and the CONSTANTS record (Verified: the merged load order, 2026-10-04)
- WEATHER records (Observed: the base game's and the loaded mods' records, 2026-10-04)
