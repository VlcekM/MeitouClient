# Changelog

What changed in each release, newest first. The release workflow copies the section of the version it builds into the GitHub release
notes and refuses to release a version without one: add a `## vX.Y.Z` section with one `- ` line per change before running it.

## v0.0.3

- Weather: the game's weather schedule per region and season drives the sky, clouds, fog and heat haze; Tab panel button rerolls the weather
- Weather effects: rain, ash and other weather particles, fog banks, wet ground, objects and plants in the rain, dust on buildings and rocks, rain ripples on water
- Cloud layer drawn like the game's, drifting with the wind
- Animations: smoother blending between moves, the game's movement and attack speeds, combat clips, stumbles and falls
- Landmarks cast their shadows however far away they are
- Trees turn into billboards at the normal distance but are drawn as far as before; rocks and ruins keep their full meshes to 12000
- Characters no longer look dirty from ambient occlusion
- Low-end GPUs: shadow filter setting (Tab, --shadow-filter) and the shadow distance slider goes to 0 to turn shadows off
- Faster: weather particles, and small plants no longer drawn into the far shadow cascades
- Sandbox mode (--sandbox) for testing movement and animations on a flat grid

## v0.0.2

- The world comes alive: the Hub's residents, bar patrons and roaming squads walk around with walk, jog, run and idle animations
- New game: pick a start, select your squad with a click or a box, right-click to move (Shift queues orders), HUD with health, skills and inventory
- Pathfinding on a navmesh, through building doors and interiors
- Characters drawn with bodies, clothing, hair, weapons and facial expressions; crowds use generated detail levels
- Bodies and medical state, stats and XP, encumbrance, eating; first melee combat (attacks, blocks, armour, knockouts)
- Game keys: F10 key list, F11 statistics, F12 profiler, F8 / Print Screen screenshot, Shift+F1.. the Faithful / Meitou switches
- Tab panel: Faithful / Meitou switches as checkboxes (saved), "Delete billboard cache" button
- Faster terrain and tree billboards (about 15% less GPU time per frame in forests)
- Small plants drawn to 3500 (was 800); large trees and rock stacks stay full meshes to 12000 before turning into billboards
- Viewer: fps in the window title; long F11 lines wrap instead of running off the screen

## v0.0.1

- First release: the game (meitou.exe) and the world viewer (meitou-viewer.exe)
