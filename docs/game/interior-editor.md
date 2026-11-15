# Interior editor

The original's dev tool for building layouts (furniture inside, signs and NPCs outside) and Meitou's
version. Layout data: [zones](../formats/zones.md) (`interiors.level`, `serialised interior`),
`BuildingLayouts` (`Meitou.Data.World`).

## Original

- **Window** (`Kenshi_InteriorMode.layout`, window caption "Layout"; **Verified** by reading the file):
  - `intlistwin`, "[INTERIOR] (furniture)": `intlistbox` (layouts), `intnamebox` (name), `intsave`,
    `intdel`, `titlelabel` (the building; the code sets "None selected"), `centerbutton` "->".
  - `extlistwin`, "[EXTERIOR] (signs and NPCs)": `extlistbox`, `extnamebox`, `extsave`, `extdel`.
  - `interior` panel: `interiorMode` "INTERIOR EDIT MODE", `showhide` (the code sets "Show Interior"),
    `clear` "CLEAR", `clearnodes` "Clear Nodes".
- **Delete** asks "This will remove this interior (exterior) for all buildings of this type that
  reference it. Are you sure you want to delete it?": a layout belongs to the building type, not to
  one building (**Verified**: `INSTANCE_COLLECTION` records keyed by `associated building`, used by name
  through a state's `interior layout name` / `exterior layout name`, `BuildingLayouts`).

## Meitou

F10 → Tools → "Interior editor..." (`InteriorEditorHost`). Read only.

- Shows the selected level building (left click; the panel follows a new selection; a player's site
  has no layouts), its type's interior or exterior layouts (Tab) including those of `shares interiors with`,
  the one its state uses (`*`), part counts, `is bar` / `is shop`.
- Enter lists a layout's parts: instance id, record name, floor (`floornum`), position relative to the
  building, yaw. Parts are marked on screen (picked one red with its name). C centres the camera on the
  picked part (the original's "->"). Esc goes back, then closes.

## Unknowns

- Meitou does not read `mods/<mod>/leveldata/` (only `data/newland/leveldata/<mod>/`), so mod layouts
  such as the ones above are not shown.
