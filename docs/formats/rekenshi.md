# RE_Kenshi mod compatibility

**Decision (owner, 2026-11-10, replacing 2026-10-18's "not compatible"): compatibility is per mod, by
behaviour.** Meitou never loads RE_Kenshi plugin DLLs (`RE_Kenshi.json`, `startPlugin`) and offers no
KenshiLib facade. The public wishlist of RE_Kenshi mods decides which plugins get one of:

- **Built in:** a feature that fits the engine (item rotation) becomes a Meitou option or tweak. Enabling
  the RE_Kenshi mod switches the option on, with a note saying so.
- **Ported:** the plugin is rewritten against the [code-mod API](../modding.md) as a mod of its own on the
  Steam workshop, kept up by its maintainer. **Decision (owner, 2026-11-18):** no port's source or binary is
  in this repository or ships with the game, whoever wrote it (Meitou's contributors upload theirs to the
  workshop like anyone); the repository only lists the ports Meitou endorses, by workshop link
  ([endorsed ports](#endorsed-ports)), and the launcher offers to subscribe to them.

A mod's ordinary data and assets load through the normal readers either way.

**Status:** every plugin's DLLs are ignored and the launcher says so ([launcher](../launcher.md)); the `FileRebinds` and `SoundBanks` data
entries are applied ([data entries](#data-entries)). Covered mods are one table,
`CoveredMods` (Meitou.Core), read by the game and by the launcher's Mods page. Built-in mods (`BuiltInMods`,
Meitou.Game): at start, each enabled mod the table names, by `.mod` name or code-mod id, that was not taken in before
(`UserConfig.AdoptedMods`) has its settings copied once into Meitou's setting, with a notice. An entry with a second takeover (the XP mods) copies what a later Meitou added once more for a mod taken in before it (their per-skill settings and label, without the main multiplier), marked `<name> (again)`. A covered code mod is not run (counted as
disabled), so the feature never applies twice, and a save made while it ran does not report it gone.

| Mod | Meitou setting | Matched against | Taken in |
| --- | --- | --- | --- |
| Player-Only XP x10 (xXDarkrusoXx, `Player XP Only x10 RE`, workshop 3679678086) | Squad XP, its skills and Show Squad XP | its 2026-11-07 update | `xp_multiplier` of its `plugin/player_xp_boost.ini`, else 10 (**Observed**: the key and path are strings in its `PlayerXpBoost.dll`; the original game writes the file on its first run); each `stat_<SKILL>` line (`stat_STRENGTH`, `stat_MELEE_ATTACK`...; **Observed**: the skill names and `stat_%s=%.3f` are strings in its DLL) of 0 or more as that skill's own rate, the others following Squad XP; Show Squad XP unless `show_hud=0`; a missing line keeps the default (10, follows, on) |
| Its code-mod port (`player-only-xp`) | Squad XP, its skills and Show Squad XP | any version (by its id) | its saved settings (`Multiplier`, each skill's of 0 or more as its own rate, `ShowIndicator`), over them the plugin's file when the plugin is installed (the port read the shared file at start), else the defaults (10, follows, on) |
| Swamp Turtle FIX (ikahurula, data mod, workshop 2237731365) | Swamp turtle attacks (Fixes) | its 2020-09-24 update | the fix is switched on. Its data still loads: it sets the Swamp Turtle's `weapon` to `_elephant type` (and its unread `attack type`), so its turtles use the elephant moves and that weapon's damage and reach ([animals](../game/animals.md) "Natural weapon") |
| Character Highlight (BFrizFoShiz, RE_Kenshi plugin, `Character Highlight`, workshop 3688641949) | Hover outline (Advanced) | its 2026-06-23 update | the outline is switched on. Alt then outlines the characters near ([ui-input](../game/ui-input.md#hover-outline)), coloured by the right-click order on each, not by the plugin's relation colours |

## Endorsed ports

`CoveredMods.Endorsed` (Meitou.Core) names each RE_Kenshi mod, by `.mod` name or workshop item, that has a port Meitou endorses: a
ported and checked code mod on the Steam workshop. The plugin mod still loads only its data; the port is an ordinary code mod the player
subscribes to and enables (below the original where it needs the original's data), and the game does nothing special for either. The
launcher notes the port on the plugin mod's row and offers to subscribe ([launcher](../launcher.md)). Nothing is taken in. To add a
port: a row here and an entry in `CoveredMods.Endorsed`. A local copy counts as the port only when it has code, since several ports
keep the original's `.mod` name.

| RE_Kenshi mod | Endorsed port | Maintainer | Matched against |
| --- | --- | --- | --- |
| Loot-Scoot-Execute (Emkej, `Loot-Scoot-Execute`, workshop 3675700748; its MCM fork 3813852124) | [[MeitouClient] Execution](https://steamcommunity.com/sharedfiles/filedetails/?id=3838346946) (`Execution`) | Jame | 2026-04-26 update; the fork's 2026-11-13 update |
| Proving Grounds (Shutori, `Proving Grounds`, workshop 3774745195) | [[Meitou] Proving Grounds](https://steamcommunity.com/sharedfiles/filedetails/?id=3841575072) (`ProvingGroundsMeitou`; [source](https://github.com/roscatoday/ProvingGroundsMeitou)) | roscatoday | 2026-09-26 update |
| Disarm (RE_Kenshi; Dafi, `Disarm`, workshop 3759461630) | [[Meitou Client] Disarm Port](https://steamcommunity.com/sharedfiles/filedetails/?id=3841646137) (`Disarm`) | dxseba | 2026-08-12 update |
| It. Tickles. Me (Grynn, `It. Tickles. Me`, workshop 3731488940) | [[Meitou Client] It. Tickles. Me. Port](https://steamcommunity.com/sharedfiles/filedetails/?id=3841647083) (`ItTicklesMe`) | dxseba | 2026-06-16 update |
| Kill Counter (luckyren, `KillCounter`, workshop 3779464387) | [[Meitou Client] Kill Counter Port](https://steamcommunity.com/sharedfiles/filedetails/?id=3841647109) (`KillCounter`) | dxseba | 2026-08-09 update |
| [RE_Kenshi] Level Up Notifications (Walnuts, `LevelUpNotify`, workshop 3701534964) | [[Meitou Client] Level Up Notify Port](https://steamcommunity.com/sharedfiles/filedetails/?id=3841647147) (`LevelUpNotify`) | dxseba | 2026-10-10 update |
| Speed menu (zuop, `Speed Menu`, workshop 3818592083) | [[Meitou Client] Speed Menu Port](https://steamcommunity.com/sharedfiles/filedetails/?id=3841647205) (`SpeedMenu`) | dxseba | 2026-10-14 update |
| Stack Sort (Jakematt2004, `StackSort`, workshop 3813810734) | [[Meitou Client] Stack Sort Port](https://steamcommunity.com/sharedfiles/filedetails/?id=3841647266) (`StackSort`) | dxseba | 2026-10-05 update |
| Taunt Expansion (tata, `TauntExpansion`, workshop 3771499164) | [[Meitou Client] Taunt Expansion Port](https://steamcommunity.com/sharedfiles/filedetails/?id=3841647315) (`TauntExpansion`) | dxseba | 2026-07-25 update |

The date each entry was matched against is Steam's `timeupdated` of that workshop item when it was compared; what the launcher shows
for a newer installed copy, and for other plugins, is in [launcher](../launcher.md).

## Data entries

Besides plugins, `RE_Kenshi.json` holds data that needs no DLL: `FileRebinds` (an object, path the game opens to path to open instead)
and `SoundBanks` (an array of `.bnk` files to load).

**Observed** in RE_Kenshi's source (`Settings.cpp` `LoadModOverrides`, `ParsePath`, `ResolvePath`; `FSHook.cpp`): the file of each active
mod is read in load order. Each string member of `FileRebinds` is stored under its key, so a later mod's entry for the same path
replaces an earlier one; other members are skipped. A target's `$(modroot "Name")` becomes the folder of the active mod of that name (a
name no active mod has stays as written); `HIDE` makes the file read as missing. The hooks on `_fsopen` and `FindFirstFileW` match the
exact path string the game passes (letter case and separators count; the two older spellings `data/newland/land/fullmap.tif` and `.cif`
are rewritten to the game's `data\newland/land\fullmap.tif` and `.cif`). A file that does not parse is logged and gives nothing.

Meitou (`ReKenshiFile`, `ReKenshiRebinds`, Meitou.Core) reads the enabled mods' files the same way, with the same order, targets and
`HIDE` (a hidden file is looked for in a folder where nothing is). It does not open files by the game's strings, so a key matches the
file it names: relative to the install root, `\` and `/` alike, a leading `./` and doubled slashes dropped, letter case ignored
(`data\newland/land\fullmap.tif`, `./data/newland//land/blendinfo.dat` and `./data/newland/land/blendMap.png` each name one file). So
of two mods' keys that spell one file differently the later wins, where RE_Kenshi honours only the spelling the game opens. A target's
`\` is a separator on every system. A file that is not UTF-8 is read in the game's code page (RE_Kenshi hands its bytes to the game's
file calls), and a target that is no path loses only that rebind. A rebind applies to the map files: the heightmap, `blendinfo.dat` and
`navtiles/seeds.def` (the files the game opens by a fixed path, [overview](overview.md)), the colour tiles and flow map; to every file
found through a resource group (`GameResources.Find`); to every file `AssetLocator` finds, by any of its rules (resource folders, a path
in the install, a name anywhere under `data/`): meshes, skeletons, textures and icons, and the interface's files (MyGUI layouts and the
resource, font and colour files they list, the locale template, `tips.txt`, `credits.txt`); and to a collision XML the game opens by name
because no cooked `.bin` of its name exists ([collision](collision.md#file-lookup)). A hidden file reads as missing, so hiding one the game
needs (an interface layout) stops what needs it from loading; a hidden `tips.txt` or locale template falls back to the base game's own file.
The log names each hiding rebind and its mod when the rebinds are read. Not applied: the data and level files, sound banks (`SoundBanks`
adds them), material and particle scripts (parsed from every resource folder's listing), the blood decal textures (listed from the resource
index), and a cooked collision file, since Meitou reads the XML beside it. The navigation
and foliage caches stamp the file read. **Observed:** Heightmap Fix (workshop 3426253797) rebinds `data\newland/land\fullmap.tif` to
`$(modroot "Heightmap Fix")/fullmap.tif`; enabled, Meitou reads that file (the ground at x -97830, z 85590 stands at about 504 instead of
2463). `SoundBanks` load before the game's banks ([audio](../audio.md#mod-sound-banks)); the launcher notes both entries.

The research behind not loading the DLLs (2026-10-11) follows. External findings are **Observed** in
upstream source or documentation, not Verified by running plugins. Links track upstream branches and may
change.

## What the existing interface requires

- **Observed:** RE_Kenshi's [Plugins.cpp](https://github.com/BFrizzleFoShizzle/RE_Kenshi/blob/master/Plugins.cpp)
  reads `RE_Kenshi.json` in mod directories. `PreloadPlugins` and `Plugins` are arrays of DLL
  filenames. The preload pass walks `availabelModsOrderedList` after `GameWorld::initModsList`;
  the postload pass walks `activeMods`. These are different lists: discovery must not assume
  that both passes only inspect enabled mods. DLLs load through `LoadLibraryW`, then the loader
  resolves the C++ export `?startPlugin@@YAXXZ`. Reproducing this entry point is only loading,
  not engine compatibility.
- **Observed:** [KenshiLib's README](https://github.com/BFrizzleFoShizzle/KenshiLib)
  requires the VS2010 toolchain, Boost 1.60 and KenshiLib/Ogre linkage for plugins. Its
  version-independent interface means access across supported Kenshi versions; it is not
  evidence of compatibility with another engine.
- **Observed:** [Functions.h](https://github.com/BFrizzleFoShizzle/KenshiLib/blob/RE_Kenshi_mods/Include/core/Functions.h)
  provides real-address lookup and function hooks, including an original-function pointer.
  [Character.h](https://github.com/BFrizzleFoShizzle/KenshiLib/blob/RE_Kenshi_mods/Include/kenshi/Character.h)
  exposes public fields with native offsets, virtual methods, inheritance and native containers.
  The interface permits direct memory access rather than requiring opaque handles and accessors.
- **Observed:** The official [examples](https://github.com/BFrizzleFoShizzle/KenshiLib_Examples)
  cover MyGUI widgets, dialogue conditions/effects, custom data, persistent world-state variables,
  cross-plugin exports and material changes. Support therefore extends beyond simulation callbacks.

## Meitou implications

**Observed (local source inspection):** `src/Meitou.Data/Fcs/FcsRecord.cs` stores an integer type
and named property dictionaries. This provides a useful representation for extension data;
it does not establish that every custom record survives loading, merging and saving correctly.
That must be tested with representative packages. The engine uses a managed simulation with
ordered tick phases and published snapshots, and its renderer is Vulkan ([engine](../engine.md),
[simulation](../simulation.md)). These are not Kenshi's native object layouts or hook locations.

**Assessment:** .NET can interoperate with native code; the language alone is not the blocker.
An unchanged DLL would require the binary interface it actually uses: exports, object layouts,
virtual tables, container/allocator conventions, lifetimes and callable hook targets. Direct
field writes bypass any replacement KenshiLib accessors. A native facade would need coherent
mirrors of exposed objects and synchronization at every relevant callback. Arbitrary executable
patches would add another problem.
