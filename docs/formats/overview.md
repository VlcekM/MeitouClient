# Game data overview

Examined 2026-10-04 on the Steam install of Kenshi.

## Install layout

| Path | Contents |
| --- | --- |
| `kenshi_x64.exe` | The engine (Ogre 3D, MyGUI, PhysX 2.x, Havok, Wwise, OIS, Steamworks) |
| `fcs.def`, `fcs_enums.def`, `fcs_layout.def`, `fcs_theme.def` | Schema of the game-data records, used by the editor |
| `forgotten construction set.exe` | The official mod editor ("FCS") |
| `resources.cfg` | Ogre resource search folders |
| `Plugins_x64.cfg` | Ogre plugins: D3D11 render system, ParticleUniverse, Terrain |
| `data/` | All base-game content |
| `mods/` | Mods, one folder per mod |
| `save/` | Save games (not analyzed) |

## Three layers of content

1. **Game records**: `data/*.base`, `data/*.mod`. Every item, character, faction, dialogue line,
   building, AI package, research entry... One binary format, see [fcs-mod.md](fcs-mod.md).
2. **Art assets**: meshes, skeletons, textures, materials, shaders, GUI, particles, audio. Found by
   file name through Ogre's resource folders.
3. **World data**: terrain maps, zones, navmesh, global pathing. Mostly not analyzed yet.

## Load order

| File | Format | Records | Dependencies |
| --- | --- | ---: | --- |
| `gamedata.base` | 16 | 9,399 | none (references `Newwworld.mod`) |
| `Newwworld.mod` | 16 | 2,511 | `gamedata.base` |
| `Dialogue.mod` | 17 | 39,077 | `gamedata.base,Newwworld.mod` (references `rebirth.mod`) |
| `rebirth.mod` | 17 | 8,571 | `gamedata.base,Newwworld.mod,Dialogue.mod` |

The table is Verified from the file headers. Base content and mods use the same format, so a mod is
just another layer on top; how layers merge is in [fcs-mod.md](fcs-mod.md#merging). The order below
is the **game's** (Verified by decompiling `kenshi_x64.exe`; addresses are functions in the current
Steam build). The editor differs: FCS `InheritFiles` uses the fixed list `gamedata.base,
Newwworld.mod, Dialogue.mod, coltontown.mod, Nizu.mod, Mohamad.mod, rebirth.mod` and also looks for a
`mods.cfg` entry as `data/<name>`; the game does neither.

1. **Core files**, a fixed list (`FUN_14086ab20`): `data/gamedata.base`, `data/Newwworld.mod`,
   `data/Dialogue.mod`, `data/rebirth.mod`, in that order. No directory scan, no `*.base` search.
   A core file that fails to load shows "Failed to load the core '<file>' file" and loading goes on.
   (Quirk: the list is only set up if `data/mods.cfg` exists or can be created; the game creates it
   empty when missing.)
2. **Core translation**: a `.translation` file for the chosen language (same format; see
   [fcs-mod.md](fcs-mod.md#game-loader)).
3. **Mods, in `data/mods.cfg` order** (read by `FUN_1408693a0`, applied by `FUN_140870ae0`):
   - Text file, one entry per line (CRLF fine: opened in text mode; lines up to 999 chars; empty lines
     skipped; **no trimming**, so trailing spaces break an entry).
   - Lines starting with `#`, `/` or `;` are comments.
   - The part after the last `.` must be exactly `mod` (case-sensitive), else the line is ignored.
     The rest is the mod's name, e.g. `MyMod.mod` → `MyMod`.
   - Names `Newwworld`, `Dialogue`, `rebirth` are ignored (already loaded as core files);
     `gamedata.base` is ignored by the extension rule.
   - The name is looked up, **case-sensitively**, among the mods found on disk (next list). Not found:
     logs `[Mods] Mod '<name>' not found.` and skips it. A name listed twice loads once, at its first
     position.
   - Dependencies in the mod headers are **not** checked or used for ordering at load time.
4. **Mod translations** (`<mod folder>/locale/...`), after all mods.

**Where mods are found** (`FUN_14086f7a0`, run when the launcher window opens, `FUN_140127890`), into
one table keyed by name:

- **Steam workshop** first (only when Steam is running): for each subscribed item, Steam's install
  folder for it (`ISteamUGC` subscribed items + install info; Observed in this install:
  `steamapps/workshop/content/233860/<item id>/`). The mod is the first file with extension exactly
  `.mod` in the folder listing (sorted; files like `X.mod.bak v1` don't count); its name is the file
  name without `.mod`, so the folder name (the item id) doesn't matter.
- **`mods/` folder** second: each subfolder `D` (sorted) that contains `mods/D/D.mod` is mod `D`. A
  local mod **replaces** a workshop mod with the same name.
- Nothing else: a `.mod` in `data/` other than the core files is never loaded.

`Meitou.Data.LoadOrder` follows these rules, except that it can't ask Steam for the subscribed items:
it takes every downloaded item in `steamapps/workshop/content/233860/` of the install's Steam library
(`GameInstall.WorkshopDirectory`), in folder-name order.

Each mod remembers its folder (used for its translation, its art and its `leveldata/`). A mod that
can't be opened or has an invalid file type is skipped and listed in one "Mod error(s) (Ignored
files)" dialog (workshop mods are marked `(*)`); the rest still load.

**The launcher** (Mods tab, `FUN_140125df0`; saving in `FUN_140120bd0`) is what writes `mods.cfg`:
- It shows all found mods: those active in `mods.cfg` first in that order, then the rest sorted so
  that a mod comes after the mods it depends on (header dependencies whose names aren't found are
  dropped from the sort; mods in a dependency cycle are left out of the list, so saving drops them
  from `mods.cfg`). It reads the mod
  headers for display (`FUN_1406be050`; only for type-17 files) and marks dependencies or references
  that aren't found as "Missing mod!". That is a warning only.
- `data/__mods.list` (launcher-only): every mod name the launcher has seen, one per line, no
  extension. A found mod **not** in it is new and starts **enabled**. The launcher rewrites it with
  the names it sees (without Steam it also keeps old names it no longer finds; Observed quirk: past
  1000 entries it starts over).
- On save, `mods.cfg` gets `<name>.mod` for each enabled mod, in the displayed order.

`.info` files (XML `ModData`: id, mod name, tags, visibility, lastUpdate; e.g. `data/_rebirth.info`,
`_<name>.info` next to workshop mods) hold workshop metadata. The game never opens them (Observed:
no defined `.info` string in the executable); the resource scan skips files starting with `_`.

## Resource lookup and art overrides (Observed)

`resources.cfg` lists ~100 `FileSystem=./data/...` folders in one `[General]` group. Ogre finds
resources by **file name only** across all of them, so names must be unique. Per `mods/readme.txt`,
a mod replaces any art file by shipping a file with the same name under
`mods/<name>/<same relative path>`. Exact precedence (relative path vs. bare name, several mods
overriding the same file) is **Unknown** and needs testing.

Verified (decompiled, `FUN_140816d90`, briefly; resources are covered in more depth elsewhere): after
the `resources.cfg` folders, the game walks the folder of every mod active in `mods.cfg`, in
`mods.cfg` order, and adds each subfolder that holds files as another `FileSystem` location, in the
resource group of the matching base folder (default `General`). Skipped: `leveldata`,
`newland/leveldata`, `locale` and translation folders. The mod's own folder is added only if it holds
files other than the `.mod`, `.translation` and `_`-prefixed files (`_<name>.info`, `_<name>.img`).

## Format inventory

Counts are files under `data/`.

| Format | Count | Origin | Status |
| --- | ---: | --- | --- |
| `.base`, `.mod` | 4 | FCS game data | **Verified** layout, see [fcs-mod.md](fcs-mod.md) |
| `.mesh` | 3277 | Ogre 2.0 (v1 meshes) | **Verified**: all parse; versions 1.100 / 1.8 / 1.41. See [ogre-mesh.md](ogre-mesh.md) |
| `.skeleton` | 365 | Ogre | **Verified**: 364 parse (v1.80 / v1.10), one is empty. See [ogre-skeleton.md](ogre-skeleton.md) |
| `.hkt` | 3995 | Havok | Header **Verified**: binary tagfile, magic `1E0DB0CA CEFA11D0`, SDK `hk_2014.2.0-r1`, root `hkRootLevelContainer`. Animations and navmesh tiles (`newland/land/navtiles/tile*.hkt`) |
| `.material`, `.program` | 181 | Ogre scripts | **Verified**: all 181 compile. See [ogre-material.md](ogre-material.md) |
| `.compositor`, `.hlsl`, `.vert`, `.frag` | ~90 | Ogre compositors / shaders | Not analyzed |
| `.dds`, `.png`, `.tga` | ~3900 | Textures | Standard formats |
| `.zone` | 701 | World cells, `leveldata/zone.X.Y.zone` | Not analyzed; strings like `0-base-S23` near the start |
| `.level` | 14 | Level data | FCS records, file type 15 (no header): first bytes Observed, and the game loads `newland/leveldata/[<core file>/]leveldata.level` with the same loader (Verified, `FUN_140870ae0`). Contents not analyzed |
| `.path` | 1 | `globalPathing.path`, world pathfinding grid | Not analyzed |
| `.raw` | 256 | Heightmaps | Not analyzed |
| `.xml` | 1158 | GUI, foliage, config | Not analyzed |
| `.bin` | 857 | Unknown | Not analyzed |
| `.layout` | 53 | MyGUI | Not analyzed |
| `.pu` | 93 | ParticleUniverse scripts | Not analyzed |
| `.bnk` | 24 | Wwise sound banks | Not analyzed |
| `.phs`, `.bod2`, `.body`, `.PxProj` | ~200 | Physics / ragdoll | Not analyzed |

## World data (Observed, folder names only)

- `newland/land/`: `fullmap.tif`, `biomemap.png`, `blendmap.png`, `areasmap.tga`, `blendinfo.dat`,
  `features.dat`, `fogfeatures.dat`, `navtiles/`, `overlaymaps/`, `textures/`.
- `newland/leveldata.level`, `newland/Assets/` (rocks, things, buildings, textures).
- `data/leveldata/*.zone` grid cells, `data/leveldata/platoon/`.
- `data/land/` (`grasssplits`, `navtiles`, `overlaymaps`, `textures`): older or shared world data.
