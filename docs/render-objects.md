# Object rendering

`WorldObjects`, `WorldObjectLayout`, `WorldObjectRenderer`, `ObjectStreamer`, `ObjectMeshCache`,
`BuildingLodMesh`, `BuildingLodShaders`, `ObjectRanges`. Placement rules: [zones](formats/zones.md);
materials: [runtime-materials](formats/runtime-materials.md); geometry: [Ogre mesh](formats/ogre-mesh.md).

## Assembly, LOD and streaming

Resolve building/part/feature instances from merged records, seeded part choice and placement
transforms. Preserve interior/exterior, materials, destroyed states, doors, offsets and scale.
Workers prepare resident mesh/texture content; render thread publishes GPU objects. Shared meshes
and per-instance state have separate lifetimes. A resident mesh can change finest LOD: refresh
all draw/ray-geometry references before retiring replaced buffers.

Faithful uses researched object/part distances; Meitou `reach` extends size-aware ranges and
distant landmarks/towns. Distant town stand-ins fade separately from real parts. Source/manual
and generated levels use projected demand with stable transitions. Do not treat a shorter part
range as an accidental renderer limit: it can be original fidelity. Visibility/depth/shadow/
reflection and streaming residency need consistent bounds.

## Arrival prefetch

During a character flight or squad tour hop ([viewer.md](viewer.md#squad-tour)) the host sets `WorldFrame` `Ahead` to the landing
view's eye from the tap on. Object zones are laid out and kept, meshes requested, and mesh LOD / texture mips chosen by the nearer of
the eye and that point (`ObjectStreamer.Near`); terrain biome textures gather cells round both. Foliage zones within the near reach of
that point are laid out and kept as well. The load order puts the arrival first: zone layouts, mesh decodes, instance resolves, terrain
biome pairs and foliage layouts rank by distance from the arrival, and what is only near the eye (the flight path) ranks after all of it
(`ObjectStreamer.Rank`). Object textures the arrival view uses are touched first and decode ahead of the other streaming work
(`WorldTextureCache.Urgent`, `BackgroundWork.RunUrgent`). The terrain height window and the grass pages still follow only the eye.
The simulation focus moves to the landing character at once, so its towns populate during the flight.

## Kept points

Advanced "Keep textures at characters" (Meitou, default on, `UserConfig.KeepTexturesAtCharacters`): the host sets `WorldFrame` `Kept` to
the drawn position of every squad bar member each frame. `ObjectStreamer.Near` / zone layout count the nearest of eye, `Ahead` and `Kept`,
so object meshes and texture mips stay loaded round each character; terrain biome textures gather cells round each (ranked after the
eye's). `Kept` ranks after the eye in the load order. Foliage and grass pages follow only the eye and `Ahead`. Costs video memory
(the VRAM guard still applies). `WorldFrame` merges the points first (`ObjectStreamer.MergeKept`): one within 500 units of the eye or of an
earlier point is dropped, so a squad standing together is one point. Every distance test walks the kept points, and `ObjectStreamer.Scan`
tests each unresolved instance every frame. Out-of-range instances (stand-ins and unknown part distances included) are therefore parked per
zone and come back by one zone test against the current ranges. **Observed** (offscreen bench at Fog Islands, Release, 16.5k instances of which
2.4k resolved): upd-objects 3.1 ms → 0.17 ms with 10 points together, 9.3 ms → 2.0 ms with 30 points 600 units apart. The same instances and meshes resolve.

## Batching and depth

Batch compatible mesh/material/state instances; sort colour batches near-first when enabled.
Open building meshes are double-sided. Fade uses per-instance dither lo/hi windows; solid,
fading and distant-town batches have different coverage needs. `EarlyDepth.Enabled` selects
opaque, discard-free colour only for no-cutout/non-fading parts. Draw solids first; retain
cutout/dither shaders for other batches. Cache native state per program so solid/fading variants
do not evict one another. Depth/caster coverage must remain compatible with colour.

## Building sign highlight

`WorldObjectRenderer.HighlightSigns` splits real parts with owning BUILDING `is sign` into yellow-tinted
batches using `FlatColour`, retaining textures, cutouts, lighting, depth and LOD fades. Shadow batches,
distant stand-ins and placement ghosts keep their normal appearance. Input/evidence: [controls](game/ui-input.md#building-sign-highlight).

## Player buildings and the placement ghost

Sites and finished buildings of `WorldSnapshot.Buildings` are runtime placements (`WorldObjects.AddRuntime/RemoveRuntime`,
`BuildingPlacement` with `WorldY` absolute), merged into their zone's layout. Meitou adds one to an already loaded zone
incrementally (`ObjectStreamer.AddRuntime`, no zone reload, so nothing flickers; a pending zone job falls back to a
reload). Host diff: [simulation.md](simulation.md#buildings).

**Scaffold.** The original clips each part at `1.2 f` of its mesh height and draws the scaffold grid (`construction.dds`)
above it ([construction look](game/buildings-production.md#construction-look)). Meitou's approximation: the part's
instance matrix carries `M44` = `ScaffoldBase` (10000) + the world-space clip height (`f` and the mesh top from its bounds);
`BuildingLodShaders` keeps pixels below it as the building and above it only a procedural world-space grid
(cell 0.12, line 0.06), discarding the rest. The shadow pass clips the same. Differences: no `construction.dds`
texture, no fade of a grid over the built part, and the clip is a world height, so a tilted site clips level.
Sites always use the fading (discard-capable) batch.

**Ghost.** `WorldObjectRenderer.SetGhosts(GhostDraw[])` draws a building's parts (laid out once at the origin, exterior
only) at a transform, from the `ObjectStreamer.Loose` pseudo-zone (never streamed out, scanned each frame like the
landmarks), with 65 % dither coverage and a tint through the push `FlatColour` (`uTint` only reaches grey-channel
materials): green valid, red invalid, blue queued. Not drawn in shadow or landmarks-only passes.

**Farm plants.** The host lays out each farm machine of the snapshot once (`FarmLayout.Plant`, seeded by the building id) and
recomputes its plants' transforms (`FarmLayout.Transform`) when the farm's growth, death or harvest count changes
([farm plants](game/buildings-production.md#farm-plants)). `WorldObjectRenderer.SetFarmPlants` keeps one `Loose` instance per
plant mesh (a `BuildingPart` of the farm building, its FARM_PART material) and draws the shown ones through `DrawReal`, shadows
included. The original draws one merged mesh per farm with a `plantData` shader array; Meitou instances each plant (batched by
mesh). Not drawn: the death tint and the per-plant brightness.

## Doors

Door parts carry `PlacedMesh.DoorIndex` (their entry in the building's `doors` list) and take the parent building's `scale`, not
their own, as the door collision does ([collision](formats/collision.md#placement-and-groups); **Observed**: Defensive Gate III/IV
are 0.85 with doors at 1, and the leaf drawn at 1 sat oversized and off-centre in the frame). The host hands the renderer the snapshot's
open amounts (`WorldObjectRenderer.Doors`, by placement id and index); `DrawReal` poses a door part from its closed placement by
`DoorLeaf.LocalOffset` ([motion](game/locks.md#doors)) along the axes of the part's collision box (`DoorFrame`, the first box of its
`xml collision`, PhysX Z-up mapped to the part's Y-up), in world units, when its amount changed. Colour and shadow passes see the moved leaf; the GI acceleration structures take
the instance transform only when they gather instances (not checked for leaves in motion).

## Turrets

A turret's turning parts (BUILDING_PART `rotation function` ROTATION_TARGET) are joints: the layout (`WorldObjectLayout.AddPart`) gives each part that is or hangs from one
`PlacedMesh.Joints` (the joints above it and its own, outermost first; `TurnJoint` = part record, pivot = its node's place, axis = its node's Y for a yaw part or X for the
gun's pitch, both as placed). The host (`GameHost.UpdateTurretPoses`) gives the renderer `WorldObjectRenderer.Turrets` (placement id = turret key → `TurretPose`: the
simulation's `TurretRig` angles from `MannedTurret.Angles`, interpolated between the last two snapshots by the simulation's alpha, kept after the gunner leaves).
`DrawReal` poses a part (`PoseTurret`) when the pose's version changed: its placement times, from the innermost joint out, a turn about the joint's pivot and axis by its
angle. **Verified** by a probe over every turret building of the install (random yaw, position and angles): the posed part's origin and its Z and Y directions equal the
simulation's `TurretRig.Derived` (30 of 30 parts). **Unknown**: the pose has not been looked at in a rendered frame. Only simulated turrets (the combat system) turn; the
viewer never does. Parts that are not joints (the six-barrel add-on, the lamp) ride on their joint.

## Closed interiors

**Original** (**Verified** by code, Ghidra, unless marked):
- A building with an `interior` list owns an interior object, open or closed.
- **Opening.** `FUN_1407f7230` runs every frame. Each player character whose location is a non-destroyed building sets that interior's keep-open timer to 10. The timer falls by the game-time step (`FUN_14054a790`), and the interior closes below 0 (`FUN_14054b690`). A building just left shows every floor (floor 4) until then.
- **Closing** (`FUN_14054b130`) hides the interior's contained objects in every pass, except those flagged outside (`RootObject+0x240`). Their entities are destroyed and the lights go off.
- **Contained objects** are registered by `FUN_14054f3d0`. They are interior-layout furniture and buildings with a parent building: a saved `isInsideBuilding`, or player ghosts placed inside.
- **The outside flag:**
  - Loaded from a state's `flags` bit 0, or the older `is outside` (`FUN_140552b70`).
  - Exterior-layout objects get it from their layout (`+0xa0`, `FUN_1405765c0`).
  - Interior-layout objects spawn without it (`FUN_140583400`). Runtime setters exist (`FUN_1404ec160`, `FUN_14047d830`), but their triggers are **Unknown**.
- `is interior furniture` / `is exterior furniture` only pick collision groups and the build label (`FUN_14055f6e0`, `FUN_1404df450`).
- Ground items and characters have no closed-interior rule, only the floor sweep `FUN_1409fb540` (**Observed**).
- **Unknown:** what makes the doorway black. There is no occluder object; the mask is destroyed on close.

**Meitou:**
- Opening is `GameHost.UpdateInteriorView`, which holds a left building open with all floors for 10 s of simulation time.
- An interior layout's object is hidden while its building is closed unless its `interiors.level` state has the outside flag (`LayoutItem.Outside`). The flag set at the editor is taken as the runtime setters' result (**Observed**).
  - In the install's interior layouts, 10 170 objects without the flag are `is exterior furniture` (barrels, cages, camp beds, Stack's rooftop awnings); these are now hidden while closed.
  - 1 820 objects with the flag (yard tents, torches) stay.
- A building inside another (`PlacedMesh.Contained`) is hidden while the building whose hull holds it is closed (`ClosedAround`). These are player sites with a parent (saved with `flags` 0), and zone states with `is inside building` and no outside flag.
- **Not done:** interior lamps still light a closed interior.

## Validation

Main, reflection and shadow stages repeat object work; inspect all three. **Observed** swamp
probes: hidden fragment shading can dominate despite large triangle counts. Generated LOD,
early depth and sorting change different costs. Validate doors/interiors, town stand-in joins,
LOD fades, mirrored/reflected geometry and texture/memory pressure in still and moving views.
[bench.md](bench.md), [render-shadows.md](render-shadows.md), [render-water.md](render-water.md).
