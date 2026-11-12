# World viewer

`tools/Meitou.ModelViewer` → meitou-viewer. Current modes: --world (also --terrain-sandbox and --field-tour, [render-terrain](render-terrain.md#ground-relief)) and --impostor-preview.
Mesh/character preview commands are retired; [character reference](character-viewer.md) retains
checks. Full option authority: --world --help, WorldOptions; toggles: AbToggles/Enhancements.

## Running

```text
dotnet run -c Release --project tools/Meitou.ModelViewer -- --world --town "The Hub"
meitou-viewer --world --zone 44,23 --radius 1.5 --screenshot out.png
meitou-viewer --view swamp --bench-frames 600 --bench-out run.json
meitou-viewer --impostor-preview <FOLIAGE_MESH>
```

Find install as `GameInstall.Locate` lists (KENSHI_PATH, meitou.local.json, `$XDG_CONFIG_HOME/meitou/config.json`, Steam, other
launchers via `GameInstallSources`); a candidate counts only with `data/gamedata.base`. **Observed** (one real Heroic install,
no test): Heroic's list and a Wine prefix's `system.reg` (GOG `GOG.com\Games\<id>`: `gameName`, `path`). Lutris, Bottles and
`.desktop` formats are **Unknown** beyond test layouts. Asset lookup/load order is documented
in [overview](formats/overview.md). Never substitute a guessed absolute game path.
--at x,z / --zone / --town selects focus; --radius zones controls source height region, with
power-of-two sample step keeping<=2048 cells/side. World renderers/streaming are shared with game.

## Options and modes

Full list: [launch-options](launch-options.md).

Explicit later options override named-view/--low-end presets. Meitou versus --faithful <feature|all>
selects independent improvements; --meitou/--faithful overrides persisted switches. Tab controls
quality/ranges/time/weather/fullscreen/VSync. Resolution/scale/upscaler, texture-quality/mips and
anisotropy are separate controls. --weather auto schedules from day/time/seed; a name forces it.
--clouds overrides density, --no-particles disables effects; --no-gi avoids ray-query setup.

Meitou defaults: object distance20000 (Faithful12000), landmarks7.5x object range (Faithful0),
terrain material distance30000, error16 rendered pixels (Faithful10), haze250000/far450000.
Water reflection0..4 determines included classes. Texture quality0..4 skips mips; default0.
--low-end: objects/landmarks5000, quarter LOD distance, short foliage/10%grass/particles,
quarter particles, no reflection/shadows/AO/AA, flat water, scale0.83, anisotropy1, textureLow3,
material distance15000, VSync off. It is a tunable preset, not an automatic hardware guarantee.
--benchmark-baseline: a 1920x1080 window, no GI, ground parallax or tessellation, no upscaler, SMAA at scale 1, texture quality 1; the game adds the Medium tier
(QualityTiers.ApplyBenchmarkBaseline). In the game a --low-end or --benchmark-baseline run neither applies nor overwrites the sliders and switches saved in `meitou.user.json`.

## Keys

F10 key list is the current authority; F11 stats,F12 profiler,Ctrl+F12 VRAM pie,Tab settings,Alt+Enter fullscreen (a borderless window at the monitor's own corner on Windows, since GLFW's bounds are the work area and a taskbar or panel at the top or left would offset it; off Windows the window manager is asked for full screen, **Verified** on KDE: panel covered; full screen is entered and left through GLFW, on the primary monitor, keeping its own windowed place, and whether it is on is read from GLFW's window monitor: KDE keeps a maximized window maximized in full screen (**Observed** KDE Wayland and XWayland, GLFW 3.4), and Silk.NET's WindowState, which checks Maximized first, then says Maximized (**Verified** against Silk.NET 2.23's GlfwWindow.CoreWindowState)).
`PlayerInterface.OpaqueUiCoverage` (not shown on F11) is the opaque UI coverage: the share of the screen under fully opaque controls (solid
backgrounds, alpha-255 atlas slices at alpha 1; `UiPainter.OpaqueCoverage`), overlaps counted once.
Panel skins have ragged translucent borders around a solid middle, so a slice counts as its largest
all-255 rectangle (inset 1 texel for bilinear), stretched to the control;
0 with the UI hidden. It bounds what a UI depth stamp could skip.
Viewer F1 switches all Faithful/Meitou; feature shortcuts use viewer bindings. Game keeps
Space pause,F2..F4 speeds,F5 save,F9 load,F6 cinema,F7 hideUI, Ctrl+F9 FPS, Shift+F1 all Meitou on/off, Shift+F2 the skin switch alone, Shift+F12 VRAM pie, Ctrl+Shift+F12 VRAM dump (every resource and block to `vram-<time>.txt` beside log.txt; world textures named `owner | file`) and Shift+F11 navmesh view ([pathfinding](game/pathfinding.md#meitou-debug-view)). Do not copy viewer
feature F-keys over game actions. F8/PrintScreen game screenshots; offscreen --screenshot is
headless and settles streaming. User and cinematic bindings are separate.

## Cinematic camera

Cinema/CameraPath stores viewer keys in %LOCALAPPDATA%/Meitou/viewer-shots.json; game keys separate.
Ctrl+numpad1..9 saves; numpad1..9 cuts (in the game, outside --duel, a numpad digit bound to a game hotkey, NUM0..NUM8 by default, is the hotkey unless Ctrl is held or the F6 editor is open: [ui-input](game/ui-input.md#4-action-table)). Enter play/stop,0 loop,+/- timing*1.25,/ time-follow (viewer; game: squad tour),
. list,Ctrl+. delete (editor Delete-all requires second click3s). PageUp/Down FOV±2.5deg,
10..100,Home50 (viewer and --duel; elsewhere in the game PageUp/Down are floor_up/down).
F6 editor supports scrub/retime0.05s/reorder/ease, add/update/delete and recording.
Keys include eye,yaw,pitch,log-distance,lens,hour; Hermite time spline unwraps yaw/hour, stops
at easing/open endpoints and joins loops. Unequal timing can overshoot; use easing where needed.
A key may set the depth of field's focus and aperture (F6 Focus and Aperture buttons on the selected key, half a stop a press; "Focus: key" goes back to the target): log-spline
between keys, a key without one stands for the target's distance or f/1. `Focus` and `Aperture` are optional in the file: left out while unset, skipped by older builds.
Record-path samples manual flight each second plus endpoints. Numpad* letterbox2.39:1/clean view.
Shift+Enter records numbered PNGs to C:/Temp/meitou-take-<time>, fixed1/60s camera/world motion;
render duration does not alter film time. Recording excludes UI/editor and uses an open flight.

## Photo mode

Game only (`PhotoMode`, `InputAction.TogglePhotoMode`, default **P**, rebindable; also the console command `photo`; not in `--duel`, the terrain sandbox, over the title or a loading cover, or during a cinema flight or squad tour). Engine choice, no original counterpart.

- **Contract:** entering pauses the simulation, hides the interface (F7's flag) and cuts the free camera to the drawn eye and view. Leaving (P, Esc, `photo off`) restores the strategy camera's pivot, yaw, pitch and boom (`CameraRig.Save`/`Restore`; the free camera's pose if it was flying), the pause speed, the interface flag, the followed character, the lens and every setting below. Nothing is saved or reaches the simulation; the game clock, weather and world keep their own time.
- **Camera** (its own keys, whatever the game's are bound to): W A S D and the arrows move along the view, Q down and E up, the right or middle mouse button turns the view, the wheel zooms the lens (8 % a notch; with Ctrl it sets the fly speed, a fixed 30 m/s default, not height-scaled), Shift x4, Ctrl x0.2, Z/X roll, V levels, PageUp/PageDown lens 2.5 deg, G steps the grid. A left click on the picture sets the focus point: the autofocus looks there (`PostOptions.LensFocusSpot`, marked on the picture) and the depth of field goes on. Mouse turn scales with the lens. The eye keeps `FreeClearance` above the ground and sea. No click selects or orders; unmapped keys are dropped.
- **Panel** (a bar along the bottom; drawn as the settings window is, through the interface's painter: its window, button and paper surfaces, or the game's skins in the Faithful interface, its font and inks; Tab, H or F7 hides it): tabs Camera, Focus, Light, Composition, Time and Presets, with Reset and Take picture (Enter or F8) at the right and the keys on its last line. Over the panel the wheel steps the setting under the pointer and a right click steps a choice back. The pointer is the arrow, hidden while the view is turned or the panel is hidden. Speech bubbles are hidden unless the Dialogue row shows them. Messages show at the top.
- **Time:** Space (the Pause binding) pauses or runs the simulation at the Game speed; the mode runs the clock itself and puts its whole state back on leaving (`SimulationClock.SaveSpeeds`: the speed, the last speed and what the pause key remembers; the pause key's own memory took the slow speed otherwise, **Observed**). The ambient motion outside the simulation (foliage sway, birds: `GameHost.EffectSeconds`) follows the simulation in and out of the mode: still when paused, faster or slower with the speed (the user's choice; what the original does in a pause is **Unknown**).
- **Remembered settings and presets** (`PhotoPresets`, `%LOCALAPPDATA%/Meitou/photo-mode.json`): leaving keeps every setting but the roll, the drawn hour and the simulation's state, and entering takes them back. A preset holds every setting (the hour only while one is drawn) and the focus point: Save preset on the Presets tab (named Preset n), a click takes one, the x beside it deletes it; console `photo preset save [name] | load <n|name> | delete <n|name> | list`. A preset is named: Save preset opens a name field in the row (the first free "Preset n" proposed, replaced by the first letter typed; Enter or OK keeps it, Esc or Cancel drops it; a name already kept replaces that preset), a right click on a preset renames it (`PhotoMode.Naming`, typed through `GameLoop.OnKeyChar`; no other key acts meanwhile). When the presets do not all fit, the row shows as many as fit between two arrows and the wheel over it moves through them. The Gallery button opens the pictures' folder (Pictures/Meitou) in the system's file browser (`PhotoMode.OpenGallery`; **Unknown**: not opened in a hidden run, which has no desktop). Control: `photo preset name [n|name]`, `photo type <text>`, `photo gallery`. **Verified** by pictures (hidden run, the panel with `photo shot --ui`): the field, a long name cut with an ellipsis, twelve presets scrolled, a rename.
- **Picture:** PNG to `Pictures/Meitou/Meitou <date time>.png` at the drawn size, with frame bars, without panel, grid and message; a line shows the path.
- **Settings** (`photo set <name> <value>`): `fov` 10..100 deg, `roll` -90..90 deg (positive banks right: the horizon's right end rises), `dof` off|on (`PostOptions.PhotoDepthOfField`), `focusmode` manual|auto, `focus` 0.3..1000 m (or `auto`), `aperture` f/0.25..22, `sharpzone` 0..30 m (each side of the focus, wholly sharp: several figures in focus at a wide aperture), `exposure` -3..+3 EV (`PostOptions.PhotoExposure`, on top of the saved brightness), `hour` 0..24 or `game` (`GameHost.PhotoHour`: the sun, sky, stars and lamps draw this hour; the weather schedule keeps the game hour), `dialogue` hidden|shown, `grid` off|thirds|golden|cross|diagonals, `frame` off|2.39:1|16:9|4:3|1:1|4:5, `speed` 0.5..3000 m/s, `live` paused|running, `gamespeed` x0.05..5 (the speed while running: slow motion below 1; the tick limit per frame is at least 1, `WorldSession.BeginTickAhead`, which threw under half speed). Also `photo on|off|toggle|get|reset|cam x y z yaw pitch|panel on|off|key <name> [up]|shot [file] [--ui]` (`--ui` keeps panel and grid in the file, for checking).
- **Precision:** the free camera keeps its eye in double precision (`FreeCamera`, `CameraState.EyeResidual`, `WorldCamera.PreciseEye`): in float it stepped 0.004 units at 50000, a pixel or two for something a few units away, small jumps in a slow flight close to the ground or an object (**Observed** in play).
- **Lens and mesh levels:** the mesh LOD value (distance) of characters and objects is multiplied by `MeshLod.LensFactor`, tan(fov / 2) / tan(25 deg), at most 1: through a long lens a far character is drawn large and takes the level it would have from as near as it looks (in photo mode and for the cinema's zoom; the game's own lens and wider are unchanged). The generated levels are prepared for the game's lens at the narrowest. Engine choice; **Observed**: a face at 10 deg from 40 m was the coarse level.
- **Roll:** `WorldCamera.Roll` turns the view's up (`Up`); shadows, post passes, sky, reflection and upscaler vectors read the view matrix. The cone tests that take only the forward direction (fog volumes' sky test, the simulation's `CameraViewCommand`) use `CullCone`, a cone holding the rolled frustum. **Verified** by screenshots (roll +-20 deg: sky, moons, shadows, water turn together).
- **Verified** (headless 2560x1440, `t_flats_shore`): enter/leave restores camera, pause and brightness; fov, roll, exposure, hour, depth of field, grids, frames, panel clicks, key flight. **Unknown:** the right-drag look and pointer pinning in a real window; a real keyboard.

## Squad tour

Game only (`SquadTour`, not --duel): numpad / flies the camera over the squad bar members in order, keeping the view's yaw, pitch and
distance at each; starts at member 1 (flies there first if the view is elsewhere), 1.5 s on each. Hops are inverted brachistochrone
(cycloid) arches lasting Advanced "Character flight time" (0.5-6 s, default 2), angle linear in time:
climb first, pitch steepens up to +0.35 rad, fastest at the top, slow landing; the target tracks the moving member. Camera input or / again ends it in
the strategy camera where it is. Advanced "Character flights" (Meitou, default off): a squad member double click / number double tap more
than 10000 units (ground) from the view target does one such hop, then follows the member; nearer, or off, it cuts as the game does.
Pressing that member again during the hop (a triple tap) cuts to its end.

## Diagnostics and references

[Benchmark](bench.md), [renderer](renderer-native.md), [simulation](simulation.md), render-* feature
references and [docs index](README.md). Establish two identical baseline renders before judging
pixel differences; temporal upscalers and evolving particles have noise. Profiler separates CPU
recording/GPU/pre-frame/streaming, not serialized sums. --log-spikes helps diagnose tails.
**Verified screenshot conventions:** right-handed/Y-up, counter-clockwise fronts, textures as
stored; human~19.5 units and katana12.8. Unit/placement formulas have their own research scopes.

## Pick & collision inspector

Meitou only. Open it from the F10 debug menu's Tools row. It works like the F10 menu: Up/Down pick a row, Enter or Space switch it, Esc closes it, and while it is open it takes every key. What was switched on stays on after it closes, so the camera can fly about with it drawn.

- **Info panel** (top right): the ground point under the cursor and its zone, the ray's ground distance, the building a click would select (name, record, instance, hit distance, hull corners, size and area, the number and area of the per-shape hulls and how much larger the merged hull is, the height range), the other buildings the ray passes with their distances, how many near buildings it misses, the selected building and the number of selected characters.
- The panel also lists the resource rocks near the cursor (what a right click would mine): the rock a click mines, and for the three nearest each one's distance from the ray against its radius, to see why a rock is or is not hit.
- **Pick hull:** the prism of every level-data building the ray's track reaches, and of every site of yours (marked `[site]`), that a click is tested against; the panel also names the site the hammer pointer comes from: red for the one a click selects (with a translucent floor), orange for the others.
- **Collision shapes:** each collision shape's own convex hull, green, next to the merged hull.
- **Collision mesh:** the real collision triangles of the building a click would select, cyan (at most 20000 triangles).
- **Navmesh:** the existing Shift+F11 view (off, Meitou's, the original's, both); the row steps it.
- Drawn like the navmesh view: over the finished picture, sorted against the scene depth.
- **Why it exists:** picking is a ray against one convex hull of all of a building's collision shapes (docs/game/ui-input.md "Building picking and selection"), so a concave building is clickable over its whole hull. The green shapes and the cyan mesh show how much tighter per-shape picking would be.
- Code: `DebugMenu` (menu, keys), `PickInspector` (panel, geometry), `DebugOverlays` (`UpdateInspectorOverlay`), `BuildingFootprint.Shapes` and `BuildingFootprints.Mesh` (per-shape hulls, collision triangles), `WorldFrame` (`InspectorOverlay`). Not covered: the buildings' visual meshes, characters, items, the navmesh polygon under the cursor, terrain or object collision other than level-data buildings and the placed sites.
