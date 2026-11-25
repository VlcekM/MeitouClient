# RE_Kenshi data entries

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
needs (an interface layout) stops what needs it from loading; a hidden `tips.txt` or locale template falls back to the base game's own file, and a
hidden `features.dat` places no map features ([zones](zones.md#datanewlandlandfeaturesdat-mapfeaturefile--layoutexample)).
The log names each hiding rebind and its mod when the rebinds are read. Not applied: the data and level files, sound banks (`SoundBanks`
adds them), material and particle scripts (parsed from every resource folder's listing), the blood decal textures (listed from the resource
index), and a cooked collision file, since Meitou reads the XML beside it. The navigation
and foliage caches stamp the file read. **Observed:** Heightmap Fix (workshop 3426253797) rebinds `data\newland/land\fullmap.tif` to
`$(modroot "Heightmap Fix")/fullmap.tif`; enabled, Meitou reads that file (the ground at x -97830, z 85590 stands at about 504 instead of
2463). `SoundBanks` load before the game's banks ([audio](../audio.md#mod-sound-banks)); the launcher notes both entries.
