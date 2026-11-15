# Interior editor

The original's dev tool for building layouts (furniture inside, signs and NPCs outside) and Meitou's
version. Layout data: [zones](../formats/zones.md) (`interiors.level`, `serialised interior`),
`BuildingLayouts` (`Meitou.Data.World`).

## Original

- **Part of the level editor.** **Observed** (disassembly, 2026-10-22): the `InteriorModeButtonWindow`
  (`FUN_14077a1f0`, layout `Kenshi_InteriorMode.layout`) is built by the level editor's setup
  (`FUN_14077be90`, which also reads the `editor` mod from [settings.cfg](../formats/settings.md)). The
  level editor toggles with `editor_toggle`, Shift+F12 ([keys](ui-input.md)). How a release build
  enables it is **Unknown**.
- **Window** (`Kenshi_InteriorMode.layout`, window caption "Layout"; **Verified** by reading the file):
  - `intlistwin`, "[INTERIOR] (furniture)": `intlistbox` (layouts), `intnamebox` (name), `intsave`,
    `intdel`, `titlelabel` (the building; the code sets "None selected"), `centerbutton` "->".
  - `extlistwin`, "[EXTERIOR] (signs and NPCs)": `extlistbox`, `extnamebox`, `extsave`, `extdel`.
  - `interior` panel: `interiorMode` "INTERIOR EDIT MODE", `showhide` (the code sets "Show Interior"),
    `clear` "CLEAR", `clearnodes` "Clear Nodes".
- **Editing** (**Observed**, from exe strings, flow not traced): with interior edit mode on, objects
  placed in the building go into its interior layout. A sign placed with the mode off warns that it
  will not be added to the exterior layout. Some other action refuses with "Nope - You are in
  interior edit mode".
- **Delete** asks "This will remove this interior (exterior) for all buildings of this type that
  reference it. Are you sure you want to delete it?": a layout belongs to the building type, not to
  one building (**Verified**: `INSTANCE_COLLECTION` records keyed by `associated building`, used by name
  through a state's `interior layout name` / `exterior layout name`, `BuildingLayouts`).
- **Save Layout** reports "Skipped N duplicated buildings and M duplicated nodes saving this layout"
  (`FUN_14055ee00` area). "Interiors that need re-saving:" is a check report. **Observed**.
- **Town warning**: "Automatic building replacements are active. You may lose data editing existing
  interiors in this town", when the town record's `buildings replacements` is not empty (`FUN_1407779c0`).
  **Observed**.
- **Where it saves**: the level editor's Save Mod writes into the mod named by `editor`
  ([settings](../formats/settings.md); [level editor](level-editor.md)). Mods ship layouts as `mods/<mod>/leveldata/interiors.level`
  (Unofficial Patches, CATO V4). The base game has `data/newland/leveldata/interiors.level`. **Observed**.
  Whether Save Mod writes `interiors.level` itself is **Unknown**.

## Meitou

F10 → Tools → "Interior editor..." (`InteriorEditorHost`). Read only.

- Shows the selected level building (left click; the panel follows a new selection; a player's site
  has no layouts), its type's interior or exterior layouts (Tab) including those of `shares interiors with`,
  the one its state uses (`*`), part counts, `is bar` / `is shop`.
- Enter lists a layout's parts: instance id, record name, floor (`floornum`), position relative to the
  building, yaw. Parts are marked on screen (picked one red with its name). C centres the camera on the
  picked part (the original's "->"). Esc goes back, then closes.
- Not done: interior edit mode, placing/moving/deleting parts, save, delete, clear, clear nodes.
  Saving, when added, must write to Meitou's own folder or a separate mod, never into `data/` or
  an installed mod.

## Unknowns

- How the level editor is enabled in a release build.
- What CLEAR and Clear Nodes remove; what `showhide` switches.
- The save path and format details of a layout save.
- Meitou does not read `mods/<mod>/leveldata/` (only `data/newland/leveldata/<mod>/`), so mod layouts
  such as the ones above are not shown.
