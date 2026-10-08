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
**Verified** against the merged records (`WeatherTests.Base_game_seasons_and_regions_follow_the_calendar_and_fallback_rules`): every
season of the base game without weathers becomes Default with limits 0..1; the only windowed weathers are "Desert Calm hot 0.6" and
"venge beams", and every season that lists one ends with Default at weight 0. The duration given to the Default of an empty season (and
to a region without seasons) is **Observed**: not read from the game, 120..720 minutes (the wind update time is 360).

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
zone) at the cell's centre (**Observed**: the two cell fields used are taken to be the centre; 3051 of the 4096 4 × 4 blocks are one
colour, the rest lie on region borders); a cell whose colour matches no record goes to "NONE" (`18003-gamedata.base`, no seasons, so
Default weather).

**Placement in the world (Verified, `WeatherTests.Areas_map_places_known_towns_in_their_regions`, 2026-10-08)**: the map is the
whole 64 × 64 zone grid with no offset and no flip. Decode the TGA to rows top to bottom (its descriptor byte is 0, a bottom-left
origin, so the file stores the last row first); zone cell `(cx, cz) = (floor(x / 4608) + 32, floor(z / 4608) + 32)` reads pixel
`(4 cx + 2, 4 cz + 2)`, the colour taken as `R << 16 | G << 8 | B` and compared with `index` (71 of the 71 colours in the map match a
record that way, 12 the other way round). Checked against the placed towns, all of which land in the region of the same name or an
expected neighbour: The Hub, Squin → Border Zone; Rebirth → Rebirth; Heng → Heng; Bast → Bast; Flats Lagoon → Flats Lagoon; Mongrel →
Fog Islands; Ashland Dome I–III → Ashlands; Tower of Abuse → Venge; Heft, Stoat → The Great Desert; Shark and the Swamp Villages → The
Swamp; Admag → Stenn Desert. The mirrored map (z flipped) puts Rebirth in the swamp, so the match is not a symmetry accident. All 71
colours match a BIOME_GROUP, so "NONE" only serves the cells outside the map and records a mod adds. Two records are called "NONE"
(`18003-gamedata.base`, index 000000, 21,368 pixels, no seasons; `56123`, index 329999, the "crabby island" season), so regions are
matched by StringId and colour, never by name. Painted by no pixel: Central, Desert (the weather region "Desert" with its 75/25-day
calendar is reachable only through its record), Empire.

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
  uses the same weights: **Observed**, the decompiler shows one fixed weight there.) **Observed** in Meitou: the four points are
  the corners `(x ± 1300, z ± 1300)` (the other reading of "± 1300 in x and z" is the four axis points; compare the fog at a region
  border in the game with `WeatherWorld.FogWeights`), and the blended colour and distance average only over the regions whose fog is
  on, weighted by their weights, so a region without fog (whose record distance is meaningless) does not pull them towards zero;
  the blended on/off weight is the plain weighted mean.
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
`1/3` per second; `post/heathaze.hlsl` uses it ([post-processing.md](post-processing.md#heat-haze-verified)).

- Which weather: the camera region's current weather (the same `region` object whose rain and dust are used); `heat haze` is
  the WEATHER field at offset 0xa4 of the loaded weather, `s` the region's strength, `sunY` the height of the sky controller's
  sun direction (FUN_14066cad0). Re-checked 2026-10-05 in FUN_1409e8f70.
- Step: by at most `dt / 3` per frame, landing on the target when closer; `dt` is the game-speed-scaled frame time with 0.01 while
  paused (see "Time bases"), so it still settles, slowly, while paused. A teleport (the same flag that snaps wetness and dust)
  sets it to the target at once.
- FUN_1409ea410 writes it every frame into `SharedSkyParams` `heatHaze`; the character editor's workspace sets 0 (FUN_1405f4dd0).
  The shader's animation runs on `gameTime` (game hours since the load, FUN_14066f190), which stops while paused: the shimmer
  freezes in place, its strength still settling.

**Which weathers and regions** (**Verified**, merged base records, 2026-10-05; region → season lists in "Base-game data"): 19 of
53 weathers have a `heat haze` (the table's column). Reachable through a BIOME_GROUP's seasons, with the season's strength limits:

| Region (BIOME_GROUP) | Season (s range) | Weathers with heat haze (weight / sum of the season's weights) |
| --- | --- | --- |
| Venge | venge (0.5–1) | venge beams 1 (100/109, only 5–23 h), Desert Calm hot 0.6 (6/109), Desert-dust-swirls 0.25 (3/109) |
| Ashlands | Ashland_Basic (0.7–1) | Kenshi_Ash-Flakes 1 (the only weather) |
| The Great Desert | great desert (1) | Desert Calm hot 0.6 (5/42, 7–22 h), Desert-dust-Light Detritus01 0.5 (4/42), GD ground sand orange 0.4 (10/42), great desert streamers 0.25 (20/42), Desert-dust-swirls 0.25 (1/42), Desert Wisps 0.2 (2/42) |
| Heng, Skimsands, Spine Canyon | great desert small (1) | Desert Calm hot 0.6 (5/45), Desert-dust-Light Detritus01 0.5 (5/45), GD ground sand orange SMALL 0.4 (10/45), great desert streamers SMALL 0.25 (20/45), Desert-dust-swirls 0.25 (5/45) |
| Stenn Desert | stenn desert (0.7–1) | Desert Calm hot 0.6 (10/60), Desert-dust-swirls 0.25 (20/60) |
| Grey Desert, The Eye | grey desert (0.5–1) | Clear Times SHORT hot 0.5 (11/33) |
| Okran's Gulf, Okran's Valley, Shem, Sinkuun, Stobe's Garden | Desert mild (1) | Clear Times SHORT hot 0.5 (4/34), Desert-dust-swirls 0.25 (11/34), ground sand 0.2 (4/34), Desert Calm 0.1 (4/34) |
| Skinner's Roam | Clear AND Storm Mix SHORTSEASON (1; 17 of 100 days) | Clear Times SHORT hot 0.5 (7/10), Dust Storm Approach SHORT 0.5 (3/10) |
| Desert | Desert Blasts (1; 75 days) / Desert Summer (0.5–1; 25 days) | Desert Blaster 0.25 / Desert-dust-Light Detritus01 0.5 |
| The Shrieking Forest, Burning Forest | Drifting-foliage (1) | Drifting-foliage 0.5 (300/400), Desert Blaster 0.25 (100/400) |
| Border Zone, Bast, Bonefields | border zone (0.5–1) | Desert-dust-swirls 0.25 (20/45), Desert Calm 0.1 (5/45) |
| Shun, The Hook, Spider Plains (south coast season) | south coast (1) | Desert-dust-swirls 0.25 (20/100), Desert Calm 0.1 (30/100) |
| Cannibal Plains (half the year), Gut | nothing, bit of rain, wind (0.5–1) | Desert Calm 0.1 (1/3) |
| The Pits, The Pits East, The Crags | the pits (0–1) | Desert-dust-swirls 0.25 (10/70), Desert Calm 0.1 (10/70) |

Not reachable: SkyBeam 1, Desert Calm MEGAHOT 1.0 1, Local-swirl-devil 0.2 (their seasons have no region), and the seasons arid
canyons and Desert-dust-swirl SEASON. "Default" has none, so a region without seasons never shimmers. The strongest common
case is Venge by day (`heatHaze` 0.5–1) and the Ashlands (0.7–1); the Great Desert reaches 0.6 in "Desert Calm hot 0.6".

**The viewer** has no weather scheduler yet: `--weather <name>` forces one record, taken at strength `s = 1`, so its `heat haze` ×
saturate(6 · sunY) is the target; the value moves towards it at 1/3 per real second (game speed 1, never paused) and jumps to it
for screenshots. `gameTime` is real time × 11/1200 hours per second from the viewer's start, held still for screenshots; the game
build (`meitou`) passes its own clock's hours instead. `--heat-haze <x>` replaces the weather's field (testing),
`--no-heat-haze` turns the pass off (the game's `HeatHaze=0`). The pass's depth falloff uses D = 10 × the install's `view distance`
(12000 → 120000; `--heat-haze-view-distance`), see [post-processing.md](post-processing.md#heat-haze-verified).

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
| CAMERA (1), CAMERA_RAIN (5), CAMERA_ACID_RAIN (6) | camera group | in front of the camera, moving with it; affects the whole region. The system sits at the camera node and the particles are kept in a cube centred `d` ahead of the camera along its view direction and wrapped modulo it (**Verified (decompiled)**, FUN_140101800 / FUN_140100dc0 / FUN_140101a20: edge `2d / 1.5`; `d` is the float at offset 0x7c of the effect data, the **size** the EFFECT loader computes: 0.75 × the largest Box extent (× scale), so the cube is the emitter box; details in [particle-universe.md](particle-universe.md#the-camera-effects-cameraeffectgroup)) |
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

### The groups in detail (Verified (decompiled) where marked; the rest Observed)

All built by `EffectGroups.Create` (`src/Meitou.Data/Particles/EffectGroups.cs`); a *unit* is the game's effect handler (a particle system at a
place) and exists as a cheap record all the time, holding a simulation only while the camera is near (Observed: the game simulates the 3 × 3
active zones of 4608 units and, outside them, only within the effect's `maximum view distance`; Meitou: within 6912 + view distance of the unit's
bounds, deactivated beyond 1.15 × that).

- **Placing** (**Verified (decompiled)**, FUN_1408fc3c0): a random cell of the region (cells of the same colour as the camera's in `areasmap.tga`),
  a random point in the middle 80 % of it, the ground height there; up to 10 tries for `min/max altitude` (skipped when both are 0; the altitude
  is the absolute world y, water is 100) and terrain normal y within `min/max slope`; after 10 tries the last point stands.
  The z margin's sign is not resolved in the decompile (both axes use the same). The normal comes from finite differences of the height (step 12).
- **Life** (**Verified (decompiled)**, FUN_1401034f0): random between `min` and `max time to live`; none (immortal) when the maximum is 0. When it
  is over the emitters stop and the particles live out; the unit is removed when they are gone.
- **POINT, POINT_LIGHTING, WANDERING*** (**Verified (decompiled)**, FUN_140102370 schedule, FUN_1401039a0 / FUN_140103ca0 spawns): a timer
  starts at the entry's minimum respawn time, runs down on the frame clock; at 0, and while fewer than `count` units exist (`count` 0: no limit),
  one is made and a random time between the entry's respawn min and max is added (0 / 0: all at once). The viewer caps units at 256.
  *Observed*: lightning is placed in a 3000 disc round the camera at ground + 10 (the game snaps to metal objects within ±2000 of a random
  point; the viewer has none); wandering units walk their heading at `wandering speed`, are carried by the wind when wind affected, follow the
  ground (re-grounded every 0.5 s) and turn at random (the decompile's arithmetic is not resolved).
- **GLOBAL** (**Verified (decompiled)**, FUN_140102040, FUN_140103f50, FUN_140101dc0): `count` units, all at once whatever the respawn times
  (`count` 0 makes none: `fog islands` never appears in the base game), immortal; each sits at the camera's x, z, ground + 100 (limited to
  `min/max altitude` when either is non-zero); its particles are kept within `d` of the node (a sphere wrap, not the camera cube).
- **GLOBAL_POINT** (**Verified (decompiled)**, FUN_140103160, FUN_1401048b0, FUN_140104610): `R` = `maximum view distance` (1000 when 0); `count` units
  within R of the camera and `count` between R and 2R (3 × `count` at most), each at a sqrt-uniform distance in its ring on the ground; units
  beyond 4R stop. Respawn times unused. **Observed (deviation)**: distances are horizontal (the unit is on the ground and the eye may be
  hundreds up; `Drifting-foliage` and the swirls have R = 50, and with 3D distances the first unit was 212 away and stopped at once, so nothing was
  ever emitted: found by a screenshot, fixed, a test covers the emission).
- **Camera effects**: one unit whatever `count` ([particle-universe.md](particle-universe.md#the-camera-effects-cameraeffectgroup)).
- **Map-feature placers**: static immortal POINT units at the placements ([below](#effect-placers-on-the-map-verified-records-and-featuresdat));
  `MapEffectPlacers.Find` / `Groups`. Observed: the game's handler lives min..max time to live and is made again; the viewer keeps one.
- **Wind** (**Observed**, the decompile of the wind use is not traced): `wind affected` adds wind × `wind speed mult` to the particles' motion; `wind
  direction emission` turns the emitted horizontal direction to the wind; `min/max wind span rate` scales the emission rate 0..1 over that wind
  range (with only a minimum: 0 below it).
- **Fade times** (`particle fade out delay`, `fog fade in/out`): the fog volume's alpha is faded by them (see Fog volumes); the particles' own
  fade is the scripts' colour affectors (Observed).

**Density (checked 2026-10-08; no bug found, cause Unknown).** The Ashlands ash is `Ash-Flakes_Light`, 1550 flakes by its techniques' quotas (the
quota column of `meitou-tools particles effects`), in the camera cube of edge 120 wrapped round the view axis, so the flakes are always within
about 150 units of the eye and a picture shows about 1500 of them. Nothing else scales that number: the camera group is one unit whatever the
entry's `count` (**Verified (decompiled)**, FUN_140102ff0), the system scale is 1, and the weather strength is not used by the groups
(**Unknown**), and prewarm fills the cube within a few seconds. `Kenshi_Heavy_Rain` the same: 5000 quota in a 120 × 120 box, 6400/s × life
0.3..1 s, about 4000 streaks alive. Every number the data gives is in use, so denser flakes in the game would come from something not read:
candidates are the plugin's per-technique default quota where a script sets none (taken 500, **Unknown**), `sky colour multiplier` and the
strength. The flakes are 0.4 to 0.7 units, so only the nearest few are big on screen (**Observed**).

**Lights** (Observed): `EFFECT.light` references (`Lightning_Bolt` has one) exist in the records (`EffectRecord.Lights`); the renderer has no point
light path, so they are read and not used.

**Textures** (Observed, by `meitou-tools particles`): `Haboob_Finger.png`, `TwisterDust_Large.png`, `EX-DustMite`, `SteamRise01.png` and the
material `SandBlown_Streaks` are not in the install; the particle pass draws a 1 × 1 white texture for them (a plain soft quad), so
those systems are approximate.

### The particle scripts (Observed: `data/particles/scripts/*.pu`; the format, the survey and the implementation: [particle-universe.md](particle-universe.md))

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
Sphere" 661, ...), with alpha and density distance. The static ones on the map are `fogfeatures.dat`, all of it in
[fogfeatures.md](fogfeatures.md): they, not the weather, make the swamp's fog (the Swamp's weathers have `fog enabled` off), the Fog
Islands', the Vain's red haze and a few others, always on whatever the weather (**Verified**: the file and its loader; **Observed**: no
weather or region value reaches them). They are drawn in the scene pass after the haze (queue 82) with post/fog.hlsl's `fog_planes_fs` /
`fog_sphere_fs` / `fog_beam_fs` (**Verified**: the shipped shaders). The twisters' EFFECT_FOG_VOLUME spheres are the same `FogSphere` objects
(type 1 a `FogCylinder` from `position` to `position 2`), drawn with `fog_sphere_fs` among the placed volumes, with the record's alpha and
`additive colour`; their fade grows the radius from 0 and brings the density distance down from 10 radii (a cylinder: from 4 × its own),
the alpha unchanged; and from inside its own sphere the camera does not see it. All of it, with the functions, in
[fogfeatures.md](fogfeatures.md#the-weather-effects-fog-volumes-verified-decompiled-fun_1400fb070-and-the-fades) (**Verified (decompiled)**).

In Meitou: an EFFECT's `fog volumes` are read as `EffectFogVolume` (type, radius, density distance, alpha, colour, additive, offsets `pos` /
`pos2`, `ground`) and every unit of that effect hands one per record to `FogVolumes` (`ParticleRenderer.CollectFogVolumes`): a sphere at the
unit's position + offset (a cylinder to the unit's position + `pos2`), with the unit's fade (`fog fade in/out` over its life) applied as the
game does, drawn per pixel with the placed volumes by the game's formula, sorted with them. `ground movement` is read and unused.

## Effect placers on the map (Verified: records and `features.dat`)

MAP_FEATURES with `hidden` true draw no mesh; their EFFECT is attached as an **instance** of the feature record (not a
reference list): "Volk-Cloud Placer" → `Volk-Cloud` (POINT, `Volk-Cloud` plume; 24 placements, the Ashlands volcano plumes),
"Volc-small-steamers Placer" → `Volc-small-steamers` (POINT; 18), "Permanent-dust-storm Placer" → `Permanent-dust-storm`
(POINT; 1), "Fog-Wall_SkinnersRoam 01" → `DesertCloudStorm` (no placements in `features.dat` of the base game, **Observed**),
and "Marker-Attractor" (a bird attractor, `athene.mesh`; 1). These effects belong to the feature, not to any weather, so they
are always on. Placement counts are from [zones.md](zones.md#map-features). The placer effects have life 800..5500 s, `wind
affected`, `ground colour` and view distances 200..1000 (EFFECT fields), so they drift with the wind and take the biome's ground
colour (**Observed**: the use of these fields for feature effects was not traced).

In Meitou (`MapEffectPlacers`, **Verified**: the placements; **Observed**: the rest): each placement in `features.dat` of such a feature makes a
static, immortal POINT unit at the placement's position plus the instance's offset (rotated by the placement's rotation; all 0 in the base
game); 43 units in 3 groups. A unit is simulated while the camera is within 6912 + its view distance of its bounds (plume radius 10700 for
`Volk-Cloud`, so it is awake from far off). They are drawn whatever the weather, also `Default`: a view over the Ashlands volcanoes has
smoke columns now (`--no-particles` removes them). The "Volk-Cloud" plume is a column of dark smoke with a trail of dark puffs
(**Observed**: the second technique's colour ramp; unconfirmed against the game).

## Gameplay effects (facts only)

From the strings and fields next to the weather code (**Observed**): WA_DUSTSTORM lowers combat accuracy unless gear gives
"weather protection"; WA_ACID burns unprotected skin ("It is currently raining acid ..."); WA_GAS and WA_BURNING damage inside
effect radii (`effect radius`, `effect strength`); BIOME_GROUP `acidic ground` burns bare feet, `acidic water` prevents
swimming; clothing has `weather protection0` (a `WeatherAffecting`) and `weather protection amount`; rain collectors need rain
("Not Raining") and wind generators need wind ("No Wind", operating speed range). Not traced further.

## Base-game data (Verified: merged base records, 2026-10-05)

53 WEATHER, 48 SEASON, 74 BIOME_GROUP, 74 EFFECT, 8 EFFECT_FOG_VOLUME. The weathers, with the fields that drive rendering and
scheduling (empty = 0, i.e. black for the two colours: the loader reads a missing colour as 0, [sky.md](sky.md#weather-tint); none; fog distance after the loader's rule; affects = `affect type` and strength):

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

## In Meitou

Step 2 of the plan below is done, in `src/Meitou.Data/World/`; step 3 (the hookup, see "In the viewer" below) is done too.

### Wet surfaces, dust and rain ripples in the viewer (step 4)

Done in the shaders (`AtmosphereShaders.Functions` holds `makeWet`, `dustCover`, `dustColour`) and `WeatherSurfaces` (`src/Meitou.Rendering`).
The shared values are frame globals, so every program of the frame reads them: `uWeatherWet` = (wetness, `rainAmount` = `saturate(rain / 50)`,
`gameTime`) and `uWeatherDust` = (x, inside, slope) in the native `FrameConstants` block (offsets 240 and 256), the dust noise and the ground
colour map as two more frame textures. They are 0 until the viewer sets them, and at 0 every shader draws what it drew before. Since the hookup
`WeatherSurfaces.Apply` takes wetness, `dustAmount` (x, inside, slope) and rain from `gpu.WeatherState` every frame, so the scheduler's ramps drive them and
`--weather <name>` (a snapped forced record) goes through the same path; `--wetness x`, `--rain x` (0..100) and `--dust x[,inside,slope]` replace
them. The water's ripple phase uses the water's own time (`uTime`), the clock the frame passes to `WaterRenderer.Draw`.

- **Terrain** (`TerrainShaders.Fragment`, also the TERRAIN-mode rock meshes): `makeWet(albedo, wetness, 1 − gloss + absorbance, waterHeight − y, 2)`
  with the layers' `absorbance` blended exactly like the textures (`biome()`: base → grass by the overlay → slope → dirt → road → cliff; two more
  texels in the biome parameter row, `ParamTexels` 13) and averaged over the pixel's biomes by their weights. The far ground colour and the untextured
  fallback take the old fixed 0.5 (**Observed**: the game's distant terrain is another material, not traced). This replaces the earlier stand-in
  ("the game's wetness rule", absorbance 0.5 everywhere); at wetness 0 only the water-line band differs, where the real absorbance (1 − gloss + layers,
  about 0.8 to 1.3 on sand) darkens the underwater edge a little more: 0.137 % of Port North's pixels at 13:00 (max 53 of 255), 0.011 % of the Hub's,
  nothing else of the ten views. A rock bake (`uWaterHeight` far below the world) gets no weather, so an impostor never keeps a rain.
- **Objects and foliage meshes** (`Shaders.MeshFragment`; the push constants' spare word, `MeshSurface`, carries the draw's bits: 1 DUST, 2 foliage shader,
  4 no weather for impostor bakes, 8 interior): absorbance `(1 − gloss)(1 − metalness)` with the viewer's gloss × `specular mult` (no metalness map: 0),
  foliage (FOLIAGE-mode map features, leaves) 0.9, `edge` 0.5. The gloss that reaches the lighting is the wet one. The grass (`GrassFragment`) starts from
  gloss 0 and writes `0.6 ×` the wet gloss, as `foliage.hlsl` does. Distant trees' impostors (the atlas is baked dry) are wetted at run time with 0.9
  (a rock's impostor with `1 − gloss + 0.4`, **Observed**: the atlas holds no biome).
- **Not done**: the water-line term on objects, foliage and grass (they have no water height; it would change the ten views at Default, the game
  darkens them); `makeWet` of building **interiors** (wetness 0, dust = `dustAmount.y`: the shader has the bit, nothing in the viewer says which parts are
  interiors yet, so every part uses x); the dust of `distant_town` stand-ins; and the **characters and creatures**. Searching every shader in
  `data/materials` (**Verified**): `dustAmount` / `dustColour` appear only in `objects.hlsl` and `triplanar.hlsl`; `makeWet` is called by
  `terrainfp4.hlsl`, `objects.hlsl`, `foliage.hlsl`, `character.hlsl`, `creature.hlsl` and `skin.hlsl`. The last three take their wetness from a
  per-vertex value (`skin.hlsl`: `max(waterLine.z, saturate(waterLine.x − y) · waterLine.y)`, the character's own wet state, not the shared `wetness`)
  with the absorbance `1 − 2 · gloss` (characters: also `× (0.2 + 0.8 · clothing)`) and the 0.5 edge. Whether `waterLine.z` follows the weather is
  **Unknown** (not traced), so the viewer's characters stay dry.
- **makeWet below wetness 0.3** (**Verified** from `common/wet.hlsl`): the game's `(1 − 1/(wet + 0.7)) · absorbance` is negative until `wet = 0.3`, so
  the game *brightens* dry, absorbent surfaces by up to `0.5 · 0.43 · absorbance` (about 11 % at absorbance 0.5, 25 % at 1.2). The viewer clamps the
  darkening at 0 (`max(darken, 0)`): its dry look is unchanged and the Default views stay 0 px, but under a light rain ground gets darker than dry only
  from wetness 0.3. **Unknown** whether the game's gbuffer encoding or the lighting pass compensates; compare a dry and a wet game screenshot before
  removing the clamp. The old terrain stand-in had the same clamp.
- **Dust** (`dustCover`): exactly the formula above. Applied to the building parts (every part, DUST is always on for them, `PlacedKind.BuildingPart`)
  and to TRIPLANAR / DUAL_TRIPLANAR map features (`SurfaceMaterial.Dust`, foliage-layer meshes of those modes), as the plan asked, because
  `triplanar.hlsl` has the branch. **Unknown** whether the game ever reaches it: the caller table in [runtime-materials.md](runtime-materials.md)
  gives map features only CLIP_INTERIOR, never DUST, and no script sets the define; if the game does not, the triplanar dust is a viewer addition and
  is a viewer addition: it is the Enhancements switch `dust` (Meitou, default on; Faithful off = building parts only; `uWeatherDust.w` carries it, surface bit 16). Not applied to
  UV-mapped, TERRAIN-mode and FOLIAGE map features, items or characters (no DUST in the game), so e.g. the red rocks round the Hub (TERRAIN mode) and
  wrecks that are UV-mapped features stay clean. The noise is `Turbulent.dds` (the `dust` texture unit; 512²
  DXT1, repeating, mipmapped; `.x` is read) at `world.xz · 0.002`. The gloss is the diffuse alpha before `specular mult` (the viewer's 0.3 for a
  cut-out material). The colour is read per pixel from the terrain's whole-world ground colour map (the BIOMES `ground colour` blended by the blend map,
  × `brightness fix`) at the surface's world position, not the one biome at the object's origin the game's material holds (**Observed**: the game builds
  one material per biome and object; the difference shows at biome borders and where `brightness fix` is not 1). A normal map is flattened by half the
  coverage (objects only). The coverage is mostly where the surface's gloss is low: `−6 · gloss` outweighs the slope term, so a glossy part
  (`diffuse.a` 0.3 or more) stays clean even at dust 2.
- **White caps in the game's Ashlands (the owner's reference shots), rocks and pillars included** (**Observed**, not traced to a cause): the only dust code
  is in `objects.hlsl` / `triplanar.hlsl`, and TERRAIN-mode map features use the terrain material, so they cannot be dusted by `dustAmount`. The likely
  source is the ground itself: a TERRAIN-mode feature takes its textures from the biome at its origin (terrain.md, "TERRAIN-mode meshes"), and the
  Ashlands biomes' slope and cliff layers are ash textures (`land/textures/Ashland_Ash_DIF.dds`, `Ashland-snow_DIF.dds`, `Ashland_AshNoise_DIF.dds`),
  so rocks look capped without any dust. The install also has Ashlands-specific object textures (`Assets/Things/Ashland_*`, `AshlandRottenTower_*`,
  `Assets/Buildings/AshDome*`), which may be baked ash. **Unknown**: whether the shots were taken in Kenshi_Ash-Flakes weather (dust would then also
  cap building parts), and whether the viewer's Ashlands rocks and pillars already show the ash layers (they do in the viewer's clean render of Ashland
  Dome Ruin). Compare a game shot in clear Ashlands weather with one in ash flakes before adding any dust to TERRAIN-mode features.
- **Water** (`WaterRenderer`): the three ripple layers as in the formula above, `rain-ripples.png` (repeating, mipmapped) at `world.xz · 0.01 · 6`,
  `· 6` with x and z swapped, and `· 2`; the time is the shader's own (`uTime × distortion`, the water's `gameTime`), so a still repeats exactly.
  `rainAmount` fades with `saturate(2 · view.y − dist · 0.0001)`.

| Class | Role |
|---|---|
| `WeatherData` (`WeatherData.cs`) | Reads WEATHER / SEASON / BIOME_GROUP / EFFECT / EFFECT_FOG_VOLUME from a `GameDatabase` into plain records (`WeatherDef`, `SeasonDef`, `RegionDef`, `EffectDef`, `FogVolumeDef`) with the loader rules: wind update time 0 → `WeatherDef.NeverMinutes` (1,000,000), fog distance min := max when the fog wind values match, Default for a season without weathers (limits 0..1), Default appended with weight 0 when a weather has a time window, region multipliers, `days per year` from GLOBAL CONSTANTS (`GameConstants`). Definitions can also be built by hand (`WeatherData.FromDefinitions`), which the quick tests do. `SeasonCalendar.Lengths` cuts the year. |
| `WeatherAreas` | The 64 × 64 cell map from `areasmap.tga` (`TgaReader` in `Meitou.Data.Textures`), cell rectangles and distances. |
| `WeatherRegion` | One region's schedule: season calendar, weighted pick with time windows, duration roll, strength, the wind model and its interpolation, the 2.5 % fog fade. Own `System.Random` seeded from the world's seed and the region's index. `Snapshot()` / `Restore()` expose season index and end day, weather entry and its start/end minute, strength, wind interpolation and fog fade for a later save format. |
| `WeatherWorld` | All regions plus the camera side: `Update(camera, WeatherTime, FrameTimes, sunHeight)` returns the `WeatherState` of the frame (also `Current`). `ForceWeather(name or WeatherDef, strength, snap)` is `--weather`. `WeatherWorld.Create(db, install, seed, start)` builds it from a database and the install; `FogWeights(x, z)` exposes the four-sample weights; `Snapshot()` / `Restore()` cover every region. |
| `WeatherState` | The frame's output, as listed in the shared design: weather record and strength, wind (xz direction, speed), sky colour multiplier and cloud density after the 30 s transition, cloud drift (direction × speed), blended fog (`FogEnabled` weight, colour, distance), `Rain`, `Wetness`, `DustAmount` (x, y inside, z slope), `HeatHaze`, and `Effects` (EFFECT, count, respawn) with `EffectStrength`. |
| `WeatherTime`, `FrameTimes`, `WeatherRamps` | Game time (day count + hours), the three frame times (`FrameTimes.FromClock(realDt, gameSpeed, paused)`), the wetness and dust ramps (`HeatHaze` is reused for the haze). |

`meitou-tools weather [--region <name> | --at x,z] [--days d0 d1] [--seed n] [--list] [--cells]` (`--cells`: the centres of the region's areas-map cells, to place the viewer) prints a region's season and weather chain (start, name,
strength, duration, wind, fog); `--list` prints every region's calendar. Every timer runs on the frame times the caller passes in and on
the game time it passes in; nothing reads a clock, so a seed and a call sequence give the same weather.

**Choices where the game is Unknown or the doc says Observed** (compare these against the game when a way to see it turns up):

- *Start*: all regions start their first season (by `val0`) on the creation day, with the first weather chosen at once; the new-game
  path and what saves keep were not traced.
- *Time-window cut*: `end = min(start + duration, today's end time)` as the doc says; when that end time is already past (the window's
  last hour, accepted by the inclusive test) it is not applied and the rolled duration stands, instead of re-choosing every frame.
- *Season changes* happen at the first update whose day count has reached the end day, then end = that day + length; weather changes
  at the first update past the end minute, with the new end = now + duration (so a coarse frame step shifts the chain slightly).
- *Wind heading*: the read-back is the unsigned angle 0..π, as the doc says, and the interpolation starts from it, so the direction
  mirrors across the x axis at an update when it pointed to the other side (the timeline tool shows this as −38° becoming 38°).
  Interpolation of the angle takes the shorter way round.
- *Fog fade*: while a region's fog fades in from "off" (or out to "off") the colour and distance are already the new (or still the
  old) weather's; only the on/off weight lerps. Forced weathers have no fade.
- *Fog samples and blend*: see "Fog" above (corners; colour and distance weighted by regions with fog on).
- *Time bases*: "0.01 when paused" is the factor that replaces the game speed in the settling time (`dt × 0.01`), not a fixed step.
- *Forced weather*: strength 1 (settable), wind speed `lerp(min, max, s)` along `ForcedWindDirection` (default +X), fog undelayed;
  sky, clouds, wetness, dust and haze snap to it unless `snap: false`.
- *Sky transition* restarts whenever the camera's weather record changes, including by crossing into a region with another weather.
- *Wetness* and *dust* clamp on the target instead of overshooting by one step.
- *Region switch* uses the strict 500-unit rule from the current cell, also after a teleport.

## In the viewer and the game (step 3)

`WorldWeather` (`src/Meitou.Rendering/WorldWeather.cs`) owns the `WeatherWorld` of a `Gpu` (`gpu.Weather`; `gpu.WeatherState` is the frame's
`WeatherState` for the renderers that read it later) and is called by `WorldFrame.Draw` after the sun is known and before the sky is prepared.

- **Inputs per frame**: the eye position, the game day and time of day (the viewer: `--day`, default 0, and `--time`; the game: its clock), the
  frame times (`FrameTimes.FromClock`: real time measured by the weather, at most 0.25 s a frame; the game passes its speed and paused flag, the viewer
  is at speed 1 and never paused) and the sun height. A held frame (`--screenshot`, `PostProcess.InstantAdaptation`) has dt 0, and the first
  frame is a teleport, so a picture shows the settled state (sky, clouds, fog, wetness and haze snapped).
- **Reroll** (Tab panel, a viewer tool, not the game's): `WorldWeather.Reroll` releases a forced weather and calls `WeatherRegion.Reroll` on the camera region, which starts a
  different weather of the current season now (weighted pick without the current weather and weight-0 entries; new duration and strength), then snaps.
- **Schedule from day 0**: the world is created at day 0, 00:00 and the regions run in game minutes up to the shown time, so `--day 52 --time 14`
  shows what `meitou-tools weather --region <r> --days 0 100 --seed 1` lists at that time (same seed, default 1; `--weather-seed`). A step
  forward in time (the `]` key, the time slider) replays the schedule through the jump, and a jump of more than an hour snaps everything. **Observed**:
  this makes every region's season calendar count from day 0 (the game's own start is Unknown, see "Choices" above); the schedule never runs backwards.
- **Mapping**: `SkyColourMultiplier` and `CloudDensity` into `SkyRenderer.SkyColourMultiplierInput` / `CloudDensityInput`; `CloudDrift` into
  `CloudWind` (`--cloud-wind` wins, `--clouds` still wins over the density); the fog into `SkyRenderer.FogInput` (weight, colour, distance): the
  shader's fog term (`uAtmoFog.z`) is now the blended weight, the game's `fogColour.a`, so a half-faded fog is half-strength; the heat haze
  into the post effect (`state.HeatHaze`, already ramped on the settling time; `--heat-haze <x>` replaces the weather's field and ramps in the
  viewer, with the state's strength); the particle effect list, strength s and wind into `ParticleRenderer.SetWeather` (`WorldWeather.EffectInput`;
  the camera effect groups are kept while the weather stays, rebuilt and prewarmed when it changes).
- **Forcing**: `--weather <name>` is `ForceWeather(name)` (strength 1, wind speed at its maximum along +x); `--weather auto` or no option is the
  scheduler. `--weather Default` is clear with no fog, clouds, haze or particles, which `tools/scripts/parity.sh` uses so the ten views stay
  comparable with the pre-weather baseline.
- **Statistics, log, keys**: the F11 lines `weather` (camera region, its season, the weather, strength, wind speed and heading, time left in
  the weather, day) and the sky multiplier, cloud density, fog, rain and wetness; the console prints a `weather` line when the camera region's weather
  changes (or a weather is forced). Keys: `,` / `.` change the hour (the day follows midnight), `[` / `]` the day, `\` cycles the forced weather
  (auto, then each WEATHER record), see [../viewer.md](../viewer.md).
- **Game**: `GameHost` passes the clock's day, the simulation's speed (the last non-zero one while paused) and the paused flag, so cloud drift,
  fog fades and the sky transition stop with the pause while wetness and haze still settle (factor 0.01). The schedule's state is not saved:
  **TODO**, `WeatherWorld.Snapshot()` / `Restore()` exist but the save format has no place for them, so a loaded game rolls its weather anew.
  The game's start day follows the clock (`--day`, default 1).
- **Unknown / approximate**: the effect groups take the weather strength s, not `WeatherDef.EffectStrength` (the doc says the weather's effect strength: compare
  once a way to see it turns up); a Tab-panel weather control does not exist (keys only).

## Implementation plan

What the viewer (and later the game) needs, in build order. Each step is testable on its own.

1. **Cloud layer pass** (viewer, small; everything it needs is in the sky inputs already). **Done** 2026-10-08, see
   [clouds.md](clouds.md#in-the-viewer); the spec follows. Draw the dome-direction pass of
   [clouds.md](clouds.md) after the sky and before the moon: plane hit with height 100 and scale 0.001, the two `Clouds.dds`
   lookups with the displacement from `CloudsNormal.dds`, `CloudsTile.dds` at −wind, `DensityOffset = 1.4c − 0.8`, multiplier 3,
   `Darkness = pow(clamp(c − 0.5, 0, 1), 0.3)`, colour `zenithLight + sunColour.rgb (1 − 0.1 D)` with `zenithLight =
   max(getColorAt(+Z) · skyMult, (0.001, 0.001, 0.0015)) · sunColour.g`, the horizon band and alpha rule, output × sqrt(1.4),
   alpha-blended in HDR. Drift: an accumulated offset += wind velocity × dt, × 0.00005. Use the same `c` for `horizonClouds`
   (replacing the stand-in). A test pins `DensityOffset`/`Darkness` and the coverage table.
2. **Weather data and scheduler in Meitou.Data** (`World/Weather*.cs`; **done**, see "In Meitou"): read WEATHER / SEASON / BIOME_GROUP / EFFECT with the
   loader rules (fog min := max when wind values match, update time 0 → 1e6, Default fallbacks, appended Default for time
   windows); map zones to regions from `areasmap.tga`; season lengths from shares × `days per year`; the weighted pick, the
   duration roll, strength, the wind model and its interpolation, all on a seedable random source (the game's own `rand()` order
   cannot be reproduced and does not need to be). Expose, for a position and a game time: the camera region's weather, its
   strength, wind, the 30 s sky transition state and the four-region fog blend with the 2.5 % fade. Tests: season lengths for
   Desert and Skinner's Roam, the fog-distance rule for every base weather, weighted-pick frequencies, wind bounds.
3. **Hook the viewer to it**: **done** 2026-10-08, see "In the viewer" below. `--weather` keeps forcing one record (through
   `WeatherWorld.ForceWeather`, the same code path); the default is the scheduler (`--weather auto`), from the camera position, `--day`
   and the viewer's time of day, feeding `sky color mult`, `c`, the cloud drift, the weather fog, the heat haze and the particle
   effect list.
4. **Wetness, dust and rain ripples** (shader-only; **done**, see "In Meitou"): add `makeWet` with the per-surface absorbance (terrain layers' `absorbance`
   fields, objects from gloss and metalness, foliage 0.9) and the wetness ramp (+0.01/s, −0.005/s); the dust term on DUST objects
   with the biome `ground colour` and the noise texture; the water's three ripple layers from `rainAmount = saturate(rain / 50)`.
5. **Particle effects** (the big one): a reader for ParticleUniverse `.pu` scripts (systems, techniques, Box / Circle / Point
   emitters, billboard renderer types, Colour / Scale / TextureRotator / LinearForce / Vortex affectors, `dyn_random` /
   `dyn_curved_*` attributes) and a CPU billboard renderer; then the effect groups by `type`: camera box with wrapping for rain
   and ash first (most visible), then the map-feature placers (volcano plumes, steamers, the permanent dust storm: static
   positions, always on), then wandering storms and twisters with their fog-volume spheres, lightning last. **Done (part one):** the
   reader, the CPU simulation, the billboard pass and the camera groups for rain and ash ([particle-universe.md](particle-universe.md),
   `--weather Heavy_Rain`, `--weather Kenshi_Ash-Flakes`, `--no-particles`); the rest of this step is open (point, wandering and global
   groups, the placers, fog volumes, lightning). **Done (part two, 2026-10-08):** the point, wandering, global and global-point groups, the
   placers, fog volumes and lightning, all as "The groups in detail" above; the simulation runs on a background task (one unit per pool
   thread) while the frame is recorded; the particles add their coverage to the upscalers' reactive mask ([renderer-native.md](../renderer-native.md) 8.20).
   Still open: the scheduler's wind and strength (the forced weather's wind max along +x is used), `ground colour` per biome, the point light of
   a lightning bolt, particles beyond the near depth slice (nothing is drawn past about 20400 units).
6. **Heat haze**: done in the post-processing (`PostProcess.RunHeatHaze`, `Meitou.Data.World.HeatHaze`); it takes the forced
   weather at strength 1 until step 3 feeds it `WeatherState.HeatHaze` (step 2 computes it): **done** with step 3. **Not planned here**: sounds, gameplay
   effects.

## Unknowns

- The areas map's own origin and size fields in the terrain object (the placement itself is **Verified**, see "Where"); whether a
  cell reads its centre pixel or another of its 16 (they differ on 1045 border blocks).
- The season order for equal order values; the new-game start of the first season; the special case for a season with one
  weather in the SEASON loader (a ceiling of a scaled value, not decoded).
- `sunlight color` of SEASON and `sky colour multiplier` / `colour multiplier` of EFFECT: loaded, their use not traced.
- How effect groups use `maximum view distance`, `sky colour multiplier` and the strength (the wind fields and the emission-rate scaling are used as
  [particle-universe.md](particle-universe.md#the-camera-effects-cameraeffectgroup) says, **Observed**).
- `ground movement` of EFFECT_FOG_VOLUME (read by the game into the volume's holder; its use was not traced).
- Whether the game's ash is denser than the data says (see "Density"); the plugin's default quota; the lights of effects.
- The wandering units' exact walk and turn rules, and where the game places lightning (it snaps to metal objects).
- The full gameplay effect of each `WeatherAffecting` value.
