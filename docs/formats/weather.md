# Weather

How vanilla Kenshi chooses, schedules and shows its weather: the WEATHER / SEASON / BIOME_GROUP / EFFECT records, the
per-region scheduler, wind, the blending at region borders, the fog, the sky and cloud transition, rain, wetness, dust, heat
haze, the particle effects (rain, dust storms, lightning, gas), sounds, and the effect placers on the map. The cloud layer
itself is in [clouds.md](clouds.md), the sky and haze in [sky.md](sky.md). Labels as in [../README.md](../README.md):
**Verified (decompiled)** = read in the named function of `kenshi_x64.exe` (Steam build, Ghidra 12.1.4, 2026-10-05;
decompiled output stays outside the repository, nothing here is translated code), **Verified** = checked in shipped files or
records, **Observed**, **Unknown**.

Sources: the merged base-game records (`gamedata.base`, `Newwworld.mod`, `Dialogue.mod`, `rebirth.mod`; `LoadOrder.BaseGame`,
dumped with a scratch probe), `fcs.def` (field help texts), the editor's `WeatherAffecting` enum, `fcs_enums.def`
(`EffectType`, `EffectVolumeType`), `data/newland/land/areasmap.tga`, `data/particles/scripts/*.pu`, the deferred and water
shaders, and these exe functions: WEATHER loader FUN_1409de7b0, SEASON loader FUN_1409df240, weather region (BIOME_GROUP)
constructor FUN_1409df8d0, zone-to-region assignment FUN_1408fd6e0 with FUN_1408f4eb0, region scheduler update FUN_1409dde50
(called for every region by FUN_1408f69d0), weather choice FUN_1409dd980, weather start FUN_1409dcf60, wind update
FUN_1409dc0e0, weather strength FUN_1409dc3e0, fog distance FUN_1409dc350, region-to-sky update FUN_1409dc4e0, weather manager
(camera side) FUN_1409e8f70 and FUN_1409ea410, effect spawning FUN_1409dcaf0 / FUN_140103210, EFFECT loader FUN_1400fa0c0,
sky controller FUN_14066e380 / FUN_14066f190 / FUN_14066cc80, frame times FUN_140788a00.

## Overview

- The world is cut into **weather regions**: one per BIOME_GROUP record, painted per zone by `areasmap.tga`.
- Each region runs its own **calendar of seasons** (SEASON records, lengths from percentages of the year) and, inside a
  season, a **chain of weathers** picked at random by weight, each lasting a random number of game minutes.
- Every region's schedule runs all the time, wherever the camera is (**Observed**: FUN_1408f69d0 walks a container of regions
  each frame and updates each; that it holds every BIOME_GROUP, not only those of loaded zones, was not confirmed).
- What the player sees comes from the region the camera is in: its weather sets the sky colour multiplier, the cloud density and
  the cloud drift (with a 30 s transition), rain, wetness, dust, heat haze, the sounds and the particle effects. Only the
  **weather fog** is blended over up to four regions near the camera.

## Time bases (Verified (decompiled), FUN_140788a00)

Three frame times are kept each frame: the real frame time (used by SkyX's own update); the frame time × game speed, **0 when
paused** (sky transitions, cloud drift, effect timers); and the same × game speed but **0.01 when paused** (wetness, dust, heat
haze, so they still settle while paused). Game time is the sky controller's day count and a time of day; weather durations,
wind updates and season ends are in **game minutes** (game hours × 60) and **days** (**Verified (decompiled)**: the weather code
multiplies the controller's hour counter by 60).

## Records

### WEATHER (type 80; Verified: fcs.def and the loader FUN_1409de7b0)

| Field | Use |
|---|---|
| `clouds density` | the cloud layer's density `c` ([clouds.md](clouds.md)) |
| `sky color mult` (RRGGBB) | the sky controller's colour multiplier: multiplies the two SkyX colours that light the clouds and `horizonClouds` (`zenithLight`, `nadirLight`; [sky.md](sky.md)) |
| `fog enabled`, `fog color`, `fog distance min/max`, `fog wind min/max` | the weather fog ([sky.md](sky.md#haze-distance-fog-how-vanilla-does-it)); see "Fog" below for the distance rule |
| `wind speed min/max` (units/s), `wind update time` (game minutes; 0 means 1,000,000), `wind update limit` (degrees, "0-360") | the wind, below |
| `rain intensity` (0..100) | rain sound level and the water's rain ripples |
| `wetness` (0..1) | target wetness of surfaces |
| `dust`, `dust inside`, `dust slope` | dust on objects outside, inside buildings, and the slope it sticks to |
| `heat haze` (0..1) | the heat-haze post effect's strength |
| `affect type` (`WeatherAffecting`: WA_NONE, WA_DUSTSTORM, WA_ACID, WA_BURNING, WA_GAS, WA_RAIN), `affect strength` | direct effect on characters (gameplay) |
| `effect strength min/max` | "Maximum effect strength"; the game reads `min` twice (below) |
| `start time`, `end time` (hours) | time-of-day window; equal values = any time |
| `effects` → EFFECT (val0 max count active at once, 0 = no limit; val1/val2 min/max respawn time in real seconds; 0/0 = all spawned at once) | the weather's particle effects |
| `wind intensity` (0..100) | wind sound level |

The loader keeps one more rule (**Verified (decompiled)**): when `fog wind min` equals `fog wind max`, it replaces `fog distance
min` by `max(fog distance min, fog distance max)`, so the fog distance is the larger value whatever the wind.

### SEASON (type 81; Verified: fcs.def, FUN_1409df240)

`weathers` → WEATHER with val0 = **weight** ("probability"), val1/val2 = **min/max duration in game minutes**; `weather strength
limit min/max` (0..1); `sunlight color` (loaded, its use was not traced: **Unknown**). A season without weathers gets the
"Default" weather (`5460-weather.mod`, clear) with limits 0..1 and logs "Warning: Season ... has no weather". If any of its
weathers has a time window (`start time` ≠ `end time`), the "Default" weather is appended with weight 0 as the fallback.

### BIOME_GROUP (type 95; Verified: fcs.def, FUN_1409df8d0, FUN_1408fd6e0)

`index` (the colour in `areasmap.tga`), `seasons` → SEASON with val0 = **order**, val1 = **share of the year** (relative to the
other seasons), `weather strength multiplier min/max`, `acidic ground`, `acidic water` (gameplay). The world's BIOMES records
(terrain texturing, [terrain.md](terrain.md)) take no part in the weather.

### EFFECT (type 82; Verified: fcs.def, FUN_1400fa0c0) and EFFECT_FOG_VOLUME (type 96)

`type` (`EffectType`: NONE, CAMERA, POINT, WANDERING, GLOBAL, CAMERA_RAIN, CAMERA_ACID_RAIN, POINT_LIGHTING, WANDERING_STORM,
WANDERING_GAS, GLOBAL_POINT), `particle system` (a ParticleUniverse system name from `data/particles/scripts/*.pu`), `affect
type` / `effect radius` / `effect strength min/max` (damage area), `min/max altitude`, `min/max slope`, `min/max time to live`
(s), `wandering speed`, `wind affected`, `wind direction emission`, `wind speed mult`, `min/max wind span rate` (wind speeds for
none / all particles), `colour multiplier`, `sky colour multiplier`, `ground colour` ("multiply particle base colour by biome
ground colour"), `maximum view distance`, fade times for particles, lights and fog volumes, and the lists `fog volumes` →
EFFECT_FOG_VOLUME, `lights` → LIGHT, `sound` → AMBIENT_SOUND. The loader also measures the particle system's Box and Circle
emitters (largest extent × the system's scale × 0.5 × 1.5) as the effect's size, and remembers its `LinearForce` affectors and
its emitters' emission rates so the game can drive them by the wind (**Verified (decompiled)**; how they are driven was not
traced). EFFECT_FOG_VOLUME: `type` SPHERE / CYLINDER, `radius`, `distance` (density distance), `colour`, `alpha`, `additive
colour`, offsets, `ground movement`.

## Where: regions from the areas map (Verified (decompiled), FUN_1408fd6e0 and FUN_1408f4eb0)

The game keeps a 64 × 64 grid of zone cells (4608 units, the zone grid of [terrain.md](terrain.md#world-coordinates)). Each
cell's region is the BIOME_GROUP whose `index` equals the colour of `areasmap.tga` (256 × 256, 24-bit, so 4 × 4 pixels per
zone) at the cell's centre (**Observed**: the two cell fields used are taken to be the centre); a cell whose colour matches no record goes to "NONE" (`18003-gamedata.base`, no seasons, so Default
weather). How the map's pixels map onto world coordinates (its origin and size fields in the terrain object) was not pinned
down: **Observed** that it is a 4 px-per-zone map of the whole world.

Base game: 74 BIOME_GROUP records; 66 list seasons. Several groups share a SEASON ("coastal" is used by 9, "Desert mild" by 5).
Regions without seasons, so always clear: Central, Arm of Okran, Berserker Country, Empire, Obedience, Rebirth, Watcher's Rim
and "NONE" (**Verified**, records).

## Seasons (Verified (decompiled), FUN_1409df8d0, FUN_1409dde50)

- **Length**: each season's share val1 divided by the sum of the region's shares, × `days per year` (100 in GLOBAL CONSTANTS
  after all base files), rounded; the last one takes what is left. A share below 1 counts as `days per year / number of seasons`.
  Seasons are ordered by val0 (ties: order **Unknown**, the sort routine FUN_1409e8790 was not read).
- **Cycle**: the region keeps the current season and the day it ends (current day + length). When the controller's day count
  reaches it, the next season in order starts (wrapping round) and a weather is chosen at once. When the region is created
  the first season in order starts (**Observed**: the new-game path was not traced; saves keep the season id and end day).
- Base-game examples (shares → days): Desert "Desert Blasts" 900 / "Desert Summer" 300 → 75 / 25 days; Skinner's Roam "Clear
  Times" (order 0, 200), "Clear AND Storm Mix SHORTSEASON" (2, 100), "Dust Storm Approach" (3, 300) → 33, 17, 50 days;
  Cannibal Plains "coastal" / "nothing, bit of rain, wind" 2 / 2 → 50 / 50; Spider Plains "south coast" (order 0, 50) then
  "purple desert" (1, 5) → 91 / 9 days. Most regions have one season with share 1 (the whole year).

## Choosing a weather (Verified (decompiled), FUN_1409dd980, FUN_1409dcf60, FUN_1409dc3e0)

- **When**: when game time passes the current weather's end, and when a season starts.
- **Candidates**: the season's weathers with weight > 0; if the season has time-limited weathers, a time-limited weather only
  counts while the controller's current whole hour is inside `[start time, end time]`. (For `start > end` the test accepts
  `hour ≤ start or hour ≥ end`, i.e. always; no base weather has such a window. The only windows are "Desert Calm hot 0.6"
  7–22 h and "venge beams" 5–23 h.)
- **Pick**: one candidate → it; else a uniform random number in [0, sum of weights) picks the first weather whose running sum of
  weights exceeds it (weighted choice). No candidate → the season's last weather (the appended "Default" when there are windows).
- **Duration**: a random whole number of minutes in [val1, val2] of the season's reference (inclusive), from now
  (`val1 + trunc(rand()/32768 · (val2 − val1 + 1))`, clamped to the range; the helper FUN_1409b3b40 checked in its disassembly). For a
  time-limited weather the end is cut at today's `end time`. A weather may follow itself.
- **Strength** `s = clamp(random[limit min, limit max] × random[mult min, mult max], 0, 1)` (season limits × region
  multipliers; all base-game multipliers are 1). It scales the wind speed, rain, dust, heat haze.
- **Effect strength**: drawn "between" `effect strength min` and ... `effect strength min` again (the string address is the same
  for both lookups, **Verified** in the disassembly of FUN_1409dcf60), so it is always the minimum; every base weather has 1–1.
- Random numbers: the C runtime's `rand() / 32768` (**Verified (decompiled)**, the exe's helpers), not seeded per region.

## Wind (Verified (decompiled), FUN_1409dcf60, FUN_1409dc0e0)

- A region's wind has a **speed** and a **direction** in the ground plane.
- On the region's first weather: speed `lerp(wind speed min, wind speed max, s)`, direction a random (x, z) in [−1, 1]²,
  normalised.
- Every `wind update time` minutes (counted from the weather's start, and again at each update): a new target speed `current +
  (lerp(min, max, s) − current) × limit / 360` and a new target heading `current + random[−π, π] × limit / 360` (wrapped to ±π;
  `limit` = `wind update limit`, so a limit of 20 turns the wind by at most ±10°). Both are then interpolated linearly over
  `round(0.05 × wind update time)` minutes. A new weather starts the same kind of change towards its own range. So the wind
  speed never jumps; with limit 20 it moves only 5.6 % of the way to the weather's speed per update.
- The current heading is read back with `angleBetween(UNIT_X, direction)`, which is unsigned (0..π) (**Observed** consequence:
  headings on one side of the x axis are folded onto the other at each update).
- Uses: the **fog distance** (below), the **cloud drift** (the camera region's wind, [clouds.md](clouds.md)), the wind sound,
  and the effects marked `wind affected`. Grass and foliage sway uses per-object `wind factor` constants, not the weather
  (**Observed**: `windFactorX/Z` in FUN_1406cd510 come from the object records).

## What the camera sees

### Region at the camera (Verified (decompiled), FUN_1409e8f70)

The weather manager finds the zone cell under the camera. It switches to a new region only when the camera is at least 500
units (squared distance 250000) outside the current cell's rectangle, so crossing a border back and forth does not flicker. A
camera jump of more than about 89 units in one frame (squared xz distance 8000) counts as a teleport: values snap instead of
fading.

### Fog: up to four regions (Verified (decompiled), FUN_1409e8f70, FUN_1409e8cc0, FUN_1409dc350)

- Four points are sampled at the camera ± 1300 in x and z. For each, its zone cell's region gets the weight `1 − min(1, d² /
  1300²)` with `d` the distance from the camera to that cell's rectangle (0 inside it); weights of the same region add, up to four
  regions, then they are normalised. Each region contributes its current weather's fog: on/off (as 1/0), the **distance**, and
  the colour. The blended values feed `MainFog` ([sky.md](sky.md#haze-distance-fog-how-vanilla-does-it)). (The colour sum
  uses the same weights: **Observed**, the decompiler shows one fixed weight there.)
- **Distance**: `lerp(fog distance min, fog distance max, saturate((wind − fog wind min) / (fog wind max − fog wind min)))`
  with the region's current wind speed, or `fog distance min` when the two wind values are equal, which after the loader's rule
  above is `max(min, max)`. In the base game only "misty rain" has different wind values (40 / 20, with both distances 20000),
  so **every base weather's fog is complete at its `fog distance max`**: Dust Storm Approach 25000, Sand stream 20000, shek
  desert storm 15000, Kenshi_Ash-Flakes 35000.
- **Weather change**: when a region's weather changes, its fog values fade from the previous weather's over the first **2.5 %**
  of the new weather's duration (e.g. 3 minutes of a 120-minute weather).

### Sky and clouds (Verified (decompiled), FUN_1409dc4e0, FUN_14066cc80, FUN_14066f190)

For the camera's region only: when its weather changes (and on the first update) the sky controller starts a linear
**30-second** transition (game-speed seconds) of its colour multiplier to the weather's `sky color mult` and of the cloud
density to `clouds density`; a teleport snaps them. Every frame the region also sets the cloud drift velocity to its wind
direction × speed. The cloud density drives the layer's coverage and darkness ([clouds.md](clouds.md)) and `horizonClouds`
([sky.md](sky.md)). The weather does not change the sun or the scattering itself.

### Rain and wetness (Verified (decompiled), FUN_1409e8f70, FUN_1409ea410, FUN_1409d6ae0, FUN_1409d6c40; Verified: shaders)

- **Rain** `= rain intensity × s` of the camera region's weather. The shared parameter `rainAmount` (SharedWaterParams) is
  `saturate(rain / 50)`; the water shader (`forward/water.hlsl`) adds three layers of ripple normals from its rain-ripple map
  (`rain-ripples.png` in the water material, **Observed**), each a ring per texel whose phase scrolls with `gameTime`, weighted
  `2 · rainAmount, 2 · rainAmount, 0.8 · rainAmount`, faded out at grazing view and with distance (`saturate(2 viewDir.y −
  dist · 0.0001)`).
- **Wetness** (shared `wetness`): towards the weather's `wetness` (not scaled by `s`): it **rises by 0.01 per second** (100 s from
  dry to soaked) while below the target; when the target is 0 it **dries by 0.005 per second** (200 s); between them (a lighter
  rain than the current wetness) it stays. A teleport sets it to about the target (**Observed**: ±0.01 offsets).
- **Surfaces** (`common/wet.hlsl`, `makeWet`), with `wet = min(wetness + underwater, 1)`, `underwater =
  saturate((waterHeight − y + edge) / edge)` (edge 0.5 for objects and foliage, 2 for terrain): the albedo is darkened by
  `min((1 − 1/(wet + 0.7)) · absorbance + 0.2 · underwater, 0.4) / 2` and the gloss pulled towards 0.5 by
  `saturate(2 · max(0, wet − 0.15 absorbance)^(1 + 4 absorbance))` (not underwater). Absorbance: objects `(1 − gloss) · (1 −
  metalness)`; terrain `1 − albedo.a +` the biome layers' `absorbance 0..road` blended like the textures (BIOMES fields, "Base
  water absorbance ... Determines rain wetness effect"); foliage a fixed 0.9; building interiors get wetness 0 (only the
  water-line term). So rain darkens absorbent ground a little (at most 20 %) and makes smooth things shinier.

### Dust (Verified (decompiled), FUN_1409e8f70, FUN_1409ea2d0; Verified: `objects.hlsl`, `triplanar.hlsl`)

- `dustAmount` (SharedWaterParams, float3): x = current dust, y = x × `dust inside`, z = the slope value. x moves towards
  `dust × s`: up by `0.017 × target` per second (about a minute to full), down by 0.001 per second (about 17 minutes from 1 to
  0); z moves towards `dust slope` by 0.1 per second; a teleport snaps them.
- Objects with the DUST flag ([runtime-materials.md](runtime-materials.md)) blend their albedo towards `dustColour` (the
  BIOMES `ground colour` at the object) by `saturate((saturate((N.y − 0.7 + z) · 4) − 6 · gloss + 2.2 − 6 · noise) · 2 + x − 1)
  · min(1, 8x)` (`noise` = the dust noise texture at `world.xz · 0.002`), and flatten the normal map by half that; interiors use
  y instead of x. Triplanar map features do the same without the normal flattening. Dust values above 1 (great desert streamers
  1.2, Kenshi_Ash-Flakes 2) cover more.

### Heat haze (Verified (decompiled), FUN_1409e8f70, FUN_1409ea410)

The shared `heatHaze` moves towards `heat haze × s × saturate(6 · sunY)` (none at night, full from about 10° of sun height) at
`1/3` per second; `post/heathaze.hlsl` uses it ([post-processing.md](post-processing.md)).

### Sounds (Verified (decompiled), FUN_1409e8f70; names only)

Wwise events `Weather_Rain_Start` / `Weather_Rain_End` when the rain level becomes non-zero / zero, RTPC `Rain_Intensity` = the
rain level (0..100); RTPC `Intensity` = `wind intensity × wind speed × 0.01` with `Weather_Wind_Start` / `Weather_Wind_Stop`;
bank `SFX_Amb_Weather_Bank.bnk`. Effects carry their own AMBIENT_SOUND (`sound`, e.g. "Inferno" on the beam effects).

## Effects (particles)

### Spawning (Verified (decompiled), FUN_1409dcaf0, FUN_140103210; behaviour from fcs.def where marked)

When a region's weather changes, its old effect groups are removed and one group is made per entry of the weather's `effects`
list, with the entry's count and respawn times and the weather's effect strength. The group class depends on the EFFECT `type`:

| `type` | Group | Behaviour (fcs.def, **Observed**) |
|---|---|---|
| CAMERA (1), CAMERA_RAIN (5), CAMERA_ACID_RAIN (6) | camera group | in front of the camera, moving with it; affects the whole region. The particles are kept in a cube centred at a point `d` ahead of the camera and wrapped modulo it (**Verified (decompiled)**, FUN_140100dc0 / FUN_140101a20: edge `2d / 1.5`; `d` is a camera value at offset 0x7c, **Unknown** which) |
| POINT (2), POINT_LIGHTING (7) | point group | spawned at random places in the area; POINT_LIGHTING "based on the amount of metal", hits once |
| WANDERING (3), WANDERING_STORM (8), WANDERING_GAS (9) | wandering group | random place, then moves at `wandering speed` (particles that get too far from their anchor are put back near it: **Observed**, FUN_140101be0, its group not confirmed) |
| GLOBAL (4) | global group | on the ground at the camera centre; whole region |
| GLOBAL_POINT (10) | global point group | points around the camera area; needs a max time to live |
| NONE (0) | none | not a weather effect (torches, blood, fire on objects) |

Spawn filters: `min/max altitude` and `min/max slope` of the ground, `min/max time to live`. Base-game EFFECT records: 74; the
weather ones by type: rain CAMERA_RAIN (`Kenshi_Heavy_Rain`, `Kenshi_red_Rain`, `Kenshi_black_rain`, `rain_light` →
`kenshi_rain1`), ash CAMERA (`Kenshi_Ash-Flakes_*`), streams and mist GLOBAL (`Sand-Stream*`, `Kenshi_Mist_Cloud`, `fog
islands`, `purple desert`), storms and twisters WANDERING (`DesertCloudStorm`, `DesertBlast-BillowCloud`, `Twister-Chuff*`,
`Twister-LARGE`, `great desert streamers`, `ground sand*`), gas and beams WANDERING_GAS (`poison gas*`, `SkyBeam`, `venge beam`,
`Twister-of-fire01`), lightning POINT_LIGHTING (`Lightning_Bolt`, `weather_lightning1`), local swirls GLOBAL_POINT
(`DesertDetritus01`, `Drifting-foliage`, `rising steam slow`).

### The particle scripts (Observed: `data/particles/scripts/*.pu`)

93 ParticleUniverse scripts (`Plugin_ParticleUniverse_x64.dll`), 93 particle materials in `data/particles/materials`,
textures in `data/particles/textures` (PNG/DDS), four meshes. Examples:

- `Kenshi_Heavy_Rain`: one technique, 5000 particles, oriented-self billboards 0.85 × 4 with bottom-centre origin, a 120 × 120
  box emitter 15 above the system emitting 6400/s down and slightly sideways (direction (0.2, −1, 0)), speed 90..182, life
  0.3..1 s, grey; an observer expires particles below y −5.85. `kenshi_rain1`: 1000 particles, 150³ box, 1000/s, 0.15 wide.
- `Lightning_Bolt`: system scale (50, 200, 50); two 1.2 s billboard techniques (materials `Lightning_Bolt`, `Lightning_Bolt2`)
  scaled ×30 with random stretch, plus spark and flame techniques. The base game's lightning is this effect (deadlands and
  sonorous rains: no limit on count, every 1..25 or 8..25 s) or `weather_lightning1` → `kenshi_lightning_beam2` in "lightning
  storm"; there is no sky flash from SkyX.
- `Permanent-dust-storm`: scale (50, 30, 50), circle emitter of radius 44, `DustMite_DESERT` billboards with a vortex
  affector; `Volk-Cloud` (in `Volc-Pumper.pu`): scale 50, two `Desert-Volcano` billboard techniques (point emitter speed 300 and
  circle emitter speed 400, particles 10..16 growing ~5× then shrinking, a colour ramp from transparent through warm grey to
  transparent, a linear force (8, 0, 8)): a rising volcanic plume.

### Fog volumes

EFFECT_FOG_VOLUME (8 records): coloured spheres attached to twister effects ("Twister-Chuff01 DustBall" radius 500, "Grey
Sphere" 661, ...), with alpha and density distance. The static ones on the map are `fogfeatures.dat` ([sky.md](sky.md)
"Not there"). How they are drawn (the fog volume pass after the haze in `main.compositor`) is **Unknown** in detail.

## Effect placers on the map (Verified: records and `features.dat`)

MAP_FEATURES with `hidden` true draw no mesh; their EFFECT is attached as an **instance** of the feature record (not a
reference list): "Volk-Cloud Placer" → `Volk-Cloud` (POINT, `Volk-Cloud` plume; 24 placements, the Ashlands volcano plumes),
"Volc-small-steamers Placer" → `Volc-small-steamers` (POINT; 18), "Permanent-dust-storm Placer" → `Permanent-dust-storm`
(POINT; 1), "Fog-Wall_SkinnersRoam 01" → `DesertCloudStorm` (no placements in `features.dat` of the base game, **Observed**),
and "Marker-Attractor" (a bird attractor, `athene.mesh`; 1). These effects belong to the feature, not to any weather, so they
are always on. Placement counts are from [zones.md](zones.md#map-features). The placer effects have life 800..5500 s, `wind
affected`, `ground colour` and view distances 200..1000 (EFFECT fields), so they drift with the wind and take the biome's ground
colour (**Observed**: the use of these fields for feature effects was not traced).

## Gameplay effects (facts only)

From the strings and fields next to the weather code (**Observed**): WA_DUSTSTORM lowers combat accuracy unless gear gives
"weather protection"; WA_ACID burns unprotected skin ("It is currently raining acid ..."); WA_GAS and WA_BURNING damage inside
effect radii (`effect radius`, `effect strength`); BIOME_GROUP `acidic ground` burns bare feet, `acidic water` prevents
swimming; clothing has `weather protection0` (a `WeatherAffecting`) and `weather protection amount`; rain collectors need rain
("Not Raining") and wind generators need wind ("No Wind", operating speed range). Not traced further.

## Base-game data (Verified: merged base records, 2026-10-05)

53 WEATHER, 48 SEASON, 74 BIOME_GROUP, 74 EFFECT, 8 EFFECT_FOG_VOLUME. The weathers, with the fields that drive rendering and
scheduling (empty = 0 / white / none; fog distance after the loader's rule; affects = `affect type` and strength):

| Weather | clouds | sky mult | fog (colour, distance) | wind speed min-max (update every N game min / limit °) | rain | wetness | dust (inside, slope) | heat haze | affects | effects [count, respawn s] |
|---|---|---|---|---|---|---|---|---|---|---|
| BIG TWISTER | 0.4 | E6C28E | off | 30-80 (10 / 20) |  |  |  |  |  | Twister-LARGE [5, 180-600] |
| black desert |  |  | off | 15-40 (360 / 20) |  |  | 0.1 (0.36, 0.1) |  |  | poison gas test [100, 0-0] |
| clear nothing |  |  | off | 0-30 (360 / 20) |  |  |  |  |  |  |
| Clear Times SHORT hot 0.5 | 0.1 | FCAC7C | off | 10-30 (360 / 20) |  |  | 0.2 (0.33, 0) | 0.5 |  | Sand-Stream any altitude [12, 2-4] |
| Default |  |  | off | 0-60 (360 / 20) |  |  |  |  |  |  |
| Desert Blaster |  | FCAC7C | off | 50-100 (360 / 20) |  |  | 0.5 (0.33, 0) | 0.25 | dust storm 1 | DesertBlast-BillowCloud [1, 85-180]; Sand-Stream any altitude [1, 60-600] |
| Desert Calm |  |  | off | 0-20 (20 / 20) |  |  | 0.2 (0.36, 0) | 0.1 |  |  |
| Desert Calm hot 0.6 (only 7-22 h) |  |  | off | 0-20 (20 / 20) |  |  | 0.15 (0.33, 0.2) | 0.6 |  |  |
| Desert Calm MEGAHOT 1.0 |  |  | off | 0-20 (20 / 20) |  |  | 0.1 (0.36, 0) | 1 |  |  |
| Desert Wisps |  |  | off | 20-60 (360 / 20) |  |  | 0.25 (0.36, 0.2) | 0.2 |  | Desert-WISPS [250, 0-0] |
| Desert-dust-Light Detritus01 |  |  | off | 30-60 (360 / 20) |  |  | 0.4 (0.33, 0) | 0.5 |  | DesertDetritus01 [1, 160-350] |
| Desert-dust-swirls WEATHER |  |  | off | 30-70 (360 / 20) |  |  | 0.2 (0.33, 0) | 0.25 |  | Desert-dust-swirls EFFECT [10, 60-600] |
| Drifting-foliage |  |  | off | 30-60 (360 / 20) |  |  | 0.4 (0.33, 0) | 0.5 |  | Drifting-foliage [1, 10-30] |
| Dust Storm Approach | 1 | FCAC7C | E9CB9E, 25000 | 5-100 (360 / 20) |  |  | 0.5 (0.33, 0) |  |  | DesertCloudStorm [1, 155-180]; Sand-Stream any altitude [22, 1-2]; Twister-Chuff_Desert01 [4, 12-120] |
| Dust Storm Approach SHORT | 1 | FCAC7C | E9CB9E, 190000 | 5-100 (360 / 20) |  |  | 0.5 (0.33, 0) | 0.5 |  | DesertCloudStorm [1, 155-(-155)]; Sand-Stream any altitude [22, 1-2]; Twister-Chuff [1, 60-600] |
| fog islands |  |  | off | 0-30 (360 / 20) |  |  |  |  |  | fog islands [0, 0-0] |
| GD ground sand orange |  |  | off | 20-60 (360 / 20) |  |  | 1 (0.5, 0.6) | 0.4 | dust storm 0.4 | ground sand orange [400, 0-0] |
| GD ground sand orange SMALL |  |  | off | 20-60 (360 / 20) |  |  | 0.7 (0.5, 0.4) | 0.4 | dust storm 0.4 | ground sand orange [60, 0-0] |
| great desert streamers |  |  | off | 30-70 (360 / 20) |  |  | 1.2 (0.4, 0.6) | 0.25 | dust storm 0.5 | great desert streamers [250, 0-0] |
| great desert streamers SMALL |  |  | off | 30-70 (360 / 20) |  |  | 1.2 (0.4, 0.6) | 0.25 | dust storm 0.5 | great desert streamers [50, 0-0] |
| ground sand |  |  | off | 20-60 (360 / 20) |  |  | 0.25 (0.36, 0) | 0.2 |  | ground sand [50, 0-0] |
| Heavy_Rain | 1 |  | off | 0-70 (20 / 20) | 100 | 1 |  |  |  | Kenshi_Heavy_Rain [1, 60-600] |
| Heavy_Rain ACID | 1 |  | off | 0-30 (20 / 20) | 100 | 1 |  |  | acid 0.5 | Kenshi_Heavy_Rain [1, 60-600] |
| Heavy_Rain deadlands | 1 |  | off | 0-0 (20 / 20) | 100 | 1 |  |  | acid 1 | Kenshi_Heavy_Rain [1, 60-600]; Lightning_Bolt [0, 1-25] |
| Heavy_Rain sonorous | 0.2 | E6E7A9 | E2E4A3, 30000 | 0-0 (20 / 20) | 100 | 1 |  |  |  | Kenshi_Heavy_Rain [1, 60-600]; Lightning_Bolt [0, 8-25] |
| Heavy_Rain sonorous acid | 0.2 | E6E7A9 | E2E4A3, 30000 | 0-0 (20 / 20) | 50 | 1 |  |  | acid 1 | Lightning_Bolt [0, 8-25]; Kenshi_red_Rain [1, 60-600] |
| just wind |  |  | off | 20-40 (360 / 20) |  |  |  |  |  |  |
| Kenshi_Ash-Flakes | 1 |  | EDE9E4, 35000 | 0-33 (20 / 20) |  |  | 2 (0.33, 0.6) | 1 |  | Kenshi_Ash-Flakes_Light [0, 0-0]; poison gas [White] [70, 0-0] |
| Kenshi_Ash-Flakes_BLACK_and_Rising Steam | 1 | B6A792 | EDE9E4, 35000 | 0-70 (20 / 20) |  |  | 1.5 (0.33, 0.5) |  |  | Kenshi_Ash-Flakes_BLACK [1, 40-400]; Kenshi_Rising_Steam [1, 60-600] |
| Kenshi_black_rain AND ground steam vents | 1 | 9F8777 | off | 400-700 (20 / 20) | 100 | 1 |  |  |  | steam-vent [15, 0-1]; Kenshi_black_rain [1, 1-1] |
| Kenshi_red_rain | 1 |  | off | 0-33 (20 / 20) | 40 | 0.65 |  |  |  | Kenshi_red_Rain [1, 50-400] |
| Kenshi_rising_steam | 1 |  | off | 0-70 (20 / 20) | 100 | 1 |  |  |  | Kenshi_Rising_Steam [1, 0-10] |
| Kenshi_Wet_Forest | 1 |  | off | 0-15 (20 / 20) | 100 | 1 |  |  |  | Kenshi_Mist_Cloud [1, 60-600]; Kenshi_Heavy_Rain [1, 60-600] |
| light rain | 0.6 |  | off | 0-30 (360 / 20) | 40 | 0.4 |  |  |  | rain_light [1, 60-600] |
| light rain acid | 0.6 |  | off | 0-30 (360 / 20) | 40 | 0.4 |  |  | acid 0.2 | rain_light [1, 60-600] |
| lightning storm | 1 |  | off | 0-30 (360 / 20) |  |  |  |  |  | weather_lightning1 [10, 1-5] |
| Local-swirl-devil |  |  | off | 20-60 (360 / 20) |  |  | 0.35 (0.36, 0.35) | 0.2 |  | Local-SWIRL-devil [1, 60-600] |
| misty rain | 1 | C6C6FF | FFFFFF, 20000 (wind 40-20) | 0-30 (360 / 20) | 100 | 1 |  |  |  | Kenshi_Heavy_Rain [1, 60-600] |
| purple desert ambience |  |  | off | 30-80 (360 / 20) |  |  | 1 (0.36, 0.1) |  | dust storm 1 | purple desert [1, 60-600] |
| Sand stream | 0.1 | FCAC7C | E9CB9E, 20000 | 20-55 (360 / 45) |  |  | 0.5 (0.33, 0) |  | dust storm 0.8 | Sand-Stream any altitude [1, 1-2] |
| Sand stream ambient | 0.1 | FCAC7C | off | 20-60 (360 / 45) |  |  | 0.35 (0.36, 0) |  |  | Sand-Stream ambient [5, 20-30] |
| shek desert storm |  |  | EED7B5, 15000 | 30-100 (360 / 20) |  |  | 1 (0.36, 0) |  | dust storm 0.5 | shek desert [1, 60-600] |
| SkyBeam | 20 |  | off | 0-30 (360 / 20) |  |  |  | 1 |  | SkyBeam [40, 0-0] |
| sonorous dark copy | 0.6 |  | off | 0-30 (360 / 20) | 40 | 0.4 |  |  | acid 0.2 | Twister-of-fire01 [25, 0-0]; rain_light [1, 60-600] |
| sonorous dark test |  |  | off | 15-40 (360 / 20) |  |  |  |  |  | poison gas test [100, 0-0]; Twister-of-fire01 [25, 0-0] |
| steam-vent from ground |  |  | off | 0-30 (360 / 20) |  |  |  |  |  | steam-vent [2500, 5-400] |
| swamp rain no wind | 0.5 |  | off | 0-0 (360 / 20) | 40 | 0.95 |  |  |  | rain_light [1, 60-600] |
| swamp steaming |  |  | off | 0-0 (360 / 20) |  |  |  |  |  | rising steam slow [2, 60-600] |
| Twister Storm | 0.2 | E6C28E | off | 30-80 (10 / 20) |  |  |  |  |  | Twister-Chuff [4, 10-25] |
| Twisters of FIRE | 0.2 | E6C28E | off | 30-80 (10 / 20) |  |  |  |  |  | Twister-of-fire01 [14, 8-30] |
| venge beams (only 5-23 h) |  | FFA76C | off | 0-30 (360 / 20) |  |  |  | 1 |  | SkyBeam [40, 0-0] |
| WEATHER (44874-Newwworld.mod) |  |  | off | 0-30 (360 / 20) |  |  |  |  |  | weather_sandstorm_wall1 [1, 1-10] |
| WEATHER (62781-rebirth.mod) |  |  | off | 0-30 (360 / 20) |  |  |  |  |  |  |

Notes: "SkyBeam" `clouds density` 20 is clamped to 1 by the sky controller; with `Darkness` 1 its clouds would be black.
"Dust Storm Approach SHORT" fog distance 190000 is beyond the haze's 30000, so its fog is faint.

Not reachable from any BIOME_GROUP (**Verified**, records): the seasons arid canyons, Twisters on the Plains, Kenshi_black_rain
AND ground steam vents, Wet Forest, Steam venting from the ground, Kenshi_Basic_rain, Twisters of FIRE!, Desert-dust-swirl
SEASON, BIG TWISTER, SkyBeams and the two empty "SEASON" records; and so the weathers steam-vent from ground,
Kenshi_rising_steam, Kenshi_black_rain AND ground steam vents, Kenshi_Wet_Forest, Twister Storm, Twisters of FIRE, BIG TWISTER,
SkyBeam, Local-swirl-devil, swamp steaming, lightning storm, sonorous dark test / copy, Desert Calm MEGAHOT 1.0 and both
"WEATHER" records. "Default" is reached only as the fallback (regions without seasons, time windows).

Which season each region has (BIOME_GROUP → SEASON [order, share]; one season with share 1 unless listed), **Verified**:
coastal: Cannibal Plains (+ "nothing, bit of rain, wind", 2 / 2), Darkfinger, Dreg, Greenbeach, Leviathan Coast, Northern
Coast, Raptor Island, Stormgap Coast, The Iron Trail. Desert mild: Okran's Gulf, Okran's Valley, Shem, Sinkuun, Stobe's Garden.
Boneyard dry times: Flats Lagoon, High Bonefields, The Outlands; Bonefields (+ border zone). border zone: Bast, Border Zone.
great desert small: Heng, Skimsands, Spine Canyon. the pits: The Crags, The Pits, The Pits East. fertile green, occasional rain:
Hidden Forest, Okran's Pride, Wend. royal valley: Greyshelf, Royal Valley, The Unwanted Zone. grey desert: Grey Desert, The Eye.
random acid: Cheaters Run, Forbidden Isle. crabby island: Howler Maze, "NONE" (56123). deadlands: Deadlands, Iron Valleys.
Volcano Zone: Sniper Valley, Stobe's Gamble. south coast: Shun, The Hook; Spider Plains (south coast [0, 50], purple desert [1,
5]). Swamp: The Grid, The Swamp. wetlands south: South Wetlands, The Crater. Vain: Arach, Vain. purple desert: Purple Sands.
Others one each: Ashlands (Ashland_Basic), Burning Forest (acid forest + Drifting-foliage), Fishman Island (mad rain with rare
acid), Floodlands (floodland), Fog Islands (fog islands), Gut (nothing, bit of rain, wind), Sonorous Dark (sonorousdark), Stenn
Desert (stenn desert), The Black Desert (poison gas), The Great Desert (great desert), The Shrieking Forest (wet forest -main +
Drifting-foliage), Venge (venge), Desert (Desert Blasts 900 + Desert Summer 300), Skinner's Roam (see Seasons).

## Implementation plan

What the viewer (and later the game) needs, in build order. Each step is testable on its own.

1. **Cloud layer pass** (viewer, small; everything it needs is in the sky inputs already). Draw the dome-direction pass of
   [clouds.md](clouds.md) after the sky and before the moon: plane hit with height 100 and scale 0.001, the two `Clouds.dds`
   lookups with the displacement from `CloudsNormal.dds`, `CloudsTile.dds` at −wind, `DensityOffset = 1.4c − 0.8`, multiplier 3,
   `Darkness = pow(clamp(c − 0.5, 0, 1), 0.3)`, colour `zenithLight + sunColour.rgb (1 − 0.1 D)` with `zenithLight =
   max(getColorAt(+Z) · skyMult, (0.001, 0.001, 0.0015)) · sunColour.g`, the horizon band and alpha rule, output × sqrt(1.4),
   alpha-blended in HDR. Drift: an accumulated offset += wind velocity × dt, × 0.00005. Use the same `c` for `horizonClouds`
   (replacing the stand-in). A test pins `DensityOffset`/`Darkness` and the coverage table.
2. **Weather data and scheduler in Meitou.Data** (`World/Weather*.cs`): read WEATHER / SEASON / BIOME_GROUP / EFFECT with the
   loader rules (fog min := max when wind values match, update time 0 → 1e6, Default fallbacks, appended Default for time
   windows); map zones to regions from `areasmap.tga`; season lengths from shares × `days per year`; the weighted pick, the
   duration roll, strength, the wind model and its interpolation, all on a seedable random source (the game's own `rand()` order
   cannot be reproduced and does not need to be). Expose, for a position and a game time: the camera region's weather, its
   strength, wind, the 30 s sky transition state and the four-region fog blend with the 2.5 % fade. Tests: season lengths for
   Desert and Skinner's Roam, the fog-distance rule for every base weather, weighted-pick frequencies, wind bounds.
3. **Hook the viewer to it**: `--weather` keeps forcing one record; otherwise the scheduler runs from the camera position and a
   time-of-day / day control, feeding `sky color mult`, `c`, the cloud drift and the weather fog (the haze already takes a fog
   colour and distance).
4. **Wetness, dust and rain ripples** (shader-only): add `makeWet` with the per-surface absorbance (terrain layers' `absorbance`
   fields, objects from gloss and metalness, foliage 0.9) and the wetness ramp (+0.01/s, −0.005/s); the dust term on DUST objects
   with the biome `ground colour` and the noise texture; the water's three ripple layers from `rainAmount = saturate(rain / 50)`.
5. **Particle effects** (the big one): a reader for ParticleUniverse `.pu` scripts (systems, techniques, Box / Circle / Point
   emitters, billboard renderer types, Colour / Scale / TextureRotator / LinearForce / Vortex affectors, `dyn_random` /
   `dyn_curved_*` attributes) and a CPU billboard renderer; then the effect groups by `type`: camera box with wrapping for rain
   and ash first (most visible), then the map-feature placers (volcano plumes, steamers, the permanent dust storm: static
   positions, always on), then wandering storms and twisters with their fog-volume spheres, lightning last.
6. **Not planned here**: heat haze (belongs to the post-processing work; the `heatHaze` value comes from step 2), sounds,
   gameplay effects.

## Unknowns

- How the areas map's pixels are placed in world coordinates (origin and size fields of the terrain object); taken as 4 px per
  zone over the whole world.
- The season order for equal order values; the new-game start of the first season; the special case for a season with one
  weather in the SEASON loader (a ceiling of a scaled value, not decoded).
- `sunlight color` of SEASON and `sky colour multiplier` / `colour multiplier` of EFFECT: loaded, their use not traced.
- How effect groups use the wind fields, emission-rate scaling and `maximum view distance`; the camera box's exact distance
  source (a camera value at offset 0x7c).
- How fog volumes are drawn.
- The full gameplay effect of each `WeatherAffecting` value.
