# RE_Kenshi mod compatibility

**Decision (owner, 2026-11-10, replacing 2026-10-18's "not compatible"): compatibility is per mod, by
behaviour.** Meitou never loads RE_Kenshi plugin DLLs (`RE_Kenshi.json`, `startPlugin`) and offers no
KenshiLib facade. The public wishlist of RE_Kenshi mods decides which plugins get one of:

- **Superseded:** another mod does the same in Meitou (a [code mod](../modding.md) on the workshop); the
  launcher suggests it.
- **Redundant (built in):** a feature that fits the engine (item rotation) becomes a Meitou option or tweak.
  Enabling the RE_Kenshi mod switches the option on, with a note saying so.
- **Ported:** the plugin's DLLs are replaced by an official Meitou port against the code-mod API, its source
  in this repo, shipped with the game and run in the plugin's place when the mod is enabled ([ports](#ports)).

A mod's ordinary data and assets load through the normal readers either way.

**Status:** every plugin's DLLs are ignored, a ported one's port runs instead, and the launcher says which ([launcher](../launcher.md));
the `FileRebinds` and `SoundBanks` data entries are applied ([data entries](#data-entries)). Covered mods are one table,
`CoveredMods` (Meitou.Core), read by the game and by the launcher's Mods page. Built-in mods (`BuiltInMods`,
Meitou.Game): at start, each enabled mod the table names, by `.mod` name or code-mod id, that was not taken in before
(`UserConfig.AdoptedMods`) has its settings copied once into Meitou's setting, with a notice. A covered code mod is not run (counted as
disabled), so the feature never applies twice, and a save made while it ran does not report it gone.

| Mod | Meitou setting | Matched against | Taken in |
| --- | --- | --- | --- |
| Player-Only XP x10 (xXDarkrusoXx, `Player XP Only x10 RE`, workshop 3679678086) | Squad XP | its 2026-11-07 update | `xp_multiplier` of its `plugin/player_xp_boost.ini`, else 10 (**Observed**: the key and path are strings in its `PlayerXpBoost.dll`; the original game writes the file on its first run). Per-skill `stat_<SKILL>` lines are not kept |
| Its code-mod port (`player-only-xp`) | Squad XP | any version (by its id) | the plugin's file when the plugin is installed (the port shares it), else its saved `Multiplier`, else 10. Per-skill settings are not kept |

Superseded (suggested) mods: the RE_Kenshi plugin still loads only its data, and the launcher suggests a code mod on the Steam workshop that does the
same (one option; other ports may exist). Nothing is taken in.

| Mod | Suggested code mod | Matched against |
| --- | --- | --- |
| Loot-Scoot-Execute (Emkej, `Loot-Scoot-Execute`, workshop 3675700748; its MCM fork 3813852124) | [MeitouClient] Execution (`Execution`, workshop 3838346946) | 2026-04-26 update; the fork's 2026-11-13 update |

The date each entry was matched against is Steam's `timeupdated` of that workshop item when it was compared; what the launcher shows
for a newer installed copy, and for other plugins, is in [launcher](../launcher.md).

## Ports

`CoveredMods.Ported` (Meitou.Core) names each ported mod by `.mod` name or workshop item (its `RE_Kenshi.json` is not checked) and its
port's code-mod id. A mod matches one kind, first of built in, ported, suggested; a mod with its own `meitou.json` runs that, never a port,
so an RE_Kenshi mod's author ships a Meitou version by putting a code mod's `meitou.json` and DLL in the mod's folder.

| Mod | Port | Matched against |
| --- | --- | --- |
| Proving Grounds (Shutori, `Proving Grounds`, workshop 3774745195) | Proving Grounds for Meitou (roscatoday, `ProvingGroundsMeitou`, `PortedMods/ProvingGrounds`) | 2026-09-26 update |

- **Source:** `PortedMods/<Name>/`, one class library per mod setting `PortId` (its `meitou.json` id) with `meitou.json` beside it, listed
  in `MeitouClient.slnx`; the authors' code keeps their style (`PortedMods/Directory.Build.props` turns the style checks off).
  `PortedMods/Directory.Build.targets` references the game (`Private="false"`) and after each build copies the DLL and manifest to
  `PortedMods/<PortId>/` beside the game's Debug or Release build; `package-release.ps1` builds each port into the zip's `PortedMods/`
  with the game's version (**Observed**, scratch run: a port built against an unversioned game is refused by a 0.1.0 one).
  A port's code is one DLL: no libraries of its own.
- **Running:** `ModHost.CodeMods` puts an enabled ported mod in its `mods.cfg` place as the port (`ModInfo.WithPort`): manifest and
  assemblies from `PortedMods/<PortId>/` beside the executable (`ModInfo.CodeFolder`, `ModContext.CodeFolder`), name, folder and data the
  mod's, so `ModContext.Folder` holds the plugin's settings files. It then loads as any code mod; the JIT warm-up and `mod-check` see it.
  Another enabled code mod with the port's id (a workshop port) is not run, so the feature applies once. A port missing from the game's
  folder is logged and the plugin does not run. **Verified** by `ModdingTests.A_ported_mod_runs_its_port_in_its_place` (temporary install).
  **Observed** (2026-11-18, harness, no game data): Proving Grounds' DLL loads from `PortedMods/ProvingGroundsMeitou` in the mod's
  place and its `Initialize` runs.
- **Observed** (2026-11-18, scratch install, scratch port and harness outside the repo, Release): the port built through the targets
  file landed in the game's `bin/Release/net11.0/` port folder; enabled, it ran in the plugin mod's place with that mod's name and folder and a
  workshop-style mod of the same id was skipped; with the port's folder gone the plugin ran nothing and the log said so.

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
`navtiles/seeds.def` (the files the game opens by a fixed path, [overview](overview.md)), the colour tiles and flow map, and every file
found through a resource group (`GameResources.Find`); rebinds of other files are not applied. The navigation and foliage caches stamp
the file read. **Observed:** Heightmap Fix (workshop 3426253797) rebinds `data\newland/land\fullmap.tif` to
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
