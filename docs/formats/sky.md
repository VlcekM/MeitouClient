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
| Clouds | `SkyX_Clouds.hlsl`, `Clouds.dds`, `CloudsNormal.dds`, `CloudsTile.dds` | a planar layer: the view ray hits a plane, the texture's red channel is the density |
| Volumetric clouds, lightning, ground | `SkyX_VolClouds*.hlsl`, `SkyX_Lightning.hlsl`, `SkyX_Ground.hlsl`, `Noise.dds` | not used by the viewer |

The textures: `SkyX_Starfield.dds` is 4096² DXT1 with 13 mips (the Milky Way and stars on a black field; the shader scrolls it
with time), `Clouds.dds` / `CloudsNormal.dds` / `CloudsTile.dds` / `Noise.dds` are 1024² DXT1 without mips, `SkyX_Moon.png`
512² RGBA (a full moon disc on transparent), `SkyX_MoonHalo.png` 512 × 256 grey-alpha. `data/materials` also has `moon_HI.dds`
(4096 × 2048) and `moon2_HI.dds`: despite the name an equirectangular **planet** map (brown deserts, dark seas, polar caps),
with `planet01.mesh` and `moon_NML.dds`. So the game's sky has a large planet or moon as a mesh (**Observed**; its placement
and phase are **Unknown**, the viewer does not draw it). `data/materials/caelum` is the older Caelum sky add-on's folder
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
and kept the green 0.54; that was wrong. The viewer's sky model still uses 0.54 (`SkyAtmosphere.WaveLength`) until its grading
is redone; `SkyAtmosphere.GameWaveLength` / `GameExposure` hold the game's values, and the haze uses them. The table below is
for the viewer's 0.54 except where it says otherwise:

| Quantity | Value |
|---|---|
| thickness `outer − inner` | 0.5213 |
| `uScale = 1 / thickness` | 1.918 |
| `uScaleDepth = thickness / 2` | 0.2607 |
| `uScaleOverScaleDepth` | 7.359, so the density scale height is 0.1359 (the planet is 71.9 scale heights across its radius, the air 3.84 deep) |
| `invλ⁴` per channel | (9.473, 11.760, 26.680); the game's green 0.48 gives 18.84 |
| Rayleigh depth straight up from sea level, `Kr · 4π · invλ⁴ · scaleDepth` | (0.0683, 0.0847, 0.1924); the game's green 0.1358 |
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
- What the game's final brightness is: its HDR chain has no gamma step and no tone curve, only the constant exposure of 0.55 / 0.8
  to 0.55 / 0.96 (see [post-processing.md](post-processing.md); the CONSTANTS `exposure min` / `exposure max` are 0.8 / 0.95). The
  exact relation between the sky's radiance (exposure 1.4 on sun intensity 30) and the lit terrain's brightness is
  **Unknown**; the viewer calibrates by eye (below).
- The sun's light colour is taken from SkyX's colour at the sun's direction (Observed, terrain.md); the game draws no sun disc in
  the skydome shader (the Mie lobe is the glow). Whether the game adds a sun billboard is **Unknown**.

### Time and the CONSTANTS record (Verified: the merged load order, 2026-10-04)

`GLOBAL CONSTANTS` after all mods: `latitude` 54, `sunrise` 5, `sunset` 23, `night darkness` 0.35, `days per year` 100 (the base
`gamedata.base` record alone says 45 / 5 / 24; `Newwworld.mod` overrides). The sun's path is in
[terrain.md](terrain.md#sun-path-verified-kenshi_x64exe-sky-controller-and-the-constants-record). What `night darkness` does is
**Unknown** (the viewer uses it to set the night's ambient floor).

### WEATHER records (Observed: the base game's and the loaded mods' records, 2026-10-04)

Record type 80, found by name. Fields that concern the sky:

| Field | Type | Meaning (Observed) |
|---|---|---|
| `sky color mult` | int, packed 0xRRGGBB | multiplier of the sky's colour: white in clear weather, `FCAC7C` (orange) in dust storms, `C6C6FF` (cool) in misty rain, `9F8777` in black rain |
| `fog enabled`, `fog distance min` / `max`, `fog color` | bool, floats, int RRGGBB | the distance where the weather fog is complete, between min (calm) and max by the wind (Verified, see "Haze"; dust storms 7000 to 25000, "Desert Blaster" 700 to 4000); the colour is sand (`E9CB9E`) or white |
| `clouds density` | float | 0 clear, 1 overcast; `SkyBeam` has 20 |

Others (`rain intensity`, `wetness`, `dust`, `dust inside`, `dust slope`, `heat haze`, `wind speed min/max`, `wind intensity`,
`wind update time/limit`, `fog wind min/max`, `affect type/strength`, `effect strength min/max`, `start time`, `end time`)
are for rain, dust and wind effects and the weather's own schedule; not read by the viewer. The "Default" weather
(`5460-weather.mod`) is clear: fog off, clouds 0, both colours white. Which weather applies where (the game picks one per
region and time) is **Unknown**; `SkyWeather` reads a record, the viewer uses "Default" unless `--weather` names another.


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
  direction, and nearer it is a fraction of it (**Verified** from the two shaders; computed, `KenshiHaze.SkyFraction`): at noon,
  horizontal, about (0.07, 0.13, 0.18) of the sky at 3000, (0.56, 0.65, 0.72) at 30000 (red, green, blue) and (0.81, 0.87, 0.91)
  at 50000. So the veil is darker than the sky behind it and slightly bluer (the short path loses less blue); nearer ridges
  are darker layers, farther ones lighter, up to the sky's colour. At night both integrals vanish: the haze is black.
- **`horizonClouds`** (**Verified**, the same sky update; inputs partly Unknown): alpha `saturate(densityOffset + 0.5)`, where the
  cloud layer's `densityOffset` (SkyX's `uDensityOffset`, option offset 0x28) is set to `1.4 c - 0.8` from the weather's cloud density
  c (the layer starts at -0.8): **0 under a clear sky** (c = 0), full from c ≈ 0.93. Colour:
  `saturate(sun · (1 - 0.1 · (offset + 0.2) · densityMultiplier) + max(horizon, floor) · sun.g) · (1 - darkness) · sqrt(exposure)`,
  with `densityMultiplier` 3 (`uDensityMultiplier`, set at creation), `darkness` = `pow(saturate(c - 0.5), 0.3)` (`uDarkness`),
  `sun` = `sunColour.rgb` = SkyX's colour towards the sun nudged by (0, 0, 0.04), divided by `4 · exposure` (when the sun is above
  -0.2; else black), `horizon` = SkyX's colour along +Z times the weather's sky multiplier, `floor` a colour in the sky controller
  (offset 0xb8, its value **Unknown**). What `getColorAt` returns (tone-mapped or not) is **Unknown**, so the colour's brightness is.
- **Global (weather) term** (`fogValue`, **Verified**): `amount = saturate(distance * fogDensity)`, alpha an ease-in-out curve of it (`2a²`
  below 0.5, `1 - 2(a-1)²` above) times `fogColour.a`, colour `fogColour.rgb * sunColour.w` (`sunColour.w` is a daylight scale: half of a
  controller value at sunrise, rising with the sun's height, and falling to 0 just below the horizon). Result `colour = lerp(atmosphere,
  global, global.a)`, `alpha = saturate(atmosphere.a + global.a)`. **Verified** (the weather blender and `MainFog`'s setters): up to
  four weathers are blended by weight; each gives `fogColour.a` = 1 if `fog enabled` else 0, `fogColour.rgb` = `fog color`, and a
  distance `d = lerp(fog distance min, fog distance max, saturate((wind - fog wind min) / (fog wind max - fog wind min)))` (`fog
  distance min` when the two wind values are equal), the current wind being the weather's. `fogDensity = 1 / d` (1 when d is 0, where
  `fogColour.a` is 0 anyway). So the weather fog is complete at d. The "Default" weather has `fog enabled` false: no weather fog.
- It does **not** differ per biome (**Observed**: no biome field is read by the shaders or the sky controller's fog code).
- **Not there**: no exponential or height-based haze in the main chain (`SkyX_Fog*.hlsl` and ground fog are separate features:
  ground fog is deprecated, fog planes/spheres/beams are the placed "fog volumes", `fogfeatures.dat`).

### In the viewer: `--haze kenshi` (default) and `physical`

`atmoApply` has two branches (key F7, `SkyRenderer.KenshiHaze`; the sky itself is the same in both):

- **kenshi** (the default): alpha the game's ramp from `0.06 D` to `min(D, 0.6 D)`, D = 50000 (`--haze-distance` sets D: view distance
  × 10). D is not the viewer's far clip (about 357000): the viewer draws far beyond the game's D and simply goes on with the game's
  formula there, which is defined for any distance. Colour: the viewer's sky colour along the fog's ray (the horizon colour for rays
  below it) times `KenshiHaze.SkyFraction`, the game's integral to the point divided by the same integral to the dome (Rayleigh, 4
  samples, the game's wavelengths 0.57 / 0.48 / 0.44, the game's lift and -0.3 clamp, 70000 world units per SkyX unit): the colour
  and phase factors cancel, so this is the game's own fraction of its sky. Applied to the display-referred sky colour, as the game's
  chain has no gamma step (a viewer choice). The fraction is clamped to 0..1, and 0 when both integrals underflow (the sun far below the
  horizon: black, as in the game); that guard is the viewer's. From 70000 on the haze is the sky's colour, so the far terrain fades into
  the sky and the far plane is hidden. Then `horizonClouds`: its pull is the game's (0 in clear weather); its colour is a stand-in built
  the game's way from the viewer's sun light and horizon colour (the game's inputs are Unknown, above). The weather's fog
  (`--weather`, when enabled): its colour lit like the ground, the game's ease-in-out curve over `distance / fog distance max`
  (the viewer has no wind; the game would use a distance between min and max), alphas added.
  Consequences that look odd but are the game's rule: from a camera high above the ground (the viewer allows much higher than the game)
  everything is past 30000 and fully hazed, steep rays all take the one fixed ray's colour; at night distant terrain goes black against
  the night sky. Not reproduced: the game's sky hue (the viewer's sky still uses green 0.54, see above), the Mie glow of the viewer's sky
  (carried into the haze near the sun; the game's haze has no Mie colour), and the water being fogged by the depth of what is under it.
- **physical** (the earlier model): closed-form optical depth along the ray with height-dependent density, in-scattering of the
  sky colour, closing at `--fog` (default 250000).

## In the viewer

`SkyRenderer` (with `AtmosphereShaders` and `AtmosphereModel`/`SkyWeather` in `Meitou.Data.World`), all in the world view:

- **Model**: O'Neil's single scattering with the game's constants, as above, but with the exact planet/atmosphere geometry
  (the planet occludes the sun, so dusk and night come out right) and our own settings (`AtmosphereSettings`; these are
  choices, not game data):
  - *Mie*: 15 × the game's coefficient (its air is very clear; Kenshi's look is hazy), a scale height 0.3 of the air's, a
    forward-scattering aerosol phase function with `g = 0.75` (the game's 0.991 lobe is far too narrow for haze).
  - *Sky gain* 3.4 on the Rayleigh light, standing in for the multiple scattering that a single-scattering model lacks.
  - *Sun scale* 1.2: the sunlit side of a white diffuse surface gets `1.2 × transmittance × cos`.
  - *Ozone* (not in SkyX): a tent-shaped absorbing layer (peak coefficients (0.010, 0.030, 0.0013) per scale height, centred 2
    scale heights up, 1.4 wide each side). The game's planet is small (71.9 scale heights), so a setting sun's light passes only
    about 10 airmasses and, with Rayleigh alone, the twilight comes out yellow-olive; the ozone takes out green, which gives
    the orange horizon and violet sky of a real dusk. Our addition, the numbers are tuned by eye.
  - *Scale height in world units*: 40000 (the unit of the game's world is **Unknown**, decimetres most likely, so about 4 km).
    Only the world-to-atmosphere length mapping; it makes the haze twice as dense as the 8 km of the Earth's air would.
  - *Display mapping*: the viewer's lighting is display-referred (like the game's: no gamma step), so radiance goes through
    half a per-channel gamma 2.2 (what SkyX's HDR path does for its night glow, and what greys and pales the sky) and half the
    same curve on luminance only (keeps the hue, so sunsets stay warm). Calibrated by eye; the game's own absolute brightness
    is **Unknown** (above).
  - *Grading* (2026-10-04, after a user report that the sky and haze looked too purple and blue): with the
    wavelengths (0.57, 0.54, 0.44) the viewer uses, red scatters 0.8 as much as green, so the single-scattering sky comes out violet.
    The game itself sets the green wavelength to 0.48 (above), which scatters green 1.6 times more; that, not a grading, is likely why
    its sky is a teal blue. The grading predates that finding and stays until the sky is redone with 0.48. Colours
    are graded by how blue-dominated they are (`(b − r) / b`, ramping from 0.05 to 0.3): up to a white balance of
    (0.92, 1.04, 0.97) and a 45% desaturation. Warm colours (sunsets, the sun's glow) are left alone. Viewer's choice, by eye.
- **Tables**: a 256 × 64 transmittance table to the top of the air (built once) and a 128 × 96 sky-view table, azimuth from the
  sun × elevation above the eye's horizon (square-root spaced), rebuilt only when the sun or the eye's height moves.
- **Sky pass**: a full-screen pass reads the sky-view table, adds the night glow and stars (`SkyX_Starfield.dds`, a 1024²
  level, laid over the sky stereographically and turning with the sun's half-turn), a full moon (`SkyX_Moon.png`) opposite the
  sun, optional clouds (`Clouds.dds`'s red channel on a flat layer, `--clouds` or the weather's density) and the sun disc
  (radius 0.36 degrees, limb darkening `1 − 0.6 (1 − μ)`, dimmed by the air, tinted two thirds of the way to white so a
  tone-mapped disc does not read as an orange dot; peak radiance 8, above the bloom threshold of 1). The sky is also tinted by the weather's
  sky colour multiplier, and with weather fog the sky near the horizon fades to the fog colour.
- **Aerial perspective** (`--haze physical`; the default is the game's own haze, see "Haze" above) (`atmoApply` in `AtmosphereShaders.Functions`, called by the terrain, water and object shaders): for
  the ray from the eye to the shaded point, the optical depth is the closed-form integral of the exponential air density
  along the ray (heights above sea level), per channel; the point's colour is `colour · T + sky(direction) · (1 − T)`, the sky
  colour looked up from the table at the ray's direction (clamped to the horizon, so ground rays take the horizon's colour).
  So the haze is bluer, redder at sunset, and thinner higher up, with the same numbers as the sky. It closes completely at the
  haze distance (`--fog`, plus a growth with the eye's height) so the far plane and the water quad's end are hidden. The
  weather's fog (when enabled) is blended in linearly between its two distances, as `atmospherefog.hlsl` does.
- **Light**: the sun colour is `SunScale × transmittance` along the sun's direction from the eye's height (zero below the
  horizon); the ambient light is the display-referred sky averaged over the upper hemisphere (halved), plus a bluish floor at
  night, `(0.18, 0.22, 0.36) × (1 − night darkness)`, standing in for moonlight (the moon casts no light of its own); the
  ground's bounce is a fraction of that and of the sun. The eye height used for the sky and light is capped at 0.9 scale
  heights (36000 units), so a map seen from far above still has a daytime sky instead of the black of space; the aerial
  perspective integrates the true heights. Terrain, water and objects use
  these, so a sunset lights the ground with the colour of the sun in the sky.
- `--simple-sky` (key `B`) switches back to the old colour model and squared-distance fog for comparison.

### Using the atmosphere in a new shader

Include `AtmosphereShaders.Functions` in the fragment shader (after `#version`), end the shader with
`colour = atmoApply(colour, eyePosition, worldPosition)`, and call `SkyRenderer.Active?.Apply(program)` each frame before
drawing (it sets the uniforms and binds the table to a high texture unit). `SkyRenderer.SkyFunctions` additionally has
`skyColour(direction, disc)` for reflections. Colours are display-referred: sun light and ambient multiply albedo as before.

### Not reproduced

Volumetric clouds, lightning, cloud lighting from the sun's direction and drifting, the moon's phase and halo, the planet mesh, the
game's sun glow shape, SkyX's ground fog, the weather schedule, and multiple scattering beyond the gain above.
