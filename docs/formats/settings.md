# Settings: `settings.cfg`, `kenshi.cfg` and the options screen

Every key the game reads from or writes to `settings.cfg`, and what it touches in `kenshi.cfg` (Ogre's own config file): type,
range, the default when the key is missing, the options-screen label, what the engine does with it, whether it needs a restart,
and whether the Meitou viewer honours it.

Sources (read for facts only; nothing decompiled is in the repo): in `kenshi_x64.exe` (1.0.68) the settings loader FUN_1403e84f0,
the settings writer FUN_1403eca90 (the naming pass calls it `Foliage_WriteSettings`; it writes every key, not only foliage), the
options screen FUN_1403f0260 (tab builder) and FUN_1403ee820 (close), the startup set-up FUN_1408149a0, the language set-up
FUN_140175a90, the compositor set-up FUN_1403ea7b0, and the consumers named per key below (found through `data_xrefs.tsv` of the
global each value is stored in); `Plugin_Terrain_x64.dll` (decompiled in full for this doc: `Terrain::setDetailThreshold`,
`setPatchSize` and the quadtree LOD test); the install's `settings.cfg` (67 lines), `kenshi.cfg`,
`data/materials/compositors/compositors.cfg`, `data/materials/forward/water.hlsl` and `locale/en_GB/LC_MESSAGES/main.pot` (the
options labels, all from `options.cpp`). The labels below are the game's English strings; tooltips are paraphrased.

Related docs that go deeper on some keys: [shadows.md](shadows.md#settings-verified-exe-settings-loader-writer-and-options-screen-settingscfg),
[foliage.md](foliage.md#distances), [post-processing.md](post-processing.md) (FXAA, HeatHaze), [sky.md](sky.md) and
[camera.md](camera.md) (`view distance`), [zones.md](zones.md#draw-distance-and-distant-towns), [terrain.md](terrain.md),
[save.md](save.md), [game/ui-input.md](../game/ui-input.md#10-settingscfg-and-the-options-window),
[game/game-loop.md](../game/game-loop.md), [game/factions-squads-towns.md](../game/factions-squads-towns.md),
[game/combat.md](../game/combat.md), [game/character-stats.md](../game/character-stats.md).

## The file and how it is read (Verified: FUN_1403e84f0 and its helpers)

- Plain text, one `key=value` per line, in the install folder (next to `kenshi_x64.exe`). Keys are case-sensitive and contain
  spaces (`view distance`, `Shadow Range`); a line matches when it starts with exactly `key=` (no spaces around `=`).
- The loader runs once at start. Every value is stored in one settings block at `0x142133490` (field offsets below as `+0x..`);
  consumers read those globals directly. (**Verified** that this is the block's base: `Blood` lands at +0xE4 = `0x142133574`,
  `view distance` at +0x18 = `0x1421334A8`, and the options sliders, the writer and the consumers all use these addresses.)
- Parsers: integers with `atoi`, floats with `atof` (so `1.5` in an integer key reads as 1). **Booleans are false only when the
  value is exactly `0`** (or empty); anything else, including `false`, `No` or `0.0`, reads as true (FUN_140404250 and the
  hand-written scans compare the first character with `0` and the length with 1).
- A key that is missing gets the default in the tables below; that default is **not** written back until the options window
  closes. **Clamps** the loader applies are listed per key (e.g. `view distance` below 500 becomes 6000).
- The writer FUN_1403eca90 rewrites all keys it knows (everything in the main loader plus `skip stencil tricks` and one line per
  compositor node). It loads the file, sets each key and saves it, so lines it does not know presumably survive (**Observed**: the
  load / set / save calls, not tested with a foreign line). It runs when the options window
  closes after the options were shown (FUN_1403ee820). On that close it also applies: audio volumes and music frequency
  (FUN_1403e7290), the main camera's far clip from `view distance` (FUN_1406ae960), the shadow mode and map size
  (`setShadowMode`, [shadows.md](shadows.md)), the decal textures' size (FUN_1408de890, when decals exist), and the window settings
  into Ogre's config, saved to `kenshi.cfg` (`Full Screen`, `Border`, `Video Mode`; see [kenshi.cfg](#kenshicfg-ogres-config)).
- Other writers: the language set-up writes `language` / `steam_language`; every save writes `continue`; the autosave writes
  `autosaveindex`; the startup set-up appends three defaults (`mouse speed vertical/horizontal`, `terrain hi-res distance`) when
  `mouse speed vertical` is missing; the level editor writes `editor`.
- Every function that names `settings.cfg` (Verified: the exe's string references) is covered here: the loader, the writer, the
  start-up set-up, the two language functions (FUN_140175a90, FUN_1401753e0), the compositor set-up, the save code
  (FUN_14047be70, FUN_14047c310), the level editor (FUN_140777ce0, FUN_14077be90), the crash-dump packer (FUN_140744900, which
  only adds the file to the dump), and the CONSTANTS loader FUN_14086b2b0, which opens `settings.cfg` but reads nothing from it
  (its `production speed` and `build speed` come from the CONSTANTS record; **Verified**).

**Options-screen controls** (Verified, FUN_1403f0260 and the combo helpers): sliders are given a min and max and bound to the
global; combo boxes store each item's **data value** (FUN_1406f6f10 attaches an `int` to the item, FUN_1406fc2a0 selects the
item whose data equals the global and writes the data back), so the number in the file is the item's data, not its position.
Labels marked "(need restart)" are the game's own words. Live application: the **View Distance** and **Terrain Detail** sliders
call FUN_1403e7490 on every change (far clip, reflection far clip, terrain thresholds); the compositor checkboxes rebuild the
`Kenshi_Main` and `Kenshi_Character_Editor` workspaces (FUN_1403e7640); **Font Size** has its own handler (FUN_1403e84d0). Other
values are read by their consumers when they run, so whether a change shows at once depends on the consumer (noted per key).

## Graphics: view and window

| Key | Type, range | Missing → | Options label | What it does | Restart |
| --- | --- | --- | --- | --- | --- |
| `view distance` | float; slider 1500 … 12000 | 5000; **< 500 → 6000** | View Distance | D = value × 10 is the main camera's far clip, the atmosphere haze distances and the water reflection's far clip base; see [sky.md](sky.md), [camera.md](camera.md). Also Ogre's (unused) `setFog` at 0.8 D / 0.96 D (FUN_140815b40) | No (slider applies live) |

The `+0x18` field. The install's file had 5000 when the earlier docs were written and has 12000 on 2026-10-06 (**Observed**).

Window and video settings are not in `settings.cfg`: the options' "Resolution (need restart)", "Full screen (need restart)" and
"Borderless (need restart)" read and write Ogre's render-system options in `kenshi.cfg` (below).

## Graphics: terrain

| Key | Type, range | Missing → | Options label | What it does | Restart |
| --- | --- | --- | --- | --- | --- |
| `terrain detail` | float 0 … 1 (slider) | 1.0 | Terrain Detail | Near LOD threshold T<sub>near</sub> = (1 − value) × 30 + 5 pixels-equivalent: 5 at 1, 35 at 0. Lower T = finer terrain | No (slider applies live) |
| `terrain distant` | float 0 … 1 | 0.0 | none (file only) | Far threshold T<sub>far</sub> = (1 − value) × 30 + 5: 35 by default | Applied at start and with the two sliders above |
| `terrain threshold` | float | 750 | none (file only) | Distance D<sub>t</sub> = value × 10 (7500 by default) where T<sub>near</sub> starts blending to T<sub>far</sub> | as above |
| `terrain patch size` | int 0 / 1 | 1 | Terrain Chunk Size (need restart): Small = 0, Large = 1 | Terrain patch grid 16 (0) or 64 (1) quads per side | **Yes** |
| `terrain hi-res distance` | int | not read | none | **Never read**: only the startup defaults writer appends `terrain hi-res distance=400` (Verified: the only reference to the string is that literal) | — |

**How the three detail values act** (**Verified**: FUN_140815b40 and FUN_1403e7490 call `Terrain::setDetailThreshold(T_near,
T_far, D_t)`; the plugin's code read in full):

- At start the terrain is created (FUN_140874930) with the plugin defaults `(8, 8, 0)` and immediately overwritten from the
  settings (FUN_140815b40), so the settings apply from the start. The View Distance and Terrain Detail sliders re-apply all three.
- The plugin stores T<sub>near</sub>, the ratio T<sub>near</sub> / T<sub>far</sub> and D<sub>t</sub>. For each quadtree node it computes a metric
  `K × nodeError / max(distance, 1)` (distance from the eye to the node's box; K is a per-camera value at offset +0x14 of the
  LOD context, presumably the projection scale: **Unknown**). If the ratio is below 1 (far coarser than near), the metric is
  multiplied by `1 − t + t × ratio` with `t = clamp((distance − D_t) / D_t, 0, 1)`: the effective threshold is T<sub>near</sub> up to
  D<sub>t</sub> and ramps linearly to T<sub>far</sub> at 2 D<sub>t</sub>. A node is split while the metric exceeds T<sub>near</sub>. With
  `terrain distant` ≥ `terrain detail` (ratio ≥ 1) there is no ramp and T<sub>near</sub> applies everywhere.
- Nodes shallower than depth 4 are always split; nodes at the maximum depth never are (below). Children unused for 500 frames are
  freed (**Observed**, the frame counter test).

**Chunk size** (**Verified**: FUN_140874930 calls `setPatchSize(value ? 64 : 16)`; the plugin's `setPatchSize`): each patch is
`size + 1` vertices per side and the quadtree's maximum depth is `15 − (bits of size)`: **10 for Small, 8 for Large**
(Verified). Taking the depth as counted from 0 at the root (**Observed**: the depth field's origin was not traced, but the result
matches the heightmap's full sample spacing in [terrain.md](terrain.md)), with the terrain scale 294912 the leaf is 288 units
(Small) or 1152 units (Large), and in both cases the finest grid step is 18 units: Large
draws 16 × fewer, 16 × bigger patches over two fewer quadtree levels, which matches the tooltip (faster at long range, more video
memory per patch). Other plugin parameters are fixed: `setTerrainScale(294912, 9800)`, `setMaterialDistance(30000)` (stored
squared), `setOgreBuildLimits(250, 100)` ([terrain.md](terrain.md#terrain-lod)).

## Graphics: textures

| Key | Type, range | Missing → | Options label | What it does | Restart |
| --- | --- | --- | --- | --- | --- |
| `texture resolution gimping` | int 0 … 4 | 1 | Texture Quality (need restart): Maximum = 0, High = 1, Medium = 2, Low = 3, Fugly = 4 | Drops top mip levels of DDS textures as they are loaded; sets the texture memory budget | **Yes** (applied at resource load) |

**Mip skipping** (**Verified**: the resource-loading listener FUN_14082ddc0 and the DDS rewriter FUN_14082d840; strings read
from the exe):

- Applies to resources whose name contains `dds` in any resource group except `Overlaymaps`, `GUI`, `PLSM2` and an empty group
  name. Levels to drop N = the setting, then:
  - a name containing `_LO.` drops one fewer;
  - if N is still above 0, a name **without** `_HI.` outside the group `Landscape` drops one fewer.
  So at High (1) ordinary textures keep full size and only `_HI.` textures and `Landscape`-group textures lose one level; at
  Medium (2) ordinary textures lose one, `_HI.` / Landscape two; and so on. Maximum (0) touches nothing.
- The DDS is rewritten in memory: only block-compressed files whose FourCC is `DXT*` (so DXT1 to DXT5; `DX10` headers and
  uncompressed formats are left alone), not cube maps (caps2 bit 0x200) and not volumes (bit 0x200000). Per dropped level the top
  mip's bytes are skipped (w × h bytes, half for DXT1), width and height halve and the mip count drops by one; it stops early
  when, before a drop, fewer than 2 mips remain or a side is already below 5.
- The decal system compensates when it reads texture sizes back (FUN_1408dfaf0 shifts the reported size of `.dds` decal sources
  left by N after the `_LO.` / `_HI.` adjustments, without the `Landscape` group test), so decal atlases keep their layout
  (**Observed**: the purpose is inferred).
- **Memory budget** (**Verified**, FUN_140816d90; slot checked against OgreMain's RTTI): every resource manager gets
  `setMemoryBudget(1 GiB)`, then the TextureManager gets **2 GiB at 0** and **1.5 GiB at 1**; 2 to 4 keep 1 GiB.
- Label note (**Observed**): `main.pot` lists "Mega-high (danger!)" at `options.cpp:521`, but the exe's first item is
  "Maximum"; the tooltip recommends more than 4 GB of video memory for the top setting.

Default texture filtering is anisotropic ×16 for every material, not a setting ([post-processing.md](post-processing.md)).

## Graphics: water reflection

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

- At levels 3 and 4 the listener also switches every loaded town to display state 4 for the reflection render and restores each
  town's previous state afterwards (FUN_1407f60d0; what state 4 is was not traced: **Unknown**, presumably the full town).
- **The pass only runs when water is visible** (**Observed**, `WaterOcclusionListener` FUN_1409d9f30): with the setting above 0
  an occlusion query on the water decides it, and the reflection is enabled when more than 100 water pixels passed; at 0 it is
  disabled.
- **Level 0 changes the water shaders** (**Verified**: FUN_1402d7280 builds `WaterFP`, `WaterFPBlend` and `WaterFPDistant` with
  the preprocessor define `NO_REFLECTION,` when the setting is 0; `water.hlsl` then uses a reflection factor of 1, i.e. the
  environment-cube specular unattenuated). When those programs are rebuilt after a change is **Unknown**.
- **Reflection range** (**Verified**, FUN_1403e7490): sets the reflection camera's far clip to `view distance × 10 × value`
  (30000 with the defaults 5000 and 0.6). That function runs only from the View Distance and Terrain Detail sliders; the
  reflection camera's creation (FUN_1409d8c30) sets only its near clip (5), orientation, FOV and aspect, so until one of those
  sliders moves in a session the far clip is Ogre's camera default (**Observed**; Ogre's default far distance is 100000, not
  checked in this OgreMain build). The target size (512 × 512) is fixed in `main.compositor`, not a setting.

## Graphics: shadows

`shadow mode` (0 Disabled, 1 CSM, 2 RTWSM; missing → 0), `shadow quality` (0, 1, 2 → map side 1024, 2048, 4096; missing → 0),
`Shadow Range` (int, slider 1000 … 9000; missing → 5000; **≤ 10 → 10000**): see
[shadows.md](shadows.md#settings-verified-exe-settings-loader-writer-and-options-screen-settingscfg). Applied when the options close
(`setShadowMode(mode, table[quality])`); the range is read by the shadow code every frame (FUN_140864d80, FUN_140865150,
FUN_140866510). No restart.

**Shadow quality labels are swapped** (**Verified**, the combo helpers above): the list shows "1024 (poor)" with data 1,
"2048 (nice)" with data 0 and "4096 (some video cards may freak out)" with data 2, and the data indexes {1024, 2048, 4096}. So
choosing "1024 (poor)" gives a **2048²** map and "2048 (nice)" a **1024²** map.

## Graphics: decals and effects

| Key | Type, range | Missing → | Options label | What it does | Restart |
| --- | --- | --- | --- | --- | --- |
| `Blood` | bool | 1 | Blood | Gates blood effects ([character-stats.md](../game/character-stats.md)); also read by the decal code (FUN_1408df7d0) | No |
| `Decal Range` | float (written as int); slider 0 … 5000 | 1000 | Decal Range | Radius passed to `Terrain::getBloodPatches(range, …)`: the terrain patches within it get blood-decal textures (FUN_1408dd860) | No |
| `Decal Resolution` | int 512 / 1024 / 2048 | 1024 | Decal Resolution: 512, 1024, 2048 (the item data are the sizes) | Side of each `Kenshi_Decal_Texture_<n>` render texture; changing it removes and re-creates them (FUN_1408de890, run when the options close) | No |
| `harpoonLimit` | int (stored as float); slider 10 … 500 | 60 | Harpoon limit | Most harpoons / arrows left stuck in the world; adding one beyond it removes one (FUN_14043acf0) | No |
| `skip stencil tricks` | bool | forced 0 | none | **Not read from the file**: the loader always sets 0 (the 16-bit store at +0x40 writes 0 here and 1 into the next byte); the writer writes the in-memory value. When 0, buildings with an `interior mask` mesh use it (FUN_140561d10) | — |

Labels "Blood" and the decal tooltip ("the quality of blood splats on the terrain", paraphrased) are from `options.cpp:560–568`.
The terrain plugin adds the blood patches to the render queue one above the terrain's (26) and only for the camera it was given
as the main one, so reflections never show them (**Verified**, `Terrain::_updateRenderQueue`). **Unknown**: how many decal
textures exist (a count kept by the decal manager) and their pixel format.

## Post-processing (compositor nodes)

Every node listed in `data/materials/compositors/compositors.cfg` (and in mods' copies) is a `settings.cfg` key of its own name:
**`FXAA`** and **`HeatHaze`** in the base game (`#SSAO` and `#RTWDebug` are commented out). Bool; **missing → on**. The options
screen adds one checkbox per node, labelled with the node name ("FXAA", "HeatHaze"); toggling rebuilds the workspaces at once. The
loader is FUN_1403ea7b0, the writer loops over the node list. Details: [post-processing.md](post-processing.md#heat-haze-verified).
Bloom, exposure and SSAO have no setting.

## Foliage and distances

| Key | Type, range | Missing → | Options label | What it does | Restart |
| --- | --- | --- | --- | --- | --- |
| `grass range` | float; slider 0 … 3 | 1 | Grass View Range | Grass page range multiplier ([foliage.md](foliage.md#distances)); read by the per-zone set-up FUN_1406d3e30 | Zones loaded afterwards |
| `grass density` | float; slider 0 … 2 | 1 | Grass Density | Multiplies grass candidates per page (FUN_1406d0340) | Zones loaded afterwards |
| `foliage range` | float; slider 0.1 … 4 | 1; **≤ 0 → 0.1** | Foliage View Range | Mesh foliage page range multiplier (FUN_1406d3e30); a zone gets foliage only when it is above 0 (FUN_1409f9560), which the clamp always makes true | Zones loaded afterwards |
| `objects view range` | float; slider 1000 … 8000 | 3000; **< 1000 → 1000** | Objects View Range | Rendering distance of building parts ([zones.md](zones.md#draw-distance-and-distant-towns); FUN_140558b40, FUN_14055f6e0) | Objects created afterwards |
| `npc range` | float; slider 1000 … 8000 | 3250; **< 1000 → 3000** | NPC View Range | Character relevance radius ([game-loop.md](../game/game-loop.md)) | No |
| `feature range` | int, stored as float; slider 0 … 8 | 2; **clamped 0 … 6** | Terrain Feature Range | Square of ±N zones (64 × 64 zone grid) around the camera's zone that map features load for (FUN_140a098e0) | No |
| `distant town range` | int, stored as float; slider 0 … 8 | 6; **clamped 0 … 10** | Distant Towns Range | Square of ±N zones around the camera's zone that distant towns show for (FUN_140a0b340) | No |
| `generate distant towns` | bool | 0 | Generate Distant Towns | Build distant meshes for towns without a baked one ([zones.md](zones.md#draw-distance-and-distant-towns); FUN_1400d8350) | Towns loaded afterwards |

"Zones loaded afterwards" etc. are **Observed** from where the consumers run (zone or object set-up), not from a test in the game.
The slider limits of `feature range` and `distant town range` (0 … 8) differ from the loader's clamps (0 … 6, 0 … 10): a slider
value of 7 or 8 for features is cut back to 6 at the next start (**Verified** both numbers; the in-session effect of 7–8 before
a restart is **Unknown**).

## Camera and controls

| Key | Type, range | Missing → | Options label | What it does |
| --- | --- | --- | --- | --- |
| `camera speed` | float; slider 150 … 1000 | 500 | Camera Move Speed | Pan speed ([camera.md](camera.md)) |
| `mouse speed pan` | float; slider 2 … 50 | 6 | Camera Rotate Speed X | Rotation speed (+0x04) |
| `mouse speed tilt` | float; slider 2 … 50 | 6 | Camera Rotate Speed Y | Tilt speed (+0x08) |
| `camera zoom` | float; slider 10 … 200 | 125 | Camera Zoom Speed | Zoom step speed (+0x0C) |
| `invert X` | bool | 0 | Invert mouse X | Stored as a ±1 factor at +0x10 |
| `invert Y` | bool | **1** | Invert mouse Y | Stored as a ±1 factor at +0x14 (inverted by default) |
| `swap mouse buttons` | bool | 0 | Swap mouse buttons | Swaps left and right buttons on the GUI |
| `hardware_mouse` | bool | 0 | Hardware Mouse | Windows cursor instead of the drawn one; the tooltip says it needs a restart |
| `Edge scrolling` | bool | 1 | Edge scrolling | Camera moves when the pointer is at the screen edge |
| `mouse speed horizontal` | float | 1.5 (see text) | none | Scales the raw mouse x movement |
| `mouse speed vertical` | float | 1.5 (see text) | none | Scales the raw mouse y movement |

- The block mapping of the four camera sliders is now **Verified** (base address above): `camera speed` +0x00, `mouse speed pan`
  +0x04 = "Camera Rotate Speed X", `mouse speed tilt` +0x08 = "… Y", `camera zoom` +0x0C.
- `mouse speed horizontal` / `vertical` are **not** in the main loader but are read at start by FUN_1408149a0 (after the
  launcher dialog closes) into two floats that multiply the mouse's relative x and y in the input listener
  (`MainListener::vfunc_1`, the results feed the camera code). If `vertical` reads as 0 (missing), both become 1.5 and the three
  default lines are appended to the file. (**Verified** the reads, the defaults and the multiplication; what the scaled deltas
  drive beyond the camera functions that read them is **Observed**.) No options control; restart to change.
- Key bindings live in `controls.cfg` ([ui-input.md](../game/ui-input.md)).

## UI and general

| Key | Type, range | Missing → | Options label | What it does |
| --- | --- | --- | --- | --- |
| `Show names` | bool | 1 | Show character names | Names above the player's characters |
| `Movement marker` | bool | 1 | Show movement marker | The green marker where you click |
| `Rotation marker` | bool | 1 | Show rotation marker | The green marker while rotating the camera |
| `floaters` | int 0 … 2 | 1 | Show damage floaters: Off = 0, Simple info = 1, Full info = 2 | Damage numbers; only shown within 4000 units (squared distance 1.6e7, FUN_1406508d0); 2 splits them by damage type |
| `font size2` | int (stored as float); slider −10 … 32 | 3 | Font Size | GUI font size; applied by its own handler. How the number maps to a size is **Unknown** |
| `tutorials` | bool | 1 | Tutorials | In-game tutorials ("Reset Tutorials" is a button, not a key) |
| `censorship` | bool | 0 | Language censorship | Filters swearing in dialogue (FUN_14067e8b0) |
| `language` | string, locale code | see text | none in options | The locale folder `locale/<code>/` used for translations |
| `steam_language` | string, Steam language name | see text | none | The Steam language the `language` was last chosen from |

**Language** (**Observed**, FUN_140175a90): the game has a table of Steam language names, display names and locale codes
(`english` / `en_GB`, `german` / `de_DE`, `french` / `fr_FR`, `russian` / `ru_RU`, `italian` / `it_IT`, `portuguese` / `pt_BR`,
`spanish` / `es_ES`, `chinese` / `zh_CN`, `japanese` / `ja_JP`, `korean` / `ko_KR`) plus translations found under `./locale`. On
Steam, if Steam's current game language differs from `steam_language`, the locale matching Steam's language wins; otherwise
`language` is used; failing both, the first entry (English). Both keys are written back at once.

## Audio

All floats, sliders 0 … 1, **missing → 0.5**, applied when the options close (FUN_1403e7290 → the audio manager).

| Key | Options label |
| --- | --- |
| `Music volume` | Music volume |
| `Ambient volume` | Ambient volume |
| `Footstep volume` | Footstep volume |
| `Sfx volume` | SFX volume |
| `VO volume` | VO volume |
| `UI volume` | UI volume |
| `Music frequency` | Music Frequency (minutes); float, slider 1 … 20, missing → 5 |

## Gameplay

| Key | Type, range | Missing → | Options label | Details |
| --- | --- | --- | --- | --- |
| `Squad size multiplier` | float; slider 0.5 … 3 | 1 | Squad size multiplier | [factions-squads-towns.md](../game/factions-squads-towns.md) |
| `Global population multiplier` | float; slider 0.5 … 4 | 1 | Global population multiplier (num NPC squads) | same |
| `raidSizeMult` | float; slider 0.25 … 4 | 1 | Town raid events: size | same (FUN_1409c5ad0) |
| `raidFrequencyMult` | float; slider 0.25 … 3 | 1 | Town raid events: frequency | same (FUN_1409c5740) |
| `attacks` | int 0 … 5 | 2 | Town attacks frequency: Bombardment = 0, High = 1, Normal = 2, Fewer = 3, Rare = 4, Never = 5 | Timer for opportunistic attacks on the player's base, below |
| `Dismemberment` | int 0 … 2 | 1 | Dismemberment: Never = 0, Rare = 1, Frequent = 2 | [combat.md](../game/combat.md), [character-stats.md](../game/character-stats.md) |
| `civilians` | bool | 1 | none | Loaded and saved; **no reader found** (no instruction addresses its global other than the writer; an access through the block's base pointer would not show up, so **Unknown**) |
| `Fast zone hopping` | bool | 0 | Fast zone hopping | Keeps a larger area loaded so switching between distant characters skips loading ([game-loop.md](../game/game-loop.md); FUN_140a0f640) |
| `Auto save` | bool | 1 | Auto save | [save.md](save.md#autosave-and-quicksave) |
| `Auto save time (minutes)` | float; slider 1 … 30 | 10 | Auto save time (minutes) | same |
| `User save location` | bool | 1 (see save.md) | User Save Location | Saves in `%LOCALAPPDATA%\kenshi\save` (1) or the install (0); [save.md](save.md) |
| `continue` | string | — | none | Save the "Continue" button loads; written after every save ([save.md](save.md)) |
| `autosaveindex` | int | 0 | none | Rotating autosave slot ([save.md](save.md#autosave-and-quicksave)) |
| `editor` | string, a mod name | none | none (level editor) | Written when the in-game level editor saves a mod ("Save Mod", FUN_140777ce0); read when the editor opens to preselect that mod in its mod list (FUN_14077be90; an unknown name selects the first). Absent from the install's file (the editor was never used) |

**`attacks`** (**Observed**, FUN_140286a70): sets a countdown drawn uniformly from a range per level: Bombardment 0, High 6 … 48,
Normal 32 … 96, Fewer 72 … 144, Rare 130 … 188; Never leaves it unset. The unit (presumably in-game hours) is **Unknown**. The
tooltip says the old default was High.

## kenshi.cfg (Ogre's config)

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

All of these need a restart (Ogre reads them when the window is created). The launcher ("Rendering system", "Resolution",
"Rendering device", "Full screen", "Borderless", "VSync", "Monitor" rows, FUN_140122270) edits them before the game starts; the
options screen reads `Full Screen` and `Border` from the render system's current config and writes `Full Screen`, `Border` and
`Video Mode` back when it closes. Other `.cfg` files: `controls.cfg` (key bindings, [ui-input.md](../game/ui-input.md)),
`resources.cfg` / `Plugins_x64.cfg` (Ogre resources and plugins, [overview.md](overview.md)), `screens.cfg`, `mods.cfg`.

## What the Meitou viewer does with these

`meitou-viewer` **does not read `settings.cfg` or `kenshi.cfg`**. It uses the game's defaults as constants and its own options:

| Setting | Viewer |
| --- | --- |
| `view distance` | Emulated: the haze uses the constant 5000 (`KenshiHaze.ViewDistanceSetting`); the camera's far plane is the viewer's own (`--view-distance`, raised with the haze at height) |
| `shadow quality`, `Shadow Range` | Honoured as options: `--shadow-quality` (index into the same table), `--shadow-range` (clamped 1000 … 9000); `--no-shadows` |
| `shadow mode` | Not honoured: the viewer always runs its CSM-style pass (Faithful or Meitou), never RTW |
| `foliage range`, `grass range`, `grass density` | Emulated with sliders / environment variables (`MEITOU_FOLIAGE_RANGE`, `MEITOU_GRASS_RANGE`, `MEITOU_GRASS_DENSITY`); default ranges ×4, not the game's 1 |
| `objects view range`, `feature range`, `distant town range` | Constants in `ObjectRanges`; the viewer's own `--object-distance` (default 12000) and `--distant-range` replace them |
| `FXAA`, `HeatHaze` | Honoured as options (`--fxaa` / `--no-fxaa`, `--heat-haze` / `--no-heat-haze`; Faithful switches) |
| `camera speed`, `camera zoom` | Constants in `KenshiCamera` / `CameraSettings` (500, 125) |
| `water reflection`, `reflection range` | Honoured as options (2026-10-06): `--water-reflection <0..4>` (default 2, the game's) and `--reflection-range <x>` (default 0.6), Tab sliders for both; `--no-reflections` / `R` stay as the on/off switch. See "Viewer: water reflection" below |
| `terrain detail`, `terrain distant`, `terrain threshold`, `terrain patch size` | Not honoured: the viewer's own CDLOD (64-quad patches, range factor K) |
| `texture resolution gimping` | Honoured as `--texture-quality <0..4>` (default 1, the game's; restart to change, like the game). See "Viewer: texture quality" below |
| `Decal Range`, `Decal Resolution`, `Blood`, `harpoonLimit` | Not implemented (no decals) |
| `generate distant towns` | Not honoured: towns without a baked mesh are drawn from their buildings' distant meshes |
| `npc range`, `mouse speed *`, `invert *`, UI, audio, gameplay keys | Not applicable / not implemented |

### Viewer: texture quality

`--texture-quality <0..4>` (default **1**, the game's value for a missing key; `TextureQuality` in `Meitou.Data.Textures`). Like the game it
is read when textures load, so it needs a restart; it is set from the options before the world loads.

- **Object and foliage textures** (`WorldTextureCache`): the DDS is rewritten as in "Mip skipping" above (`TextureQuality.LevelsToDrop`
  for the name and resource group, `DropTopMips` for the rewrite; unit tests in `TextureQualityTests`). The group is the file's
  `resources.cfg` group (`OgreScriptResources.GroupOfFile`; the first group in ordinal order that holds the file, so a folder listed in
  two groups is not told apart: **Observed**, a file the game loads through one group and finds in another is a corner case not checked).
  442 `_HI.` / `_LO.` files exist in the install (mostly `animal`, `characters`), so those branches are live.
- **Terrain layers** (**Observed** approximation): the game's biome textures are in the `Landscape` group (see
  [terrain.md](terrain.md#biomes-fields-and-shader-parameters-verified-kenshi_x64exe)), so at 1 and above they lose `level` mips: 2048² becomes
  1024² at the default. The viewer's layer arrays have one size (`--layer-size`, 2048), so the size is lowered to `min(--layer-size, 2048 >> drop)` for all
  layers instead of per file. **This halves the terrain texture resolution against earlier builds at the default**; `--texture-quality 0` restores it.
  Sources smaller than 2048² (a few 1024² and one 512²) are not given a further drop, so they differ from the game slightly.
- **Memory budget**: the game sets the TextureManager to 2 GiB / 1.5 GiB / 1 GiB (above). The viewer's two texture caches (objects, foliage)
  each use it as the size at which they unload least-recently-used textures (`HighWaterMb`; before: 1024 always). The game has one manager, so the
  viewer's total can be up to twice that: **Observed** (an approximation).
- **Not applied**: sky, water, post-processing and heat-haze textures (the game also drops mips of non-cube `SkyX` / `General` group DXT textures; the viewer loads those at
  full size), the decal compensation (no decals), and per-file group lookups for the model viewer (it keeps level 0).

### Viewer: water reflection

`--water-reflection <0..4>` (default **2**, the game's value for a missing key; Tab slider "Water reflection 0-4 (game)") and `--reflection-range <x>`
(default 0.6; Tab slider). `ReflectionPass.Level` / `Range`. What the levels draw, in the viewer's terms (the sky and terrain always come first; the
viewer's own cut-downs from [viewer.md](../viewer.md#world-mode) "Reflections" stay and say *how much*, the level says *what*):

| Level | Game | Viewer |
| --- | --- | --- |
| 0 | no pass; water shaders built with `NO_REFLECTION` | no pass; the water's `uReflect = 0` path: the sky colour is reflected unattenuated (**Observed** to be the counterpart of the game's reflection factor 1; the viewer's shader is not the game's, so the look is not compared) |
| 1 | queues 5-8, 25 | sky and terrain |
| 2 | + queue 50 | the same: queue 50 is the characters (**Observed** from the label), none exist in the viewer yet |
| 3 | + queue 20 | + every placed object (buildings and map features are not told apart: **Unknown** which queue the features use) |
| 4 | no list: everything | + foliage (trees, bushes, rocks; no grass, as before). Which queue the game's foliage is in is **Unknown**; "everything" is assumed to be the only level that includes it |

- **Reflection range**: the mirrored scene's far clip is `Sky.HazeDistance × range` (the game's view distance × 10 × value; 30000 with the defaults),
  set each frame (the intended value: the game's quirk that it only applies once the View Distance or Terrain Detail slider has moved, before which Ogre's
  default far clip applies, is **not** reproduced). The viewer's previous constant was 150000, which `--reflection-range 3` reproduces.
- **Not done**: the "only when more than 100 water pixels are visible" occlusion query. The Vulkan GL layer's `BeginQuery` supports only elapsed-time queries
  (`QueryTarget` has no samples-passed), and that layer is not part of this change. Town display state 4 at levels 3 and 4 is not reproduced either (**Unknown**).
- **Default changed**: the viewer used to draw sky, terrain, objects and foliage (level 4) out to 150000; the default is now the game's level 2 and
  range 0.6, so objects and foliage no longer appear in reflections unless `--water-reflection 3` / `4` is given.

## Unknowns

- K in the terrain LOD metric (the per-camera value the plugin divides by distance) and the node error's units, so the
  thresholds' unit ("pixels") is not confirmed.
- What town display state 4 is (set for reflections at levels 3 and 4), and when the water programs are rebuilt after
  `water reflection` changes to or from 0.
- The reflection camera's far clip before FUN_1403e7490 first runs (Ogre's default assumed).
- The number and pixel format of the decal textures; the unit of the `attacks` timers; the `font size2` mapping.
- Whether anything reads `civilians`.
- In-session effect of `feature range` 7 or 8 (above the loader's clamp).
