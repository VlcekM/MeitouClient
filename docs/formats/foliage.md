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

## How the viewer draws it

See docs/viewer.md, "Foliage".
