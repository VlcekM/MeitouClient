# Settings

Reference for settings.cfg, kenshi.cfg and options-screen bindings. **Verified** where original
exe loader/writer/UI/schema and install values agree; Observed/Unknown entries retain their
scopes. Complete key/type/default/clamp/effect/restart tables below are authoritative.
[Input](../game/ui-input.md), [camera](camera.md), [rendering](../viewer.md).

## Loading and applying

Keep distinction between missing-key default, persisted value, loader clamp and UI slider range.
Not every UI option applies immediately; restart requirements are per key. Do not silently
interpret an unknown field from its label. settings.cfg user options and kenshi.cfg engine
terrain/chunk configuration are separate stores. meitou.user.json is the engine's own config;
its graphics/bindings must not be confused with original files.

Terrain detail/chunk size/LOD test, texture quality (mip skipping), water reflection level/range,
shadows, decals, distance controls, audio and gameplay keys drive different subsystems. A lower
texture quality changes loaded mips, not anisotropic filtering or screen resolution. Reflection
levels determine included classes, not just target size. Shadow mode and quality choose different
pipelines/map dimensions. Meitou honors documented keys where implemented; unsupported settings
remain unsupported rather than guessed from UI wording.

### Graphics: view and window

| Key | Type, range | Missing → | Options label | What it does | Restart |
| --- | --- | --- | --- | --- | --- |
| `view distance` | float; slider 1500 … 12000 | 5000; **< 500 → 6000** | View Distance | D = value × 10 is the main camera's far clip, the atmosphere haze distances and the water reflection's far clip base; see [sky.md](sky.md), [camera.md](camera.md). Also Ogre's (unused) `setFog` at 0.8 D / 0.96 D (FUN_140815b40) | No (slider applies live) |

### Graphics: terrain

| Key | Type, range | Missing → | Options label | What it does | Restart |
| --- | --- | --- | --- | --- | --- |
| `terrain detail` | float 0 … 1 (slider) | 1.0 | Terrain Detail | Near LOD threshold T<sub>near</sub> = (1 − value) × 30 + 5 pixels-equivalent: 5 at 1, 35 at 0. Lower T = finer terrain | No (slider applies live) |
| `terrain distant` | float 0 … 1 | 0.0 | none (file only) | Far threshold T<sub>far</sub> = (1 − value) × 30 + 5: 35 by default | Applied at start and with the two sliders above |
| `terrain threshold` | float | 750 | none (file only) | Distance D<sub>t</sub> = value × 10 (7500 by default) where T<sub>near</sub> starts blending to T<sub>far</sub> | as above |
| `terrain patch size` | int 0 / 1 | 1 | Terrain Chunk Size (need restart): Small = 0, Large = 1 | Terrain patch grid 16 (0) or 64 (1) quads per side | **Yes** |
| `terrain hi-res distance` | int | not read | none | **Never read**: only the startup defaults writer appends `terrain hi-res distance=400` (Verified: the only reference to the string is that literal) | — |

### Graphics: textures

| Key | Type, range | Missing → | Options label | What it does | Restart |
| --- | --- | --- | --- | --- | --- |
| `texture resolution gimping` | int 0 … 4 | 1 | Texture Quality (need restart): Maximum = 0, High = 1, Medium = 2, Low = 3, Fugly = 4 | Drops top mip levels of DDS textures as they are loaded; sets the texture memory budget | **Yes** (applied at resource load) |

### Graphics: water reflection

| Key | Type, range | Missing → | Options label | What it does | Restart |
| --- | --- | --- | --- | --- | --- |
| `water reflection` | int 0 … 4 | 2 | Water reflection: Disabled = 0, Landscape = 1, Characters = 2, Buildings = 3, Everything = 4 | What the 512² reflection camera draws; 0 removes the reflection | No for 1 … 4; 0 also changes the water shaders (below) |
| `reflection range` | float | 0.6 | none (file only) | Reflection camera far clip = `view distance` × 10 × value | Only applied by FUN_1403e7490 (see below) |

**What each level draws** (**Verified**: `ReflectionTextureListener` pre/post render, FUN_1409d9c70 / FUN_1409d9d60; the
`SceneManager` slots 135 to 138 checked against OgreMain's RTTI: `addSpecialCaseRenderQueue`, `clearSpecialCaseRenderQueues`,
`setSpecialCaseRenderQueueMode`): before the reflection target renders, the listener adds render queues to a special-case list
and sets it to **include only** those; afterwards it clears the list and sets the mode back to exclude (with an empty list,
i.e. everything).

| Level | Queues included | Meaning (Observed from the labels and other docs) |
| --- | --- | --- |
| 0 | none | nothing drawn (and the pass is switched off, below) |
| 1 Landscape | 5, 6, 7, 8, 25 | SkyX's sky queues 5 to 8 ([clouds.md](clouds.md)) and the terrain (**Verified**: FUN_140874930 calls the terrain's `setRenderQueueGroup(25)`, `MovableObject` slot 10 in OgreMain's RTTI) |
| 2 Characters | + 50 | characters |
| 3 Buildings | + 20 | buildings |
| 4 Everything | no list | the listener returns before setting "include", so the exclude mode with an empty list draws every queue |

### Graphics: decals and effects

| Key | Type, range | Missing → | Options label | What it does | Restart |
| --- | --- | --- | --- | --- | --- |
| `Blood` | bool | 1 | Blood | Gates blood effects ([character-stats.md](../game/character-stats.md)); also read by the decal code (FUN_1408df7d0) and the blood decal sources ([blood-decals.md](../game/blood-decals.md)) | No |
| `Decal Range` | float (written as int); slider 0 … 5000 | 1000 | Decal Range | Radius passed to `Terrain::getBloodPatches(range, …)`: the terrain patches within it get blood-decal textures (FUN_1408dd860) | No |
| `Decal Resolution` | int 512 / 1024 / 2048 | 1024 | Decal Resolution: 512, 1024, 2048 (the item data are the sizes) | Side of each `Kenshi_Decal_Texture_<n>` render texture; changing it removes and re-creates them (FUN_1408de890, run when the options close) | No |
| `harpoonLimit` | int (stored as float); slider 10 … 500 | 60 | Harpoon limit | Most harpoons / arrows left stuck in the world; adding one beyond it removes one (FUN_14043acf0) | No |
| `skip stencil tricks` | bool | forced 0 | none | **Not read from the file**: the loader always sets 0 (the 16-bit store at +0x40 writes 0 here and 1 into the next byte); the writer writes the in-memory value. When 0, buildings with an `interior mask` mesh use it (FUN_140561d10) | — |

### Foliage and distances

| Key | Type, range | Missing → | Options label | What it does | Restart |
| --- | --- | --- | --- | --- | --- |
| `grass range` | float; slider 0 … 3 | 1 | Grass View Range | Grass page range multiplier ([foliage.md](foliage.md#materials-sizes-and-grass)); read by the per-zone set-up FUN_1406d3e30 | Zones loaded afterwards |
| `grass density` | float; slider 0 … 2 | 1 | Grass Density | Multiplies grass candidates per page (FUN_1406d0340) | Zones loaded afterwards |
| `foliage range` | float; slider 0.1 … 4 | 1; **≤ 0 → 0.1** | Foliage View Range | Mesh foliage page range multiplier (FUN_1406d3e30); a zone gets foliage only when it is above 0 (FUN_1409f9560), which the clamp always makes true | Zones loaded afterwards |
| `objects view range` | float; slider 1000 … 8000 | 3000; **< 1000 → 1000** | Objects View Range | Rendering distance of building parts ([zones.md](zones.md#placement-files-and-assembly); FUN_140558b40, FUN_14055f6e0) | Objects created afterwards |
| `npc range` | float; slider 1000 … 8000 | 3250; **< 1000 → 3000** | NPC View Range | Character relevance radius ([game-loop.md](../game/game-loop.md)) | Yes: the character draw distance (`GameplaySettings.NpcRange`, from the eye; the in-view flag and the race `+0x78` factor are not modelled). The start value; the Graphics > Characters slider `Character draw distance` (the game's range, steps of 50, saved in `meitou.user.json`) overrides it, also for the simulation's on-screen test |
| `feature range` | int, stored as float; slider 0 … 8 | 2; **clamped 0 … 6** | Terrain Feature Range | Square of ±N zones (64 × 64 zone grid) around the camera's zone that map features load for (FUN_140a098e0) | No |
| `distant town range` | int, stored as float; slider 0 … 8 | 6; **clamped 0 … 10** | Distant Towns Range | Square of ±N zones around the camera's zone that distant towns show for (FUN_140a0b340) | No |
| `generate distant towns` | bool | 0 | Generate Distant Towns | Build distant meshes for towns without a baked one ([zones.md](zones.md#placement-files-and-assembly); FUN_1400d8350) | Towns loaded afterwards |

### Camera and controls

| Key | Type, range | Missing → | Options label | What it does |
| --- | --- | --- | --- | --- |
| `camera speed` | float; slider 150 … 1000 | 500 | Camera Move Speed | Pan speed ([camera.md](camera.md)) |
| `mouse speed pan` | float; slider 2 … 50 | 6 | Camera Rotate Speed X | Rotation speed (+0x04) |
| `mouse speed tilt` | float; slider 2 … 50 | 6 | Camera Rotate Speed Y | Tilt speed (+0x08) |
| `camera zoom` | float; slider 10 … 200 | 125 | Camera Zoom Speed | Zoom step speed (+0x0C) |
| `invert X` | bool | 0 | Invert mouse X | Stored as a ±1 factor at +0x10 |
| `invert Y` | bool | **1** | Invert mouse Y | Stored as a ±1 factor at +0x14 (inverted by default) |
| `swap mouse buttons` | bool | 0 | Swap mouse buttons | Tooltip: swaps left and right on the GUI. **Verified** (OIS press and release callbacks `FUN_14082a090` / `FUN_14082a1e0`, global `DAT_14213346b`): the button is swapped (0 and 1 trade) once, before both the key code posted to the game's input (`(button + 1) * 0x1000`) and MyGUI's inject; so the game view swaps too, as Meitou does (Controls page, `KeyMap.SwapButtons`) |
| `hardware_mouse` | bool | 0 | Hardware Mouse | Windows cursor instead of the drawn one; the tooltip says it needs a restart |
| `Edge scrolling` | bool | 1 | Edge scrolling | Camera moves when the pointer is at the screen edge |
| `mouse speed horizontal` | float | 1.5 (see text) | none | Scales the raw mouse x movement |
| `mouse speed vertical` | float | 1.5 (see text) | none | Scales the raw mouse y movement |

### UI and general

| Key | Type, range | Missing → | Options label | What it does |
| --- | --- | --- | --- | --- |
| `Show names` | bool | 1 | Show character names | Names above the player's characters |
| `Movement marker` | bool | 1 | Show movement marker | The green marker where you click (Meitou: Interface page, `UserConfig.MovementMarker`) |
| `Rotation marker` | bool | 1 | Show rotation marker | The green marker while rotating the camera (Meitou: Interface page, `UserConfig.RotationMarker`) |
| `floaters` | int 0 … 2 | 1 | Show damage floaters: Off = 0, Simple info = 1, Full info = 2 | Damage numbers; only shown within 4000 units (squared distance 1.6e7, FUN_1406508d0); 2 splits them by damage type. Layout: [ui-screens](../game/ui-screens.md#8-damage-floaters); stored at options +0x7c |
| `font size2` | int (stored as float); slider −10 … 32 | 3 | Font Size | GUI font size: added in points to every font's MyGUI default height ([mygui.md](mygui.md)) (Meitou: Interface page, `UiPainter.FontSizeSetting`) |
| `tutorials` | bool | 1 | Tutorials | In-game tutorials ("Reset Tutorials" is a button, not a key) |
| `censorship` | bool | 0 | Language censorship | Filters swearing in dialogue (FUN_14067e8b0) |
| `language` | string, locale code | see text | none in options | The locale folder `locale/<code>/` used for translations; empty falls back to `steam_language`, unknown is `en_GB` ([localisation](localisation.md)). Meitou reads it; the launcher's Language setting writes this line, nothing else of the file ([launcher](../launcher.md)) |
| `steam_language` | string, Steam language name | see text | none | The Steam language the `language` was last chosen from; decides when `language` is empty (**Verified**, FUN_140175a90) |

Meitou adds an **Interface scale** slider (Interface page, 50 … 200 %, default 100 %; `UserConfig.InterfaceScale` in `meitou.user.json`, no `settings.cfg` key; the game has none). The UI scale is height / 1080 × it: fonts, template pixel sizes, every skin piece (their offsets and tile sizes are fixed pixels in the files; Meitou scales them in both modes) and Meitou's own panels follow it, and a window as large as its layout says (the window host's roots, the save/load window) grows by the setting. Panels placed by screen fractions inside a full-screen layout do not grow, so above 100 % their text can outgrow them.

### Audio

Floats 0..1, missing → 0.5, applied when the options close (FUN_1403e7290). Each becomes a Wwise game parameter × 100 (`User_Volume_Music`,
`User_Volume_Ambient_SFX`, `Footstep_Volume`, `User_Volume_SFX`, `User_Volume_UI`, `User_Volume_VO`; also at start, FUN_140259620), turned
into bus gains ([audio.md](../audio.md#volumes)). Meitou applies them at once from the Audio page of its settings menu, kept in `meitou.user.json` (first run: this file); the viewer reads `settings.cfg` once.

| Key | Options label |
| --- | --- |
| `Music volume` | Music volume |
| `Ambient volume` | Ambient volume |
| `Footstep volume` | Footstep volume |
| `Sfx volume` | SFX volume |
| `VO volume` | VO volume |
| `UI volume` | UI volume |
| `Music frequency` | Music Frequency (minutes); float, slider 1 … 20, missing → 5 |

### Gameplay

| Key | Type, range | Missing → | Options label | Details |
| --- | --- | --- | --- | --- |
| `Squad size multiplier` | float; slider 0.5 … 3 | 1 | Squad size multiplier | [factions-squads-towns.md](../game/factions-squads-towns.md) |
| `Global population multiplier` | float; slider 0.5 … 4 | 1 | Global population multiplier (num NPC squads) | same |
| `raidSizeMult` | float; slider 0.25 … 4 | 1 | Town raid events: size | same (FUN_1409c5ad0) |
| `raidFrequencyMult` | float; slider 0.25 … 3 | 1 | Town raid events: frequency | same (FUN_1409c5740) |
| `attacks` | int 0 … 5 | 2 | Town attacks frequency: Bombardment = 0, High = 1, Normal = 2, Fewer = 3, Rare = 4, Never = 5 | A faction's wait between passing-by assaults on the player's towns, uniform, in game hours (**Observed**: the elapsed-time helper `FUN_140791bf0` read as hours): 0, [6, 48), [32, 96), [72, 144), [130, 188); 5 stops them (`FUN_140286a70`, `FUN_140290450`; [ai.md](../game/ai.md#package-classes)) |
| `Dismemberment` | int 0 … 2 | 1 | Dismemberment: Never = 0, Rare = 1, Frequent = 2 | The options page binds the drop-down to the global `DAT_142133504` (`FUN_1403f0260`), which the hit, degeneration and eaten code read when a part is damaged (`FUN_14064f300`, `FUN_14064f5c0`, `FUN_140650370`, `FUN_14029af90`), so a change applies at once (**Verified**, decompile). [combat.md](../game/combat.md), [character-stats.md](../game/character-stats.md) |
| `civilians` | bool | 1 | none | Loaded and saved; **no reader found** (no instruction addresses its global other than the writer; an access through the block's base pointer would not show up, so **Unknown**) |
| `Fast zone hopping` | bool | 0 | Fast zone hopping | Keeps a larger area loaded (Meitou: Gameplay page, applies to the running world)  so switching between distant characters skips loading: a ring of one more zone round the box of each player character ([character-stats](../game/character-stats.md#corpses), FUN_140a0f640; storage `DAT_142133518`, **Verified**) |
| `Auto save` | bool | 1 | Auto save | [save.md](save.md#autosave-fun_14047be70) |
| `Auto save time (minutes)` | float; slider 1 … 30 | 10 | Auto save time (minutes) | same |
| `User save location` | bool | 1 (see save.md) | User Save Location | Saves in `%LOCALAPPDATA%\kenshi\save` (1) or the install (0); [save.md](save.md) |
| `continue` | string | — | none | Save the "Continue" button loads; written after every save ([save.md](save.md)) |
| `autosaveindex` | int | 0 | none | Rotating autosave slot ([save.md](save.md#saveload-contract)) |
| `editor` | string, a mod name | none | none (level editor) | Written when the in-game level editor saves a mod ("Save Mod", FUN_140777ce0); read when the editor opens to preselect that mod in its mod list (FUN_14077be90; an unknown name selects the first). Absent from the install's file (the editor was never used) |

### kenshi.cfg (Ogre's config)

`kenshi.cfg` is Ogre's own configuration file: the renderer set-up FUN_140818970 references `Plugins_x64.cfg` and `kenshi.cfg`
(**Observed**: the strings, presumably the `Ogre::Root` constructor's plugin and config file names; the call was not traced), and
the game calls `Root::restoreConfig` / `saveConfig` (**Verified**), which use that config file. Format: `Render System=<name>` then a
`[<render system>]` section of that system's options. If `restoreConfig` fails the game picks `Direct3D11 Rendering Subsystem`
(or the first available renderer) and saves (FUN_1408149a0).

| Key (section `[Direct3D11 Rendering Subsystem]`) | Install value | Who sets it |
| --- | --- | --- |
| `Render System` (top) | Direct3D11 Rendering Subsystem | launcher (the start-up dialog, FUN_140129190 also knows Direct3D9) |
| `Rendering Device` | the GPU name | launcher |
| `Video Mode` | `1920 x 1080 @ 32-bit colour` | launcher; options "Resolution (need restart)" |
| `Full Screen` | No | launcher; options "Full screen (need restart)" (Yes / No) |
| `Border` | None | launcher "Borderless"; options "Borderless (need restart)" (None = borderless, otherwise Default) |
| `VSync` | No | launcher |
| `VSync Interval` | 1 | Ogre's option, not touched by Kenshi's code (**Observed**: no string reference) |
| `FSAA` | 1 | Ogre's option; 1 = no multisampling ([post-processing.md](post-processing.md)) |
| `Floating-point mode`, `Backbuffer Count`, `Driver type`, `Allow NVPerfHUD`, `Information Queue Exceptions Bottom Level`, `Min/Max Requested Feature Levels`, `sRGB Gamma Conversion` | as shipped | Ogre's D3D11 render-system options; Kenshi's code does not reference them (**Observed**: none of the names is a string in the exe) |

### What the Meitou viewer does with these

| Setting | Viewer |
| --- | --- |
| `view distance` | Emulated: the sky haze uses the constant 5000 (`KenshiHaze.ViewDistanceSetting`); the heat haze's depth scale uses 12000 (`HeatHaze.ViewDistanceSetting`, the install's value since 2026-10-06; the `--heat-haze-view-distance` option was removed); the camera's far plane is the viewer's own (a constant 450000, raised with the haze at height; the `--view-distance` option was removed) |
| `shadow quality`, `Shadow Range` | Honoured as options: `--shadow-quality` (index into the same table), `--shadow-range` (clamped 1000 … 9000 in Faithful; Meitou shadows default to 10000 and accept 1000 … 15000); `--shadow-range 0` (or the Tab slider's left end) turns the sun shadows off |
| `shadow mode` | Not honoured: the viewer always runs its CSM-style pass (Faithful or Meitou), never RTW |
| `foliage range`, `grass range`, `grass density` | Emulated with sliders / environment variables (`MEITOU_FOLIAGE_RANGE`, `MEITOU_GRASS_RANGE`, `--grass-density`); default foliage range ×4 and grass range ×8, not the game's 1 |
| `objects view range`, `feature range`, `distant town range` | Constants in `ObjectRanges`; the viewer's own `--object-distance` (default 20000 in Meitou, 12000 in Faithful; the landmark distance is a fixed 7.5 times it in Meitou) and `--distant-range` replace them |
| `FXAA`, `HeatHaze` | Honoured as options (`--no-fxaa`, `--no-heat-haze`; Faithful switches) |
| `camera speed`, `camera zoom`, `invert X`, `invert Y` | Controls page (Camera move speed 150 … 1000, Camera zoom speed 10 … 200, Invert mouse X / Y), kept in `UserConfig`, first taken from settings.cfg (`UserConfig.SeedCamera`), applied to `CameraSettings` (`ApplyCamera`) |
| `water reflection`, `reflection range` | Honoured as options (2026-10-06): `--water-reflection <0..4>` (viewer default 4; the game's missing-key default is 2) and the reflection range (viewer default 3; the game's 0.6; Tab slider only, the `--reflection-range` option was removed); `--water-reflection 0` / `R` are the on/off switch (`--no-reflections` was removed). See "Viewer: water reflection" below |
| `terrain detail`, `terrain distant`, `terrain threshold`, `terrain patch size` | Not honoured, deliberately (2026-10-06): the viewer does not replicate the game's per-node rule above. Its CDLOD (64-quad patches) uses its own resolution-aware screen-space error, measured in pixels of the picture actually rendered (after the upscaler's render scale): `--terrain-error <px>` (default 10, Tab slider "Terrain detail"), no far ramp (the `--terrain-far-error` and `--terrain-ramp` options were removed). See [terrain.md](terrain.md#lod-water-and-sun) |
| `texture resolution gimping` | Honoured as `--texture-quality <0..4>` and, in the game, the saved Tab slider (applied before the world loads; the option and `--low-end` win). Missing: 0, full size (the game's missing-key default is 1). Object/foliage/character textures reload when the slider moves; the terrain layer arrays take one size, `2048 >> level` (the `Landscape` drop, [dds.md](dds.md) "Terrain layer textures"), at start, so terrain needs a restart like the game. 133 texture pairs (BC3 + BC1, full mips), all allocated up front: 1064 / 266 / 67 / 17 / 4 MiB at levels 0 … 4 |
| `Blood`, `Decal Range`, `Decal Resolution` | Honoured by the game (read once at start from `settings.cfg`; restart to change): blood decals on the terrain, [blood-decals.md](../game/blood-decals.md). Not in the viewer |
| `harpoonLimit` | Honoured: seeded from the file into the Gameplay settings (clamped 10 … 10000, default 60; the game's own slider stops at 500) and kept in `meitou.user.json`; the slider "Harpoon limit" is on the Advanced page and acts on the next bolt that sticks (`CombatSystem.StuckLimit`) |
| `generate distant towns` | Not honoured: towns without a baked mesh are drawn from their buildings' distant meshes |
| `mouse speed pan`, `mouse speed tilt` | The game's Controls page has one "Camera rotation speed" slider (2 … 50, default 6) setting both; kept in `meitou.user.json`, `settings.cfg` not read |
| `Squad size multiplier`, `Global population multiplier`, `raidSizeMult`, `raidFrequencyMult`, `attacks`, `Dismemberment` | The game's Gameplay settings page, same ranges and defaults; kept in `meitou.user.json` (first run: this file). All reach the running world at once (the population ones act on squads, raids and assaults made afterwards; `Dismemberment` on the next part damaged, through the world's `BodyOptions`) |
| `npc range`, `mouse speed horizontal` / `vertical`, `invert *`, UI, audio, other gameplay keys | Not applicable / not implemented |

### Viewer: water reflection

| Level | Game | Viewer |
| --- | --- | --- |
| 0 | no pass; water shaders built with `NO_REFLECTION` | no pass; the water's `uReflect = 0` path: the sky colour is reflected unattenuated (**Observed** to be the counterpart of the game's reflection factor 1; the viewer's shader is not the game's, so the look is not compared) |
| 1 | queues 5-8, 25 | sky and terrain |
| 2 | + queue 50 | + characters (**Observed** from the label) |
| 3 | + queue 20 | + every placed object (buildings and map features are not told apart: **Unknown** which queue the features use) |
| 4 | no list: everything | + foliage (trees, bushes, rocks; no grass, as before). Which queue the game's foliage is in is **Unknown**; "everything" is assumed to be the only level that includes it |

## Unknowns

- (Resolved 2026-10-06: K in the terrain LOD metric is the viewport height and the node error a height in world units, see
  "Graphics: terrain".) Still open there: which way round the terrain vertex shader applies the per-patch morph factor.
- What town display state 4 is (set for reflections at levels 3 and 4), and when the water programs are rebuilt after
  `water reflection` changes to or from 0.
- The reflection camera's far clip before FUN_1403e7490 first runs (Ogre's default assumed).
- Whether anything reads `civilians`.
- In-session effect of `feature range` 7 or 8 (above the loader's clamp).

## Expression reference

- The file and how it is read: `key=value`; `key=`; `=`
- Graphics: terrain: `terrain hi-res distance=400`; `t = clamp((distance − D_t) / D_t, 0, 1)`
- kenshi.cfg: `Render System=<name>`
- Viewer: water reflection: `uReflect = 0`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- The file and how it is read (Verified: FUN_1403e84f0 and its helpers)
