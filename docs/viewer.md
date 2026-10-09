# Viewer (`tools/Meitou.ModelViewer`)

`meitou-viewer` is the world viewer (`--world`, [World mode](#world-mode)) and the impostor preview (`--impostor-preview`,
[impostors.md](impostors.md)), on the renderers of `src/Meitou.Rendering` (FCS lookups follow the full load order, including
enabled and workshop mods): Vulkan 1.3 (OpenGL was removed, DECISIONS 18), our own GLSL shaders (Kenshi's HLSL is only read for
facts), CPU-decoded textures ([formats/dds.md](formats/dds.md)). Status labels as in [README.md](README.md).

The standalone **mesh viewer** (`meitou-viewer <mesh>`) and **character viewer** (`meitou-viewer --character <record>`) were
removed in phase 8 (DECISIONS 23). What they did and how, their options and their verified findings are in
[character-viewer.md](character-viewer.md); the code is at the git tag `model-viewer-last`.

## Running

The install comes from `KENSHI_PATH` / `meitou.local.json`. Usage of the world viewer is under [World mode](#world-mode).
`--quit-after <s>` (any interactive mode) closes the window after s seconds and prints the frames drawn (a smoke test);
`MEITOU_VK_VALIDATION=1` runs the Vulkan validation layer. `--renderer vulkan` is accepted and ignored in every mode (`gl` prints a
message that OpenGL is gone and is ignored).

## Finding files

1. An existing path, then the path relative to the install (FCS stores `.\data\...` paths).
2. The bare name in the `resources.cfg` and load-order mod folders (Ogre's lookup, not recursive; within a group the last
   registered folder wins, `OgreScriptResources`).
3. The bare name anywhere under `data/`.

Bare names are not unique: `katana05.mesh` exists as `data/meshes/katana05.mesh` (v1.100, 1.6 units long) and
`data/items/weapons/mesh/katana05.mesh` (v1.8, 12.8 units, the path the WEAPON record names); they are
different models. **Answered** ([characters.md](characters.md#meshes-per-item-verified--140537690--1405358a0)):
the game reduces item mesh paths to the bare name before loading, so the resource index decides, and
`data/items/weapons/mesh` is registered after `data/meshes` in the same group: the game loads the 12.8-unit
weapon file. `AssetLocator` gets the same file for the bare name `katana05.mesh` (rule 1 fails for a bare name) by rule 2.

## How mesh textures are resolved

Mesh material names mostly don't name script materials, and `StaticObject` (the most common one) only has
placeholder textures ([formats/ogre-material.md](formats/ogre-material.md#mesh-materials-verified-2026-10-04)).
The game builds materials at run time from FCS records ([formats/runtime-materials.md](formats/runtime-materials.md),
decompiled). `MaterialResolver` (`src/Meitou.Rendering`, used by the world's objects; it was written for the removed mesh viewer,
[character-viewer.md](character-viewer.md)) approximates that, best first:

1. **FCS records whose filename fields name the mesh** (bare name, case-insensitive), each giving one texture
   set for the whole mesh:

   | Record type, field | Textures from | Notes |
   | --- | --- | --- |
   | FOLIAGE_MESH `mesh`, MAP_FEATURES `mesh` | same record: `texture map`, `normal map`, `texture map 2`, `normal map 2`, `tile X/Y` | mode from `material type` / `texture mode` (MapFeatureMode) |
   | FOLIAGE_MESH `leaves mesh` | `leaves texture`, `leaves normal`, `leaves alpha threshold` | transparent, double-sided (fcs.def) |
   | WILDLIFE_BIRDS `mesh` | `texture` | |
   | ATTACHMENT `mesh` / `mesh female` (hair) | `texture map` / `texture map female`; grey and alpha from `hair diffuse channel` / `hair alpha channel`, cut at `alpha rejection` | tint is a fixed brown in the viewer (game: hair colour) |
   | RACE `male mesh` / `female mesh` | `body texture male/female`, `nm male/female`; head from the first `heads male/female` HEAD record (`texture map`, `normal map`) | |
   | ARMOUR, CONTAINER, ITEM, LIMB_REPLACEMENT, MAP_ITEM, NEST_ITEM, CHARACTER_PHYSICS_ATTACHMENT, CROSSBOW, BUILDING_PART, FARM_PART, BUILDING `distant mesh` | `material` references -> MATERIAL_SPEC / MATERIAL_SPECS_CLOTHING / MATERIAL_SPECS_WEAPON | a MATERIAL_SPEC with its own `material` list, and MATERIAL_SPECS_COLLECTION, are random choices (fcs.def); all choices are listed, likeliest first |
   | BUILDING_PART without a `material` | the `material` of BUILDINGs (or parts) listing it in `parts` | **Observed**: 223 parts with a mesh have no own material; the preview code falls back to the base building's material (runtime-materials.md) |
   | WEAPON `mesh` / `bare sword` / `sheath` | MATERIAL_SPECS_WEAPON "models" of every WEAPON_MANUFACTURER whose `weapon types` lists the weapon | |
   | ITEM with its own `texture map` (1 record) | same record | |

   Found by listing every filename field per record type (all types with `.mesh` fields are covered) and every
   reference list pointing at MATERIAL_SPEC* / HEAD / COLOR_DATA records (scratch probes, 2026-10-04).
2. The submesh's **script material**, if its first pass names a texture that exists and isn't a placeholder
   (`black.dds`, `flat.dds`, `white.dds`...). Covers the exporter leftovers (`01-Default`...).
3. Otherwise untextured (flat grey).

### Facts used, and how sure

- **Verified (decompiled, runtime-materials.md)**: textures go into units by unit name (`diffuseMap`,
  `normalMap`, `metalnessMap`, `diffuseMap2`...); TRANSPARENCY clips on the **normal map's alpha** with
  `threshold = alpha threshold / 255` (default 128); BuildingShader ALPHA -> TRANSPARENCY, FOLIAGE ->
  TRANSPARENCY + DOUBLESIDED, EMISSIVE -> normal-map alpha is glow; COLOURING (diffuse × vertex colour)
  whenever a building-part or map-feature mesh has vertex colours; DUAL only with vertex colours; triplanar
  map features use the `Triplanar` template.
- **Verified (decoded channels)**: the cut-out mask is the normal map's alpha, the diffuse alpha is gloss:
  `Trees&VegAtlas02.dds` alpha is a noisy gloss map while `Trees&VegAtlas02_N.dds` alpha holds crisp leaf
  silhouettes; `Arc-Leaves_DIF.dds` alpha never exceeds 19 while `Arc-Leaves_NML.dds` alpha spans 0–254.
- **Observed (Kenshi HLSL, read for facts)**: `objects.hlsl` blends dual sets as `lerp(set2, set1, vertex alpha)`
  (vertex alpha 1 = first set); `triplanar.hlsl` projects `world position / 5000 × tiling`; the character
  shader samples the head at `uv + (0, 1)` (head UVs sit in v −1..0 of the body mesh) and adds it to the
  body, both with border addressing and a transparent black border (`character.material`), and reads the body
  normal as `.wy` (X in alpha, Y in green). The removed mesh and character viewers did the same
  ([character-viewer.md](character-viewer.md)). Screenshots of `human_male` show a correct face only with this.
- **Observed (viewer heuristic)**: a normal map whose mean blue is below 180 and whose R, G, B means agree is
  treated as swizzled (`WorldTextureCache.LooksSwizzled`; catches the character body maps; building/weapon maps have mean blue ≈ 240–255).
- **Observed (screenshots)**: weapons share a UV layout: MATERIAL_SPECS_WEAPON textures (e.g. `01_HI.dds`,
  `12_HI.dds`) map sensibly onto both `katana05` and `chopper01_bare` (blade steel, edge strip and wrapped grip
  where expected).
- **Observed (Kenshi HLSL `skin.hlsl`)**: clothing worn on a character (the `Skinned` template, which gets no
  TRANSPARENCY flag) always cuts where the normal map's alpha is below 0.6, whatever its ItemShader
  ([characters.md](characters.md#worn-clothing-observed-hlsl-skinhlsl-parameters-in-runtime-materialsmd)).
  Materials from `MaterialResolver` clip on normal alpha like ground items; the removed character viewer used the 0.6 rule.
- **Verified (decompiled)**: `leaves alpha threshold` is / 255, cut on the leaves normal map's alpha (formats/foliage.md, "Materials"). Leaves whose
  record names no normal map were drawn opaque by the removed mesh viewer.
- Not followed: ARMOUR shown on the ground uses a temporary record built from `vest texture` / `vest normalmap` (runtime-materials.md); the resolver only follows ARMOUR's `material` references.
- Not produced by the resolver: TERRAIN-mode features (biome textures; world mode draws them with the terrain renderer, below), dust,
  construction scaffold, colour masks / dyes, metalness, the character's hair/beard overlays, skin tone, blood.

## Conventions (Verified by screenshots)

Ogre's conventions match OpenGL's defaults: right-handed, Y up, counter-clockwise front faces. Texture rows are
uploaded as stored (top row first) and UVs used unchanged; text-free proof is the human body map (shorts on the
hips, feet at the bottom, face on the head). Nothing renders inside out with back-face culling on.
Kenshi's units: a human is about 19.5 units tall, the katana the game loads 12.8. Weapons are not scaled when
attached (their node only undoes the body node's scale); the 1.6-unit `data/meshes/katana05.mesh` is a different
file the game never picks ([Finding files](#finding-files)). These were checked in the removed mesh and character
viewers ([character-viewer.md](character-viewer.md)).

## Limitations

Of the texture resolution above:

- One texture set per mesh from FCS; per-submesh only for script materials.
- No metalness or specular maps beyond diffuse-alpha gloss. No sRGB handling, which matches Kenshi (Observed: it
  does no sRGB conversion either, [characters.md](characters.md#colour-space-observed)).
- The swizzled-normal decision is a heuristic, not what the game does (the game picks by shader template).

## Characters

The character viewer (`--character`) was removed in phase 8 (DECISIONS 23). How it assembled, posed, skinned, animated
and shaded a character, its options and its screenshot findings are in [character-viewer.md](character-viewer.md);
the rules it followed are in [characters.md](characters.md) and [animation.md](animation.md).

## World mode

The world renderers live in `src/Meitou.Rendering` (shared with the game, `meitou`; see [engine.md](engine.md)); the viewer adds
its options, the interactive keys, `--screenshot` and `--fly-benchmark`. `meitou-viewer --world` draws a square region of the Newland world: the terrain from `fullmap.tif`, textured
the way Kenshi's terrain shader layers its biomes, plus the buildings of the zone files and the map features of
`features.dat`. Facts used: [formats/terrain.md](formats/terrain.md#how-the-terrain-is-textured) and
[formats/zones.md](formats/zones.md#from-placements-to-meshes).

```
dotnet run --project tools/Meitou.ModelViewer -- --world --town "The Hub" --radius 1 --distance 2500 --pitch 30
dotnet run --project tools/Meitou.ModelViewer -- --world --zone 44,23 --radius 1.5 --screenshot out.png
dotnet run --project tools/Meitou.ModelViewer -- --world --at -2304,62208 --radius 3 --no-objects
dotnet run --project tools/Meitou.ModelViewer -- --world --radius 32 --no-objects --distance 260000 --pitch 60
```

`--world --help` lists the options: where (`--at x,z`, `--zone i,j`, `--town <name>`, default the world's
centre), `--radius` in zones (default 1.5), `--step` (heightmap sample step; by default the smallest power of
two keeping at most 2048 cells per side, so `--radius 32`, the whole world, uses step 8), camera
(`--yaw`, `--pitch`, `--distance`), `--screenshot` / `--size` (the window opens maximized on Vulkan, vsync on; `--size` is for
screenshots, which are headless), `--no-textures`, `--no-objects`, `--no-foliage` (`F` toggles), `--distant-range <zones>`, `--no-distant`,
`--object-distance` (default 20000 in Meitou, 12000 in Faithful: the `reach` switch, "Draw distances" below), `--landmark-distance` (Meitou only, default 150000, 0 = none), `--layer-size` (terrain layer textures, at most 2048), `--texture-quality 0..4` (the game's `texture resolution gimping`; the viewer defaults to 0, full size; the game's missing-key default 1 drops one top mip of compressed textures as they load, so the terrain layers become 1024²; [formats/settings.md](formats/settings.md#viewer-texture-quality)), `--water-reflection 0..4`, `--reflection-range`, `--debug 1|2|3`, `--time <hour>` (default 13), `--no-water`,
`--view-distance` (default 450000), `--fog` (distance where the haze is complete at ground level, default 250000), `--simple-sky` (the old colour-model sky and fog; `B` toggles), `--wetness <0..1>`, `--rain <0..100>` and `--dust x[,inside,slope]` replace the weather's wet-surface, rain-ripple and dust values (testing, [formats/weather.md](formats/weather.md#wet-surfaces-dust-and-rain-ripples-in-the-viewer-step-4)); `--weather <name|auto>` (default `auto`: the weather scheduler at the camera, from `--day <n>` (default 0) and `--time`, `--weather-seed <n>` (default 1); a WEATHER record's name forces that one: sky colour, fog, clouds, wind, heat haze and camera particle effects, "Default" is clear), `--no-particles`, `--no-fog-volumes` (leaves out the placed fog volumes of `fogfeatures.dat`, the swamp's fog among them: [formats/fogfeatures.md](formats/fogfeatures.md)), `--no-occlusion-cull` (keeps the foliage the previous frame's depth hides in the main colour views; a Hi-Z pyramid, picture unchanged, F11 `occlusion` line, [formats/foliage.md](formats/foliage.md#occlusion-culling-in-the-viewer-meitou-2026-10-08); `MEITOU_OCC_ALT=1` runs it on alternate frames for same-run timing, `MEITOU_FLY_TURN=<rad/frame>` turns the fly benchmark), `--no-fog-cull` (draws what the fog around the camera completely hides, which Meitou otherwise leaves out of the main camera's view: terrain nodes, objects, characters and foliage zones wholly inside the drawn-last fog block beyond the distance where it is opaque; the picture is the same, F11 and the `--screenshot` log have a `fog cull` line with the counts; the same switch covers the weather fog cull: beyond the distance where the weather's own fog is complete (a weather with fog enabled, no placed fog volume reaching past it) the same things are left out and Meitou's shadow cascades end there; [In Meitou](formats/fogfeatures.md#in-meitou)), `--particle-prewarm <s>` (docs/formats/particle-universe.md), `--clouds <0..1>` and
`--material-distance` (where the full terrain material gives way to the ground colour, default 30000 as in the
game), `--terrain-error <px>` (the terrain LOD's screen-space height error in pixels of the rendered picture, default 16 in Meitou and 10 in Faithful (the `reach` switch); `--terrain-far-error <px>` and `--terrain-ramp <u>` add a far ramp like the game's, off by default; `MEITOU_TERRAIN_LOD_LOG=1` prints the level ranges and drawn nodes per level). `--camera-at x,z` starts the camera somewhere
else than the loaded point (as if flown there), `--no-stream` keeps the detail around the start point instead of following
the camera (the behaviour before streaming), and `--fly-to x,z` with `--screenshot` flies there first at 3 times the
fast key speed and prints the frame times on the way (a streaming test). `MEITOU_STREAM_LOG=1` prints upload steps over 3 ms.
`--fly-benchmark <frames>` is the streaming benchmark (offscreen, no window; see "Streaming cost and unloading" below):
it flies the camera round a circle (`--fly-radius`, default 12000 units, round the loaded point; `--fly-speed`, default 150 units per
frame, 9000 per second at the 60 frames per second of wall time it paces itself to) and prints frame-time percentiles, the worst
frames with the render-thread time of each stage, the resident memory and (`vram` line) every allocator owner with its MB, device-local
MB and count. With `--screenshot` the picture is taken afterwards, back at the start. `MEITOU_PASS_STATS=1` adds the per-stage table of
docs/engine.md "Frame cost breakdown" (native counters since phase 8 stage 3; GPU per frame since 2026-10-07) and a `gpu  mean` line with the
pre-frame's GPU time (uploads, cull and grass kernels, bakes). Its `counts` lines give grass blades, the objects' instances, triangles and calls per
frame (colour and shadow, and those stopped by the game's part distance) and, with `MEITOU_FOLIAGE_TRIS=1`, the GPU-culled foliage's triangles,
instance-draws and impostor quads per view kind; the `top` line lists the meshes that drew the most triangles (with their size class and impostor
state) and the impostors that drew the most quads; `sizes` gives the objects' radii. `MEITOU_OBJECT_PART_RANGE=<u>` replaces the 3000 that small
building parts stop at. Two cautions ([render-distance-benchmark.md](render-distance-benchmark.md) section 1): the paced flight lets the GPU clock
down at light load, so take GPU times from `--fly-pipelined` runs; a pipelined run's CPU times include the wait for a frame slot (in
`upd-terrain/post start`), so take CPU times from paced runs.

Its `jobs` line names the recording mode and thread count and gives the job threads' summed CPU time per stage, as a mean per frame. That
is the command recording which wave 4 moved off the render thread ([renderer-native.md 6.5](renderer-native.md#65-as-built-wave-4-2026-10-06)).

The switches:
- `MEITOU_RECORD_THREADS`:
  - `0`: the single-threaded command stream as before;
  - `1`: the shadow, reflection and scene hosts use secondary command buffers, recorded on the render thread;
  - the default: the secondaries are recorded on the job threads.
- `MEITOU_RECORD_MIN_DRAWS`: the draw count below which a pass is recorded on the render thread anyway. The default is 32; `0` threads
  every pass.

The picture is the same in every mode.

Keys: left drag orbits the target, right drag looks
around, wheel zooms, `W A S D` free fly along the view direction, `Q`/`E` world down/up (speed follows the height above ground; Shift ×4, Ctrl ×0.25), `T` textures, `N` normal
maps, `O` objects, `G` water, `B` simple sky, `,`/`.` time of day −/+ 1 hour (the day follows midnight), `[`/`]` game day −/+ 1 (the weather schedule catches up, it never runs backwards), `\` cycles the forced weather (auto, then each WEATHER record), `X` wireframe, `V` debug view (blend weights,
layer weights, untextured shading), `H` prints the camera as command-line
options, `P` screenshot into C:\Temp, `F1` to `F9` the Faithful / Meitou switches (`particles` and `lod` have only their Tab checkboxes) (see "Post-processing" below), `F11` toggles the frame statistics at the top left (fps, the frame time (wall time between frames, mean and longest since the last update, four times a second), CPU and GPU frame time, VRAM: the process's device-local usage of the driver's budget through VK_EXT_memory_budget, else our allocator's bytes of the heaps' size, then our allocator's blocks and their used bytes, the eight largest owners by allocation name without its numbers (`GpuAllocator.Breakdown`; since phase 8 stage 3 every owner has its own name, no `gl buffer` / `gl texture` is left; `--fly-benchmark` prints them all on its `vram` line, with their device-local MB), the foliage and objects resident detail, reflection, sky and post-processing costs, camera position and zone, drawn terrain, objects and foliage, loading counts, the triangles of the main view per frame (total, then terrain, objects, foliage meshes, rocks and impostors from the GPU cull's indirect arguments, read back only while F11 is shown (`FoliageGpuCull.CountTriangles`), grass at two per blade, a lower bound for cross-quad grass, and characters; shadows and the reflection not counted; the resident-memory lines were removed 2026-10-09); lines wider than the window wrap, continuations indented (`DebugOverlay.Wrap`); the window title names the renderer and shows the fps, updated four times a second), `F12` cycles a profiler chart along the bottom (GPU, render thread, off; see "Profiler" below), `F10` a panel listing these keys below them (built from the usage text; drawn after the screenshot readback, so saved pictures never show it; `--show-keys` opens it at start and, with `--screenshot`, draws it into the picture). `Tab` toggles a settings panel (top left, two columns, drawn over the statistics and the profiler) with sliders, dragged with the left mouse button: time of day (00:00 to 23:59, to the minute, the same hour as `--time` and `,` / `.`), VSync (on by default; off presents with MAILBOX, else IMMEDIATE, so the frame rate is uncapped and F11 says "uncapped"), object draw distance (1000 to 400000, log scale; default 20000 Meitou / 12000 Faithful), landmark distance (Meitou only, 1000 to 400000, log scale, default 150000; see "Landmarks" below), distant-town range (0 to 100 zones), object LOD distance (x0.25 to x4: the mesh LOD value is divided by it, Ogre's LOD bias inverted), foliage and grass draw distance (x0.25 to x80, default x4: four times the game's `foliage range` / `grass range` of 1; with the Meitou `range` switch the foliage one only moves the FAR layers' large meshes, see "Foliage"), foliage LOD distance (x0.25 to x8, default x1, log scale: the generated foliage levels of the Meitou `lod` switch switch where they would differ by less than 4 px divided by it, so x2 keeps full detail twice as far and x0.5 switches at half the distance; the label also shows the pixel tolerance; same as `MEITOU_LOD_PIXELS`; the shadow cascades keep their own 2-texel tolerance; added 2026-10-09), the large, medium and small foliage ranges of that switch (1000-120000, 400-80000, 200-40000, defaults 50000, 12000, 3500), the impostor distance (500-40000, default 4000: where foliage meshes with an atlas become impostors with the `impostors` switch; `--impostor-distance`), the large impostor distance (500-40000, default 12000 since 2026-10-08: the same for the large size class, rocks, rock stacks, hoodoos, ruins and giant trees (other trees take the 4000 distance since 2026-10-08 but are still drawn to the large range), whose billboards looked flat at 4000; `--large-impostor-distance`) and grass density (x0.1 to x2; applies at once to all loaded grass: pages hold the blades of density x2 and a lower setting draws a prefix of them), terrain detail (the screen-space error `--terrain-error` in rendered pixels, 1 to 32 on a log scale, default 16 in Meitou and 10 in Faithful, less is finer; it also moves a far error set with `--terrain-far-error` in proportion; see [formats/terrain.md](formats/terrain.md#in-the-viewer-terrainquadtree-terrainlod-terrainrenderer-terrainshaders)), shadow distance (the game's `Shadow Range`, 1000 to 200000 on a log scale, the game's own slider stops at 9000; its left end shows "0 (off)" and turns the sun shadows off, `ShadowPass.Enabled`, for low-end GPUs), shadow filter (Meitou shadows only, 0 to 2, default 2, `--shadow-filter`; see "Shadows" below), haze strength (0 to 3, default 0.93 so far mountains stay visible, 1 = the game's haze; a viewer option, also `--haze-strength`). A "Reset to defaults" button at its bottom puts every slider back to its value at start (the command-line options and the defaults of the mode the viewer started in: it keeps no table of its own, so a `--faithful reach` start resets to 12000 / 10 px, and toggling `F8` after the start moves the sliders that were still at the old mode's default, but not what Reset returns to). Beside it, "Delete billboard cache" asks "Delete N billboard atlases (M MB) in <folder>?" with Yes / No on a row below (any other click on the panel is No; hiding the panel drops the question) and, after Yes, deletes every `.mimp` in the impostor disk cache (`ImpostorCache.Clear`; temporary files of a save in progress and other files stay) and shows what it deleted. The atlases already resident stay drawn; each is baked again the next time it is needed and not resident (the next start, or after eviction). "Reroll weather" (with the weather scheduler) asks "Reroll the weather of <region> (now <weather>)?" and, after Yes, releases a forced weather and starts a different weather of the camera region's current season at once (`WeatherRegion.Reroll`: the same weighted pick without the current weather and weight-0 entries, a new duration and strength), snapping the sky, fog and ramps; a season with one weather says so. A viewer tool, not the game's. F11's weather line starts with the camera's zone (`zone x,y`, as `--zone` names it). While the pointer is on a panel the camera ignores the mouse. `--show-keys` with `--screenshot` draws both panels. Its text comes from a system monospace font (Consolas, Cascadia Mono, Courier New, DejaVu Sans Mono or Menlo) rasterised with stb_truetype; with none of them the panel is unavailable.

Camera codes: `Ctrl+C` copies the world camera to the clipboard as 50 hex digits: byte 1, then the target X, Y and Z, the yaw, the pitch (radians) and the distance, as little-endian floats. `Ctrl+V` moves the camera to the code in the clipboard. The format is fixed, so two viewers (e.g. two builds side by side) can be put at the same spot. `--monitor <n>` opens the window on monitor n (1-based) and maximizes it there.

Profiler (`F12`; `FrameProfiler`): a chart along the bottom of the last 300 frames, one line per `StageClock` stage (the weather particles have their own, `particles`, since 2026-10-08: their update and their draw, which were in `sky-prepare` and `water`), first the GPU, then the render thread, then off. Every stage lap writes a GPU timestamp (through the native `QueryArena`, recorded where the frame's commands go with `GpuContext.Interleave`), and a stage's GPU time is the gap since the stamp before it, summed when the stage runs once per depth slice (terrain, objects, foliage, water). The results are read a few frames late, without waiting. `other` is the frame's whole GPU time (`GpuContext.GpuFrameMs`, uploads included) minus the stamped stages, and `present` (render thread only) is the overlay, submit and present after the scene. The wait for a free frame (vsync) is not counted. The header also shows the VRAM (as F11, read every 30 frames), and a pie at the top right splits it: the eight largest device-local owners by allocation name, the rest of ours, the free room in our blocks, and the driver's count beyond our blocks (its own allocations, the swapchain, DLSS). The legend shows the mean of the last 30 frames for both sides. The y axis scales to a round number above the largest value shown. GPU stages `sky-draw` to `post` should add up to about the post `scene` cost in the F11 statistics.

Spike log (`--log-spikes`, or `MEITOU_LOG_SPIKES=1`; `SpikeLog`, in the interactive viewer and `--fly-benchmark`, which then runs the profiler's stamps too): for every frame whose GPU time is over 1.5 times the median of the last 300 (and 2 ms; `MEITOU_SPIKE_FACTOR`, `MEITOU_SPIKE_MIN_MS`), one `spike` line with the frame's GPU stage times (`pre-frame` is the time from the start of the frame's uploads to the end of the pre-frame command buffer: uploads, compute culls, grass kernels, impostor bakes; it is the `other` stage), its render-thread stage times, and the notes of what the frame did: every upload by the allocation's name (`upload impostor atlas albedo 4.50 MB`), `uploads total` (the frame's own counter, to see what no note names), impostor atlases made ready, refined, coarsened or unloaded, bake starts and steps, the memory guard's pressure, GC collections with their pause. The notes are taken when the frame ends and printed when its GPU times have been read, a few frames later. `MEITOU_SPIKE_SKIP=<n>` (default `MEITOU_BENCH_SKIP`) leaves the first frames out (streaming settles); the end of the run prints `gpu-run` (per stage: mean without the frames over 2.5 times the median, p50, p95 and max) and `spikes` (every kind of note over the run: frames it appeared in, count, MB, most in one frame). `--fly-benchmark` prints the Meitou shadows' schedule too (`shadows   meitou schedule`: how often each cascade was drawn and why), and `MEITOU_BENCH_ORBIT=<pixels per frame>` turns the camera round its target while it flies (0.005 rad a pixel). On a card shared with other programs the stage times of one spike line can all grow together with no note: that is the other program, not the viewer (look for a stage that grew alone).

What the spike log found (**Observed**, 2026-10-08, RTX 4070, Release, Shark `--distance 3000 --pitch 10 --yaw 300 --time 12`, swamp rain, 1280x720 DLSS; the card was shared, so only stages that grow alone count). (1) With a warm impostor cache and a still camera the viewer itself has no periodic spike: 8 frames over 1.5 x the median in 3000, in a different stage each time, with no upload or rebuild noted, in runs where other programs were using the card. (2) The "every few seconds" spikes are impostor bakes: frames with a bake step cost 3 to 6 ms of GPU time in the pre-frame commands (`other`), 8 to 12 ms frames against a 5.4 ms median, one bake after another as new atlases are wanted (empty cache, `MEITOU_IMPOSTOR_CACHE` on a scratch folder, 237 atlases in 90 s: 201 frames with a pre-frame over 2 ms, 11.4 ms at most; `other` p95 0.47 ms). Rock atlases are made per biome, so a flight into a new biome bakes with a warm cache too. The bake budget was 40 million shaded samples a frame; at 4 million (now the default) 2 such frames and 3.4 ms at most (`other` p95 1.31 ms: more frames carry about 1 ms each instead of few carry 4 to 6; 12 million gave 17 frames and 3.5 ms). Details and the correction of the old "a bake is cheap" claim: [impostors.md](impostors.md) section 8. (3) Not changed: atlas uploads of up to 9 MB in one frame (two large atlases' first levels back to back; `UploadBytesPerFrame` is 6 MB but a whole level goes in a step), a few ms of render-thread copying and little GPU time; and, with the memory guard in pressure (a card shared with other programs), the same atlas coarsened and refined again within a few frames (`impostor coarsened ... skip 0->1` then `refined`), each a few MB of upload. The shadows' own redraw of far cascades shows as a `shadows` stage of 1 to 3 ms every few frames (cadence above), not as `other`.

```
dotnet run --project tools/Meitou.ModelViewer -- --world --town "Shark" --radius 1.5 --distance 3000 --pitch 20
dotnet run --project tools/Meitou.ModelViewer -- --world --town "Port North" --radius 1.5 --distance 3500 --pitch 22 --yaw 200
dotnet run --project tools/Meitou.ModelViewer -- --world --town "The Hub" --radius 2 --distance 9000 --pitch 10 --yaw 300
```

How it works (status as in [README.md](README.md)), one doc per part:

- [render-terrain.md](render-terrain.md): terrain (CDLOD), texturing, streaming of heights, textures and meshes, upload and unloading, frame times.
- [render-water.md](render-water.md): the game's flat water and Meitou water (waves, shore distance field, foam), with its measurements.
- [render-sky.md](render-sky.md): sky, sun, light and atmosphere (game sky and haze).
- [render-objects.md](render-objects.md): buildings and map features: assembly, object streaming, LOD, draw distance (the `reach` switch), distant towns.
- [render-foliage.md](render-foliage.md): trees, bushes, rocks and grass: paging, GPU cull, impostors, reflections, measurements.
- [render-shadows.md](render-shadows.md): the sun's shadow cascades (the game's CSM and Meitou shadows) and the shadow pass cost.
- [render-post.md](render-post.md): the HDR framebuffer and the post chain (SSAO, fog, exposure, upscaler).
- [bench.md](bench.md): the benchmark harness (`--view`, `--ab`, `--bench-frames`, `--bench-tris`), start-up time and the load caches, `--tiered-jit` / `--pgo`.

Approximations and gaps: no wetness, shadows, volumetric clouds, weather schedule, characters, interiors,
construction states or lights; cliff normal-map channel flips are not reproduced; building part choice follows
the game's rolls but was not compared in game, and nested choices can drift where effects or loading callbacks
roll (zones.md); picks from material collections (towns with "moor mats", 32 parts) are the viewer's own seeded
choice, as the game's are unseeded; foliage as listed in [render-foliage.md](render-foliage.md); doors are always closed and
turrets unaimed; the full terrain material is drawn within the material distance of the eye
(30000), the ground colour beyond it; buildings and map features stream with the camera ([render-objects.md](render-objects.md))
(zones around the eye); the overlay and colour windows hold 73728 units around the eye, so
`--material-distance` above about 36000 shows no more material than that. Known gaps of the streaming: a texture
pair that finds no free slot (more than 192 pairs needed at once; the base game has 133, so only modded installs) is
marked failed for good and stays in ground colour, instead of being retried when a slot frees; the height-window blend
band shifts at a swap (above). The straight-edged patches of other tones in the north-eastern sea of the whole-world
view are in the game's own `biomemap.png`, not a viewer fault
([formats/terrain.md](formats/terrain.md#why-some-sea-areas-in-the-north-east-have-other-tones-observed)).
Interactive mode was smoke-tested only
(starts, loads, renders; the controls were not exercised by hand).
