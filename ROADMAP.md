# Roadmap

Goal: a drop-in replacement for `kenshi_x64.exe` that loads the original `data/`, `mods/` and
saves unchanged. Compatibility is the constraint everything else bends around, so work goes from
the data inward and rendering comes late.

## Ground rules

- **No assets in the repo.** Everything is read from the user's install (`KENSHI_PATH` /
  `meitou.local.json`). `.gitignore` blocks Kenshi asset extensions so fixtures can't leak in;
  tests that need the game skip when it isn't configured.
- **Clean-room readers for proprietary formats.** Ogre, MyGUI and ParticleUniverse are MIT, so
  porting their code/format logic is fine. Havok, PhysX and Wwise are proprietary: we write our
  own readers from observed file structure, never link their SDKs or redistribute their DLLs.
- **Behaviour from observation.** Game logic is reimplemented from the FCS schema, the data, and
  observed in-game behaviour; we don't copy code out of `kenshi_x64.exe`.

## Original engine (from the install)

Ogre 3D (Direct3D 11 render system, Octree scene manager, Terrain plugin, MeshLodGenerator,
Overlay), MyGUI, ParticleUniverse, SkyX, PhysX 2.x (`PhysXLoader64`, `NxCharacter`), Havok
(animation / navmesh), Wwise (`.bnk`), OIS input, Steamworks.

## Format inventory

See [docs/formats/overview.md](docs/formats/overview.md) for every format found in the install
and how far each is analyzed. Detailed layouts live next to it in `docs/formats/`.

## Technology choices (defaults, open to change)

- **Rendering:** Silk.NET (D3D11 first, matching the original HLSL; Vulkan/OpenGL later for Linux).
- **Physics:** BepuPhysics2 (pure C#), tuned to match PhysX character/ragdoll behaviour.
- **Audio:** own Wwise bank/WEM decoder → OpenAL Soft (Silk.NET).
- **UI:** own renderer for MyGUI `.layout` / skins, so GUI mods keep working.
- **Input/windowing:** Silk.NET.

## Phases

1. **Data layer** — FCS `.mod`/`.base` reader *and writer*, `fcs.def` schema, mod load order
   (`mods.cfg`, `data/*.mod`, Steam workshop), record merging/overrides exactly as the original.
   Round-trip tests against every base-game file. Evaluate OpenConstructionSet (existing C# FCS
   library) as reference or dependency.
2. **Ogre assets** — mesh/skeleton/material/shader readers, texture loading; a model viewer tool.
3. **World** — terrain (`.raw`), zones, levels, buildings, foliage; a free-camera world viewer.
4. **Havok** — tagfile reader, animation decompression, skeletal animation playback; navmesh tiles.
5. **Simulation core** — game clock, characters, stats, inventory, factions, squads, AI packages /
   tasks, dialogue (`Dialogue.mod`), combat, economy. Headless and deterministic, independent of
   rendering.
6. **Saves** — read and write original save games.
7. **Presentation** — full renderer, MyGUI-compatible UI, audio, particles, physics/ragdolls.
8. **Parity** — side-by-side comparison against the original, mod compatibility test suite.

## Open questions

- How far to support script-extender mods (RE_Kenshi / KenshiLib plugins hook native code and
  can't work as-is in a managed engine).
