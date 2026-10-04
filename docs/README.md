# MeitouClient docs

Research notes on the original game. Every claim is marked with how sure we are:

- **Verified**: checked against real files (say which, and how).
- **Observed**: seen in some files, not yet confirmed everywhere.
- **Unknown**: open question or guess.

Update these whenever a reader or experiment teaches us something new. Never paste copyrighted
game content (dialogue text, large dumps) here; identifiers, counts and byte layouts are fine.

| Doc | Topic |
| --- | --- |
| [formats/overview.md](formats/overview.md) | Install layout, load order, resource lookup, mod overrides, format inventory |
| [formats/fcs-mod.md](formats/fcs-mod.md) | FCS game data: `.base` / `.mod` binary layout, record types, overrides, `fcs.def` schema |
| [formats/ogre-mesh.md](formats/ogre-mesh.md) | Ogre `.mesh` models: versions, chunk layout, vertex formats, LOD |
| [formats/ogre-skeleton.md](formats/ogre-skeleton.md) | Ogre `.skeleton`: bones, animations, keyframes; mesh links |
| [formats/ogre-material.md](formats/ogre-material.md) | Ogre scripts (`.material`, `.program`): syntax, inheritance, material model, lookup |
| [formats/runtime-materials.md](formats/runtime-materials.md) | How the game builds materials from FCS records: templates, flags, texture units, shader parameters |
| [animation.md](animation.md) | How the game uses skeletons and animations: attachments, animation sources, layers, blending, appearance sliders |
| [characters.md](characters.md) | Assembling a character: `.bod2` body files, race/head/hair/clothing/weapon choice, attachment points, body and hair shading, face poses |
| [formats/phs.md](formats/phs.md) | Scythe physics `.phs` files: layout, attachment points |
| [formats/terrain.md](formats/terrain.md) | World coordinates, zone grid, `fullmap.tif` heightmap, how biomes texture the terrain (`blendinfo.dat`, blend/overlay/colour maps), legacy height tiles |
| [formats/zones.md](formats/zones.md) | `.zone` / `.level` world-state files, building and town placements, roads, `features.dat`, placements to meshes |
| [formats/dds.md](formats/dds.md) | DDS textures: headers, block formats, base-game survey |
| [viewer.md](viewer.md) | `meitou-viewer`: usage, how textures are resolved from FCS records, world mode (terrain + placed objects), known gaps |
