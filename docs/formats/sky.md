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
| Moon | `SkyX_Moon.hlsl`, `SkyX_Moon.png`, `SkyX_MoonHalo.png` | a textured quad with a phase mask and a halo; **never drawn by Kenshi** (below) |
| Clouds | `SkyX_Clouds.hlsl`, `Clouds.dds`, `CloudsNormal.dds`, `CloudsTile.dds` | a planar layer: the view ray hits a plane, the texture's red channel is the density; all of it in [clouds.md](clouds.md) |
| Volumetric clouds, lightning, ground | `SkyX_VolClouds*.hlsl`, `SkyX_Lightning.hlsl`, `SkyX_Ground.hlsl`, `Noise.dds` | not used by the game (**Verified**: the exe imports nothing of SkyX's `VCloudsManager`, [clouds.md](clouds.md)) nor by the viewer |

The textures: `SkyX_Starfield.dds` is 4096² DXT1 with 13 mips (the Milky Way and stars on a black field; the shader scrolls it
with time), `Clouds.dds` / `CloudsNormal.dds` / `CloudsTile.dds` / `Noise.dds` are 1024² DXT1 without mips, `SkyX_Moon.png`
512² RGBA (a full moon disc on transparent), `SkyX_MoonHalo.png` 512 × 256 grey-alpha. `data/materials` also has `moon_HI.dds`
(4096 × 2048 BC1 with mips) and `moon2_HI.dds` (2048 × 1024 BC1): despite the names equirectangular **planet** maps (brown deserts,
dark seas, polar caps), with `planet01.mesh` and `moon_NML.dds` (not referenced by the planet material). These are the game's two
moons ("Planets" below). `data/materials/caelum` is the older Caelum sky add-on's folder (**Unknown** whether anything still uses it).

**SkyX's own moon is never drawn** (**Verified (decompiled)**, 2026-10-10): Kenshi's `SkyX_x64.dll` has no `MoonManager` class at all
(its exports and RTTI list `AtmosphereManager`, `BasicController`, `CloudLayer`, `CloudsManager`, `ColorGradient`, `GPUManager`,
`MeshManager` and `SkyX`); `GPUManager::_updateFP` still assigns `SkyX_Moon.png` / `SkyX_MoonHalo.png` to the `SkyX_Moon` material, but
no object uses that material, and the exe never names it. The Earth-moon disc of `SkyX_Moon.png` is a leftover of SkyX's samples.

### Stars (Verified (decompiled): `SkyX_x64.dll` `MeshManager::updateGeometry`, `SkyX::update`, `SkyX_Skydome.hlsl`, the sky update FUN_14066f190)

- **Mapping.** The dome's texture coordinate (`TEXCOORD1`) per vertex is `4 (1 + t · (cos az, sin az))` with `t` = the zenith angle / 90°
  (the vertex's ring index over the rings above the horizon) and `(cos az, sin az)` the direction's xz normalised. That u follows +x and v +z
  is **Observed** (the decompiled stores lost their offsets; a swap would mirror the star pattern and needs a side-by-side with the game).
  So the dome is an **azimuthal-equidistant** map round the zenith. The vertex shader scales it by 0.1, so the zenith samples (0.4, 0.4) and
  the horizon is a circle of radius 0.4 round it: the visible sky covers 0.8 of the 4096² texture, about 18 texels per degree.
- **Filtering.** `filtering linear linear none` (no mip filter): the top level only, 4096², wrap addressing.
- **Movement.** The fragment shader samples at `uv + uTime · 0.1` (HDR branch): the same shift on **both** axes, a diagonal
  translation that wraps, not a rotation. `uTime` = SkyX's time × 0.5, set in `SkyX::update`, where the time grows by `timeMultiplier ×
  frame seconds`. Kenshi's sky update sets the time multiplier each frame to `game speed value × 0.009166666` (0 while a flag, presumably
  the pause, is set). 0.0091667 is the game's hours per real second at speed 1 (1 game hour = 109 real seconds, [game-loop.md](../game/game-loop.md)), so the
  time is **game hours since the sky was created** (the load), and the starfield moves 0.05 texture units along u and v per game hour
  (1.2 a game day). That the multiplied global is the game speed is **Observed** (the factor's match; its writers were not read). The planets
  (below) do not move with it.

### Planets (Verified (decompiled): the sky creation FUN_14066e380, the planet constructor FUN_14066c920, the update FUN_140670ed0; `materials/forward/moon.material` and `moon.hlsl`)

The sky creation makes two objects of `planet01.mesh` (a UV sphere, every vertex at radius 25.8721, `u = 0.2685 − atan2(z, x) / 2π`,
`v = acos(y) / π`, **Verified** against every vertex, test `NightSkyTests`) with these arguments:

| Material (texture) | Direction (normalised) | Scale | Spin (rad / game day) | Angular radius | Elevation |
|---|---|---|---|---|---|
| `Moon` (`moon_HI.dds`) | (1, 0.3, −1) | 35 | 1 | 9.09° (18.2° across) | 12.0° |
| `Moon2` (`moon2_HI.dds`) | (1, 0.14, −0.7) | 10 | 4.731 | 2.59° (5.2° across) | 6.5° |

- **Placement.** Each frame (both from the sky controller's update FUN_14066ff60, which also fills the day / hour / minute struct) the
  planet's node goes to the camera's position plus the fixed direction × `½ (a + b) (1 − tan 0.01°)`, with `a` and `b` two camera getters
  (vtable 0x100 and 0x110: by Ogre's layout the near and far clip; not matched to the RTTI), and is scaled by `Scale × that distance ×
  tan 0.01°`. The angular radius is therefore `asin(25.8721 · Scale · tan 0.01°)` whatever the clip planes. The directions are fixed in
  the world: **the planets never move across the sky** (they hang over +x, −z, low above the horizon, day and night); only the sun moves.
- **Spin.** The node's orientation is a turn about world y by `(((day · 24 + hour) · 60 + minute) · Spin / 1440)` radians (whole game minutes).
- **Draw.** No shadows; render queue 6 with the priority of a counter that starts at 0 (`setRenderQueueGroupAndPriority`, vtable slot 11,
  **Verified** against `OgreMain`'s RTTI), so priorities 1 and 2: Moon first, Moon2 over it where the discs overlap (11.2° apart, the radii sum to
  11.7°). The SkyX dome is in queue 5 and its cloud entity in queue 6 at Ogre's default priority 100 (**Observed**: the queue bytes 5–8 from SkyX's struct
  order, [clouds.md](clouds.md#geometry-and-pass-verified-decompiled-skyx_x64dll)), so the **clouds pass in front of the planets**.
- **Shading** (`moon.material`: `depth_write off`; `moon.hlsl`): the vertex shader forces z to the far plane (so the planet shows only where
  nothing else was drawn, behind all geometry) and computes SkyX's Rayleigh in-scattering towards the vertex (the skydome's integral, 4
  samples, the Rayleigh phase, `× uExposure`; no Mie, no night glow). The fragment shader: `texture × saturate(dot(normal, sunDirectionReal))
  + that colour`, alpha 1. So the planet is lit by the **real sun direction**: by day it is pale (the sky's colour is added on top), at night
  only its sunlit side shows, and as the sun goes round under the horizon the lit crescent turns. The night side is black, covering the stars.

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

Others (`rain intensity`, `wetness`, `dust`, `dust inside`, `dust slope`, `heat haze` (read for the heat-haze pass, [post-processing.md](post-processing.md#heat-haze-verified)), `wind speed min/max`, `wind intensity`,
`wind update time/limit`, `fog wind min/max`, `affect type/strength`, `effect strength min/max`, `start time`, `end time`)
are for rain, dust and wind effects and the weather's own schedule; not read by the viewer. The "Default" weather
(`5460-weather.mod`) is clear: fog off, clouds 0, both colours white. Which weather applies where and when (regions from
`areasmap.tga`, seasons, weighted random weathers, wind) is in [weather.md](weather.md); `SkyWeather` reads a record, the viewer
uses "Default" unless `--weather` names another (the viewer's default is now the scheduler, `--weather auto`: [weather.md](weather.md#in-the-viewer-and-the-game-step-3); the sky colour multiplier, cloud density and fog come from its state).


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
- **The sky is fogged by the global term** (**Verified**, `fog.hlsl` `atmosphere_fog_fs`; this pass runs over every pixel incl. the sky): a pixel
  with no geometry has `distance = farClip`; its atmosphere term is zeroed, the global term remains: alpha = ease-in-out(saturate(farClip /
  fog distance)) × `fogColour.a`, colour `fogColour.rgb · sunColour.w` (the shader's final `rgb / a` undoes the premultiply). With
  farClip = D = 50000, a weather whose fog distance is 50000 or less (dust storms 25000, Ashlands 35000, sand stream 20000) replaces the
  whole sky, clouds and stars with one **flat** fog colour; farther fogs only tint it. So the dust storm sky is flat sand × daylight
  (brown-orange after the exposure), the Ashlands' flat light grey. The distant terrain's mid-range haze is a different matter: it is the
  atmosphere term (SkyX blue, pulled to the dark `horizonClouds` colour at high cloud density, `darkness` 0.81 at c = 1) mixed with the fog.
- It does **not** differ per biome (**Observed**: no biome field is read by the shaders or the sky controller's fog code). Local fog
  (the swamp's grey-brown fog, the Fog Islands', the Vain's red haze) comes from the placed fog volumes of `fogfeatures.dat`, drawn after
  this pass: [fogfeatures.md](fogfeatures.md).
- **Made for an eye near the ground.** The fog's ray always starts at SkyX's fixed camera, so the formula ignores the eye's
  height; the game's camera never gets more than 1840 above its pivot on the ground ([camera.md](camera.md), **Verified**), and
  there it behaves. From far higher up it breaks down (**Verified by computation**: `KenshiHaze.Colour`, the viewer's transcription
  of the shader's maths, test `AtmosphereTests`): far points lie well below the eye, the lift no longer raises them, the ray dips
  steeply (to the −0.3 clamp) and runs a whole dome radius below SkyX's ground, where O'Neil's polynomial fit of the optical depth
  goes strongly negative (down to about −10 per sample) and the attenuation `exp(−optical · extinction)` grows instead of shrinking.
  For an eye 2000 above the ground the colour's blue stays under 1.3 at every distance; for an eye 34000 up it reaches ~7800 at
  60000 away, 50000 up already at 30000. In the game this never happens. (An earlier note said "fully hazed from ~34000 up";
  the haze is complete past 30000 from any height, but above ~20000 the colour also runs away.) Within the game's range the
  optical depth never goes below −0.26 per sample (eye up to 15000, every distance and hour), the skydome's never below 0.35.
- **Not there**: no exponential or height-based haze in the main chain (`SkyX_Fog*.hlsl` and ground fog are separate features:
  ground fog is deprecated, fog planes/spheres/beams are the placed "fog volumes", `fogfeatures.dat`, [fogfeatures.md](fogfeatures.md):
  their blocks are height-limited, but they are separate objects drawn after the haze, not part of it).

### In the viewer: `--haze kenshi` (default) and `physical`

`atmoApply` has two branches (`--haze kenshi|physical`, `SkyRenderer.KenshiHaze`; the sky itself is the same in both):

- **kenshi** (the default): the game's formula, evaluated per pixel in the object, terrain, foliage and water shaders instead of in a
  separate pass over the G-buffer (same result for opaque surfaces). Alpha: the ramp from `0.06 D` to `min(D, 0.6 D)`, D = 50000
  (`--haze-distance` sets D: view distance × 10). D is not the viewer's far clip (about 357000): past D the game's formula simply goes
  on. Colour: `KenshiHaze.Colour` / GLSL `hazeColour`, SkyX's exposure × Rayleigh phase × `invλ⁴ · Kr · sun` × the 4-sample
  in-scattering from SkyX's camera to the point mapped by the dome radius 70000, with the game's lift and -0.3 clamp: the game's
  own expression, in the same HDR units as the sky and the lit scene, so the exposure treats them alike. At night the integral
  vanishes and the haze is black, as in the game. Then `horizonClouds`: its pull is the game's (0 in clear weather); its colour is the game's
  (`CloudLayer.HorizonColour`: the same `sunColour.rgb` and `zenithLight` as the cloud pass, [clouds.md](clouds.md)), from the same cloud density c as
  the layer. The weather fog (the scheduler's blend, or `--weather <name>`; weight 0..1 = the game's `fogColour.a`, which scales the alpha): `fog color · sunColour.w`, the game's
  ease-in-out curve over `distance / fog distance max` (the viewer has no wind; the game uses the same distance for every base weather, see the WEATHER table above),
  alphas added. A consequence that looks odd but is the game's rule: at night distant terrain goes black against the night sky.
  Not reproduced: the water being fogged by the depth of what is under it.
- **Above the game's camera heights** (a viewer choice; no game behaviour exists there): the viewer allows any height, and the
  game's formula from far above is wrong (above: a dark brown band, a white arc in the thousands, cyan rims where it fades). So
  with `kenshi` the haze blends towards `physical` as the eye climbs: weight `smoothstep(5100, 18700, clearance)`, clearance being
  the eye's height above the highest ground or water at the eye and on rings out to 2000 round it ([camera.md](camera.md), in-game
  under ~1840). Below 5100 it is exactly the game's haze (with the strength at 1, the Faithful haze; the Meitou default is 0.93, see below) (screenshots before and after the change pixel-identical:
  `--town "The Hub" --distance 40000 --pitch 3` at 6, 13, 22.6, 23.3 and 1 o'clock, and `--at -51468,-14324 --yaw 95 --pitch 2
  --distance 300` at 13 with `--no-foliage`; with foliage that view already differs from itself run to run by the same margin); from 18700 up it is the physical haze, which looks like a map seen from an aircraft (the haze thins as
  the eye climbs out of the dense low air). In the band both are evaluated and mixed (the weight is per frame, so outside it only one
  runs). The water's widened sun glint is weighed by the game's Fresnel term (F0 0.04) by the same weight (viewer choice; it
  otherwise became a large blown-out disc on the sea from high up).
- **A guard in the integral** (viewer): each sample's optical depth is floored at −1 (`SkyXModel.OpticalFloor`, also in the GLSL),
  which bounds the runaway; it changes nothing within the game's range or for the sky (minimums above).
- **Haze strength** (viewer option, not the game's: `--haze-strength <x>`, the Tab panel's "Haze strength (1 = game)", 0 to 3,
  default 0.93, the Meitou haze switch, so far mountains stay visible; 1 = the game's, the Faithful side, F3 in the viewer): multiplies how far the atmosphere haze is blended in (the game's ramp, or the physical haze's amount,
  capped at 1), before the weather's fog, which it leaves alone.
- **At night** (**Verified**: `KenshiHaze.Colour` is exactly 0 at 3000, 10000, 30000 and 50000 units for a sun 60° under the horizon): the haze colour is SkyX's
  sunlit in-scattering, which is black once the sun is down, so the ramp turns everything past 0.06 D darker and everything past 0.6 D (30000) black against a
  black sky: only the land round the camera stays lit (the game's rule; `--faithful night` keeps it, pixel for pixel). The Meitou `night` switch ("Night air")
  fills it in: [Night air (Meitou)](#night-air-meitou) below.
- **physical** (`--haze physical`, all heights; a viewer alternative, not the game's): the closed-form optical depth of SkyX's own air (its Rayleigh and Mie depths
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
- **Stars** (since 2026-10-10): `SkyX_Starfield.dds` whole (4096², BC1, all 13 levels, repeating) on the game's mapping and shift
  (`Starfield.Uv` in `NightSky.cs`: azimuthal-equidistant round the zenith, `+ 0.05 ×` game hours on both axes), times SkyX's night factor,
  `(0.35 + saturate(−sunY · 0.45))` and the HDR ×2. The hours are the game's since the load (`Gpu.GameHours`), else the viewer's clock at
  game speed 1 (the heat haze's). Trilinear where the game takes the top level only: at the game's screen sizes that is the top level too.
  Until then the viewer took a 1024² level and laid it stereographically over the hemisphere, about 3× coarser than the game: blurred, blocky stars.
- **Planets** (since 2026-10-10; replaced the `SkyX_Moon.png` disc opposite the sun, which the game never draws): both, as above, hit
  analytically in the sky pass (ray against a sphere of the angular radius in the fixed direction), the object normal turned back by the spin
  gives the mesh's UV, `textureGrad` with the gradients of whichever of u and u + ½ is continuous (no seam), coverage antialiased over a pixel.
  Colour `texture × saturate(n · sun) + SkyX's Rayleigh colour towards the pixel` (`SkyRenderer.planet`). Drawn after the stars and before the
  clouds; the weather's fog over the sky covers them as it covers the dome. The day is the weather's (`WorldWeather.Day`), the hour the clock's.
- **Clouds** (the weather's density, or `--clouds <0..1>` as a test override; `--cloud-wind <x>,<z>` for the drift): the game's
  planar layer, drawn in the sky pass after the stars and before the moon, alpha-blended in HDR ([clouds.md](clouds.md#in-the-viewer)).
  `SkyRenderer` takes the density (`CloudDensityInput`), the sky colour multiplier (`SkyColourMultiplierInput`) and the wind velocity
  (`CloudWind`, advanced by `StepClouds`) as inputs; the same density drives `horizonClouds`.
- **Weather tint** (corrected 2026-10-08): the game applies `sky color mult` only to the `zenithLight` / `nadirLight` colours (clouds and
  `horizonClouds`), see the WEATHER table above; the skydome is **not** tinted (the viewer used to multiply it: a blue dome times the
  dust storm's orange gave an olive sky; removed). A WEATHER record without the field reads **black** (0), not white (**Verified
  (decompiled)**, FUN_1409de7b0: the colour is `setAsARGB` of a map `operator[]` lookup that inserts 0; same for `fog color`), which only
  changes the cloud light's zenith part (floored). The sky pixels are fogged by the weather's fog like everything else (Haze below).
- **Overcast horizon** (fixed 2026-10-08): in the Vain (regions Vain and Arach, season "Vain": `Kenshi_red_rain` ×60, c = 1, sky multiplier
  white, no fog; `clear nothing` ×10, c = 0) the viewer drew a white band between the far terrain and the clouds; the game's horizon is
  the dark grey of the overcast. Not the fog, the sky multiplier or the haze pull: it was the cloud pass's horizon fade (the sparkle fix,
  [clouds.md](clouds.md#in-the-viewer)), whose target colour `zenithLight + sunColour.rgb` lacked the layer's darkening; the band's alpha there
  is `o + 0.5` = 1, so it covered the sky fully, about five times brighter than the clouds above. In the game the pixel below 2.9° keeps the
  darkening `1 − saturate(D) · Darkness` (**Verified**, `SkyX_Clouds.hlsl`: × 0.19 at c = 1). The fade's target is now the `horizonClouds`
  colour (a viewer choice, **Observed** to match: the band and the hazed far terrain meet in one dark grey; screenshots `--at -94464,-11520
  --yaw 90 --pitch 6 --distance 1800 --time 13 --weather Kenshi_red_rain`). Clear weathers skip the cloud pass and are unchanged.
- **Light**: the deferred lighting pass's model ([lighting.md](lighting.md)): `kenshiLight` in the mesh, terrain and grass shaders.
  The water still takes a sun colour and an ambient (`WorldLighting`): `π · 0.96 · sunColour.rgb · w` and the irradiance cube's up
  and down faces times the environment factor. At night the Meitou `planetshine` switch changes which light this is (the planet's, below).
- **Exposure**: in game-sky mode the post-processing measures the frame's mean luminance and scales by `0.55 / clamp(mean,
  MIN_LUMINANCE, MAX_LUMINANCE)` with the game's band ([post-processing.md](post-processing.md)).
- `--simple-sky` (key `B`) switches back to the old colour model, the old light and squared-distance fog, with a fixed exposure,
  for comparison.

### Planetshine (Meitou)

**The game has nothing like it** (**Verified**, [lighting.md](lighting.md): the night land is lit by the flat irradiance ambient alone, the
sun's light is zero from a sun height of −0.093, and the planets are only drawn, never lights). Meitou lets the big planet (Moon,
`SkyPlanet.All[0]`) light the land at night; `--faithful planetshine` (or the Tab panel's checkbox) is the game's flat night.
Everything is in `Planetshine` (`NightSky.cs`, tests `PlanetshineTests`) and `SkyRenderer.Prepare`.

- **Physics** (textbook, **Verified** by the unit tests): the planet is a Lambert sphere lit by the sun, so a surface facing it receives
  `E / E_sun = A · (Ω / π) · Φ(α)`. `A` is the planet's mean albedo; `Ω = 2π (1 − cos r)` its solid angle (`r` = 9.09°, so 0.0790 sr; Moon2's
  2.59° gives an eighth of that and is **left out**); `Φ(α) = (sin α + (π − α) cos α) / π` the Lambert phase function; `α` the phase angle
  at the planet between the sun and the observer. Sun and observer are far from the planet, so `cos α = −sun · towardsPlanet` (sun = the real sun's
  direction from the eye): α = 0 with the sun behind the eye (a full planet), π with the sun behind the planet. It is the same phase the sky pass
  draws (`saturate(n · sun)` at the disc's centre), so the land is brightest when the disc is.
- **Phase through the night** (latitude 54, sunrise 5, sunset 23, the planet at +X, 12° up; **Observed**, screenshots below): the sun sets at −X,
  opposite the planet, so it is gibbous at dusk and a crescent by dawn: α = 46° at sunset, 56° at 23:30, 68° at midnight, 116° at 2:00,
  145° at 4:00, 134° at sunrise; Φ = 0.75, 0.65, 0.53, 0.13, 0.02, 0.05.
- **Colour**: the planet texture's mean, computed once at load (`Planetshine.MeanAlbedo`): the first mip at most 128 wide of `moon_HI.dds`
  (128 × 64), the raw texel values the sky shader uses as albedo (BC1 is sampled unorm, no sRGB step), each row weighted by `sin(v π)` (the
  sphere's area in it). **Observed**: (0.284, 0.293, 0.259), nearly grey. A missing texture falls back to a 0.3 grey (**Unknown**).
- **Strength**: the light is `E_ref · A · (Ω / π) · Φ(α) · strength` in the unit of `uAtmoSunLight`, with `E_ref` the luminance of
  `KenshiLighting.SunLight` for the sun at the zenith (**Observed**: 0.3185; the planet sees the sun without our air, so a fixed reference, not
  the sun's own light, which is zero at night). The physical value (strength 1) is small *against the game's night*: a full planet of this albedo sends
  0.0071 of the sun (0.0023 in light units), and the real phase 0.0015 at 23:30 and 0.0003 at 2:00 (light units), while the game's night ambient on an up-facing white
  surface is `0.96 · 1.2 · 0.2 = 0.23`, about a quarter of what the noon sun gives one facing it (`π · 0.96 · 0.3185 ≈ 0.96`). So the default
  `Enhancements.MeitouPlanetshineStrength` is **60** (`--planetshine-strength <x>`, the Tab slider "Planetshine strength (x physical)", 0 to 200):
  at the brightest part of the night (23:30) a face turned to the planet gets about the ambient's level again, so the relief reads (rock faces
  towards the planet lit, the others and the ground at slope in the ambient's flat grey), and by 4:00 the crescent adds almost nothing, as it
  should. Against real moonlight the physical value is strong (about 3000 ×: the planet covers 0.079 sr, the Moon 7e-5, at a larger albedo), but
  the game's night is nothing like a physical one. Where it reads (**Observed**, `--yaw 135`, 1280 × 720): faces towards the planet (rock towers, walls)
  come up clearly, flat ground, which the planet 12° up meets at `N·L` 0.2, gets about a fifth of what such a face does, so on open land it is
  subtle and the relief and the long shadows carry it. Chosen by eye on the screenshots, not measured against anything: **Unknown** what the
  game's artists would have wanted; the slider is there for that.
  The auto exposure does not undo it: the night's mean luminance stays under the floor (`exposure min × night darkness` = 0.28, scale ×1.96):
  at `--at -51468,-14324 --distance 3000 --pitch 8 --yaw 135` 0.092 → 0.103 at 23:12, 0.047 → 0.055 at 0:00, 0.047 → 0.049 at 2:00, 0.047 at 4:00.
- **Twilight blend**: `Planetshine.Weight(sunY)` is 1 up to a sun height of −0.093 (where the game's sun light ends) and 0 from −0.04
  (2.3° down, the sun's light still a quarter of its horizon value), smoothstep between: the planet's light is added to the sun's only while the sun's
  last light fades. (A first version blended from +0.06; the planet then took the shadows' direction round while the sunset still lit the land at
  half the noon sun's brightness, and it overlapped the light shafts, which run to −0.02. Narrowed 2026-10-10.) One directional light stands for both
  (`Planetshine.Combine`): the colours add, the direction is the sun's (clamped, as the game) and the planet's weighted by the square root of
  their luminances, so it follows the brighter one and turns from the setting sun's horizontal light to the planet's (12° up). **Observed**
  (`SkyClock` 54°, sunrise 5, sunset 23, strength 60, light as a share of the noon reference): at dusk the sun's own light is 0.47 at 23:03, 0.30 at
  23:06, 0.17 at 23:09 (planet 0.01), 0.08 at 23:12 (planet 0.11, the direction already 74° round) and 0.0007 at 23:18, when the planet's 0.30 is all
  there is and the direction 125° round. So the turn comes in about ten game minutes while the total light is 0.18 to 0.19 of the noon sun's, deep
  twilight with the sun 3° down, **not** near zero: the planet's light at this strength is itself 0.28 to 0.30 of the noon sun's, so no moment
  has both near zero. The unit test asserts the quickest turn comes under 0.2 of the reference, the greatest step under 5° per 0.05° of sun and no
  brightness jump over 10 %, for dusk and dawn. At dawn the crescent is faint (0.013 to 0.017) and the direction turns back to the sun's by
  sunY −0.04 with the planet's light under 0.01 beside the sun's 0.08.
  Without the planet's light (switch off, or `Weight` 0 above −0.04) the sun's direction and colour come back bit for bit.
- **What follows it** (everything reads the one published light, so nothing needed a shader change): `uAtmoLight.xyz` and `uAtmoSunLight`
  (`kenshiLight` in the terrain, object, foliage, grass and character shaders, the water's shore shading, the GI probes' sun term and their
  traced shadows), the `WorldLighting` handed to the terrain, objects, foliage, characters and the water, and the shadow cascades' direction.
  **Left as the sun's**: `uAtmoSun` (the sky's scattering, the stars' night term and the planet's own phase), the ambient (`uAtmoLight.w` is
  computed from the *sun's* clamped direction: with the planet's, 12° up, the factor `clamp(5 L.y + 0.2, 0.1, 1)` would rise from 0.2 to 1
  and quintuple the night ambient), the exposure floor, the cloud light, the weather fog's `sunColour.w`, and the light shafts (their
  strength is already zero from a sun height of −0.02, [render-shafts.md](../render-shafts.md)).
- **Shadows**: the cascades are fitted and drawn for the published direction, so at night they follow the planet; the pass's cut-off on the real
  sun height (−0.2) is lifted while the planet lights the land (`SkyRenderer.ShadowSunHeight`: the sun's height, but at least 0 once the planet's
  luminance is over 1e−4 of the reference). A planet at 12° throws long shadows (3 to 5 times an object's height); the cascade fit already handles the
  sun at the horizon (the game draws the dusk map along a horizontal light), so nothing was changed in it. **Observed** (`--no-shadows`
  against the default, midnight, same view): 11 % of the pixels differ by more than 6/255 (sum over the channels), mean 2.4. When the planet is
  new (luminance under the floor) no map is drawn.
- **Water**: both water shaders take the published direction and colour as their sun for the lit water (diffuse, crests), so by night the sun glint was a planet glint
  (**Observed**, `--town Shark --distance 2000 --pitch 10 --yaw 315 --time 23.5`, `--faithful planetshine` beside the default: a blown-out white patch in the water
  between the huts, about 100 of 255 on the glint, the brightest thing in the frame bar the lamps, absent in Faithful). Its cause: the glint took the strength-scaled
  light (×60 on the physical value) while the planet in the sky pass is not scaled. **Fixed 2026-10-10**: `WorldLighting` carries a separate glint light
  (`GlintDirection`, `GlintColour`; `Glint` gives the sun's own when none is set) to both water shaders (`uGlintDir`, `uGlintColour`, used for the specular lobe, its
  half vector and the wet sand's sheen, not for the diffuse light or the crests). While the planet is in the light (`Planetshine.Weight` above 0) it is
  `Planetshine.Combine` of the real sun's radiance (`sunLight · π · 0.96`, what `SunColour` is) and the planet disc's radiance `Planetshine.DiscRadiance`
  (`albedo · Φ(α)`, times the weight; the sky pass draws `albedo · cos α` at the disc's centre, which is dark from a quarter phase on while a lit crescent remains, so the glint
  takes the Lambert phase function instead: 1 full, 0.32 at a quarter, 0 new), the direction weighted by their luminances' square roots as the land's light is. Above
  the weight's window (day) it is left unset, so the glint takes `SunDirection` and `SunColour` unchanged: **Verified** day pictures (`--time 13`) identical to before
  the change (1 pixel of 921 600 differs by one level, the foliage's noise). **Observed**: the patch is now 62 to 77 of 255 at 23:30 (the lit disc in the sky 85 to 165 over its face),
  27 to 41 at 0:30, 27 at 1:00 and 9 at 2:00, following the phase down, grey like the planet, and absent with `--faithful planetshine`; it is still a point-source glint for a disc 18° wide (a hot spot where the real reflection would be a broad smear, and with no Fresnel term,
  which would lower it further at this grazing angle: **Unknown** whether the owner wants either).
- **Not exercised**: the probes' light with `--gi` (they read the same published light: a run at 23:30 gave no error and the mean luminance 0.055 →
  0.065 against Faithful, but the picture was not inspected for the bounce), the simple sky (`--simple-sky` keeps its own light), and weather other than
  `Default` at night.
- **Not done**: Moon2's light (an eighth of the solid angle, 11° from the large one); the cloud layer lit by the planet; fog volumes and particles
  keep the sun's `Daylight`; the planet's light passing through the atmosphere (reddening near the horizon); a planet eclipsed by the land.
- Screenshots (`C:\Temp\meitou-planetshine\` while the work was done; `--at -51468,-14324 --radius 2 --distance 3000 --pitch 8 --yaw 135
  --weather Default --size 1280x720`, time 23.2, 0, 2 and 4; `--faithful planetshine` beside the default; yaw 315 faces the planet).
### Night air (Meitou)

The `night` switch (label "Night air"; since 2026-10-10 it replaced a thinning of the haze to a quarter of its strength, which kept the far land visible but
lit as brightly as the near land by the flat night ambient against a pitch-black sky: ridges looked pasted on and snowy peaks glowed on black). **The game has
nothing like it**: Faithful (`--faithful night`) is the game's black haze and SkyX's own night glow, byte for byte (**Verified** 2026-10-10: the open-land and
high views at 1:00 are identical pixel for pixel before and after the change; day and dusk differ by one pixel of one level, the foliage's run-to-run noise).
Reference math in `NightAir` (`NightSky.cs`, tests `NightAirTests`), shader side in `AtmosphereShaders` (`atmoNightAir`, `hazeTarget`).

- **Colour** (HDR, in the units of the haze colour): `NightAir.Colour(planetLight) = Airglow + 0.25 · luminance(planetLight) · (0.8, 1.0, 1.4)`. `Airglow` is
  (0.030, 0.045, 0.068), luminance 0.043: a cool, slightly green glow. The planet's light is the strength-scaled `Planetshine.Light` (the Tab slider moves it),
  worked out whatever the `planetshine` switch says, so the air follows the planet's phase: the planet adds 0.51 of the airglow's luminance at 23:30 (gibbous,
  Φ 0.65), 0.42 at midnight, 0.10 at 2:00 and 0.02 at 4:00 (a thin crescent), so the air is darker in the small hours (game clock: latitude 54, sunrise 5, sunset 23; **Verified** by `NightAirTests`). The blue tint stands for Rayleigh scattering. **Unknown**: any physical basis for the
  gain (0.25) and the airglow level; both **chosen by eye** on screenshots (a first pair, a seventh of the ambient-lit land, drew nothing visible: the horizon came out
  at 1 to 3 of 255; the present pair gives a horizon of about (10, 16, 27) of 255 on screen after exposure ×1.96, tone map and night grade, against
  (20 to 35, 24 to 36, 35 to 50) for the planet-lit ground at mid-distance (rows 300 to 400) and (0, 0, 1) at the zenith). **Verified** that the exposure did not move: the near ground (rows 450 to
  700 of the same view, under 3000 units, where the haze has not begun) is the same before and after to within a level (e.g. (21, 24, 35) and (20, 24, 36)), so the frame's mean stays under the
  exposure's night floor and the scale stays ×1.96.
- **In the haze** (`atmoKenshiHaze`, also the fog volumes' haze colour): the colour the far land fades into is `hazeColour(ray) + atmoNightAir(direction)`;
  `hazeColour` is the game's (black at night), the weather's fog and the horizon-cloud pull still apply over it (`mix(hazeTarget, cloud, pull)` then the fog), so a foggy
  or overcast night stays the game's. The alpha ramp is the game's (0.06 D to 0.6 D) times the haze strength (0.93), untouched: the far land fades **into** the
  air's colour instead of black, near land stays lit, far land progressively the air's colour. Where the air is brighter than the land (shadowed ridges)
  they read dark-on-glow.
- **In the sky** (`atmoSky`, so the sky pass, the water's reflection of the sky and the physical haze all see it; drawn under the stars and planets, so it
  works with both `stars` settings): `+ atmoNightAir(d)` where `d` is the direction with its height clamped at 0 (below the horizon the horizon's colour), and SkyX's own
  night glow (`night · ((0.05, 0.05, 0.1) (1 − y)³)^2.2`, **Verified** nonzero at the horizon, (0.0025, 0.0025, 0.0115) for a sun 64° under it, 0.0013 at y 0.1)
  is scaled by `1 − weight` so the two never add: the game's glow gives way to the night air as it comes in.
- **Horizon falloff**: `atmoNightAir(d) = colour · weight · exp(−max(d.y, 0) / 0.2)` (`NightAir.HorizonFalloff`): 1 at and below the horizon, 0.43 at 10° (y
  0.17), 0.18 at 20°, 0.08 at 30°, 0.007 at the zenith. A real airglow band peaks about 10° up; a plain exponential from the horizon is the simple choice (**Observed**:
  the stars keep their contrast above 40°). The haze evaluates the same function of the direction **from the eye to the surface**, so a fully hazed ridge at
  1° and the sky right above it are the same colour, and hazed ground, which is below the horizon, gets the horizon's colour. **Observed**: a vertical line through the horizon
  of `--at -51468,-14324 --distance 3000 --pitch 8 --yaw 95 --time 1`, in 8-pixel steps, goes (0, 1, 5), (1, 4, 10), (4, 9, 17), (5, 10, 20), (8, 15, 26), (10, 16, 27) of 255 up to
  the first land, with no step where the hazed far terrain takes over.
- **Twilight**: the weight is `Planetshine.Weight(sunY)`, the deep-twilight window (1 up to a sun height of −0.093, where the game's sun light ends, 0 from −0.04, smoothstep
  between), so the sunlit haze fades out as the sun's last light does and the air comes in while it does, never over daylight; added to the haze's own colour, not mixed with it,
  so nothing dips. **Verified** (`NightAirTests`, 0.02° steps from 10° up to 40° down, dusk and dawn, looking towards and away from the sun at 0.6 D): the haze
  target's luminance never exceeds its value with the sun on the horizon and never steps by more than 10 %. **Observed**: the mean luminance of the horizon band (rows 150 to 260 of
  the land view, 1280 x 720, screenshot values 0..255) at 22:54, 23:00, 23:06, 23:09, 23:12, 23:15, 23:18 and 23:24 (`--time 22.9` to `23.4`) is 85, 85, 70, 62, 57, 56, 53 and 37 against
  85, 85, 70, 61, 53, 46, 38 and 22 for Faithful: no rise, no pop where the air comes in (it is 0 up to 23:06); the blue channel of the mean rises a little (43 at 23:12 to 49 at 23:18)
  as the red of the sunset fades and the air takes over.
- **Not done**: the planet's direction in the glow (it is the same all round the horizon, not brighter towards the planet); the planet's colour (the tint is blue whatever
  the albedo); the air for the simple sky (`--simple-sky` has none); `--haze physical` far above the ground gets the glow only through `atmoSky`.
- **Left open** (**Unknown**, not exercised): weather with a fog colour at night (the weather fog's colour is black at night, `fog · Daylight(sunY)`, and mixes over everything
  including this glow, as it does in the game), dust storms and rain.

### Meitou night sky (the `stars` switch; the viewer's own design, not the game's)

Faithful (`--faithful stars`) is the game's starfield texture as above, pixel for pixel. Meitou (the default) replaces the texture with a procedural
sky in the same sky pass (`SkyRenderer`, GLSL `starPoints`; the reference math is `MeitouNightSky.cs` in `Meitou.Data.World`
with tests in `MeitouNightSkyTests`). Nothing here is read from the game except the latitude and the sun's path. Status of every claim: **Observed**
(screenshots in the viewer, cost measured on an RTX 4070 shared with other programs, so the minimum of five runs) unless it says **Verified** (a unit test).

- **Rotation** (`CelestialSphere`). `SkyClock.SunDirection` puts the sun on the plane spanned by (1, 0, 0) and (0, cos lat, sin lat), so it turns about
  their cross product, (0, −sin lat, cos lat); the celestial pole is the end of that axis above the horizon, (0, sin lat, −cos lat), 54° up towards −z
  (the side opposite the noon sun, +z). The stars turn about it the way the sun does (up from +x, over +z, down to −x) once per game day, driven by the
  game's day and hour (`SkyRenderer.Day`, `Hour`), with a fixed turn at hour 0 (`PhaseOffset`, −60°, a viewer choice). **Verified**: the axis is perpendicular to the sun's direction at any hour, the pole is at the latitude,
  a star on the equator rises east, culminates due south at 36° and turns the sun's way, a day later everything is back. The sun's own clock is not
  uniform (18 h of day and 6 h of night for each half turn) while the stars' is, so they share the axis but not the rate. The planets stay where the
  game puts them (they are drawn after the stars and cover them). There is no yearly drift: the same stars cross at the same hour every night
  (**Unknown** whether to add one).
- **Point stars** (`StarField`). A direction in celestial coordinates → a cube face → a grid of cells, in three layers: bright (28 cells a side, 1800 stars
  of magnitude −2 to 4.5, a flatter slope so a few bright ones show on any screen), main (64 a side, 8100 stars, 4.5 to 6.5) and faint (160 a side, 45000,
  6.5 to 8.2), each a power law of about 3.16 times more stars per magnitude. A cell's chance of a star is the layer's density times the cell's solid angle
  (a cube face cell shrinks towards the corners; **Verified**: no denser at the face centres), the same everywhere on the sphere. A one-word
  hash of the cell decides presence, three more words give the position in the cell, the magnitude, a colour and a twinkle phase. A star is a Gaussian of
  0.7 pixels (sigma) with its energy kept, peak `brightness / 2πσ²`, the brightest with a wider halo; the distance is taken in pixels through the
  derivatives of the direction, so the stars are the same 1 to 2 pixel points at 720p and at 4K and never squares (a star's spot is never cut at a cell
  edge: the nearest neighbouring cells are searched, and none sits within a quarter cell of a face edge). Brightness is `10^(−0.4 · 0.7 · m)`: the real
  sky's range compressed so faint stars show beside bright ones (**Verified**: 25 times between magnitudes 1 and 6, not 100). Colours: B−V index from a
  mixture of blue, white, yellow and red stars → temperature (Ballesteros) → a blackbody's tint (Kim's fit of the Planckian locus), kept 30% saturated,
  at luminance 1 (a table of 32 that a hash indexes). **Verified**: each layer holds its count, thousands of stars brighter than magnitude 6 (not tens of thousands), 30 to 250 brighter than magnitude 1.
- **No Milky Way** (removed 2026-10-10 at the owner's request, after review in the viewer). There was one: a band inclined 62° to the celestial equator
  with a bulge, baked into a 512² cube map (a 0.9 s CPU bake, 16.8 MB of video memory) plus a per-pixel grain of faint stars, dust lanes made crisp per pixel, and a
  denser band of the faint star layers along it. All of it went, the denser band too (it would still have read as a Milky Way); the star layers kept their
  counts, so the sky away from the old band is unchanged star for star. The code is in git history before the removal (`MilkyWay` in `MeitouNightSky.cs`).
- **Horizon** (`NightAtmosphere`). Extinction by the air mass (Kasten and Young) with e^−k per air mass of (0.08, 0.13, 0.22) for red, green, blue: low stars
  dim and redden; a ramp takes everything to 0 between 3.5° and 0.5° altitude. Stars twinkle only where the air mass is above 1.8 (below about 33°): two slow
  sines per star, up to ±50% at the horizon, none overhead; the clock is the viewer's (still for a screenshot).
- **Brightness and the fade**. The same term as the game's: SkyX's night factor × `(0.35 + saturate(−sunY · 0.45)) · 2`, so the stars come out as the
  sky darkens exactly as the texture's did, and the units are the starfield texture's (peak stars at about the texture's own values). Mean luminance of the same view, Meitou against Faithful: 0.011 against 0.048 looking straight up, 0.029 against 0.043 at the old band, both with the Milky Way
  still in (the point stars match the texture's bright stars; without the band Meitou is darker still). Both stay under the exposure band's floor (0.28, the night's adapted exposure ×1.964 in either), so
  switching does not move the exposure. Sampled outside the Meitou branch the sky pass is unchanged (Faithful's picture is byte-identical to
  the one before the switch existed). While the sun is above 0.3 the stars are skipped altogether (SkyX's night factor is 0 then).
- **Cost** (sky pass, 1920 × 1080, RTX 4070, minimum of five runs, 2026-10-10, measured with the Milky Way still in, so its removal makes Meitou a little cheaper; not re-measured): Faithful 0.12 ms, Meitou 0.34 ms (0.31 before the per-pixel grain and crisp dust: the
  grain and the dust edges cost 0.03), 0.26 ms with the faint layer left out (grain still on); 4K 1.11 ms (the same per pixel); 0.11 ms by day (skipped). Per pixel the work is bounded: three layers, each looking at one to four cells (a layer's cells that
  can reach the pixel at all), an early test against the largest chance before the exact one, the three-word hash only for cells that hold a star. An
  integrated GPU draws the first two layers (`StarField.IntegratedLayers`; `MEITOU_FORCE_INTEGRATED=1` shows it elsewhere, `MEITOU_STAR_LAYERS=0..3` sets the layers).
- **Water**: the reflection pass draws the sky with the same program and the reflected view, so the reflected sky has the same stars (the derivatives give the
  pixel scale of the reflection target).

### Using the atmosphere in a new shader

Include `AtmosphereShaders.Functions` in the fragment shader (after `#version`), light the surface with
`kenshiLight(albedo, normal, towardsEye, gloss, worldPosition)` in game-sky mode (`uAtmoParams.x > 0.5`), end with
`colour = atmoApply(colour, eyePosition, worldPosition)`. A native program (`LegacyProgram`, or the native model's frame constants) gets
the uniforms and the three textures (irradiance and specular cubes, ambient map) from the frame globals the `SkyRenderer` publishes
(docs/renderer-native.md 4.3, 8.6). A GL program, while any is left, calls `AtmosphereShaders.AssignSamplerUnits(gl, program)` once after
linking (the cube and ambient-map samplers go on the top three texture units); nothing binds textures there any more. `SkyRenderer.SkyFunctions` additionally has `skyColour(direction, disc)` for reflections. In game-sky mode colours are
HDR in the game's units; the post-processing's exposure brings them to the screen.

### Not reproduced

Volumetric clouds, lightning, cloud lighting from the sun's direction (the game has none), SkyX's ground fog, and SkyX's per-vertex
evaluation (the viewer's is per pixel; the planets' sky colour is per pixel too, the game's per vertex of the planet mesh). SkyX's moon
billboard is not drawn because the game does not draw it.
