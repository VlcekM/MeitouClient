# Sky and atmosphere

How Kenshi makes its sky (SkyX), what the data says about it, and how the world viewer
(`meitou-viewer --world`) reproduces it. The sun's path and the recorded exe constants are also in
[terrain.md](terrain.md#sky-and-sun); this file holds the rest. Labels: **Verified** (checked against files or the exe, and
how), **Observed** (read in files or shaders, not tested), **Unknown**.

## The game's sky

### What draws it (Observed: `data/materials/SkyX`)

Kenshi uses the open-source SkyX add-on (LGPL, Ogre). The folder has the material script `SkyX.material` and, per effect, an
HLSL and a GLSL file:

| Effect | Files | Notes |
|---|---|---|
| Skydome | `SkyX_Skydome.hlsl` | O'Neil's scattering evaluated per vertex (see below), phase functions per pixel, night glow, starfield |
| Fog | `SkyX_Fog.hlsl`, `SkyX_Fog2.hlsl` | `skyXFog2`: the same integral per pixel for scene fog (see below) |
| Moon | `SkyX_Moon.hlsl`, `SkyX_Moon.png`, `SkyX_MoonHalo.png` | a textured quad with a phase mask and a halo |
| Clouds | `SkyX_Clouds.hlsl`, `Clouds.dds`, `CloudsNormal.dds`, `CloudsTile.dds` | a planar layer: the view ray hits a plane, the texture's red channel is the density; all of it in [clouds.md](clouds.md) |
| Volumetric clouds, lightning, ground | `SkyX_VolClouds*.hlsl`, `SkyX_Lightning.hlsl`, `SkyX_Ground.hlsl`, `Noise.dds` | not used by the game (**Verified**: the exe imports nothing of SkyX's `VCloudsManager`, [clouds.md](clouds.md)) nor by the viewer |

The textures: `SkyX_Starfield.dds` is 4096² DXT1 with 13 mips (the Milky Way and stars on a black field; the shader scrolls it
with time), `Clouds.dds` / `CloudsNormal.dds` / `CloudsTile.dds` / `Noise.dds` are 1024² DXT1 without mips, `SkyX_Moon.png`
512² RGBA (a full moon disc on transparent), `SkyX_MoonHalo.png` 512 × 256 grey-alpha. `data/materials` also has `moon_HI.dds`
(4096 × 2048) and `moon2_HI.dds`: despite the name an equirectangular **planet** map (brown deserts, dark seas, polar caps),
with `planet01.mesh` and `moon_NML.dds`. So the game's sky has a large planet or moon as a mesh (**Observed**; its placement
and phase are **Unknown**, the viewer does not draw it; lead: the sky creation FUN_14066e380 builds two objects from
`planet01.mesh`, one named "Moon2", with constant vectors, not decoded). `data/materials/caelum` is the older Caelum sky add-on's folder
(**Unknown** whether anything still uses it).

### The scattering model (Verified: SkyX shader source and the recorded exe constants)

The skydome shader is the GPU Gems 2 chapter 16 algorithm (O'Neil): a ray from the eye to the dome vertex is cut into
`uSamples` pieces (the game's `Samples` is 4); at each sample the Rayleigh and Mie light is attenuated by the optical depth
sun → sample → eye, with the air density `exp(uScaleOverScaleDepth · (inner − height))`. Output (HDR path):
`exposure · (rayleighPhase · RayleighColor + miePhase · MieColor)`; LDR path `1 − exp(−exposure · …)`.

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

(That the options are Kr and Km themselves, not multipliers of other base values, is **Observed** from the option names; the
shader's own default numbers in `SkyX.material`, KrESun 0.323 and so on, are SkyX's older defaults.)

- Phase functions (Verified from the shader): Rayleigh `0.75 (1 + 0.5 cos²)`, Mie the Cornette-Shanks form with
  `g = −0.991`. SkyX computes `cos` from the vector *from the pixel towards the eye*, so the negative g gives a forward lobe
  around the sun: with `cos` taken as `dot(sun, viewDirection)` the same lobe has `g = +0.991`. That lobe is only about half a
  degree wide, so in the game the Mie light is a tight glow around the sun and the sky's colour is almost purely Rayleigh.
- Night (Verified from the shader): where the sky colour's brightest channel is under 0.1 (`nightmult = saturate(1 − max · 10)`)
  it adds a glow `(0.05, 0.05, 0.1) · (2 − 0.75 · saturate(−sunY)) · (1 − height)³` (`height` the dome's normalised height,
  so the glow is strongest at the horizon) and the starfield texture times `0.35 + saturate(−sunY · 0.45)`.
- Fog (Observed, `SkyX_Fog2.hlsl`): the same integral per pixel with 4 samples, the ray's length saturated to the dome
  radius, `1 − exp(−exposure · colour)` out. The game's main haze does not use it but `post/atmospherefog.hlsl` (see "Haze"
  below: world → dome scale 70000, distances 0.06 D to 0.6 D, both Verified).
- The game's final brightness (**Verified**, see [lighting.md](lighting.md#exposure-verified-exe-sky-update-and-sky-creation-posthdrfp4hlsl)):
  the skydome is drawn in SkyX's HDR mode (the game calls `setLightingMode(1)`; the `1 − exp` form is mode 0) into the same HDR
  target as the lit scene, and the composite multiplies everything by `0.55 / adapted`, the mean scene luminance clamped to
  `[exposure min · lerp(night darkness, 1, saturate(5 sunY)), exposure max]` = `[0.8 …, 1.2]` by day. No gamma step, no tone curve.
  So the sky's radiance and the terrain's light are in one unit: the terrain's sun is SkyX's own colour towards the sun (below).
- The sun's light colour is SkyX's colour towards the sun, nudged by (0, 0, 0.04), divided by `4 · exposure`, black below sunY
  −0.2 (**Verified**, exe sky update; details in [lighting.md](lighting.md)). The game draws no sun disc in the skydome shader (the Mie
  lobe is the glow). Whether the game adds a sun billboard is **Unknown**; the viewer draws none.

### Time and the CONSTANTS record (Verified: the merged load order, 2026-10-04)

`GLOBAL CONSTANTS` after all mods: `latitude` 54, `sunrise` 5, `sunset` 23, `night darkness` 0.35, `days per year` 100 (the base
`gamedata.base` record alone says 45 / 5 / 24; `Newwworld.mod` overrides). The sun's path is in
[terrain.md](terrain.md#sun-path-verified-kenshi_x64exe-sky-controller-and-the-constants-record). `night darkness` is the
exposure floor's night factor: `MIN_LUMINANCE = exposure min · lerp(night darkness, 1, saturate(5 sunY))` (**Verified**, exe sky
update, 2026-10-05; [lighting.md](lighting.md)). `exposure min` 0.8 and `exposure max` 1.2 after all mods, the same in the base game's own load order
(**Verified** by loading both; which of `gamedata.base`, `Newwworld.mod`, `rebirth.mod` sets them was not checked).

### WEATHER records (Observed: the base game's and the loaded mods' records, 2026-10-04)

Record type 80, found by name. Fields that concern the sky:

| Field | Type | Meaning (Observed) |
|---|---|---|
| `sky color mult` | int, packed 0xRRGGBB | white in clear weather, `FCAC7C` (orange) in dust storms, `C6C6FF` (cool) in misty rain, `9F8777` in black rain. The sky controller fades to it over 30 s and multiplies only the two SkyX colours behind `zenithLight` (+Z) and `nadirLight` (+Y) with it, i.e. the cloud light and `horizonClouds` (**Verified (decompiled)**, the sky update; no other reader was searched, so whether anything tints the skydome itself is still open) |
| `fog enabled`, `fog distance min` / `max`, `fog color` | bool, floats, int RRGGBB | the distance where the weather fog is complete; by the wind between min and max only when `fog wind min` ≠ `fog wind max`, else `max(min, max)` (**Verified (decompiled)**, the WEATHER loader). In the base game only "misty rain" has two wind values, so every weather's fog is complete at its `fog distance max` (dust storms 25000, "Sand stream" 20000; [weather.md](weather.md#fog-up-to-four-regions-verified-decompiled-fun_1409e8f70-fun_1409e8cc0-fun_1409dc350)); the colour is sand (`E9CB9E`) or white |
| `clouds density` | float | 0 clear, 1 overcast, clamped to 0..1 by the sky controller (`SkyBeam` has 20); drives the cloud layer ([clouds.md](clouds.md)) |

Others (`rain intensity`, `wetness`, `dust`, `dust inside`, `dust slope`, `heat haze`, `wind speed min/max`, `wind intensity`,
`wind update time/limit`, `fog wind min/max`, `affect type/strength`, `effect strength min/max`, `start time`, `end time`)
are for rain, dust and wind effects and the weather's own schedule; not read by the viewer. The "Default" weather
(`5460-weather.mod`) is clear: fog off, clouds 0, both colours white. Which weather applies where and when (regions from
`areasmap.tga`, seasons, weighted random weathers, wind) is in [weather.md](weather.md); `SkyWeather` reads a record, the viewer
uses "Default" unless `--weather` names another.


### Haze (distance fog): how vanilla does it

What the game does, from the shipped shaders, `kenshi_x64.exe` and `SkyX_x64.dll` (decompiled output is not in the repo;
checked 2026-10-04):

- **One full-screen pass after the lighting, over everything lit** (**Verified**: `compositors/main.compositor`, node
  `Lighting_HDR`: after the water (queues 81 to 82) and before fog volumes and particles, `AtmosphereFogMaterial`
  with `scene_blend alpha_blend`). No terrain, object, foliage or water shader fogs itself (**Verified**: only `water.hlsl` takes
  `pFogParams`, in its vertex stage, and nothing in the shipped scripts uses it for fog; `grep fog` finds only the fog scripts and the
  old `caelum` folder). The pass reads the G-buffer depth (`depth * farClip`; no geometry means `farClip`), rebuilds the world position
  and writes `(colour, alpha)`: the scene becomes `scene * (1 - alpha) + colour * alpha`. So the haze depends on **distance from the eye
  only**, not on height above the ground (Verified: `post/fog.hlsl` `atmosphere_fog_fs`, `post/atmospherefog.hlsl`). The lighting
  pass turns depth into distance with `pFogParams.x`, the fog pass with `farClip`: both are D (below), so the units match.
- **Where the distances come from** (**Verified**, the sky controller update that fills `SharedSkyParams`): `D = view distance × 10`
  (the `view distance` setting; the install's `settings.cfg` has 5000 (Observed), the options slider is given 1500 and 12000, presumably its range (Observed),
  so D = 50000, also the camera's far clip) and `pFogParams = (D, D · k1, min(D, D · k2), 0)` with **k1 = 0.06 and k2 = 0.6**.
  k1 and k2 are two floats in the CONSTANTS block (a `.bss` struct at `0x142133dd0`, offsets 0x198 and 0x194) that the CONSTANTS
  loader sets as literals (0x3d75c28f, 0x3f19999a) before reading the record's fields; no field or setting overwrites them (the loader
  is the only writer found: no instruction, pointer or 32-bit RVA in the exe addresses them directly, which is why an earlier search
  missed it). So the atmosphere haze starts at **3000** and is complete at **30000** with the default setting: it builds from close to
  mid distance and everything past 0.6 D is fully the haze's colour. (The 0.8 and 0.96 next to `view distance × 10` in the scene setup are
  for Ogre's own `setFog`, which the deferred pipeline does not use.)
- **Atmosphere term** (`post/atmospherefog.hlsl` `calculateAtmosphereFog`, **Verified**): alpha `fogLevel = saturate((distance -
  pFogParams.y) / (pFogParams.z - pFogParams.y))`, linear, on the true distance. Colour: SkyX's scattering integral along the ray
  from the eye to the point, Rayleigh colour only (the Mie extinction is in, its colour is not), 4 samples, times `uExposure`, then
  `lerp(colour, horizonClouds.rgb, horizonClouds.a)`. The ray: the point's offset from the eye divided by `uSkydomeRadius`, placed on
  SkyX's planet with the eye at SkyX's camera height (`inner + 0.01 · thickness`, 0.0052 units, about 365 world units, above the
  point's reference level whatever the real eye height). A point below the eye is lifted towards the eye's level by `1 - l`, with
  `l = (dx² + dz²) / R²` (fully lifted close by, not at all one radius away); the ray's length is saturated to 1 (one dome radius); a
  ray steeper than -0.3 is replaced by the fixed `(0, -0.3, 0.953)`. Pixels with no geometry get alpha 0, except that a ray below the
  eye is first moved to the plane `y = 0`, so the open sea out to the horizon is fogged.
- **`uSkydomeRadius` = 70000** (**Verified**: `common.program`'s `SkyXFogParams` default; nothing in the exe names it, and
  `SkyX_x64.dll` sets a `uSkydomeRadius` only on its own ground pass's parameters). The other `SkyXFogParams` (`uCameraPos`,
  `uInnerRadius`, `uInvWaveLength`, `uKr4PI`, `uKrESun`, `uKm4PI`, `uScale`, `uScaleDepth`, `uScaleOverScaleDepth`, `uExposure`) are
  copied from the skydome's by SkyX's `GPUManager::setGpuProgramParameter` (**Verified**), so the fog uses the sky's own atmosphere
  (wavelengths (0.57, 0.48, 0.44), exposure 1.4, see above).
- **Finite path against the sky**: the skydome evaluates the same integral from the same eye over a path of length ~1 (the dome is
  the unit sphere round the eye in SkyX units), so the haze colour at a distance of 70000 is the sky's own Rayleigh colour in that
  direction, and nearer it is a fraction of it (**Verified** from the two shaders; computed with `KenshiHaze.Colour` against
  `SkyXModel.Colour`, test `AtmosphereTests`): at noon,
  horizontal, about (0.07, 0.13, 0.18) of the sky at 3000, (0.56, 0.65, 0.72) at 30000 (red, green, blue) and (0.81, 0.87, 0.91)
  at 50000. So the veil is darker than the sky behind it and slightly bluer (the short path loses less blue); nearer ridges
  are darker layers, farther ones lighter, up to the sky's colour. At night both integrals vanish: the haze is black.
- **`horizonClouds`** (**Verified**, the same sky update; inputs partly Unknown): alpha `saturate(densityOffset + 0.5)`, where the
  cloud layer's `densityOffset` (SkyX's `uDensityOffset`, option offset 0x28) is set to `1.4 c - 0.8` from the weather's cloud density
  c (the layer starts at -0.8): **0 under a clear sky** (c = 0), full from c ≈ 0.93. Colour:
  `saturate(sun · (1 - 0.1 · (offset + 0.2) · densityMultiplier) + max(horizon, floor) · sun.g) · (1 - darkness) · sqrt(exposure)`,
  with `densityMultiplier` 3 (`uDensityMultiplier`, set at creation), `darkness` = `pow(saturate(c - 0.5), 0.3)` (`uDarkness`),
  `sun` = `sunColour.rgb` = SkyX's colour towards the sun nudged by (0, 0, 0.04), divided by `4 · exposure` (when the sun is above
  -0.2; else black), `horizon` = SkyX's colour along +Z (Ogre's `UNIT_Z`, **Verified (decompiled)**) times the weather's sky
  multiplier, `floor` = (0.001, 0.001, 0.0015), the sky controller's offset 0xb8, set when the sky is created (**Verified
  (decompiled)**). `getColorAt` returns SkyX's HDR colour, `exposure · (…)` ([lighting.md](lighting.md#suncolour-verified-exe-sky-update-skyx_x64dll)),
  so the colour is in the same HDR units as the cloud layer's ([clouds.md](clouds.md)).
- **Global (weather) term** (`fogValue`, **Verified**): `amount = saturate(distance * fogDensity)`, alpha an ease-in-out curve of it (`2a²`
  below 0.5, `1 - 2(a-1)²` above) times `fogColour.a`, colour `fogColour.rgb * sunColour.w` (`sunColour.w` is a daylight scale: half of a
  controller value at sunrise, rising with the sun's height, and falling to 0 just below the horizon). Result `colour = lerp(atmosphere,
  global, global.a)`, `alpha = saturate(atmosphere.a + global.a)`. **Verified** (the weather blender and `MainFog`'s setters): the
  current weathers of up to four weather regions are blended by weight (the zone cells at the camera ± 1300 in x and z, weight
  `1 − min(1, d²/1300²)` with `d` the camera's distance to the cell; [weather.md](weather.md#fog-up-to-four-regions-verified-decompiled-fun_1409e8f70-fun_1409e8cc0-fun_1409dc350));
  each gives `fogColour.a` = 1 if `fog enabled` else 0, `fogColour.rgb` = `fog color`, and a
  distance `d = lerp(fog distance min, fog distance max, saturate((wind - fog wind min) / (fog wind max - fog wind min)))` (`fog
  distance min` when the two wind values are equal, which the WEATHER loader has already replaced by `max(min, max)`), the
  current wind being the region's. `fogDensity = 1 / d` (1 when d is 0, where
  `fogColour.a` is 0 anyway). So the weather fog is complete at d. The "Default" weather has `fog enabled` false: no weather fog.
- It does **not** differ per biome (**Observed**: no biome field is read by the shaders or the sky controller's fog code).
- **Not there**: no exponential or height-based haze in the main chain (`SkyX_Fog*.hlsl` and ground fog are separate features:
  ground fog is deprecated, fog planes/spheres/beams are the placed "fog volumes", `fogfeatures.dat`).

### In the viewer: `--haze kenshi` (default) and `physical`

`atmoApply` has two branches (key F7, `SkyRenderer.KenshiHaze`; the sky itself is the same in both):

- **kenshi** (the default): the game's formula, evaluated per pixel in the object, terrain, foliage and water shaders instead of in a
  separate pass over the G-buffer (same result for opaque surfaces). Alpha: the ramp from `0.06 D` to `min(D, 0.6 D)`, D = 50000
  (`--haze-distance` sets D: view distance × 10). D is not the viewer's far clip (about 357000): past D the game's formula simply goes
  on. Colour: `KenshiHaze.Colour` / GLSL `hazeColour`, SkyX's exposure × Rayleigh phase × `invλ⁴ · Kr · sun` × the 4-sample
  in-scattering from SkyX's camera to the point mapped by the dome radius 70000, with the game's lift and -0.3 clamp: the game's
  own expression, in the same HDR units as the sky and the lit scene, so the exposure treats them alike. At night the integral
  vanishes and the haze is black, as in the game. Then `horizonClouds`: its pull is the game's (0 in clear weather); its colour is a
  **stand-in** built the game's way from the viewer's sun colour and horizon colour (the game's `getColorAt` input for the cloud layer
  and its floor colour are Unknown, above). The weather's fog (`--weather`, when enabled): `fog color · sunColour.w`, the game's
  ease-in-out curve over `distance / fog distance max` (the viewer has no wind; the game uses the same distance for every base weather, see the WEATHER table above),
  alphas added. Consequences that look odd but are the game's rule: from a camera high above the ground (the viewer allows much higher
  than the game) everything is past 30000 and fully hazed, steep rays all take the one fixed ray's colour; at night distant terrain
  goes black against the night sky. Not reproduced: the water being fogged by the depth of what is under it.
- **physical** (a viewer alternative, not the game's): the closed-form optical depth of SkyX's own air (its Rayleigh and Mie depths
  straight up, without the earlier turbidity factor) along the ray, with one density scale height = 40000 world units (a viewer
  choice; the game's world unit is Unknown), in-scattering of the sky's colour in the ray's direction, closing at `--fog`
  (default 250000).

## In the viewer

`SkyRenderer` (with `AtmosphereShaders`, and `SkyAtmosphere` / `SkyXModel` / `KenshiHaze` in `Sky.cs`, `KenshiLighting` /
`AmbientMap` / `ExposureConstants` in `KenshiLighting.cs`, `SkyWeather`, all in `Meitou.Data.World`), in the world view. Rewritten
2026-10-05 to the game's own numbers; the earlier eye-tuned model (15 × Mie with g 0.75 and a lower scale height, a sky gain of
3.4, a sun scale of 1.2, an ozone layer, a half-gamma display mapping, a blue-dependent grading, the precomputed transmittance
and sky-view tables, the 0.36° sun disc) is gone, with `AtmosphereModel.cs`.

- **Sky pass**: a full-screen pass evaluates SkyX's skydome per pixel (`atmoSky`): the O'Neil integral with 4 samples from SkyX's
  fixed camera, the game's options (table above), the Rayleigh and Cornette-Shanks phases with the cosine towards the eye, the HDR
  output `exposure · (…)`, the night glow in its HDR form `pow(glow, 2.2)` where SkyX's night factor is on. SkyX evaluates the
  integral per dome vertex and interpolates; the viewer evaluates it per pixel (a viewer choice: smoother, same formula). Directions below
  the horizon take the horizon's colour (the ground covers them; SkyX's dome has a lower half the viewer does not need). Values are
  HDR; the post-processing's exposure brings them to the screen. Predicted at noon in clear weather (`SkyXModel`, scratch
  computation) with the exposure of ×0.69 that a typical day scene gets, a sky about (61, 105, 131) at 20° and (130, 176,
  184) at the horizon on screen (sRGB 0..255 without a gamma step, like the game), the teal blue of the game's screenshots.
- **Stars**: `SkyX_Starfield.dds` (a 1024² level) times SkyX's night factor, `(0.35 + saturate(−sunY · 0.45))` and the HDR ×2, laid
  over the upper hemisphere stereographically and turning with the sun's half-turn. SkyX's own mapping is the dome's UV layout,
  which the viewer does not reproduce (a **stand-in** placement; the brightness formula is the shader's).
- **Moon**: `SkyX_Moon.png`, always full, opposite the sun, saturated and alpha-blended as `SkyX_Moon.hlsl` does; its size (0.016
  rad) and placement are **stand-ins** (the game's moon position is Unknown).
- **Clouds** (`--clouds` or the weather's density): a flat layer of `Clouds.dds`'s red channel; the colour follows the cloud shader's
  form with a **stand-in** zenith light (the game's inputs are now known: [clouds.md](clouds.md)).
- **Weather tint**: the sky is multiplied by the weather's `sky color mult`. In the game the sky update applies it only to the
  `zenithLight` / `nadirLight` colours (clouds and `horizonClouds`), see the WEATHER table above; white in the "Default" weather,
  so the default views do not depend on it. With weather fog the sky near the horizon fades to the fog colour (a viewer choice).
- **Light**: the deferred lighting pass's model ([lighting.md](lighting.md)): `kenshiLight` in the mesh, terrain and grass shaders.
  The water still takes a sun colour and an ambient (`WorldLighting`): `π · 0.96 · sunColour.rgb · w` and the irradiance cube's up
  and down faces times the environment factor.
- **Exposure**: in game-sky mode the post-processing measures the frame's mean luminance and scales by `0.55 / clamp(mean,
  MIN_LUMINANCE, MAX_LUMINANCE)` with the game's band ([post-processing.md](post-processing.md)).
- `--simple-sky` (key `B`) switches back to the old colour model, the old light and squared-distance fog, with a fixed exposure,
  for comparison.

### Using the atmosphere in a new shader

Include `AtmosphereShaders.Functions` in the fragment shader (after `#version`), light the surface with
`kenshiLight(albedo, normal, towardsEye, gloss, worldPosition)` in game-sky mode (`uAtmoParams.x > 0.5`), end with
`colour = atmoApply(colour, eyePosition, worldPosition)`, call `SkyRenderer.AssignSamplerUnits(gl, program)` once after linking
(the cube and ambient-map samplers go on the top three texture units) and `SkyRenderer.Active?.Apply(program)` each frame before
drawing. `SkyRenderer.SkyFunctions` additionally has `skyColour(direction, disc)` for reflections. In game-sky mode colours are
HDR in the game's units; the post-processing's exposure brings them to the screen.

### Not reproduced

Volumetric clouds, lightning, cloud lighting from the sun's direction and drifting, the moon's phase and halo, the planet mesh,
SkyX's ground fog, the weather schedule, the starfield's dome mapping, and SkyX's per-vertex evaluation (the viewer's is per pixel).
