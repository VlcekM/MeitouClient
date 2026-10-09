# Foliage: trees, bushes, rocks and grass

Kenshi places its foliage when a zone is activated (ZoneMapContent activation), on its own, from the FCS records
and the terrain maps. Nothing about the foliage is stored in the world files except the `is foliage` resource
buildings (zones.md, "Buildings with no parts"). Rendering goes through PagedGeometry (the Ogre "Forests" library:
TreeLoader3D, BatchPage / WindBatchPage, GrassLoader / GrassLayer).

Sources:
- the exe, kenshi_x64.exe (Ghidra, functions cited by address):
  - FUN_1406d3e30: per-zone setup
  - FUN_1406d3740: mesh layer loop
  - FUN_1406d2410: one placement
  - FUN_1406cd970: grass coverage
  - FUN_1406d0340: grass layer
  - FUN_1406ce160: per-mesh record read
  - FUN_1406cc2e0 / FUN_1406cc910: orientation
  - FUN_1406d87f0 / FUN_1406cb960 / FUN_1406cb600: random numbers
  - FUN_1406cbba0 / FUN_1406cbf20 / FUN_1406cc0e0: noise
  - FUN_1407eac60 / FUN_1407eae40: zone height and slope
  - FUN_140927f10 / FUN_1409fe7a0: nearest town and its range
  - FUN_140a40f40: grass populate
  - FUN_1403eca90: settings writer
- `fcs.def`, `settings.cfg`, `data/materials/deferred/foliage.hlsl` and `foliage.material`.

The code lives in `src/Meitou.Data/World/Foliage*.cs`, with tests in `tests/Meitou.Tests/World/FoliageTests.cs`.

## Records

- **BIOMES** `foliage` → FOLIAGE_LAYER list (in list order). A biome is keyed by its `index` colour
  (RGB, the colour the blend file and `biomemap.png` use). **Verified (decompiled)**.
- **FOLIAGE_LAYER**:
  - `meshes` → FOLIAGE_MESH, where the reference's value 0 is the number placed per zone.
  - `grass` → GRASS, where value 0 is the coverage channel: 0 reads overlay R, anything else overlay G.
  - `visibility range`: FoliageVisibilityRange, 0 CLOSE, 1 MEDIUM, 2 FAR, 3 FEATURE.
  - `wind`: a wind page, see below.
  - `lod range`: × 10, stored per mesh (FUN_1406ce160). **Observed**: no instruction in the foliage system
    (0x1406c0000–0x1406e8000) or the PagedGeometry code (0x140a28000–0x140a50000) reads that slot back, so it
    probably has no effect; a reader elsewhere is not ruled out.
  - `uses foliage system`, `lod levels`, `page size` (on some layers, e.g. SageBrush: lod range 150, page size 50;
    LandTrumpets: lod range 50, page size 50, lod levels 1): **Verified (string search of kenshi_x64.exe)**: none
    of the three names occurs in the exe, while every field it reads is there as a literal (`lod range` once).
    The game never looks them up; they are editor-only. The viewer ignores them.
  - **Verified (decompiled)** for the record reads.
- **FOLIAGE_MESH**: every field in `fcs.def`, read in FUN_1406ce160.
  - Scale range: `min height`/`max height` × 2, each clamped to [0.01, 10].
  - `meshes` are child meshes that cluster around the parent.
  - `building type` makes the rock a production building: Iron/Copper Resource, mines (**Verified**).
- **GRASS**: sprite, colour map, size, density, slope, altitude, wind, and the coverage noise (`noise scale`,
  `zero cutoff`, `brightness boost`, `cap`, `blackout*`).
- **TOWN** `no-foliage range` (default 2000): meshes with `avoid towns` are rejected within it, measured to the
  nearest town that is not a nest marker (type 8). **Verified (decompiled)**.

## Random numbers

- **Verified (test against the MT19937 reference outputs; decompiled for the rest)**: one 32-bit Mersenne Twister
  for all foliage.
  - Standard initialisation and tempering. The game regenerates the state block straight after seeding, which
    gives the standard sequence (seed 5489 → 3499211612 first).
  - **Float draw** in [min, max]: `min + u / (2^32 − 1) × (max − min)`, in double precision, rounded to float.
  - **Integer draw** in [0, n]: results masked to the smallest all-ones mask ≥ n, redrawn while above n.
- **Seed** per (layer, zone), reseeded before each mesh layer:
  - The leading integer of the layer's string id (its number, as a C++ stream reads it; 0 when there is none)
    minus `(int)(h / 2^30 × 16777215 − 16777215)`.
  - `h` is the noise hash below (with the exe's multipliers) of the zone's minimum corner, truncated to integers.
  - Each layer depends only on its own seed: its placements do not depend on the layers before it. A wrong hash
    therefore moves every mesh layer of every zone (see "Noise").
  - **Verified (decompiled)**. That the sequence then reproduces the game's placements is **Unknown**: see
    "Not verifiable".

## Noise

- **Verified (disassembly of FUN_1406cbba0, and the same constants in FUN_1406d3740 and FUN_1406cc2e0)**: integer
  value noise with Kenshi's own multipliers.
  - Hash of `n = x + 57 z`: `n ^= n << 13`, then `(n (n² 60493 + 19990303) + 1376312589) & 0x7FFFFFFF`. The exe
    multiplies by 0xEC4D, adds 0x131071F, multiplies by n, subtracts 0x2DF722F3 (adds 0xD208DD0D) and clears bit
    31; 0xD208DD0D and 1376312589 (0x5208DD0D) differ only in that bit.
  - These are **not** the textbook 15731 / 789221 multipliers. This doc and the code had those until 2026-10-05,
    which gave every mesh layer a wrong seed and a wrong grass coverage and orientation noise. Found because the
    viewer drew a LandTrumpets cluster in zone 21.29 (Skinner's Roam, near the Hub) where the user's in-game
    screenshot shows none; whether the game's clusters now sit where the viewer puts them is **Unknown** (no
    saved foliage to compare with, see "Not verifiable").
  - **Observed** (10 base-game zones, regenerated overlay R against the shipped R, see "Overlay channels"): with
    the exe's multipliers 25.7 % of the non-zero pixels match exactly, against 17.0 % with the textbook ones (zone
    14.30: 73 % against 56 %; 22.32: 82 % against 61 %). That supports the constants but does not verify the
    whole coverage rule.
  - Value `1 − h / 2^30`.
  - Smoothed over the 3 × 3 neighbourhood: sides / 8, corners / 16, centre / 4.
  - Cosine interpolated, with π stored as the double 3.1415927.
- **Grass coverage noise** at world (x, z):
  - Five octaves: frequency 2^o / scale, amplitude 0.5^o, with scale = `noise scale × 72`.
  - The sum s gives `0.5 + 0.5 s`.
  - The result is zero at or below `zero cutoff` and rescaled to 0..1 above it.
  - Then multiplied by `brightness boost` and clamped to [0, `cap`].
  - With `blackout`, a second noise at (x + 50007, z + 50007) multiplies in: scale `blackout noise scale` × 72,
    cutoff `blackout zero cutoff`.

## Ground

- **Verified (decompiled)**: the placer reads a 129 × 129 float grid per zone. Samples are 36 units apart from
  the zone's minimum corner, which is every second `fullmap.tif` sample (terrain.md).
  - **Height**: bilinear, the point clamped to the grid.
  - **Slope**: the larger of |h − h(i−1, j)| and |h − h(i, j+1)| at the sample the point falls in. It is in
    height units per 36 units, not degrees (fcs.def: "Its not in degrees!").

## Overlay channels and grass coverage

- The overlay tile (`new_overlay.X.Y.png`, 1024², 36 units a pixel, terrain.md) is what the placer reads. In the
  game's memory the bytes are B, G, R, A. In the PNG's RGBA they are:
  - **R**: generated grass coverage
  - **G**: grass spots
  - **B**: dirt
  - **A**: road
  - **Verified (decompiled)** for the reads.
- Before placing, the zone's 129² of R and G are cleared. R is then regenerated from every GRASS of every biome in
  the zone, keeping the larger value at each pixel.
  - Value: noise × 255 × an altitude fade (linear over 300 units outside `min/max altitude`).
  - Dirt (B > 70) gives 0.
  - Road pixels (A > 10) are skipped.
  - With several biomes, only the pixels of each grass's own biome are written (`biomemap.png`).
  - The byte conversion wraps above 255.
  - **Verified (decompiled)**.
- **Observed** (base game, 25 sampled zones):
  - The regenerated R is non-zero on nearly the same pixels as the R the game ships (221,963 against 225,308).
  - The values agree exactly on only 12–99 % of a zone's pixels (41.6 % overall). These figures, and the variants
    below, were measured with the wrong (textbook) noise hash; with the exe's hash see "Noise" (a different set of
    10 zones: 17.0 % → 25.7 %). The variants have not been retried with it.
  - Swapping axes, pixel-centre offsets, half or double scale, clamping instead of wrapping, and no altitude
    fade did not close the gap.
  - The shipped R is probably what the editor baked. The game overwrites it at run time anyway, so the viewer
    uses its own.
  - Test: `Generated_grass_coverage_has_the_shipped_overlay_footprint`.
- A grass layer's density map is the zone's 129² of R (channel 0) or G (channel ≠ 0). **Verified (decompiled)**.
- **Zone biome list**: the blend file's slot mask over the zone's box, in slot order (terrain.md). This is
  **Observed**: that it is the game's list is assumed from the blend-file rule.

## Placing meshes

The order of work in a zone:
- Grass layers come first, then mesh layers.
- Within each, biomes and layers keep their list order.
- Each mesh layer reseeds the generator, then places each of its meshes `count` times.

**Verified (decompiled)** throughout this section.

- **Point**:
  - Two uniform draws over the zone box, X first.
  - With `limit to grass areas`, the point is redrawn up to 21 times while overlay R + G ≤ 149.
  - With several biomes, a point whose `biomemap.png` biome is not the layer's ends the current cluster.
- **Clusters** (`clustered`):
  - A cluster has `IntUpTo(max − min) + min` members and radius `rand(radius min, radius max)`.
  - The first member is the uniform point (the centre).
  - Each further member: direction `normalize(rand(−1,1) second, rand(−1,1) first)`, distance `rand(5, radius)`.
  - A slope or altitude failure ends the cluster.
- **One placement**, in this order:
  1. Slope outside [`min slope`, `max slope`]: fail.
  2. Outside the zone box: skip (no fail).
  3. Inside a town's range (`avoid towns`): rejected, but the draws below still happen.
  4. Height outside [`min altitude`, `max altitude`]: fail.
  5. `floating`: y = max(y, water height 100).
  6. Scale = `rand(min scale, max scale)`.
  7. When the two `vertical offset` values differ, offset = `rand(min, max)`; y += offset × scale.
  8. Road test: any overlay A ≠ 0 on the first row, centre, last row or side columns of the box of half-size
     `road avoidance × scale` rejects the object.
  9. Yaw = `rand(0, 360)`.
  10. The object is added unless rejected. Scale and yaw are stored quantised to 8 bits (PagedGeometry
      TreeLoader3D); the viewer keeps them unquantised.
  11. Children, even of a rejected parent:
      - count `IntUpTo(max − min) + min` of the child's cluster numbers, radius `rand(child radius min, max)`
      - each child: a direction as above, then distance `rand(0, r) + child cluster radius + parent's`
      - each child is placed by the same steps, recursively
  12. `grass spot` stamps a disc of radius `grass spot × scale` into overlay G (strength ≤ 1, × 96), which later
      "limit to grass areas" layers see.
- **Orientation** (made when a page is built):
  - `slope align` turns the up axis onto the terrain normal after the yaw.
  - Otherwise, without `keep upright`, the C runtime `rand()` is seeded with `noise × 10^6` at the position.
    Three draws give the axis (third, second, first) and the angle is noise × 360°.
  - Otherwise yaw only.
  - **Verified (decompiled)**.
- Resource rocks are ordinary placements of meshes whose `building type` is set. The game then finds or makes the
  building at the rock.

## Distances

- **Verified (decompiled; setting names from the settings writer FUN_1403eca90)**: page range =
  `{50, 100, 800, 4000}[visibility] × 10 × setting`:
  - meshes use `settings.cfg` `foliage range`
  - grass uses `grass range`; `grass density` multiplies grass counts
  - all three are 1 by default, which makes CLOSE 500, MEDIUM 1000, FAR 8000 and FEATURE 40000 units
- Grass layers and wind layers clamp the visibility to FAR at most.
- The page transition (fade band) is 10 units, or 100 for wind layers.
- Of the 257 layers the BIOMES use, 244 are MEDIUM and 13 FAR. No FEATURE layer is used.
- **Observed (decompiled)**: no impostor level was found. The non-wind tree path adds one detail level at the
  range above, and the only `data/impostors/` reference is a temp-directory call on the grass page manager.
- Distance is measured along the ground for grass: `foliage.hlsl` uses the eye's and vertex's X/Z (**Verified**).
  For mesh pages it is **Unknown**; the viewer uses X/Z too.
- At the game's settings a MEDIUM layer (the junk, ruins, bushes and most rocks) ends 1000 units from the eye with a 10-unit
  transition: the game itself shows them appearing that close (by design of its pages, **Verified** from the ranges above). The
  viewer's "Foliage draw distance x" slider at x1 is that setting; its default is x4.
- **Layers are independent of each other** except through `limit to grass areas` (**Verified**: the placement rules above, and
  test `Far_only_layout_places_exactly_the_far_instances_of_the_whole_layout`, which compares the far layers placed alone with the
  whole layout over 10 zones of the base game, instance for instance). Each mesh layer reseeds the generator from its own id and
  the zone's corner; the only thing a layer reads that other layers write is overlay R + G (the grass coverage and the grass
  spots), and only the top-level meshes of a `limit to grass areas` layer read it (children never test it). The roads (A), the
  ground, the biome map and the towns are read-only. So a layer none of whose meshes limits to grass areas places the same
  instances whether or not any other layer is placed. Of the 257 layers the BIOMES use, 5 have such a mesh; all 13 FAR layers
  stand alone (base game, Observed by listing). The viewer uses this to lay out only the FAR layers of distant zones.

## Mesh sizes

Not a game concept: the game ranges a mesh by its layer only ("Distances"). The viewer's Meitou `range` switch draws meshes by size instead
(docs/render-foliage.md), so the sizes of the meshes the biomes place were measured (**Observed**, 2026-10-06, a scratch survey over the
install's load order: every FOLIAGE_MESH of a non-grass layer of a BIOMES record, children included, 643 meshes, all of them found and
decoded). A mesh's **size** is its bounding radius (half the diagonal of the box around the mesh and its leaves mesh, at least 1; what the
renderer measures after decoding) times the larger of its record's two scale limits (`MaxScale`; some records have them swapped, e.g.
`Land Sail-Wrapped` 0.8 / 0.4).

- Spread: median 83 units; 5 % below 4, 25 % below 29, 75 % below 295, 95 % below 858; the largest are `FOLIAGE_Plant_Swamp-TwigLarger`
  (3169) and `Giant_MultiLimbTree` (2020). Unscaled radii run from 1 (skeleton parts) to 3209 (`Jungle_TREE&Branches`, placed at scale
  0.07 to 0.13), so the scale must be part of the measure.
- **Classes** (`FoliageSizes`): **small** below 40, **medium** 40 to 125, **large** from 125. 205 small, 157 medium, 281 large meshes. The
  thresholds come from the first default class ranges 800 / 2500 / 5000 (`FoliageSizes.Threshold*Range`; the defaults are 3500 / 12000 / 50000 since 2026-10-08): the largest small mesh at the end of the small range subtends the same
  angle as the largest medium one at the end of the medium range (40 / 800 = 125 / 2500); the smallest large mesh ends at 5000 at half that.
- Examples (size): small: skeleton parts (2-6), `Robotics-Junk` (6-25), small boulders (`Bouldersmall*`, 3-8), the `SPARSER` junk pieces
  (9-27), `Bleached_Skull01` (12), cacti trumpets (18-22), `Thorny Plant Singles` (13), motor parts (33-39); medium: `TechJunk04` (64),
  `TechRustyJunk_05` (59), boulders (`FOLIAGE_Boulder_GREY-01` 86, `DUNE_Boulder06` 44), `SageBrush` (77), `RuinBlocks` (52-53), `CacTreeTu`
  (47-82), most bone clusters (74-110); large: trees (`Thin Tree [Wide]` 145, `CraggyTree` 207, `Cascade Tree01` 221, `Foliage_CYPRUS-TYPE`
  429, `BushTree01` 626), ruins (`HugeRuinWall` 484-485, `Crumble-Building_Corner01` 541, `RUIN-RefineryHead` 215-551), wrecks
  (`JunkSat01` 355), rock stacks and hoodoos (`Foliage_1K_RockStack` 198-226, `FlatTop_Hoodoo` 237-348, `Rock01 Foliage-Rockstack` 937-960),
  resource rocks (160-210), the FAR formations (`Barkworm_Pillar` 569-1895, `Canyon_NewBlockside` 333-560).
- Near the large threshold (where the choice is a judgement): medium `Baobabesque Tree` and `FruitBall_Tree` (121), `SpindleTree05` (112),
  `PalmType OASIS` (106), `Rod-Tree` (89-91); large swamp ferns (126-151), `BigGrassClump` (136), `TreeFall01` (134), `Skin_Cliff_Ridge`
  (148-151). Test: `Base_game_meshes_fall_into_the_expected_classes`.
- **Trees take the normal billboard distance** (2026-10-08, `FoliageSizes.LargeBillboard`, the owner's request: the conifers were billboards of the large class's distance; the same day first done by making them medium, which also cut their draw range to 12000, then changed to this).
  **Observed** (scratch survey of the 643 catalog meshes, the folder of each mesh file against its size class): the game keeps its meshes in
  folders by kind, and of the 283 large meshes `Assets/Rocks` holds 131 (rocks, boulders, pillars, hoodoos, rock stacks, cliffs, resource rocks),
  `Assets/Things` 49 (ruins, wrecks, junk, bones, scaffolds, towers), `Assets/Buildings` 14 (houses, walls) and `Assets/Plants` plus `foliage/Trees` 89
  (trees, palms, ferns, grass clumps, spores). **Rule**: a large mesh in `Assets/Plants` or `foliage/Trees` (`FoliageSizes.IsVegetation`, from the record's
  mesh path) stays in the large size class (drawn to the large range, 50000) but becomes a billboard at the normal impostor distance (4000), not the
  large one (12000), unless its size is 1000 or more (`GiantFrom`: such a plant is a landmark). The size classes themselves are unchanged. **Unknown** for mods: a mesh outside those folders keeps the size rule, so a mod's trees in
  their own folder keep the large impostor distance.
  Other signals were checked and **do not** separate trees from the rest: the material mode (FOLIAGE alpha cut, mode 4, covers 44 large meshes but
  also wreck pieces, cables, scaffolds and `Vast_Cluster_Piece*`, and misses the dead pines, `CraggyTree` and `Roaming_Tree*`, which are UV mapped),
  a leaves mesh (only 33), the layer's visibility (`Deep-Fir` is a plain MEDIUM layer like most rocks) and wind flag (30 large meshes with palms,
  wrecks and towers), the surface share (sparse trees 0.01 to 0.04 sit among ruins at 0.08 to 0.12), the box shape (the dead pines are 6.5 times taller
  than wide but `Deep-Fir` is 1.4, and a slenderness rule moved a hoodoo and a ruin tower), the record's keep-upright / slope-align / wind fields. The
  folder is the game's own classification, not a name list.
  **Large meshes on the normal billboard distance** (85 meshes, **Observed**, none is a rock, ruin, building or cliff): the conifers `FOLIAGE_Plant_Deep-Fir 01` (341),
  `Foliage_PINE_SCRAGGY01` (217), `Thin Craggy Tree` (259), `Foliage_CYPRUS-TYPE` and its two variants (429, 258, 258), `FOLIAGE_DeadPineType` and `-red`
  (133, 166); other trees `Baobabesque Tree`, `BigTree01 [Lush]`, `BigTree02 [Lush]`, `BushTree01`, `CanyonLandCrater_Tree`, `Cascade Tree01` and
  `- Higher`, `CraggyTree` and `Feather Leaves`, `FanTree LightBlue`, `Foliage_GungeTree`, `FOLIAGE_Plant_FineTree01` and `01A`, `FullTree` and
  `CLiff Type`, `Jungle_TREE&Branches`, `Roaming_Tree01-03`, `SKulTree` and `02`, `SpindleTree01-04`, `TerrainTree`, `Thin Tree [Wide]` (four variants),
  `TreeFall01/03/04/05`, `Yucka01` and `OASIS`; palms and plants `FOLIAGE_Plant_BananaStyle01-03-BLUE`, `FOLIAGE_Plant_FanPalm01-06-RED`,
  `FOLIAGE_Plant_HorsetailType` and `Lower`, `FOLIAGE_Plant_Monster-Style01` and `-LIGHT`, `FOLIAGE_Plant_Swamp-beard_Frame`, the swamp ferns
  (`FOLIAGE_Swamp Fern-Stalked` x3, `FOLIAGE_Swamp Ferngrey green`, `Foliage_Swamp-Tall_Leafy`), `HydraPlant01`, `LeafyPlant_`, `SageBrush_DESERT-OASIS`,
  `Spore01`, `Spore02` and `[Blister]`, `Swamp_Head_Clump`, `TallSpikeyCactus`, `Urchin_Like`, `YuccaSpiderBush`; the grass clumps `BigGrassClump` and
  `Pale_BigGrass01-11`. **Kept large** (size 1000 and up): `Giant_MultiLimbTree` (2020), `Giant_BigWood` (1801), `Jungle_LargeScale_plant` (1339),
  `FOLIAGE_Plant_Swamp-TwigLarger` (3169). Test: `Base_game_meshes_fall_into_the_expected_classes` (conifers medium; `Barkworm_Pillar01`,
  `Crumble-Building_Corner01`, `ResourceRock-IRON01`, a hoodoo, a rock stack and `Giant_MultiLimbTree` large), `Vegetation_is_told_by_the_games_asset_folder`.
- The FAR layers (13 of the 257 the biomes use) are mostly large formations: `Barkworm_Pillars`, `BlackRocks`, `Canyon_NewBlocksides`,
  `Rusty Land Extrusions`, `MoltenCliffStrings`, `Ejecta-Boulder01`, `Foliage_1K_RockStacks KenshiBowl`. Small and medium meshes in them are
  children (`SageBrush` and `Cactus_Type03` under `BlackRocks`). The trees are MEDIUM layers.

## Materials

**Verified (decompiled)**; runtime-materials.md has the builder and its flags.

- A mesh's material follows `material type` (MapFeatureMode) through the map-feature builder:
  - **FOLIAGE (4)**: TRANSPARENCY | DOUBLESIDED, cut where the **normal map's alpha** < `alpha threshold` / 255.
  - **Leaves mesh**: `leaves texture` and `leaves normal`, transparent and double-sided, cut at
    `leaves alpha threshold` / 255. This settles the scale left Unknown in viewer.md.
  - **TERRAIN**: the biome textures of one biome, see [TERRAIN-mode meshes](#terrain-mode-meshes).
  - Other modes get DUST, tinted with the biome's `ground colour`.
- Wind layers give their meshes `windFactorX/Z = wind factor × 0.25`. PagedGeometry's wind vertex program text
  is in the exe, but Kenshi's deferred `objects.hlsl` has no wind input. Whether trees sway in game is
  **Unknown**; the viewer does not sway them.

### TERRAIN-mode meshes

Most big rocks and rock formations (and `features.dat` map features with `texture mode` TERRAIN) are textured
like the terrain, not with their own textures. Sources: the shipped `data/materials/deferred/mapfeature.material`,
`mapfeature.hlsl` and `terrainfp4.hlsl` (read for facts), and `kenshi_x64.exe` (Ghidra: the map-feature builder
FUN_140843920, its TERRAIN branch, and the functions it calls; decompiled output kept outside the repo).

- **Material** (**Verified**, material script): `Feature_Terrain_DX11`. Vertex program `feature_vs`
  (`mapfeature.hlsl`), fragment program `mapfeature_fs` from `terrainfp4.hlsl` with the defines `NO_ROADS` and
  `DX11`. The pass culls back faces (`cull_hardware clockwise`, Ogre's default) and has the units `diffuseMaps`,
  `normalMaps`, `overlayMap`, `colourMap` and the interior clip mask.
- **One biome per material** (**Verified (decompiled)**). The TERRAIN branch makes a one-entry biome list and
  hands it, with the template name `Feature_Terrain_DX11`, to the terrain material builder FUN_140a14c90 (the
  one whose parameters terrain.md lists). The biome comes from FUN_140a0ac00 → FUN_140a09630: the world point's
  X and Z are mapped to a pixel of the biome map (`biomemap.png`; nearest pixel, clamped at the edges), its RGB is
  looked up among the BIOMES `index` colours, and a record named `EMPTY` is used when none matches. The branch
  reads none of the mesh record's own texture fields (`texture map`, `normal map`, tiling): in TERRAIN mode they
  are ignored. No blend map is used, so a rock on a biome border is textured entirely with the biome of that
  one point.
- **Which point**: the builder takes a position argument. For a foliage instance or a map feature it is
  presumably the object's own position, but the callers were not examined: **Unknown** (could also be per page).
  The viewer uses each instance's origin.
- **Overlay and colour maps** (**Observed (decompiled)**): the builder also gets the overlay-map object of the
  point's zone (FUN_140a16c20: the zone indices times a constant, floored, then a lookup); the constant was not
  read, presumably 1/8, which makes it the zone's 8 × 8-zone `overlaymaps` tile. The material name is the template
  name plus the biome names plus that map's name, so materials are shared per biome and tile.
- **Shader** (**Verified**, reading `mapfeature.hlsl` / `terrainfp4.hlsl`; the layer model is terrain.md's):
  - The normal is the mesh's vertex normal through the world matrix, normalised. No normal map of the mesh's own.
  - `slope = min(1, 1 − normal.y)`. The terrain's own shader does not clamp; for meshes it matters, because
    faces that overhang (normal pointing down) would otherwise fall out of the cliff layer's range (its
    `slope max` is usually 1) and show the base layer.
  - The cliff projection weights are computed per vertex like the terrain's, except that a vertex whose normal has
    y > 0.9 takes the (z, height) projection alone (weights (1, 0)); the terrain uses y > 0.995 → (0.5, 0.5).
  - Roads are off (overlay alpha treated as 0). Wetness, the distance fade to the ground colour and the brightness
    fix are as on the terrain.
- **Meshes** (**Observed**, a probe over the meshes of the foliage layers of Skinner's Roam, `FF6400`): the
  TERRAIN-mode rock meshes have no LOD levels, and all but the small `Boulder01` (hard edges on half its corners)
  have smooth normals (no position carries two normals more than 20° apart; the stored normals are within 3–13° on
  average of area-weighted face normals). The big formations are
  low-poly: `Barkworm_Pillar01`–`03` have about 3000 triangles for a bounding radius of about 4700 units, so their
  detail is all in the biome textures, which are 2048² (e.g. `Mesa2ROCKLoop_DIF.dds`, the Skinner's Roam cliff
  layer at tiling 7, one repeat per 714 units).
- Example (**Observed**, viewer against the user's game screenshots in zone 20.28 / 20.29): Skinner's Roam's cliff
  layer has `slope min/fade/max` 0.16 / 0.11 / 1.0 and its base layer is a white gravel (`WadiGravel-WHITE`).
  Without the clamp, every overhanging face of the formations showed that gravel as large pale patches, cut
  along the low-poly facets; with it the rocks are cliff texture throughout, as in the game.

## Grass blades

- **Verified (decompiled FUN_140a40f40)**: a page gets `density × 0.005 × area × grass density` candidates.
  - Each candidate has a uniform point in the page.
  - It is kept when the slope there ≤ `max slope`, a random number is below the density map (0..1), and (when
    either altitude limit is non-zero) the height is inside them. A zero limit is unbounded.
  - A kept blade gets one random scale used for both width and height, and a random yaw (0..2π).
  - The page layout and the random numbers depend on the camera, so no exact blade layout exists to reproduce.
- **Quad size**: width from `min width × 9` to `max width × 17`, height likewise (**Verified (decompiled)**).
  `cross quads` draws two quads at right angles.
- **Colour map**: stretched over the zone's bounds and multiplied in (**Observed**).
- **Shader (`foliage.hlsl` grass_vs / grass_fs, Verified by reading)**:
  - The top vertices (v = 0) move by `direction × sin(time + x × frequency)`.
  - The blade sinks into the ground between 80 % and 100 % of `fadeRange × 20` from the eye.
  - The cut-out is diffuse alpha < 0.6 (`foliage.material` threshold).
  - Albedo = sprite × vertex colour. The normal is straight up and gloss 0.
- Kenshi's values for the shader parameters (**Observed (decompiled)**): sway magnitude `wind factor × 3`,
  speed 0.3, frequency 2.
- **Unknown**: the `time` rate, and what Kenshi passes as `fadeRange`. The viewer advances by speed × π a second
  (PagedGeometry's rule) and sinks blades over the last fifth of the layer range.

## Not verifiable

- The 1,335 stored Iron/Copper Resource buildings sit in 10 zones, in tight groups of 2–5 within about 80 units.
  The current records cluster children 450+ units out, and give 5–23 rocks a zone where the store has 174–470.
  The stored placements therefore come from older data and cannot check the placement sequence; there are no
  saves to compare with.
- Treat the mesh placement as the decompiled rules, **not** as compared against the game.
- One in-game comparison point (Observed, user screenshot 2026-10-04, near world (-50882, -11613), zone 20.29, south of
  The Hub): the game shows the crashed aircraft wreck JunkSat01 (`56738-Newwworld.mod`) with JunkBall01 (`46967-Newwworld.mod`)
  a few hundred units behind it, both from the FOLIAGE_LAYER `JunkBalls` (one attempt each per zone, MEDIUM range 1000),
  in front of a big rock arch (also foliage, the Canyonland big boulders). The viewer's earlier placement put the nearest
  JunkBall01 1,963 units and the nearest JunkSat01 2,561 units from that point, in opposite directions. The camera position of
  the game shot is not known exactly, so this is not a test yet.
  Those distances were with the textbook noise hash. With the exe's hash ("Noise") the nearest JunkSat01 is in zone 21.29
  at (-50283, -12040), 736 units away, with a JunkBall01 at (-49630, -11199) beyond it in the same direction (1,319
  units), and a big boulder (FOLIAGE_Boulderbig08) at 975 units: a viewer shot from (-50583, -11613) now shows the
  wreck in front of the arch as in the game shot (**Observed**, agreement in layout only, not measured).


## Occlusion culling in the viewer (Meitou, 2026-10-08)

Meitou's own cull (the game has none for foliage); the picture does not change, `--no-occlusion-cull` turns it off. Foliage instances (meshes, TERRAIN-mode
rocks, impostors) that the previous frame's depth hides are left out of the main camera's colour views by the GPU cull kernel; the shadow cascades and the
water reflection are not touched (a hidden caster still shadows what is seen).

**The pyramid** (`HizPyramid`, built at the end of `PostProcess.End`, nothing else reads it). One compute dispatch per level over the scene's depth: level 0
takes 4 x 4 pixels a texel, each next level 2 x 2 texels, to 1 x 1 (10 levels at 1280 x 720). A texel holds the **least and the greatest view distance** (z along the
view axis, rebuilt from the near slice's depth, else the far slice's, as the velocity pass does; a cleared pixel is 1e9) in a `vec2` of a storage buffer (levels
concatenated). It is the previous frame's: the cull of frame N reads what frame N-1 left, with that frame's eye and view axes
(`OcclusionView`, in the cull's `ViewData` as `hz` / `hzOff`). Not used (the view's `mode.z` 0) on the first frame, when the last build is not the frame just
before, when the eye has moved over 5000 units (the `RunUpscale` reset rule), for the shadow and reflection views, and with the CPU cull or the verify mode.

**The rule** (`HizOccluded` in `FoliageShaders.CullCompute`, after the range, frustum, fade and fog tests; **Verified** by `OcclusionCullTests`, which writes it out on
the CPU, draws random scenes of spheres and a ground plane with a sub-pixel jitter, and checks that every ray from the drawing eye and from eyes up to the step away
to points of a sphere the rule calls hidden hits the scene first; with the parallax term set to 0 the test fails, so it does bite). The instance's bounding sphere is
taken into the previous view; its nearest distance is `zn = z - r`. Skipped (drawn) when `zn < 40` or the rectangle leaves the picture. The pixel rectangle of the
sphere's view-space box, grown by 1 pixel for the depth's jitter, selects the pyramid level where it spans at most 2 x 2 texels; the greatest distance in those texels
plus a margin (1 % of `zn` and 3 units, for terrain LOD changes and the depth's precision) must be less than `zn`. That alone is right for a still camera only. For an
eye that has moved by `delta` since the depth was drawn the rectangle is also grown by the parallax: a ray from the new eye to a point of the sphere crosses the old
picture within `delta * f / z` pixels of that point at the depth `z` where it passes a surface (`f` = pixels per unit of tangent), so it needs `delta * f / z_least`
with `z_least` the nearest surface in the grown rectangle; three rounds of growing, else drawn. Argument that this is conservative: in the old picture the grown rectangle
holds only surfaces nearer than the sphere and at least `z_least` away; a ray from the new eye enters the rectangle (in front of all of them, because at the
depth where its offset is the padding it is nearer than `z_least`) and ends at the sphere (behind all of them), so it crosses a surface or passes a nearer one's edge
and is stopped. A pure turn needs no growing (a rotation about the eye reprojects exactly). What it does not cover: surfaces that moved (characters, swaying
foliage: a character that has just walked away from an instance that lay wholly behind it can leave that instance out for one frame), and terrain or objects that
changed detail by more than the margin.

**Cost and effect** (**Observed**, 2026-10-08, RTX 4070 shared with other viewers, 1280 x 960 at DLSS, Shark swamp rain `--radius 1 --distance 3000 --pitch 10 --yaw 300 --time 12`,
`MEITOU_OCC_ALT=1 MEITOU_PASS_STATS=1 --fly-benchmark 800 --fly-speed 0`, which runs the cull on even frames only and names those frames' stages `+occ`, so both
come from one run under the same load): the foliage mesh draws take 0.89 ms without it and 0.66 ms with it, the TERRAIN-mode rocks 0.36 / 0.36 ms (they are near the
eye, inside the 40-unit skip or in plain view), the cull kernel's own time per main view 48.4 / 47.7 us, and the pyramid costs 0.04 ms a frame (`post cost ... hiz`; two dispatches).
Net about 0.2 ms of an 8 ms frame. 22213 foliage instances are left out of the two main colour views (the stat line `occlusion`, F11 and `--screenshot`; a frame ring late).
The Hub in clear weather (`--radius 1 --distance 3000 --pitch 15 --yaw 300`): meshes 0.30 / 0.22 ms, rocks 0.135 / 0.121, 9521 instances left out; the pyramid costs about what it
saves there. Image: the swamp view with and without it is byte-identical (`cmp`; two runs of either are too).

**Turning and flying** (**Observed**, see the end of this section): pixel differences against `--no-occlusion-cull`.

**A depth prepass for the foliage meshes does not pay** (**Observed**, 2026-10-08, same view, tried and removed): alpha-tested leaves disable early depth rejection, so the
colour shader (lighting, shadow filter, haze) could run on hidden fragments. A prepass with the cut-out and dither tests only (the colour shader with its lighting cut away, so the same
fragments are left out; depth bias 4 + slope 1, the colour pass then testing less-or-equal) and the images were identical (0 differing pixels), but the foliage mesh draws
took 0.89 ms against 0.78 ms with it (same run, alternating frames: `fol meshes+pre`) at `--distance 3000`, and 0.76 against 0.61 ms at `--distance 400 --pitch 4`. With the colour
shader replaced by the prepass's trivial one the mesh draws did not get cheaper either (0.96 ms against 0.78-0.90), so they are bound by vertices and triangle setup, not fragments.
The expensive part of the foliage is the TERRAIN-mode rocks (about 2.0-2.2 million triangles a frame in the colour views, 0.4-1.2 ms, the plant `FOLIAGE_Plant_Swamp-TwigLarger` at about
5000 triangles an instance, 437 of them); fewer triangles would pay, not a prepass: see "Generated mesh levels" below.

**Verification of motion** (**Verified**, 2026-10-08): the Shark swamp view with `--fly-benchmark 170 --fly-radius 300 --fly-speed 6` (flying) and with `MEITOU_FLY_TURN=0.02` (turning
0.02 rad a frame about the eye), screenshots at frames 40, 80, 120 and 160, with and without `--no-occlusion-cull`: all eight pairs are byte-identical (max difference 0, as between two runs
without it), with 20 000 to 25 000 instances left out of the main view. Known limit (**Unknown**, not seen): an occluder that moves away (a character) can leave an instance out for one frame,
as the depth is last frame's; the margin covers a camera moving up to 5000 units a frame, a larger step skips the cull for that frame.

## Layout cache in the viewer (Meitou, 2026-10-09)

The layout above is a pure function of the game data, so the viewer keeps each laid-out zone on disk (`FoliageLayoutCache`, `src/Meitou.Data/World/FoliageLayoutCache.cs`) and a later
start reads it back instead of placing it again. In the Shark swamp view (`--view swamp`, 414 zones, 1,047,128 meshes, 208 grass pages) the foliage stage went from 25.2 s to 1.4 s.

- **What is stored**: per zone and kind (`w` whole, `f` far layers only: they are separate entries, the far kind is not cut from the whole one) the placed instances (mesh and layer by record
  id, position, scale, yaw, orientation: 40 bytes each, bulk-read), the grass patches (layer, which of the layer's grass types, channel, bounds, and the 129² coverage map, stored once
  when patches share it), the zone's flags (`Complete`, `Resources`), the 129² ground heights the grass blades are built from, and the milliseconds the placement took (for the saving
  reported at start-up). Meshes, layers and grass types are looked up by id in the catalog of the start that reads the file; an id the catalog does not have makes the entry unusable.
  The zone's overlay tile, blend file, biome map and heightmap are not opened at all for a hit.
- **Where**: `%LOCALAPPDATA%\Meitou\foliage\<key>\z<X>.<Y>.<w|f>.mfl` (`MEITOU_FOLIAGE_CACHE` overrides the folder; never in the repository). 89 MB for the 414 zones above (about 215 KB a zone).
  Capped at 2 GB (`MEITOU_FOLIAGE_CACHE_MB`, 0 = no cap): over it, the folders of other keys go whole, least recently used first, then this key's oldest files, down to 90%; keys unused
  for 30 days and temporary files an hour old are deleted. A pass runs once per start (in the background, on the first hit or write).
- **Key** (SHA-256, 16 hex digits in the folder name, all 32 bytes echoed in each file): the format version; a hash of the SOURCE of Meitou.Data and Meitou.Core (every `.cs` file and the two
  project files, line endings normalised, sorted by path relative to `src`; `LayoutCodeHash.targets` computes it at every build into `obj/.../LayoutCodeHash.g.cs`, 126 files). Meitou.Data references only
  Meitou.Core, so that is everything the layout runs on (**Verified**: `Meitou.Data.csproj` has the one project reference); any change to the layout code, the random numbers or the readers it uses makes a
  new key, with no constant to remember to bump, and an edit anywhere else (the renderer, the viewer, the tests) does not. It replaced the assemblies' module version ids (2026-10-09), which differ
  between worktree paths and with any rebuild of the assembly, so every new worktree started cold (**Observed**: 0 hits, 414 misses, 46 s for the swamp start). **Verified** (2026-10-09): the same commit built in a
  second directory gives the same hash; an edit to a Meitou.Data file changes it; an edit in Meitou.Rendering, or a Meitou.Data file saved with CRLF line endings, does not; after a rebuild with a Rendering edit the
  swamp start had 414 hits (`FoliageLayoutCacheTests`: the hash equals the same recipe run on the sources). Not covered, as before: the compiler, the .NET runtime and the StbImageSharp package version beyond what
  `Meitou.Data.csproj` states (a runtime update that changed floating-point results would not change the key; **Unknown** whether one ever has); every field of every record reachable from the
  biomes' foliage lists (layers, meshes with children, grass types, building type ids); the towns (position and `no-foliage range`); size and write time of `fullmap.tif`, `blendinfo.dat`,
  `biomemap.png` and every `new_overlay.*.png`. The game files are keyed by size and write time, not content. Not in the key, because the layout does not read them (**Verified** by
  reading `FoliageLayout.Place`: its inputs are the zone, ground, overlay, biomes, biome map and towns): foliage range, grass range, grass density, the Faithful / Meitou switches. They
  decide which zones get which kind and how many blades a page makes, not what the layout holds.
- **Safety**: a file is written under a temporary name and moved into place, so a killed viewer leaves no half file. A file carries magic, format version, key, zone, kind, counts and a
  64-bit checksum, and is read whole; anything that fails any check (truncated, flipped byte, another key, zone or kind, an unknown id) counts as a miss and the zone is placed again
  and the entry rewritten. The checksum is for damage, not tampering.
- **Switch**: `--no-load-cache` (or `MEITOU_NO_LOAD_CACHE=1`) neither reads nor writes it. The start-up log has a line `foliage   layout cache N hits, M misses ...; saved about X s of
  worker time` (the stored placement times of the hits less the time to load them) and a `busy until` line: when each kind of work (layouts, grass pages, mesh decode and upload, textures,
  impostors) was last still going during the foliage settle.
- **Pictures**: **Verified** (2026-10-09): `--view swamp --screenshot` is byte-identical (`cmp`) before the change, on the cold start that fills the cache, and on the warm start that reads it.
  The layout is independent of the order zones are placed in (three workers place them in a different order every start): **Observed**, same comparison.
- Tests: `FoliageLayoutCacheTests` (round trip, key changes, truncated and damaged files, other key / zone / kind, `--no-load-cache`, upkeep; one `[Slow]` test with real zones of the game).

## Generated mesh levels in the viewer (Meitou, 2026-10-08)

The game ships no LOD levels for foliage meshes (**Observed**, the probe above for the TERRAIN-mode rocks; `FoliageMesh` has no LOD fields), so Meitou's own are a deviation from the
game, behind the `lod` switch (`--faithful lod` draws every instance at full detail exactly as before; the Tab panel has its checkbox, there is no F key: F1 to F9 are taken). They
are made for **TERRAIN-mode meshes with at least 500 triangles** (`FoliageLodBuilder.MinTriangles`; below that, low-poly cliffs showed shading differences) (`MeshAsset.Terrain`, drawn through the terrain's mesh path). Ordinary foliage meshes are not touched
(see "Not done" below).

**Making them** (`FoliageLodBuilder`, `FoliageRenderer.Lod.cs`). When a mesh is resident, one job per mesh file on the streaming workers (`BackgroundWork`, at most 3 at a time, below normal
priority) reads the file, decodes it again and either finds its levels in the disk cache or builds them. `MeshSimplifier.Chain` (the characters' quadric edge collapse; output unchanged
for them) reduces each part to 50, 25 and 10 % of its triangles (floor 48 per part; parts under 96 triangles are left alone). A level is kept when it has at most 80 % of the triangles
of the one before and a deviation (below) of at most 25 % of the mesh's radius and a shading angle of at most 0.6 rad. All levels index the part's own vertex buffer (the collapse only re-points corners at vertices the part
already has), so a part's levels 1 and up are one extra index buffer (`GpuPart` is untouched; `RockLod` holds them and is dropped with the mesh). Until the job is done and uploaded the
mesh is drawn at full detail; the render thread only does the upload (one step of the foliage upload queue). TERRAIN-mode meshes are textured by biome projection from position and
normal, so UV seams do not matter to them, and their normals are smooth (the probe above): a collapsed corner takes the vertex it lands on, normal included.

**Deviation** (`FoliageLodBuilder.Deviation`): the largest distance, both ways, between the original surface and the level's: 1500 sampled points of the original (its vertices and triangle
centres) to the level's triangles, and 1500 of the level (centres and edge midpoints) to the original's, by exact point-triangle distance with a box rejection. A thin part the level loses
(a twig, a sheet) shows as a large distance from the first set, a bridge over a gap from the second. It is kept per level in mesh units, the maximum over the parts, never decreasing with the
level, and divided by the mesh's radius (the radius `FoliageCull.FillSpheres` uses) at upload. Because it scales with the instance (an instance's radius is the mesh's times its scale),
a level's deviation on screen is `relative × sphere.w × pixels per radian / distance`.

**Choosing a level** (kernel 1 of the GPU cull, `LodSelected` in `FoliageShaders.CullCompute`, chunks with `FoliageCullChunk.Lod`). A group's level `k` is its own batch: the same chunks
(instances) as level 0, each carrying the level's relative deviation and the next coarser level's (`LodError`, `LodNextError`; infinite for the last). A view gives the kernel its
tolerance and scale (`FoliageLodView`, the sixteen bytes after the view data): an instance belongs to the coarsest level whose deviation is at most the tolerance,
`relative_k × sphere.w × pixelsPerRadian / (|centre − eye| − sphere.w) ≤ 4` pixels of the render (the same units as the terrain's pixel error; `MEITOU_LOD_PIXELS`), and in a shadow
cascade `relative_k × sphere.w / texel ≤ 2` texels (`MEITOU_LOD_TEXELS`; the cascade's texel is passed by `WorldFrame.DrawShadows`, `DrawDepth(texel:)`). Exactly one level takes an
instance (the errors never decrease), so no instance is drawn twice or lost; a level that is not selectable has no instance. The test is made before the fog and occlusion counts, so
those count each instance once. The scan kernel's draw record gained a first index (`FoliageCullDraw.FirstIndex`, the level's place in the part's level buffer), the rest of the cull is
unchanged; `PrepareRocks` makes one indirect draw per (part, level), `TerrainRenderer.DrawMeshesIndirect` draws them like any rock. A batch of a level that no instance of the view's groups
can pick (from each group's box, least and greatest radius, and the level's thresholds: `LevelNeeded`) is not drawn at all: without that, the extra empty indirect draws made the Hub's
rocks slower than without levels. The levels in a frame's work list are fixed when it is built (`MeshAsset.WorkLod`), so a level uploaded between the shadow cascades and the colour view
cannot make instances vanish from one of them. Not with the CPU cull (`MEITOU_GPU_CULL=0`) or `MEITOU_GPU_CULL_VERIFY=1` (they draw full detail); a cascade without a known texel draws full detail.

**Disk cache** (`FoliageLodCache`): `%LOCALAPPDATA%\Meitou\lods\<mesh>_<key>_v2.mlod` (`MEITOU_LOD_CACHE` overrides; never in the repository, `*.mlod` is git-ignored), the key the
SHA-256 of the mesh file's bytes (24 hex digits) and `FoliageLodBuilder.Version` in the name. The file holds the deviations, the triangle counts and the level index lists (16 bit when the
part's indices fit; a mesh with no level is a file too, so it is not worked out again). Written to a temporary name and moved into place. It is never trimmed (the files are small).
`MEITOU_LOD_LOG=1` prints one line per mesh file: its levels' triangles and deviations. `MEITOU_LOD_ALT=1` draws levels on even frames only (stage names of those frames end in `+lod`) for
timing both in one run, as `MEITOU_OCC_ALT`; the F11 and `--fly-benchmark` foliage line reports the work (built, cached, written).

**Shading term.** The deviation also includes the 97th-percentile angle between the level's interpolated normal and the original's at the level's triangle centres; a level's relative error is
`max(geometric / radius, 0.02 × angle in radians)` (`MEITOU_LOD_NORMAL`). Without it the Hub's low-poly cliffs differed in shading (mean 0.277/255, max 175); with it and the 500-triangle
floor, mean 0.127, max 163 (**Observed**, Hub radius 2). Weak point: the angle is not weighted by triangle area.

**Measured** (**Observed**, radius 2, `--distance 3000`, DLSS, 600-frame `--fly-benchmark` standing still; the levels are built before the screenshot).

| | Faithful | `lod` |
|---|---|---|
| Swamp, rocks colour triangles per frame | 2177 k | 1139 k |
| Swamp, rocks shadow triangles | 601 k | 530 k |
| Swamp, rocks colour GPU ms (`fol rocks`) | 0.91 | 0.48 |
| Swamp, image diff vs Faithful | | mean 0.0025/255, max 3 |
| Hub, rocks colour triangles | 226 k | 160 k |
| Hub, rocks shadow triangles | 199 k | 162 k |
| Hub, image diff vs Faithful | | mean 0.127/255, 0.27 % of pixels over 12, max 163 |

Shadow cascades save little: ordinary meshes are not levelled and their shadow draws are fragment-bound. The `lod` runs' frame-time percentiles are worse only because of the worker
threads' build and a shared machine; compare the GPU stage times. Cold build: 46 meshes built in 4.0 s of worker time, 9 without a level; the cache holds 46 files, 377 KB. Warm: 34 files
read in 82 ms. Faithful is byte-identical to the build without this work (swamp and Hub, radius 1).

**Thin meshes.** No separate rule: a sheet or twig that a level would lose has a large deviation and keeps full detail (the nine meshes without a level, e.g. the sheet-like
`Metal_Tower_Melted-Piece01`/`02`). No impostors for meshes (**Unknown** whether they would pay).

**Not done.** Ordinary (non-TERRAIN) foliage meshes have no levels: their UVs and alpha cutouts need a UV-aware simplifier, and their shadows (0.6 ms for 755 k triangles in the swamp)
are fragment-bound (**Observed**). Tolerances (4 px, 2 texels, 0.02) were tuned on two views and a short fly-through only; popping is bounded by the tolerance but not measured
(**Unknown**). `MEITOU_LOD_ALT` skews the cascade counts (cascades 1 to 3 draw on one frame parity).

## How the viewer draws it

See docs/render-foliage.md.
