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

The table is Verified from the file headers. The order itself comes from the editor (FCS
`InheritFiles`): a fixed list `gamedata.base, Newwworld.mod, Dialogue.mod, coltontown.mod, Nizu.mod,
Mohamad.mod, rebirth.mod` (only those present; the middle three are not in the current game), then
the lines of `data/mods.cfg` in order: one mod file name per line (e.g. `MyMod.mod`), written by the
editor's "export mods.cfg" without the fixed files. A mod in `mods.cfg` is looked up as
`data/<name>` or `mods/<name without .mod>/<name>`. Base content and mods use the same format, so a
mod is just another layer on top; how layers merge is in [fcs-mod.md](fcs-mod.md#merging-fcs). The
game's own order (including Steam workshop folders) is **Unknown** until checked in `kenshi_x64.exe`.

A mod lives in `mods/<name>/<name>.mod`. `.info` files are XML `ModData` (id, mod name, tags,
visibility, lastUpdate), e.g. `data/_rebirth.info`, holding workshop metadata.

## Resource lookup and art overrides (Observed)

`resources.cfg` lists ~100 `FileSystem=./data/...` folders in one `[General]` group. Ogre finds
resources by **file name only** across all of them, so names must be unique. Per `mods/readme.txt`,
a mod replaces any art file by shipping a file with the same name under
`mods/<name>/<same relative path>`. Exact precedence (relative path vs. bare name, several mods
overriding the same file) is **Unknown** and needs testing.

## Format inventory

Counts are files under `data/`.

| Format | Count | Origin | Status |
| --- | ---: | --- | --- |
| `.base`, `.mod` | 4 | FCS game data | **Verified** layout, see [fcs-mod.md](fcs-mod.md) |
| `.mesh` | 3277 | Ogre | Header **Verified**: `[MeshSerializer_v1.100]` |
| `.skeleton` | 365 | Ogre | Header **Verified**: `[Serializer_v1.80]` |
| `.hkt` | 3995 | Havok | Header **Verified**: binary tagfile, magic `1E0DB0CA CEFA11D0`, SDK `hk_2014.2.0-r1`, root `hkRootLevelContainer`. Animations and navmesh tiles (`newland/land/navtiles/tile*.hkt`) |
| `.material`, `.program`, `.compositor`, `.hlsl`, `.vert`, `.frag` | ~270 | Ogre scripts / shaders | Not analyzed |
| `.dds`, `.png`, `.tga` | ~3900 | Textures | Standard formats |
| `.zone` | 701 | World cells, `leveldata/zone.X.Y.zone` | Not analyzed; strings like `0-base-S23` near the start |
| `.level` | 14 | Level data | Not analyzed |
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
