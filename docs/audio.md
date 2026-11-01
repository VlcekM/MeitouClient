# Audio

How Kenshi drives its sound, and how Meitou plays it. The bank and WEM formats are in [formats/wwise-bnk.md](formats/wwise-bnk.md); this
page is the runtime: what the game sends to Wwise, and Meitou's player (`src/Meitou.Audio`). Only the ambience (biome beds, weather beds) and the music
play from the games' own rules (in the viewer and the game); the player also does pause ducking, game objects with 3D positions, attenuation and reverb,
and the game's settings menu has the audio sliders. The game also plays the interior ambience and the building emitters (campfires, torches, water tanks). The characters (footsteps, combat, voices) and the interface sound too, as do the sky beams (effect emitters), the robot spiders and the movement of manned turrets.

The facts about the exe come from its decompiled camera update and audio manager, traced 2026-10-10 (functions named per claim). Facts
about the banks are checked against the 24 banks of the Steam install.

## The pieces, simply

- **Banks** hold the recordings (Wwise Vorbis WEMs) and the instructions (events, containers, curves). See the format doc.
- **The game** only posts **event names** (`Enter_Desert_Day`), sets **game parameters** (numbers like `Camera_Zoom`) and **states**
  (`Time_in_Game` = `Day`); Wwise does the rest from the bank.
- **Meitou** reads the banks, converts the recordings to Ogg Vorbis and decodes them as they play (NVorbis), interprets the instructions
  in its own player, mixes everything in software to one stereo stream and hands it to the sound card through OpenAL Soft.

## What the game sends (Verified (decompiled) unless marked)

The ambience is run each frame from the camera update (strategy camera `FUN_1406b0f90`, free camera `FUN_1406b0960`, both ending in
`FUN_1406b0140`); the audio manager is the global `AudioSystemGlobal` object. The camera has a **pivot** node and an **eye** node (a child
of the pivot at the zoom distance).

### Biome ambience

- The biome is the BIOME_GROUP (weather region, `areasmap.tga`) **under the pivot**; its `ambience` field (enum `Ambience`: Ashlands,
  Boneyard, Canyons, Coast, Desert, Forest, Jungle, Plains, Swamplands, Tarsands; default Desert) names the bank `SFX_Amb_<name>`. Its
  `music` field (enum `BiomeMusic`: Dunes, Savanah, Swamp, Tarsands, None) picks the music in the same step. Base game: 74 regions; Plains
  15, Desert 14, Canyons 12, Coast 10, Boneyard 7, Tarsands 5, Forest 4, Swamplands 3, Ashlands 2, Jungle 2 (Verified, records).
- **Day** is `sunrise < hour < sunset` strictly (GLOBAL CONSTANTS, 5 and 23; `FUN_14066cb20`), otherwise night.
- Each frame the game compares one number, the biome index with bit 8 set at night, with the last (−1 at start). No delay, no hysteresis.
  On a change: if the biome changed (not only the time), `Leave_<OldBiome>`; then `Enter_<Biome>_Day` or `Enter_<Biome>_Night`. So day to
  night inside a biome re-posts `Enter_` with the other suffix and no `Leave_`; the night event's actions stop the day beds (10 s fade)
  and start the night ones.
- When the biome's bank is not loaded yet, the game loads it first and posts `Enter_` afterwards. **Bug in the game**: on that delayed path
  the suffix is inverted (night posts `_Day`), until the next change. Banks of biomes left behind are unloaded 60 s after they were last
  current (`FUN_140258e30`).
- Events go to one game object (100); the listener is the camera **eye** ([listener](#game-objects-3d-and-attenuation): the orientation is broken in the game).
- The banks loaded at start are listed under [Music](#music).

### Music

Traced 2026-10-17 from the audio manager (`AudioSystemGlobal`: biome music `FUN_1402587a0`, bank pass `vfunc_2` `140259130`, tick
`vfunc_4` `140258e30`, init `FUN_140259620`). Event and node ids checked by hashing the names against the banks' events.

- **Which music.** In the same camera step as the ambience (only when a region is found under the pivot) the region's `music` index goes
  to `FUN_1402587a0`; an index outside 0..4 counts as 4. The names come from the exe's own table (`FUN_14025e170`): 0 `Dunes`, 1
  `Savannah`, 2 `Swamplands`, 3 `Tarsands`, 4 = no music (the FCS labels `Savanah` / `Swamp` play no part). Day and night do not matter.
- **On a change** (nothing happens if the index is the same): if the old index was −1 (the first call after start), `MT_Music_Bank` is
  **unloaded**. For 0..3 with the bank `<Name>_Music_Bank` loaded: switch `Biomes` = `<Name>` on object 100, and the id of
  `Music_<Name>_Play` is remembered. Bank not loaded: it is queued for loading, and when it is loaded the switch is set and the id
  remembered. Index 4: the id is cleared. **Nothing is posted on a change**: no `_Play`, and the game never posts `Music_<Name>_Stop` (the
  exe only ever builds `Music_` + name + `_Play`; the four `_Stop` events in the banks are unused).
- **Only a timer starts music** (`vfunc_4`, every frame, skipped entirely while the title screen exists, `DAT_142133320`): when the clock
  passes the deadline, the next deadline is now + F × (0.8 + 0.6 × u), u = `rand()` / 32768 in [0, 1), F = `Music frequency` × 60 s;
  then, if an id is remembered **and** `Music volume` > 0, it posts `Music_<Name>_Play` on object 100. The deadline moves on even when
  nothing is posted (no music biome, volume 0, bank still loading), and nothing checks whether music is still playing, so `_Play` is posted
  again every 0.8-1.4 F. The first deadline is audio init + F (not randomised). Changing the setting moves the deadline earlier if
  now + new F is sooner (`FUN_140258200`), never later.
- **The clock** is a wall-clock stopwatch (`FUN_14025b3b0` on `DAT_1421349b0`, QueryPerformanceCounter, never stopped by anything), so game
  speed, pause, the title screen and loading do not slow it. A deadline that passes during the title screen fires on the first tick after
  it, usually before the camera has picked a biome, so that interval is spent silent: nothing plays music at once when a game starts.
- **`Music frequency`** (settings.cfg, minutes, default 5, slider "Music Frequency (minutes)" 1..20; `FUN_1403e84f0`, `FUN_1403f0260`): at
  the default, music starts every 4-7 real minutes. `Music volume` (default 0.5) → `User_Volume_Music` × 100 (`FUN_140257d80`), at init and
  when the options close (`FUN_1403e7290`).
- **Banks.** At start (bank pass after `Init.bnk`): `MT_Music_Bank`, `SFX_General_Bank`, `SFX_Amb_Weather_Bank`, `SFX_Amb_Indoors_Bank`,
  `VO_Bank`, then `Boot_Up_Game` (`7D68E3F0`, plays the main theme `3A626E51`) on object 100. Biome music banks load on demand, in the
  audio manager's background bank pass (a ThreadWannabe slot; Observed). A biome music bank is unloaded 60 s after it was last current
  (kept alive only while its `_Play` id is the remembered one, and only when no load is pending), which cuts whatever of it still plays
  (Observed: Wwise stops a bank's content on unload). `Radio_Music_Bank`, `SFX_Bank` and the reverb banks are never loaded.
- **Main theme.** Plays from `Boot_Up_Game` on the title screen and is never restarted: the audio manager is built once (`FUN_140259d10`
  from `FUN_14086a960`) and the title screen only by the start-up loading loop (`Main_LoadingStateLoop` `14082b370`); whether a return to
  the menu exists is Unknown. Stopped by `Create_or_Load_Game` (`6455137B`, in `MT_Music_Bank` and `SFX_General_Bank`: a click sound plus
  stop `3A626E51` with a 5 s fade, **object scope** (Stop/3)) posted on the **GUI object 110** when a new game, load or import starts (`FUN_140917400`,
  `FUN_14047b210`, `FUN_14047b2f0`). Because that stop is scoped to the posting object and the theme plays on object 100, **it does not stop the theme** (Verified by the
  action scope numbering, [wwise-bnk.md](formats/wwise-bnk.md#action-3)); the theme ends with the first ambience `Enter_` event (Stop/2, global scope) or the bank unload above.
- **What `_Play` does** (Observed, banks): plays the biome's three layers directly, no `Biomes` switch involved; what they are, how long
  they last and how the day/night switch behaves: [wwise-bnk.md Music](formats/wwise-bnk.md#music).
- **Pause and speed.** `Pause_Game` / `Resume_Game` (`FUN_140787d40`, on object 100, when paused or speed 0 flips) duck the SFX bus
  `59927CD0` to −96 dB and the ambience bus `95836E4C` by −9 dB (1 s), and reset both (3 s); the music bus `BA70EAC8` (child of the master
  bus, −4 dB) is not touched, so music plays on through pause. All four actions use fade curve 4 (linear; `SFX_General_Bank`, dumped with
  `meitou-tools audio`). Volume fades run straight in linear gain, not in dB (Wwise's transitions on dB properties; **Observed** from the
  Wwise runtime's documented behaviour, not checked in the exe): half way through the 3 s reset the SFX bus is at −6 dB, where a dB ramp
  would still be at −48 dB and leave hits near-silent for seconds after every unpause. Game speed sets the state `Game_Speed` (`C2E2475B`) to `Normal` / `x2` /
  `x4` at speeds 1 / 2 / 5 (`FUN_140787e40`, `FUN_140787fb0`); only `SFX_Bank` and `SFX_General_Bank` use it, so speed does not affect music.
- **Not in the game:** combat or danger music, stingers, intensity parameters, indoor rules for music, focus-loss muting
  (`MuteBackgroundMusic` is never called): no such strings or calls in the exe. **Radio:** `Radio_On` (`D272B88E`: stops the biome music
  root `00DBE1E6` over 10 s, plays a click and the radio playlist `34220ED8`), `Radio_Off` (`D0987A94`) and `FCC71927` (name Unknown, stops
  both) are in `Radio_Music_Bank` and `SFX_General_Bank`, but the exe has no "Radio" string and never loads the bank: unused content.

### Game parameters and states

| Name | Id | Value | Default (Init.bnk) |
|---|---|---|---|
| `Camera_Zoom` | `5FE07BCE` | (eye height − ground height under the eye) / 2000 × 100, each frame; not clamped; not set where there is no ground | 50 |
| `Time_in_Game` (parameter) | `5DBE7335` | **not the clock**: (real seconds mod 600) / 60, a 0..10 sawtooth every 10 real minutes, each frame | 5 |
| `Time_in_Game` (state group) | `5DBE7335` | `Day` / `Night`, set when day flips (`FUN_14066ff60`; seeded opposite so it fires once at start) | — (1000 ms transitions) |
| `User_Volume_Ambient_SFX`, `_Music`, `_SFX`, `_VO`, `_UI`, `Footstep_Volume` | | the settings.cfg sliders (`Ambient volume` …) × 100, at start and when the options close | 50 (Music, SFX, VO 100) |
| `Building_Integrity` | `00C01EF9` | the building's `interior integrity` (0..100) on entering it | 50 |
| `Intensity` | `933E38F4` | per emitter: input × scale + offset; the weather sets wind intensity × wind speed × 0.01 | 50 |
| `Rain_Intensity` | `9637D55D` | the rain level 0..100 ([weather.md](formats/weather.md)) — **not a game parameter in any bank**, so it does nothing | — |

`Camera_Zoom` is the parameter every ambience and weather bank uses: the beds' layers and the one-shot containers have volume curves on
it, so the critters fade out as the camera rises (0 at the ground, 100 at 2000 units up).

### Interiors

Traced 2026-10-17 from the last part of the camera update (`FUN_1406b0140`, with the ambience); **Verified** unless marked.

- **What is tested.** Each frame a physics ray from (pivot x, 40000, pivot z) straight down, the closest hit among collision groups 2 and 13 (mask `0x2004`). Group 13 is the building's `interior mask`
  collision, the indoor trigger hull ([collision.md](formats/collision.md#collision-groups-observed-fun_14055f6e0-and-the-loaders)); group 2 is paired with 9 or 14 in the game's ground-height rays
  (masks `0x204`, `0x4004`), so it is the terrain (**Observed**). So *indoors* means the pivot's X/Z lies inside an interior-mask hull whatever the height, and the highest hull wins. The test does not
  look at the selected character, a roof or the interior view; the pivot is the camera's target, as for the biome. The update is skipped when the ray helper reports failure (no physics scene; **Observed**).
- **When.** The hit object's handle is compared with last frame's (null: nothing hit, or no building). On a change: `Leave_Building` if the previous one was a building (even one without an ambience), then,
  if the new building's `interior ambience` (enum `InteriorSound`: 1 Metal, 2 Stone, 3 Wicker, 4 Wood; 0 None starts nothing) is not None: switch `Indoors` = its name, `Building_Integrity` = its
  `interior integrity`, `Enter_Building`, or `Enter_Building_Big` when `cavernous`; all on object 100. The events also flip the reverbs ([below](#reverb-and-aux-buses)).
- **Data** (base game records, Verified). 603 BUILDINGs; 58 have an interior-mask hull, 38 of those an ambience (Metal 6, Stone 20, Wicker 4, Wood 8) and 20 none; 25 more have an ambience but no hull and never
  sound. Integrity is 100 for most (50 and 70 for some, 0 and 1 for ruins); `cavernous` is set on 17 Metal and 2 Stone buildings. 13 gateways have a hull, none an ambience.
- **Meitou** (`InteriorSounds`; `GameHost.AudioWorld.cs`). `WorldObjectRenderer.InteriorUnder(x, z)` finds the loaded building whose hull footprint (`InteriorVolume.ContainsGround`) holds the pivot, highest first; the
  volume carries `Ambience`, `Integrity` and `Cavernous` from its record. Differences: gateways and destroyed buildings have no volume (the exe's hull would still be hit), and the hull is the convex footprint of the
  mask's collision, and a hull whose top lies below the terrain at the pivot's X/Z still counts (the exe's ray hits the terrain first; `ContainsGround` ignores terrain). Rendered (`audio render … Indoors:<name> Building_Integrity=100 Enter_Building`, `Camera_Zoom` 50): about −84 (Wood), −78 (Metal), −84 (Stone), −87 (Wicker) dBFS; integrity 0 is 4 dB louder than 100
  (**Observed**; it fades in over 1 s and `Leave_Building` ends it in 1 to 2 s).

### Point emitters

Traced 2026-10-17: the emitter class (`FUN_140259a80` builds it from an AMBIENT_SOUND record, `FUN_140258410` registers, `FUN_140258cb0` updates, `FUN_1402585c0` / `FUN_1402581a0` start and stop, `FUN_140258c20` sets
power, `FUN_140258260` destroys), the building set-up `FUN_14055c240`, its loader `FUN_14029e0c0` and the production update `ProductionBuilding::vfunc_28`. **Verified** unless marked.

- **The record.** AMBIENT_SOUND (27 in the base game): `emitter` (the `Emitters` switch value; 25 distinct names, all cases of the `Start_Emitter` container, [wwise-bnk.md](formats/wwise-bnk.md)),
  `chance` 0..1, `cutoff` (50; `Sun_Beams` 300), `intensity` 0..1, `efficiency multiplier` 0..1, `needs power`. Users: BUILDING `sounds` (about 50 buildings; each reference's three values are an offset),
  EFFECT `sound` (SkyBeam, venge beam, beam1: `Sun_Beams`), RACE `ambient sound` (the two robot spiders: `Iron_Spider`, on the character's own object), FOLIAGE_MESH and MAP_FEATURES `sounds` (none in the
  base game; the exe walks all their entries, each rolled by its `chance`).
- **Range.** Every frame the squared distance from the camera **eye** (the node the listener uses) to the emitter is compared with `cutoff² × 100`, i.e. a range of cutoff × 10 units (500; 3000 for `Sun_Beams`):
  strictly beyond it stops the emitter, otherwise it starts if it is not playing and is allowed (below). Buildings run this from their per-frame update (**Observed**). There is **no cap** on the number playing: the range and Wwise's 256 voices are the only limits.
- **Start and stop.** The first start registers a game object of the emitter's own (counter from `0xFFFFF`; a building's emitter uses the building's object); then `SetSwitch("Emitters", name)`, `SetPosition`
  (zero orientation), `SetRTPCValue("Intensity")` only when `intensity × 100` is not 0, and `Start_Emitter` (Play/3, 500 ms fade); a stop is `Stop_Emitter` (Stop/3, 1 s). Destroying an emitter posts the stop and then
  stops everything on its object and unregisters it. With a base intensity of 0 the parameter is never sent for the object, so Wwise reads the **global** `Intensity` (the wind's, set by the weather) until a machine sets
  its own (**Observed**, from the code paths).
- **Power.** An emitter starts unpowered. One that has `needs power` waits for `FUN_140258c20(efficiency, powered)`; it also sets `Intensity` = efficiency × `efficiency multiplier` × 100 + `intensity` × 100 when the value changed,
  for an emitter in range. An emitter without `needs power` starts by distance alone. Only two places call it (**Verified**, the callers of `FUN_140258c20`; nothing else starts or stops an emitter but the distance update and the teardown):
  - **Each frame**, `ProductionBuilding::vfunc_28` (the class of mines, wells, refineries, generators, batteries, wind generators, rain collectors and furnaces; skipped while the game is paused, `DAT_1421349c9` = GameWorld `+0x8b9`, [game-loop](game/game-loop.md#speed-and-pause)):
    efficiency = the building's slot 110, powered = `FUN_140299370` (has power) and efficiency above 0.
  - **Once**, `FarmBuilding::vfunc_72` (finishing construction, and `Zones_BuildBuilding` for a building with nothing left to build): powered, efficiency = slot 108, only if the emitter exists and the building is not yet marked complete.
    `FarmBuilding`, `CraftingBuilding` (the Medical Workbenches, Robotics and Skeleton Limb benches) and `ResearchBuilding` override `vfunc_28` without the call, as do other classes (the Peeler Machine), so their emitters are never told again.
    At that moment nobody works them (efficiency 0), so the `Intensity` stays the base (0 is never sent, so the object hears the global wind `Intensity`) and the emitter plays whether or not the bench is used.
    **Observed**: the order of `read_building_n3` (makes the emitter, only for a complete state) and this call is not traced for a building the player finishes; if the emitter comes second it is never powered.
- **Efficiency** (the chain **Verified**, its meaning **Observed**): `FarmBuilding::vfunc_110` calls slot 108 for every class except `WindGeneratorBuilding`, which returns its turbine's animation value (`FUN_1404cbd90`, units **Unknown**).
  Slot 108 = operators ÷ raw `max operators` (1 when that is 0) × slot 109 (health × granted ÷ demand of slot 163, 1 with no demand) × slot 119 (the yield `FarmBuilding::vfunc_92` × the BUILDING's raw `output rate` (`+0x1a8`, not the
  `+0x1a4` one that includes `production mult`) × the research production multiplier). Nothing caps it (a drill with `output rate` 1.4 gives 140), so Wwise's curve limits it.
  A generator's demand is 0, so its efficiency is health × `output rate` whatever its fuel or switch. A battery's demand is its rate while its charge is below capacity (efficiency = granted ÷ rate, negative while it drains), else 0.
- **Has power** (`FUN_140299370`, **Verified** from the decompile): true for a pure generator (output above 0, no capacity) whatever its fuel or `power on` (**a generator with no fuel or switched off still hums**); true for a machine
  drawing nothing (output plus research increase 0: wells, looms, silos, furnaces); else false when `power on` is off; else true if the town's generator output (`Town::vfunc_87`'s `+0x474`) is above 0, or the machine
  `gets battery`, the town is in battery mode (`+0x470`) and its batteries' output (`+0x480`) is above 0; no town: true. It is town-wide, not the machine's own grant (a starved machine is powered, with a low efficiency).
- **Data** (probe of the base game, Newwworld and rebirth). Needs-power records with `intensity` / `efficiency multiplier`: `Auto_Mine`, `Electronics_Bench`, `Leather_Tanning`, `Loom`, `Medical_Bench`, `Research_bench`, `Robotics_Bench`,
  `Rum_Still`, `Water_Well`, `Windmill_Large`, `Windmill_Small` 0 / 1; `Stone_Processor` 0 / 0.75; `Generator`, `BioFuel`, `Furnace`, `Silo_Grind` 0 / 0 (they never send `Intensity`: the global one applies); `Battery_Bank` and `Light_Hum` 1 / 0
  (constant 100). `Leather_Tanning` and `Light_Hum` are not the first `sounds` entry of any BUILDING.
- **Buildings.** The loader (`FUN_14029e0c0`) flags a building whose `sounds` list is not empty; when the building is built into the world (`Zones_BuildBuilding`, and again at completion) the flag registers its
  Wwise object (also used by door sounds) and makes **one** emitter from the **first** `sounds` entry only (a Peeler Machine lists Generator and Grind; only Generator sounds), if the building is complete (the first
  byte of its state struct is `is complete`) and `chance` > `rand()` (rolled once). Position = the building's position + its orientation × (the entry's values × the building's `scale`) (the Wind Generator's (0, 240, 0)
  lifts it onto the rotor). Torn down with the building (`FUN_140549920`).
- **Effects** (`EffectHandler::vfunc_1` = `uses_sound` `140102870`, `vfunc_2` `140100a50`, `FUN_140258120`, `EffectHandlerWandering::vfunc_1`). The handlers that make one are the base handler's (point, wandering, global-point effects; the camera and global
  handlers override the update and make none). On its first update after being activated a handler creates its particle system and, from the **first** `sound` entry of its EFFECT, an emitter at its position: no chance roll, no game object of a building
  (an object of its own from the counter, registered at the first start), `needs power` and `efficiency multiplier` forced to 0, base `intensity` kept. Every frame `vfunc_2` copies the handler's position into the emitter (and calls `SetPosition` once registered),
  then runs the range test above, so a wandering beam's sound moves with it. The emitter ends (`Stop_Emitter`, `StopAll`, unregister) with the handler's particle system: after the handler's life is over, its fade-out has run (`FUN_1400f8d10` at 1 or more) and no particle
  of the system is left alive. Base data: EFFECT `SkyBeam` and `venge beam` (type 9, WANDERING_GAS) and `beam1` (type 3, WANDERING) have `Sun_Beams` (AMBIENT_SOUND `Inferno`: cutoff 300, so range 3000; `intensity` 1, so `Intensity` 100 is sent); no other EFFECT has a `sound`.
- **Races** (`uses_ambient_sound` `140629940`, `FUN_1405cfd90`). A character's emitter is made from the **first** `ambient sound` of its RACE on the character's own Wwise object (shared: the emitter registers and unregisters nothing), for a living character only (the corpse
  flag `+0x5bc` clear); every call of `uses_ambient_sound` destroys the old emitter and makes a new one. Who calls it is **Unknown** (no caller or vtable entry in the dump). `FUN_1405cfd90` (the character's frame update) copies the character's position into the emitter (no
  `SetPosition`: the character's object is placed right after) and runs the range test. Base data: the two robot spiders (`Robot Spider Worker`, `Robot Guard Spider`) have `Iron_Spider` (cutoff 50, range 500; `intensity` 0, so the object hears the global `Intensity`).
  A corpse gets a sound too (**Observed**, `CharacterHuman::vfunc_29` `1405ccd90`): AMBIENT_SOUND `Corpse Flies` (`95425-sfx.mod`, emitter `Corpse_Flies`, cutoff 50, `intensity` 1) and the `Fly-Particle` effect (`55393-Newwworld.mod`), made once the body has lain more than 4 of the game's time units
  (`FUN_14066cc00`, hours) in an active zone and removed with the effect; **not done in Meitou**.
- **Meitou** (`WorldEmitters`, `BuildingEmitters`, `WorldObjects.Emitters`; `GameHost.AudioWorld.cs`). Every emitter gets its own object (ids from 2³²) and the same rules; the roll is a stable hash of the placement (the exe
  uses `rand()`); the level's buildings with a destroyed or incomplete state get none, a player-built building gets its emitter when its site is complete. The level's emitters are read once for the whole world
  (a worker task, about 0.2 s) and culled by distance, where the exe makes and tears them down with the zone. They also cover the furniture of each building's interior and exterior layout (`WorldObjects.Emitters`
  mirrors the placement of `Lay`; **Observed**: the exe's furniture path was not traced, layout objects are ordinary BUILDING records with `sounds`). The base level then has 1,252 emitters: 534 from buildings and
  718 from layout furniture. 585 play by distance: 333 `Fire` (campfires, torch posts, streetlights), 157 `Oven` (bread ovens, cooking stoves) and 95 `Water_Tank`. 667 need power (265 `Generator`, 96 `Windmill_Large`,
  94 `Battery_Bank`, 44 `Water_Well`, 36 `Electronics_Bench`, 32 `Research_bench`, 28 `Windmill_Small`, 17 `Auto_Mine`, 14 `Medical_Bench`, 14 `Stone_Processor`, 12 `BioFuel`, 8 `Rum_Still`, 4 `Silo_Grind`, 3 `Furnace`)
  and are driven as [below](#machine-sounds-meitou). Rendered: `Fire` at `Intensity` 10 is −58, −66, −81 dBFS and silent at 50, 200, 400 and 600 units from the listener
  (**Observed**); in the game, with the sound eye moved 84 units from two campfires both started and at 904 units both stopped (a temporary log line).
  **Effects** (`EffectRecord.Sound`, `GameHost.AudioEffects.cs`): each live effect unit of a group whose EFFECT has a `sound` (the weather's and the placers' groups) gets an emitter `effect/N` at the unit's position, moved to it each frame (`WorldEmitters.Move`) and
  removed when the unit is done. The sky beams (`beam1` in the weather, `SkyBeam`, `venge beam`) therefore play `Sun_Beams` within 3000 units. **Robot spiders** (`CharacterSounds`, `CharacterSoundProfile.Ambient`): the profile's race `ambient sound` starts when a
  living character is within its range of the eye (the character's own object, the current position) and stops when it is out of range or down; the call frequency of `uses_ambient_sound` being Unknown, the emitter is simply kept as long as the rule holds. Corpse flies: an emitter per corpse with flies (`GameHost.CorpseFlies`). Not done: FOLIAGE_MESH and MAP_FEATURES emitters.

#### Machine sounds (Meitou)

`ProductionSystem` derives, for each machine of the player's buildings, `Efficiency` and `HasPower` every tick (display only, not saved, not hashed); `MachineSnapshot.Sound` is what the host passes to `WorldEmitters.SetPower`
each frame (`GameHost.UpdateEmitters`, key = the building's instance id). **Engine choices** are marked.

- **Per-frame classes** (`MachineData.PowersEmitterEachFrame`: function 1 without a farm, 4, 5, 14, 23, 26, 27): `Sound` = (efficiency, has power and efficiency above 0), computed as the exe's chain above from the sim's values: operators
  ÷ `PowerOperators` (the raw +0x3ac, like its power demand; an automatic machine, 0, counts as 1 and works unattended,
  [production](game/buildings-production.md#production)), × the consumer's granted share (a battery's as above), × `Machine.Yield` × the raw `output rate` (`MachineData.OutputRate`) × the research production multiplier. Health is 1.
  Has power follows `FUN_140299370` with the sim's town grid (generators' output above 0, or batteries while the town is in battery mode) and `power on`. A wind generator's efficiency is its blades' wind share (**Verified**, `WindGeneratorBuilding::getSoundIntensity`;
  [rotating parts](game/buildings-production.md#rotating-parts)). Result: a Generator hums whenever built; a well, mine or still sounds while worked and powered, at `Intensity` 100 × the factors where its record has an `efficiency multiplier` (`Stone_Processor` 0.75 ×).
  A battery bank (constant `Intensity` 100) plays while it is full or charging, not while it drains or is empty.
- **Once-powered classes** (benches, research, farms, others): `Sound` = (0, powered), the exe's completion call: they play from the moment the machine exists, at the base `Intensity` (global when 0). **Engine choice**: the order for
  a building the player finishes is Observed only (above), the sound is given to it as for the level's.
- **The level's buildings** and layout furniture have no machine (the simulation runs only the player's buildings), so the host decides once when it adds the emitter (`GameHost.LevelPowered`, **engine choice**; the exe runs their
  classes): needs-power emitters of once-powered classes play as above; generators, wind generators (efficiency their wind share, each frame), battery banks (the town is assumed to produce power) and furnace-like machines needing no operators
  play; machines that need operators or town power (wells, mines, processors, stills, looms, silos, distillers, the Electrical Workbench) stay silent, since the NPC workers and town grids are not simulated. Level furniture
  that is a bench or generator follows the same rule through its own BUILDING record.
- **Power switch.** The exe's `FarmBuilding::vfunc_161` (the machine panel's power button) flips `power on`, then sets the `Generator` switch to `On` / `Off` on the GUI object 110 and posts `UI_Generator`
  (**Verified**). Meitou has `Machine.PowerOn` (saved, default on) but its machine panel (`PlayerInterface.Production.cs`) has no power button and no command sets it, so `UI_Generator` has no trigger.

### Other world-side events

From the same trace (**Verified**; hashes in [wwise-bnk.md](formats/wwise-bnk.md)). **Doors** register a Wwise object at construction (the door class sets the flag that buildings with `sounds` get) and set `Door_Type` from `door type`
(DoorMaterial 0 Wood, 1 `Mechanical_Metal`, 2 `Gate`). On the door's object: `Door_Open` when it starts to open (state 0 to 2), `Door_Close` when it starts to close (1 to 3), `Door_Shut` when closing ends (3 to 0) **and**
when opening ends (2 to 1), and `Break` for doors and walls whose integrity falls to 0 (`DoorStuff::vfunc_62`, `WallBuilding::vfunc_62`; see **Break** below). Turrets post `Turret_Mvt_Start` / `Turret_Mvt_Stop` (`TurretBuilding::vfunc_28`) and the
gun's shot sounds on the building's object (see **Turrets**). `UI_Generator` (`FarmBuilding::vfunc_161`), the `Building_*` build-mode sounds and `Building_Click` (select sound) belong to the interface.
**Meitou:** door sounds as above (`Meitou.Audio.DoorSounds`, fed each frame by `GameHost.UpdateDoorSounds` from the snapshot's doors, which carry each door's
position, record and broken flag). A door object takes the leaf's centre, else its building's origin. A door missing from the snapshot is closed, since the
snapshot leaves out plainly closed doors. A door seen for the first time is assumed to have been closed (if it is opening) or open (if it is closing); one at rest is taken as it is, so a load is quiet. Doors over 3000 units from the eye are skipped
(**engine choice**; the exe posts for every door). The `--quit-after` log counts the door sounds.

**Break** (**Verified**, `DoorStuff::vfunc_62` `14029d690`, `WallBuilding::vfunc_62` `14029d560`; the damage entry of the building, called through the vtable). Given a hit's two floats (`p0`, `p1`; `p1` is the heavier term): a door that is not already broken takes
`clamp(2·p1 + 0.5·p0, 20, 60)` and loses 0.015 × that of its integrity (wall: half the sum, clamped 10 to 60, 0.003 ×); when the integrity is 0 or less it is marked broken (virtual `+0x310`/`+0x340`) and `Break` posts on the building's object. **Neither
function sets the `Break` switch group** (cases `Door` and `Gate`, bank `SFX_General_Bank`): no other code in the exe sets it (**Verified**, no `SetSwitch` string `Break`), so with the group never set the original plays the container's default, which
is silent (**Observed**, rendered). **Meitou:** `DoorSounds` posts `Break` when a door becomes broken (the lock bash `LockSystem.Bash` and a lockpick's break set `DoorBroken`), after setting `Break` to `Gate` for door type 2 and `Door` otherwise
(**engine choice**: it fixes the exe's omission; Fixes settings page, `Door break sound`, off plays as the exe). Neither break posts `Character_Unlock_Success`. Walls: a wall line hit to 0
(`CombatSystem.ApplyZoneBuildings`) emits `SoundKind.WallBroken`; the host posts `Break` on a pooled object at the wall's position (`CharacterSounds.WallBreak`; the exe uses the
building's object), with `Break` = `Gate` under the same setting (**engine choice**; off: unset, silent as the exe).

**Turrets** (`TurretBuilding::vfunc_28` `140434b70`, `RotatingEnt::vfunc_2`). **Verified:** each frame of an active turret with a target the largest angle its turning parts still have to turn (`FUN_1404cc0d0`, degrees) is kept; with the counter at 0 and that angle over 0.1 it posts `Turret_Mvt_Start` and sets
the counter to 8; the counter counts down every frame and posts `Turret_Mvt_Stop` on reaching 0 (so a turret that keeps turning posts a start and a stop every 8 frames; no fade on either). Both post on the building's object (`+0x208`, registered when the gun first asks for it).
The turning parts are the BUILDING_PART records with `rotation function` 5 (ROTATION_TARGET): in order, each takes the aim point in its own frame and turns by its error (yaw part, `rotation axis` Y: `atan2(x, z)`; pitch part: `atan2(-y, √(x²+z²))`) limited to
± the turning speed × frame time (efficiency × `aim speed` × (1 + 0.01 × damage), [combat](game/combat.md#turrets)). A shot posts `Attack` on the building's object with the `Weapons` switch `Turret`. **Unknown:** the
idle behaviour of a manned turret without a target. **Meitou:** the simulation fires turrets and publishes who mans which turret and its aim point (`CharacterSnapshot.Turret`, outside the hash).
`GameHost.AudioTurrets.cs` keeps a display-only copy of each manned turret's parts within 4000 units, turned toward that aim point by the rule above in game time at efficiency 1 (`TurretSounds`, `TurretRig`),
and posts the two events on an object of its own at the turret (a start beyond 3000 units from the eye is dropped; **engine choice**). The gun mesh turns with the simulation's angles ([render-objects](render-objects.md#turrets)). A shot
(`SoundKind.TurretShot`) posts `Attack` with `Weapons` = `Turret` on a pooled object at the muzzle (**engine choice**).
To hear it: a town under attack whose guards man their turrets, or a player character sent to a turret with an enemy in range.

### Weather

`Weather_Rain_Start` / `Weather_Rain_End` when the rain level becomes non-zero / zero; `Weather_Wind_Start` when the wind starts. The game
posts `Weather_Wind_Stop` to stop it, which no bank has (the bank's event is `Weather_Wind_End`), so in the game the wind bed never stops
once started (Observed, by the hashes; not heard in the game).

### Volumes

The six sliders become the `User_Volume_*` game parameters × 100. Each category's **bus** in `Init.bnk` turns its parameter into a gain
through a curve: Ambience (`95836E4C`): silent up to 5, half at 65, full at 100, so the default 0.5 plays at gain 0.375 (−8.5 dB). The
other buses are alike (SFX, UI: half at 65; Music: half at 50 and −4 dB bus volume; VO: linear; footsteps +6 dB bus volume, unity at 50 rising to 1.5× at 100).

### Characters: footsteps, combat, voices

Every character is a Wwise game object (the exe numbers them from 1000 in a per-handle map), repositioned with its facing every frame. Events are posted
through one helper (`FUN_1405c9a70(char, event, maxSqDist)`): **dropped when the character is farther from the camera eye than the limit**, squared 1,000,000
(1000 units) for everything, 40,000 (200 units) for footsteps, 0 (never) for stop events. `Movement_Speed` (units per second) is set only within 1000 units; the ground is looked up only within ~346 units
(squared 120,000). All **Verified** (exe decompile) unless marked.

- **Footsteps and swings are animation data.** An ANIMATION, ANIMAL_ANIMATION or COMBAT_TECHNIQUE record lists ANIMATION_EVENT records in `events` (BUILDING_FUNCTIONALITY in
  `animation events`), each reference's first value the time in per cent. The record has `event` (the Wwise event), `override state` / `override value` (a switch set on the
  object first) and `stop` (posted when the animation ends). Only the highest-weight animation that has events fires them (`FUN_1405b5c30`); the cursor (`FUN_140064690`) fires
  the events between the last and the current fraction, the rest of the pass when the clip wraps, and everything up to the fraction at once when an animation becomes the dominant one.
  An event named exactly `Footsteps` with no override first sets `Armor_Worn` to the body armour's `material type` (Cloth, Leather, Chain, Plate; the heaviest body-slot item, `FUN_1405d28b0`).
- **Surface** (`CharMovement::vfunc_11` calls `FUN_14065df80` while a character moves; `FUN_1405cafc0` sets the switch, `FUN_140a08c70` names it). **Verified** unless marked. GroundType 0..8 names Sand, Grass,
  Concrete, Wood, Metal, Water, Mud, Snow, Dirt; a RACE with `barefoot` and no boots uses `BFoot_Sand`, `BFoot_Grass`, `BFoot_Wood`, `BFoot_Metal`, `BFoot_Dirt` for those (the rest are shared). The name table has no bound:
  a type past 8 reads beyond it (**Unknown**; one record, `Bridgehead`, has `interior material` 9). In order:
  1. Height under 100: `Water`.
  2. The ground ray (`FUN_14065f160`, run for the movement: from 20 units above the character straight down) leaves the hit's collision group and the hit object's handle on the movement. Its masks are `0x7de04` (groups 2,
     9..12, 14, 15..18) when the character is in no building and `0x7f9e00` (groups 9..12, 15..22; no terrain or foliage) when it is in one. The terrain can override a lower hit (**Observed**; gated by a character flag at `+0x1a9` and, in a building, a building property via vfunc `0x330`; not decoded). Group 2 (the terrain,
     **Observed**): the BIOMES record under the point (biome map pixel, five bytes: ground, slope, grass, dirt, road type) picks by the overlay map's channels at that point (A above `0xA0` road, B dirt, G or R grass, else
     ground), only within ~346 units of the camera (squared 120,000).
  3. Any other group: when the hit object is a BUILDING (handle type 0; any other object, such as a foliage one, leaves the switch as it was), the building's `exterior material` (`FUN_14029e0c0` reads both fields into
     the building, `+0x220`) when the character is in no building (its place handle, `CharacterAnimal::vfunc_59`, is the null handle, type `0xb`), else its `interior material` (`+0x21c`). Both are GroundType BUILDING
     fields (fcs.def defaults: interior Wood, exterior Concrete) and belong to the whole building, not to the part hit. No distance limit on this branch.
  Base game, all 603 BUILDINGs set both (interior/exterior): Wood/Concrete 412, Metal/Metal 131, Wood/Wood 21, Mud/Metal 13, Wood/Metal 6, Concrete/Concrete 6, Dirt/Mud 5 (hive), Water/Metal 2, Dirt/Dirt 3, Metal/Concrete 1,
  and three odd ones (Sand inside: `Carpet`, `Weird Crate`; 9 inside: `Bridgehead`). The 58 with an indoor hull: Wood/Concrete 36, Metal/Metal 12, Concrete/Concrete 6, Dirt/Mud 2, Metal/Concrete 1, Wood/Wood 1.
- **Weapons switch** (set on the object when it equips; table indexed by WEAPON `skill category`): Katanas, Sabres, Blunt, Heavy, Hackers, (unarmed: none), Bow, Turret, Blunt. The bank's values are `Katanas`,
  `Sabres`, `Hackers`, `Heavy_Weapons` and so on, so **Observed (names compared, not heard):** the exe's `Blunt`, `Heavy`, `Bow` select nothing and those weapons' swings are silent in the original; Meitou keeps that.
  Animals set `Weapons` to their creature name and `Creature_VO` to it too.
- **Impacts** (`Impact` event on the defender, after setting `Impacts`): a blow that lands: `Light_Hit` or `Heavy_Hit` (heavy = it staggers), `HtH_Impacts` or `HtH_Impacts_Robot` when the attacker fights
  unarmed (robot race); blocked: `Parry` (`HtH_Impacts` for an unarmed non-robot attacker), nothing when the block is a dodge technique (the base game's `Dodge back` rows are flagged
  `is block` and `is dodge`; **Verified**: the outcome test `FUN_140665fa0` checks `is dodge` (+0x2d) first and returns 0, a dodge, before it looks at `is block`, and take-hit `FUN_1404394f0` posts nothing for 0); dodged: nothing; a bolt into a body: `Deflection` after `Armor_Worn` when armour covers the spot, else `Impact`. A bolt in the ground:
  `Impact` on a pooled object at the spot, `Impacts` = the ground's name (Stone for anything unnamed). `Attack` on a shooter.
- **Voices** (`VO_Bank`, loaded explicitly). A person's `VO_Races` = RACE `sounds` (0..3: Human, Shek, Hive, Robot; other: Human) plus `_Male` / `_Female` unless single-gender; an animal's `Creature_VO` = RACE
  `sounds` less 4 indexing Beak, Bull, Cage, Dog, Duck, Gar, Goat, Gorillo, IronSpider, Leviathan, Pack, Spider, Turtle (below 4: Dog). A landed hit: **50 %** (`random & 8`) `VO_Get_Hit` (animals `VO_Creature_Take_Damage`).
  Animals only: `VO_Creature_Die` when they go down, `VO_Creature_Victory` for the attacker, `VO_Creature_Alert` when threat goes from none to some, `VO_Creature_Passive` on a 20 to 60 s timer (the first up to 100 s). People have no
  other voice events in the exe's character code (grunts on swings are inside the attack events' containers).
- **Character tasks** (all through the same helper unless noted; slots 1 / 2 / 3 of a task are start / update / end): `Character_Build_Loop_Start` and `_Stop` (Build 2, Repair 95), `Character_Medic_Start` / `_Stop` (First aid),
  `Character_Unlock` (loop, 1000 units; started by Task_UnlockDoor, Task_PickLock (also for shackles) and Task_CutShackles (also locks) at their start; Task_BruteForceShackles starts none) stopped by `Character_Unlock_Success` (slot 2 when a roll, cut or
  break works; a click that holds the Stop) and `Character_Unlock_Fail` (a bare Stop, slot 3 of Task_PickLock and Task_UnlockDoor, so at every end of the task, success included; Task_CutShackles posts none at its end: what stops the loop of an interrupted cut is **Unknown**;
  all **Verified**: strings, callers and the bank's actions), `Character_Lock` (Task_LockDoor), `Character_Bed` with `Bed` = `Get_In` / `Get_Out`,
  `Character_Pick_Up_Ground` (`Task_Pickup` slot 2, 1000 units, after the `Inventory_Items` switch, which the bank ignores; posted when any of the stack went into the inventory, not when nothing fitted), `Character_Pick_Up_Person` / `Character_Drop_Person` (on the carried person, with its `Armor_Worn`), `Character_Loot` (character target) / `Character_Inspect`,
  `Character_Consume` with `Consume` = `Eat` (200 units) and `Sell_Item` (200 units). `Character_Building_Materials` is posted but **no bank has it** (silent). Involuntary drops (knock-out, ragdoll) are silent.
  `Character_Drop_Ground`, `Taunt` and `Create_Character` are posted on the GUI object ([Interface](#interface)).

Meitou (`CharacterSounds`, `AnimationSoundEvents`, `GroundSurfaces` in `Meitou.Audio`; `GameHost.AudioCharacters.cs`): characters within 1100 units of the eye get an object (dropped past 1400, or when they leave the snapshot);
the host hands each character's place, facing, speed (horizontal distance between the last two snapshots over the tick) and animation layers (`AnimationLayer.Length` and `.Definition`, added to the snapshot) every frame.
Combat facts come from `World.Sounds`, a display-only queue the host sets while audio runs (blocks, hits, shots, bolt hits and sticks, characters going down, fighters taking up a fight); nothing in the simulation reads it, so
it is outside the state hash. Overlay tiles for the ground load on a worker (6 kept).

Building floors in Meitou (`BuildingFloors` in `Meitou.Rendering`, `FloorSurfaces.HighestBelow`, `CharacterSounds.Floor`, `GameHost.AudioFloors.cs`; display only, nothing reaches the simulation). For a moving character within the
surface limit (120,000 squared; the exe has none for buildings, but footsteps sound only within 200 units), asked again after it moved a unit: the placed, undestroyed BUILDINGs of the zones within 400 units (X/Z of the building's
position) are listed on a worker; a building's floor collision (`FloorSurfaces.For`: ordinary and stairs parts, groups 9..12 and 15..18) is made on a worker when a character first comes within 400 units. The highest floor
triangle under the feet and at most 20 above them is the hit, taken when within 4 units below the feet (engine choice: the sim stands on the navigation mesh); the highest across buildings wins. The character is "in a
building" when the renderer's indoor hull test (`InteriorAt`, margin 2) holds, else outdoors. A hit gives the building's `interior material` or `exterior material`; no hit, or the floors not made yet, leaves the terrain overlay
(and a material past 8 changes nothing). Differences from the exe (**Observed**, engine choices): the terrain override above is not implemented (a floor slab at or just below the terrain, within the 4 units, gives the building's material where the exe may say terrain); destroyed buildings are left out (the exe hits their `destroyed collision`); buildings under construction count as finished (the exe shifts their parts to the passable groups until built); furniture (groups 19..22, in the indoor mask) is not
a floor, and terrain showing through holes in an indoor floor falls to the overlay (the exe's indoor ray has no terrain bit); walkable foliage outdoors uses the overlay (the exe leaves the switch unchanged); a building
whose floor lies farther than 400 units from its position is missed at that distance; the first steps near a building not yet loaded use the overlay.

Task sounds in Meitou (`CharacterLoop`, `CharacterSounds.Job` / `.Carried`; `GameHost.AudioCharacters.cs`). The snapshot carries the movement task, whether a lock is being picked and whether the character is in a bed (read-only, outside the hash):
`Task.Build` is the `Character_Build_Loop_Start` / `_Stop` loop, `Task.Treat` the `Character_Medic_*` loop, a lock being picked or shackles being picked or cut (`WorkingShackles`; not while walking to it) the `Character_Unlock` loop (a change of job stops the old loop first, with `Character_Unlock_Fail`, the exe's bare Stop; a character that gets its object mid-job starts it
then; **engine choice**: an interrupted cut is stopped too). Breaking shackles has no loop, as in the exe. One-shots: `Character_Loot` when `Task.Loot` starts; `Character_Bed` (`Bed` = `Get_In` / `Get_Out`) when the bed state changes; `Character_Pick_Up_Person` when a body is lifted and `Character_Drop_Person` when a put-down order sets it down (both on the carried person, with
its `Armor_Worn`; a drop by knock-out or ragdoll is silent, as in the exe); from `World.Sounds`: `Character_Consume` (`Consume` = `Eat`, 200 units) when `FeedSystem` feeds, `Character_Unlock_Success` when a pick works, a character is freed from shackles (picked, cut or broken) or a warden unlocks a door or cage, `Character_Lock` when a
warden locks one, `Character_Pick_Up_Ground` on a character that takes a ground item (all or part of the stack), and, as the sound of `PutShacklesOrder` (the player's stand-in for the exe's drag of shackles onto the target's armour slot, which posts `Item_Drop`
on the GUI object with `Inventory_Items` of the item, `FUN_140715560`), that `Item_Drop` through `UiSounds` when the shackles are on. The exe's AI cuffing (`Task_CuffTarget`: "Restraining" bar, `medic` animation) posts no event (**Verified**: no `PostEvent` caller in it), so a put-on by anyone but the player is silent. The RE found no task sound for dismantling (order 96; only `Task_Build`, orders 2 and 95, loops: **Observed**), so it is silent but for `Thing_Complete` when done. **No trigger in Meitou:** `Character_Inspect` (loot targets
only characters), `Character_Unlock_Fail` as a sound of its own (it is only the loop's Stop; a failed roll posts nothing, as in the exe), `Character_Building_Materials` (no bank has it), `Sell_Item` from NPC shoppers, `Character_Drop_Person` from the corpse machine.
Meitou's wardens have no unlock loop (their rounds only start the click); the player's lock picking loops as the exe's `Task_PickLock`.

**Unarmed `Weapons`** (`FUN_1402b4570`, when a technique is chosen): the switch becomes `HtH_Punch` when the technique's last blow uses limb 0 or 1 (the arms, **Verified**) else `HtH_Kick`, with `_Robot` appended for a robot (**Observed**: the exe also appends it when a limb check on the attacker's race says robotic limbs). Meitou applies it when the animation of a COMBAT_TECHNIQUE valid
for unarmed fighting only (not a block or dodge, not a creature's) starts (**Observed**: the exe's guard on the technique is a flag whose meaning is not traced).

### Interface

Every interface sound is an event on the **GUI object, 110**, posted by the handler of its own widget; the exe has no central click hook (**Verified**: all 142 `PostEvent` callers, handler addresses in the table are
the exe's). Posts are unconditional unless noted. Events that are not in `SFX_General_Bank` (so silent): `Medic`, `Initiate_Conversation`, `gui_notrade`, `gui_noequip`.

| Event (switch) | When |
|---|---|
| `Hover` | the pointer enters one of the seven title buttons (**Observed**: the binder is probably mouse-set-focus) |
| `General_Click` | title and Esc-menu buttons, new-game, load/save, options and message-box buttons, save-list rows, inventory Arrange, Jobs toggle, run-speed arrows, job Remove, a portrait click that selects, character-editor arrows and buttons; Sneak turned **off** |
| `Create_or_Load_Game` | a load or new game starts (music director) |
| `Game_Speed_Normal` / `_x2` / `_x4` (state `Game_Speed`) | the HUD speed buttons (the exe also on F2 to F4); speeds 1, 2 and 5 only |
| `Change_Level` | the floor arrows (the PgUp/PgDn keys are silent) |
| `Stats_Preview` (`Stats_Up` opening, `Stats_Down` closing) | the extended-stats button |
| `Blocks_Only` / `Stealth` (on) / `Taunt` | the Block, Hold, Passive, Ranged orders (on and off) / Sneak on / Taunt |
| `Right_Click` | an order given: right click, context menu or quick order; not a move order while steering |
| `Permanent_Job` | an order given with Shift (a job added to each selected character's list instead of a one-shot order), once per order however many characters get it and even if the add is refused: `FUN_1407f5a90` posts it unconditionally after its loop over the selection (**Verified**, decompiled; the dispatcher link is **Observed**, [ai-tasks](game/ai-tasks.md)); Medic and Rescue with Shift the same; its hash is in `SFX_General_Bank` (**Observed**) |
| `Inventory_Open` / `_Close` | any inventory window (character, bag, loot, trade) created or removed |
| `Item_Lift`, `Item_Drop`, `Sell_Item`, `Character_Drop_Ground` (`Inventory_Items`) | an item picked up with the mouse / put down / traded / dragged out to the ground; the switch is by record, below |
| `Stats_Open` / `_Close`; `Tab`; `Map_Open` / `_Close`; `Tech_Open` / `_Close` | windows: stats; overview tabs (map 0, research 2; the others `Tab`); Map_Close only when closing on the map tab |
| `Building_Button`; `_Navigate`; `_Confirm`; `_Undo`; `_Place` (`Building_Place` = `Outdoors` / `Indoors`); `_Cant_Build_There`; `_Click` (`Buildings_Click`) | entering build mode; list and arrow selection; confirm; undo (only with something queued); a placed ghost; a refused one; selecting a building (index 0 is silent) |
| `UI_Generator` (`Generator` = `On` / `Off`); `Building_Lock_Unlock` (`Build_Lock` = the state being *left*) | machine power switch; door Security button (`FUN_140547060` sets `Build_Lock` to `Lock_Building` when the door's lock flag is set, else `Unlock_Building`, posts, then flips the flag: **Verified**, decompiled) |
| `Dialogue_Choice` (`Dialogue_Choice` = `Continue_Conversation`) | a reply picked |
| `Notifications` (`Notifications` = `Building_Complete`, `Thing_Complete`, `Bounty_Fulfilled`, `New_Biome`) | construction finished; research or blueprint learned, a dismantle finished; a bounty paid; the HUD biome banner appearing ([Biome banner](game/ui-screens.md#biome-banner), `MainBarGUI::vfunc_10`, **Verified**) |
| `Slider`, `Create_Character` | data-panel slider; the character editor's Done (and a released prisoner) |
| `Prospect_Click`; `Prospect_Results` | the HUD Prospect button when it gives a character the prospecting task (`FUN_140721800`; closing the open window is silent); the Prospecting Results window shown (`FUN_14049f180`), a second after the click ([Prospecting](game/ui-screens.md#prospecting)) |
| `Research_Tab` (`Research_Tabs`) | a pick in the overview research tab's type list (`ResearchTypeList`; `FUN_14049b5e0`, not for a deselect); the switch value is the list item's text, the RESEARCH `category`. **Verified** bank: five values, `Industry` and `Defense` resolved by hash, three **Unknown**; an unmatched value is silent |

`Inventory_Items` (`FUN_14075e360`, **Verified**): WEAPON and CROSSBOW by `material` (`Weapon_Metal`, `Weapon_Wood`); ARMOUR by `material type` (`Fabric`, `Leather`, `Robotic_Component`, `Steel_Bar`); ITEM, CONTAINER, NEST_ITEM
and MAP_ITEM by `inventory sound` (`Backpack`, `Building_Material`, `Fabric`, `Flour`, `Food`, `Kits`, `Leather`, `Luxury`, `Narcotics`, `Ore`, `Potato`, `Robotic_Component`, `Rum`, `Steel_Bar`, `Tools`, `Water`, `Wheat`,
`Armor_Plating`, `Blueprints`, `Meat`); LIMB_REPLACEMENT `Robotic_Component`. The `Notifications` bank also has `Raid` and `Under_Attack` and three unresolved values the exe never passes. **Unknown:** the MyGUI events behind
Hover and Slider; whether the editor's speed reset posts `Game_Speed_Normal`; what Wwise does for a NULL switch value.

Meitou (`UiSounds` in `Meitou.Audio`, `PlayerInterface.Sounds.cs`, `GameHost.AudioUi.cs`): the host plays what `PlayerInterface.UiSound` receives. Hooks: `ExecuteUiAction` (HUD actions), `FaithfulAction` (the original screens' widgets),
the commands `Say` sends (orders, item moves, trades, dialogue choices; `AddJob` and the Shift Medic / Rescue commands as `Permanent_Job`), and a poll in `Draw` for windows opening and closing, the title button under the pointer, the speed keys (F2 to F4, when the clock moves to a new speed without a button), a building of the player's finishing
(`Building_Complete`) and a research finished or a building unlocked by a blueprint (`Thing_Complete`, one a frame; the sim also queues `DismantleDone` for a finished dismantle and `BountyPaid` for a reward, played as `Thing_Complete` / `Bounty_Fulfilled`).
The prospecting, research tab and biome sounds are posted where the exe posts them: the HUD's Prospect action when it gives the task (`Prospect_Click`), the results window opening (`Prospect_Results`, in the poll), a type list pick in the overview (`Research_Tab`),
and the banner (`New_Biome`, `PlayerInterface.Biome.cs`).
`Building_Click`: selecting a building (`SetBuilding`, so a click on the one already selected sounds again) switches `Buildings_Click` by the BUILDING's `select sound` (`UiSounds.BuildingSound`) and posts nothing for 0. The switch values are the exe's table at
`0x141e17308` read by `FUN_1405504a0` (**Verified**: read from kenshi_x64.exe; 1 `Bed`, 2 `Dummy`, 3 `Farm`, 4 `Generator`, 5 `Grain_Silo`, 6 `House`, 7 `Mine_Ore`, 8 `Mine_Stone`, 9 `Research_Bench`, 10 `Store_Armor`, 11 `Store_Building_Materials`, 12 `Store_Flour`, 13 `Store_Ore`,
14 `Store_Prisoner`, 15 `Store_Rum`, 16 `Store_Stones`, 17 `Stove`, 18 `Well`, 19 `Wind_Generator`, 20 `Chain_Bench`, 21 `Fabric_Loom`, 22 `Leather_Bench`, 23 `Medical_Desk`, 24 `Plate_Station`, 25 `Shop_Counter`, 26 `Smithy`, 27 `Stone_Processor`, 28 `Armorer`, 29 `Bar`, 30 `Boxes`,
31 `Campfire`, 32 `Gate`, 33 `Gen_Goods`, 34 `Hive_Hut`, 35 `Lamp`, 36 `Long_House`, 37 `Outhouse`, 38 to 42 `Store_Armor`, `Store_Building_Materials`, `Store_Flour`, `Store_Ore`, `Store_Prisoner` again, 43 `Tailor`, 44 `Tower`, 45 `Travel`, 46 `Turret`, 47 `Wall_Metal`,
48 `Wall_Station_House`, 49 `Wall_Stone`, 50 `Wall_Wood`, 51 `Wood_Obj_Big`, 52 `Wood_Obj_Small`; the **Unknown** is which BUILDING records set which index, and whether the bank has a sound for each value). `Building_Lock_Unlock`: Meitou's stand-in for the Security button is the
building menu's Lock Doors / Unlock Doors verb (all the doors at once; `Build_Lock` is the state before the press). `Create_or_Load_Game` comes from `MusicDirector.OnCreateOrLoadGame`, `Hover` from the poll.
**No trigger in Meitou (not played):** `UI_Generator` (the machine panel has no power switch, [above](#machine-sounds-meitou)), `Create_Character` on a prisoner's release,
`Slider` outside the editor (no data-panel slider yet; its drag events would sound through `SoundForScreen` as the editor's do). `Building_Place` is `Indoors` for a ghost on a building's floor (a parent building), else `Outdoors`.

## Meitou's player

`src/Meitou.Audio`: `AudioSystem` wires these together for a host (the viewer, the game); any missing part (no exe library, no device) leaves the
host running without sound.

- `WwiseCodebookLibrary.Load` reads the codebook library from `%LOCALAPPDATA%\Meitou\audio\codebooks.bin` (72 KB), made from the exe the
  first time and remade when the exe's size or date changes (54 ms from the exe, 14 ms from the cache).
- `WwisePlayer` loads a bank on a worker (reads it, converts every WEM to Ogg Vorbis in parallel: about 130 ms for a biome bank) and then
  hands it to the audio thread. Events wait (up to 10 s) for their bank. It interprets:
  - **Actions**: play (with the action's fade-in), stop (every playing instance whose node is the target or below it, with the fade),
    set state, set/reset volume, pitch, low-pass and bus volume offsets (faded, in dB; per game object for scope 3), the delay property, and effect
    bypass ([reverb](#reverb-and-aux-buses)). Stop with scope 3 only reaches sounds started on the posting game object.
  - **Sounds** loop by their loop property (0 forever). **Random/sequence containers**: continuous ones (flags bit 3) play one child after
    another (back to back; transition mode 3 waits the transition time), forever or for the loop count; step ones play one child. Random
    picks by weight, skipping the last *avoid repeat* picks. **Layer containers** play all children, each scaled by its layer's crossfade
    curve. **Switch containers** with `ContinuousValidation` swap children with fades on state changes and wait even when silent;
    step switches select once per play and end with their children. **Verified** against `SFX_General_Bank` in `WwisePlayerInstallTests`:
    repeated footsteps release all roots; a surface change does not interrupt a playing footstep or restart finished ones. Step/continuous semantics:
    [Wwise guide](https://www.audiokinetic.com/en/public-library/2024.1.5_8803/?id=defining_contents_and_behavior_of_switch_containers&source=Help).
  - **Gains** add up from the sound to the top of the hierarchy: volume properties (dB), a random roll in each node's ranges per play,
    RTPC curves of the game parameters, then the output bus and its parents (bus volume, the slider curves). Volume curves store y as
    linear gain − 1 (below). Pitch (cents) resamples; low-pass runs a one-pole filter.
  - **3D paths**: a positioned container's one-shots pick a random path and move along its vertices (listener-relative x, z); the player
    pans them left/right by direction (equal power). The ambience has no attenuation and no sends, so these get neither distance gain nor reverb.
  - **Positions, attenuation, reverb**: [below](#game-objects-3d-and-attenuation).
- `AudioOutput` keeps four blocks of 1024 frames (23 ms each at 44100 Hz) queued on one OpenAL Soft streaming source, refilled by a thread.
- `AmbienceDirector` is the game's rules above: biome under the pivot, day/night, `Camera_Zoom`, `Time_in_Game`, rain and wind. Two
  fixes of the game's bugs, each a switch on the Fixes settings page (off plays as the game): the delayed-load suffix bug is not copied (the right event always plays; `Ambience after loading`), and the wind is stopped with
  `Weather_Wind_End` (`Wind stops`; off posts the game's `Weather_Wind_Stop`).
- **Music engine** (`WwisePlayer.Music.cs`; the layouts and meanings are in [wwise-bnk.md](formats/wwise-bnk.md#music)). Music nodes are
  instances in the same tree as the sounds, so fades, stop actions (also on an ancestor, across banks), the gain chain up to the music bus
  (`BA70EAC8`: −4 dB, the `User_Volume_Music` curve) and the voices are the same code.
  - **Switch** (`MusicSwitchInstance`): the decision tree is read with the current values of its groups (the child keyed with the value,
    else the one keyed 0; `SetSwitch` shares the state table). When a value changes during playback the playlist playing is held after its
    current segment and the new pick starts at that segment's exit cue (**Unknown**: the other candidate is the rule that crossfades at once,
    ~10 s); a change back before the cue cancels it; once the last segment's cue has passed (only its tail left) a change does nothing.
  - **Playlist**: the tree is walked lazily, the next segment pulled at each exit cue and started there, the old one's tail sounding on.
    Segment items repeat by loop count (0 forever); groups make loop-count passes of one child (step) or all (continuous), in order
    (sequence) or by weight (random; shuffle = weighted without replacement). Avoid-repeat history and sequence positions are kept per
    playlist group across plays. A segment whose bank is not loaded is skipped.
  - **Segment**: one voice per clip at segment time `PlayAt + BeginTrim`, played from the begin trim to the end trim; silences hold the time.
  - **Timing.** A start is placed at the output frame at or after the exact time inside the block being mixed, with the fractional part as
    the clip's starting phase, and a clip fades into silence over its last frame, so a segment that follows another at its cue leaves no
    gap and no overlap: the main theme's 281.7 s loop, 12,423,326.475 output frames long, repeats without a missing or doubled frame.
  - Plays are not exclusive: a `_Play` posted while the last still sounds (a short `Music frequency`) starts a second instance (**Unknown**
    whether Wwise limits music nodes). Not done, since no Kenshi bank needs it: stingers, transition segments, special track types, clip
    automation, the transition rules' fades, `continue playback`, the radio's 3D attenuation. `UnloadBank` cuts what plays from the bank,
    drops its recordings and objects (those of the same id in another loaded bank stay).
- `MusicDirector` is the game's music rules above. `Update(music)` each frame takes the `music` of the region under the pivot (null when
  none): on a change it sets the `Biomes` switch and remembers `Music_<Name>_Play` (when the bank is loaded, else after loading it; the
  first change unloads `MT_Music_Bank`); a real-time stopwatch timer (first deadline construction + F, then now + F × (0.8 + 0.6 r)) posts
  the remembered event when set and `Music volume` is above 0, moving the deadline either way; no `_Stop` is ever posted; a biome bank
  not current for 60 s is unloaded. `StartMainTheme` (loads `MT_Music_Bank`, posts `Boot_Up_Game`, pauses the timer) and
  `OnCreateOrLoadGame` (posts `Create_or_Load_Game`, resumes it) are for a host with a title screen (the game, see below); the viewer has none and does not call
  them. `Music frequency` (minutes, default 5) and `Music volume` (default 0.5, sent as `User_Volume_Music` × 100) come from `AudioSettings`.

### Volume curves (Observed)

All 111 volume RTPC curves in the version-112 banks (scaling 2, "dB") have y between −1 and +0.08, while pitch curves are in cents and
low-pass curves in its units. Read as **linear gain − 1** (−1 silent, 0 unchanged), they make sense everywhere: the sliders' curves are
silent at 0 and unchanged at 100, the critters fade out with height. The player multiplies the gain by 1 + y. Not checked against Wwise
itself.

### Levels (Observed)

The ambience is quiet: the Desert bed is mastered at −45 dBFS RMS (the general sound effects at −25 to −33), its layer container takes
12 dB, the slider's default another 8.5, so the desert by day mixes at about −66 dBFS RMS. That is what the banks say; whether the game
sounds the same was not measured. The viewer's `--audio-gain <dB>` raises the whole mix.

The music at `Music volume` 0.5 (`User_Volume_Music=50`): the main theme's loudest second is about −29 dBFS RMS, a biome play's about −37 to −45
(one segment per layer, so seconds of silence between pieces). `Init.bnk` defaults the parameter to 100 (the curve is half at 50, so +6 dB),
which is what the offline render uses unless told.

### Game objects, 3D and attenuation

What the game does (**Verified**, `FUN_140259620`, `FUN_140258410`): it registers object 100 (`Music`) and 110 (`GUI`); every event of the
ambience, weather and music posts on 100, `Create_or_Load_Game` on 110. Point emitters (AMBIENT_SOUND records) each get an object id from a counter:
register, `SetSwitch("Emitters", name)`, `SetPosition` with a zero orientation, `SetRTPCValue("Intensity")`, then `Start_Emitter` / `Stop_Emitter` on it. The exe
never sets aux sends, output bus volumes or occlusion (the wrappers exist, unused), and registers the plug-ins silence source (`0x65`), RoomVerb (`0x76`),
parametric EQ (`0x69`), compressor (`0x6C`) and one effect `0x6E` (Unknown).

- **Listener.** The game's `SetListenerPosition` call (`FUN_1406b0140`) puts the camera's up vector in the front slot and leaves the top slot unset (**Verified**
  by disassembly), so its 3D panning follows no real direction. Meitou's listener has the camera's true front and up (`AudioSystem.SetListener`); there is no
  Faithful switch, since the game's behaviour is a broken orientation that cannot be reproduced (and no new option may be added). The Audio settings' Hear from
  switch (`UserConfig.HearFromSelected`, default Camera) moves the listener, the distance limits and the building test to 160 units above the first selected
  character (the camera while none is); the orientation stays the camera's, so the pan matches the screen.
- **Objects.** `WwisePlayer` registers an object on first use. A sound posted on an object remembers it; its position (`SetPosition`) is read each block
  (lock-protected). Object 0 is the default object and is never placed. An object never placed is heard at the listener. `UnregisterObject` ends its sounds.
  `SetSwitch` and `SetParameter` take an object (0 = global); a value set on an object wins over the global one. An object-scoped Stop (scope 3) only stops
  sounds started on the posting object.
- **Position.** A node whose positioning is game-defined (3D, no paths) is placed at its object. Distance and pan come from the object's offset in the
  listener's frame (`Spatial.Locate`): the pan is side over horizontal distance, front and back folded, pulled to the centre within 50 units, then the
  equal-power law. Paths (3D type 3) move relative to the listener as before.
- **Attenuation.** The node's [attenuation](formats/wwise-bnk.md#attenuation-14) gives the dry gain (curve 0 at the distance, linear gain − 1 as for
  RTPC curves; zero beyond the last point), the low-pass (curve 3) and, only for a placed object with a front, the cone (full inside the inner angle,
  the outside volume and low-pass beyond the outer, linear between). Measured by rendering: a sound 100 / 200 / 300 / 500 units away fell by about
  8 / 18 / 26 dB / to silence against the one at the listener, and was louder on the side it was placed. The emitters (AMBIENT_SOUND) are placed with a zero orientation, so cones are inert there.
  **Characters** (`FUN_1405cfd90` copies the position and a facing vector into the object's placement, **Verified** by disassembly; what Wwise does with that vector, whose second
  orientation slot looks unset like the listener's, is **Unknown**): the impact sounds (the hit and parry variants of `Impact`) use attenuation `2E5501C5`, the only one with a cone, 90° / 245°
  and −6 dB with no low-pass. Logged in a sandbox fight (listener 150 to 180 units away), a blow's dry gain was 0.30 for a fighter facing away from the listener (angle 150°),
  0.40 at 86° and 0.55 facing it, at the same distance: half the amplitude on and off. Meitou places characters without a front (engine choice), so no sound has a cone.

### Reverb and aux buses

- **Which reverb** is switched by the game's Bypass-effects actions ([wwise-bnk.md](formats/wwise-bnk.md#buses)): `Enter_<biome>` turns the outdoor reverb on, the
  building events turn the big or small indoor one on and the outdoor one off, `Leave_Building` reverses it. `WwisePlayer` keeps the bypass bits per aux
  bus and applies the actions; hosts can also call `SetAuxBypass(auxBus, bypass, slots)`.
- **Which sounds** reach it: the nearest node with aux-send bits 04/08 sends to the bus ids it lists (`SFX_General_Bank`: 6 nodes, `VO_Bank`: 1, to both
  aux buses), the level being the voice's level times the attenuation's aux curve (usually the dry curve) and the output bus gain (Meitou's choice).
  Every ambience and weather node has no sends, so the beds never go through the reverb.
- **The reverb** (`RoomReverb`, a clean-room approximation of RoomVerb, **Observed** parameters in [wwise-bnk.md](formats/wwise-bnk.md#effects-18-and-19)):
  Freeverb-style, 8 damped combs and 4 Schroeder all-passes per side with a stereo spread, comb feedback set for the effect's decay time, damping from
  the HF damping parameter, a pre-delay of up to 250 ms, a 250 Hz input high-pass (below it all combs add coherently), input normalisation, the wet level
  the root-sum-square of the two wet level parameters scaled by the effect's RTPC curves (the outdoor one follows `Camera_Zoom`). A bypassed reverb takes no
  input and rings out (Wwise's behaviour is **Unknown**); slots of one bus run in parallel (Wwise runs them in series, **Unknown**). A silent tail is skipped.
  Measured by rendering: decay about 3.5 s (big indoor), 0.9 s (small), 2 s (outdoor); the level is near unity.
  Unknown: which wet level is early reflections or tail, the pattern id and millisecond bytes, the outdoor pre-delay curve.

### Voice cap

Meitou's rule, the Audio settings' Sound voices slider (`AudioSettings.Voices`, kept in meitou.user.json: 16 to 256 in steps of 8, Meitou 96, the top
"All" mixes every sound; the viewer uses 96). Each block the player works out every sound's gains first
(`SoundInstance.Prepare`: the node chain, buses, place and attenuation), then mixes the music and that many of the loudest
other sounds at the listener (dry or sent); a sound mixed in the last block counts 1.5 times as loud, so one at the edge does not flip every block. The rest
are silent: one heard last block first fades out over a block, then its voice keeps its clock without decoding (`Voice.Skip`); back among the loudest it
moves its place within the decoded frames, or reopens and seeks (an error ends the voice, never the mix), and fades in over one block; a one-shot that ran out
meanwhile ends. Trimmed music clips are never
capped. The game's own Wwise limits (`STMG` max voices and volume threshold, each node's max instances, read but not modelled) are not this rule. The `--quit-after` log
shows how many sounds were capped. After the mix a peak limiter (both modes, `AudioOutput.Limit`: ceiling 0.89, instant attack, 0.15 s release) turns a
loud sum down instead of hard-clipping it at the 16-bit conversion.

Starting a sound is the real cost of a big fight: opening a Vorbis stream (its codebooks) took about 1.7 ms and every voice decoded its clip again, so
hundreds of footsteps, swings and hits a second overran the audio thread (**Observed** with `meitou-tools audio render`: 700 hits and grunts in 8 s cost 1.7 s
of mixing). Clips up to 20 s are decoded once and shared by their voices (`PcmCache`, up to 256 MB; longer clips, the music and anything past the budget
stream as before): the same render takes 0.57 s, about 0.3 ms a sound once its clip is decoded. A capped voice on shared samples catches up by moving its place.

### Hearing range

Meitou's rule, the Audio settings' Sound range slider (`AudioSettings.Range`, meitou.user.json: 1 to 4 in steps of 0.25, Faithful 1, Meitou 2, which the viewer
also uses). `WwisePlayer.Reach` divides every distance before it is read: the attenuation curves (dry, aux, low-pass) and the game-side limits
(`WwisePlayer.HeardDistanceSquared`: character tracking and event limits, door and turret ranges, the AMBIENT_SOUND ranges). The pan uses the true
distance. More reach means more objects and sounds at once, which the [voice cap](#voice-cap) bounds.

### API for event sources

For a host that posts its own events (footsteps, combat, UI, voices, emitters), through `AudioSystem.Player` (a `WwisePlayer`):
`Post(event, gameObject)` (a name or id), `SetPosition(gameObject, position, front)`, `RegisterObject` / `UnregisterObject`, `SetSwitch(group, value, gameObject)`,
`SetParameter(name, value, gameObject)`, `SetAuxBypass`, and `AudioSystem.SetListener` / `SetPaused` / `Apply(AudioSettings)`. `AudioSystem.MusicObject`
(100) and `GuiObject` (110) are the game's ids; pick other ids for emitters. `SFX_General_Bank` (loaded with the ambience) holds the effects. Events found by
hashing the exe's strings: about 71 of its 88 (`Footsteps` `8E31CC26`, `Start_Emitter` `AEB16404`, `Stop_Emitter` `AE9274C2`, `Impact` `C2299AA7`, `Attack`, `Door_Open`, the UI
and `Game_Speed_*` events) and 6 of the 14 in `VO_Bank` (`VO_Creature_*`, `VO_Get_Hit`). Switch groups confirmed by hash: `Door_Type`, the weapon groups,
`Impacts`, `Break`, `Armor_Worn`, `Surface_Material`, `Consume`, `Notifications`, `Stats_Preview`, `Inventory_Items`, `Building_Place`, `Buildings_Click`,
`Emitters` (`2488FB4C`; values such as `Furnace`, `Stone_Processor`, `Generator`, `Research_Bench`); game parameters `Movement_Speed` (`5A988332`) and others.
Open for the next wave: which event each game action posts and on which object, and the footstep switches. Interiors and emitters: [Interiors](#interiors), [Point emitters](#point-emitters).

### Not done

The emitters of foliage and map features, the callers that post effects, the task and interface sounds whose trigger Meitou lacks (listed under [Characters](#characters-footsteps-combat-voices) and [Interface](#interface)) (the engine is ready:
[API](#api-for-event-sources)), the game's Wwise voice limits (Meitou has its own [cap](#voice-cap)), obstruction and occlusion (the game uses none), the radio, serial effect slots and the cone as Wwise draws it, and unloading ambience
banks left behind (they stay loaded: about 5 to 14 MB of Ogg each; the music banks unload as above).

## In the viewer and the game

On by default; `--no-audio` turns it off, `--audio-gain <dB>` raises or lowers everything. The console (the game's `log.txt`) prints the device, or
why there is none: the host then runs silently. The `--quit-after` log's `audio` line shows the biome, day or night, rain, wind and the number of voices, and the music: the
main theme or the remembered biome music, seconds until the timer next posts it, music voices (`AudioSystem.StatsLine`; paused shows). Both hosts feed
`AudioSystem.Update` each frame (region under the pivot, weather, hour, sunrise and sunset, eye height).

- **Viewer** (`WorldApp.Audio.cs`): pivot = the camera's target, eye = its position, the viewer's hour; no title screen, so no main theme.
- **Game** (`GameHost.Audio.cs`): the audio is made on a worker while the window and the world load. Pivot = `camera.Target` and eye =
  `camera.Eye` (the strategy or free camera as drawn), hour, sunrise and sunset from the game clock (`session.Clock`), region and weather from the
  weather scheduler (`gpu.Weather`). Each frame it also sets the listener from the camera (`camera.Forward` and the true up) and `SetPaused(session.Simulation.IsPaused)`
  (paused or speed 0): `Pause_Game` / `Resume_Game` post on object 100. `SFX_General_Bank` loads with the ambience banks.
  - **Audio sliders.** The settings menu's Audio page has the original's seven (`Music volume`, `Ambient volume`, `Footstep volume`, `Sfx volume`, `UI volume`, `VO volume`, `Music frequency`;
    [settings.md](formats/settings.md)), kept in `meitou.user.json` (`audio`); the first run takes them from the install's `settings.cfg`. They apply at once (`AudioSystem.Apply`),
    where the original applies them when its options close. Meitou adds Sound voices ([Voice cap](#voice-cap)), Sound range ([Hearing range](#hearing-range)) and Hear from (Listener, above).
    The viewer still reads `settings.cfg` once at start.
  - **Title screen.** `StartMainTheme` when the world is up and the title is open (either UI mode: the faithful title or the Meitou game menu).
    While the title is open the host does not update the ambience or the music (**Unknown** whether the original's camera update runs there; if it did, its first biome call would unload the theme's bank), so no `Enter_` event or
    music-bank unload cuts the theme. A new game or a load posts `Create_or_Load_Game` (5 s fade) as it starts; `--new-game` and `--load` skip the
    title and start no theme. Importing is not offered by the game's menu.
  - **Return to the main menu** (Meitou's choice: the original has no such path): the ambience is ended (`AmbienceDirector.Silence`: `Leave_<biome>`,
    rain and wind end) and `StartMainTheme` runs again, so the menu sounds as at start-up; the next game's first update re-enters its biome.
  - **World sounds** (`GameHost.AudioWorld.cs`, each frame after the ambience): the building under the pivot (`InteriorSounds`, [Interiors](#interiors)) and the building emitters (`WorldEmitters`,
    [Point emitters](#point-emitters)) with the eye as the distance origin. The level's emitters are read once on a worker (`WorldObjects.Emitters`), a player-built site's when it completes. The main menu ends the
    interior ambience and stops the emitters. `GameHost.AudioEffects.cs` adds the effect emitters (sky beams) and `GameHost.AudioTurrets.cs` the turrets' movement sounds ([Other world-side events](#other-world-side-events)); robot spiders are in `CharacterSounds`. The console prints the number of emitters read; the end-of-run line `audio     world:` shows indoors or outdoors, emitters playing of
    all, and the nearest one's name and distance.
  - **Characters and interface.** `UpdateAudio` also calls `UpdateCharacterAudio` (`GameHost.AudioCharacters.cs`, [Characters](#characters-footsteps-combat-voices)); the interface sounds are on from `AttachAudio`, also on the
    title screen (`GameHost.AudioUi.cs`). `VO_Bank` loads with the first character. The `--quit-after` log shows `characters: N characters, N footsteps, N swings, N impacts, N voices, N interface sounds`.
  - Not in screenshots, the headless paths or tests: only the interactive run creates the audio.

## Tools

- `meitou-tools audio` surveys the banks; `audio <bank>` dumps a bank's events and tree; `--decode` decodes its WEMs; `--wav <folder> [id]`
  writes them as WAV.
- `meitou-tools audio render <seconds> <out.wav> [Bank.bnk …] [Event …] [Parameter=value …] [Group:State …]` mixes events offline (no
  sound card) and prints the level each second, e.g.
  `audio render 60 desert.wav SFX_Amb_Desert.bnk Camera_Zoom=5 Time_in_Game:Day Enter_Desert_Day`. A word of eight hex digits is an event
  id (`audio render 290 mt.wav MT_Music_Bank.bnk User_Volume_Music=50 7D68E3F0`); music: `… Dunes_Music_Bank.bnk Time_in_Game:Day Music_Dunes_Play`.
