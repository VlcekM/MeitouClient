# Audio

How Kenshi drives its sound, and how Meitou plays it. The bank and WEM formats are in [formats/wwise-bnk.md](formats/wwise-bnk.md); this
page is the runtime: what the game sends to Wwise, and Meitou's player (`src/Meitou.Game/Audio`). Only the ambience (biome beds, weather beds) and the music
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
  The Crater and Border Zone are Canyons, Shun is Coast (Verified, records). `Enter_Canyons_Night` starts layer `0C07AA21` (two loops) and
  random one-shots `221D4A37`; the loops are tonal, peaking near 1.5-2 kHz and 4.5 kHz like an insect chorus, the day loop is low wind
  (Observed: band energies of the exported wems), so crickets there at night are the data's.
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

- **When.** The hit object's handle is compared with last frame's (null: nothing hit, or no building). On a change: `Leave_Building` if the previous one was a building (even one without an ambience), then,
  if the new building's `interior ambience` (enum `InteriorSound`: 1 Metal, 2 Stone, 3 Wicker, 4 Wood; 0 None starts nothing) is not None: switch `Indoors` = its name, `Building_Integrity` = its
  `interior integrity`, `Enter_Building`, or `Enter_Building_Big` when `cavernous`; all on object 100. The events also flip the reverbs ([below](#reverb-and-aux-buses)).
- **Data** (base game records, Verified). 603 BUILDINGs; 58 have an interior-mask hull, 38 of those an ambience (Metal 6, Stone 20, Wicker 4, Wood 8) and 20 none; 25 more have an ambience but no hull and never
  sound. Integrity is 100 for most (50 and 70 for some, 0 and 1 for ruins); `cavernous` is set on 17 Metal and 2 Stone buildings. 13 gateways have a hull, none an ambience.
- **Meitou** (`InteriorSounds`; `WorldAudio`). `WorldObjectRenderer.InteriorUnder(x, z)` finds the loaded building whose hull footprint (`InteriorVolume.ContainsGround`) holds the pivot, highest first; the
  volume carries `Ambience`, `Integrity` and `Cavernous` from its record. Gateways keep a hull for this test only (`InteriorVolume.IsGateway`, `ZoneObjects.GateHulls`: `FUN_140551b20` builds the interior object of any building with an `interior` list, no gateway test, **Verified**; they are no room for roof hiding, sight or the view). Differences: destroyed buildings have no volume (the exe's hull would still be hit), each shape of the mask's collision is taken as
  its convex footprint, and a hull whose top lies below the terrain at the pivot's X/Z still counts (the exe's ray hits the terrain first; `ContainsGround` ignores terrain). Rendered (`audio render … Indoors:<name> Building_Integrity=100 Enter_Building`, `Camera_Zoom` 50): about −84 (Wood), −78 (Metal), −84 (Stone), −87 (Wicker) dBFS; integrity 0 is 4 dB louder than 100
  (**Observed**; it fades in over 1 s and `Leave_Building` ends it in 1 to 2 s).

### Point emitters

Traced 2026-10-17: the emitter class (`FUN_140259a80` builds it from an AMBIENT_SOUND record, `FUN_140258410` registers, `FUN_140258cb0` updates, `FUN_1402585c0` / `FUN_1402581a0` start and stop, `FUN_140258c20` sets
power, `FUN_140258260` destroys), the building set-up `FUN_14055c240`, its loader `FUN_14029e0c0` and the production update `ProductionBuilding::vfunc_28`. **Verified** unless marked.

- **The record.** AMBIENT_SOUND (27 in the base game): `emitter` (the `Emitters` switch value; 25 distinct names, all cases of the `Start_Emitter` container, [wwise-bnk.md](formats/wwise-bnk.md)),
  `chance` 0..1, `cutoff` (50; `Sun_Beams` 300), `intensity` 0..1, `efficiency multiplier` 0..1, `needs power`. Users: BUILDING `sounds` (about 50 buildings; each reference's three values are an offset),
  EFFECT `sound` (SkyBeam, venge beam, beam1: `Sun_Beams`), RACE `ambient sound` (the two robot spiders: `Iron_Spider`, on the character's own object), FOLIAGE_MESH and MAP_FEATURES `sounds` (none in the
  base game; the exe walks all their entries, each rolled by its `chance`).
- **Power.** An emitter starts unpowered. One that has `needs power` waits for `FUN_140258c20(efficiency, powered)`; it also sets `Intensity` = efficiency × `efficiency multiplier` × 100 + `intensity` × 100 when the value changed,
  for an emitter in range. An emitter without `needs power` starts by distance alone. Only two places call it (**Verified**, the callers of `FUN_140258c20`; nothing else starts or stops an emitter but the distance update and the teardown):
  - **Each frame**, `ProductionBuilding::vfunc_28` (the class of mines, wells, refineries, generators, batteries, wind generators, rain collectors and furnaces; skipped while the game is paused, `DAT_1421349c9` = GameWorld `+0x8b9`, [game-loop](game/game-loop.md#speed-and-pause)):
    efficiency = the building's slot 110, powered = `FUN_140299370` (has power) and efficiency above 0.
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
- **Power switch.** The exe's `FarmBuilding::vfunc_161` (the machine panel's power button) flips `power on`, then sets the `Generator` switch to `On` / `Off` on the GUI object 110 and posts `UI_Generator`
  (**Verified**). Meitou has `Machine.PowerOn` (saved, default on) but its machine panel (`ProductionWindows`) has no power button and no command sets it, so `UI_Generator` has no trigger.

### Other world-side events

**Break** (**Verified**, `DoorStuff::vfunc_62` `14029d690`, `WallBuilding::vfunc_62` `14029d560`; the damage entry of the building, called through the vtable). Given a hit's two floats (`p0`, `p1`; `p1` is the heavier term): a door that is not already broken takes
`clamp(2·p1 + 0.5·p0, 20, 60)` and loses 0.015 × that of its integrity (wall: half the sum, clamped 10 to 60, 0.003 ×); when the integrity is 0 or less it is marked broken (virtual `+0x310`/`+0x340`) and `Break` posts on the building's object. **Neither
function sets the `Break` switch group** (cases `Door` and `Gate`, bank `SFX_General_Bank`): no other code in the exe sets it (**Verified**, no `SetSwitch` string `Break`), so with the group never set the original plays the container's default, which
is silent (**Observed**, rendered). **Meitou:** `DoorSounds` posts `Break` when a door becomes broken (the lock bash `LockSystem.Bash` and a lockpick's break set `DoorBroken`), after setting `Break` to `Gate` for door type 2 and `Door` otherwise
(**engine choice**: it fixes the exe's omission; Fixes settings page, `Door break sound`, off plays as the exe). Neither break posts `Character_Unlock_Success`. Walls: a wall line hit to 0
(`CombatSystem.ApplyZoneBuildings`) emits `SoundKind.WallBroken`; the host posts `Break` on a pooled object at the wall's position (`CharacterSounds.WallBreak`; the exe uses the
building's object), with `Break` = `Gate` under the same setting (**engine choice**; off: unset, silent as the exe).

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
  1. Height under 100: `Water`.
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
  Animals only: `VO_Creature_Die` when they go down, `VO_Creature_Victory` for the attacker, `VO_Creature_Alert` when threat goes from none to some, `VO_Creature_Passive` on a 20 to 60 s timer (the first up to 100 s; **Verified** `FUN_1405cdf00`: nothing while the animal is down and the timer stays due; in combat mode the call is skipped but the timer still reschedules). People have no
  other voice events in the exe's character code (grunts on swings are inside the attack events' containers).
- **Machine work** (**Verified**, decompile): each update of `Task_OperateMachine` (slot 2 `FUN_14035bc90`, once `tryOperate` passes and the clip, the functionality's `animation` else
  `stonemine`, plays) and of `Task_UseTrainingDummy` (slot 2 `FUN_14035c1e0`) adds the BUILDING_FUNCTIONALITY's `animation events` to that clip on the operator (`FUN_140065280`, `FUN_140064e10`;
  time = value x 0.01): a sorted insert that skips an event record already there at that time and moves the clip's next-event index past an event inserted before its last fraction
  (`FUN_140064870`). No removal was found: they last as long as the clip's instance (**Observed**). Data (base game and rebirth, **Verified** by probe): mines `Impact` with `Impacts` =
  `Ore` / `Stone` / `Copper` at 30 %, plates and weapon smiths `Plate` 30 %, farms `Character_farm` 30 % (with a ground effect, below), sword and assassin training `Dummy` 40 %;
  the `stonemine` clip's own `Pickaxe` is `Attack` with `Weapons` = `Work_Pick` at 10 %. SFX_General_Bank has those `Impacts` cases and `Attack`'s `Work_Pick` (**Verified**, hashed names).
  The AI's worker goals act out through the same task (87 / 221, [ai-tasks](game/ai-tasks.md#slave-work-shout-and-capture)), so NPC town workers and obedient slaves sound alike.

Meitou (`CharacterSounds`, `AnimationSoundEvents`, `GroundSurfaces` in `Meitou.Game.Audio`; `CharacterAudio`): characters within 1100 units of the eye get an object (dropped past 1400, or when they leave the snapshot);
the host hands each character's place, facing, speed (horizontal distance between the last two snapshots over the tick) and animation layers (`AnimationLayer.Length` and `.Definition`, added to the snapshot) every frame.
Combat facts come from `World.Sounds`, a display-only queue the host sets while audio runs (blocks, hits, shots, bolt hits and sticks, characters going down, fighters taking up a fight); nothing in the simulation reads it, so
it is outside the state hash. Overlay tiles for the ground load on a worker (6 kept).

**Unarmed `Weapons`** (`FUN_1402b4570`, when a technique is chosen): the switch becomes `HtH_Punch` when the technique's last blow uses limb 0 or 1 (the arms, **Verified**) else `HtH_Kick`, with `_Robot` appended for a robot (**Observed**: the exe also appends it when a limb check on the attacker's race says robotic limbs). Meitou applies it when the animation of a COMBAT_TECHNIQUE valid
for unarmed fighting only (not a block or dodge, not a creature's) starts (**Observed**: the exe's guard on the technique is a flag whose meaning is not traced).

### Interface

Every interface sound is an event on the **GUI object, 110**, posted by the handler of its own widget; the exe has no central click hook (**Verified**: all 142 `PostEvent` callers, handler addresses in the table are
the exe's). Posts are unconditional unless noted. Events that are not in `SFX_General_Bank` (so silent): `Medic`, `Initiate_Conversation`, `gui_notrade`, `gui_noequip`.

| Event (switch) | When |
|---|---|
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

## Meitou's player

`src/Meitou.Game/Audio`: `AudioSystem` wires these together for a host (the viewer, the game); any missing part (no exe library, no device) leaves the
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
- `AudioOutput` keeps four blocks of 1024 frames (23 ms each at 44100 Hz) queued on one OpenAL Soft streaming source, refilled by a thread. Every 0.5 s it checks
  `ALC_CONNECTED`; a lost device (unplugged, switched or disabled output) is reopened on the current default with `alcReopenDeviceSOFT`,
  since OpenAL otherwise stays silent until restart (issue #309).
- `AmbienceDirector` is the game's rules above: biome under the pivot, day/night, `Camera_Zoom`, `Time_in_Game`, rain and wind. Two
  fixes of the game's bugs, each a switch on the Fixes settings page (off plays as the game): the delayed-load suffix bug is not copied (the right event always plays; `Ambience after loading`), and the wind is stopped with
  `Weather_Wind_End` (`Wind stops`; off posts the game's `Weather_Wind_Stop`).
- **Music engine** (`WwisePlayer.Music.cs`; the layouts and meanings are in [wwise-bnk.md](formats/wwise-bnk.md#music)). Music nodes are
  instances in the same tree as the sounds, so fades, stop actions (also on an ancestor, across banks), the gain chain up to the music bus
  (`BA70EAC8`: −4 dB, the `User_Volume_Music` curve) and the voices are the same code.
  - **Playlist**: the tree is walked lazily, the next segment pulled at each exit cue and started there, the old one's tail sounding on.
    Segment items repeat by loop count (0 forever); groups make loop-count passes of one child (step) or all (continuous), in order
    (sequence) or by weight (random; shuffle = weighted without replacement). Avoid-repeat history and sequence positions are kept per
    playlist group across plays. A segment whose bank is not loaded is skipped.
  - **Segment**: one voice per clip at segment time `PlayAt + BeginTrim`, played from the begin trim to the end trim; silences hold the time.
  - **Timing.** A start is placed at the output frame at or after the exact time inside the block being mixed, with the fractional part as
    the clip's starting phase, and a clip fades into silence over its last frame, so a segment that follows another at its cue leaves no
    gap and no overlap: the main theme's 281.7 s loop, 12,423,326.475 output frames long, repeats without a missing or doubled frame.
- `MusicDirector` is the game's music rules above. `Update(music)` each frame takes the `music` of the region under the pivot (null when
  none): on a change it sets the `Biomes` switch and remembers `Music_<Name>_Play` (when the bank is loaded, else after loading it; the
  first change unloads `MT_Music_Bank`); a real-time stopwatch timer (first deadline construction + F, then now + F × (0.8 + 0.6 r)) posts
  the remembered event when set and `Music volume` is above 0, moving the deadline either way; no `_Stop` is ever posted; a biome bank
  not current for 60 s is unloaded. `StartMainTheme` (loads `MT_Music_Bank`, posts `Boot_Up_Game`, pauses the timer) and
  `OnCreateOrLoadGame` (posts `Create_or_Load_Game`, resumes it) are for a host with a title screen (the game, see below); the viewer has none and does not call
  them. `Music frequency` (minutes, default 5) and `Music volume` (default 0.5, sent as `User_Volume_Music` × 100) come from `AudioSettings`.

### Levels (Observed)

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

### Reverb and aux buses

- **Which reverb** is switched by the game's Bypass-effects actions ([wwise-bnk.md](formats/wwise-bnk.md#buses)): `Enter_<biome>` turns the outdoor reverb on, the
  building events turn the big or small indoor one on and the outdoor one off, `Leave_Building` reverses it. `WwisePlayer` keeps the bypass bits per aux
  bus and applies the actions; hosts can also call `SetAuxBypass(auxBus, bypass, slots)`.
- **Which sounds** reach it: the nearest node with aux-send bits 04/08 sends to the bus ids it lists (`SFX_General_Bank`: 6 nodes, `VO_Bank`: 1, to both
  aux buses), the level being the voice's level times the attenuation's aux curve (usually the dry curve) and the output bus gain (Meitou's choice).
  Every ambience and weather node has no sends, so the beds never go through the reverb.

### Hearing range

Meitou's rule, the Audio settings' Sound range slider (`AudioSettings.Range`, meitou.user.json: 1 to 4 in steps of 0.25, Faithful 1, Meitou 2, which the viewer
also uses). `WwisePlayer.Reach` divides every distance before it is read: the attenuation curves (dry, aux, low-pass) and the game-side limits
(`WwisePlayer.HeardDistanceSquared`: character tracking and event limits, door and turret ranges, the AMBIENT_SOUND ranges). The pan uses the true
distance. More reach means more objects and sounds at once, which the voice cap bounds.

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
[API](#api-for-event-sources)), the game's Wwise voice limits (Meitou has its own cap), obstruction and occlusion (the game uses none), the radio, serial effect slots and the cone as Wwise draws it, and unloading ambience
banks left behind (they stay loaded: about 5 to 14 MB of Ogg each; the music banks unload as above).

## In the viewer and the game

On by default; `--no-audio` turns it off, `--audio-gain <dB>` raises or lowers everything. The console (the game's `log.txt`) prints the device, or
why there is none: the host then runs silently. The `--quit-after` log's `audio` line shows the biome, day or night, rain, wind and the number of voices, and the music: the
main theme or the remembered biome music, seconds until the timer next posts it, music voices (`AudioSystem.StatsLine`; paused shows). Both hosts feed
`AudioSystem.Update` each frame (region under the pivot, weather, hour, sunrise and sunset, eye height).

- **Viewer** (`InteractiveSession.UpdateAudio`): pivot = the camera's target, eye = its position, the viewer's hour; no title screen, so no main theme.
- **Game** (`GameSound`): the audio is made on a worker while the window and the world load. Pivot = `camera.Target` and eye =
  `camera.Eye` (the strategy or free camera as drawn), hour, sunrise and sunset from the game clock (`session.Clock`), region and weather from the
  weather scheduler (`gpu.Weather`). Each frame it also sets the listener from the camera (`camera.Forward` and the true up) and `SetPaused(session.Simulation.IsPaused)`
  (paused or speed 0): `Pause_Game` / `Resume_Game` post on object 100. `SFX_General_Bank` loads with the ambience banks.
  - **Audio sliders.** The settings menu's Audio page has the original's seven (`Music volume`, `Ambient volume`, `Footstep volume`, `Sfx volume`, `UI volume`, `VO volume`, `Music frequency`;
    [settings.md](formats/settings.md)), kept in `meitou.user.json` (`audio`); the first run takes them from the install's `settings.cfg`. They apply at once (`AudioSystem.Apply`),
    where the original applies them when its options close. Meitou adds Sound voices (Voice cap), Sound range ([Hearing range](#hearing-range)) and Hear from (Listener, above).
    The viewer still reads `settings.cfg` once at start.
  - **Return to the main menu** (Meitou's choice: the original has no such path): the ambience is ended (`AmbienceDirector.Silence`: `Leave_<biome>`,
    rain and wind end) and `StartMainTheme` runs again, so the menu sounds as at start-up; the next game's first update re-enters its biome.
  - **World sounds** (`WorldAudio`, each frame after the ambience): the building under the pivot (`InteriorSounds`, [Interiors](#interiors)) and the building emitters (`WorldEmitters`,
    [Point emitters](#point-emitters)) with the eye as the distance origin. The level's emitters are read once on a worker (`WorldObjects.Emitters`), a player-built site's when it completes. The main menu ends the
    interior ambience and stops the emitters. It also adds the effect emitters (sky beams) and the turrets' movement sounds ([Other world-side events](#other-world-side-events)); robot spiders are in `CharacterSounds`. The console prints the number of emitters read; the end-of-run line `audio     world:` shows indoors or outdoors, emitters playing of
    all, and the nearest one's name and distance.
  - **Characters and interface.** `UpdateAudio` also calls `CharacterAudio.Update` (`GameSound.Characters`, [Characters](#characters-footsteps-combat-voices)); the interface sounds are on from `AttachAudio`, also on the
    title screen (`UiAudio`). `VO_Bank` loads with the first character. The `--quit-after` log shows `characters: N characters, N footsteps, N swings, N impacts, N voices, N interface sounds`.
  - Not in screenshots, `--headless`, the other headless paths or tests: only the interactive run creates the audio.

## Tools

- `meitou-tools audio` surveys the banks; `audio <bank>` dumps a bank's events and tree; `--decode` decodes its WEMs; `--wav <folder> [id]`
  writes them as WAV.
- `meitou-tools audio render <seconds> <out.wav> [Bank.bnk …] [Event …] [Parameter=value …] [Group:State …]` mixes events offline (no
  sound card) and prints the level each second, e.g.
  `audio render 60 desert.wav SFX_Amb_Desert.bnk Camera_Zoom=5 Time_in_Game:Day Enter_Desert_Day`. A word of eight hex digits is an event
  id (`audio render 290 mt.wav MT_Music_Bank.bnk User_Volume_Music=50 7D68E3F0`); music: `… Dunes_Music_Bank.bnk Time_in_Game:Day Music_Dunes_Play`.
