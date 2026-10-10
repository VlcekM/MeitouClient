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
- **Night haze** (the `night` switch, since 2026-10-10; Meitou thinned, the default, Faithful black, the game's): the game's haze colour is
  SkyX's sunlit in-scattering, which goes to black once the sun is down, so at night its ramp turns everything past 0.06 D darker and
  everything past 0.6 D (30000) black: only the land round the camera stays lit, the rest is a black band under the stars (the game's rule, see
  above; screenshots `--at -51468,-14324 --distance 3000 --pitch 8 --yaw 95 --time 1 --weather Default` with haze strength 0.93 and 0 show
  the far terrain and a lit town only without the haze). Meitou multiplies the haze strength by `SkyRenderer.NightHazeFactor(sunY)`: 1 from
  sunY 0.05 up, `Enhancements.MeitouNightHazeFloor` (0.25) from −0.15 down, smoothstep between, so by night the far land fades a quarter of
  the way to black and stays visible. The colour stays the game's; the weather's fog is untouched.
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
  and down faces times the environment factor.
- **Exposure**: in game-sky mode the post-processing measures the frame's mean luminance and scales by `0.55 / clamp(mean,
  MIN_LUMINANCE, MAX_LUMINANCE)` with the game's band ([post-processing.md](post-processing.md)).
- `--simple-sky` (key `B`) switches back to the old colour model, the old light and squared-distance fog, with a fixed exposure,
  for comparison.

### Meitou night sky (the `stars` switch; the viewer's own design, not the game's)

Faithful (`--faithful stars`) is the game's starfield texture as above, pixel for pixel. Meitou (the default) replaces the texture with a procedural
sky in the same sky pass (`SkyRenderer`, GLSL `starPoints` and the Milky Way lookup; the reference math is `MeitouNightSky.cs` in `Meitou.Data.World`
with tests in `MeitouNightSkyTests`). Nothing here is read from the game except the latitude and the sun's path. Status of every claim: **Observed**
(screenshots in the viewer, cost measured on an RTX 4070 shared with other programs, so the minimum of five runs) unless it says **Verified** (a unit test).

- **Rotation** (`CelestialSphere`). `SkyClock.SunDirection` puts the sun on the plane spanned by (1, 0, 0) and (0, cos lat, sin lat), so it turns about
  their cross product, (0, −sin lat, cos lat); the celestial pole is the end of that axis above the horizon, (0, sin lat, −cos lat), 54° up towards −z
  (the side opposite the noon sun, +z). The stars turn about it the way the sun does (up from +x, over +z, down to −x) once per game day, driven by the
  game's day and hour (`SkyRenderer.Day`, `Hour`), with a fixed turn at hour 0 (`PhaseOffset`, −60°, a viewer choice that has the Milky Way high in the
  south and west during the game's night, 23 to 5). **Verified**: the axis is perpendicular to the sun's direction at any hour, the pole is at the latitude,
  a star on the equator rises east, culminates due south at 36° and turns the sun's way, a day later everything is back. The sun's own clock is not
  uniform (18 h of day and 6 h of night for each half turn) while the stars' is, so they share the axis but not the rate. The planets stay where the
  game puts them (they are drawn after the stars and cover them). There is no yearly drift: the same stars cross at the same hour every night
  (**Unknown** whether to add one).
- **Point stars** (`StarField`). A direction in celestial coordinates → a cube face → a grid of cells, in three layers: bright (28 cells a side, 1800 stars
  of magnitude −2 to 4.5, a flatter slope so a few bright ones show on any screen), main (64 a side, 8100 stars, 4.5 to 6.5) and faint (160 a side, 45000,
  6.5 to 8.2), each a power law of about 3.16 times more stars per magnitude. A cell's chance of a star is the layer's density times the cell's solid angle
  (a cube face cell shrinks towards the corners; **Verified**: no denser at the face centres). The faint layers gather towards the Milky Way. A one-word
  hash of the cell decides presence, three more words give the position in the cell, the magnitude, a colour and a twinkle phase. A star is a Gaussian of
  0.7 pixels (sigma) with its energy kept, peak `brightness / 2πσ²`, the brightest with a wider halo; the distance is taken in pixels through the
  derivatives of the direction, so the stars are the same 1 to 2 pixel points at 720p and at 4K and never squares (a star's spot is never cut at a cell
  edge: the nearest neighbouring cells are searched, and none sits within a quarter cell of a face edge). Brightness is `10^(−0.4 · 0.7 · m)`: the real
  sky's range compressed so faint stars show beside bright ones (**Verified**: 25 times between magnitudes 1 and 6, not 100). Colours: B−V index from a
  mixture of blue, white, yellow and red stars → temperature (Ballesteros) → a blackbody's tint (Kim's fit of the Planckian locus), kept 30% saturated,
  at luminance 1 (a table of 32 that a hash indexes). **Verified**: about 4800 stars brighter than magnitude 6 after the faint layers' enrichment (thousands,
  not tens of thousands), 30 to 250 brighter than magnitude 1.
- **Milky Way** (`MilkyWay`). A band round a great circle inclined 62° to the celestial equator (so at latitude 54° it comes within 8° of the zenith), a
  flattened bulge at declination +15°, and nothing taken from Earth's sky. A 3D noise of the direction (no cube seams): a haze of unresolved stars with
  structure at several scales, mostly small ones down to the cube map's texel (the broad swells are weak, so it reads as grain, not clouds), thin dark rifts that
  run along the plane (noise stretched across the band, warped; ridged), a thin rift through the bulge, a few tiny dark clouds; bluish white, warm cream towards the bulge,
  the dust taking the blue first. Baked once on the CPU at the first use (about 1 s on all cores for 512² × 6, RGBA16F, box-filtered mip levels, 17 MB)
  into a cube map in celestial coordinates, so it turns with the stars. Brightness is a faint glow, well under the starfield texture's nebula
  (`MilkyWay.Scale`, `SkyRenderer.MilkyWayGain`; peak lumps about 0.1 in the texture's units, the nebula runs 0.1 to 0.3 over most of its area), the sky between the band and the stars stays black.
- **Horizon** (`NightAtmosphere`). Extinction by the air mass (Kasten and Young) with e^−k per air mass of (0.08, 0.13, 0.22) for red, green, blue: low stars
  dim and redden; a ramp takes everything to 0 between 3.5° and 0.5° altitude. Stars twinkle only where the air mass is above 1.8 (below about 33°): two slow
  sines per star, up to ±50% at the horizon, none overhead; the clock is the viewer's (still for a screenshot).
- **Brightness and the fade**. The same term as the game's: SkyX's night factor × `(0.35 + saturate(−sunY · 0.45)) · 2`, so the stars come out as the
  sky darkens exactly as the texture's did, and the units are the starfield texture's (peak stars at about the texture's own values, the Milky Way far
  under its nebula). Mean luminance of the same view, Meitou against Faithful: 0.011 against 0.048 looking straight up, 0.029 against 0.043 at the band
  (the point stars match the texture's bright stars; the Milky Way is deliberately much fainter than its nebula, which was the first thing to go: a
  first, brighter version read as overcast cloud). Both stay under the exposure band's floor (0.28, the night's adapted exposure ×1.964 in either), so
  switching does not move the exposure. Sampled outside the Meitou branch the sky pass is unchanged (Faithful's picture is byte-identical to
  the one before the switch existed). While the sun is above 0.3 the stars are skipped altogether (SkyX's night factor is 0 then).
- **Cost** (sky pass, 1920 × 1080, RTX 4070): Faithful 0.12 ms, Meitou 0.31 ms (the Milky Way 0.04, the layers 0.05 / 0.07 / 0.09 each), 0.24 ms with the
  faint layer left out; 0.11 ms by day (skipped). Per pixel the work is bounded: three layers, each looking at one to four cells (a layer's cells that
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
