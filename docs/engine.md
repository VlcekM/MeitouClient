# Engine architecture

Projects: Core installation, settings, localisation (shared with the launcher); Data clean-room readers, typed game data and pure rules;
Simulation the headless world (systems, `Navigation/` own navmesh, saving, `Session/` tick clocks, `WorldBoot`), which never references Game;
Game the executable `MeitouClient` (`Host/` WorldSession/input/camera, `Rendering/` Vulkan/content/streaming with `Display/` and `Metal/`,
`Audio/`, `Ui/`, `Mods/` the code-mod loader); ModelViewer debug world/atlas; Tools surveys and mod-check. Folders are feature slices and
namespaces follow them. GPU and simulation ownership stay separate. [Simulation contracts](simulation.md),
[native renderer](renderer-native.md), [viewer controls](viewer.md).

## Loop, input and camera

WorldSession owns world, clocks, input/bindings, camera and last two snapshots. Control30Hz real
(default config tickRate), max10 ticks/frame; simulation1/30 game second, speed0/1/2/5 and
2*speed budget. Control precedes simulation; interpolation uses each clock's alpha, shortest
angle rotation and cuts on mode changes. The authoritative time/order/determinism details are
in simulation. Host copies camera state into WorldCamera and snapshots into CharacterDrawList (every frame for characters in reach; those beyond it in `Distant`, refilled only when the snapshot changes; emptied when the title takes over the list). The next world tick runs on its own
thread during the world draw ([simulation](simulation.md#tick-ahead)).

KenshiCamera follows pivot/boom/zoom/pitch/ground constraints from [camera](formats/camera.md).
Engine choices: pivot500*clamp(boom/600,0.1,1), keys1.5rad/s, mouse0.005rad/pixel; terrain-ground
pivot rather than building support. Free-camera ; follows smoothed flight, exits at ground hit.
InputState consumes events once per tick; commands apply at world boundaries, not UI callbacks.
meitou.user.json persists fps/vsync/fullscreen/tickRate/simThreads/graphics/bindings (layout and versioning: [launcher](launcher.md#graphics)); renderer
settings and current key list belong in viewer. Quicksave F5/loadF9; cinematic F6/hideUI F7.

## Host and loading

Meitou UI mode presents title controls over a live view of the real world; Faithful retains
the original title image and loading screen. Advanced settings persist two independent scene-transition
toggles in `meitou.user.json`, both on by default: `mainMenuTransitions` covers entering a new game/save
from the main menu and returning to it; `inGameTransitions` covers save loads/new games during play.
Each has a persisted speed slider (`mainMenuTransitionSpeed` / `inGameTransitionSpeed`, 0.25–4x, default 1.5x).
Disabling either uses the regular loading screen and warm-up. Enabled scene loads in Meitou use `SkyArrival`, the load starting at once:
the hour runs forward to the destination's (title: its morning; save: CAMERA `time hour`/`minute`, handed over by
the reading worker as soon as `quick.save` is parsed), 0.8 s + 1 s per 6 h, accelerating over its first half and braking over the second;
a 0.5 s wall-clock hold; a 3.4 s ascent about
the fixed eye to the sky. In play, and from the title when the run is at most 3 h (known once the save's hour is), the view climbs at once, without
the hold, the hour running forward meanwhile. The sky is at the destination hour (a day save shows day sky, not stars); a wait there for the load; a 0.3 s fade to black once its stages are done, under
which the destination warms up from the landing view; then a 3 s descent that fades in from black over its first 1.4 s, starting 300 units above the view and
orbiting 1.2 rad round its target onto the view's heading. All but the hold are divided by the selected speed. Ascent ramps speed over its
first/last 40%; descent ramps up over 30% and brakes over 20%. Integrated smoothstep velocity brings
angular velocity and acceleration to zero at both ends of each flight. Save reads, the new world's systems and new-game
system preparation overlap the flight; world replacement waits until terrain is outside
the view. A new game climbs at once at the current hour: no sitting down, hold or hour run.
Escape/Space skips the flight, never pending load work. Progress appears in the sky after 2s.
Camera/hour overrides belong to presentation; simulation does not advance during the flight,
and weather scheduling uses the actual game hour. Sky-only frames avoid world callbacks while
loading; warm-up renders the destination before descent. Errors drop the flight and restore UI.
`TitleComposition` shows the real world at a fixed spot near Okran's Shield (Okran's Pride),
given as a viewer camera code (`TitleComposition.Spot`, 50° lens), just after sunrise (`Sunrise + 0.75` h,
the sun peeking over the hill right of the valley; clear weather, cloud coverage 0.38). The host's renderers stream round that camera as
round any other; booting to the title starts the world there. Everything below is presentation only, set
while the title draws or shows and cleared when it closes; simulation and saves never see it:
- `SkyClock.SunTurn` turns the sun path about the vertical (height unchanged) to ~26° right of the heading;
  stars use the unturned path so their orientation stays continuous into the loading sky.
- The sky renderer's `PlanetFill` (0.8; 0 in play) lights the moons from the viewer's side so their whole face reads,
  and `PlanetTurn` (identity in play) moves them ~1.6° up and ~2.4° left in the view.
  Moon and character fill fade smoothly with sun height from the title's morning level to zero at the horizon.
- The clouds drift right to left across the view at 125 units/s (`CloudWind`), faster than the game's wind.
- Menu dust uses a separate particle renderer with the installed `kenshi_weather_dust1` system at 0.3 density,
  warm tint and 24s wind gusts; it runs while paused and fades out as the camera tilts to the stars.
- The shafts' air layer is raised to 2x and their strength to 1, so the hill the sun peeks over casts
  visible rays through the haze; `FogVolumes.HidePlaced` leaves out the placed `fogfeatures.dat` volumes
  (one stood as a dark dome where the sun is). `PostOptions.Balance` (1.1, 0.98, 0.88; 1 in play), a white balance
  before the tone map, keeps the orange of a lower sun.
- `FoliageRenderer.Shift` moves the ghost-town ruin (FOLIAGE_MESH houses and scrap piles) ~400 units left,
  applied as foliage zones are accepted.
- One wanderer (sandbox character in drifter's gear, with a stronger build: `CharacterAppearance.Shape`, null in play, sets 0.6
  muscle and broader shoulders, frame, chest, arms and neck) stands on the plateau's top where the ray through his screen point
  (x 0.337, y 0.93) meets it, close to the eye, lit by a warm fill (`CharacterRenderer.Fill`, 0 in play: added ambient,
  three times as strong at the silhouette) so his back, turned to the sun, is not a flat silhouette, ~300 px tall at 900 p, his back to the camera looking
  10° left of the heading, in a frozen `idle_stand_confident` pose. The camera's `MaxNear` is lowered to 20 for
  the title (the near plane is a quarter of the eye's ~350-unit clearance, ~87, which clipped him).

## Rendering and upscaling

WorldFrame prepares/streams sky/shadows/reflection, the scene (one depth) and post. Algorithms
live in render-* references; Data holds backend-independent terrain/placement/light rules.
Own GLSL compiles through bundled shaderc, explicit native resource/state recording and ordered
secondary jobs. [Native API](renderer-native.md).
