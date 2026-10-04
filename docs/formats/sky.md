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
(0.57, 0.54, 0.44), g −0.991, exposure 0.48; the shader's parameters follow the comments of `SkyX.material`):

| Quantity | Value |
|---|---|
| thickness `outer − inner` | 0.5213 |
| `uScale = 1 / thickness` | 1.918 |
| `uScaleDepth = thickness / 2` | 0.2607 |
| `uScaleOverScaleDepth` | 7.359, so the density scale height is 0.1359 (the planet is 71.9 scale heights across its radius, the air 3.84 deep) |
| `invλ⁴` per channel | (9.473, 11.760, 26.680) |
| Rayleigh depth straight up from sea level, `Kr · 4π · invλ⁴ · scaleDepth` | (0.0683, 0.0847, 0.1924) |
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
  radius, `1 − exp(−exposure · colour)` out. The world → dome scaling (`uSkydomeRadius`) and the fog distances are runtime
  values (**Unknown**). `atmospherefog.hlsl` blends the scene to that colour linearly between two distances (see
  [terrain.md](terrain.md#atmosphere-verified-constants-observed-shader)).
- What the game's final brightness is: its HDR chain has no gamma step and no tone curve, only the constant exposure of 0.55 / 0.8
  to 0.55 / 0.96 (see [post-processing.md](post-processing.md); the CONSTANTS `exposure min` / `exposure max` are 0.8 / 0.95). The
  exact relation between the sky's radiance (exposure 0.48 on sun intensity 30) and the lit terrain's brightness is
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
| `fog enabled`, `fog distance min` / `max`, `fog color` | bool, floats, int RRGGBB | fog between the two distances (dust storms 7000 to 25000, "Desert Blaster" 700 to 4000); the colour is sand (`E9CB9E`) or white |
| `clouds density` | float | 0 clear, 1 overcast; `SkyBeam` has 20 |

Others (`rain intensity`, `wetness`, `dust`, `dust inside`, `dust slope`, `heat haze`, `wind speed min/max`, `wind intensity`,
`wind update time/limit`, `fog wind min/max`, `affect type/strength`, `effect strength min/max`, `start time`, `end time`)
are for rain, dust and wind effects and the weather's own schedule; not read by the viewer. The "Default" weather
(`5460-weather.mod`) is clear: fog off, clouds 0, both colours white. Which weather applies where (the game picks one per
region and time) is **Unknown**; `SkyWeather` reads a record, the viewer uses "Default" unless `--weather` names another.

## In the viewer

`SkyRenderer` (with `AtmosphereShaders` and `AtmosphereModel`/`SkyWeather` in `Meitou.Data.World`), all in the world view:

- **Model**: O'Neil's single scattering with the game's constants, as above, but with the exact planet/atmosphere geometry
  (the planet occludes the sun, so dusk and night come out right) and our own settings (`AtmosphereSettings`; these are
  choices, not game data):
  - *Mie*: 15 × the game's coefficient (its air is very clear; Kenshi's look is hazy), a scale height 0.3 of the air's, a
    forward-scattering aerosol phase function with `g = 0.75` (the game's 0.991 lobe is far too narrow for haze).
  - *Sky gain* 3.2 on the Rayleigh light, standing in for the multiple scattering that a single-scattering model lacks.
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
- **Tables**: a 256 × 64 transmittance table to the top of the air (built once) and a 128 × 96 sky-view table, azimuth from the
  sun × elevation above the eye's horizon (square-root spaced), rebuilt only when the sun or the eye's height moves.
- **Sky pass**: a full-screen pass reads the sky-view table, adds the night glow and stars (`SkyX_Starfield.dds`, a 1024²
  level, laid over the sky stereographically and turning with the sun's half-turn), a full moon (`SkyX_Moon.png`) opposite the
  sun, optional clouds (`Clouds.dds`'s red channel on a flat layer, `--clouds` or the weather's density) and the sun disc
  (radius 0.36 degrees, limb darkening `1 − 0.6 (1 − μ)`, dimmed by the air, tinted two thirds of the way to white so a
  tone-mapped disc does not read as an orange dot; peak radiance 8, above the bloom threshold of 1). The sky is also tinted by the weather's
  sky colour multiplier, and with weather fog the sky near the horizon fades to the fog colour.
- **Aerial perspective** (`atmoApply` in `AtmosphereShaders.Functions`, called by the terrain, water and object shaders): for
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
