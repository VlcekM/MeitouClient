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
| [animation.md](animation.md) | How the game uses skeletons and animations: attachments, animation sources, layers, blending, appearance sliders |
