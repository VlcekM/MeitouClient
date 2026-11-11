# Foliage placement and materials

Original-game research from placer binary/disassembly, FCS records, overlays and shipped assets.
Rendering/streaming choices: [render-foliage](../render-foliage.md), [impostors](../impostors.md).
**Unknown:** reproducing actual original placement locations; the game does not save foliage.
Algorithm verification does not establish screenshot parity for complete generated layouts.

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
  Exception (**Verified**, `FUN_1406d2410` at the town test, `FUN_140927f10` the nearest town): a mesh with a non-empty `building type` list (the resource rocks) is **not**
  rejected when the nearest town's owner faction is the player's (faction `+0x250`, see factions-squads-towns.md); every other mesh, and rocks near any other town, are rejected on
  distance alone. The town list is the live one, so it includes towns the player founded or took, and the test runs when the zone is paged. Not modelled: Meitou places a zone
  from the level's towns only and caches the layout by content, so ownership (and the player's own towns) never changes a layout; a town the player owns in a loaded save keeps
  its rocks away. Wiring it needs the layout cache key to carry which towns the player owns.
  A town with range 0 keeps nothing away (**Observed**: Newwworld's The Hub `18919-Newwworld.mod`, so its trees grow among the buildings, issue #522).
- **No building test** (**Verified (decompiled)**, `FUN_1406d2410`): the mesh placer rejects by slope, zone, town range, altitude and the overlay's road
  channel only (the grid test there is the overlay A byte, `FUN_1406d82d0`); nothing checks buildings, so a tree can stand in a building's yard or room.
  Whether anything removes foliage under buildings after placement is **Unknown** (no such caller found).

## Randomness, noise and ground

**Verified** MT19937 reference sequence (5489 first3499211612); float=min+u/(2^32-1)*(max-min),
double then float; integer inclusive rejection under smallest all-ones mask. Reseed each mesh
layer using leading numeric StringId minus zone-noise transform; layers are independent.
**Verified disassembly** FUN_1406cbba0/FUN_1406cbf20/FUN_1406cc0e0 hash n=x+57z, n^=n<<13, then
(n*(n²*60493+19990303)+1376312589)&0x7fffffff. These differ from textbook multipliers.
Value1-h/2^30; smoothing sides/8,corners/16,centre/4, cosine interpolation pi3.1415927.
Grass five octaves scale=noise-scale*72, amplitudes0.5^o; remap0.5+0.5sum, cutoff/rescale,
brightness/cap; blackout multiplies shifted(50007,50007) noise.

**Verified decompiled** ground129² float grid,36-unit spacing, bilinear clamped height. Slope
is height difference per36, not degrees. Overlay RGBA: R coverage,G grass spots,B dirt,A road.
Clear129² R/G; grass biome/layer order regenerates R by max, altitude fades300, B>70 gives0,
A>10 skips, byte conversion wraps. Channels0→R, nonzero→G. **Observed** regenerated overlay
footprint supports the model but value matches are incomplete; biome list comes from blend masks.

## Mesh placement

**Verified decompiled** grass before meshes, biome/list order; layer reseed. Uniform X then Z;
limit-to-grass redraw<=21 while R+G<=149. Wrong biome ends cluster. Cluster first member centre;
count inclusive min/max, radius min/max, subsequent direction draws reversed into normalized
XZ, distance5..radius. Slope/altitude failures end cluster; out-of-zone skips.

Per member: slope → zone → town rejection (still consume later draws) → altitude → floating
Y=max(Y,100) → random scale → vertical-offset*scale → road perimeter rejection → yaw0..360 →
add if accepted. Original scale/yaw quantized8-bit; Meitou keeps floats. Spawn children even
for rejected parents, recursively, then stamp grass-spot disc into G (<=96). Slope-align rotates
up after yaw; otherwise keep-upright yaw only, or CRT random axis seeded by positional noise.
Resource rocks create/find their building after ordinary placement.
Re-checked against `FUN_1406d3740` (the layer loop) and `FUN_1406d2410` (one member) for issue #406: every test and draw above is in the same
order in `FoliageLayout.PlaceMesh`/`Place` (**Verified**, decompile read). Tower of Abuse (37.35): our placer puts a
`Foliage_Rock_Bouldercluster_MonoTex` (3438) at 23862,963,18520, scale 2.13, 150 from the tower and 690 from the town's level position (range 200),
whose rocks cover the ramp foot (23340-23450, 18140-18260); the shipped tile there has ground and the lower ramp (y 1159-1208) (**Observed**,
`navmesh --probe`). The original has no rock there either (**Observed**, a vanilla screenshot of the ramp), so an input differs; not the
town (one placement, 23826,17893), height and slope (zone grid then terrain, no building test, `FUN_1409b32f0`/`FUN_1409b1e30`) nor the
random orientation (`FUN_1406cc2e0`: same hash, `srand(float(1 - h/2^30) * 1e6)`, axis (r3, r2, r1), angle × 360; same seed for this rock).

## Materials, sizes and grass

FOLIAGE_LAYER/MESH/GRASS schema fields govern density/clusters, ranges, wind, scale, material mode,
road/town/altitude/slope, children and textures. Keep field semantics separate from renderer LOD.
TERRAIN mode uses biome triplanar material; emissive and double-sided leaf cutouts differ.
Grass blades use ground coverage/orientation noise, dimensions and material parameters.
Original layers/ranges/page transitions have no discovered impostor level. Mesh-radius classes
and ordinary-tree exceptions used by Meitou are engine rules, not original range facts.

Meitou layout cache hashes relevant source/placement inputs and is bypassed by --no-load-cache;
generated LOD, Hi-Z/fog culls and atlas residency live in the renderer references.

## Expression reference

  **Verified** (`FUN_1406d2410` l.146-156): a mesh with a `building type` (a resource rock) is not rejected inside the range of a town whose owner faction is the player's; every other mesh, and rocks near other towns, are. Meitou: `FoliageTown.Owned` from `FoliageWorld.PlayerTowns`, a snapshot of the player's towns taken when a world is made or loaded (`GameHost.SnapshotPlayerTowns`); a town founded or captured later changes resource-rock placement only when its zone is placed again. The layout cache key includes the owned flags and the cache is bypassed when the snapshot differs from the one it was keyed with. No placement test (needs a full zone input).
- Noise: `n = x + 57 z`; `n ^= n << 13`
- Materials: `windFactorX/Z = wind factor × 0.25`
- TERRAIN-mode meshes: `slope = min(1, 1 − normal.y)`
- TERRAIN-mode rock normals (**Observed**, 40 meshes of `foliage/rocks` and `newland/Assets/Rocks` read with `OgreMeshReader`): most
  store unindexed triangles (vertices = 3 × triangles). The `newland` rocks give every copy of a position the same normal
  (authored smooth); `formation03x`, `rockformation`, `rockformation02`, `rock_M_round`, `roborock`, `hatrock`, `rock03`,
  `AirRock01`, `AshlandRibTEST` have hard edges (a position's copies differ by 19° to 102° on average). `Barkworm_Pillar01` has zero-length
  normals.

## Not verifiable

- The 1,335 stored Iron/Copper Resource buildings sit in 10 zones, in tight groups of 2–5 within about 80 units.
  The current records cluster children 450+ units out, and give 5–23 rocks a zone where the store has 174–470.
  The stored placements therefore come from older data and cannot check the placement sequence; there are no
  saves to compare with.
- Treat the mesh placement as the decompiled rules, **not** as compared against the game.
- **Observed:** user screenshot near the Hub, 2026-10-04, supports the executable noise hash and relative JunkSat01/JunkBall01/arch layout. Exact camera position was unavailable; this is not a measured placement-parity test.
