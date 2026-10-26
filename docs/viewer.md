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
In the game a --low-end run neither applies nor overwrites the sliders and switches saved in `meitou.user.json`.

## Keys

F10 key list is the current authority; F11 stats,F12 profiler,Shift+F12 VRAM pie,Tab settings,Alt+Enter fullscreen (a borderless window at the monitor's own corner on Windows, since GLFW's bounds are the work area and a taskbar or panel at the top or left would offset it; off Windows the window manager is asked for full screen, **Verified** on KDE: panel covered; full screen is entered and left through GLFW, on the primary monitor, keeping its own windowed place, and whether it is on is read from GLFW's window monitor: KDE keeps a maximized window maximized in full screen (**Observed** KDE Wayland and XWayland, GLFW 3.4), and Silk.NET's WindowState, which checks Maximized first, then says Maximized (**Verified** against Silk.NET 2.23's GlfwWindow.CoreWindowState)).
F11 includes the opaque UI coverage: the share of the screen under fully opaque controls (solid
backgrounds, alpha-255 atlas slices at alpha 1; `UiPainter.OpaqueCoverage`), overlaps counted once.
Panel skins have ragged translucent borders around a solid middle, so a slice counts as its largest
all-255 rectangle (inset 1 texel for bilinear), stretched to the control;
0 with the UI hidden. It bounds what a UI depth stamp could skip.
Viewer F1 switches all Faithful/Meitou; feature shortcuts use viewer bindings. Game keeps
Space pause,F2..F4 speeds,F5 save,F9 load,F6 cinema,F7 hideUI, Ctrl+F9 FPS, Shift+F1 all Meitou on/off, Shift+F2 the skin switch alone, Shift+F12 VRAM pie and Shift+F11 navmesh view ([pathfinding](game/pathfinding.md#meitou-debug-view)). Do not copy viewer
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
Record-path samples manual flight each second plus endpoints. Numpad* letterbox2.39:1/clean view.
Shift+Enter records numbered PNGs to C:/Temp/meitou-take-<time>, fixed1/60s camera/world motion;
render duration does not alter film time. Recording excludes UI/editor and uses an open flight.

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
