# Roadmap

Goal: a drop-in replacement for `kenshi_x64.exe` that loads the original `data/`, `mods/` and
saves unchanged. Compatibility is the constraint everything else bends around, so work goes from
the data inward. The viewers came early because they are the quickest check that a reader is right.

## Ground rules

- **No assets in the repo.** Everything is read from the user's install (`KENSHI_PATH` /
  `meitou.local.json`). `.gitignore` blocks Kenshi asset extensions so fixtures can't leak in;
  tests that need the game skip when it isn't configured.
- **Clean-room readers for proprietary formats.** Ogre, MyGUI and ParticleUniverse are MIT, so
  porting their code/format logic is fine. Havok, PhysX and Wwise are proprietary: we write our
  own readers from observed file structure, never link their SDKs or redistribute their DLLs.
- **Behaviour from facts.** Game logic is reimplemented from the FCS schema, the data, observed
  in-game behaviour, and facts learned by decompiling the original binaries for interoperability
  (formats, merge rules, formulas, constants). Decompiled output never enters the repo and code is
  never copied or translated from it; the facts go into `docs/` in our own words (see CLAUDE.md).

## Original engine (from the install)

Ogre 2.0 "Tindalos" (Direct3D 11 render system, Octree scene manager, Terrain plugin, MeshLodGenerator,
Overlay), MyGUI, ParticleUniverse, SkyX, PhysX 2.x (`PhysXLoader64`, `NxCharacter`), Havok
(navmesh; Behavior/Animation is linked but characters animate through Ogre, see
[docs/animation.md](docs/animation.md)), Wwise (`.bnk`), OIS input, Steamworks.

## Format inventory

See [docs/formats/overview.md](docs/formats/overview.md) for every format found in the install
and how far each is analyzed. Detailed layouts live next to it in `docs/formats/`.

## Technology choices (defaults, open to change)

- **Rendering:** Silk.NET, Vulkan only (OpenGL was removed, DECISIONS 18). The renderers write GL-shaped calls (`IGl`) with our own
  GLSL (Kenshi's HLSL is read for facts only); `VkGl` translates them onto Vulkan 1.3.
- **Physics:** BepuPhysics2 (pure C#), tuned to match PhysX character/ragdoll behaviour.
- **Audio:** own Wwise bank/WEM decoder → OpenAL Soft (Silk.NET).
- **UI:** own renderer for MyGUI `.layout` / skins, so GUI mods keep working.
- **Input/windowing:** Silk.NET.

## Phases

Status as of 2026-10-05. Details and open questions live in the linked docs.

1. **Data layer** — *done.* FCS `.mod`/`.base` reader and writer (byte-identical round trip of every
   base-game file), `fcs.def` types, load order (`mods.cfg`, `mods/`, Steam workshop) and record
   merging as `kenshi_x64.exe` does it, which differs from the mod editor in places
   ([fcs-mod.md](docs/formats/fcs-mod.md)). Open: derived `wall master` references; workshop
   subscriptions are approximated by every downloaded item.
2. **Ogre assets** — *done.* Mesh, skeleton, material/shader script and DDS/image readers;
   resource lookup as Kenshi registers it; runtime materials from FCS records; `meitou-viewer`.
   Characters ([characters.md](docs/characters.md)): body, head, hair, armour, weapons, `.phs`
   attach points, `.bod2` appearance, body-shape bone scales, face poses, mesh LOD, blended
   animation layers, and a seeded generator for loadout and random appearance. Open: clipping of
   hidden body parts, muscle normal blend, stump meshes, the game's exact random sequence.
3. **World** — *mostly done.* `fullmap.tif` heights, zone/level files, `features.dat`
   ([terrain.md](docs/formats/terrain.md), [zones.md](docs/formats/zones.md)); `meitou-viewer --world`
   with CDLOD terrain to the horizon textured by biome, water, sky and sun path, and buildings
   assembled like the game (seeded part choice, town materials, doors, destroyed states). In
   progress: detail streaming around the camera, infinite sea. To do: foliage (including the
   visible mineable rocks), building LOD, scene reflections, global pathing data.
4. **Havok** — *not started.* Tagfile (`.hkt`) reader; navmesh tiles
   (`newland/land/navtiles`). Character animation does not need it (Ogre skeleton animations).
5. **Simulation core** — *not started.* Game clock, characters, stats, inventory, factions, squads,
   AI packages / tasks, dialogue (`Dialogue.mod`), combat, economy. Headless and deterministic,
   independent of rendering. The character generator is its first piece.
6. **Saves** — *not started.* Read and write original save games. Known so far: stat field names
   from the save writer.
7. **Presentation** — *partly.* `meitou` boots into the world with the Kenshi camera on a fixed 30 Hz tick with interpolated
   drawing (`src/Meitou.Engine`, `src/Meitou.Game`, [docs/engine.md](docs/engine.md)); the world renderers are a library
   (`src/Meitou.Rendering`, on Vulkan), shared with the viewer. To do: game renderer
   (shadows, effects) with graphics options (terrain LOD distance, now fixed at its useful maximum, and
   material distance), MyGUI-compatible UI, audio, particles, physics/ragdolls.
8. **Parity** — *not started.* Side-by-side comparison against the original, mod compatibility
   test suite.

## Open questions

- How far to support script-extender mods (RE_Kenshi / KenshiLib plugins hook native code and
  can't work as-is in a managed engine).
