---
title: Meitou 0.17.0
date: 2026-11-08
author: VlcekM
image: https://meitou.kenshi.wiki/media/0.17.0.webm
---

Hey hey everyone 😁,

0.17 is here, and we took our sweet time getting it out. It's got almost 2000 changes since 0.16, almost two weeks ago.

Characters now *think* like in Kenshi. Your squad and every NPC choose what to do
the way the base game does, so who joins a fight, who comes to help, who runs, who heals and who goes to bed all follow its rules.
Same for senses: who sees you, who hears you sneaking, and when a squad tells the others what it saw. Huge thanks to **Bazouz660**,
who reimplemented original Kenshi's behavior for this release 😎

# Code mods

Mods can now ship their own C# code, not only data. A code mod gets its own settings page, can depend on other mods, and keeps its
own data in your saves. Mods' own world files load too, so towns and buildings a mod adds show up.
We got documentation on how to mod Meitou on our [Wiki](https://github.com/VlcekM/MeitouClient/wiki/Writing-code-mods).

# Today's subject: Slavery

Slavers cuff, strip and carry, workmasters actually work their slaves, escaped slaves lie low, and jail rounds work as in
the game. 

We also did a lot of work on loot and nests, so now nests lay their eggs, loot and fogmen poles, and towns spawn artifacts.

# Frame generation and the new graphics features

DLSS and FSR 3 frame generation, on Linux too. Great if you want to double your FPS and don't care about input lag that much. I highly recommend using the new 
enhanced hardware cursor to go along with this.

The Meitou look has some new toys: blood splashes on walls and floors, heads can come off, ragdolls fly with the blow, and dust and
smoke are lit by the sun 😎 The amazing @pupumer also implemented some advanced terrain rendering options if you have too much FPS.

# Performance

We spent a good chunk of this release on stutters, loading and memory: faster path searches, cheaper terrain
and UI, and smaller meshes. The game shouldn't show white textures if your VRAM is on the lower end.

Many thanks to everyone who contributed code this time: Bazouz660, Demitrix2k, doublevoid, CrockMr, dfdffish (Simplified Chinese!),
GosuBoy, dxseba, roscatoday, TheIvoryDisaster, asc3ns10n and LucasSKrewer, and to all of you sending feedback.

Full changelog:

- Code mods: mods can ship C# code (meitou.json) with dependencies, load order, a settings page and save data; mod-check in meitou-tools
- Mods' own leveldata loads, so towns and buildings a mod adds to the world appear
- AI decides as the original: player characters and NPCs pick goals and jobs through one chooser; NPCs join fights only by their combat goals
- Senses as the original: sight, hearing and squad sightings; every awake NPC spots sneakers, and being heard trains stealth
- Combat: defenders follow the swing coming at them, fighters walk to a target's floor instead of hitting through it, crowd fights 3x cheaper
- Ranged: bolts hit the original's hull boxes, crossbow reloads pause instead of restarting, bolt tracers
- Slavery: slavers cuff, strip and carry captives, workmasters work their slaves, escaped and ex-slaves, torture racks
- Prisons: release rounds and freed prisoners as the original, Lock / Unlock on own cages; lockpick and stealth KO pointers show their chance
- Towns: nests lay their eggs, loot and debris, ruined homes, artifacts, town machines and production chains, patrols that stroll
- Raids, campaigns, roaming squads, prosperity and town wipe-outs follow the original's rules
- Birds: flocks by biome that perch on buildings and features
- Building: repair broken doors, build in ruins, wall snapping to town walls, research paid from the benches round the camera
- Trade walks up like Talk and opens in talk range; any selected character in reach trades; fencing chance shown; bought animals are named
- Bought town houses are emptied of furniture, items and residents, and what you place in them joins the town
- Frame generation: DLSS (Streamline) or FSR 3, also on Linux; DLSS preset setting; render scale modes from Ultra performance to DLAA
- Graphics settings rebuilt: Faithful or Meitou looks with presets you can save and share, the same rows in the game and the launcher
- Meitou effects: blood on walls and floors (saved), decapitation, advanced ragdolls, sun-lit dust and smoke, KenshiNative's bloom
- Severed limbs and stumps bloody and skin-tinted; clothing colours from the character's or faction's scheme; belt-slot armour drawn
- Performance: navmesh with half the polygons and faster path searches, cheaper terrain, UI and characters, fewer GC stalls (#124)
- Memory: object meshes about half the size; under VRAM pressure textures load smaller instead of not at all, so 4 GB cards show the start town
- Loading: character LOD and colour map disk caches; the loading screen waits only for what is near the camera
- Navmesh: map features walkable (Mourn, Swamp Village), sealed interiors joined by their doors, half-unit steps (#357, #358)
- Controls: rebindable mouse buttons and squad number keys, Fast and Fastest speed sliders, enhanced hardware mouse, camera speed and invert
- In-game Feedback button: sends a screenshot, save and log as a GitHub issue
- Simplified Chinese; crime names and run-time messages translated; translation mods no longer break new games (#200)
- Launcher 1.5-1.9: Continue the last save, mods From save, the mod list saves itself, Arrange by dependencies, resizable window
- Audio: sound on GOG installs (#336), output reopens on a device change (#309), mining sounds for NPC workers
- Fix sunk cannibal cages (#258), FCS prosthetics (#224), high heels sinking (#126), Hub walls in the Shinobi tower (#111), AMD spikes (#197)
- Fix the indoor camera bobbing (#204), carried animals falling off (#176, #307), miners working from afar (#332), swimming on sunken floors (#345)
