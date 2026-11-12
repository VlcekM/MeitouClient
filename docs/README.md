# Documentation

Read only the topic/sections needed. Original-game facts use **Verified** (source/check),
**Observed** (limited evidence), **Unknown** (unresolved). Keep facts current; no development
diaries, asset dumps or decompiled output. Current choices: [DECISIONS](../DECISIONS.md).
Class layouts, field names and method names cited as **KenshiLib 1ce4a16** come from
[BFrizzleFoShizzle/KenshiLib](https://github.com/BFrizzleFoShizzle/KenshiLib) (GPLv3, Kenshi 1.0.65),
used as a naming reference for our 1.0.68 analysis; they are **Observed** until checked against our exe.

## Implementation

Not built, by design: the original's in-game level editor and fog editor (`Kenshi_LevelEditor`, `Fog_Editor`, Shift+F12; [UI input](game/ui-input.md)); the developer key tools listed there.

- [animation](animation.md)
- [bench](bench.md)
- [engine](engine.md)
- [faithful-ui-parity](faithful-ui-parity.md)
- [character-renderer](character-renderer.md)
- [character-viewer](character-viewer.md)
- [characters](characters.md)
- [impostors](impostors.md)
- [launch-options](launch-options.md): every command-line option and environment variable
- [launcher](launcher.md): game release installer, mod list and presets
- [feedback](feedback.md): the roadmap page, the in-game feedback button and its service, issue forms and the Haiku area labels
- [macos-port](macos-port.md): running on MoltenVK, its constraints and fixes; the native Metal path next to it
- [metal-backend](metal-backend.md): a native Metal path next to Vulkan - what the spike proved and what a second backend would need
- [metal-content-testability-plan](metal-content-testability-plan.md): the content/testability gaps and the order to close them
- [metal-performance-memory](metal-performance-memory.md): the Metal frame-rate and bounded-memory plan (baseline, targets, order)
- [metal-effects-parity-plan](metal-effects-parity-plan.md): MEP-I, the remaining Metal effect-parity milestones and ship gates
- [meitou-ui](meitou-ui.md): UI kit for the Meitou UI mode
- [modding](modding.md): code mods: `meitou.json`, per-mod load context, one game-shipped Harmony, tick anchors, dialogue codes, sidecar saves, UI, F10 rows
- [modding-dependencies](modding-dependencies.md): code mods building on code mods: versions, `dependencies`, shared assemblies, Arrange
- [modding-practice](modding-practice.md): code mods as authors met them: which release to build against, moving names, UI, testing
- [native-ui](native-ui.md)
- [render-clouds](render-clouds.md)
- [render-distance-benchmark](render-distance-benchmark.md)
- [render-foliage](render-foliage.md)
- [render-gi](render-gi.md)
- [render-interior-cut](render-interior-cut.md): terrain, foliage and map features cut inside buildings
- [render-lights](render-lights.md)
- [render-objects](render-objects.md)
- [render-post](render-post.md)
- [render-shadows](render-shadows.md)
- [render-shafts](render-shafts.md)
- [render-sky](render-sky.md)
- [render-terrain](render-terrain.md)
- [render-water](render-water.md)
- [renderer-native](renderer-native.md)
- [resource-layer](resource-layer.md): the resources and the commands between the renderers and the API - the proposed neutral vocabulary
- [simulation](simulation.md)
- [audio](audio.md): what the game sends to Wwise, Meitou's player, ambience rules
- [viewer](viewer.md)

## Format research

- [boost-unordered](formats/boost-unordered.md): iteration order of the exe's hash tables
- [camera](formats/camera.md)
- [clouds](formats/clouds.md)
- [collision](formats/collision.md)
- [dds](formats/dds.md)
- [fcs-mod](formats/fcs-mod.md)
- [fogfeatures](formats/fogfeatures.md)
- [gettext](formats/gettext.md)
- [localisation](formats/localisation.md): language selection, locale overlay, translations
- [havok-tagfile](formats/havok-tagfile.md): `.hkt` encoding, navmesh tiles
- [foliage](formats/foliage.md)
- [lighting](formats/lighting.md)
- [lights](formats/lights.md)
- [mygui](formats/mygui.md)
- [ogre-material](formats/ogre-material.md)
- [ogre-mesh](formats/ogre-mesh.md)
- [ogre-skeleton](formats/ogre-skeleton.md)
- [overview](formats/overview.md)
- [particle-universe](formats/particle-universe.md)
- [phs](formats/phs.md)
- [post-processing](formats/post-processing.md)
- [rekenshi](formats/rekenshi.md)
- [runtime-materials](formats/runtime-materials.md)
- [save](formats/save.md)
- [settings](formats/settings.md)
- [shadows](formats/shadows.md)
- [sky](formats/sky.md)
- [terrain](formats/terrain.md)
- [weather](formats/weather.md)
- [wwise-bnk](formats/wwise-bnk.md): sound banks, HIRC v112, Wwise Vorbis to Ogg
- [zones](formats/zones.md)

## Gameplay research

- [ai-tasks](game/ai-tasks.md)
- [ai](game/ai.md)
- [blood-decals](game/blood-decals.md): blood pools, smears and splashes on the terrain
- [luckyren](game/luckyren.md): Meitou luckyren Mode, characters bursting on a fatal hit (gear, blood, flying bones)
- [animals](game/animals.md)
- [birds](game/birds.md)
- [buildings-production](game/buildings-production.md)
- [carry-loot](game/carry-loot.md)
- [interior-editor](game/interior-editor.md): the original's layout editor, Meitou's read-only viewer (F10 Tools)
- [level-editor](game/level-editor.md): the original's in-game level editor (top bar, buildings tool, Save Mod), Meitou's (F10 Tools)
- [locks](game/locks.md): cages, beds, doors, lockable containers, lockpicking, shackles
- [combat](game/combat.md)
- [dialogue](game/dialogue.md): packages, line choice, conditions, effects, conversation window
- [economy](game/economy.md)
- [factions-squads-towns](game/factions-squads-towns.md)
- [game-loop](game/game-loop.md)
- [character-stats](game/character-stats.md)
- [pathfinding](game/pathfinding.md)
- [perception](game/perception.md)
- [ragdolls](game/ragdolls.md)
- [ui-input](game/ui-input.md)
- [ui-screens](game/ui-screens.md)
