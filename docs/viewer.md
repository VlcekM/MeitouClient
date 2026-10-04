# Model viewer (`tools/Meitou.ModelViewer`)

A standalone viewer for the meshes of the install (FCS lookups follow the full load order, including enabled and workshop mods): OpenGL 3.3 core through Silk.NET, its own GLSL shaders
(`Shaders.cs`; Kenshi's HLSL is only read for facts), CPU-decoded textures
([formats/dds.md](formats/dds.md)), GPU skinning and skeletal animation. Status labels as in
[README.md](README.md).

## Running

```
dotnet run --project tools/Meitou.ModelViewer -- <mesh> [options]
dotnet run --project tools/Meitou.ModelViewer -- human_male --anim ninjarun
dotnet run --project tools/Meitou.ModelViewer -- bush01 --screenshot out.png --size 800x600 --yaw 35 --pitch 20
dotnet run --project tools/Meitou.ModelViewer -- mask2 --info
```

`<mesh>` is a bare file name (`.mesh` optional) or a path (absolute, relative to the install, or relative to the
working directory). The install comes from `KENSHI_PATH` / `meitou.local.json`. `--help` lists the options and
keys (orbit / pan / zoom with the mouse, `W` wireframe, `M` next material candidate, `Left`/`Right`
animations, `Space` pause, `P` screenshot into the temp folder...). `--screenshot` renders into a hidden window's 4× multisampled
framebuffer and writes a PNG; with `--anim` the camera frames the posed mesh.

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
weapon file. `meitou-viewer katana05` (rule 1 fails for a bare name) gets the same file by rule 2.

## How mesh textures are resolved

Mesh material names mostly don't name script materials, and `StaticObject` (the most common one) only has
placeholder textures ([formats/ogre-material.md](formats/ogre-material.md#mesh-materials-verified-2026-10-04)).
The game builds materials at run time from FCS records ([formats/runtime-materials.md](formats/runtime-materials.md),
decompiled). The viewer approximates that, best first (`MaterialResolver`):

1. `--texture` (and `--normal`) from the command line.
2. **FCS records whose filename fields name the mesh** (bare name, case-insensitive), each giving one texture
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
3. The submesh's **script material**, if its first pass names a texture that exists and isn't a placeholder
   (`black.dds`, `flat.dds`, `white.dds`...). Covers the exporter leftovers (`01-Default`...).
4. Otherwise untextured (flat grey).

`M` / `--material n` cycles through the candidates (e.g. the 16 weapon models of a katana).

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
  normal as `.wy` (X in alpha, Y in green). The viewer does the same. Screenshots of `human_male` show a
  correct face only with this.
- **Observed (viewer heuristic)**: a normal map whose mean blue is below 180 and whose R, G, B means agree is
  treated as swizzled (catches the character body maps; building/weapon maps have mean blue ≈ 240–255).
- **Observed (screenshots)**: weapons share a UV layout: MATERIAL_SPECS_WEAPON textures (e.g. `01_HI.dds`,
  `12_HI.dds`) map sensibly onto both `katana05` and `chopper01_bare` (blade steel, edge strip and wrapped grip
  where expected).
- **Observed (Kenshi HLSL `skin.hlsl`)**: clothing worn on a character (the `Skinned` template, which gets no
  TRANSPARENCY flag) always cuts where the normal map's alpha is below 0.6, whatever its ItemShader
  ([characters.md](characters.md#worn-clothing-observed-hlsl-skinhlsl-parameters-in-runtime-materialsmd)). The
  mesh viewer still clips on normal alpha like ground items; `--character` uses the 0.6 rule.
- **Unknown**: `leaves alpha threshold` scale. With `/255`, `ThinTree01_Leaves` with `TreesAtlas01.dds` would be
  invisible (its normal map is often empty in the records; the viewer then draws it opaque).
- Not followed: ARMOUR shown on the ground uses a temporary record built from `vest texture` / `vest normalmap` (runtime-materials.md); the viewer only follows ARMOUR's `material` references.
- Not reproduced: TERRAIN-mode features (biome textures), dust, construction scaffold, colour masks / dyes,
  metalness, the character's hair/beard overlays, skin tone, blood.

## Skinning and animation

- Weights come from bone assignments: per vertex the 4 largest, renormalised (Ogre's rule). Assignments past
  the skeleton's bone count are dropped with a warning (`whistler.mesh`: 6,917 of them).
- Skeleton choice: `--skeleton`; else for meshes used by ARMOUR / CONTAINER / ATTACHMENT / LIMB_REPLACEMENT
  the body skeleton (`male_skeleton`, or `female_skeleton` for a `... female` field), since Kenshi shares the
  body's skeleton instance with worn items by bone index ([formats/ogre-skeleton.md](formats/ogre-skeleton.md#binding-worn-meshes-to-the-character));
  else the mesh's link, else `<mesh>.skeleton` beside it. A missing skeleton leaves the mesh in its binding pose.
- Pose maths follow Kenshi's modified Ogre (ogre-skeleton.md, "Bone maths"): keyframes relative to the
  binding pose, rotations nlerped along the shortest path, scale not inherited, a child's offset scaled by
  the Y of its parent's derived scale. Skin matrix = posed derived transform × inverse binding transform.
- The mesh viewer plays one animation at a time at weight 1, without the ANIMATION record preprocessing
  ([animation.md](animation.md)), so upper/lower animations (`walk upper`, `walk lower`) move only part of the
  body. `--character` blends several (see [Characters](#characters)).

Verified with screenshots (2026-10-04): `human_male` binding pose and `ninjarun` at 0.2 s (skeleton lines on
the skinned mesh), `pack_beast` `walk`, `Iron Clad Jacket_F` on `female_skeleton` (its own link is broken).

## Conventions (Verified by screenshots)

Ogre's conventions match OpenGL's defaults: right-handed, Y up, counter-clockwise front faces. Texture rows are
uploaded as stored (top row first) and UVs used unchanged; text-free proof is the human body map (shorts on the
hips, feet at the bottom, face on the head). Nothing renders inside out with back-face culling on.
Kenshi's units: a human is about 19.5 units tall, the katana the game loads 12.8. Weapons are not scaled when
attached (their node only undoes the body node's scale); the 1.6-unit `data/meshes/katana05.mesh` is a different
file the game never picks ([Finding files](#finding-files)).

## Limitations

- One texture set per mesh from FCS; per-submesh only for script materials.
- No LOD, shadows, metalness or specular maps beyond diffuse-alpha gloss; poses only in `--character`.
  No sRGB handling, which matches Kenshi (Observed: it does no sRGB conversion either,
  [characters.md](characters.md#colour-space-observed)).
- The swizzled-normal decision is a heuristic, not what the game does (the game picks by shader template).

## Characters

`meitou-viewer --character <CHARACTER or RACE record>` assembles a character from FCS records
(`Meitou.Data.Characters.CharacterAppearance`; the rules and their evidence are in [characters.md](characters.md))
and shows it:

```
dotnet run --project tools/Meitou.ModelViewer -- --character "Dust Bandit"
dotnet run --project tools/Meitou.ModelViewer -- --character Ruka --screenshot ruka.png --size 900x1000
dotnet run --project tools/Meitou.ModelViewer -- --character Greenlander --female --equip "Samurai Boots" --equip Katana --drawn
dotnet run --project tools/Meitou.ModelViewer -- --character "Dust Bandit" --anim "walk lower" --anim "walk upper":0.8
dotnet run --project tools/Meitou.ModelViewer -- --character "Dust Bandit" --faction "Dust Bandits" --seed 3
dotnet run --project tools/Meitou.ModelViewer -- --character Ruka --shape height=1.2 --shape "Arm bulk=145;muscle=1"
dotnet run --project tools/Meitou.ModelViewer -- --character Greenlander --lod 1 --wireframe
```

A record is found by string id, else by name (CHARACTER before RACE). `--help` lists the options (`--female` /
`--male`, `--equip` repeatable, `--naked`, `--drawn`, `--seed`, `--faction`, `--anim name[:weight]` repeatable,
`--bind-pose`, `--no-morphs`, `--no-skin-tone`, `--shape name=value`, `--no-shape`, `--lod n`, `--info`,
screenshot and camera options).

What it does:

- **Body**: race mesh with its skeleton; face poses from the body file baked into the vertices; body, head,
  body/head masks, skin tone, the hair's and beard's head overlays and up to three clothing "vest" layers in one
  shader (`CharacterRenderer`), with the character shader's swizzled normals.
- **Worn items** (ARMOUR, ATTACHMENT hair/beards, CONTAINER, LIMB_REPLACEMENT): skinned with the body's skin
  matrices by bone index. Clothing textures from the item's `material` (MATERIAL_SPECS_CLOTHING, highest
  weight), the 0.6 normal-alpha cut-out of Kenshi's `Skinned` material, and a dye from ARMOUR `color` (else
  CHARACTER `color`) through the colour map. Hair meshes use the channel-packed texture and the hair colour.
- **Weapons**: attached to bones at the race's attachment points (`hands` = `Bip01 Prop2`, `hip`, `back`,
  `back2` from `attachment points`), offset as Kenshi computes it; sheathed = `mesh`, `--drawn` = `bare sword`
  in the hand plus `sheath` at the hip. Weapon textures via the mesh viewer's resolver (manufacturer models).
- **Animation blending**: every `--anim` is a layer with a weight. An ANIMATION record name (`run upper
  stealth`) or an Ogre animation name (`ninjarun`, matched to the first ANIMATION record with that `anim name`)
  gives the layer that record's track deletions (`delete below waist`...) and override bones, so `walk lower` +
  `walk upper` animate the whole body. Weights add Ogre-style: average mode scales them down only when they sum
  above 1; layers with override bones apply in a second pass that first pulls those bones toward the binding
  rotation (animation.md, ogre-skeleton.md). Layers are not synchronised and have no fades (Kenshi's
  per-layer controller is not reproduced). Without `--anim` the body file's `idle stance` plays. With a body
  file, `postures`, `neck set` and `shoulder set` are added as layers held at length × `Posture` / `Neck
  position` / `Shoulder set` ÷ 100 (animation.md, "Posture sliders"; `--no-postures` leaves them out). With the idle the four layers sum to weight 4, so average mode scales each to 0.25; that is why `--no-postures` visibly changes the idle.
- **Generated characters** (`--seed n`, `CharacterGenerator`): the character is rolled by the game's spawn rules
  ([characters.md](characters.md#generating-a-character)): gender, and without a body file a random head, hair,
  beard, hair colour, skin tone, sliders and one of the race's `morph num` faces from its editor limits; then
  clothing per slot with quality and material, backpack, crossbow and weapons with manufacturer and model (the
  model's texture is used). The roll is printed (`loadout` lines). `--faction` names the FACTION the character
  spawns in (its `hairstyles` limit the hair); the same seed always gives the same character, not the one the
  game would give.
- **Body shape** (`CharacterShape`, formulas in [animation.md](animation.md#body-shape-sliders)): on the 30-bone
  human skeletons the body file's (or rolled) sliders become Kenshi's per-bone sizes and positional sizes, with
  the skeleton's movement scale; the `Animator` applies them as Kenshi's Ogre does (derived scale = bone size ×
  own scale, child offset × positional size × the parent's Y scale; binding pose unchanged). Muscle comes from
  the CHARACTER's `stats` STATS record (strength, weapon/armour smithing; an approximation), starvation is 0,
  all limbs present, a missing slider counts as 100. `--shape name=value` overrides a slider (Kenshi units,
  or a fraction when ≤ 3: `height=0.8` = 80; names case-insensitive, spaces optional: `legsbulk=130`) or
  `muscle=`, `starve=`, `legratio=`, `missing=larm,rarm,lleg,rleg`, `hidestump=0..3`; `--no-shape` turns it off.
  `shaved` Shek characters get the cut-horn pose rule. Weapons on bones inherit the hand's bone size (Kenshi's
  behaviour there is Unknown).
- **LOD** (`CharacterLod`, [formats/ogre-mesh.md](formats/ogre-mesh.md#lod)): every part keeps its mesh's
  generated LOD levels and picks one per frame with Kenshi's distance_sphere rule (camera distance to the mesh
  file's bounds centre minus its radius, against the level distances: 200 for bodies and hair, 400 for armour);
  `--lod n` forces level n (clamped per mesh), `L` cycles auto / 0 / 1. Changes are printed (`lod` lines) and
  the title shows each part's level.
- **Keys** (besides the mesh viewer's camera and display keys): `Tab` / `1`–`9` select a layer, `Left` / `Right`
  change its animation, `+` / `-` its weight, `Insert` adds a layer, `Delete` removes one, `Home` binding pose,
  `L` LOD level.

Verified with screenshots (2026-10-04): Dust Bandit with `--seed` 1–6 (Samurai Boots, Horse Chopper on the back in
the Rusting Blade / Rusted Junk texture, or the Junkbow instead; varied skin tones; no beard), Hungry Bandit seeds
1–4 (men and women, different heads, haircuts, beards, skin; iron club at the hip). Earlier, before the spawn rules
were traced: Dust Bandit (Greenlander, helmet hides the hair, sandals win the boots
slot with chance 400, horse chopper at the hip with the handle forward), the same with a katana drawn in the
right hand, sheath at the hip and a second katana diagonally on the back while blending `walk lower` +
`walk upper sword`, Ruka (Shek woman: horn and face poses, skin tone, leather shirt as a vest layer), Beep
(Hive Worker Drone with a stick-people head texture and the rag loincloth). Body shape and LOD: Ruka with and without
`--no-shape`, Greenlander at Height 80 and 120 (no seams at shoulders or elbows), with `muscle=1`, `starve=1` and
missing limbs with and without `hidestump`, Dust Bandit at forced LOD 0 and 1 (wireframe) and switching by distance.

Not done: `hide parts` (part map, needs a per-vertex attribute; rules in characters.md), the `muscleBlend` normal
map blend (rule known), stumps for missing limbs, blood, faction colours; without `--seed` the viewer takes the likeliest choice of each roll (first head, likeliest
hair, neutral sliders).

## World mode

`meitou-viewer --world` draws a square region of the Newland world: the terrain from `fullmap.tif`, textured
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
(`--yaw`, `--pitch`, `--distance`), `--screenshot` / `--size`, `--no-textures`, `--no-objects`,
`--object-distance`, `--layer-size`, `--debug 1|2|3`, `--time <hour>` (default 13), `--no-water`,
`--view-distance` (default 450000), `--fog` (distance of full haze at ground level, default 250000) and
`--material-distance` (where the full terrain material gives way to the ground colour, default 30000 as in the
game). `--camera-at x,z` starts the camera somewhere
else than the loaded point (as if flown there), `--no-stream` keeps the detail around the start point instead of following
the camera (the behaviour before streaming), and `--fly-to x,z` with `--screenshot` flies there first at 3 times the
fast key speed and prints the frame times on the way (a streaming test). `MEITOU_STREAM_LOG=1` prints upload steps over 3 ms.

Keys: left drag orbits the target, right drag looks
around, wheel zooms, `W A S D` free fly along the view direction, `Q`/`E` world down/up (speed follows the height above ground; Shift ×4, Ctrl ×0.25), `T` textures, `N` normal
maps, `O` objects, `G` water, `,`/`.` time of day −/+ 1 hour, `X` wireframe, `V` debug view (blend weights,
layer weights, untextured shading), `[`/`]` LOD distance (× 1.25), `H` prints the camera as command-line
options, `P` screenshot into the temp folder.

```
dotnet run --project tools/Meitou.ModelViewer -- --world --town "Shark" --radius 1.5 --distance 3000 --pitch 20
dotnet run --project tools/Meitou.ModelViewer -- --world --town "Port North" --radius 1.5 --distance 3500 --pitch 22 --yaw 200
dotnet run --project tools/Meitou.ModelViewer -- --world --town "The Hub" --radius 2 --distance 9000 --pitch 10 --yaw 300
```

How it works (status as in [README.md](README.md)):

- **Terrain** (`TerrainQuadtree`, `TerrainRenderer`): CDLOD out to the horizon, as described in
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
  see-through near the camera, and it is opaque beyond 4000 units, as in the game. It reflects the sky colour
  (no reflection render target).
- **Sky** (`SkyRenderer`, `SkyClock`): the sun follows the game's formula for the hour (latitude 54, sunrise 5,
  sunset 23 from the CONSTANTS record). Sky, sun and ambient colours come from a simple model of our own, not
  SkyX scattering. The horizon colour is also the fog colour, and the fog distance grows with the eye's height.
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
  distance of the eye are loaded into two texture arrays (one layer per distinct diffuse/normal pair, each brought to
  512² with its mips by a worker thread); per-biome constants go into a float texture with one row per biome; the blend
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
    colour shows first and the material appears around the camera within a second or two (the title shows `loading N`).
- **Objects** (`WorldObjects`, `WorldObjectRenderer`): placements become meshes with `WorldObjectLayout`, built
  as the game builds them ([formats/zones.md](formats/zones.md#from-placements-to-meshes)): parts chosen with the
  game's `rand()` seeded from the position, doors added, destroyed states (`destroyed mesh`, upper floors
  removed), rotating parts at their rolled start angle, and each part's MATERIAL_SPEC from the part, the building
  or the building's town (`BuildingTowns`; `BuildingMaterial` turns it into textures). Map features (and parts
  without a chosen material) are textured by the model viewer's `MaterialResolver` (candidate preferred: the one
  naming the placed record). Meshes are loaded once per file; textures decode on worker threads
  (`WorldTextureCache`). The log line `objects` counts destroyed buildings and the foliage resource buildings,
  which the game draws as foliage rocks (not drawn here). Instances beyond
  `--object-distance` or outside the frustum are skipped; interactively at most 8 new meshes load per frame.
  TERRAIN-mode map features are drawn with the terrain shader (biome textures, no roads).
- Back-face culling is off for objects (open building meshes); distance haze is the viewer's own.

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

Approximations and gaps: no wetness, shadows, clouds, weather, scum or water reflections of the scene, foliage or grass, characters, interiors,
construction states or lights; cliff normal-map channel flips are not reproduced; building part choice follows
the game's rolls but was not compared in game, and nested choices can drift where effects or loading callbacks
roll (zones.md); picks from material collections (towns with "moor mats", 32 parts) are the viewer's own seeded
choice, as the game's are unseeded; Iron/Copper Resource rocks (foliage) are missing; doors are always closed and
turrets unaimed; the full terrain material is drawn within the material distance of the eye
(30000), the ground colour beyond it; buildings and map features are still loaded only for the start region
(`--radius`), not streamed with the camera; the overlay and colour windows hold 73728 units around the eye, so
`--material-distance` above about 36000 shows no more material than that. Known gaps of the streaming: a texture
pair that finds no free slot (more than 192 pairs needed at once; the base game has 133, so only modded installs) is
marked failed for good and stays in ground colour, instead of being retried when a slot frees; the height-window blend
band shifts at a swap (above). The straight-edged patches of other tones in the north-eastern sea of the whole-world
view are in the game's own `biomemap.png`, not a viewer fault
([formats/terrain.md](formats/terrain.md#why-some-sea-areas-in-the-north-east-have-other-tones-observed)).
Interactive mode was smoke-tested only
(starts, loads, renders; the controls were not exercised by hand).
