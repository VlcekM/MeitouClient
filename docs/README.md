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
| [animation.md](animation.md) | How the game uses skeletons and animations: attachments, animation sources, layers, blending, appearance sliders and the body-shape formulas (bone sizes, muscle, starvation, missing limbs) |
| [characters.md](characters.md) | Assembling a character: `.bod2` body files, race/head/hair/clothing/weapon choice, how the game rolls an NPC (clothing, quality, weapons, manufacturers, random face/body from editor limits), attachment points, body and hair shading, hidden body parts (part maps), face poses, LOD of character meshes |
| [formats/phs.md](formats/phs.md) | Scythe physics `.phs` files: layout, attachment points |
| [formats/collision.md](formats/collision.md) | Static collision: PhysX NxuStream `.xml` / `.bin` files of building parts and foliage, cooked mesh layouts, placement (axis change, building node, scale), collision groups, shape-to-triangle rules for the navmesh |
| [formats/terrain.md](formats/terrain.md) | World coordinates, zone grid, `fullmap.tif` heightmap, how biomes texture the terrain (`blendinfo.dat` and its slot-mask quadtrees, blend/overlay/colour maps), terrain LOD, water (level 100, planes, biome parameters), sun path, legacy height tiles |
| [formats/sky.md](formats/sky.md) | SkyX (the sky): scattering model and derived constants, night glow, starfield, moon, clouds, WEATHER records, the CONSTANTS sky values; how the viewer reproduces them (atmosphere, aerial perspective) |
| [formats/clouds.md](formats/clouds.md) | The SkyX cloud layer: options, the shader step by step, density/darkness from the weather, wind drift, coverage by density, lighting at sunset and night; no volumetric clouds or cloud shadows |
| [formats/particle-universe.md](formats/particle-universe.md) | ParticleUniverse `.pu` scripts: grammar, a survey of the 93 base-game scripts (systems, techniques, emitters, affectors, observers, dynamic attributes), the particle materials and their shaders, the CPU simulation and billboard pass as built, the camera effects (rain, ash) and the cube `d` they wrap into |
| [formats/weather.md](formats/weather.md) | Weather: WEATHER / SEASON / BIOME_GROUP / EFFECT records, regions from `areasmap.tga`, seasons, weighted weather choice and durations, wind, fog blend at borders, sky transition, rain, wetness, dust, heat haze, particle effects and placers, sounds; base-game table; implementation plan |
| [formats/fogfeatures.md](formats/fogfeatures.md) | Placed fog volumes (`fogfeatures.dat`): file layout, the 28 base-game volumes (the swamp's fog, the Fog Islands', the Vain's red haze), plane sign, how the game draws them after the haze (`fog_planes_fs`), the viewer's version: one full-screen pass over the scene |
| [formats/camera.md](formats/camera.md) | The game's strategy camera: pivot and boom, zoom 10 to 2000, pitch limits, at most 1840 above the pivot, settings, free camera; the viewer's altitude band above it |
| [formats/lighting.md](formats/lighting.md) | How the deferred scene is lit: sun colour and daylight factor, irradiance and specularity cubes, the biome ambient map, the BRDF terms, exposure from CONSTANTS; how the viewer reproduces them |
| [formats/lights.md](formats/lights.md) | Placed point and spot lights: the LIGHT record, light instances on buildings, parts and layouts, power rolls, the light pass (attenuation, spot, effects), when lights are on; world counts; `WorldLights` |
| [formats/shadows.md](formats/shadows.md) | Sun shadows: the `shadow mode` / `shadow quality` / `Shadow Range` settings, CSM (splits, stable fitting, snapping, caster bias, 12-tap PCF), RTW, which materials cast, how the term enters the lighting; the viewer's shadow pass |
| [formats/foliage.md](formats/foliage.md) | Foliage: FOLIAGE_LAYER / FOLIAGE_MESH / GRASS records, the per-zone placer (random numbers, noise, grass coverage, clusters, rules), distances, materials, grass blades and shader, the zone layout cache |
| [formats/zones.md](formats/zones.md) | `.zone` / `.level` world-state files, building and town placements, roads, `features.dat`, placements to meshes |
| [formats/dds.md](formats/dds.md) | DDS textures: headers, block formats, base-game survey |
| [formats/post-processing.md](formats/post-processing.md) | Kenshi's render chain: HDR lighting, exposure (no tone curve), bloom (off, and none in Meitou), FXAA (the viewer's Faithful anti-aliasing), SSAO (disabled), heat haze (after FXAA, flow-map shimmer growing to 8333 units), what is absent; how the viewer maps them |
| [formats/settings.md](formats/settings.md) | Every `settings.cfg` key (type, range, default, clamps, options label, what it drives, restart) and the `kenshi.cfg` keys: terrain detail / chunk size and the terrain LOD test, texture quality mip skipping, water reflection levels and range, decals, shadows, distances, audio, gameplay; what the viewer honours |
| [formats/save.md](formats/save.md) | Save games: where they live, folder layout, the FCS type-15 files (`quick.save`, `.platoon`, `.zone`), handles and slot lists, what is saved vs derived vs regenerated, the save/load chain |
| [game/game-loop.md](game/game-loop.md) | Frame structure, frame time, game speed and pause, the in-game clock, one world update in order, threads (AI back thread), the character update budget, zones near and far |
| [game/character-stats.md](game/character-stats.md) | Stats and skills (numbering, XP gain and levelling), hunger, blood and bleeding, body parts and injuries, first aid, limb replacements, races and their modifiers, encumbrance |
| [game/combat.md](game/combat.md) | Combat: the CONSTANTS tuning, weapons, technique choice, block and dodge, armour, the hit pipeline, KO, bleeding and death, ranged weapons and turrets, combat XP |
| [game/ai.md](game/ai.md) | AI: AI_PACKAGE / AI_TASK records, the squad Blackboard, the per-character decision loop, the GOAP planner, jobs and player orders, unloaded squads, senses, dialogue triggers |
| [game/ai-tasks.md](game/ai-tasks.md) | AI tables: `taskType` to task class, the TaskData registry, state type ids |
| [game/factions-squads-towns.md](game/factions-squads-towns.md) | Factions and relations, squad templates and how squads are created and spawned, towns, debris, loot and building contents |
| [game/pathfinding.md](game/pathfinding.md) | Pathfinding: the Havok navmesh manager, `navtiles` tiles, runtime generation, path queries, movement speeds, off-screen movement |
| [game/economy.md](game/economy.md) | Economy: money, item value and unit price, trader and town market factors, buying, selling and fencing, shop stock and restock, blueprints and research cost, bounties, slaves and prisoners, loot |
| [game/buildings-production.md](game/buildings-production.md) | Buildings: construction, production and crafting, power, storage, farming, research and tech |
| [game/ui-input.md](game/ui-input.md) | Input: the binding encoding, `controls.cfg`, the action table, key and mouse handling, selection, pointer modes, camera input, `settings.cfg` and the options window |
| [game/ui-screens.md](game/ui-screens.md) | UI screens: MyGUI stack and resources, the screen catalog, main menu and new game flow, HUD, overview, inventory, trade, dialogue, build, tutorials |
| [viewer.md](viewer.md) | `meitou-viewer`: usage and options, how textures are resolved from FCS records, the world mode's keys, profiler and spike log, known gaps; links the render docs below |
| [render-terrain.md](render-terrain.md) | World view terrain: CDLOD, texturing, streaming of heights, textures and meshes, upload and unloading, frame times |
| [render-water.md](render-water.md) | World view water: the game's flat water and Meitou water (FFT ocean, shore distance field, breakers, foam, river map) |
| [render-sky.md](render-sky.md) | World view sky, sun, light and atmosphere |
| [render-objects.md](render-objects.md) | World view buildings and map features: assembly, object streaming, LOD, draw distance (`reach`), distant towns |
| [render-foliage.md](render-foliage.md) | World view foliage: paging, GPU cull, ranges, impostors, reflections, measurements |
| [render-shadows.md](render-shadows.md) | World view shadows: the game's CSM, Meitou shadows, the shadow pass cost |
| [render-post.md](render-post.md) | World view post-processing: the HDR framebuffer, SSAO, fog, exposure, upscalers |
| [render-gi.md](render-gi.md) | Ray-traced global illumination (Meitou, optional): device, acceleration structures, debug views, plan |
| [render-shafts.md](render-shafts.md) | Light shafts (Meitou `shafts` switch): the haze and weather fog darkened where the sun is shadowed along the view; froxel grid, cost |
| [render-lights.md](render-lights.md) | Lamps: the game's point and spot lights on the world (WorldLamps), their grid, shading, cost and parity |
| [bench.md](bench.md) | The benchmark harness (`--view`, `--ab`, `--bench-frames`), start-up time and the load caches (`--no-load-cache`), `--tiered-jit` / `--pgo` |
| [character-renderer.md](character-renderer.md) | The native character renderer: `--crowd` harness, per-instance bone and material buffers, instancing per mesh part, LOD and shadow choices, measurements, what is left |
| [character-viewer.md](character-viewer.md) | The removed mesh and character viewer (DECISIONS 23, tag `model-viewer-last`): options, mesh and skeleton loading, skinning and the bone block, animation blending, body shape, character shading inputs, LOD, camera, lighting, verified findings, gaps; for the later native character renderer |
| [engine.md](engine.md) | Engine architecture: projects, the game loop (fixed tick, interpolation, game time, input bindings), the Kenshi and free cameras, the renderers and the backend interface, how to check renderer changes |
| [simulation.md](simulation.md) | The simulation core (phase 5) as built: stage status, time model and session, the tick phases, threading and determinism (golden hashes), the world model and character table, the system order, then one section per system (population, player, movement and paths, navmesh, animation, bodies, items and eating, combat, saves), the game host, measurements, open questions, owner decisions |
| [impostors.md](impostors.md) | Far-billboard impostors (Meitou mode): hemi-octahedral layout, size classes and compression, the bake, the `.mimp` format and cache, the sampling GLSL, transition distance and crossfade, `--impostor-preview` measurements |
| [render-distance-benchmark.md](render-distance-benchmark.md) | What stops drawing everything larger than 1 px: the 1 px distances per class, range sweeps (GPU per pass, render thread, draws, triangles, VRAM, streaming), the ranked bottlenecks, the wave-4 recording modes, proposed fixes with expected gains |
| [renderer-native.md](renderer-native.md) | The native Vulkan renderer API (built; `IGl`/`VkGl` deleted in phase 8, 8.9): API surface, shaders, the former coexistence seam, GPU-driven foliage and objects, threading, the parallel port plan and parity gate, phase 8 |
