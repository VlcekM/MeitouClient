# Interior cut

What lies inside a building's interior mask is not drawn, so a room dug into a hillside shows its floor and no grass
grows through a bar's floorboards. `InteriorMaskRenderer`; consumers: terrain, foliage, map features.

## Original

- **Verified** (installed `interiormask.material`/`.hlsl`, `workspace.compositor`): node `InteriorMask` draws render queues
  16-17 into `rt_depth_range` (RG16F, cleared 0, no depth buffer) twice: culling clockwise writing G, culling anticlockwise
  writing R, each value view depth + 1. Readers sample it as `rt_interiormask`: `if (w < G) clip(R - w)`, i.e. a fragment
  more than 1 unit beyond R and before G is cut. The water reflection binds a 1x1 dummy; the terrain shadow caster does
  not sample it.
- **Verified** readers (installed `data/materials`): `terrain.material`/`terrainfp4.hlsl` (patches, always);
  `foliage.material` `Grass` (`grass_fs`, always; the `Foliage` material there is commented out); `objects.hlsl` and
  `triplanar.hlsl` under `CLIP_INTERIOR` (0x200); `mapfeature.material` `Feature_Terrain_DX11` (`interiorClip` unit).
- **Verified (decompiled)**: map features always get CLIP_INTERIOR ([runtime-materials](formats/runtime-materials.md)),
  and so do foliage leaves (FUN_1406d1380 copies the `leaves *` fields and calls FUN_140843920 with mode 4, FOLIAGE).
  Building parts and items do not. **Observed**: the foliage meshes' own materials also come from FUN_140843920
  (FUN_1406cf140 / FUN_1406cf640 read `material type` and call it; not traced further), so Meitou cuts them too.
- **Verified (decompiled)**: the mask entity exists only while the building's interior is open. It is made in the open path
  (render queue 0x10, visibility 0x800, no shadows; skipped with `skip stencil tricks`) and destroyed on close, so a closed
  building cuts nothing ([closed interiors](render-objects.md#closed-interiors)). Meitou cuts with every loaded mask (engine
  difference).
- **Observed**: the queue-16 geometry is the `interior mask` parts' `phs or mesh` (`Storm_House_01_Interior_MASK.mesh` etc.;
  [settings](formats/settings.md) `skip stencil tricks`), placed with the building node. **Verified** (probe, merged
  records): 31 mask parts have such a mesh (gate triggers have none), none has an offset; the meshes are not the
  `xml collision` hulls (Outpost mesh Y 214..401, hull -155..748) and 30 of 31 are not convex.

## Meitou

- The mask: masks of loaded buildings within 3000 of the eye, drawn before the scene with the near slice's camera into
  RGBA16F; every face writes depth + 1, min into R, max into A (the nearest and farthest face, the same as the game's facing
  split for a closed mesh seen from outside; not dependent on the meshes' winding). With the eye inside a building's
  trigger hull its masks write R = 0 (the game's value there: every face seen is a back face). `Boxes` are the drawn mask
  meshes' X/Z bounds.
- Readers: `InteriorMaskRenderer.WithCut` puts the test first in a fragment program. A `discard` costs the early depth
  test, so programs without one stay without it and only work over a box takes the cut:
  - terrain: patches overlapping a box use cut variants of the colour and prepass programs;
  - map features: instances whose sphere overlaps a box form their own batches, drawn with the full (dithered) program
    with the cut on (`ViewConstants.interiorCut` and push `fadeMode` bit 2), never the solid one;
  - foliage meshes: the full colour and prepass programs (already discarding) cut while the view has it on; the GPU cull
    gives a fully visible instance over a box the fade 1.5 (drawn whole), which lays it out with the dithered ones;
  - grass: always cut in the main near view (its cut-out already discards);
  - TERRAIN-mode features and rocks (the terrain's mesh path): placements over a box are drawn with the meshes' full
    colour program with `TerrainConstants.interiorCut` on (features and CPU-culled rocks in their own list; GPU-culled
    rocks get the fade 1.5 too and are drawn by the fading draw).
  - other buildings' parts (Fixes settings, `Walls inside buildings`, on; off draws them, as the game): a part whose sphere overlaps the mask box of a
    building in the interior view takes the map features' cut batch (`WorldObjectRenderer.CutsIntoViewed`), so the Hub's wall stays out of the Shinobi
    Thieves tower ([data](formats/zones.md#interior-masks), issue #111). Not cut: parts of a building whose mask is drawn (the viewed one's own), doors,
    layout objects, contained buildings, stand-ins. Their shadows: in the shadow passes the same parts (sphere over a viewed building's hull) cast
    nothing inside the hull (convex footprint and height range of up to 4 viewed buildings, 32 edges in all, in `ViewConstants.cutPlanes`; the
    objects' depth program discards). Limits: the mask is shared, so such a part between two masks along a ray, or in a non-convex mask's concavity, is
    cut too; a far cascade cached before the view opened keeps the shadow until it is redrawn.
- Only the main view's near slice; not shadows, the water reflection or farther slices.
- Engine limits: "inside" is decided by the hull, not per ray; foliage impostors and the grass motion pass (TAA velocity)
  are not cut.
