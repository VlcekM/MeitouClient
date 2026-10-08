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
`--view-distance` (default 450000), `--fog` (distance where the haze is complete at ground level, default 250000), `--simple-sky` (the old colour-model sky and fog; `B` toggles), `--weather <name>` (a WEATHER record's sky colour, fog and clouds; default "Default": clear), `--clouds <0..1>` and
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
maps, `O` objects, `G` water, `B` simple sky, `,`/`.` time of day −/+ 1 hour, `X` wireframe, `V` debug view (blend weights,
layer weights, untextured shading), `H` prints the camera as command-line
options, `P` screenshot into C:\Temp, `F1` to `F8` the Faithful / Meitou switches (see "Post-processing" below), `F11` toggles the frame statistics at the top left (fps, CPU and GPU frame time, VRAM: the process's device-local usage of the driver's budget through VK_EXT_memory_budget, else our allocator's bytes of the heaps' size, then our allocator's blocks and their used bytes, the eight largest owners by allocation name without its numbers (`GpuAllocator.Breakdown`; since phase 8 stage 3 every owner has its own name, no `gl buffer` / `gl texture` is left; `--fly-benchmark` prints them all on its `vram` line, with their device-local MB), the foliage and objects resident detail, reflection, sky and post-processing costs, camera position and zone, drawn terrain, objects and foliage, loading counts, resident memory; lines wider than the window wrap, continuations indented (`DebugOverlay.Wrap`); the window title names the renderer and shows the fps, updated four times a second), `F12` cycles a profiler chart along the bottom (GPU, render thread, off; see "Profiler" below), `F10` a panel listing these keys below them (built from the usage text; drawn after the screenshot readback, so saved pictures never show it; `--show-keys` opens it at start and, with `--screenshot`, draws it into the picture). `Tab` toggles a settings panel (top left, two columns, drawn over the statistics and the profiler) with sliders, dragged with the left mouse button: time of day (00:00 to 23:59, to the minute, the same hour as `--time` and `,` / `.`), VSync (on by default; off presents with MAILBOX, else IMMEDIATE, so the frame rate is uncapped and F11 says "uncapped"), object draw distance (1000 to 400000, log scale; default 20000 Meitou / 12000 Faithful), landmark distance (Meitou only, 1000 to 400000, log scale, default 150000; see "Landmarks" below), distant-town range (0 to 100 zones), object LOD distance (x0.25 to x4: the mesh LOD value is divided by it, Ogre's LOD bias inverted), foliage and grass draw distance (x0.25 to x80, default x4: four times the game's `foliage range` / `grass range` of 1; with the Meitou `range` switch the foliage one only moves the FAR layers' large meshes, see "Foliage"), the large, medium and small foliage ranges of that switch (1000-120000, 400-80000, 200-40000, defaults 50000, 12000, 3500), the impostor distance (500-40000, default 4000: where foliage meshes with an atlas become impostors with the `impostors` switch; `--impostor-distance`), the large impostor distance (500-40000, default 12000 since 2026-10-08: the same for the large size class, trees, rock stacks and hoodoos, whose billboards looked flat at 4000; `--large-impostor-distance`) and grass density (x0.1 to x2; applies at once to all loaded grass: pages hold the blades of density x2 and a lower setting draws a prefix of them), terrain detail (the screen-space error `--terrain-error` in rendered pixels, 1 to 32 on a log scale, default 16 in Meitou and 10 in Faithful, less is finer; it also moves a far error set with `--terrain-far-error` in proportion; see [formats/terrain.md](formats/terrain.md#in-the-viewer-terrainquadtree-terrainlod-terrainrenderer-terrainshaders)), shadow distance (the game's `Shadow Range`, 1000 to 200000 on a log scale, the game's own slider stops at 9000), haze strength (0 to 3, default 0.93 so far mountains stay visible, 1 = the game's haze; a viewer option, also `--haze-strength`). A "Reset to defaults" button at its bottom puts every slider back to its value at start (the command-line options and the defaults of the mode the viewer started in: it keeps no table of its own, so a `--faithful reach` start resets to 12000 / 10 px, and toggling `F8` after the start moves the sliders that were still at the old mode's default, but not what Reset returns to). Beside it, "Delete billboard cache" asks "Delete N billboard atlases (M MB) in <folder>?" with Yes / No on a row below (any other click on the panel is No; hiding the panel drops the question) and, after Yes, deletes every `.mimp` in the impostor disk cache (`ImpostorCache.Clear`; temporary files of a save in progress and other files stay) and shows what it deleted. The atlases already resident stay drawn; each is baked again the next time it is needed and not resident (the next start, or after eviction). While the pointer is on a panel the camera ignores the mouse. `--show-keys` with `--screenshot` draws both panels. Its text comes from a system monospace font (Consolas, Cascadia Mono, Courier New, DejaVu Sans Mono or Menlo) rasterised with stb_truetype; with none of them the panel is unavailable.

Camera codes: `Ctrl+C` copies the world camera to the clipboard as 50 hex digits: byte 1, then the target X, Y and Z, the yaw, the pitch (radians) and the distance, as little-endian floats. `Ctrl+V` moves the camera to the code in the clipboard. The format is fixed, so two viewers (e.g. two builds side by side) can be put at the same spot. `--monitor <n>` opens the window on monitor n (1-based) and maximizes it there.

Profiler (`F12`; `FrameProfiler`): a chart along the bottom of the last 300 frames, one line per `StageClock` stage, first the GPU, then the render thread, then off. Every stage lap writes a GPU timestamp (through the native `QueryArena`, recorded where the frame's commands go with `GpuContext.Interleave`), and a stage's GPU time is the gap since the stamp before it, summed when the stage runs once per depth slice (terrain, objects, foliage, water). The results are read a few frames late, without waiting. `other` is the frame's whole GPU time (`GpuContext.GpuFrameMs`, uploads included) minus the stamped stages, and `present` (render thread only) is the overlay, submit and present after the scene. The wait for a free frame (vsync) is not counted. The header also shows the VRAM (as F11, read every 30 frames), and a pie at the top right splits it: the eight largest device-local owners by allocation name, the rest of ours, the free room in our blocks, and the driver's count beyond our blocks (its own allocations, the swapchain, DLSS). The legend shows the mean of the last 30 frames for both sides. The y axis scales to a round number above the largest value shown. GPU stages `sky-draw` to `post` should add up to about the post `scene` cost in the F11 statistics.

```
dotnet run --project tools/Meitou.ModelViewer -- --world --town "Shark" --radius 1.5 --distance 3000 --pitch 20
dotnet run --project tools/Meitou.ModelViewer -- --world --town "Port North" --radius 1.5 --distance 3500 --pitch 22 --yaw 200
dotnet run --project tools/Meitou.ModelViewer -- --world --town "The Hub" --radius 2 --distance 9000 --pitch 10 --yaw 300
```

How it works (status as in [README.md](README.md)):

- **Terrain** (`TerrainQuadtree`, `TerrainLod`, `TerrainRenderer`): CDLOD out to the horizon with a screen-space error measured in rendered pixels (the viewer's own rule, not the game's; same in Faithful and Meitou), as described in
  [formats/terrain.md](formats/terrain.md#terrain-lod). The whole map (every 8th sample) and a `HeightWindow` of the
  finest samples around the eye are height textures (the window follows the camera, see Streaming); one 64 × 64 grid patch is drawn per quadtree node, with the odd vertices
  morphing onto the coarser level, so there are no cracks or pops. Nodes outside the frustum are skipped.
  Two depth slices (far 20000 to `--view-distance`, then near), with the near plane following the eye's height.
  Beyond `--material-distance` the terrain shows the biome ground colour × the colour map.
- **Water** (`WaterRenderer`; [formats/terrain.md](formats/terrain.md#water)): one surface at Y = 100, a
  quad centred on the eye and 1.5 times the view distance wide, so the sea reaches the horizon in every direction, past
  the world's edge too (the game's own distant plane stops at the edge; the infinite sea is the viewer's choice). Beyond
  the edge the water colour and parameters fade over 30000 units from the edge pixels to the open sea (the most common
  parameters among the outer ring of the maps), so the last pixels do not stretch outwards, and the water counts as deep.
  It has colour from `watercolourmap.png`, flow from `flowmap.png`, the `water.png` normal map scrolled three
  times, and per-pixel biome parameters (`BiomeField` over `blendinfo.dat` + `blendmap.png`). Shallow water is
  see-through near the camera, and it is opaque beyond 4000 units, as in the game. With reflections (default; `R` or `--no-reflections` toggles) it reflects the mirrored scene (`ReflectionPass`: sky, terrain, objects at 3000 units or nearer drawn about Y = 100 into a half-resolution RGBA16F texture, drawn 4x multisampled and resolved (without it the mirrored shoreline showed stair steps, magnified by the normal-map distortion), with oblique near-plane clipping at the water; the Fresnel and normal-map distortion are the old shader's); without, it reflects the sky colour. The glint widens and dims with distance and is capped, so a far sea shows no blown-out disc.
- **Sky, light and atmosphere** (`SkyRenderer`, `AtmosphereShaders`, `SkyClock`, `SkyXModel`, `KenshiHaze`, `KenshiLighting`,
  `AmbientMap`; facts in [formats/sky.md](formats/sky.md) and [formats/lighting.md](formats/lighting.md)): the sun follows the
  game's formula for the hour (latitude 54, sunrise 5, sunset 23). In game-sky mode (default) everything is in the game's own HDR
  units and numbers. The sky is SkyX's skydome evaluated per pixel with the game's options (wavelengths 0.57 / 0.48 / 0.44,
  exposure 1.4, 4 samples, HDR mode), the night glow and the game's starfield and moon texture; no sun disc (the game has none in
  the dome; its sun is the Mie glow), with `--clouds` or a cloudy weather a cloud layer (a stand-in). Terrain, objects and grass
  are lit by `kenshiLight`, the game's deferred lighting model: the sun colour taken from SkyX towards the sun the way the game's
  sky controller takes it, times the daylight factor and the per-biome ambient map's sun brightness; the image-based ambient
  from `mp_irradiance.dds` times the ambient map's colour; GGX sun specular and the `mp_specularity.dds` environment specular with
  the game's environment BRDF. No shadows (every face towards the sun is fully lit). Every world shader ends with
  `colour = atmoApply(colour, eye, position)`, the game's haze (sky.md, "Haze"). The post-processing applies the game's auto
  exposure from the measured mean luminance, so the screen brightness follows the game's `0.55 / adapted` with the CONSTANTS band.
  New shaders include `AtmosphereShaders.Functions` (sky.md, "Using the atmosphere in a new shader"). `--simple-sky` / `B` give
  the old colour model, light and fog, with a fixed exposure.
  Cost: not re-measured after the 2026-10-05 rewrite (the sky's integral now runs per pixel, about 4 × 5 exponentials, instead of
  a table lookup; the haze runs the same 4-sample integral per pixel). Earlier figures (2026-10-04, table version): the sky pass
  0.06 to 0.09 ms GPU at 1280 × 720 (`MEITOU_SKY_BENCH=1` with `--screenshot` times 100 passes).
- Frame times (2026-10-04, offscreen 1280 × 960, average of 10 frames after loading): Shark with water 1.6 ms,
  Port North coast 1.6 ms, The Hub looking at the horizon 1.1 to 1.2 ms, whole world from above 0.6 ms
  (12 ms at 1400 × 1400 with `--material-distance 1e7`, which puts the full material everywhere).
  With streaming (same machine and method, 2026-10-04): Port North 1.2 to 1.3 ms (1.4 before); the same view after
  `--camera-at -2304,62208` 2.9 to 4.2 ms with the material all over the screen (0.5 to 1.1 ms with `--no-stream`,
  where the far terrain is only ground colour); sea past the map edge 1.5 to 3.4 ms; whole world from above 1.8 ms and
  2.6 ms at 1400 × 1400 with `--material-distance 1e7` (the material now reaches as far as the maps' window, 36864
  units around the eye). Flight with `--fly-to` (Port North to -2304,62208 at 8400 units per second, 60 frames per second of wall time,
  1364 frames, 191000 units, no objects), five runs: median 1.9 to 7.1 ms, 99th percentile 6.7 to 25 ms (`--no-stream`:
  3.2 and 16.0 ms; the runs differ a lot with what else the machine does, and the streamed view shades far more
  material). The first frame costs 200 to 250 ms in every run (shader compile, as with `--no-stream`). After it the worst
  frame was 9 to 35 ms in four runs and 407 ms in one (no streaming step was that long; a loaded machine).
  Before the maps were rewritten in place, every window move cost a frame of about 50 ms.
- **Texturing** (`TerrainTextures`, `TerrainShaders`): the biomes of every `blendinfo.dat` cell within the material
  distance of the eye are loaded into two texture arrays (one layer per distinct diffuse/normal pair) **compressed on the GPU as stored**: the diffuse array is BC3 (colour, gloss in alpha), the normal array BC1 (the shader reads only their RGB). A square power-of-two BC1/BC3 DDS at least `--layer-size` wide with its mips is uploaded block for block from the level of that size down to 1x1 (no decode, no resample); BC1 diffuse maps get an opaque alpha block and BC3 normal maps lose their alpha (`BlockCompression.Bc1ToBc3` / `Bc3ToBc1`, tests in `BlockCompressionTests`). Anything else (smaller or non-square files, no mip chain, PNG/TGA, a missing file as a constant colour) is decoded, scaled to size², given a box-filtered mip chain and encoded again with a small bounding-box BC1/BC3 encoder (visibly softer than a stored texture, but the base game has only three such files). The default layer size is **2048**, the game's own: 133 pairs take 1064 MB (66 MB at 512, 266 MB at 1024; the old RGBA8 arrays were 4 to 8 times as large: 1024 would have been 2.1 GB, 2048 8.4 GB), 1.5 GB at the 192-slot limit. Loading the 19 biomes around the rock view takes about 5.5 s at 2048 (disk and block conversion on worker threads) against 1.4-1.8 s at 512 or 1024. `--layer-size 512` / `1024` still work (they take a lower mip level of the stored file); per-biome constants go into a float texture with one row per biome; the blend
  map is sampled for the five slot weights; windows of the overlay and colour maps follow the eye (see Streaming). The layer model, slope scaling
  (FCS value × 0.01, **Verified** from the exe) and the overlay/colour channels follow terrain.md.
- **Streaming** (`TerrainStreamer`, `TerrainTextures`, `UploadQueue`): detail follows the camera, nothing is tied to
  the start point. All decoding is on worker threads; every GL call stays on the render thread, in steps of about 2 MB
  that run until 2 ms of a frame is spent, so no frame waits for a whole upload.
  - *Fine heights*: when the eye is further than 15% of the window's width from its centre, a worker re-reads a
    `HeightWindow` (1536 cells, 27648 units, at the start window's step) centred on it from a second handle on
    `fullmap.tif`, measures it against the quadtree's min/max cells, and the render thread uploads it in slabs into a new
    texture and swaps. The old window is used until then; in the interior both hold the same samples (origins are
    multiples of 64 samples), so nothing moves there. The blend band at the window's border (about 2800 units wide,
    coarse to fine) does move with every swap, so terrain 7000 to 14000 units ahead of the eye can change a little. The bounds only ever widen. Not done with `--step` 8 or more (as coarse
    as the whole-world grid).
  - *Biome layers*: each frame, the cells within the material distance + half a cell of the eye give the biomes needed,
    nearest first. Their texture pairs decode (at most 2 to 6 at a time) and are installed in free slots of the arrays
    (133 pairs in the base game, all fit; the limit is 192 slots, then the least recently needed unused pair is
    evicted). A biome is resident when all its pairs are. The cell table says per biome slot: the biome's row,
    "loading" (254) or unused (255); a slot still loading shows its share in the ground colour, so material fades in
    biome by biome, never as flat or wrong colour.
  - *Overlay and colour maps*: windows of 2048² overlay pixels (36 units) and 4096² colour pixels (18 units), the same
    73728-unit square, in two textures addressed toroidally (texel = world pixel mod window). Moving the window by
    4608 units or more rewrites only the strips that came into view, in place, with mips up to level 6 cut on the
    worker (the origin is aligned to 64 overlay pixels). Decoded tiles are cached (24 overlay, 12 colour). A fresh
    texture per window stalled the GPU for about 50 ms at its first use (Observed on this machine: a re-centre frame of 50 ms, against
    under 16 ms in place), which is why the strips are rewritten in place. The textured shading fades out over the last
    3000 units of the window, far beyond the material distance.
  - Screenshots wait for everything the camera view needs (`TerrainStreamer.Settle`); interactively the far ground
    colour shows first and the material appears around the camera within a second or two (the F11 statistics show `loading N`).
- **Streaming cost and unloading** (`BackgroundWork`, `UploadQueue`, `WorldTextureCache`, `ObjectMeshCache`, `FoliageRenderer`; **Observed**
  with `--fly-benchmark`, 2026-10-04, 1280 × 720, RTX 4070, `--world --town "The Hub" --radius 1.5 --distance 3000 --pitch 20`, 1500 frames at
  150 units per frame round a circle of 12000 units; the machine was shared with the game and other viewers, so run-to-run spread is large):
  - *What ran on the render thread* (found with the benchmark's stage clock, `StageClock`, laps in `WorldApp.Draw`; the worst frames list
    the stages that took 1 ms or more): foliage `Accept` grouping thousands of instances (15 to 20 ms), the foliage texture pump, which had no
    time budget (one texture's slabs, 10 to 35 ms), grass-page and foliage-mesh uploads in one `BufferData` each (10 to 20 ms), the object
    streamer making a big zone's instances in one frame (up to 30 ms), and, on a loaded machine, worker threads at normal priority taking time
    slices from the render thread. Fixed by: grouping on the zone's worker (`PreparedZone`); `WorldTextureCache.Pump` always bounded (default 1.5 ms)
    and slabs of 512 KB; foliage meshes and grass pages uploaded in 512 KB slabs (a page dropped meanwhile deletes what its steps made);
    `ObjectStreamer.FillZones` makes instances 1 ms a frame; every decode and layout job runs on `BackgroundWork`, a few dedicated
    below-normal-priority threads (`ProcessorCount - 3`); a replaced terrain height texture is deleted four frames after the swap.
  - *Compressed textures*: BC1, BC2, BC3, BC4 and BC5 DDS textures with a full mip chain are uploaded as stored (`CompressedTexImage2D`, S3TC; BC4/BC5 as RGTC (core since GL 3.0), BC4 with a swizzle so it still reads as grey with alpha 1 like the decoder's output; **Unverified on a real file**: the base game has no BC4/BC5, the path is only reviewed and compiles); in slabs
    of whole block rows) instead of decoded to RGBA8: 4 to 8 times less GPU memory and no CPU decode of the mips (only the level the swizzle
    test reads). Other formats, and files without a full chain (the viewer generates mips for those), take the RGBA8 path. The picture does not
    change visibly: two runs of The Hub differ by a mean of 0.05 per channel value (max 38 on a few edge pixels, run-to-run noise) and compressed
    against RGBA8 by 0.065. `MEITOU_UNCOMPRESSED_TEXTURES=1` brings the old path back for comparison.
  - *Unloading*: nothing is unloaded while within a draw range, whatever the camera looks at; once a second the objects mark instances within
    their range (`WorldObjectRenderer.MarkInRange`: the mesh as used, the material's textures read) and the foliage the groups of zones
    within their layer range (`FoliageRenderer.TrimResident`). Then a GPU mesh not marked for `IdleSeconds` (60) is deleted (buffers, vertex
    arrays; the instances that had resolved to it go back to waiting, `ObjectStreamer.Scan` asks for it when they come in range again), and a
    `WorldTexture` whose id nobody read for 60 s is deleted (`WorldTextureCache.Trim`; reading `WorldTexture.Id` is what counts as use and
    also starts the reload of an unloaded texture, so materials never need rebuilding). Hysteresis is that idle time. Memory pressure:
    above 768 MB of object meshes or 1024 MB of textures (per cache) the least recently used ones idle for 8 s go too, down to three quarters
    of the mark. At most 24 meshes and 40 textures are deleted per second. Grass pages and terrain biome layers already had their own eviction.
    `MEITOU_UNLOAD_IDLE=<seconds>` changes the idle time (a huge value turns unloading off, for comparison).
  - *Numbers*: resident GPU memory of meshes, textures and grass (the F11 statistics show `resident` MB; `Describe()` and the benchmark list meshes,
    textures, unload and reload counts) over a 3000-frame flight round a 20000-unit circle: before, with nothing unloaded and RGBA8 textures,
    it grew to 6.4 GB (3.5 GB of foliage textures, 2.7 GB of object textures) and stayed there, process working set 5.7 GB (the first
    benchmark runs: 3.0 to 4.4 GB resident, working set 5.6 to 6.2 GB after a 1500-frame flight); now 1.3 to 1.4 GB (compression alone) and, with
    `MEITOU_UNLOAD_IDLE=10`, a 0.7 to 1.3 GB band that follows the camera (about 850 object meshes and 400 foliage meshes unloaded and
    reloaded, 160 + 420 textures); after such a flight the same view looks the same as without unloading (mean difference 0.045, as run-to-run).
    Frame times with water reflections on, 1500 frames: before, 12.9 to 21 ms median, 61 to 83 ms 95th percentile, 122 to 305 ms 99th,
    maximum 270 ms to 2.3 s (those runs also had 5 GB more GPU memory in use); now 11.6 to 12.6 ms, 28 to 30 ms, 73 to 79 ms, maximum 125 ms
    (one run 694 ms, 635 ms of it waiting for the GPU). Render-thread time up to the end of the commands (no GPU wait): median 8.6 to 9.4 ms,
    95th 24 to 26 ms. With `--no-reflections` (the same flight): median 10.0 ms, 95th 16.8, 99th 23.1, maximum 37.0 ms, 28 frames over 20 ms and one over 33.
  - *Left*: single steps of a texture or buffer slab still take 10 to 20 ms now and then (driver or GPU contention, not size: they hit the first
    slab of a fresh buffer or texture), and one such step is the floor of a frame's overrun. The first foliage draw after a burst of streaming
    can take 20 to 50 ms too (it was often the reflection's, which draws first). Not done: a persistent-mapped upload ring (GL 3.3 core has none;
    buffers are filled with `BufferSubData`), unloading the terrain's overlay and colour windows (fixed size). The reflection pass itself is cheap now
    (next bullet, "Reflections").
- **Objects** (`WorldObjects`, `WorldObjectRenderer`): placements become meshes with `WorldObjectLayout`, built
  as the game builds them ([formats/zones.md](formats/zones.md#from-placements-to-meshes)): parts chosen with the
  game's `rand()` seeded from the position, doors added, destroyed states (`destroyed mesh`, upper floors
  removed), rotating parts at their rolled start angle, and each part's MATERIAL_SPEC from the part, the building
  or the building's town (`BuildingTowns`; `BuildingMaterial` turns it into textures). A building whose state names an
  `exterior layout name` also gets that layout's signs and banners (`BuildingLayouts`, from `interiors.level`;
  [formats/zones.md](formats/zones.md#building-layouts)), counted in the `objects` log line; interior layouts (furniture)
  and nest debris are not drawn. Map features (and parts
  without a chosen material) are textured by `MaterialResolver` ([above](#how-mesh-textures-are-resolved); candidate preferred: the one
  naming the placed record). Draw distance, LOD and streaming are described below ("Object streaming, LOD and distant towns"). The log
  line `objects` counts destroyed buildings and the foliage resource buildings, which the game draws as foliage rocks (drawn by the foliage below).
  TERRAIN-mode map features are drawn with the terrain shader through plain per-level vertex arrays (`TerrainRenderer.DrawMeshes`), with the game's `Feature_Terrain` rules ([formats/foliage.md](formats/foliage.md#terrain-mode-meshes)): the one biome of `biomemap.png` at the mesh's origin (the terrain's blend-map mix while that biome's textures are not resident), slope clamped at 1, per-vertex cliff projection weights, no roads, back faces culled; they pick a mesh LOD level but do not fade.
- **Object streaming, LOD and distant towns** (`ObjectStreamer`, `ObjectMeshCache`, `BuildingLodShaders`, `ObjectRanges`; data side
  `MeshLod`, `DistantTowns`):
  - Zones are laid out on worker threads (`WorldObjects.BuildZone`, 3 at a time, nearest first) within the distant range of the eye
    and dropped one zone width beyond it. A mesh is requested once an instance is within its range, decoded on a worker (including the
    index buffer with every LOD level back to back), then uploaded in steps of about 1 MB through the shared `UploadQueue`
    (`WorldTextureCache` slices textures the same way), so a frame is rarely held up more than a few ms (interactive run with
    `MEITOU_STREAM_LOG=1`: slow-update lines went from 30 to 220 ms down to mostly under 10, rare spikes to 50 ms from driver syncs and
    scans). Meshes and textures are unloaded again when out of every draw range for a while (next bullet, "Streaming cost and unloading").
  - **Mesh LOD**: per submesh, from the mesh file's levels (`MeshLod`, the rule in [formats/ogre-mesh.md](formats/ogre-mesh.md#lod)),
    with the world bounding sphere of the mesh. Level changes are a dithered cross-fade (interleaved gradient noise, per-instance
    `lo`/`hi` range carried in the instance matrix; the upper level takes the pixels below the threshold and the lower one the rest, so
    they never both cover a pixel), a smoothstep band of 6% of the level's distance. Manual levels draw another mesh file.
  - **Draw distance** (the `reach` switch, `F8`): a real object is dropped by `PartRenderingDistance` (zones.md) and fades out over a band near
    `--object-distance` (default 20000 in Meitou and 12000 in Faithful; the game itself shows real objects only in loaded zones, about 3000).
    The depth slices split at 1.1 x that (at least 20000), so Meitou's second slice starts at 22000. The terrain LOD error follows the same switch (16 px / 10 px).
  - **Landmarks** (Meitou only; docs/renderer-native.md 8.19): a placement whose world bounding radius (the mesh's bounds radius, read from the file without
    loading it, times its largest scale) is 2000 or more is a landmark: the giant map features (the Skylink satellite, rib cages, tower cores, dangler and
    ring wrecks, pipelines; 90 placements of 31 meshes in the base game, no building part). Landmarks are collected once at start from all populated
    zones (about a second on a worker), left out of the zones, and kept in one list that `Scan`, `MarkInRange`, the CPU cull and the shadow casters all walk
    with the same range: `--landmark-distance` (default 150000, Tab slider "Landmark distance (Meitou)", 1000 to 400000) but never less than the object
    distance, and still capped by the game's part distance for building parts. Their meshes use the far LOD forms and mip streaming like any object. Faithful
    (`--faithful reach` or `all`, or `--landmark-distance 0`) builds no list: every placement stays in its zone and the object distance rules, so the
    pictures do not change. Without Meitou at the start there is no list (and no slider); `F8` later moves the distances, not the landmarks.
    With the Meitou shadows every landmark drawn also casts its shadow however far it is (a landmark shadow map along the sun beyond the cascades;
    [formats/shadows.md](formats/shadows.md), "Landmark shadows").
  - **Distant towns**: for a town with a baked mesh (`data/meshes/distant/distant_<handle>.mesh`, `DistantTowns.Find`) the baked mesh
    rises in as the real buildings fade out, with per-vertex fade from the eye distance (no dither cost); it falls off at
    `--distant-range` zones (default 10, the game's setting maximum; the game's default is 6; `--no-distant` disables). A town without one
    shows each building's own `distant mesh` as an instance ("stand-in").
  - **Batching**: one instanced draw per (mesh, material set, level, town): instance matrix rows are vertex attributes 7 to 10 (divisor 1,
    re-pointed per batch), uniforms and texture binds are cached. A view of The Hub from 3500 units: 61 draw calls, 155 instances, 0.3 ms
    CPU (offscreen run, 2026-10-04). Objects are drawn in every depth slice (far first).
  - Debug: `MEITOU_LOD_DEBUG=1` colours surfaces by LOD level, `=2` draws only wireframe by level (green 0, yellow 1, orange 2, red 3,
    magenta manual level, blue distant stand-in). `MEITOU_STREAM_LOG=1` prints slow steps. `--distant-range <zones>`, `--no-distant`.
  - Not done: eviction of meshes, fading of TERRAIN-mode features, cross-fade of manual levels' own materials; the F11 statistics show
    objects, draw calls, draw CPU ms and what is still loading.
- Back-face culling is off for objects (open building meshes); the light (`kenshiLight`) and the haze are the atmosphere's (above).

Verified with screenshots (2026-10-04, saved outside the repo): The Hub (`--town "The Hub"`: walls, gates and
towers join up, buildings upright, roads in the ground texture), zone 44.23 (TERRAIN-mode cliff blocks take
the biome textures), zone 54.44 (UV-mapped cliff features), zone 31.45 at radius 3, and the whole world from
above (biome regions as in `biomemap.png`). Building assembly, before/after (2026-10-04): The Hub (21 destroyed
houses now in their ruined meshes), Brink (wind generators at rolled angles, destroyed shacks), Squin (houses in
the town material instead of a stray candidate), Stack (`--at -55671,-12401 --radius 0.15 --distance 350 --pitch
20 --yaw 90`: the Small Shack's door fills its frame).

Water and sky, verified with screenshots (2026-10-04, saved outside the repo): Shark (houses on stilts and
walkways over swamp water), Port North (coast with sun glitter), the whole world (sea around the land), and The
Hub towards the horizon (terrain to the edge of the map, fading into the haze).

Approximations and gaps: no wetness, shadows, volumetric clouds, weather schedule, characters, interiors,
construction states or lights; cliff normal-map channel flips are not reproduced; building part choice follows
the game's rolls but was not compared in game, and nested choices can drift where effects or loading callbacks
roll (zones.md); picks from material collections (towns with "moor mats", 32 parts) are the viewer's own seeded
choice, as the game's are unseeded; foliage as listed under "Foliage" below; doors are always closed and
turrets unaimed; the full terrain material is drawn within the material distance of the eye
(30000), the ground colour beyond it; buildings and map features stream with the camera (above)
(zones around the eye); the overlay and colour windows hold 73728 units around the eye, so
`--material-distance` above about 36000 shows no more material than that. Known gaps of the streaming: a texture
pair that finds no free slot (more than 192 pairs needed at once; the base game has 133, so only modded installs) is
marked failed for good and stays in ground colour, instead of being retried when a slot frees; the height-window blend
band shifts at a swap (above). The straight-edged patches of other tones in the north-eastern sea of the whole-world
view are in the game's own `biomemap.png`, not a viewer fault
([formats/terrain.md](formats/terrain.md#why-some-sea-areas-in-the-north-east-have-other-tones-observed)).
Interactive mode was smoke-tested only
(starts, loads, renders; the controls were not exercised by hand).

### Foliage

Trees, bushes, rocks (the mineable Iron/Copper rocks too) and grass, placed as Kenshi does
([formats/foliage.md](formats/foliage.md)); `--no-foliage` leaves them out and `F` toggles them.

- **Placement** (`FoliageWorld`, `FoliageLayout`), in two tiers around the eye, each worker with its own heightmap handle, overlay
  tile cache and biome map:
  - *Whole* within the near reach (`NearReach`: the longest MEDIUM / CLOSE mesh layer range, 1000, x `RangeSetting`, or the grass
    reach, 1000 x `GrassRangeSetting`, whichever is longer) plus half a zone of prefetch: every layer and the grass.
  - *Far only* from there out to the far reach (`FarReach`: the longest mesh layer range, FAR 8000, x `RangeSetting`): only the FAR
    layers, without the grass coverage, which is nine tenths of a zone's layout (`FoliageLayout.Place(..., farOnly: true)`; exact, see
    [formats/foliage.md](formats/foliage.md#distances): the far instances are the same as the whole layout's). When such a zone comes
    within the near reach it is laid out whole and its groups are replaced.
  - Order: whole layouts first, then far ones, each nearest first, measured from the eye or from where the eye will be in 1.5 s
    (its smoothed velocity; capped at two zones, and a jump such as a camera code is not a motion), so streaming keeps ahead of a
    flying camera. The prediction adds whole layouts ahead; it never adds zones beyond the far reach. Up to three whole and three
    far layouts at a time; whole layouts go to the front of the worker queue (`BackgroundWork.RunUrgent`), ahead of grass pages and
    texture decodes. Zones beyond the far reach + one zone are dropped.
  - Cost per zone (zone 18.22's 7 x 7 neighbourhood, one thread, **Observed** 2026-10-05): whole 70–100 ms, of which the grass coverage
    65–77 ms; far only 8 ms (reading the ground). `Meitou.Data` is built optimised in Debug too (`-p:MeitouDebugData=true` to debug
    it): unoptimised, a whole zone took 0.6 s and a far one 78 ms, too slow for any flying camera.
  - Settling the view at zone 18.22 at x4 (183 zones in reach): 2.9 s and 48,849 placed meshes, against 44 s and 1,004,250 before the
    tiers; the picture is identical (image-diff mean 0, max 0).
- **Pop-in while flying** (fixed 2026-10-05; the user's report: junk, ruins and plants appearing right in front of the camera around
  zone 18.22, whatever the sliders). Those are foliage (the layers TechRustyJunk, Tech_Wreckage01, Motor_WHOLE, Vast_Cluster_Pieces,
  all MEDIUM: 4000 units at x4), not placed objects (none within 2500 units). The cause was zone layout falling behind the camera:
  every zone out to 32000 units was laid out whole (0.6 s each in the Debug build, three at a time), so at Shift speed (about 8400
  units a second at 1400 units above the ground) the zones under the camera were not laid out yet. `--fly-benchmark 1500
  --fly-speed 140 --fly-radius 60000` from `--at -60564,-45142 --distance 1400 --pitch 25` prints a `pop-in` line (the nearest zone
  within the near reach without its whole layout, per frame): before, 608 of 1500 frames had such a zone, nearest at distance 0 (the
  camera's own zone; frames 200 to 900 nearly all); after, none (and within the far reach, only zones within 1 km of its edge are
  missing at times, against zones at distance 0 before). At 2100 units a second neither had any. `MEITOU_FLY_SHOT=<frame>,...` saves
  those frames of the flight as pictures (with `--screenshot`); frame 500 showed bare rock before and the plants, trees and rock
  stacks after. Two draw-side faults were fixed with it: a zone's groups kept the range of the "Foliage draw distance" slider at the
  time they were laid out (the slider only affected zones loaded afterwards; ranges are now taken at draw time), and a zone was
  culled as a box with a fixed margin of 300 units, so a big mesh (the ruins reach 650+ units from their origin, FAR rock pillars
  thousands) whose zone was out of view vanished while still on screen (the margin is now the zone's largest mesh reach).
- **Meshes** (`FoliageRenderer`, `FoliageShaders`): each instance is drawn up to its layer's range (MEDIUM 1000, FAR
  8000, × `RangeSetting`, the game's `foliage range`), measured along the ground, and fades out with a dither over
  the last tenth of it (the game's 10-unit transition is too short to see). Instances of one mesh are one instanced
  draw per part of the mesh and of its leaves mesh, with the shared mesh shader (so the atmosphere's light and haze
  apply). FOLIAGE-mode meshes and leaves cut out on the normal map's alpha (`alpha threshold` / 255, `leaves alpha
  threshold` / 255), double-sided, with alpha to coverage when the scene is multisampled; other modes are back-face
  culled. TRIPLANAR uses the object shader's triplanar path, DUAL modes the vertex-alpha blend. TERRAIN-mode meshes
  (most rocks) go through the terrain shader (`TerrainRenderer.DrawMeshes`, the map-feature rules above), one draw each, and pop at the middle of
  the fade band instead of dithering. DUST tinting is not reproduced. Trees do not sway (Kenshi's object shader
  has no wind).
- **Ranges by size** (the `range` switch, `F6`; Meitou by default, `--faithful range` for the game's): the game ends all of a layer's
  meshes at the layer's range, so at x4 a tree, a ruin wall and a skull of the same MEDIUM layer all stop at 4000. With Meitou ranges each
  mesh is drawn to the range of its size class ([formats/foliage.md](formats/foliage.md#mesh-sizes): bounding radius × largest scale;
  small below 40, medium to 125, large above): large (trees, ruins, wrecks, rock stacks) 50000 (12000 until 2026-10-07), medium (junk, boulders, bushes) 12000 (5000 until then), small
  (litter, small plants) 3500 by default (800 until 2026-10-08). The three distances are settings, not constants: Tab sliders "Large / Medium / Small foliage range"
  (1000-120000, 400-80000, 200-40000, log scale, steps of 50; defaults 50000 / 12000 / 3500: the billboards draw beyond 4000 as one quad per tree) and `--range-large`, `--range-medium`, `--range-small`. The fade band is the same
  rule as before, a tenth of the range (at least the layer's transition), dithered (screen-door noise of the pixel, as objects' LOD fades; Meitou billboards, a few pixels across at long ranges, take a per-instance threshold instead so they do not flicker over pixels: [impostors.md](impostors.md) section 12, **Verified** from the code, flicker gain **Unknown** by measurement). How the old sliders fit in:
  - "Foliage draw distance x" (`RangeSetting`, the game's `foliage range`) still scales every layer in Faithful. In Meitou it only matters for
    the large meshes of FAR layers (the landmark rock formations, 8000 × x, 32000 at x4): they keep the longer of that and the large range,
    so the formations still stand on the horizon. Every other mesh (MEDIUM and CLOSE layers, and the small and medium children of FAR
    layers) follows its class slider alone.
  - "Grass draw distance x" and "Grass density x" are unchanged: grass has no meshes to classify.
  - Until a mesh is decoded its size is unknown; its groups count as the longest class range (for the reload and residency decisions only:
    nothing undecoded is drawn).
  - No pop-in from it: a zone beyond the near reach is laid out with its FAR layers only, so the near reach (`NearReach`, where zones are
    laid out whole) is the longest class range in Meitou (50000 by default, 12000 before 2026-10-07; against 4000 at x4 in Faithful), or the grass reach if longer;
    the far reach covers the FAR layers' range as before. A longer large range costs layout work on the worker threads (whole layouts,
    70-100 ms a zone, for 1.6 times the area at 5000 against 4000), not render-thread time.
  - Cost (docs/engine.md, "Foliage ranges by size"): at the defaults the dense tree view draws 1 202 meshes instead of 5 034 (most of the
    game's x4 set is litter and bushes), foliage stage 3.5 -> 2.1 ms and culling 4.1 -> 1.2 ms of render-thread CPU (Release); the junk field
    671 -> 377 meshes, 2.8 -> 1.75 ms. Settling a still picture takes about twice as long (more zones laid out whole).
  - `--faithful all` (or `--faithful range`) gives the game's ranges: the parity views are unchanged by the switch (0 differing pixels).
- **Impostors** (the `impostors` switch, `F7`; Meitou by default, `--faithful impostors` never draws them, [impostors.md](impostors.md)): beyond
  the impostor distance (default 4000, Tab slider "Impostor distance (F7 Meitou)", `--impostor-distance <u>`) a tree or bush is one
  camera-facing quad sampling a pre-baked atlas, crossfaded with the mesh over a tenth of the distance, and cast into the sun's shadow cascades
  the same way. The atlases (baked on first need, cached in `%LOCALAPPDATA%\Meitou\impostors`) are held within a VRAM budget that follows the card (8% of the driver's budget, at most 1024 MB; 5% and 256 MB on an integrated GPU; x0.75 under VRAM-guard pressure; 164 / 328 / 655 / 983 / 1024 MB at 2 / 4 / 8 / 12 / 16 GB), `--impostor-budget <MB>` overrides it ([impostors.md](impostors.md) section 10). Far atlases keep fewer mips (`MEITOU_IMPOSTOR_FARMIP=0` off, `MEITOU_IMPOSTOR_MIP_MARGIN` 1 level); a plan keeps the nearest, degrades the farthest and only then leaves a mesh as a mesh. Meshes under radius 48 with 100 or more triangles have a small class (32 px frames, transition by size; `MEITOU_IMPOSTOR_SMALL=0` off, `_SMALL_MIN_RADIUS`, `_SMALL_MIN_TRIANGLES`, `_SMALL_FRAME`, `_SMALL_GRID`). `MEITOU_FORCE_INTEGRATED=1` makes the budget rule treat the card as integrated (testing). The benchmark prints a `pop-in    impostors:` line. `MEITOU_IMPOSTOR_CASTERS=0`
  keeps mesh casters, `MEITOU_IMPOSTOR_LOG=1` logs atlas loads and evictions. The disk cache is capped at 512 MB by default
  (`--impostor-cache-mb <MB>` or `MEITOU_IMPOSTOR_CACHE_MB`, 0 = no cap): older files go first, files of an older format or baker version
  at once ([impostors.md](impostors.md) section 4). `MEITOU_IMPOSTOR_BAKE_MSAMPLES` (40) is how many million shaded samples of a bake are
  recorded in a frame (section 8). `--fly-benchmark` leaves the first n frames out of its percentiles with `MEITOU_BENCH_SKIP=<n>` (default 1;
  a cold run's first 30 frames load the first zones). Tuning knobs for the baker: `MEITOU_IMPOSTOR_GRID` (12),
  `MEITOU_IMPOSTOR_MAGNIFY` (1.4), `MEITOU_IMPOSTOR_BIAS` (1; the caches differ by them).
- **Culling** (`FoliageCull`, step A1 of the GPU-driven foliage in [renderer-native.md](renderer-native.md#56-parity-what-can-match-exactly-and-what-cannot-be-promised)):
  each zone's instances are records of 96 bytes (`FoliageInstanceRecord`: transform, bounding sphere, ground position, scale, group index:
  the layout a compute shader will read), the spheres filled once when the mesh's bounds are known instead of per frame; a group's range
  as range² and the band as a reciprocal; the planes' normal lengths once per view; and the batches drawn in the order of their mesh's
  first group in the work list (visible or not) instead of their first visible instance. Pictures unchanged: 0 differing pixels in the
  ten parity views (Debug, `--faithful all`; Release with Meitou's other switches, `--faithful range`), except the rock view, which differs
  between two runs of the base build itself (21 pixels at 13:00, 6 at 02:00, one spot at about (716-725, 302-323), the fenced wall left of
  the rock; cause **Unknown**): A1's rock pictures equal one of the base runs' exactly, and the Release
  Meitou run differs from the base in the same spot (31 pixels). Tests `FoliageCullTests`.
  - **GPU cull** (step A2, [renderer-native.md](renderer-native.md#561-as-built-step-a2-2026-10-06-foliagegpucull)): by default the meshes' instances
    are tested by compute kernels per view (main slices, cascades, reflection) and drawn with indirect draws; the TERRAIN-mode rocks too, in the same dispatch, drawn through `TerrainRenderer.DrawMeshesIndirect` with their biome rows and mirroring.
    `MEITOU_GPU_CULL=0` brings back the CPU cull above (for A/B); `MEITOU_GPU_CULL_VERIFY=1` runs both, compares the GPU's lists a frame later
    with the CPU's (sets, order, every matrix and fade, the draw arguments, every rock placement) and prints a `gpu cull verify` line at exit. Pictures unchanged (0 px); the instance arena holds 68-byte records (`FoliageInstanceRecord.Pack`, `gpu cull verify` equal);
    the grass too: its pages are picked, thinned and ordered by two kernels per view and drawn with one indirect draw (`MEITOU_GPU_GRASS=0` for the CPU walk; [5.6.2](renderer-native.md#562-as-built-wave-3b-gpu-grass-2026-10-06-foliagegrassgpu));
    `MEITOU_FOLIAGE_TIMING=1` adds the dispatch recording, the rock draws per call and a `foliage gpu cull:` line with the kernels' GPU time per view.
- **Video memory guard** (2026-10-07, [renderer-native.md 8.11](renderer-native.md#811-video-memory-at-large-ranges-2026-10-07)):
  at about 90% of the driver's device-local budget (VK_EXT_memory_budget) streaming of new zones, grass pages, textures and meshes stops, idle
  caches are evicted after 2 s, and the foliage, object and distant-town ranges (and a shadow distance above its default) are multiplied by a
  falling scale until the use is under 80%; the ranges return slowly under 74%. F11 shows `guard: ok, N% of the M MB budget` or
  `guard: ranges x0.62, streaming paused, ...`, and the console logs one `vram      guard:` line the first time. It never acts at the default
  settings. Knobs: `MEITOU_VRAM_GUARD=0` (off), `MEITOU_VRAM_BUDGET_MB=<mb>` (pretend a smaller card, for testing), `MEITOU_VRAM_KILL=<fraction>`
  (headless runs: exit with code 9 past that share of the budget, default 0.95, 0 = off). `--shadow-range` is capped at 15000 on the command
  line. The F11 `scratch` line and the benchmark's `scratch` line give the foliage cull's and grass kernels' per-frame memory (held, need, refused).
  Beyond the whole reach (the longest of the small, medium and grass ranges) a zone keeps only large meshes; the instance arena takes 68 bytes
  a record. All pictures unchanged (0 px).
- **Object textures and meshes at large ranges** (2026-10-07, [renderer-native.md 8.14](renderer-native.md#814-object-textures-and-meshes-at-large-ranges-2026-10-07)):
  what is in range is no longer held at full detail. An object texture loses the top mips no pixel can sample at the distance of its nearest user (the pixel footprint
  from the field of view, render size and sampler bias, over how large a texel is: the part's `MeshTexelScale`, the instance's scale, the material's tile; two levels of margin;
  not triplanar materials, not the distant towns' atlas, not textures of 512 or less) and is loaded again, the old image drawn until the new one is in, when something comes near; a mesh is
  decoded with only the LOD levels (and vertices) its nearest instance can draw and remade in place when one gets nearer. Object textures at the forest view, default / all20 / all40 / `max`:
  536 / 1252 / 1490 / 1725 MB become 500 / 788 / 984 / 1342; object meshes 73 / 273 / 395 / 813 become 53 / 157 / 265 / 666. The caches' high-water marks follow the driver's budget (the old
  fixed marks on the 11.4 GB card) and under the guard's pressure the streaming sheds first. The benchmark's `resident` line shows `mip streaming: N MB less than every mip, R refined, C coarsened`
  and the meshes `remade finer / coarser`; `pop-in    objects:` counts parts drawn untextured, textures waiting for a finer image, instances held back for a mesh remake. Knobs: `MEITOU_MIP_STREAM=0`,
  `MEITOU_MESH_STREAM=0` (the old behaviour, for comparisons), `MEITOU_MIP_MARGIN`, `MEITOU_MIP_TOP`, `MEITOU_MIP_SKIP=<names>`, `MEITOU_MIP_LOG=1`, `MEITOU_TEX_STATS=1` (a texture summary at exit).
  Pictures unchanged: 0 px in the ten views, both modes.
- **Grass**: blades are generated per 576-unit page (8 × 8 a zone) on worker threads when the page comes within
  the grass range (`FoliageGrassField`, the game's candidate rule; seeded per page, so deterministic but not the
  game's exact blades), uploaded as one instance buffer per (page, grass type), and drawn nearest page first with
  quads built in the vertex shader: sway along X on the top edge for wind layers, (held still in offscreen renders, so pictures repeat exactly; interactively it follows real time), sinking into the ground over
  the last fifth of the range, sprite alpha cut at 0.6 (alpha to coverage when multisampled), colour map over the
  zone, lit with an up normal; the aerial perspective is evaluated per vertex.
  Pages are generated nearest first, at most 12 at a time over all zones. **Density**: a page is generated for the
  highest density setting (`MaxGrassDensity`, 2) and records, per 1/64 of its candidates, how many blades they gave
  (`FoliageGrassField.BladesWithPrefixes`). The candidates come from one random sequence in order, so the blades of a
  lower setting are exactly the first ones of a higher setting's (test `Lower_grass_density_is_a_prefix_of_a_higher_one`):
  the "Grass density x" slider draws that prefix of every loaded page at once, no page is regenerated, and the
  game's rule (`density x 0.005 x area x setting` uniform candidates) is unchanged. Memory is that of density 2.
- **Range** (what limited it, fixed 2026-10-04): foliage was drawn only in the near depth slice, i.e. up to about
  20400 units (`SplitDistance` 20000 x 1.02), so the FAR layers (8000 x range setting) were cut short at x4 (32000):
  it is drawn in every slice now (`Draw(..., continuation: true)` for the second, counts and GPU time summed). Zones
  were laid out only to the mesh reach, so a grass range above the foliage range loaded no grass: the reach is the
  longer of both now. The cull skips a whole zone outside the frustum before its instances. Checked with
  `MEITOU_FOLIAGE_RANGE`, `MEITOU_GRASS_RANGE`, `MEITOU_GRASS_DENSITY` (start values of the sliders): at x4 trees,
  rocks and grass appear out to 32000 / 4000 units (screenshots forest zone 14.30 at x1 and x4).
- **Range cost** (RTX 4070, 1600 x 900, 4x MSAA, offscreen, 2026-10-04; GPU times vary 2-5x between runs while other
  viewers run, so the quietest of several runs is given first): zone 14.30 from 5000 units out at x4: 180 zones laid
  out in 9 s (518k meshes, 205 grass pages, 628k blades), 1091 meshes drawn, 313 draw calls, draw CPU 1.5-1.9 ms,
  GPU 1.0 ms (up to 4.5 ms contended), frame 4.6 ms; The Hub and Squin from outside their walls at x4: 0.6-1.2 ms
  GPU, 1.1-1.7 ms CPU. At x16 (measured before the sliders were capped at x8): 700-2100 zones (the whole world is in reach), 2-7M mesh instances
  (about 80 bytes each, up to ~0.5 GB), 2600 grass pages with 5.8M blades in the forest, 3400-5600 draw calls, draw
  CPU 25-70 ms, GPU 30-150 ms, layout 45-190 s: x16 is not usable (sliders stop at x8, not measured). x4 is within 3 ms GPU for
  foliage when the GPU is not shared.
- **Reflections** (`ReflectionPass`; what the game does is in [formats/terrain.md](formats/terrain.md#shading-observed-waterhlsl)). **What is drawn follows the game's `water reflection` and `reflection range`** (`--water-reflection 0..4`, viewer default 4 (the game's missing-key default is 2: sky and terrain only; 3 adds objects, 4 foliage); `--reflection-range`, viewer default 3 x haze distance (the game's is 0.6); Tab sliders; details and what is not done in [formats/settings.md](formats/settings.md#viewer-water-reflection)); the cut-downs below say how much of it. The
  mirrored scene is drawn into a half-resolution 4x multisampled texture, and what it draws is cut to what a half-resolution, ripple-distorted
  image can show. Foliage meshes (no grass) and objects reach 3000 units from the eye (`FoliageDistance` through `FoliageRenderer.Draw(...,
  maxRange)`, which also stops the pass walking the far zones; `ObjectDistance`; before, the foliage went as far as the picture's own layers, up to 32000
  units), objects choose their LOD level at 3 times the distance (`ObjectLodBias`, through `WorldObjectRenderer.LodBias`), and the mirrored
  terrain measures its LOD error with half the pixel scale (`TerrainLodScale` 0.5: its target is half the size; its own quadtree: `TerrainRenderer.Draw(..., secondary: true)`, so the two passes do not reset
  each other's ranges each frame; 37 to 50% of the triangles in the two test views). The pass learns the framebuffer to return to from the caller (`RestoreFramebuffer`) instead of
  three `glGet` calls: with the driver's threaded optimisation those waited for the driver thread and cost 2 to 4 ms of CPU a frame. A finished
  image is reused for up to 3 frames (`MaxAge`) while the eye moves less than 3 units plus 0.4% of its height above the water and the view turns less than
  about 0.1 degree, so a still camera redraws every 4th frame (the water samples the image with the matrix it was drawn with, so
  the only error is that parallax). Knobs for experiments: `MEITOU_REFL_FOLIAGE`, `_OBJECTS`, `_LOD`,
  `_TLOD`, `_AGE` (the defaults above are 3000, 3000, 3, 0.5, 3; `MEITOU_REFL_FOLIAGE=1e9 _LOD=1 _TLOD=1 _AGE=0` restores the old limits; the "before" numbers below used it, so they already include the glGet fix, which alone took the pass from about 5.6 to 1.9 ms of CPU in fly-speed-40 runs; the last A/B runs against a HEAD build were lost to other programs saturating the GPU).
  `--fly-benchmark` prints `reflect`: passes drawn and reused, the pass's CPU and GPU time (timestamp queries, a few frames late; the first
  pass is left out), and the CPU time by part; the worst frames name the part of a slow reflection.
  **Numbers** (**Observed**, 2026-10-05, RTX 4070, 1280 x 720, 1500 frames, `--fly-benchmark` with the default 150 units a frame, three runs each, on a
  machine that other programs share, so only the means of the pass are firm; frame percentiles moved by 2x between runs of the same build, and
  the runs with `--no-reflections` were as noisy): Port North (mostly desert, little water): the pass 0.73 to 1.02 ms CPU and 0.90 to 1.23 ms GPU
  per frame before, 0.58 to 1.02 and 0.66 to 1.24 now (p99 of the frame 17.6 to 36.6 ms before, 20.8 to 70 ms now, 17.0 to 42.2 without reflections: no
  difference that the noise does not cover). Shark (swamp, forests; the pass dominated by foliage): 1.40 to 1.63 ms CPU and 2.40 to 2.48 ms GPU
  before, 1.14 to 1.29 and 1.72 to 1.99 now (-20%, -25%); frame p99 27.6 to 55.7 ms before, 29.2 to 30.8 now (26.4 to 38 without reflections), the
  slowest pass 101 ms of CPU in one run before (12 to 14 ms in the others), 12 to 28 ms now. A still camera (Shark, `--fly-speed 0`): 450 of 600 frames reuse the image, 0.20 ms CPU
  and 0.38 ms GPU per frame. Looks: the same views with the old (HEAD) and the shipped settings differ by a mean of 0.002 to 0.08 of 255 over the picture
  and 0.26 over a pond with mirrored stilt houses and trees (0.36% of its pixels by more than 12, shipped LOD 3 against HEAD; a view of the same scene run twice differs by 0.003);
  pushing it further (foliage 1500, LOD 4, terrain 0.25, objects 2000) loses the trees' reflection in the pond (0.6, 1.2%). Not done: drawing
  the reflection spread over frames (a rendered frame costs the same, so the 99th percentile does not move), fewer samples or a smaller texture
  (the shoreline's stair steps came back).
- **Cost** (RTX 4070, 1280 × 960, 4x MSAA, offscreen, 2026-10-04): a cypress grove in zone 14.30 (995 meshes,
  68k blades) 0.6–1.2 ms draw CPU, about 2 ms GPU (timestamp queries; the F11 statistics show both); the grassland
  at the zone's centre (128k blades) 0.2 ms CPU, 0.3 ms GPU; frame 2.8 ms against 2.3 ms with `--no-foliage`.
  A 20640-unit `--fly-to` from zone 14.30 eastward: median 3.9 ms against 2.8 ms, 99th percentile 46 ms against
  30 ms (uploads are held to 2 ms a frame and one texture a frame). The log line `foliage` gives the totals;
  `MEITOU_FOLIAGE_DEBUG=nograss` or `nomeshes` leaves one part out.
- Verified with screenshots (2026-10-04, saved outside the repo): zone 14.30 (cypress grove with cut-out canopies,
  scattered rocks, grassland with four grass types), swamp zone 24.38 (swamp plants and ferns), an Iron Resource
  rock in zone 30.30 (`--at -8777,-9105`), each against `--no-foliage`.
- Not reproduced: the game's exact grass blades, DUST tint, translucency, wetness, shadows, sub grass, ambient
  sounds, collision; mesh LOD levels (pages use the full mesh, as PagedGeometry's batches do).

### Shadows

The sun's shadow map as the game's CSM mode draws it ([formats/shadows.md](formats/shadows.md)): four cascades in one atlas
(`--shadow-quality <0|1|2>`, 1024² / 2048² / 4096², default 2048²; `--shadow-range <u>`, default 5000 in Faithful and 10000 in Meitou (1000-9000 and 1000-15000; the F5 toggle swaps the default if the range was not touched); `--no-shadows`), drawn
before the reflection and the main pass from the terrain, objects and foliage meshes. `--debug-shadows 1|2|3` shows the cascade
maps, the term per cascade, or the term over the picture.

That is the Faithful choice of the `shadows` switch (F5, `--faithful shadows`). The default is the **Meitou shadows**
([formats/shadows.md](formats/shadows.md#meitou-shadows-the-shadows-switch)): the same atlas, quality and range, but cascades
fitted from the camera's near plane to the range (all four in use), drawn on a staggered schedule (the first every frame, the
others every second or fourth frame, each kept with the matrices it was drawn with), a soft receiver (16-tap rotated disk,
contact-hardening penumbrae from a blocker search, cascades blended, a fade instead of the cut-off at the range's end) and the
terrain's own shadow out to the horizon, so mountains shadow valleys at low sun. The debug view 2 still tints by the game's
cascade selection. It is also the cheaper one (**Observed**, same caveats as below, against `--faithful shadows`): Hub
fly-benchmark shadow stage 2.43 against 3.31 ms CPU; forest camera 2.1-2.3 against 3.6 ms CPU and 0.29 against 0.77 ms GPU
(details in the format doc).

**Shadow pass cost** (**Observed**, 2026-10-05, RTX 4070, Debug build, 1600 x 900, `--faithful all --time 13`; the machine shared with
other agents' viewers, so spread is large). `--screenshot` logs print the pass per cascade (`WorldFrame.DetailedStats`: terrain
triangles, objects and foliage instances, draw calls and CPU ms, and the foliage's steps: culling, upload, meshes, TERRAIN-mode rocks)
and the foliage main pass per depth slice.
- *Where the time went*: at the forest camera (`--at -37582,-80684 --yaw -70.5 --pitch 6.1 --distance 10588`) the shadow pass cost
  ~17-27 ms of CPU against ~1.5 ms of GPU. Cascades 0 and 1 are unused there (the camera's near plane is beyond their split), cascade 2
  draws nothing, and cascade 3 drew 11,214 foliage instances in 6,051 draw calls: 64 instanced batches of trees and bushes and 5,978
  single draws of TERRAIN-mode rocks (`TerrainRenderer.DrawMeshes`, one draw per placement and part with its matrix as a uniform),
  which alone took ~23 ms. The foliage candidate cache shared by the cascades was working (`cull (cached)` in the log, ~1.2-1.4 ms a
  cascade); the cost was VkGl's per-draw work (~4 µs: pipeline lookup, the loose-uniform block copied to the ring for the new matrix,
  descriptor offsets, vertex and index buffer binds) times thousands of draws. The main pass had the same pattern on a smaller scale:
  574 rocks, ~2.8 ms.
- *Fix*: the TERRAIN-mode meshes (foliage rocks and TERRAIN-mode objects) are drawn instanced, one draw per (vertex array, index count,
  mirrored or not), the placement as four per-instance rows at locations 7 to 10 (`TerrainShaders.MeshInstanceLocation`) and, in the
  main pass, the feature's biome row in row 0's w (it only reaches the position's unused w) read as a flat varying
  (`TerrainShaders.MeshFragment`). Exactness: the shaders compute the position with the uniform form's arithmetic; the shadow map is
  depth only with a Less test, so the order of the draws cannot change a texel; in colour only two different placements at exactly
  the same depth could show the order. Measured against the build before (itself identical in two full runs): 0 differing pixels on
  all ten parity views (`tools/scripts/parity.sh`, `--faithful all`) and on `--debug-shadows 1` (the atlas drawn into the picture) at
  the forest and The Hub. The rock view with `--debug-shadows 1` (90 rocks on screen) was identical in 8 of 9 runs; one run differed
  in 20 pixels (largest 14/255) at a distant building, not a rock, with the same streaming and residency counts in its log; the
  build before was identical in 9 of 9. Unexplained at the time. Most likely explained 2026-10-06: two texture uploads into the same
  image in one upload command buffer raced (synchronisation validation: 10 WRITE_AFTER_WRITE hazards per view); a build with step-2
  timing changes showed few-pixel rock-view differences in 2 of 9 runs, and with the uploads ordered by a barrier 0 of 9 and sync
  validation clean (docs/engine.md "Vulkan backend", "Memory and order").
- *Numbers* (`--fly-benchmark 300 --fly-speed 0`, a still camera, three interleaved runs each, medians): forest, draws per frame
  7,485 -> 977, shadow stage 25.3 -> 5.7 ms (its foliage part 23.9 -> 4.2), foliage main pass 7.5 -> 4.1 ms, render-thread CPU per
  frame (p50, commands recorded) 36.9 -> 13.3 ms; The Hub (`--town "The Hub" --distance 40000 --pitch 3`; 46 rocks in the main pass, 3 in the shadow map), draws
  777 -> 746, shadow stage 3.2 -> 3.1 ms, CPU per frame 6.7 -> 6.6 ms (within noise).
- *Not done*: reusing last frame's map when the sun, every cascade's snapped box and the LOD eye are unchanged. It would be exact
  only if every change to the casters (streamed meshes and textures arriving or unloading, foliage pages, terrain height windows, LOD
  and range settings) invalidated it, and there is no single version counter for that yet; a missed case would show stale shadows in
  motion that the still-picture gate cannot catch. Left: the foliage candidate pass (~1.3 ms for the first drawn cascade, which records
  every instance within 1.2 x the shadow range, and ~1.2 ms to test them against each further cascade) and the terrain patches
  (~0.9 ms for cascade 3).

### Post-processing

The world view draws the scene into a single-sample RGBA16F framebuffer with depth and runs a
chain into the window (or, with `--screenshot`, into the offscreen RGBA8 framebuffer that is saved, so pictures go through the
same chain; `PostProcess`, `PostProcessShaders`, `PostProcessOptions`). The window itself is single-sample. What the game does
and why the presets look the way they do: [formats/post-processing.md](formats/post-processing.md). In short, Kenshi has exposure
only (no curve, no gamma, bloom off, SSAO disabled) plus FXAA. MSAA was removed: anti-aliasing is the game's FXAA (Faithful) or a temporal method (Meitou).

- **Faithful and Meitou** (`Enhancements`): every feature where Meitou improves on the game is a switch, Faithful (the game as it ships) or Meitou (the default), chosen one by one; the game's own graphics settings (draw distances, shadow quality) are separate and mean the same in both. Keys F1 to F8 until there is a settings menu (the key list shows each one's state with its detail, e.g. "Faithful (off)" or "Meitou (SSAO)"; toggling prints a note on the difference to the console), `--faithful all` or `--faithful ao,haze,...` on the command line (`--meitou` turns them back on): F1 `ao` ambient occlusion (none / SSAO), F2 `dither`, F3 `haze` (strength 1 / 0.93, far mountains stay visible), F4 `aa` anti-aliasing (the game's FXAA / a temporal method: TAA, FSR or DLSS, chosen on the Tab panel's "Anti-aliasing" slider, which F4 turns back on; FSR and DLSS need their libraries, else TAA), F5 `shadows` (the game's CSM / the Meitou shadows: cascades fitted to the view, soft contact-hardening penumbrae, no seams or cut-off, the terrain's shadow to the horizon; see "Shadows" above), F6 `range` foliage draw ranges (per layer / by mesh size; see "Foliage" above), F7 `impostors` far foliage (meshes only / baked billboards from the impostor distance, crossfaded, also as far shadow casters, and thinned out at the range edge instance by instance while a few pixels across; [impostors.md](impostors.md) sections 7 and 12). F8 `reach` draw distances (the viewer's old values / farther: objects at full detail to 12000 / 20000, the terrain LOD error 10 / 16 px, and in Meitou the landmark distance 150000; "Landmarks" above; `--faithful reach`). The terrain LOD has one rule in both modes, its own resolution-aware screen-space error (the game's per-node rule is documented in formats/settings.md but not replicated); only the allowed error differs, by the `reach` switch since 2026-10-07 (`--faithful all` pictures changed with the rule on 2026-10-06; they are the same again with `reach` Faithful). The Meitou foliage class ranges (50000 / 12000 / 800) belong to the `range` switch, so `--faithful range` is the game's per-layer ranges whatever `reach` says.
- **Cost of the farther defaults** (measured 2026-10-07, [renderer-native.md](renderer-native.md) 8.19): the new Meitou defaults cost GPU and CPU time and video memory against the old ones; the owner also runs the viewer on an integrated-GPU laptop (about 30 fps at the old defaults), where the cost matters most. No integrated-GPU defaults exist yet; the numbers are recorded in 8.19.
- **Presets**: `--post meitou` (default: the game's chain plus SSAO and dither), `--post kenshi` (the game's chain) and `--post off` (nothing, not even FXAA; in game-sky mode the game's auto exposure still applies, since the scene is in the game's HDR units). The upscaler is not part of the presets. The single effects below can be added on top.
- **Effects** (option, key): SSAO (`--ssao`, F1): 12 taps, half resolution, from the depth of the near depth slice only (the far
  slice's depth is cleared before the near one is drawn, so nothing beyond about 20000 units is occluded; it also fades out from
  3000 to 10000 units), normals from depth differences, depth-aware blur, multiplies the HDR colour. Bloom was removed (2026-10-05): the game has it at magnitude 0, and Meitou does not add one. The tone curve (shoulder, ACES), grading and vignette were removed too: the game has none of them and clips at 1, so the composite does exactly that.
  FXAA (`--no-fxaa` turns it off; runs only without an upscaler): the composite writes an RGBA8 picture and FXAA 3.11's quality algorithm with the game's settings reads it into the target (`PostProcessShaders.Fxaa`, written from the published algorithm). Exposure
  (`--exposure`, keys - and =): a multiplier on top of the game's auto exposure (game sky), or the whole exposure with `--simple-sky`; default 1.
  `--post-debug ao` shows the occlusion alone.
  Heat haze (`--no-heat-haze` turns it off, `--heat-haze <x>` replaces the weather's `heat haze` field for testing): the game's shimmer, the last pass (after FXAA, or after the composite when an upscaler runs), strength = the `--weather` record's `heat haze` × saturate(6 · sun height) (strength 1, no scheduler), moving at 1/3 per second, still for screenshots; the F11 statistics show the current value, the target and the weather when there is any. The default weather has none: try `--town Heft --weather "Desert Calm hot 0.6" --time 13` (the Great Desert's hottest calm). It is base-game behaviour, so it has no Faithful/Meitou switch. Details: [formats/post-processing.md](formats/post-processing.md#heat-haze-verified), [formats/weather.md](formats/weather.md#heat-haze-verified-decompiled-fun_1409e8f70-fun_1409ea410).
- **Cost** (RTX 4070, 1920x1080, GPU timestamps, ms per frame; the F11 statistics show `post gpu` per stage next to the frame's cpu and gpu time,
  and `--screenshot` prints the average over 10 frames): SSAO 0.1 to 0.3, composite 0.04, heat haze 0.10 to 0.15 (1600x900, when the weather has some; skipped otherwise, so `--no-heat-haze` and haze-free weathers give the same pictures as before it, checked on the parity views)
  (the `kenshi` preset costs the resolve and composite only). Measured while other jobs shared the GPU, so the spread is
  mostly noise; the whole chain is roughly 1 to 3 ms against a 14 to 18 ms scene.
- **Contract for scene code**: shaders write the colours they always did, unclamped (colours above 1 are fine); code that draws into
  another framebuffer between `PostProcess.Begin` and `End` must rebind the one it found (`ReflectionPass` does).
- **Haze**: `--haze kenshi|physical`, `--haze-distance`; see [formats/sky.md](formats/sky.md#haze-distance-fog-how-vanilla-does-it). The default is `kenshi`, the game's own haze: a linear ramp from 3000 to 30000 (0.06 D to 0.6 D, D = 50000 = view distance 5000 × 10; `--haze-distance` sets D) towards the game's own haze colour (SkyX's Rayleigh in-scattering to the point, in the same HDR units as the sky), which equals the sky's colour at 70000. Far ranges come out as pale layered silhouettes, as in the game, and at night the far terrain goes black (the game's rule). Above the game's camera heights (it never gets more than 1840 above its pivot, [formats/camera.md](formats/camera.md)) the game's formula runs away (from far up it gave a white arc and cyan rims); the viewer's own choice there: from 5100 to 18700 above the highest ground within 2000 of the eye the haze blends into `physical`, and the water's sun glint takes the game's Fresnel factor (no blown-out disc on the sea). Below 4000 the picture is exactly the game's haze. Screenshots print the eye, this clearance and the weight (`haze` line). `physical` is a height-dependent integral over SkyX's air (a viewer alternative, all heights). `--haze-strength <x>` (and the Tab slider, viewer options) scales how far the haze is blended in; 1 is the game's.
- **Limits**: SSAO sees only the near
  depth slice and has no normal buffer (curved surfaces show faint banding, thin objects can halo); the auto exposure measures a
  scene without shadows, so its mean runs higher and its exposure lower than the game's in sunlit views; colour LUTs and depth of field are not implemented (the game has neither).
