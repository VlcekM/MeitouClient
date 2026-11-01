# Game data overview

## Install layout

**Observed:** Steam Kenshi install, 2026-10-04. `data/` contains records, art and world data;
`mods/` contains local mods. `fcs.def`, `fcs_enums.def`, `fcs_layout.def`, `fcs_theme.def`
define editor schemas. `resources.cfg` supplies Ogre search folders; `Plugins_x64.cfg` loads
D3D11, ParticleUniverse and Terrain. Engine stack: Ogre, MyGUI, PhysX 2.x, Havok, Wwise, OIS,
Steamworks. Saves use `%LOCALAPPDATA%\kenshi\save\` unless `User save location=0` selects legacy
`save/`; see [save.md](save.md).

## Load order

**Verified:** headers and decompiled Kenshi loader (`FUN_14086ab20`, `FUN_1408693a0`,
`FUN_140870ae0`), except observations explicitly marked below.

1. Core files, exactly `data/gamedata.base`, `data/Newwworld.mod`, `data/Dialogue.mod`,
   `data/rebirth.mod`. Types 16,16,17,17; record counts 9,399/2,511/39,077/8,571. Each header
   depends on preceding files; base references Newwworld, Dialogue references rebirth.
   No scan; failed core logs an error and continues. Core list is initialized only if
   `data/mods.cfg` exists or can be created (missing file is created empty).
2. Core language `.translation` (`locale/<code>/<code>.translation`, same record format) for the language chosen from `settings.cfg` ([localisation](localisation.md)).
3. Mods in `data/mods.cfg` order, then the translation of the first mod that has one (`<folder>/locale/X/X.translation`; that mod also sets the language). Header
   dependencies do not affect runtime order. Unopenable/invalid mods are skipped and collected
   in one ignored-files dialog, workshop entries marked `(*)`.

`mods.cfg`: text-mode lines up to 999 characters, CRLF accepted, empty lines skipped, **no
trimming**. First character `#`, `/`, `;` means comment. Suffix after last dot must equal
case-sensitive `mod`; preceding text is the name. Ignore Newwworld/Dialogue/rebirth; `.base`
fails extension check. Names resolve case-sensitively; missing names log and skip, duplicates
load once at their first position. Merge rules: [fcs-mod.md](fcs-mod.md#merging).

Discovery (`FUN_14086f7a0`, launcher `FUN_140127890`) uses one name-keyed table:

- Steam running: subscribed items via `ISteamUGC` install info; first sorted file ending exactly
  `.mod` supplies its filename stem as name. **Observed** location:
  `steamapps/workshop/content/233860/<item id>/`; item folder name is irrelevant.
- Local sorted subfolders D containing `mods/D/D.mod`, replacing workshop entries of the same
  name. Other `.mod` files in `data/` are not discovered.

**Meitou difference:** `LoadOrder` cannot query subscriptions; it scans every downloaded item
in `GameInstall.WorkshopDirectory`, in folder-name order. Each loaded mod retains its folder
for art, translations and its world layer ([zones](zones.md#mods)). A record path `.\mods\<name>\x` resolves into that mod's folder (Workshop or
local; `CollisionPaths.Resolve`), used for collision, interiors, body files, attachment points and race `editor limits` (#93).

**Verified** launcher (`FUN_140125df0`, save `FUN_140120bd0`): active entries first in cfg order,
remaining mods dependency-sorted; unknown dependency names dropped, cycles omitted (saving
therefore drops them). Only type-17 headers are read for display (`FUN_1406be050`); missing
references/dependencies warn without blocking. `data/__mods.list` remembers names without
extensions; newly found names start enabled. Rewritten with visible names; without Steam old
missing names remain. **Observed:** beyond 1000 entries it restarts. Save writes enabled
`<name>.mod` in displayed order.

FCS differs: `InheritFiles` fixes gamedata.base, Newwworld.mod, Dialogue.mod, coltontown.mod,
Nizu.mod, Mohamad.mod, rebirth.mod and also searches cfg entries under `data/`.

**Observed:** `.info` XML `ModData` stores id/name/tags/visibility/lastUpdate; no defined `.info`
string in the engine. Resource scan skips `_`-prefixed files.

## Resource lookup and art overrides (Observed)

`resources.cfg` lists about 100 FileSystem folders in General. Ogre resolves bare filenames
across folders; names must be unique. `mods/readme.txt` describes art replacement under
`mods/<name>/<same relative path>`. **Unknown:** exact relative-path/bare-name precedence and
multiple overrides; needs tests. Letter case of paths is not reliable in the original data: `resources.cfg` and the particle renderer say `data/particles/textures`, the folder is `Textures`; a mesh links `mammal_beak.skeleton`, the file is `Mammal_beak.skeleton`; a record names `automine.xml`, the file is `automine.XML`. `GameFiles.Resolve` finds the real path on a case-sensitive file system (**Verified** on Linux with an LD_PRELOAD check of failed lookups over load plus a minute of play on a large save: these three, all found afterwards; area switches, menus and the editor were not covered).

**Verified** `FUN_140816d90`: after base folders, walk active mods in cfg order; add subfolders
holding files to matching base resource group, default General. Skip `leveldata`,
`newland/leveldata`, `locale`, translation folders. Add mod root only for files other than
`.mod`, `.translation`, `_`-prefixed metadata/images.

## Format inventory

**Observed** counts under `data/`; confidence and detailed verification live in each reference.

| Files | Reference / known boundary |
|---|---|
| 4 `.base`/`.mod` | [FCS records](fcs-mod.md) |
| 3277 `.mesh`, 365 `.skeleton` (one empty) | [Mesh](ogre-mesh.md), [skeleton](ogre-skeleton.md) |
| 3995 `.hkt` | [Havok tagfile](havok-tagfile.md) (SDK `hk_2014.2.0-r1`); animations and [navtiles](../game/pathfinding.md) |
| 181 `.material`/`.program`, ~90 shader/compositor files | [Materials](ogre-material.md), [runtime](runtime-materials.md), [post](post-processing.md) |
| ~3900 `.dds`/`.png`/`.tga` | [DDS](dds.md); standard images |
| 701 `.zone`, 14 `.level` | [Zones](zones.md); `.level` type15/headerless; native level paths `newland/leveldata/[<core file>/]leveldata.level` (**Verified** FUN_140870ae0) |
| 1 `globalPathing.path` | Unknown layout |
| 256 `.raw`, 1 `fullmap.tif` | [Terrain](terrain.md); legacy257² uint16/4097² world; current16385², height=raw*9800/65535 |
| 3 `.dat` | [Features](zones.md), [fogfeatures](fogfeatures.md), [blendinfo](terrain.md) |
| 1158 `.xml` (1125 collisions), 862 `.bin` | [Collision](collision.md), [MyGUI](mygui.md) |
| 53 `.layout`, 93 `.pu`, 24 `.bnk` | [MyGUI](mygui.md), [particles](particle-universe.md), [Wwise banks](wwise-bnk.md) (**Verified** v112 hierarchy; Vorbis decoded) |
| ~200 `.phs`/`.bod2`/`.body`/`.PxProj` | [PHS](phs.md), [bodies](../characters.md); `.PxProj` are Max projects, not loaded |

## World data (Observed, folder names only)

`newland/land/`: fullmap.tif, biomemap.png, blendmap.png, areasmap.tga, blendinfo.dat,
features.dat, fogfeatures.dat, navtiles/, overlaymaps/, textures/. Also newland/leveldata.level,
newland/Assets/, data/leveldata/*.zone and platoon/. `data/land/` grasssplits/navtiles/overlaymaps/
textures hold older or shared world data.
