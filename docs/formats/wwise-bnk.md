# Wwise sound banks (`.bnk`)

Kenshi's audio is 24 Wwise sound banks in `data/audio/` (388 MB). There are no loose `.wem` or `.pck` files: every sound the game plays
is embedded in a bank. This page is the clean-room layout, worked out by probing the 24 banks (hex dumps, then a reader that has to
consume every object to its exact declared size) plus the names in `kenshi_x64.exe`'s string table. No Wwise SDK code or third-party
code was used. Reader: `Meitou.Data.Audio.WwiseBank`. Dump tool: `meitou-tools audio [bank] [--media]`. Tests:
`tests/Meitou.Tests/Audio/WwiseBank*Tests.cs`.

## The banks

| Bank | Version | Contents |
|---|---|---|
| `Init.bnk` | 112 | Buses (9), effects (6), aux buses (2); global settings `STMG` (decoded) and `ENVS` (not decoded) |
| `Reverb_Indoor_Bank`, `Reverb_Outdoor_Bank` | 112 | One bus, effect share set, effects, aux bus each |
| `SFX_Amb_<Biome>` ×10 | 112 | Ambience beds of Ashlands, Boneyard, Canyons, Coast, Desert, Forest, Jungle, Plains, Swamplands, Tarsands |
| `SFX_Amb_Indoors_Bank` | 112 | Interior ambience (`Enter_Building`, `Enter_Building_Big`, `Leave_Building`) |
| `SFX_Amb_Weather_Bank` | 112 | Rain and wind beds |
| `<Biome>_Music_Bank` ×4, `MT_Music_Bank`, `Radio_Music_Bank` | 112 | Interactive music (Dunes, Savannah, Swamplands, Tarsands; the main theme; the radio), see [Music](#music) |
| `SFX_General_Bank` | 112 | UI, footsteps, combat, building (855 sounds, 88 events) |
| `VO_Bank` | 112 | Voice (465 sounds) |
| `SFX_Bank` | **88** | An older bank (981 sounds, 130 events) in an older layout; only its sections and object headers are read |

**Verified** (`Every_bank_is_version_112_except_SFX_Bank...`): the versions above, and every bank's id is the name hash (below) of its
file name. Savannah and Dunes have music banks but no ambience bank (the exe's biome list has 12 names: the 10 above plus these two).

## File layout

Little-endian. A bank is a sequence of sections: 4-byte ASCII tag, u32 body length, body. `BKHD` comes first. Unknown tags are skipped
by length. **Verified** for all 24 banks (the sections tile the file exactly).

| Tag | Body |
|---|---|
| `BKHD` | u32 version (112 or 88), u32 bank id, u32 language id (0 in every bank), u32 0, u32 `0x187` (Unknown), then 0 to 12 zero bytes (the section is 20 to 32 bytes) |
| `DIDX` | Media index: 12 bytes per embedded WEM: u32 source id, u32 offset into `DATA`, u32 length |
| `DATA` | The WEM files back to back, at the `DIDX` offsets |
| `HIRC` | The object hierarchy (below) |
| `STID` | Bank names: u32 string type (1), u32 count, per entry u32 bank id, u8 length, ASCII name. Lists the bank itself, and in `MT_Music_Bank` also `SFX_General_Bank` (which its event plays from). **Verified** |
| `STMG` | Only in `Init.bnk`: global settings, see [below](#global-settings-stmg) |
| `ENVS` | Only in `Init.bnk`: environment (obstruction / occlusion) curves. **Unknown**, not decoded |

## Ids and names

Every id that has a name in the Wwise project (banks, events, game parameters, state groups, states, switches) is the 32-bit **FNV-1**
hash (not 1a) of the lowercased name: start at 2166136261, per byte multiply by 16777619 and then XOR the byte. **Verified**: all 24
bank ids match their file names, and the event names below all match objects in the expected bank. Other objects (sounds, containers,
actions) have ids that are not name hashes, and banks store no names except `STID`.

Names found (from the exe's strings, combined as the exe's fragments suggest; **Verified** by hash):

- Ambience, per biome bank: `Enter_<Biome>_Day`, `Enter_<Biome>_Night`, `Leave_<Biome>` (exactly these three events in each of the 10).
- Interiors: `Enter_Building`, `Enter_Building_Big`, `Leave_Building`; switch group `Indoors` (`F49BEF3F`) with `Wood` `7AAB588A`, `Wicker` `18B774EE`, `Metal` `9375C65E`,
  `Stone` `48896D1C`; game parameter `Building_Integrity` (`00C01EF9`, an RTPC of the room-tone containers). **Verified** by hash.
- Weather: `Weather_Rain_Start`, `Weather_Rain_End`, `Weather_Wind_Start`, and **`Weather_Wind_End`**. The exe's string table holds
  `Weather_Wind_Stop`, which is no event in any bank (**Verified** by hash); if the game posts that name ([weather.md](weather.md#sounds-verified-decompiled-fun_1409e8f70-names-only)),
  the wind bed is never stopped by it. **Observed**, not tested in the game.
- Music: `Music_<Biome>_Play` / `Music_<Biome>_Stop` (Dunes, Savannah, Swamplands, Tarsands), `Boot_Up_Game` (`7D68E3F0`, plays the main theme; **Verified** by hash 2026-10-17), `Create_or_Load_Game`, `Radio_On`,
  `Radio_Off`; switch groups `General` (`07F737F7`: `menu`, `In_Game`, `Radio`) and `Biomes` (`3A1DA55A`: `Dunes`, `Savannah`,
  `Swamplands`, `Tarsands`, `Ashlands`); group `80FF233D` (values `Beds`, `Frames`, `6CF968BD`). Not matched: event
  `FCC71927` (stops radio and biome music), group `80FF233D`, state `6CF968BD`, `Biomes` values `56E982E5`,
  `A36D90EE`, `AE786C6D` (**Verified** by hash against every string in `kenshi_x64.exe`; whether the exe uses `General` / `Biomes` is not checked).
- State group `Time_in_Game` (`5DBE7335`) with states `Day`, `night`, `none` (used by switch containers in Ashlands and Coast, and by the
  biome music); interior switch `Wicker`.
- General: `Start_Emitter`, `Stop_Emitter`, `Footsteps`, `Door_Open`, `Map_Open`, UI clicks and many more in `SFX_General_Bank`.
- Emitters: `Start_Emitter` plays the switch container `3808BFD8` (500 ms fade; group `Emitters` `2488FB4C`, default none, 43 cases), `Stop_Emitter` stops it (1 s); each case is one looping
  container with an `Intensity` (`933E38F4`) volume curve on some. All 25 distinct `emitter` names of the base game's AMBIENT_SOUND records are cases (`Fire` `9FAC79C9`, `Generator`
  `13EBF864`, `Water_Tank` `72F5415D`, …; **Verified** by hash). Doors: `Door_Open`, `Door_Shut`, `Door_Close` (`07AAAB24`), `Break`, switch group `Door_Type` (`58A8F750`) with `Wood`
  `7AAB588A`, `Mechanical_Metal` `62EA9F8C`, `Gate` `42DF2F68` (**Verified** by hash).
- Game parameters (the 24 in `STMG`): `Camera_Zoom` (`5FE07BCE`, the one RTPC and layer-crossfade parameter of every ambience and weather
  bank), `Time_in_Game`, `User_Volume_Ambient_SFX`, `User_Volume_Music`, `User_Volume_SFX`, `User_Volume_VO`, `User_Volume_UI`,
  `Footstep_Volume`, `Building_Integrity`, `Intensity`; what the game sets them to is in [audio.md](../audio.md#game-parameters-and-states).
  `Rain_Intensity`, which the exe sets, is not among them: setting it does nothing. 14 of the 24 are not named.

## HIRC

u32 object count, then per object: u8 type, u32 size, then `size` bytes beginning with the u32 object id. **Verified**: in every bank
the objects tile the section exactly.

Types (the format's numbers): 1 state, 2 sound, 3 action, 4 event, 5 random/sequence container, 6 switch container, 7 actor-mixer,
8 bus, 9 layer (blend) container, 10 music segment, 11 music track, 12 music switch, 13 music random/sequence, 14 attenuation,
18 effect share set, 19 effect, 20 aux bus. The reader decodes 2-14, 18-20 ([Music](#music), [Buses](#buses), [Attenuation](#attenuation-14), [Effects](#effects-18-and-19)) in version 112 banks and keeps the rest (states, 1) raw.

**Verified** (`Every_version_112_object_of_a_decoded_type_uses_exactly_its_size`, and a survey of every object): with the layouts below, every object of
types 2-14 and 18-20 in the 22 version-112 banks decodes to exactly its size (3,260 objects counting every bank, as the test does). The field order is certain; the meaning of a field is **Observed** unless
said otherwise.

### Shared pieces

- **Property list**: u8 count, that many u8 property ids, then a u32 value each (float or int by property); then u8 count, the ids,
  and a (f32 min, f32 max) random range each.
- **RTPC list**: u16 count; per entry u32 game parameter id, u8 type (0), u8 accumulation (1), u8 target property (0 volume, 2 pitch,
  3 low-pass), u32 curve id, u8 scaling (2 = dB for volume curves, 0 otherwise), u16 point count, points of (f32 x, f32 y, u32 interpolation:
  4 linear, 9 constant and others seen, the Wwise curve shapes). **Observed**: the y of all 111 volume curves (scaling 2) lies in −1 .. +0.08
  while pitch curves are in cents and low-pass curves in its units; read as linear gain − 1 (−1 silent, 0 unchanged) every one makes
  sense (the slider curves are silent at 0 and unchanged at 100; [audio.md](../audio.md#volume-curves-observed)). Layer crossfade curves
  are plain linear gains (all 1 in Kenshi).

### Event (4)

u32 action count, the action ids in order.

### Action (3)

u16 action type, u32 target id, u8 target-is-bus, property list, then by kind (high byte of the type). The low byte is the scope,
numbered as in the Wwise SDK (**Verified**, by which events use which: `Stop_Emitter` is Stop/3 and the emitters are separate game objects; the ambience `Enter_` events, the
pause and the bypass actions use 2; Play is always 3): 2 = global (the target on every game object), 3 = the posting game object only, 4 = all, 5 = all but the exceptions
on the posting object, 6/7 = all but the exceptions. Kinds seen in version 112 banks:

| Kind | Name | Then |
|---|---|---|
| 0x01 | Stop | u8 fade curve, u32 exception count, exceptions (u32 id, u8 is-bus) |
| 0x04 | Play | u8 fade curve, u32 bank id (the target's bank) |
| 0x08 / 0x09 | Set / reset pitch | u8 fade curve, u8 value meaning (1 offset, 2 absolute), f32 value, f32 min, f32 max, exceptions as Stop |
| 0x0A / 0x0B | Set / reset volume | as pitch |
| 0x0C / 0x0D | Set / reset bus volume | as pitch |
| 0x0E / 0x0F | Set / reset low-pass | as pitch |
| 0x12 | Set state | u32 state group, u32 state |
| 0x1A | Bypass effects | u8 bypass (1 = bypass, 0 = unbypass), u8 slot mask (bit n = slot n, 0x10 = all slots), exceptions as Stop |

Action properties seen: `0x0F` (int ms, 1000 to 10000 on stops and plays: the fade time) and `0x0E` (int ms, 6000; probably a delay).

### Sound (2)

- Source: u32 plugin (`0x00040001` Wwise Vorbis for every real sound; `0x00650002` the silence source, 41 in `SFX_General_Bank`),
  u8 stream type (0 in every sound: embedded), u32 source id (the `DIDX` id), u32 file id (the bank's id), u32 offset, u32 size,
  u8 source bits (0). A source plugin (type nibble 2) adds u32 parameter size and the parameters (size 0 for every silence source).
  **Verified**: the offset is the WEM's offset **in the bank file** (not in `DATA`), and offset and size equal its `DIDX` entry; every
  Vorbis sound's media is in its own bank. Music tracks use the same source layout (all 239 likewise).
- Then the node base (below).

### Node base (sounds and containers)

1. u8 override parent effects, u8 effect count; if any: u8 bypass bits, then per effect u8 slot, u32 effect id, u8 is share set, u8 is rendered.
2. u8 **Unknown** (0 in every object).
3. u32 override bus id, u32 parent id (the actor-mixer or container above; 0 at the top).
4. u8 priority bits (0 everywhere).
5. Property list. Node properties seen: `0x00` volume (dB), `0x02` pitch (cents), `0x03` low-pass (0..100), `0x3A` loop count
   (int; 0 = forever on looping beds), `0x0D` (100 on mixers and some sounds, Unknown), `0x12` (-3, once), `0x3B` (0.15, Unknown).
   Ranges seen on volume, pitch and low-pass: each play adds a random offset in the range.
6. Positioning: u8 bits. Bit 0 = overrides the parent's positioning. If bit 0 and bit 3 (3D) are set: u8 3D bits, u32 attenuation id;
   and if the 3D type (bits 5-6) is 3: u8 path mode, s32 transition time (ms), u32 vertex count, vertices (f32 x, y, z, s32 duration ms),
   u32 path count, paths (u32 first vertex, u32 vertex count), then per path three floats (random range in x, y, z). Values seen:
   `C0` (no override), `C3` (override, 2D), `C9`/`D9` (3D: bit 3 and the 3D byte, a game-defined position), `79`/`F9` (3D with paths, 3D type 3: the random one-shots of the ambience move along
   paths around the listener). With bit 0 clear a node takes its parent's positioning.
7. Aux sends: u8 bits; bit 2 = overrides the parent's sends, bit 3 = four user-defined aux bus ids follow (u32 each, 0 = none). Seen: `00`, `04`, `08`, `0C`. Only 6 nodes of `SFX_General_Bank` and one of `VO_Bank`
   send, all to `[23F4785D, 5E1D37D4]`; there is no send level property (**Observed**: the send is the voice level times the attenuation's aux curve).
8. Advanced settings: u8 bits, u8 virtual voice behaviour (1), u16 max instances, u8 below-threshold behaviour, u8 bits.
9. States: u32 group count; per group u32 group id, u8 sync type, u16 count, (u32 state id, u32 state object id) pairs.
10. RTPC list.

### Containers

- **Actor-mixer (7)**: node base, u32 child count, child ids.
- **Random/sequence (5)**: node base, u16 loop count (0 forever), u16 loop min, u16 loop max, f32 transition time (ms), f32 transition
  min, f32 transition max, u16 avoid-repeat count, u8 transition mode, u8 random mode, u8 mode (0 random, 1 sequence), u8 flags
  (`0x12` or `0x1A`), u32 child count and ids, u16 playlist count, playlist (u32 child id, s32 weight; 50000 each = equal).
- **Switch (6)**: node base, u8 group type (1 = state group), u32 group id, u32 default switch, u8 continuous validation, u32 child count
  and ids, u32 switch count, per switch u32 switch id + u32 count + node ids, u32 node-parameter count, per node u32 id, u8 bits, u8 bits,
  s32 fade out, s32 fade in.
- **Layer (9)**: node base, u32 child count and ids, u32 layer count, per layer u32 layer id, RTPC list, u32 crossfade game parameter,
  u8 its type, u32 association count, per association u32 child id, u32 point count, points (f32, f32, u32).

## Music

Types 10-13, in the six music banks only: 402 segments, 239 tracks, 27 switches, 19 playlists. Reader: `WwiseMusicSegment`,
`WwiseMusicTrack`, `WwiseMusicSwitch`, `WwiseMusicPlaylist`; `meitou-tools audio <X>_Music_Bank` prints the tree. The layouts are
**Verified** by size (all 687 decode exactly). Fields marked *const* hold one value in every object, so their meaning is a guess
(**Unknown**); a non-zero stinger count, transition segment or track type is rejected (kept raw), since no Kenshi object has one.

### Layouts

- **Music node** (start of 10, 12, 13): u8 flags (*const* 0, but 6 on the top switch), node base, u32 child count and ids, the grid
  (f64 grid period ms, f64 grid offset ms, f32 tempo bpm, u8 beats per bar, u8 beat unit, u8 flags: 0 = inherited, 1 or `FF` = set
  here), u32 stinger count (*const* 0). **Verified**: every grid that is set is exactly 16 beats (8000 ms at 120 bpm, 9600 at 100,
  10666.7 at 90, 13714.3 at 70); unset ones read 1000 ms, 120, 4/4.
- **Segment (10)**: music node (children = tracks), f64 duration ms, u32 marker count, markers (u32 id, f64 position ms, u32 name length,
  ASCII name; names all empty). Every segment has two markers: `0298DF12` at 0 (entry cue) and `5BBBD648` (exit cue) at the duration,
  except five radio jingles whose exit cue is 210-325 ms before the end (Observed). 163 segments have no track: silences of 5-60 s.
- **Track (11)**: u8 flags (*const* 0), u32 source count, sources (the [sound source](#sound-2) layout), u32 clip count, clips (u32 sub-track,
  u32 source id, f64 play-at ms, f64 begin trim ms, f64 end trim ms, f64 source duration ms), u32 sub-track count (*const* 1), u32 clip
  automation count (*const* 0; per item u32 clip index, u32 type, u32 point count, points as RTPC, a guess), node base, u8 track type
  (*const* 0), s32 look-ahead ms (*const* 0). Kenshi: one source, one untrimmed clip at 0 per track. **Verified**: each clip's source
  duration equals its WEM's decoded length (239 of 239) and the segment's duration ends where the clip does.
- **Transition rules** (12, 13): u32 rule count; per rule u32 count + source ids, u32 count + destination ids (`FFFFFFFF` = any); exit:
  s32 fade-out ms, u32 fade curve, s32 fade-out offset ms, u32 sync type, u32 cue filter (*const* 0), u8 play post-exit; entry: s32
  fade-in ms, u32 curve, s32 fade-in offset ms, u32 cue filter (*const* 0), u32 jump-to playlist item, u16 entry type (*const* 0), u8 play
  pre-entry, u8 match source cue name (*const* 0); u8 has a transition segment (*const* 0). Observed: the first rule of every container
  is any → any, sync 7, no fades, post-exit and pre-entry on (the default rule). Sync types seen: 7 (exit cue), 0 (immediate, the
  crossfade rules) and 2 (on the layer switches, which set a grid: probably next bar). Curves 1, 2, 4 (linear), 6: the
  [RTPC interpolation](#shared-pieces) numbers. Jump-to `09EA0775` is the main theme playlist's root item (the id resolves).
- **Switch (12)**: music node, rules, u8 continue playback, u32 depth, depth × u32 group id, depth × u8 group type (0 switch, 1 state;
  depth is 1 everywhere, so the order of these two lists is Unknown), u32 tree size in bytes, u8 mode (*const* 0), then the decision
  tree: 12-byte nodes, u32 key (0 = any), then on the last level u32 music object id, above it u16 first child index and u16 child count,
  then u16 weight and u16 probability (*const* 50, 100). The root is node 0. **Verified**: every tree parses and every leaf resolves.
- **Playlist (13)** (music random/sequence): music node, rules, u32 item count, the items depth-first: u32 segment id (0 = group), u32
  item id, u32 child count, u32 play type (`FFFFFFFF` on segments; groups 0, 2, 3), s16 loop count (0 forever, 1 once), u16, u16 (*const*
  0), u32 weight (50000 = 1), u16 avoid-repeat count, u8 use weights (1 on groups), u8 shuffle. Play types read as Wwise's four group
  types: 0 sequence continuous, 1 sequence step, 2 random continuous, 3 random step (Observed: 2 appears only on the radio, with shuffle
  and loop forever; 0 only on the main theme's one-item loop).

### How Kenshi's music is built (Observed)

- **Hierarchy.** The top switch `18B6E6A0` (switch group `General`, bus `BA70EAC8` = the music bus of `Init.bnk`, −4 dB with the user
  slider) maps `menu` → the main theme playlist `3A626E51`, `In_Game` → switch `00DBE1E6`, `Radio` → nothing. `00DBE1E6` (group
  `80FF233D`) maps `Beds` → `38CE2D16` (−3 dB), `Frames` → `3DD92503` (−9 dB), `6CF968BD` → `1E46D584` (−6 dB), and each of these
  switches on `Biomes` to the biome's layer (`Ashlands` and three unnamed values: nothing). Every music bank carries its own copy of
  the upper switches under the same ids: `18B6E6A0` and `00DBE1E6` are byte-identical in the four biome banks; the copies differ only in
  their child lists (each lists the children in its own bank); the decision trees are the same.
- **Biome music.** `Music_<Biome>_Play` plays the biome's three layers **directly** (bed with a 1000 ms fade, the other two 5000 ms), so
  the upper switches only lend their volumes; `Music_<Biome>_Stop` stops all three (6000 ms). The bed layer is a state switch on
  `Time_in_Game` (`Day` / `Night`) between two playlists; Swamplands' second layer is one too. Every biome playlist is a root group
  *random step, loop once* over subgroups (each random step, loop once) of music segments or of silences, so **one play gives one segment
  (or one silence) per layer and then ends**; continuous music needs the game to post the event again (the exe's `Music frequency`
  setting is the lead, see [audio.md](../audio.md)). Avoid-repeat (1 to 6) only matters if the random history outlives one play.

| Bank | Beds (`Time_in_Game` switch) | `6CF968BD` layer | `Frames` layer | Media, tempo |
|---|---|---|---|---|
| Dunes | 6 pieces 62-83 s (7.4 min), 14 silences | 27 pieces 8-37 s (8.4 min), 18 silences | 21 pieces 6-35 s (6.5 min), 16 silences | 54 WEMs, 22.2 min, 100 bpm |
| Savannah | 6, 71-100 s (8.6 min), 14 | 32, 12-37 s (12.2 min), 18 | 34, 6-51 s (9.6 min), 16 | 72, 30.4 min, 120 bpm |
| Swamplands | 3 (night only; day is silences), 78-85 s (4.1 min), 9 | 23, 10-103 s (15.6 min), 24; day/night switch | 29, 6-17 s (5.0 min), 16 | 55, 24.7 min, 90 bpm |
| Tarsands | 6, 85-94 s (8.8 min), 6 | 13, 30-64 s (9.4 min), 6 | 29, 6-69 s (8.1 min), 6 | 48, 26.3 min, 70 bpm |

Silences are 15-60 s in the beds, 5-60 s in the other layers. Segment volumes range −8..+6 dB; the bed switches add −3 (Dunes) or −2 dB (Savannah, Swamplands).

- **Main theme** (`MT_Music_Bank`). Event `Boot_Up_Game` (`7D68E3F0`) plays `3A626E51`: a sequence-continuous group looping forever over one item, itself
  looping forever, of one 281.7 s segment (−6 dB). Its second rule (any → any at the exit cue, fade-out 10100 ms offset +3200, fade-in
  3200 ms offset −600) would crossfade the loop. `Create_or_Load_Game` is Stop/3 on the GUI object, which does not reach the theme (started on object 100); the ambience enter events (Stop/2) stop it.
- **Radio** (`Radio_Music_Bank`). `Radio_On` stops `00DBE1E6` (all biome music, by ancestor; 10 s) and plays `34220ED8` (−9 dB,
  positioned 3D with attenuation `2791811A`): random continuous, shuffle, avoid 4, looping forever over 9 items,
  4 songs of 152-267 s and 5 jingles of 5.5-11.3 s (−6 dB; three items weigh 0.6, one 0.5). `Radio_Off` stops it without a fade;
  `FCC71927` stops radio and biome music (5000 ms). `Radio_On` and `Radio_Off` also play a sound in `SFX_General_Bank`.
- **Media.** Music WEMs are the same Wwise Vorbis as the ambience, all embedded (source stream type 0 in all 239; no streaming, prefetch
  or loose `.wem`), 44.1 kHz stereo except the radio's 9 mono WEMs and the main theme's 48 kHz one. They decode with the rest ([below](#wwise-vorbis)).

### Playback semantics

The Wwise music model as it applies to these banks; the field meanings are Observed, the timing rules **Unknown** until heard against the
original.

- **Segment.** Segment time 0 is its start. A clip sounds its source from `BeginTrim` at segment time `PlayAt + BeginTrim` until
  `PlayAt + SourceDuration + EndTrim` (in Kenshi: the whole WEM from 0). The entry cue is where the segment counts as started (0 here),
  the exit cue where the next segment is aligned; audio after the exit cue is the post-exit tail (the radio jingles), before the entry
  cue the pre-entry (none here).
- **Playlist.** When the playing segment reaches its exit cue (sync 7), the next item's entry cue starts there; with post-exit on, the
  old tail keeps sounding over it. Groups: *step* plays one child per pass of the group, *continuous* plays all children (in random
  order for random, shuffled without replacement with shuffle); weights set the odds; avoid-repeat excludes the last N picks; the loop
  count repeats the group (0 forever) and a segment item (MT: forever = sample-exact repeat at 281,708.1 ms).
- **Rules.** The rule with the best (source, destination) match applies. With two any → any rules (the bed switches, the main theme)
  which one wins is **Unknown**: the first means switching `Day` ↔ `Night` waits for the current piece's exit cue (up to ~100 s) and cuts,
  the second crossfades at once (fade-out ~10.4 s, fade-in ~10 s). Which reference point the fade offsets shift is **Unknown**; the
  values (+3200 out, −3200 in) suggest a crossfade that straddles the sync point.
- **Stop.** A stop action fades the playing instance over its fade time and ends it; a stop on an ancestor (`00DBE1E6`) ends everything
  under it, in any bank.

## Global settings (`STMG`)

In `Init.bnk` only. f32 volume threshold (−80), u16 maximum voices (256), u32 state group count, per group u32 id, u32 default transition
(ms), u32 count and (u32 from, u32 to, u32 ms) transitions; u32 switch group count, per group u32 id, u32 game parameter, u32 count and
(f32, u32, u32) points; u32 game parameter count, per parameter u32 id, f32 **default value**, u32 ramp type, f32 ramp up, f32 ramp down,
u8 built-in. **Verified** by size (the 546 bytes are used exactly; the switch group entry layout is a guess, Kenshi has none). Kenshi: two
state groups (`Time_in_Game` 1000 ms, `C2E2475B` 500 ms) and 24 game parameters, `Camera_Zoom` 50, `Time_in_Game` 5, the user volumes 50
or 100 ([audio.md](../audio.md#game-parameters-and-states)).

## Buses

A bus (type 8) or aux bus (type 20): u32 parent bus (0 for the two top buses), a property list without ranges (ids `1A`, `1B`, `1C`, `1F`:
0, 100, 0, 100 on every bus, Unknown; and on three buses `05`, the **bus volume** in dB: +6 footsteps, −4 music, −9 on the outdoor aux
bus `5E1D37D4`), 13 bytes (Unknown), f32 maximum duck volume, u32 duck count (0 in Kenshi), u8 effect count and, if any, u8 bypass bits then per
effect u8 slot, u32 effect id, u8 is-share-set, u8 is-rendered; 6 bytes (Unknown); the RTPC list (each category bus's slider curve, **Observed**,
listed in [audio.md](../audio.md#volumes)); u32 state group count (0). **Verified**: exact size for all 15 buses (11 buses, 4 aux buses).
Sounds reach a bus through the nearest node with an override bus id (in the biome banks the top actor-mixer goes to the SFX bus
`59927CD0` and the one below it to the ambience bus `95836E4C`).

**Aux buses** (**Observed**, `Init.bnk`, `Reverb_*` banks): indoor `23F4785D` has slot 0 = RoomVerb `21C5CB35` (big room) and slot 1 = `1284F30B`
(small room), both bypassed at start (bypass bits 3); outdoor `5E1D37D4` has slot 0 = `3C6A9ABC`, bus volume −9 dB, bypassed at start.
The game switches them with Bypass-effects actions (scope 2): every biome `Enter_*` event unbypasses outdoor slot 0 (mask 01);
`Enter_Building_Big` (`AD014E1F`) unbypasses indoor slot 0 and bypasses outdoor (mask 0x11 = all slots); `Enter_Building` (`C26421B4`) unbypasses
indoor slot 1 and bypasses outdoor; `Leave_Building` (`704C22D9`) bypasses indoor and unbypasses outdoor.

## Attenuation (14)

Used by the nodes whose positioning names an attenuation id. u8 cone flag; if set, 5 f32 (inside angle, outside angle in degrees, outside volume dB,
low-pass, high-pass). Then 7 u8 curve indices (by usage: 0 dry volume, 1 game-defined aux send, 2 user-defined aux send, 3 low-pass, 4-6 unused,
0xFF = none), u8 curve count, per curve u8 scaling, u16 point count, points (f32 x = distance, f32 y, u32 interpolation); then the RTPC list.
**Verified**: exact size for all 14 attenuations. Volume curves have scaling 2 and are read as linear gain − 1, like the RTPC curves (**Observed**); the
low-pass curve y is 0..75. Kenshi's maximum distances are 400..1000 units. Two attenuations (`0FDAD054`, `2E5501C5`) have a cone of 90°/245° and −6 dB,
which the game cannot use: it sets no orientation on emitters.

## Effects (18 and 19)

Effect (19) and effect share set (18): u32 plug-in id, u32 parameter size, the parameters, u8 media count (0), the RTPC list, u16 0. **Verified**:
exact size for all 11 (6 in `Init.bnk`, 3 more in the reverb banks). Plug-ins in the banks: `0x00760003` RoomVerb (`1284F30B` 0.9 s decay, `21C5CB35`
3.2 s, `3C6A9ABC` outdoor 1.9 s, share sets `769EF412`, `02E5E37F`), `0x00690003` parametric EQ (`1E402603`, `2CB4FF60`), `0x006C0003` compressor (`395A8EFF`).

**RoomVerb parameters** (**Observed**, from the values; 20 f32 then bytes): 0 decay time (s), 1 high-frequency damping (kHz), 2 diffusion (%), 3 stereo width
(degrees), 4-12 three filter bands (gain dB, frequency, Q), 13-15 front, rear and centre level, 16 dry level (−96.3 dB), 17-19 levels in dB: 17 is 0
and 18 and 19 are the two wet levels (small indoor −7 / −28, big indoor −16 / −21, outdoor −25 / −20). **Unknown**: the trailing bytes (byte 80 = 1, a
pattern id 23 / 9 / 29, millisecond values such as 34 / 6 / 11 / 67 / 79 or 123 / 95 / 53 / 62), which of the two wet levels is early reflections
and which the tail. The outdoor effect has three RTPCs on `Camera_Zoom` for parameter ids 16 (a 25.6 .. 175.4 curve, read as pre-delay ms,
**Unknown**), 60 and 62 (dB curves falling to about −1 at zoom 100, so the reverb fades as the camera rises).

## How an ambience bank is built (Observed, e.g. `SFX_Amb_Desert`)

`Enter_Desert_Day` = stop all of `3A626E51` (the main theme: the music random/sequence container of `MT_Music_Bank`),
play a **layer container** holding one looping sound (a long stereo bed, `loop=0`), play a **random container** of short one-shots
(random volume -55..0 dB and pitch, positioned 3D along paths around the listener, transition 1000 ms, avoid repeating the last 4-6),
stop the night versions, and set the effect bypass of aux bus `5E1D37D4` (the outdoor reverb, in `Init.bnk` and `Reverb_Outdoor_Bank`). `Enter_Desert_Night` is the same with the night bed and one-shots.
All of these fade over 10 s (`0x0F` = 10000). `Leave_Desert` stops the biome's top mixer. Ashlands and Coast also have switch
containers on `Time_in_Game` (`Day` / `night` / `none`). The weather bank: `Weather_Rain_Start` plays a layer container of three looping
rain beds plus a random container of extra layers; `Weather_Wind_Start` plays two looping wind beds; the ends stop their mixers.

## Embedded media (WEM)

A WEM is RIFF/WAVE. **Verified** for the ambience banks (every WEM decodes in `Every_ambience_WEM_decodes_to_exactly_its_sample_count`): a 66-byte `fmt ` chunk with codec tag
`0xFFFF` (Wwise Vorbis), then directly a `data` chunk: there is no separate `vorb` chunk, so the Vorbis setup values are in the extended
`fmt `. The 292 ambience WEMs are 44100 Hz, 252 stereo and 40 mono (Observed with `meitou-tools audio <bank> --media`). Three tiny
ones in `SFX_Amb_Indoors_Bank` (`12C0F32E`, `0888326E`, `34EC0D31`, 461 to 1625 bytes) are encoded silence of 1, 5 and 10 s, the
gaps of the interior random containers (**Verified**: they decode to exact zeros).

## Wwise Vorbis

Reader: `Meitou.Data.Audio.WwiseVorbis` (WEM → standard Ogg Vorbis, which NVorbis, MIT, decodes). `meitou-tools audio <bank> --decode`
decodes a bank, `--wav <folder> [id]` writes WAV files.

**Verified**: every WEM in all 24 banks (2,786, about 5.5 hours, including `SFX_Bank`'s media) converts and decodes to exactly its sample
count (`--decode` over each bank; the ambience banks in `Every_ambience_WEM_decodes_to_exactly_its_sample_count`). A wrong setup layout
or codebook would make the decoder reject the stream; this does not show whether the window flags are right (see Audio packets).
Speed: about 200× real time on one core (Release).

### The `fmt ` chunk (66 bytes)

| Offset in `fmt ` | Field |
|---|---|
| 0x00 | u16 codec `0xFFFF` |
| 0x02 | u16 channels |
| 0x04 | u32 sample rate |
| 0x08 | u32 average bytes per second (× 8 is the nominal bitrate of the rebuilt identification header) |
| 0x0C, 0x0E | u16 block align, u16 bits per sample: 0 |
| 0x10 | u16 extra size: 48 |
| 0x12 | u16 0 |
| 0x14 | u32 channel mask (3 for stereo) |
| 0x18 | u32 **sample count** (frames the sound plays) |
| 0x1C | u32, equal to the first audio packet offset in the files looked at (Unknown) |
| 0x20 | u32, equal to the `data` chunk length in the files looked at (Unknown) |
| 0x24 | u32 (Unknown) |
| 0x28 | u32 **setup packet offset** in `data` (0) |
| 0x2C | u32 **first audio packet offset** in `data` |
| 0x30 .. 0x3B | three u32 (Unknown; perhaps packet size bounds) |
| 0x3C | u32 uid (the rebuilt stream's Ogg serial) |
| 0x40 | u8 log2 of the short block size (8: 256) |
| 0x41 | u8 log2 of the long block size (11: 2048) |

### Packets

The `data` chunk holds packets, each a u16 size and then the packet, with no granule positions. The first is the setup packet, the rest
are audio. There is no identification or comment packet: the converter writes both (from channels, rate, bitrate and block sizes).

### The setup packet

Bit-packed as Vorbis (least significant bit first): a shortened Vorbis setup header (**Verified**: rebuilt as below, every setup packet
in every bank is used up to within its last byte, which the converter checks, and the result decodes):

- No packet type and `vorbis` signature.
- u8 codebook count - 1, then per codebook a **10-bit index into the codebook library** (below) instead of the codebook.
- No time-domain transform list (the converter writes one entry of type 0).
- Floors: 6-bit count - 1, per floor the floor-1 fields of Vorbis without the 16-bit floor type (all are type 1).
- Residues: 6-bit count - 1, per residue a **2-bit** residue type instead of 16 bits, then the Vorbis fields.
- Mappings: 6-bit count - 1, per mapping no 16-bit type (all type 0), then the Vorbis fields.
- Modes: 6-bit count - 1, per mode a 1-bit block flag, no window or transform type (both 0), the 8-bit mapping.
- No framing bit.

### Packed codebooks

A library entry is a codebook in a packed form of the Vorbis layout (**Verified**: all 598 parse and fill their span as below, and the
expanded codebooks decode):

- No `BCV` sync. u4 dimensions (Vorbis: 16 bits), u14 entries (Vorbis: 24), u1 ordered.
- Ordered: u5 initial length - 1, then the Vorbis run counts.
- Not ordered: u3 bits per codeword length (1..5), u1 sparse, then per entry (after a u1 present flag when sparse) the length - 1 in that
  many bits (Vorbis: 5 bits).
- u1 lookup type (Vorbis: 4 bits; so only 0 and 1). Type 1: u32 minimum, u32 delta, u4 value bits - 1, u1 sequence, then the
  `lookup1_values` multiplicands as in Vorbis.
- Each takes `bits / 8 + 1` bytes: a whole extra byte when it ends on a byte boundary.

### The codebook library

The library is part of the game's Wwise decoder in `kenshi_x64.exe`: a table of 599 increasing 64-bit pointers in `.data` (598
codebooks and the end) into one block of packed codebooks in `.rdata` (about 72 KB). `WwiseCodebookLibrary.FromExecutable` finds it in any non-code section by
that shape (a run of 200+ increasing pointers at most 8000 bytes apart) and accepts it only if every entry parses to exactly its span.
So it is read from the user's install, never shipped, and no exe build's offsets are hardcoded. **Verified** on the Steam exe
(36,718,592 bytes): one such table, 598 codebooks (**Observed**; every ambience WEM decodes against it). Other builds (GOG, older
versions): **Unknown**. A GOG install has no `kenshi_x64.exe` (**Observed** in a GOG user's log); its exe is taken to be `kenshi_GOG_x64.exe` (**Unknown**: the
name GOG's build is known by, not checked on a GOG install), so `GameInstall.Executable` takes the first of the two names that exists. A failure logs why: the exe missing, or its size, sections and longest pointer run.

### Audio packets

A Vorbis audio packet without its first bit (the packet type, 0) and, for a long block, without the previous/next window flags. The
converter writes the type bit, copies the mode number (`ilog(modes - 1)` bits), for a long block writes the flags from the neighbouring
packets' modes (previous = the previous packet is a long block, next = the next one is), then copies the rest. Granule positions are
rebuilt from the block sizes (each packet after the first adds a quarter of its own and of the previous block size), and the last page
gets the sample count so the decoder trims the end.

The flags follow the Vorbis definition (the encoder writes exactly the neighbours' block types), but no signal test singles them out:
at long/short transitions, forcing every flag to "short" gives identical output (both reconstruct completely there), while forcing them
to "long" or swapping them changes it, and a mismatched window both attenuates and aliases, so energy measures do not decide.
**Observed**, and confirmed by listening (2026-10-10: the owner heard the decoded beds and one-shots clean, no clicks).

## Version 88 (`SFX_Bank`)

The sections and object headers read the same way. The object bodies differ: e.g. a sound's source has a u32 where version 112 has
the u8 stream type (the source id is 3 bytes later). Not decoded; **Unknown**.
