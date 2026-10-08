# Changelog

What changed in each release, newest first. The release workflow copies the section of the version it builds into the GitHub release
notes and refuses to release a version without one: add a `## vX.Y.Z` section with one `- ` line per change before running it.

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
