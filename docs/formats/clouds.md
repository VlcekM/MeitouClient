# Clouds

How vanilla Kenshi draws its clouds: one SkyX planar cloud layer on the sky dome, driven by the weather. The sky itself is
in [sky.md](sky.md), the weather that sets the cloud density in [weather.md](weather.md). Labels as in
[../README.md](../README.md); **Verified (decompiled)** means read in the named function of `kenshi_x64.exe` or
`SkyX_x64.dll` (Ghidra 12.1.4, Steam build, 2026-10-05; decompiled output kept outside the repository), **Verified** alone
means read in a shipped shader or file.

Sources: `data/materials/SkyX/SkyX_Clouds.hlsl` (read for facts only), `Clouds.dds`, `CloudsNormal.dds`, `CloudsTile.dds`;
`SkyX_x64.dll` `CloudLayer::CloudLayer`, `CloudLayer::_registerCloudLayer`, `CloudLayer::_updatePassParameters` /
`_updateInternalPassParameters`, `CloudsManager::setupCloudEntity`, `CloudsManager::add`, `CloudsManager::setWindDirection`;
`kenshi_x64.exe` sky creation (FUN_14066e380), sky update (FUN_14066f190), the sky transition setter (FUN_14066cc80) and the
weather region update (FUN_1409dc4e0).

## What there is (and is not)

- **One planar cloud layer** (SkyX `CloudLayer`), added once when the sky is created (**Verified (decompiled)**, FUN_14066e380
  calls `CloudsManager::add` once). No second layer.
- **No volumetric clouds, no SkyX lightning, no cloud shadows.** The exe imports only `SkyX::create/update/~SkyX`,
  `frameStarted`, `preViewportUpdate`, `notifyCameraRender`, `setLightingMode`, `setRenderQueueGroups`,
  `AtmosphereManager::_update/getColorAt`, `CloudLayer::setOptions`, `CloudsManager::add/setWindDirection` (**Verified**,
  the exe's import table): nothing from `VCloudsManager`, so `SkyX_VolClouds*.hlsl`, `SkyX_VolClouds_Lightning.hlsl` and
  `Noise.dds` are unused. No shipped shader outside `SkyX/` and `caelum/` samples a cloud texture (**Verified**: `grep -i cloud`
  over `data/materials` finds only `horizonClouds` in `post/atmospherefog.hlsl` / `common.program` and a `cloudy_noon.jpg` cube
  in `forward/rtticons.material`), so the ground gets no cloud shadow.
- Cloud-like things in the world are **particle effects**, not sky: volcano plumes (`Volk-Cloud`), dust storm walls, mist,
  steam, twisters ([weather.md](weather.md#effects-particles)).

## Geometry and pass (Verified (decompiled), SkyX_x64.dll)

- `CloudsManager::setupCloudEntity` makes a second entity of the sky dome's own mesh (`SkyXMesh`), no shadows, attached to the
  dome's scene node, render queue = SkyX's skydome queue + 1, with a new material `SkyXClouds`; each layer adds one pass to it.
  Kenshi sets the SkyX queues to the bytes 5, 6, 7, 8 (`setRenderQueueGroups`), so the clouds would be in queue 6 (**Observed**:
  which byte is the skydome's is from SkyX's struct order, not checked).
- The pass (`_registerCloudLayer`): `scene_blend alpha_blend` (Ogre `SBT_TRANSPARENT_ALPHA`), no culling, no lighting, no
  depth write; vertex program `SkyX_Clouds_VP`, fragment `SkyX_Clouds_HDR_FP` (Kenshi uses SkyX's HDR mode, see
  [sky.md](sky.md)); textures in units 0..2: `Clouds.dds`, `CloudsNormal.dds`, `CloudsTile.dds`, wrap addressing.
- Layers are kept sorted by height, highest first (`CloudsManager::add`); irrelevant with one layer.
- The vertex shader passes the dome vertex's direction (`TEXCOORD0`, SkyX's normalised position) to the fragment shader. That the
  texcoord is the unit direction from the dome centre is **Observed** (from the shader's use; the dome mesh layout was not read).
  The dome follows the camera, so the clouds have **no parallax**: moving the camera never moves them; only the wind does.

## Options (Verified (decompiled): `CloudLayer::CloudLayer` defaults, FUN_14066e380 values)

Kenshi's `CloudLayer::Options` is 0x30 bytes with the fields below; `DensityMultiplier`, `DensityOffset` and `Darkness` look
like Kenshi additions (**Observed**: from memory of the open-source SkyX 0.4, not checked against its source here).
The game creates the layer with:

| Option (offset) | Value | Shader constant | Used by Kenshi's shader |
|---|---|---|---|
| Height (0x00) | 100 | `uHeight` | yes |
| Scale (0x04) | 0.001 | `uScale` | yes |
| WindDirection (0x08) | (1, 1) | — | no (the manager's vector is used, below) |
| TimeMultiplier (0x10) | 0.125 | `uTime` = multiplier × SkyX time | no (the line is commented out) |
| DistanceAttenuation (0x14) | 0.05 | `uDistanceAttenuation` | yes |
| DetailAttenuation (0x18) | 1 | `uDetailAttenuation` | no |
| HeightVolume (0x1c) | 0.25 | `uCloudLayerHeightVolume` | yes |
| VolumetricDisplacement (0x20) | 0.01 | `uCloudLayerVolumetricDisplacement` | yes |
| DensityMultiplier (0x24) | 3 | `uDensityMultiplier` | yes |
| DensityOffset (0x28) | −0.8 at creation | `uDensityOffset` | yes |
| Darkness (0x2c) | 0 at creation | `uDarkness` | yes |

`uExposure` is SkyX's exposure (1.4). `uWindDirection` is `CloudsManager`'s vector (offset 0x28), which Kenshi sets every frame
to an **accumulated offset**, not a direction (below). `uSunColor` and `uAmbientLuminosity` are declared but never set or read.

## From the weather (Verified (decompiled), FUN_14066f190, FUN_14066cc80, FUN_1409dc4e0)

- The sky controller holds a **cloud density** `c` (offset 0x54, 0.25 at creation) and a **sky colour multiplier** (0x48, white
  at creation). When the weather of the camera's weather region changes (or on the first update), the region update starts a
  **linear transition** of both to the new weather's `clouds density` and `sky color mult` over **30 s** of game-speed time
  (zero while paused); a camera jump of more than about 89 units in one frame snaps them instead
  ([weather.md](weather.md#sky-and-clouds-verified-decompiled-fun_1409dc4e0-fun_14066cc80-fun_14066f190)). On every frame of a
  transition (a snap included) both are clamped to 0..1, then:
  - `DensityOffset = 1.4 c − 0.8`
  - `Darkness = pow(clamp(c − 0.5, 0, 1), 0.3)`
  
  and `CloudLayer::setOptions` sends them (outside a transition the layer keeps the last values). So `c` above 1 (SkyBeam's 20)
  is clamped to 1.
- **Wind**: the weather region gives a wind direction and speed; while the camera is in that region the controller's wind
  velocity is `direction.xz × speed` (world units per second). Each frame the controller adds `velocity × dt` (dt: frame time
  × game speed, 0 when paused) to an offset and passes the offset to `CloudsManager::setWindDirection`. The shader shifts the
  texture by `offset × 0.00005`. So the clouds drift with the weather's wind, at a texture speed of `5e-5 × wind speed` per
  second, and change direction smoothly when the wind turns.

## The fragment shader (Verified: `SkyX_Clouds.hlsl`, Kenshi's version)

With `d` the pixel's dome direction (y up), `o = uDensityOffset`, `m = uDensityMultiplier` (3):

1. **Plane hit**: the cloud point is `d · uHeight / d.y`; texture coordinate `uv = point.xz · uScale` = `0.1 · d.xz / d.y`, plus
   the wind shift `w = offset · 0.00005`. Straight up the layer is magnified (the texture repeats every 10 units of `d.xz/d.y`,
   about 84° from the zenith), towards the horizon it is squeezed.
2. **First density** `D1 = saturate((Clouds.r(uv + w) + o) · m)`; a normal from `CloudsNormal` (`−(2 · tex − 1)`, y and z
   swapped).
3. **Fake volume**: the direction is bent by `uCloudLayerVolumetricDisplacement · d.y` along the normal's xz, and the plane is
   raised by `uHeight · (1 − D1) · uCloudLayerHeightVolume · d.y`; new `uv'` from that. The normal map is used for nothing else
   (no lighting from the normal).
4. **Density** `D = (Clouds.r(uv' + w + (0.2, 0.6)) + o) · m + CloudsTile.r(uv' − w) · 0.1` (not saturated; the tile texture
   scrolls the opposite way).
5. **Colour** `zenithLight + sunColour.rgb · (1 − 0.1 D)`.
6. **Horizon band**: `h = saturate(10 · saturate(d.y − uDistanceAttenuation))`: 0 up to 0.05 (2.9° elevation), 1 above 0.15
   (8.6°). `D' = D + h`; the colour is multiplied by `1 − saturate(D') · uDarkness`.
7. **Alpha** `a = D' · saturate(1 − tile + o)`, then `alpha = lerp(o + 0.5, a, h)`: near the horizon the alpha is the
   uniform `o + 0.5`, which is exactly the `horizonClouds.a` the haze pass uses ([sky.md](sky.md#haze-distance-fog-how-vanilla-does-it)),
   so the distant haze and the cloud band meet without a seam.
8. **Output** (HDR) `(saturate(colour) · sqrt(1.4), saturate(alpha))`, alpha-blended over the skydome.

### Coverage by density (Observed: computed from the textures)

Over the whole 1024² texture, ignoring step 3's displacement and taking the overhead case (`h = 1`), the alpha of step 7:

| `clouds density` c | `DensityOffset` | mean alpha | texels with alpha > 0.05 | > 0.5 | horizon band alpha | `Darkness` |
|---|---|---|---|---|---|---|
| 0 | −0.80 | 0.000 | 0 % | 0 % | 0 | 0 |
| 0.1 | −0.66 | 0.001 | 0.5 % | 0 % | 0 | 0 |
| 0.2 | −0.52 | 0.010 | 6.4 % | 0.1 % | 0 | 0 |
| 0.4 | −0.24 | 0.25 | 87 % | 13 % | 0.26 | 0 |
| 0.5 | −0.10 | 0.54 | 99.6 % | 49 % | 0.40 | 0 |
| 0.6 | 0.04 | 0.83 | 100 % | 91 % | 0.54 | 0.50 |
| 1 | 0.60 | 1.00 | 100 % | 100 % | 1 | 0.81 |

(`Clouds.dds` red: mean 0.275; `CloudsTile.dds`: grey, 0.08..0.93, mean 0.53; `CloudsNormal.dds` is nearly flat, 0.42..0.59.)
So a clear weather (`c` = 0: "Default", "clear nothing", "Desert Calm", the dust-swirl and ground-sand weathers) has **no
clouds at all**; the thin wisps of the user's desert screenshots are weathers with `c` ≈ 0.1..0.2 ("Sand stream", "Sand stream
ambient", "Clear Times SHORT hot 0.5", "Heavy_Rain sonorous"); `c` 0.6 ("light rain") is a broken, mostly covered sky; the
heavy rains, ash and dust-storm weathers have `c` = 1, overcast and darkened ([weather.md](weather.md#base-game-data-verified-merged-base-records-2026-10-05)). That the screenshots were
taken in such a weather is **Observed** (the weather was not logged).

### Lighting at sunset and night (follows from the Verified formulas; not screenshot-checked)

- `sunColour.rgb` is SkyX's colour towards the sun nudged by (0, 0, 0.04), divided by `4 · 1.4`, and **black below sun height
  −0.2** ([lighting.md](lighting.md)). `zenithLight = max(getColorAt(+Z) · skyMult, (0.001, 0.001, 0.0015)) · sunColour.g`
  (**Verified (decompiled)**, FUN_14066f190: the direction is Ogre's `Vector3::UNIT_Z`, the floor is set at sky creation). So
  the clouds have **no directional lighting**: every cloud pixel gets the same sun colour plus the +Z horizon colour, dimmed
  only by density (`1 − 0.1 D`) and darkness.
- At sunset the sun colour is orange-red, so all clouds turn uniformly orange-red, and the +Z term shrinks with `sunColour.g`.
- At night both terms vanish (`sunColour` = 0), the colour is black while the alpha is unchanged: the clouds become **black
  silhouettes** hiding the stars and the night glow behind them.
- `nadirLight` (`max(getColorAt(+Y) · skyMult, floor)`) is declared by the cloud shader but not used.

## In the viewer

Implemented 2026-10-08 (step 1 of the plan in [weather.md](weather.md#implementation-plan)): `SkyRenderer`'s sky fragment shader runs the
pass above per pixel (the dome direction is the view ray), `Meitou.Data.World.CloudLayer` holds the numbers (options, `DensityOffset`,
`Darkness`, `zenithLight`, the `horizonClouds` colour, the coverage computation, the drift offset), pinned by `CloudLayerTests` (the
coverage table is recomputed from the shipped textures and must match the one above).

- **Order**: sky, stars, the planets, **clouds**, all in the one sky pass; the clouds are alpha-blended in HDR (`mix(sky, cloud, alpha)`).
- **Inputs on `SkyRenderer`**: cloud density c (`CloudDensityInput`, else the forced `--weather` record's; `--clouds <0..1>` overrides
  both, a test flag), sky colour multiplier (`SkyColourMultiplierInput`, else the record's), cloud wind velocity xz (`CloudWind`,
  `--cloud-wind <x>,<z>`). c is clamped to 0..1. The same c gives `horizonClouds` (colour and pull), so the band and the haze meet.
- **Light**: `sunColour.rgb` = `KenshiLighting.SunColour(sun)` and `zenithLight = max(getColorAt(+Z) · skyMult, floor) · sunColour.g`
  with `getColorAt` = `SkyXModel.Colour(+Z, sun, skydome: false)` (the HDR scattering colour, without the dome's night factor); both
  computed on the CPU when the sun or the multiplier changes.
- **Drift**: `StepClouds` adds `velocity × dt` to a double offset every frame on the viewer's frame clock (real time, at most 0.25 s,
  as the heat haze's); it is held at dt = 0 in `--screenshot`. The shader gets `offset × 0.00005` wrapped to 0..1 (the textures repeat
  with period 1, so this is the same picture with the float precision kept). Checked once with a temporary offset (4000 units): the
  pattern shifts by the expected 0.2 of a texture.
- **Clear sky**: with c = 0 the pass is skipped. That differs from the game only by filtering noise: the table says no texel has any
  alpha at c = 0, so the game's layer is invisible too; skipping makes the ten parity views identical to a build without clouds.
- **Choices where the notes were Unknown or open (Observed; compare against the game's own screenshots in a known weather)**:
  - *Below the horizon*: the direction is evaluated at `d.y = 0.0005`, so the pixel gets the horizon value `alpha = o + 0.5` and the
    colour of the band's edge; the lower half is only visible from high above the world (sky pass backdrop), where it shows the same
    colour the haze is pulled to.
  - *Mipmaps*: the game's DDS files have no mip chain, so its layer aliases towards the horizon; the viewer samples `Clouds.dds`,
    `CloudsNormal.dds` and `CloudsTile.dds` with a generated mip chain (bilinear, repeat), which averages the squeezed detail instead.
    Above the 8.6 degree band the pattern is the game's; below 15 degrees it is smoother than the game's.
  - *Weather fog* (corrected 2026-10-08): the game's fog pass covers the sky too (`sky.md`, "Haze"), so a weather whose fog is complete
    before the far clip hides the clouds entirely; the viewer's earlier elevation-based fade of the sky and clouds (a stand-in) is gone.
  - *The planets behind the clouds* (2026-10-10): the game never draws SkyX's moon ([sky.md](sky.md#the-games-sky)); its two planets are in queue 6 at the
    priorities 1 and 2, before the cloud entity (queue 6, default priority 100), so the viewer draws them before the layer and clouds cover them.
  - *Sky colour multiplier*: it multiplies the whole sky in the viewer already (a stand-in, see [sky.md](sky.md)); the clouds take
    it only through `zenithLight`, as in the game.
- **Seen** (`--world --town "The Hub" --pitch 15 --time 13 --size 1600x900`): "Clear Times SHORT hot 0.5" (c 0.1) one wisp;
  "light rain" (c 0.6) broken thin cloud with blue gaps; "Dust Storm Approach" (c 1) an overcast sky, the wall of fog hiding the clouds
  near the horizon; "light rain" at 19:00 the same layer in the warmer light.
- **Horizon sparkle (fixed 2026-10-08, Observed)**: with `--weather "light rain"` a row of small white ticks ran along the horizon line. It was the cloud
  pass (gone with `--clouds 0`, absent on a build without the pass): below `d.y` 0.05 the alpha is the uniform horizon value, but the colour still followed
  the texture lookups, whose uv is `height · xz / d.y`, hundreds of units at `d.y` 0.0005 to 0.01, so the minified lookups sparkle. Now the cloud colour
  fades to a plain value from `d.y` 0.05 down to 0.01 (above 0.05 the layer is untouched). A viewer choice; the game's SkyX shader has no such fade.
  The plain value is the `horizonClouds` colour before the √exposure, `saturate(zenithLight + sunColour.rgb · (1 − 0.1 (o + 0.2) · 3)) · (1 − Darkness)`
  (corrected 2026-10-08: it first was `zenithLight + sunColour.rgb` with no darkening, which in an overcast weather drew a **white band** between the
  far terrain and the clouds, e.g. the Vain's `Kenshi_red_rain`, c = 1; see [sky.md](sky.md), "Weather tint"). Below `d.y` 0.05 the game's own pixel
  keeps step 6's darkening (`h` = 0, so `D' = D`; at c = 1, o = 0.6 and `D ≥ 1.8`, so `saturate(D')` = 1 and the colour is × (1 − 0.81)), **Verified**
  from `SkyX_Clouds.hlsl`; `horizonClouds` is the same form with the texture's part of `D` taken as 0.2, so the band now meets the haze in one colour.
  The weather system now drives the layer (`--weather auto`): see [weather.md](weather.md#in-the-viewer-and-the-game-step-3).

## Meitou clouds

The `clouds` switch (Meitou by default, `--faithful clouds` for the layer above; viewer design, not the game's; `Meitou.Data.World.MeitouClouds`,
pinned by `MeitouCloudsTests`, the shader branch in `SkyRenderer`). Added 2026-10-10 on branch `clouds`.

The game's layer has no directional light (above): flat white by day, one orange at sunset, black at night. Meitou keeps everything that
decides **where** cloud is (the plane, the textures and lookups, the fake volume, the coverage, the alpha, the horizon band, the drift, the
weather's density) and only changes the **colour** of each cloud pixel:

- **Key light**: the sun (`sunColour.rgb`, the game's layer's own unit), and at night, with the `planetshine` switch, the big planet's light at
  half the share the land gets (`PlanetShare` 0.5; the land's is boosted ×60), joined into one light by `Planetshine.Combine`.
- **Depth towards the light**: the lesser of two paths, up through the layer (the pixel's unsaturated density `D` over the light's height, the
  slant at most 6; a light below the plane, just after sunset, shines on the underside the eye sees: `0.25 D`) and sideways across it (four
  density taps `saturate((Clouds.r + o) · 3)` along the light's xz direction, 0.015 texture units apart, summed, plus half the pixel's own,
  × 0.6). `CloudsNormal.dds` is not used: it is nearly flat (above).
- **Transmittance** `0.5 · exp(−0.6 depth) + 0.5 · exp(−0.09 depth)`: one single-scattered and one multiple-scattered octave, so thick
  cores go grey, not black.
- **Phase**: two Henyey-Greenstein lobes on the cosine between the view and the light, 0.55 of g = 0.6 (the silver lining towards the sun)
  and 0.45 of g = −0.25, normalised so the sphere's mean is 1.
- **Ambient**: the sky's mean light (SkyX's colour at the zenith and four directions 20° up, × the weather's sky multiplier, plus the night
  air) mixed with 0.2 of the sky colour right behind the pixel (the gradient across a sunset sky), greyed (0.45 grey in a clear sky, rising to
  fully grey at `c` = 1: under an overcast the light comes from cloud, not blue sky; the blue alone made the clouds cyan), × (1 − 0.4 ·
  saturate(D / 2)) for darker thick bases.
- **Colour** `key · transmittance · phase · 1.6 + ambient`, then the game's `Darkness` at half strength (the depth already darkens; the full
  darkness on top made overcast skies near black), clamped to 8 instead of 1 (bright edges near the sun reach the tone map), × √exposure.
  Below `d.y` 0.05 the colour fades to the game's plain horizon colour as in the Faithful layer, so the haze still meets the band.

**Seen** (2026-10-10, `--world --town "The Hub" --distance 20000 --pitch 1 --weather Default --clouds 0.45`, against `--faithful clouds`):
13:00 the white wisps gain shaded volume with grey cores; 19:00 towards the sun the clouds glow round it, away from it they are grey-white
with darker cores; 22:24 (sun 3.5° up) warm lit undersides with the sky's gradient; 1:00 the clouds near the planet catch its light (the
game's are black against the stars); `--clouds 0.9` at 11:00 a neutral grey overcast with visible structure (the game's is a flat
blue-grey). **Verified** Faithful is unchanged: `--faithful clouds` against the commit before the switch, 0 px at 22:24 and 4 px of 1/255 at
13:00 (two runs of the old build differ by 35 px of 1/255).

### Smoothed textures and the day key (2026-10-11)

The user saw the lit clouds go "pixelated and oily" overhead. Two causes, told apart by experiment (Observed, The Hub at 13:00, "light rain",
looking 25° up away from the sun):

- **The textures**: `Clouds.dds` and `CloudsTile.dds` are **DXT1 (BC1), 1024², no mips** (**Verified**, the DDS headers), and `Clouds.dds`
  is not grey: red differs from green or blue by more than 2 on 96 % of texels (up to 91), so only red is the density the game uses. BC1
  keeps four values per 4 × 4 block between two 5-bit endpoints; the game's flat white hides the steps, the lit clouds turned them into
  grain and contour lines. Reading the lookups one and a half mip levels coarser softened the grain but not the lines (the alpha still read
  the full texture). Meitou now makes **smoothed copies** at load (`MeitouClouds.SmoothRed`: the red blurred by a wrapping Gaussian of sigma
  1.2 texels, in all three channels, mipmapped) and reads them for the whole Meitou layer (both lookups, the tile, the light taps) and the
  cloud shadows; the Faithful layer still reads the game's textures. Coverage is unchanged: the table above recomputed from the copies
  differs by at most 0.2 points (c 0.2 to 0.6).
- **The colours**: with the key light made neutral the warm-and-cyan rim along thin edges went away. SkyX's sun colour is a warm cream at
  noon, against the greyed blue ambient of the cores. The day key now keeps **0.4 of its saturation** (to its luminance) from a sun height
  of 0.35 up, full colour below 0.1 (sunsets stay orange) (`MeitouClouds.DayKey`).

Overhead, where a texel spans many pixels, the copies read soft rather than blocky; finer detail there would need a procedural detail layer.

## Meitou cloud shadows

The `cloudshadows` switch (Meitou by default, `--faithful cloudshadows` for none, as in the game; viewer design; `MeitouClouds.PlaneHeight`,
`ShadowAt`, `Parallax`, pinned by `MeitouCloudsTests`; added 2026-10-11 on branch `clouds`).

- **A world-anchored plane.** The game's layer follows the eye (its plane is always "100 units" above it), so it cannot shade a place. Meitou
  puts the plane at **12000 world units** (a unit is about a decimetre, [terrain.md](terrain.md); above the highest terrain, `WorldLayout.MaxHeight`
  9800) and keeps the game's texture scale there: `uv = 0.1 · xz / 12000`, so the pattern repeats every 12 km. The wind shift (5e-5 × speed per
  second) then sweeps the shadows at 5e-4 × 12000 = **6 × the weather's wind speed** (wind 30 units/s, about 3 m/s at the ground: 18 m/s, a wind aloft).
- **Parallax.** With the `clouds` switch Meitou the sky pass reads the same plane: `uv = 0.1 · (eye.xz + d.xz / d.y · (12000 − eye.y)) / 12000`
  (as the game's `0.1 · d.xz / d.y`, times `(12000 − eye.y) / 12000`, at least 0.25, plus `0.1 · eye.xz / 12000` wrapped in doubles), so the cloud
  overhead is the one whose shadow falls there, and clouds drift past as the camera moves (100 m of travel moves the layer 1/120 of a texture).
  With `clouds` Faithful the sky keeps the game's eye-locked layer (and the shadows no longer match it exactly).
- **The shadow.** For a lit point, the sun's ray (its real direction, `uAtmoSun`) is followed to the plane, where the sky pass's overhead alpha is
  read (the second lookup with its (0.2, 0.6) shift, the tile, the band at 1, no fake volume), at mip 0.5 (a texel is 12 m there, the sun's
  penumbra from 1.2 km about 10 m). The sun light is × `1 − strength · alpha`, strength **0.7**, faded in by a smoothstep from sun height 0.03 to
  0.12 (the hit point runs off far away at a low sun). Applied in `kenshiLight` (terrain, objects, foliage, grass, characters, everything lit as the
  land), the Meitou water's foam and its sun glint. The uniforms are frame globals (`uAtmoCloud`: strength, DensityOffset, height; `uAtmoCloudWind`;
  textures `uAtmoClouds`, `uAtmoCloudsTile`); off (strength 0) in a clear sky, the simple sky or with the switch Faithful.
- **Seen** (2026-10-11, The Hub, `--clouds 0.45`, 13:00 and 20:00, from 9000 and 30000 away): soft km-wide patches over the land, the sky's
  clouds drift with the camera. At `--clouds 0.9` the alpha is near 1 everywhere, so the whole land loses about 0.7 of its sun: an overcast look,
  but much darker than the game's overcast (whose ground is fully sunlit). **Verified** Faithful: `--faithful clouds,cloudshadows` against the
  commit before (f4b867e), 12 px differ by 1/255 (two runs of the old build: 0 px); the sky's uv now goes through a multiply by 1 and an add of 0.
- **Not done**: the GI probes bounce the unshadowed sun; the light shafts don't see the clouds (shafts through cloud gaps would be the next step,
  the same lookup in the froxel grid); the sun shadow cascades don't fade under overcast. GPU cost not measured yet (two `textureLod` per lit
  pixel; the bench aborted on the VRAM budget while another viewer ran).

Not done (options for later) for the lit clouds: below 2.9°
(`d.y` 0.05) the colour fades to the game's flat horizon colour, and the haze is still pulled to the game's `horizonClouds` colour, so between
2.9° and 8.6° a lit cloud meets game-coloured haze (no seam seen in the shots above, but not checked in every weather).

## Unknowns

- The dome mesh's texcoord layout (taken as the unit direction from the shader's use) and the dome's lower half (below the
  horizon the shader gives `alpha = o + 0.5`, hidden by terrain in practice).
- The exact render queue (6 from SkyX's struct order), which decides that the clouds cover the planets (queue 6, priorities 1 and 2;
  [sky.md](sky.md), "Planets"): the viewer draws the planets first.
- Whether the game's mip-less sampling looks noticeably different from the viewer's mipmapped one near the horizon (needs a game
  screenshot in a cloudy weather at a low pitch).

## Meitou volumetric replacement

The Meitou clouds switch now selects a procedural volume in the world renderer instead of the lit planar layer
above. This is an **engine choice**, not new original-game behaviour. Density, lighting, wind, temporal filtering,
reflection and the new cloud-shadow map are documented in [../render-clouds.md](../render-clouds.md).
The planar shading and shadow descriptions above describe the retained legacy fallback and Faithful-cloud shadow mode.
