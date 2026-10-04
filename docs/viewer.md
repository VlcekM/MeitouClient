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
```

A record is found by string id, else by name (CHARACTER before RACE). `--help` lists the options (`--female` /
`--male`, `--equip` repeatable, `--naked`, `--drawn`, `--seed`, `--anim name[:weight]` repeatable,
`--bind-pose`, `--no-morphs`, `--no-skin-tone`, `--info`, screenshot and camera options).

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
- **Keys** (besides the mesh viewer's camera and display keys): `Tab` / `1`–`9` select a layer, `Left` / `Right`
  change its animation, `+` / `-` its weight, `Insert` adds a layer, `Delete` removes one, `Home` binding pose.

Verified with screenshots (2026-10-04): Dust Bandit (Greenlander, helmet hides the hair, sandals win the boots
slot with chance 400, horse chopper at the hip with the handle forward), the same with a katana drawn in the
right hand, sheath at the hip and a second katana diagonally on the back while blending `walk lower` +
`walk upper sword`, Ruka (Shek woman: horn and face poses, skin tone, leather shirt as a vest layer), Beep
(Hive Worker Drone with a stick-people head texture and the rag loincloth).

Not done: body-shape sliders (formulas not transcribed), `hide parts` (part map), `muscleBlend`, blood,
faction colours, LOD, the game's random appearance for characters without a body file (the viewer takes the
likeliest choices, or weighted ones with `--seed`), which listed weapon a CHARACTER really carries.

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
`--object-distance`, `--layer-size`, `--debug 1|2`. Keys: left drag orbits the target, right drag looks
around, wheel zooms, `W A S D` fly over the ground, `Q`/`E` down/up (Shift faster), `T` textures, `N` normal
maps, `O` objects, `X` wireframe, `V` debug view (blend weights, layer weights), `[`/`]` LOD distance, `H`
prints the camera as command-line options, `P` screenshot into the temp folder.

How it works (status as in [README.md](README.md)):

- **Terrain**: `HeightWindow` reads the region's samples row by row; `TerrainMesh` makes chunks of 64 × 64
  cells with skirts and per-LOD index buffers shared by all chunks; each frame picks a LOD per chunk from its
  distance (halving detail per doubling beyond 1.5 chunk sizes) and skips chunks outside the view frustum. The
  whole world at step 8 draws in under 30 ms. Near and far planes follow the eye's height above the ground.
- **Texturing** (`TerrainTextures`, `TerrainShaders`): the biomes of every `blendinfo.dat` cell touching the
  region go into two texture arrays (one layer per distinct diffuse/normal pair, each brought to 512² from its
  nearest mip); per-biome constants go into a float texture; the blend map is sampled for the five slot weights;
  the region's piece of the overlay and colour maps is cut from their tiles. The layer model, slope scaling
  (FCS value × 0.01, **Verified** from the exe) and the overlay/colour channels follow terrain.md.
- **Objects** (`WorldObjects`, `WorldObjectRenderer`): placements become meshes with `WorldObjectLayout`, meshes
  are loaded once per file and textured with the model viewer's `MaterialResolver` (candidate preferred: the one
  naming the placed building or part), textures decode on worker threads (`WorldTextureCache`). Instances beyond
  `--object-distance` or outside the frustum are skipped; interactively at most 8 new meshes load per frame.
  TERRAIN-mode map features are drawn with the terrain shader (biome textures, no roads).
- Back-face culling is off for objects (open building meshes); distance haze is the viewer's own.

Verified with screenshots (2026-10-04, saved outside the repo): The Hub (`--town "The Hub"`: walls, gates and
towers join up, buildings upright, roads in the ground texture), zone 44.23 (TERRAIN-mode cliff blocks take
the biome textures), zone 54.44 (UV-mapped cliff features), zone 31.45 at radius 3, and the whole world from
above (biome regions as in `biomemap.png`).

Approximations and gaps: no water, wetness, shadows, sky, foliage or grass, characters, interiors,
construction states or lights; cliff normal-map channel flips are not reproduced; building parts are a seeded
random pick (the game's choice is Unknown); buildings without their own material and no FCS candidate keep the
mesh's script material or stay grey; the game's terrain LOD morphing and distant-terrain material are not
reproduced; the cell table only holds biomes of cells touching the region, so far outside the region nothing
is drawn anyway. Observed and unexplained: in the whole-world view a few 2 × 2-zone cells (north-east) show as
rectangles of a slightly different tone; the cause is **Unknown** (possibly how the viewer weights slot 4 and
normalises partial sums compared with the game's first-biome rule). Interactive mode was smoke-tested only
(starts, loads, renders; the controls were not exercised by hand).
