# Launch options and environment variables

Every command-line option and environment variable read by the project's executables. The parsing code named in each section is the authority; help text was cross-checked against it (gaps are listed under "Unclear").

Settings persist in `meitou.user.json`. The install is found in this order: `KENSHI_PATH`; `meitou.local.json` in the current folder, the executable's folder, and their parents; on Linux and macOS `XDG_CONFIG_HOME/meitou/config.json` (`kenshiPath`); then auto-detection. If none finds it, the game asks and writes `meitou.local.json`. Source: `src/Meitou.Core/Content/GameInstall.cs`, `GameInstallSources.cs`.

An option that belongs to another executable is rejected as unknown (exit 2).

## Game (`meitou`, MeitouClient.exe)

Authority: `src/Meitou.Game/GameOptions.cs` (`Split`, `Usage`), `src/Meitou.Game/Program.cs`. Options not taken here go to the shared world options. Help: `-h` / `--help`.

### Start and session

| Option | Value | Default | Effect |
|---|---|---|---|
| `--new-game [start]` | start name | Wanderer | New game: squad at its town, camera on it. |
| `--list-starts` | | | Print the available starts and exit. |
| `--load <folder>` | save folder | none | Start in a save, Meitou's or the original game's. With `--screenshot` too. |
| `--seed <n>` | integer | 0 | World seed. A new game from the menu keeps it once given. |
| `--no-population` | | off | No residents or movement. |
| `--move-to <x> <z>` | two numbers | none | With `--new-game`: select the squad and walk it there (pictures). |
| `--ticks <n>` | count | 0 | With `--screenshot`: run n control ticks before the picture. |
| `--quit-after <s>` | seconds | none | Close after s seconds and print the frame rate (smoke test). |
| `--no-precompile` | | off | Compile methods on first call, not ahead on a background thread. |
| `--play-bench <dir>` | folder | none | Scripted run, every frame measured; writes `frames.csv` and `summary.md`, then quits. Forces fps limit 0 and vsync off. Works with `--load` or `--new-game`. |
| `--settings-schema <file>` | path | | Writes the launcher's settings description and exits. Must be the first two arguments; no log or window. |
| `--recommend-preset <file>` | path | | Writes the quality preset for this computer (`HardwareProbe`, [launcher](launcher.md#graphics)) as JSON and exits. Must be the first two arguments; no log or window. |

### Graphics and UI

| Option | Value | Default | Effect |
|---|---|---|---|
| `--fps-limit <n>` | integer | 150 (`meitou.user.json`) | Frame limit when vsync is off; 0 is unlimited. |
| `--vsync` / `--no-vsync` | | `meitou.user.json` (off) | Vsync for this run. |
| `--renderer <vulkan\|metal>` | vulkan or metal | vulkan | Renderer for the whole frame. Metal is macOS only; the launcher appends this. |
| `--ui <faithful\|meitou>` | | `meitou.user.json` (meitou) | UI mode, independent of graphics. |
| `--ui-showcase` | | off | With `--screenshot`: HUD, inventory, stats, map, menus, a trade and a conversation, in either UI mode. |
| `--ui-panel <name>` | panel name | none | With `--screenshot`: open one panel (hud, hud-expanded, inventory, stats, overview, menu, title, ...). |
| `--title-preview` | | off | With `--screenshot`: the staged Meitou title scene. Sets UI mode to Meitou. |

### Sandbox, scenes, duel and battle

| Option | Value | Default | Effect |
|---|---|---|---|
| `--sandbox [n]` | 1 to 1000 | off; 1 when bare | Animation sandbox on a flat wireframe floor. n = 2: two facing each other; more: the rest idle in rings. |
| `--sandbox-character <name>` | CHARACTER or ANIMAL_CHARACTER name or id | first member of the default start | Character the sandbox spawns. |
| `--sandbox-mode <list>` | walk, run, free, a speed in u/s, patrol, fight (comma list) | none (idle) | The sandbox hero's start state, e.g. `run,patrol`. |
| `--sandbox-races` | | off | Adds one character of every person race in rings round the pair. |
| `--terrain-sandbox [cliffs] [x,z]` | | flattest spot (38376,-8280); cliffs spot (92164,123124) | Empty world on a field of ground-texture tiles; `cliffs` uses cliff textures on a steep face. |
| `--duel [x,z]` | | dune crest | Looping two-Samurai duel at sunset, on rails. Time held at 22:00 and weather clear unless `--time` or `--weather` is given. |
| `--battle <n> <unit> <m> <unit> [rows]` | counts of 1 or more; CHARACTER or ANIMAL_CHARACTER name or id | rows of 25 | Two NPC blocks fight, facing each other. Excludes `--new-game`, `--load`, `--no-population`, `--duel`, `--sandbox`. Backspace restarts. |
| `--battle-map <map>` | plateau, mesa, fog, sandbox, or x,z | plateau | Where the battle is fought. `mesa` is the duel spot; `fog` a plateau in the Fog Islands; `sandbox` is the flat floor. |
| `--battle-width <a>[,<b>]` | counts | rows as given | Fighters per row for each side (one value: both). |
| `--battle-no-fear` | | off | Nobody in the battle runs away. |
| `--scene <name>` | fight, turret, bed, acid, menu | none | With `--screenshot` (and `--load`): stage a scene and save frames. |
| `--scene-frames <n>` | 1 to 200 | 8 | Frames saved. |
| `--scene-every <n>` | 1 or more | 30 | Control ticks between saved frames. |
| `--scene-npc <record>` | CHARACTER name or id | Bandit | Enemies spawned by the fight and turret scenes. |
| `--scene-town <name>` | part of a town name | nearest turret | Town the turret scene moves the squad to first. |

## Viewer (`meitou-viewer`)

Authority: `tools/Meitou.ModelViewer/Program.cs` (dispatch), `WorldApp.cs`, `WorldApp.FieldTour.cs`, `ImpostorApp.cs`, and `src/Meitou.Rendering/WorldFrame.cs` for the world options (see "Shared world options"). With no arguments it opens `--world --town "The Hub"`.

Dispatch: `--world`, `--view` and `--bench-compare` open the world viewer; `--impostor-preview` and `--impostor-bake-all` open the impostor tool; anything else prints a short usage and exits 2.

### World viewer (`--world`)

| Option | Value | Default | Effect |
|---|---|---|---|
| `--world` | | | Selects the world viewer. Any shared option may follow. |
| `--view <name>` | swamp, swamp-rain, swamp-low, dust, hub | none | Named camera, world and weather preset (`src/Meitou.Rendering/Bench/NamedViews.cs`). Options after it override it. |
| `--quit-after <s>` | seconds | none | Close after s seconds and print frames and average fps. |
| `--terrain-sandbox [cliffs] [x,z]` | | flattest spot; `cliffs` sets yaw 180 | Empty world on a tile field; the tile under the camera is named top left. |
| `--field-tour <dir>` | folder | none | With `--terrain-sandbox`: a picture of every tile and an `index.html`. |
| `--tour-distance <u>` | units | 120 | Field-tour camera distance. |
| `--tour-pitch <deg>` | degrees | 30 | Field-tour camera pitch. |
| `--relief <Name=value>` | repeatable | game values | Sets a `GroundRelief` or `RockSmoothing` property for the run, e.g. `TargetSlope=0.3`. Applies to any `--world` run. |

### Impostor tool (`--impostor-preview`, `--impostor-bake-all`)

| Option | Value | Default | Effect |
|---|---|---|---|
| `--impostor-preview <mesh>` | mesh name | none | Bake and preview the impostor of one mesh. |
| `--impostor-bake-all` | | off | Bake every impostor. |
| `--rebake` | | off | Bake again even when cached. |
| `--class <name>` | class name | none | Impostor class (see `ImpostorApp.cs`). |
| `--out <dir>` | folder | `C:\Temp\meitou-impostors` | Output folder. |
| `--size <W>x<H>` | | 1600x900 | Picture size. |
| `--debug <n>` | integer | 0 | Debug output level. |

## Shared world options

Authority: `src/Meitou.Rendering/WorldFrame.cs` (`Parse`, `Usage`), `src/Meitou.Rendering/PostProcessOptions.cs` (`TryParse`, the post-processing rows), `src/Meitou.Rendering/Enhancements.cs` (switch ids). Used by the game (after its own options) and by the viewer.

### Where and camera

| Option | Value | Default | Effect |
|---|---|---|---|
| `--at <x>,<z>` | world X,Z | world centre | Look at this point. |
| `--zone <i>,<j>` | zone indices | | Centre of zone i,j (i along +X, j along +Z). |
| `--town <name>` | part of a town name | The Hub (game) | A town whose name contains this. |
| `--camera-code <hex>` | camera code | | Start at a camera code (the viewer's Ctrl+C); the world loads round it. |
| `--radius <zones>` | zones | 1.5 | Half-size of the loaded square (4608 units per zone). |
| `--yaw <deg>` / `--pitch <deg>` | degrees | 30 / 35 | Start heading and pitch above the horizon. |
| `--distance <u>` | units | radius | Camera boom. |
| `--fly-to <x>,<z>` | world X,Z | | With `--screenshot`: fly there first, then take the picture. |
| `--windowed` | | off (full screen) | Bordered window; maximised unless `--size` is given. |
| `--monitor <n>` | 1-based | not set | Window on monitor n. |
| `--show-keys` | | off | Start with the key list open (F10). |
| `--play-shots` | | off | Fly the saved camera keys on a loop. |
| `--crowd <n>` | count | 0 | Generated characters in the start town (character renderer test). |

### Output, size and debugging

| Option | Value | Default | Effect |
|---|---|---|---|
| `--screenshot <out.png>` | file | none | Render offscreen to a PNG and exit. |
| `--size <W>x<H>` | pixels | 1280x960 (1280x720 for bench and A/B runs) | Render size. |
| `--no-vsync` | | vsync on (viewer) | Start with vsync off. The game takes it as its own option. |
| `--log-spikes` | | off | Log frames whose GPU time is over 1.5 times the median. |
| `--sway-step <s>` | seconds | | Grass sway advanced per frame (motion check, with screenshot or bench). |
| `--orbit-step <deg>` | degrees | | With `--screenshot`: the camera orbits this much per frame (upscaler motion vectors). |
| `--wireframe` | | off | Wireframe view. |
| `--debug <n>` | 1 or 2 | off | 1: blend-map slot weights; 2: layer weights. |
| `--debug-shadows <n>` | 1 to 3 | off | 1: the four cascade maps; 2: shadow term by cascade; 3: term multiplied over the picture. |
| `--help` / `-h` | | | Print the usage. |

### Time, weather, audio and content

| Option | Value | Default | Effect |
|---|---|---|---|
| `--time <hour>` | hours | 13 | Time of day for the sun. |
| `--time-speed <h/min>` | game hours per real minute | 0 | Clock speed; stopped by default. |
| `--day <n>` | game day | 0 | Day the weather schedule starts at. |
| `--weather <name\|auto>` | `auto` or a WEATHER record name | auto | `auto` runs the scheduler; a name forces that weather. |
| `--clouds <0..1>` | density | from weather | Cloud density override. |
| `--no-particles` | | off | No weather particles. |
| `--particle-prewarm <s>` | seconds | longest particle life | Simulated seconds before the first frame; 0 starts empty. |
| `--particle-density <x>` | 0.1 to 1 | 1 | Emission rate multiplier. |
| `--no-audio` | | off | No sound. |
| `--audio-gain <dB>` | dB | 0 | Whole-mix level; 0 is the game's level. |
| `--no-objects` | | off | No buildings or map features. |
| `--no-foliage` | | off | No trees, bushes, rocks or grass. |
| `--grass-density <x>` | 0.1 to 2 | 1 | Grass blades multiplier. |
| `--no-water` | | off | No water. |
| `--water-grid <n>` | 64 to 1024 | 256 | Water grid segments round the eye. |
| `--water-ocean <n>` | 64, 128 or 256 | 256 | Water FFT size per cascade (128 for integrated GPUs). |
| `--water-reflection <0..4>` | 0 to 4 | 4 | What the water mirrors (0 sky only, 4 includes trees and rocks). |
| `--reflection-samples <n>` | samples | 4 | Samples per texel of the half-resolution reflection target. |
| `--texture-quality <0..4>` | 0 to 4 | 0 | Drops that many top mips of compressed textures (0 full size). |
| `--anisotropy <n>` | 1 to 16 | 16 | Maximum anisotropic filtering. |
| `--shadow-quality <0\|1\|2>` | 0, 1 or 2 | 2 | Shadow map side 1024, 2048 or 4096. |
| `--shadow-range <u>` | units; 0 turns shadows off | 5000 (10000 with Meitou shadows) | Shadow distance; clamped to 1000 to 9000, or 1000 to 15000 with Meitou shadows. |
| `--shadow-filter <0\|1\|2>` | 0 to 2 | 2 | Meitou shadow filter (1 and 0 cheaper, for low-end GPUs). |
| `--no-gi` | | GI on where ray queries exist | No ray-traced probe GI. |
| `--gi-debug <0..3>` | 0 to 3 | 0 | GI debug views: 1 hit distance, 2 one-bounce lighting, 3 normals. |
| `--fog <u>` | units | 250000 | Distance where the haze is complete. |
| `--material-distance <u>` | units | 30000 | Beyond it, terrain shows the biomes' ground colour. |
| `--terrain-error <px>` | pixels | 16 (Meitou), 10 (Faithful) | Terrain LOD error allowed near the eye. |
| `--object-distance <u>` | units | 20000 (Meitou), 12000 (Faithful) | Full-detail draw distance for placed objects. |
| `--range-large <u>` | units | 50000 | Foliage draw range of the large size class. |
| `--distant-range <zones>` | zones | 10 | How far distant towns are drawn; 0 for none. |
| `--impostor-budget <MB>` | MB | 8% of video memory (5% integrated), 48 to 1024 | Video memory for resident impostor atlases. |
| `--no-load-cache` | | off | Do not read or write the load caches (foliage layout, character LOD, world colour map and terrain colour tile caches). |
| `--low-end` | | off | Potato-PC preset: short draw distances, no shadows, reflections, AO, relief, light shafts or AA, 0.83 render scale, sparse grass and particles. Options after it override it. |

### Post-processing and upscaling (`PostProcessOptions.cs`)

| Option | Value | Default | Effect |
|---|---|---|---|
| `--post <meitou\|kenshi\|off>` | preset | meitou | Post chain preset; applied before the rows below. |
| `--upscaler <off\|taa\|fsr\|dlss\|metalfx>` | | taa (Meitou), off (Faithful) | Temporal upscaler. FSR and DLSS need their vendor libraries; MetalFX is Metal only, else TAA. |
| `--render-scale <0.25..1 or name>` | 0.25 to 1; native, ultra-quality, quality, balanced, performance, ultra | 0.83 with an upscaler (Meitou), else 1 | Render size per axis. |
| `--no-fxaa` | | FXAA on | Game's FXAA off when no upscaler runs. |
| `--smaa` | | off | SMAA instead of FXAA when no upscaler runs. |
| `--no-heat-haze` | | on | No heat haze. |
| `--particle-divisor <0\|1\|2\|4>` | | 0 | Weather particles drawn at 1/n of render size; 0 sizes by sprite. Rain and fire stay full size. |

### Meitou and Faithful switches

| Option | Value | Default | Effect |
|---|---|---|---|
| `--meitou <ids>` | comma list of ids, or `all` | all | Turns the listed Meitou enhancements on. |
| `--faithful <ids>` | comma list of ids, or `all` | | Turns the listed enhancements off, giving the game's look. |

Ids (`Enhancements.cs`): `ao`, `dither`, `haze`, `aa`, `shadows`, `range`, `impostors`, `reach`, `water`, `particles`, `lod`, `gi`, `tonemap`, `shafts`, `heathaze`, `night`, `planetshine`, `stars`, `nightgrade`, `clouds`, `cloudshadows`, `cloudfog`, `interiorgi`, `charlod`, `skin`, `interiorlamps`, `icons`, `blood`, `bloodsurfaces`, `parallax`, `displace`, `weld`, `tracers`, `fog`. The strength of each is a Tab slider.

### Benchmarks and A/B (`--screenshot`, offscreen)

| Option | Value | Default | Effect |
|---|---|---|---|
| `--ab <id>` | switch id | | Alternates Meitou (side A) and Faithful (side B) for one switch, per frame or per period. |
| `--ab-period <k>` | frames | 1 | Frames per side of the A/B alternation. |
| `--bench-frames <n>` | frames | off | Offscreen: measure n frames after warm-up; writes per-stage JSON. |
| `--bench-out <file>` | path | temp folder | JSON result path. |
| `--bench-motion <m>` | still, orbit, fly, turn | still | Camera motion during the benchmark. |
| `--bench-orbit <px>` | pixels per frame | | Orbit speed for `orbit`. |
| `--bench-serial` | | off | Wait for every frame (frame = CPU + GPU). |
| `--bench-tris` | | off | With `--bench-frames`: count triangles per pass. |
| `--bench-compare <a.json> <b.json>` | files | | Print two results side by side and exit. |
| `--no-gpu-lock` | | off | Do not queue on `%TEMP%\meitou-gpu.lock`. |
| `--fly-benchmark <frames>` | frames | off | Offscreen circle flight; prints frame-time percentiles. |
| `--fly-radius <u>` | units | 12000 | Radius of the fly benchmark circle. |
| `--fly-speed <u/frame>` | units per frame | 150 | Speed of the fly benchmark. |
| `--fly-pipelined` | | off | No GPU wait per frame; two frames in flight. |
| `--fly-csv <file>` | file | off | Per-frame CSV of frame times and video memory; allocator owners every 30 frames in `<file>.owners.csv`. |

## Meitou.Tools (`meitou-tools`)

Authority: `tools/Meitou.Tools/Program.cs` (dispatch and usage), with the option parsing in `NavmeshTool.cs`, `WeatherTool.cs`, `AudioTool.cs`. Every subcommand except `image-diff` and `world-map` reads the install (see the install order above).

| Subcommand | Arguments | Effect |
|---|---|---|
| `formats` | | Count file types under the install's `data` folder. |
| `fcs <file>` | path or name in `data` | Summarise a `.mod` or `.base` file. |
| `load` | | Apply the load order; summarise merged game data. |
| `armour <name>` | armour name | ARMOUR records of that name after the load order, their fields and the quicksave's saved copies. |
| `meshes` / `skeletons` / `materials` | | Read and report every mesh, skeleton or material script. |
| `fcs-types` | | Match record type numbers to `fcs.def` names. |
| `fcs-records <N>` | type number | Fields of the first records of type N. |
| `world` | | Cross-check heightmap, zone files and `features.dat`. |
| `particles [effects]` | `effects` optional | Survey particle scripts; `effects` lists EFFECT records. |
| `particles weathers` | | Every fog volume, and every WEATHER with its effects, fog volumes and lights. |
| `particles warmup [weather...]` | weather names | Cost of changing to a weather. |
| `navmesh [...]` | see below | Build a zone's navmesh and write debug files. |
| `weather [...]` | see below | Season and weather timeline for a region. |
| `audio [bank] [...]` | see below | Survey Wwise banks, dump one, decode or write WAVs. |
| `image-diff <a.png> <b.png> [diff.png]` | | Mean difference and share of pixels over 12/255. |
| `world-map <png> [step]` | step, default 16 | Top-down world map. |

`navmesh` options: `--zone x,z`, `--town name`, `--around n`, `--geometry`, `--obj f.obj`, `--png f.png`, `--scale u`, `--box x0,z0,x1,z1`, `--path x0,z0,x1,z1` with `--from-y y`, `--to-y y`, `--closed`, `--near x,z,radius` (the buildings, map features, carvers and solids reaching the point), `--probe x,z[;x,z...]` (the column at each point: gathered triangles, solids, our and the original's polygons), `--probe x,z,radius,step` (a map of ground both meshes keep), `--feature-tops` (per map feature of the zone: walkable top samples the original and we keep), `--route x0,z0,x1,z1` (the long-range route with building footprints, then with map features too), `--ragdoll` (the zone's ragdoll collision triangles, map features' among them), `--no-interiors`, `--interior key` (one building's interior: parts, components, coverage), `--compare-interiors x,z ... | towns` (the original tiles' interior faces our kept polygons cover, and per group whether the original connects it and ours is reached from the street, SEALED where only the original is; `towns` adds every zone with a town; per group under 90 % the building and its uncovered faces by height, pruned or absent; with `--around n` the neighbours resolved too; `--verbose` each face and the furniture standing on original faces), `--repeat n`, `--fingerprint` (SHA-256 of the cache files), `--compare x,z ...`, `--own-only` (the building's own shapes only, no furniture or neighbouring buildings), `--verbose` (a failed `--path` also prints the polygons that reach the goal without clearance); build settings (`NavBuildSettings`): `--cell u`, `--climb u`, `--tile n` (cells per tile), `--min-region n`, `--merge-region n` (region areas in cells), `--simplify e` (contour error in cells).

`weather` options: `--region name`, `--at x,z`, `--days d0 d1`, `--seed n`, `--list` (regions and calendars), `--extents`.

`audio` options: `--media`, `--decode`, `--wav <folder> [wem id]`.

## Launcher (`launcher/Meitou.Launcher`)

The launcher takes no command-line options of its own. It passes to the game:

- the user's launch arguments from its settings (`LauncherConfig`), split on spaces; the Graphics page warns that `--low-end` makes the game ignore the graphics sliders;
- `--renderer <vulkan|metal>`, appended last, so it wins over a hand-written one (`MainWindow.axaml.cs`).

Environment it sets for the game: `KENSHI_PATH`; on Windows with `steam_api64.dll` and **Launch as Kenshi on Steam** on, `SteamAppId` and `SteamGameId` (and a temporary `steam_appid.txt`); on macOS `MEITOU_METAL_GPU_BUDGET_MB` (Metal) or the Vulkan loader path. It removes any inherited `MEITOU_METAL_GPU_CULL` and `MEITOU_METAL_GPU_BUDGET_MB` first. It reads `DYLD_LIBRARY_PATH` on macOS (`MacSupport.cs`).

## Development executables

These have no user-facing docs; options are from their top-level argument loops.

- `src/Meitou.Metal.Host/Program.cs` (Metal sky through the backend seam): `--hour <h>` (default 12), `--hold` (1800 frames instead of 300), `--shot <png>` (10 frames, then save), `--world` (use the install's sky), `--tri` (triangle test), `--table` (triangle coloured from the bindless table; implies `--tri`).
- `src/Meitou.MetalSpike/Program.cs` (shader chain spike): `--rt-bench` (headless ray-tracing probe, then exit), `--hold` (1800 frames).

## Environment variables

| Variable | Read by (file) | Effect |
|---|---|---|
| `KENSHI_PATH` | `src/Meitou.Core/Content/GameInstall.cs`, `tools/scripts/*.ps1` | Kenshi install folder. Also used by the test suite through `GameInstall.Locate`; tests skip when it is missing. |
| `DYLD_LIBRARY_PATH` | `src/Meitou.Rendering/Gpu/Core/VulkanDevice.cs`, `launcher/Meitou.Launcher/MacSupport.cs` | macOS Vulkan loader search path; only named in the error message the viewer prints. The launcher sets it. |
| `SteamAppId` | `src/Meitou.Game/SteamSession.cs` | Steam app id; the launcher sets it on Windows. |
| `SteamGameId` | launcher only | Set for Steam's overlay; not read by project code. |
| `XDG_CONFIG_HOME`, `XDG_DATA_HOME`, `XDG_DATA_DIRS` | `src/Meitou.Core/Content/GameInstallSources.cs` | Linux and macOS install discovery and the `meitou/config.json` location. |
| `MTL_CAPTURE_ENABLED` | Metal framework (not project code) | Enables Metal GPU capture; the game's Ctrl+/ key uses it (per the usage text). |
| `MEITOU_PASS_STATS` | `tools/Meitou.ModelViewer/PassMeter.cs`, `src/Meitou.Rendering/Gpu/FrameLoop.cs` | `1`: per-pass GPU cost breakdown. |
| `MEITOU_PASS_STATS_SKIP` | `tools/Meitou.ModelViewer/PassMeter.cs` | Frames skipped before the breakdown (default 60). |
| `MEITOU_BENCH_ORBIT` | `tools/Meitou.ModelViewer/WorldApp.Benchmark.cs` | Pixels per frame the benchmark camera orbits (0.005 rad per pixel). |
| `MEITOU_BENCH_SKIP` | `WorldApp.Benchmark.cs` (default 1), `src/Meitou.Rendering/SpikeLog.cs` (default 0) | Frames left out of benchmark percentiles and spike logs. |
| `MEITOU_FLY_TURN` | `WorldApp.Benchmark.cs` | Radians the fly benchmark turns each frame instead of flying. |
| `MEITOU_STREAM_LOG` | `tools/Meitou.ModelViewer/WorldApp.cs` | `1`: print frames slower than 15 ms with streamer timings. |
| `MEITOU_VRAM_KILL` | `tools/Meitou.ModelViewer/VramWatch.cs` | Benchmark exits (code 9) when video memory passes this fraction of the budget (default 0.95; 0 off). |
| `MEITOU_LOD_DEBUG` | `src/Meitou.Rendering/WorldObjectRenderer.cs` | `1`: colour surfaces by LOD level; `2`: wireframe coloured by level. |
| `MEITOU_FOLIAGE_RANGE` | `src/Meitou.Rendering/FoliageRenderer.cs` | Starting value of the foliage range setting (offscreen measurements). |
| `MEITOU_GRASS_RANGE` | `src/Meitou.Rendering/FoliageRenderer.cs` | Starting value of the grass range setting. |
| `MEITOU_FORCE_INTEGRATED` | `src/Meitou.Rendering/Gpu/Core/VulkanDevice.cs` | `1`: treat any GPU as integrated (budgets for integrated GPUs). |
| `MEITOU_VRAM_BUDGET_MB` | `src/Meitou.Rendering/Gpu/Core/VulkanDevice.cs` | Caps the video memory reported, for testing the memory guard. |
| `MEITOU_VK_VALIDATION` | `src/Meitou.Rendering/Gpu/Core/VulkanDeviceOptions.cs` | `1` validation; `sync` adds synchronisation checks; `gpu` adds GPU-assisted checks; `0` off. Unset: caller's default. |
| `MEITOU_VRAM_GUARD` | `src/Meitou.Rendering/WorldFrame.cs`, `src/Meitou.Game/GameHost.Metal.cs` | `0` turns the memory-pressure guard off. On by default. |
| `MEITOU_OBJECT_LOD_CACHE` | `src/Meitou.Rendering/ObjectLodGen.cs` | Folder for the object LOD cache (default under `%LOCALAPPDATA%\Meitou`). |
| `MEITOU_CHARACTER_LOD_CACHE` | `src/Meitou.Rendering/Characters/CharacterContent.cs` | Folder for the character LOD cache (default under `%LOCALAPPDATA%\Meitou`). |
| `MEITOU_IMPOSTOR_CACHE` | `src/Meitou.Rendering/Impostors/ImpostorCache.cs` | Impostor cache folder. |
| `MEITOU_SSAO_STAMPS` | `src/Meitou.Rendering/PostProcess.cs` | `1`: GPU timestamps around the SSAO pass (diagnostic). |
| `MEITOU_SHAFT_STAMPS` | `src/Meitou.Rendering/PostProcess.Shafts.cs` | `1`: GPU timestamps around the light-shaft pass (diagnostic). |
| `MEITOU_UPSCALER_LOG` | `src/Meitou.Rendering/Upscalers/Streamline.cs`, `FsrUpscaler.cs` | `1`: log upscaler info (errors are always logged). |
| `MEITOU_DLSS_PATH` | `src/Meitou.Rendering/Upscalers/VendorUpscalers.cs` | Folder of the DLSS runtime: the Streamline DLLs on Windows, the DLSS model and `libmeitou_ngx.so` on Linux. |
| `MEITOU_STREAMLINE_PATH` | `src/Meitou.Rendering/Upscalers/VendorUpscalers.cs` | Older name of `MEITOU_DLSS_PATH`, read when that is unset. |
| `MEITOU_FFX_PATH` | `src/Meitou.Rendering/Upscalers/FsrUpscaler.cs` | Path to the FidelityFX library (FSR). |
| `MEITOU_DLSS_PRESET` | `src/Meitou.Rendering/Upscalers/VendorUpscalers.cs` | Overrides the DLSS preset number. |
| `MEITOU_NGX_LOG` | `src/Meitou.Rendering/Upscalers/Ngx.cs` | `1` prints NGX's whole log (DLSS on Linux). |
| `MEITOU_DLSS` | `src/Meitou.Rendering/Upscalers/Ngx.cs` | `0` keeps NGX from loading (no DLSS on Linux; the game uses TAA). |
| `MEITOU_BENCH_ONLY` | `src/Meitou.Game/GameHost.PlayBench.cs` | Comma list of `--play-bench` segments to run (e.g. `settle,zoom`). |
| `MEITOU_TERRAIN_NO_NORMAL` | `src/Meitou.Game/GameHost.Metal.cs` | `1`/`true`: terrain shader variant without normal mapping (A/B; fixed before the first draw). |
| `MEITOU_METAL_GPU_BUDGET_MB` | `src/Meitou.Game/GameHost.Metal.cs` | Metal video-memory budget in MB (default 5 GB). The launcher sets it on macOS. |
| `MEITOU_METAL_NO_TAA` | `GameHost.Metal.cs` | `1`/`true`: native size, no jitter, no temporal resolve (diagnostic). |
| `MEITOU_METALFX` | `GameHost.Metal.cs` | `1`/`true`: MetalFX upscaler (same as `--upscaler metalfx`). |
| `MEITOU_METALFX_SPATIAL` | `GameHost.Metal.cs` | `1`/`true`: MetalFX spatial mode. |
| `MEITOU_METAL_FOLIAGE` | `GameHost.Metal.cs` | `0`: foliage off on Metal (on by default). |
| `MEITOU_METAL_CLOUD_SHADOWS` | `GameHost.Metal.cs` | `0`: cloud shadows off on Metal (on by default). |
| `MEITOU_METAL_WATER` | `GameHost.Metal.cs` | `0`: water off on Metal (on by default). |
| `MEITOU_METAL_SHADOWS` | `GameHost.Metal.cs` | `0`: shadows off on Metal (on by default). |
| `MEITOU_METAL_NO_IMPOSTORS` | `GameHost.Metal.cs` | `1`: no impostors on Metal. |
| `MEITOU_METAL_NO_TERRAIN_TEXTURES` | `GameHost.Metal.cs` | `1`: terrain textures off (prints a notice). |
| `MEITOU_METAL_NO_PARTICLES` | `GameHost.Metal.cs` | `1`: no weather particles on Metal. |
| `MEITOU_METAL_GROUND_RELIEF` | `GameHost.Metal.cs` | `0` or `false`: ground relief (parallax) off; on by default. |
| `MEITOU_METAL_GI` | `GameHost.Metal.cs` | `1`/`true`: native Metal probe GI (opt-in; experimental). |
| `MEITOU_METAL_GI_LOG` | `src/Meitou.Metal/Gi/MetalProbeGi.cs` | `1`: log the probe GI. |
| `MEITOU_METAL_GI_FALLBACK` | `src/Meitou.Metal/Gi/MetalProbeGi.cs` | `1`: disables the sky-light term of the probe GI (A/B). |
| `MEITOU_METAL_NO_GRASS` | `GameHost.Metal.cs` | `1`: no grass blades on Metal. |
| `MEITOU_METAL_NO_FOLIAGE_MESHES` | `GameHost.Metal.cs` | `1`: no foliage meshes on Metal (grass only). |
| `MEITOU_METAL_GPU_CULL` | `GameHost.Metal.cs`, `src/Meitou.Metal/MetalFrame.cs`, `src/Meitou.Rendering/FoliageRenderer.cs` | `1`/`true`: experimental GPU foliage cull (unsafe; the launcher removes it). |
| `MEITOU_METAL_CPU_CULL` | `GameHost.Metal.cs` | `1`: force the CPU foliage cull, overriding the GPU cull. |
| `MEITOU_METAL_TRACE` | `GameHost.Metal.cs` | `1`: trace output of the Metal frame loop. |
| `MEITOU_METAL_NO_BUILDINGS` | `GameHost.Metal.cs` | `1`: skip the runtime building, ground-item and lamp updates (freeze diagnostic). |
| `MEITOU_METAL_NO_PARTICLE_COVERAGE` | `src/Meitou.Game/MetalPostChain.cs` | `1`/`true`: particles do not write TAA's coverage alpha. |
| `MEITOU_METAL_OVERLAY` | `GameHost.Metal.cs` | `1`: add the frame-stats lines to a screenshot. |
| `MEITOU_METAL_SETTINGS` | `GameHost.Metal.cs` | `1`/`true`: open the settings panel for a still. |
| `MEITOU_METAL_FAST_MATH` | `src/Meitou.Metal/MetalShaders.cs` | Metal fast-math compile option: unset is Metal's default (on); `1` or `0` forces it. |
| `MEITOU_METAL_DUMP` | `src/Meitou.Metal/MetalShaders.cs` | Folder to write the translated MSL of each shader into. |
| `MEITOU_METAL_NO_COMPRESSION` | `src/Meitou.Metal/MetalContext.cs` | `1`: no lossless target compression (A/B). |
| `MEITOU_METAL_SLOT_WAIT` | `src/Meitou.Metal/MetalContext.cs` | Unset or `1`: wait for the reused slot's fence (default). `0`/`false`: frame-number only. `serial`: one frame in flight. |
| `MEITOU_METAL_NO_STATE_CACHE` | `src/Meitou.Metal/MetalCommandList.cs` | `1`: reissue every encoder state (A/B). |
| `MEITOU_METAL_PRIVATE_STATIC` | `src/Meitou.Rendering/TerrainRenderer.cs`, `TerrainTextures.cs` | `1`: private storage for static terrain data (A/B). |

Names starting `MEITOU_` are also recorded in the benchmark JSON (`WorldApp.Bench.cs`).

## Unclear

- `MEITOU_BENCH_SKIP` defaults differ: 1 in `WorldApp.Benchmark.cs:93`, 0 in `SpikeLog.cs:19`.
- `MEITOU_METAL_GI_FALLBACK` (`MetalProbeGi.cs:469`), `MEITOU_METAL_PRIVATE_STATIC` (`TerrainRenderer.cs:63`), and the two `*_STAMPS` variables are described from the names and read sites only; their effects were not traced.
- `MEITOU_FRAGMENT` (`src/Meitou.Rendering/Gpu/Shaders/GlslProgramCompiler.cs:167`) is a shader macro, not an environment variable, so it is excluded.
- `MTL_CAPTURE_ENABLED` appears only in the game's usage text (`GameOptions.cs:120`); no project code reads it.
- The viewer's `--class` and the impostor `--debug` semantics are not traced beyond the parse.
