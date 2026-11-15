# Object rendering

`WorldObjects`, `WorldObjectLayout`, `WorldObjectRenderer`, `ObjectStreamer`, `ObjectMeshCache`,
`BuildingLodMesh`, `BuildingLodShaders`, `ObjectRanges`. Placement rules: [zones](formats/zones.md);
materials: [runtime-materials](formats/runtime-materials.md); geometry: [Ogre mesh](formats/ogre-mesh.md).

## Assembly, LOD and streaming

Resolve building/part/feature instances from merged records, seeded part choice and placement
transforms. Preserve interior/exterior, materials, destroyed states, doors, offsets and scale.
**Part choice** (`FUN_140848950`, **Verified**; `WorldObjectLayout.ChooseParts`): a `parts` entry of group 0 is kept without a draw at chance 0 or 100, else when the int roll `FUN_1409b3b40(0, 100)` (0..100, 101 values; its scale is max - min + 1) is at most its chance. Non-zero groups sit in a std::map keyed by group and are visited in ascending key order after group 0; each draws exactly one float (`FUN_1409b1c80`, scale: the group's total, **Observed**), the total adding every entry's chance with 0 counted as 100, missing targets included; the entries are walked in list order, an entry whose chance is below the roll takes it off and the walk goes on, else it is taken (a missing target yields no part); a roll past the sum takes nothing. The 'Unable to find key in unordered_map' strings in that function belong to other record maps.
**Part nodes** (**Verified**, decompile): each part's node gets `offset × scale` and `setScale(scale)` (`FUN_14055e150`) as a child scene node, so it inherits its parent's scale. A `parts` child hangs under its parent part's node (`FUN_1405607f0`), but the children of an `interior` part hang under the building's node (`FUN_1405609e0` recurses with no parent). Issue #583: the Swamp House (scale 0.72) has its interior floor `RebelE_INTFLOOR.mesh` under the interior part; scaled twice, it sank under the black backing `RebelE_Black_Lower.mesh`.
Workers prepare resident mesh/texture content; render thread publishes GPU objects. Shared meshes
and per-instance state have separate lifetimes. A resident mesh can change finest LOD: refresh
all draw/ray-geometry references before retiring replaced buffers.

Mesh vertex buffers use `GpuVertex` layouts, packed at upload from the CPU's 84-byte `Vertex`: objects, foliage, debris and impostor
bakes 44 bytes (normal and tangent snorm16, colour half), characters 64 (colour stays float: its X carries the morph slot bits).
**Verified** (viewer, The Hub, two cameras): renders differ from the float layout less than two float-layout runs differ from each
other; resident object meshes 174 → 98 MB.

Switch `weld` (Meitou): objects, foliage, characters and debris weld their meshes as they load (`OgreMeshWeld`): vertices equal in
everything but the tangent frame (same handedness, bone assignments and pose offsets) become one with the normalized sum of their
tangents; indices, generated LOD levels, bone assignments and poses are renumbered. Faithful keeps the files' vertices. Read at
decode, so a change applies to meshes loaded after it; LOD caches key on it (`MeshWeld.CacheVariant`). **Verified** (viewer, The
Hub, TAA off): Faithful matches the build without the switch within run-to-run noise; Meitou changes buildings by 0.06/255 mean,
object meshes 98 → 50 MB. About 115 ns per vertex on the decode thread.

Part distance, **Verified** (decompile `FUN_140558b40`, constants read from the exe): a part still at the `objects view range` whose
entity's local box radius (`MovableObject::getLocalAabb().getRadius()`, half the mesh box's diagonal, unscaled) is above 100 gets the
largest float as rendering distance; `ObjectRanges.PartRenderingDistance` with `ObjectMeshCache`'s radius is the same measure.

**Wall part reach** (**Verified**, original decompile): `FUN_14057cc70` creates a `WallBuilding` for `links together` after checking
`is wall gate`. Its virtual +0x2c8 returns itself (`FUN_140592760`), so `FUN_14055f6e0` skips the ordinary part-distance assignment.
All wall parts therefore bypass the small-part cutoff, independently of `link type`, mesh size or LOD. **Observed**, clean GOG meshes:
`Wall3.mesh` has a local AABB radius of 182.78, `Wall3_Section01.mesh` 93.17. Applying only the size exemption incorrectly clipped short
walls while retaining full sections (#667). Meitou uses the owner's wall flags for this exemption; the whole-object reach still applies.

Generated object levels (`ObjectLodGen`, `MeshSimplifier.Chain` with the texture guard): a collapse re-points a corner at a vertex
of the new point, so the texture coordinate jumps unless that vertex is chosen by the triangle's own mapping. A guard that only
compared the jump with the part's mean UV density let tiled decks (Shield Bridge, tread squares) pick another UV island, showing
planks and diagonal patches from about 700 units. Now a corner takes the vertex whose UV best matches the triangle's mapping
extended to the new position, and the collapse is refused when none is within 0.01 UV. **Verified** (Shield Bridge at 34816,-112031,
offscreen at distance 700: the deck matches a build without generated levels). Cost: such meshes keep about half their triangles at most.

## Shared texture images

The sharing caches also have one high-water mark together (`TextureShare.ResidentBytes` against `MarkMb`, 2 GB at full quality), not one each.
Above it, textures unused for 8 s are unloaded, least recently used first, until the total is under three quarters of the mark. Below it,
a texture is unloaded after 20 s unused.

## See-through items (Meitou, issue #562)

For mods and Meitou's glass bottles (below); no base-game record has the field. A `MATERIAL_SPECS_CLOTHING` record with the file field `opacity map` (`OpacityDither.MapField`) is partly
see-through: the map's red channel is the opacity (1 solid, 0 clear), on the item's own UVs. Without the field nothing changes.

- **Main view: a blended pass** (`WorldFrame.DrawSeeThrough`, in `FinishScene` after the GI's resolve, the lamps' shadows, the characters'
  occlusion and the reflections, before the shafts and fog). The colour passes leave the see-through draws out and they are drawn afterwards
  over the scene by their opacity, into the scene's colour and depth alone (the GI's albedo and traced-sun targets keep the opaque scene),
  depth tested, writing neither depth nor the scene's alpha (the character mask). So occlusion, reflections, fog, motion vectors and the
  upscaler see what is behind them, as with the water and particles. Front faces only unless the material is double-sided: two layers of a
  closed shape would show nearly twice the opacity. Glass bottles (below) write depth and draw front faces only.
  - Ground items (`WorldObjectRenderer`, `AlphaSource.Opacity`, the map in the second diffuse slot: an item's material is never dual; it replaces
    the ALPHA type's cut-out): `HoldSeeThrough` takes their draws out of the main pass, `DrawSeeThrough` records them farthest first with the
    `objects see-through` program, which reads the GI's probes itself. Premultiplied (`One, SrcAlpha`): an item writes its colour times the
    opacity and one less the opacity, the same result as blending by alpha; glass bottles (below) use the same state.
  - Worn and held items (`CharacterShaders.SeeThrough`, the map in `SlotOpacity`): the colour program's push `seeThrough` is 1 in the main pass
    (the item's fragments dropped) and 2 in the blended pass, which draws again only the batches that have held such a part and drops everything
    else in them. The worn cut-out on normal alpha stays.
  - Not sorted within a mesh or between objects and characters (objects first). A see-through item under a water surface is drawn over it.
- **Elsewhere: a dither** (`opacityDither()`, the LOD cross-fade's interleaved gradient noise, never moving): the shadow casters (the shadow's
  filter blurs it into a lighter shadow), the water reflection, the objects' selection mask, icons (supersampled ones average 4 x 4 samples
  into alpha) and portraits. Hover and pick are solid; the GI's and the traced sun's rays take the item as solid (`RayCut`).
- Drawn solid until the opacity map is resident.
  - **The main view draws them as glass** (Vulkan; elsewhere ordinary see-through items): `MeshSurface.Glass` (bit 128 of the push spare)
    marks the draws, `PrepareSeeThrough` blits the scene's colour at the render size into `GlassScene` before the blended pass (only with a
    bottle held), and `DrawSeeThrough` records the bottles first in a job of their own: front faces only, writing depth (each fragment
    replaces the scene, so overlapping bottles and a neck over its shoulder keep their order whatever the triangles' order), the copy's
    bindless index in the second normal slot. A fragment: the copy seen through both walls (the texture's hue mostly washed out, darker
    towards the silhouette) and the drink (its surface a darker line), times one less Schlick's Fresnel, plus the near wall's and the far
    wall's reflection (the far wall's normal the near one's mirrored across the plane facing the eye, as in a round bottle; dimmed by the
    near wall and the drink), each the game lighting's specular of a smooth dielectric (`kenshiLight` with no albedo, gloss 0.96: sun, sky
    and lamps; at most 16), and 6 % of the lit texture; where the map is solid the lit texture. The copy's lookup is bent along the refracted
    view ray, at most 6 pixels. The other see-through items follow, writing no depth.

## Player buildings and the placement ghost

Sites and finished buildings of `WorldSnapshot.Buildings` are runtime placements (`WorldObjects.AddRuntime/RemoveRuntime`,
`BuildingPlacement` with `WorldY` absolute), merged into their zone's layout. Meitou adds one to an already loaded zone
incrementally (`ObjectStreamer.AddRuntime`, no zone reload, so nothing flickers; a pending zone job falls back to a
reload). Town-house repairs replace only the matching placement and its furniture/interior after the replacement layout
is ready; loaded neighbours retain their instances and GPU bindings, including while a zone is still being filled.
Host diff: [simulation.md](simulation.md#buildings).

**Ghost.** `WorldObjectRenderer.SetGhosts(GhostDraw[])` draws a building's parts (laid out once at the origin, exterior
only) at a transform, from the `ObjectStreamer.Loose` pseudo-zone (never streamed out, scanned each frame like the
landmarks), with 65 % dither coverage and a tint through the push `FlatColour` (`uTint` only reaches grey-channel
materials): green valid, red invalid, blue queued. Not drawn in shadow or landmarks-only passes.

**Farm plants.** The host lays out each farm machine of the snapshot once (`FarmLayout.Plant`, seeded by the building id) and
recomputes its plants' transforms (`FarmLayout.Transform`) when the farm's growth, death or harvest count changes
([farm plants](game/buildings-production.md#farm-plants)). `FarmPlants.Set` keeps one `Loose` instance per
plant mesh (a `BuildingPart` of the farm building, its FARM_PART material) and draws the shown ones through `DrawReal`, shadows
included. The original draws one merged mesh per farm with a `plantData` shader array; Meitou instances each plant (batched by
mesh). Each plant's colour (`FarmLayout.Tint`, the death tint and brightness of
[farm plants](game/buildings-production.md#farm-plants)) multiplies its albedo, packed into a negative spare `M44`
(`FarmPlants.PackTint`: 8 bits per channel over [0, 1.275]).

## Closed interiors

**Original** (**Verified** by code, Ghidra, unless marked):
- A building with an `interior` list owns an interior object, open or closed.
- **Opening.** `FUN_1407f7230` runs every frame. Each player character whose location is a non-destroyed building sets that interior's keep-open timer to 10. The timer falls by the game-time step (`FUN_14054a790`) only while the building is complete (its vtable slot 69 returns the building's is-complete byte `+0x160`, **Verified**; Meitou: a construction site is held open), and the interior closes below 0 (`FUN_14054b690`). A building just left shows every floor (floor 4) until then.
- **Closing** (`FUN_14054b130`) hides the interior's contained objects in every pass, except those flagged outside (`RootObject+0x240`). Their entities are destroyed and the lights go off.
- **Contained objects** are registered by `FUN_14054f3d0`. They are interior-layout furniture and buildings with a parent building: a saved `isInsideBuilding`, or player ghosts placed inside.
- **The outside flag:**
  - Loaded from a state's `flags` bit 0, or the older `is outside` (`FUN_140552b70`).
  - Exterior-layout objects get it from their layout (`+0xa0`, `FUN_1405765c0`).
- `is interior furniture` / `is exterior furniture` only pick collision groups and the build label (`FUN_14055f6e0`, `FUN_1404df450`).

**Meitou:**
- Opening is `GameHost.UpdateInteriorView`, which holds a left building open with all floors for 10 s of simulation time, or until it is unloaded, removed or destroyed.
  - In the install's interior layouts, 10 170 objects without the flag are `is exterior furniture` (barrels, cages, camp beds, Stack's rooftop awnings); these are now hidden while closed.
  - 1 820 objects with the flag (yard tents, torches) stay.
- A building inside another (`PlacedMesh.Contained`) is hidden while the building whose hull holds it is closed (`ClosedAround`); a player site takes its parent's hull over a neighbour's within the 2-unit margin (`PlacedMesh.ContainerId`). These are player sites with a parent (saved with `flags` 0), and zone states with `is inside building` and no outside flag.
- Shadow passes keep what the floor view hides above the viewed floor (parts, objects standing above it; `WorldObjectRenderer.DrawReal`), so a lower
  floor stays shaded under its ceiling (a watchtower's floor 0 was sunlit). The traced scene keeps them too (`WorldObjectRenderer.LeftOutOfRays`): the rays stand for the sun and the sky, so a roof the view hides still shades the room and blocks the sky. Faithful shadows leave them out (`HiddenFloorsCast`), as the game does: **Verified** (decompile) `Building::setFloorVisibility` (`FUN_1405482e0`) calls the scene node's `setVisible(vis, cascade)` for each physical entity of the floor, and an invisible object is in no camera's queue, the shadow cameras' included.
