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
  - `lod range`: × 10, stored per mesh. The renderer use of this value is **Unknown**.
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
  - `h` is the noise hash below of the zone's minimum corner, truncated to integers.
  - **Verified (decompiled)**. That the sequence then reproduces the game's placements is **Unknown**: see
    "Not verifiable".

## Noise

- **Verified (decompiled)**: the classic integer value noise.
  - Hash of `n = x + 57 z`: `n ^= n << 13`, then `(n (n² 15731 + 789221) + 1376312589) & 0x7FFFFFFF`.
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
  - The values agree exactly on only 12–99 % of a zone's pixels (41.6 % overall).
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
  - **TERRAIN**: the biome textures.
  - Other modes get DUST, tinted with the biome's `ground colour`.
- Wind layers give their meshes `windFactorX/Z = wind factor × 0.25`. PagedGeometry's wind vertex program text
  is in the exe, but Kenshi's deferred `objects.hlsl` has no wind input. Whether trees sway in game is
  **Unknown**; the viewer does not sway them.

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

## How the viewer draws it

See docs/viewer.md, "Foliage".
