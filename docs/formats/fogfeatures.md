# Placed fog volumes (`fogfeatures.dat`)

Where the swamp's fog comes from: not from the weather (the Swamp's weathers have `fog enabled` off, [weather.md](weather.md)) and not
from BIOME_GROUP, but from fog volumes placed on the map with the level editor and stored in `data/newland/land/fogfeatures.dat`. They
are drawn after the atmosphere haze, over everything lit, whatever the weather or season. Labels as in [../README.md](../README.md):
**Verified (decompiled)** = read in the named function of `kenshi_x64.exe` (Steam build, Ghidra 12.1.4; decompiled output stays
outside the repository, nothing here is translated code), **Verified** = checked against the shipped files, **Observed**, **Unknown**.

Sources: the install's `fogfeatures.dat` (4501 bytes), `data/materials/post/fog.material` and `fog.hlsl`, `compositors/main.compositor`,
`gui/layout/Fog_Editor.layout`, and the exe functions: loader FUN_14010f060 (called with "fogfeatures.dat" by the world load
FUN_14086db80), level-editor save FUN_140777ce0 (writes it with FUN_14010c760), `FogVolume` base constructor FUN_14010ac90, the setters
FUN_14010aea0 (density), FUN_14010b100 (colour), FUN_14010afd0 (edge), the block constructor FUN_14010d370, the sphere FUN_14010e500, the
beam FUN_14010e840 with its placement FUN_140109130, the hull builder FUN_14010b6a0, the per-frame inside test FUN_140109720 with FUN_140109370 /
FUN_140109ad0 (setInside) and the `contains` of the sphere FUN_1401090c0 and the cylinder FUN_140109260, the alpha setter FUN_140108f50, the
additive switch FUN_140109a60, the weather effects' volumes FUN_1400fb070 and their fades (`FogController::FogFadeSphere` FUN_140109410 /
FUN_140109500, `FogFadeCylinder` FUN_140109590 / FUN_140109680, started by FUN_140109f20 / FUN_14010a050 from FUN_1400f8ed0 / FUN_1400f8f60), the
particle systems' wrapper `ParticleSystemHandler` FUN_1404079c0, and in `OgreMain_x64.dll` `Pass::setSceneBlending` (both forms).

## File layout (Verified: FUN_14010f060, and a scratch parser that consumes the install's file exactly, 2026-10-08)

Little-endian. The game opens it from the "Landscape" resource group.

| Field | Size | Notes |
|---|---|---|
| magic | 4 | `FF01` (version 1) or `FF02` (version 2); anything else is read as version 0 |
| count | int32 | number of volumes |
| per volume: name length, name | u8 + bytes | version 2 only. The editor's label, by default the type name and the position, e.g. `Block  (7522, -10611)` |
| type | u8 | 2 sphere, 3 beam (cylinder), 4 block (`FogPlaneVolume`, "Block" in the editor). Other values are not handled by the loader |
| colour | 4 × float | RGBA. Only RGB is used: the colour setter copies three components and the alpha stays the constructor's 1 (Verified (decompiled), FUN_14010b100, FUN_14010ac90) |
| distance | float | the density distance: the shader gets `density = 1 / distance` (1 when the distance is not positive; FUN_14010aea0) |
| sphere (2) | 3 floats + float | centre, radius |
| beam (3) | 3 + 3 floats + float + float | start, end, radius, edge (`edgeBlur = 1 / edge`, FUN_14010afd0) |
| block (4) | float + 7 × (3 floats + float) | edge, then seven planes as (normal, d) |

**Plane sign (Verified)**: the loader builds `Ogre::Plane(normal, -d)`; Ogre's (normal, constant) constructor stores `d' = -constant`, so
the plane is `normal · p + d = 0`. The hull builder hands the shader `planes[i] = (normal, -d')` (FUN_14010b6a0), i.e. `w = -d`, and the
shader treats `dot(normal, p) < w` as inside. So a point is in the block when `dot(normal, p) < -d` for all seven. Checked on the data:
"Robot Pit surround (-73200, -57250)" is then x in (-80528, -65871), z in (-64655, -49844), y < 1098, around the named point; with the other
sign every block would sit on the far side of the map.

## The base game's volumes (Verified: the install's file)

28 volumes: 25 blocks, 2 beams ("Quarry", "Hellish Cylinder"), 1 sphere (the Skinner's Roam dome). The blocks, with colour and density
distance (edge in brackets):

| Name (as stored) | Colour | Distance (edge) |
|---|---|---|
| Swamp_N-W-Mid (-46488, 37343) | 0.68, 0.56, 0.43 | 2000 (900) |
| #140805Swamp[SOUTH]#140805 (-39318, 54045) | 0.59, 0.56, 0.50 | 4500 (810) |
| Swamp-W-Small area (-60903, 44660) | 0.63, 0.59, 0.50 | 9000 (1000) |
| SwampDesertpool (-26131, 27308) | 0.61, 0.61, 0.59 | 10000 (1000) |
| Central Forestland (-17803, 54472) | 0.83, 0.73, 0.59 | 10000 (1000) |
| Western Forest (-79231, -75130) | 0.78, 0.71, 0.57 | 9000 (1000) |
| Floodlands (-59263, -75088) | 0.63, 0.63, 0.63 | 23500 (1000) |
| fog island layer (-78888, -35796) | 0.88, 0.83, 0.78 | 3500 (690) |
| FOG-ISLANDS01 (-80472, -42607) | 0.85, 0.80, 0.78 | 12500 (300) |
| Robot Pit surround (-73200, -57250) | 0.76, 0.78, 0.78 | 2000 (1000) |
| Robot Pit White (-72309, -58162) | 0.85, 0.80, 0.80 | 7500 (1000) |
| Block (7522, -10611) | 0.21, 0.16, 0.14 | 7000 (380) |
| Vain_Fog01 (-110403, 3457) | 1.00, 0.31, 0.29 | 7500 (680) |
| VAIN_FOG02 (-91179, 1914) | 0.95, 0.31, 0.29 | 4500 (810) |
| Hellish small central Block (-115068, 62804) | 0.68, 0.63, 0.63 | 2000 (1000) |
| Squin Fog (-61168, 15766) | 0.95, 0.77, 0.65 | 13500 (1000) |
| Canyonland central Fog (-111573, 92622) | 0.81, 0.61, 0.63 | 23500 (1000) |
| Desert01 (50221, -100513) | 0.68, 0.93, 1.00 | 50000 (1000) |
| Volcano01 (68973, 39202) | 0.36, 0.34, 0.34 | 7500 (1000) |
| Ashlands (98617, 111199) | 1, 1, 1 | 15500 (1000) |
| Skinners Roam Wall (-19784, -16986) | 0.86, 0.72, 0.63 | 11000 (1000) |
| SkiinersRoam Wall inner (-14629, -17375) | 0.90, 0.75, 0.65 | 1000 (500) |
| Lost Lands (83629, 6662) | 1.00, 0.81, 0.70 | 5500 (1000) |
| Lost Valley (79541, -5412) | 1.00, 0.81, 0.77 | 3000 (340) |
| Lost Bluffs (72097, 1197) | 1.00, 0.86, 0.77 | 6500 (310) |

(`#140805` is a MyGUI colour code in the label.) The `(x, z)` in a default label is the volume's position when it was made, and matches
where the block is.

**Shape (Verified, the data)**: almost every block's planes face upwards (normal y 0.9 to 1.0) with small tilts, plus two or three walls.
So a block is a low tent: a nearly flat ceiling that slopes down at its edges and is open below (the seven planes do not close it;
FogFeature.Corners finds corners far underground). The terrain closes it from below. Ceilings at the named points: Swamp_N-W-Mid about
930, Swamp[SOUTH] about 2140 (2900 over Shark), Swamp-W-Small about 670, Central Forestland about 2500. Shark (-47041, 48137) lies inside
Swamp[SOUTH], Swamp_N-W-Mid (ceiling 285 there) and Swamp-W-Small (499) (**Verified**, `FogFeatureTests`); Heft lies in "Desert01", a faint
pale-blue block over the Great Desert; The Hub's ground (about 325) lies under Central Forestland, Swamp[SOUTH] and SwampDesertpool
(ceilings about 1140 to 1240 there). The Fog Islands' fog comes from these volumes too (the `fog islands` weather lists an effect with count 0).

The sphere and the two beams (**Verified**: the install's file; positions rounded):

| Name (as stored) | Type | Colour | Distance (edge) | Shape |
|---|---|---|---|---|
| Skinners Fog Wall Extra#140805Sphere#140805 dome (-10159, -24…) | sphere | 1.00, 0.84, 0.72 | 13500 | centre (-13125, -3698, -24794), radius 10000 |
| Quarry (-47001, -56232) | beam | 1, 1, 1 | 2000 (1000) | (-46550, 1095, -56004) to (-46598, 1794, -56003), radius 7502.5: a flat disc |
| Hellish Cylinder (-113904, 62140) | beam | 1, 1, 1 | 7500 (1000) | (-113904, -3161, 62140) to (-113904, 2476, 62140), radius 10000: upright |

## How the game draws them

- **Objects** (Verified (decompiled)): class `FogVolume` (vtable: destructor, setDensity, setColour, setInside, contains), with
  `FogSphere`, `FogCylinder` (the beam) and `FogPlaneVolume`; `MainFog` (the weather fog's shared parameters) derives from it too. A
  `FogController` singleton keeps them (the constructors append themselves to its list). Each volume gets a material clone of `FogSphere` /
  `FogBeam` / `FogPlaneVolume` (post/fog.material, `scene_blend alpha_blend`, `depth_write off`), and the editor's ones a visibility flag
  0x1000 (**Observed**: whether the main camera's mask includes it was not checked; the volumes are visible in game).
- **Block mesh** (Verified (decompiled), FUN_14010b6a0): the corners where three planes meet inside the other four, one polygon per plane
  through its corners, render queue 82 (the vtable call at offset 0x50, `setRenderQueueGroup`, the slot the particle wrapper also calls,
  below). Queue 82 is drawn by the "Fog volumes and Particles" scene pass after the atmosphere haze quad (`main.compositor`: water 81 to 82,
  then the haze, then 82 to 85; with Ogre 2.x's exclusive `rq_last` the volumes are drawn once, after the haze; **Observed**).
- **Sphere mesh** (Verified (decompiled), FUN_14010e500; the mesh's bounds Verified with Meitou's mesh reader on the install's file):
  `unit_sphere.mesh` (radius 0.5) on a node at the centre scaled by 2 × radius, so the hull has the stored radius; queue 82 goes with the mesh
  name into the creation call (**Observed**: the 0x52 stored next to the name). `fog_sphere_vs` measures the sphere as 0.48 of the unscaled
  mesh, so the shader's sphere is **0.96 × the stored radius**, just inside the hull.
- **Beam mesh** (Verified (decompiled), FUN_14010e840, FUN_140109130): `cylinder.mesh` (a hexagonal prism: x ±0.5, z ±0.433, y 0 to 1) on a
  node at the start, turned so that its local y points at the end, scaled (2 × radius, length, 2 × radius). The constructor sets edge 300, which
  the loader then replaces with the file's. `fog_beam_vs` takes the node's y axis as the axis (its length the beam's) and 0.4 of the unscaled
  mesh across, so the shader's cylinder has **0.8 × the stored radius**.
- **Inside test every frame** (Verified (decompiled), FUN_140109720): for each volume, `contains(camera position, r + 0.1)` with r a camera
  value (**Observed**: presumably the near clip distance), then `setInside` (FUN_140109ad0): inside, culling mode 3 (`CULL_ANTICLOCKWISE`: the
  front faces are culled, the back faces drawn) and the depth check off, so the back faces cover the screen; outside, mode 2 (`CULL_CLOCKWISE`,
  the usual back-face culling) with the depth check (the numbers are Ogre's enum, **Observed** from the Ogre source). The sphere's `contains`
  (FUN_1401090c0) is `|camera − node position| < radius + r` with the node's *local* position (`Node::getPosition`): right for the file's
  spheres, whose nodes hang from the root, but not for an effect's (below). The cylinder's (FUN_140109260) checks that the camera projects onto
  the axis within r of its ends and lies within radius + r of it (**Observed**: the decompiler lost two operands; read as the nearest point on
  the axis). Nothing else per frame changes a volume: the setters are called only by the constructors, the loader, the editor and the effect
  fades below (**Observed**, direct calls in the call graph; virtual calls from the editor). No weather, season, region or time-of-day value
  reaches the file's volumes, except the light below.
- **Block shader** (`fog_planes_fs`, Verified: the shipped HLSL): per pixel, the ray from the eye and the G-buffer distance D (the far clip
  for the sky). For each plane the ray's crossing; planes facing the eye raise `near` (from 0), the others lower `far` (from D); `near` is
  capped at D. Edge softening: `edgeBlur × saturate(1 / (far × 0.00006))` (softer far away), and at the midpoint of the path the product over
  the seven planes of `saturate(distance inside the plane × edgeBlur)`, times `1 + |ray.y| × 0.9`. Alpha: the global fog's ease-in-out curve
  (`2a²` below 0.5, `1 − 2(a − 1)²` above) of `saturate((far − near) × density × edge)`; colour `colour × max(0.08, sunColour.w × 1.6 ×
  saturate(3 sunY + 0.2))` (sunColour.w the daylight scale, [lighting.md](lighting.md)). So by day the fog is 1.6 × its colour in the HDR
  scene, and at night it keeps 0.08 of it: faintly visible, unlike the haze, which goes black. With the eye inside (`near` 0) that is the
  result. With the eye outside, the volume's colour moves towards the haze colour at its near side (weight `saturate((near − 12000) ×
  0.0001)`: from 12000 to 22000 away), the haze's alpha scaled by the volume's, then the weather fog of `near` is laid over it, alpha
  `saturate(a + weather a) × volume a`. The result is alpha-blended over the hazed scene. A volume wholly beyond D adds nothing (`near`
  reaches D before `far` does).
- **Sphere shader** (`fog_sphere_fs`, Verified: the shipped HLSL): the ray's two crossings of the sphere (radius 0.96 r), each clamped to
  0..D. Beyond 10000 the far crossing is pulled in by `1000 × saturate(near / 10000 − 1) × (sin(0.004 y) + 1 − cos(0.0008 z) cos(0.0008 x) + 1)`,
  with x, y, z the world position of the pixel's hull fragment (the hull's near side when the eye is outside), which breaks a far sphere up.
  Alpha: the same curve of `saturate(path × density)`, times the colour's alpha; colour `colour × sunColour.w` (no 1.6, no 0.08 floor, no haze
  blending: the shader's own "ToDo"). A ray that misses the sphere has a zero (beyond 10000 a negative) path.
- **Beam shader** (`fog_beam_fs`, Verified: the shipped HLSL): the ray's two crossings of the infinite cylinder (radius 0.8 r round the axis),
  clamped to 0..D, then cut by the two end planes (perpendicular to the axis through the start and the end). End fade: at the middle of the
  path, its distance along the axis from the start and from the end, each times edgeBlur and saturated, multiplied, then `1 − (1 − e)²`.
  Alpha: the curve of `saturate(path × density × fade)` times the colour's alpha; colour `colour × sunColour.w`, as the sphere. The HLSL divides
  by `1 − (ray · axis)²` and by `ray · axis` unguarded.
- **Blending** (Verified (decompiled), FUN_140108f50, FUN_140109a60 and Ogre's `Pass::setSceneBlending`): the file's volumes keep colour alpha 1
  and the material's alpha blend (`SBT_TRANSPARENT_ALPHA`: source alpha, one minus source alpha). The effects' volumes below take the record's
  alpha (clamped to 0..1) as the colour's alpha, a volume at alpha 0 is hidden, and `additive colour` switches the pass to (source alpha, one).
- **Order** (Verified (decompiled), FUN_14010b6a0 and in `OgreMain_x64.dll` `SubEntity::getSquaredViewDepth` and `Node::getSquaredViewDepth`): all
  queue-82 volumes are transparent objects sorted back to front by the squared distance from the camera to their node (a sub-entity without
  extremity points asks its node): a sphere's centre, a beam's start, a block's corners' mean (the hull builder averages the corners and puts
  the node there), an effect's volume the effect's position plus the offset.
- **Why the swamp looks the way it does** (from the above): standing in Shark, the eye is inside Swamp[SOUTH] (density distance 4500) and
  usually Swamp_N-W-Mid (2000): things 2000 to 4000 away are mostly fog, the sky (D = 50000 but the path ends at the ceiling, ~2600 above,
  lengthened by `1 + |ray.y| × 0.9`) is covered, and the colour is the volumes' grey-brown × 1.6, desaturating everything behind it.

## The weather effects' fog volumes (Verified (decompiled), FUN_1400fb070 and the fades)

An EFFECT's `fog volumes` (EFFECT_FOG_VOLUME, [weather.md](weather.md#fog-volumes)) are the same objects: `type` 0 makes a `FogSphere` (the
constructor above: `unit_sphere.mesh`, queue 82, `fog_sphere_fs`) at `position x/y/z` with `radius` and the density from `distance`; `type` 1 a
`FogCylinder` from `position` to `position 2` with `radius` (edge 300, the constructor's). Then `colour` (ARGB, alpha forced to 1), `alpha` and
`additive colour` as above. The volume's node becomes a child of the effect's node, so it moves with the effect. Because the sphere's
`contains` reads that node's position relative to its parent, the camera is practically never "inside" an effect's sphere: its hull stays in the
outside state (back faces culled, depth tested), so **from inside a twister's dust ball the ball is not drawn** (Verified (decompiled) as code;
not seen in game).

Fades: when the effect starts, if its `fog fade in duration` is above 0, `FogFadeSphere` grows the sphere's radius linearly from 0 to its own and
brings the density distance linearly from 10 × the radius down to its own over that time; when the effect stops, `fog fade out duration` runs
the same backwards (from the current values). `FogFadeCylinder` does the same with the radius and from 4 × the density distance (the length
kept). The alpha does not fade. A fade of 0 seconds is skipped (the volume appears and goes at once).

## Particles are not fogged (Verified (decompiled) and the shipped materials)

The particle systems' wrapper (`ParticleSystemHandler`, constructor FUN_1404079c0) puts every effect's particle system in render queue 84
(`MOV DL, 0x54` before the call through vtable offset 0x50, `setRenderQueueGroup`, at 0x140407a9e). The fog volumes are in queue 82, so the
"Fog volumes and Particles" pass draws all volumes first and then all particles. The particle materials (`data/particles/materials`) use
`Basic_Coloured_Ambient_VP` / `Basic_Coloured_Texture_VP` with `Basic_Texture_FP` / `Basic_Texture_Clipped_FP_HLSL` (one uses
`Particle_Blend_Depth`), from `materials/forward/basic.hlsl` and `particles.hlsl`, which have no fog or haze term: texture × colour × vertex
colour, the vertex colour darkened by the sun's height. Particles are not in the G-buffer either, so no volume measures them. So rain, ash or
smoke inside or behind a fog volume are drawn over it unfogged, hidden only where the scene's depth is in front of them.

## Mods

The loader is called once, with the name `fogfeatures.dat`, and builds the whole list from the one file `openResource` returns from the
"Landscape" group (Verified (decompiled), FUN_14010f060 and its caller FUN_14086db80): a mod's file **replaces** the base list, it is never
added to it. Which copy wins follows Ogre's index for that group ([ogre-material.md](ogre-material.md#resource-locations)): the last-added
location in "Landscape" that has the name. A mod folder joins the group of the base folder with the same path, and for `newland/land` that
record ends as "Overlaymaps" (the folder is listed in both sections), so a mod's `newland/land/fogfeatures.dat` would be indexed in Overlaymaps
and the Landscape lookup would still find the base file (**Observed**: inferred from the documented rules, not tried in game). A copy in a folder
that maps to Landscape alone (such as the mod's `newland/land/textures`) would win. Where the level editor writes the file (FUN_14010c760 calls
`createResource` in "Landscape") was not traced: **Unknown**.

## Not known

- The camera value used for the inside test (above).
- Version 1 files (no names) were not seen.
- Where the level editor saves the file (above).

## In Meitou

`FogFeatures` (`Meitou.Data.World`) reads the file into `FogFeature` records (all three types; `Contains`, `Corners`, `SectionBounds` for
blocks). `FogVolumes` (`Meitou.Rendering`) turns every volume into a few vec4s: a block (10) its box between heights -1000 and its top,
edgeBlur, colour and density and the seven planes; a sphere (3) its centre, the shader radius 0.96 r and the hull radius r, alpha, additive,
colour and density; a beam (4) its start, unit axis and length, the shader radius 0.8 r, edgeBlur, alpha, additive, colour and density. Each
frame (`Update`, after the particles moved) it keeps every volume whose box lies within the game's far clip D (the haze's far distance, `uAtmoFog.w`) and inside a wedge round the
camera's horizontal direction as wide as the view's corner rays (the same in x, z for the water reflection's mirrored camera; off when the
camera looks nearly straight down), adds the weather effects' volumes (`ParticleRenderer.CollectFogVolumes`, with the game's fades as size and
density, `FogVolumes.Faded`, and an effect's sphere left out while the eye is inside it, as the game), sorts them by the distance to
their game node (above), and packs them farthest first into `uFogVolumeData` (512 vec4s in the frame block: 51 blocks, so all 28 of the base
game and dozens of effect spheres fit; if more are in view the farthest are left out and counted). `uFogVolumeEye` holds the eye and the
blocks' light, `uFogVolumeInfo` the vec4s in use and `sunColour.w` for the spheres and beams. `FogVolumeShaders` holds the three shaders'
formulas as GLSL, and `fogVolumesAccumulate`, which runs the list farthest first and returns it as one affine map of whatever is behind
(`colour * transmittance + add`: an alpha blend is `c (1 - a) + fog a`, an additive one `c + fog a`, and a chain of those composes).

**One full-screen pass, as the game** (changed 2026-10-08; before, `atmoApply` in every world shader evaluated the list per fragment, so
overdrawn foliage paid it several times and the water reflection paid it again). The volumes are no longer in the world shaders: `atmoApply`
is the haze and the weather's fog alone, the sky shader has no volume line, and the reflection pass draws none (the game's `Water_Reflection`
draws render queues up to 60 and the volumes are queue 82, [post-processing.md](post-processing.md)); with the Meitou water a pass of its own
lays them over the finished reflection along the reflected rays (2026-10-10, [../render-water.md](../render-water.md) "Fog in the reflection"),
and the Faithful water keeps the game's unfogged mirror. `PostProcess.RunFogVolumes` runs
`PostProcessShaders.FogVolumes` once, after the opaque scene, the water and the sky (the haze is still in the shaders, so the volumes go over
the hazed colour, the game's order) and before the particles (which `WorldFrame` now draws after it, in their own scene rendering, still
depth-tested against the near slice). Each pixel's offset is rebuilt from the depth as SSAO does (view depth from the near slice's depth,
else the far slice's, along the pixel's ray; no depth: the sky, distance D). The far slice has a depth buffer of its own in every mode now
(it was only kept with an upscaler), because the single pass needs it; the slices are separate scene renderings (they were one without an
upscaler). The shader writes (add, transmittance) and the hardware blends `scene * transmittance + add` (blend factors one and source
alpha), with the colour mask red, green and blue, so the scene colour's alpha (the characters' SSAO mask) is untouched. The distance is capped at D, as the game's G-buffer depth is (Meitou draws terrain beyond D; the game does not), so a volume beyond D adds nothing and is left out exactly.
**Water**: Meitou's water is blended without writing depth (the same fact the velocity pass uses), so the depth under it is the seabed's. The pass
pulls the pixel in to the water plane when the ray crosses it before the scene point (`uWaterY`, the water's height, none when the water is
off), which is where the water shader fogged its own surface; so a pixel is fogged once, at the water's distance, the seabed behind
translucent shallow water included (before, the water layer was fogged at the plane and the seabed behind it at its own distance and the two blended
by the water's alpha; the two agree except in shallow water where the distances differ). The pass sees no edge of the water quad (it reaches past the far clip).
**SSAO** still fades with the volumes: `airVisibility` is the haze's transmittance (`atmoApply(1) - atmoApply(0)`) times `fogVolumesTransmittance`.
**Upscalers**: the pass runs on the render-size scene before TAA / FSR / DLSS, so the volumes are in the colour they resolve; it ignores the
projection's sub-pixel jitter (its ray is the pixel's centre; the fog is smooth, the depth edges are what the upscaler's history handles), and
touches neither the motion vectors, the depth nor the reactive mask. The fog cull is unchanged (`FogVolumes.Hidden` is asked by the same draws).
The data are uniform-buffer reads, the same for every pixel; a block whose box the pixel's ray misses costs one box test. The viewer prints the volumes drawn (`fog vols` line) with
`--screenshot`; `--no-fog-volumes` turns them off (the pass is skipped when no volume is in view). The "post cost" line has `scene` (up to the
water), `fog` (this pass) and `particles`.

Cost of the pass (**Observed**, 2026-10-08, same card, best of several runs because other sessions share the GPU, interleaved with a build from before the change): the pass is 0.14 ms at Shark in swamp rain and 0.11 ms at The Hub; Shark scene 7.54 to 5.87 + 0.14 (+ 0.02 particles), foliage 4.51 to 3.47, the sky pass 0.17 to 0.05 ms, the reflection 0.32 to 0.25 ms; the Hub scene 3.32 to 3.13 + 0.11. Before it, with per-fragment volumes (**Observed**, 2026-10-08, RTX 4070, 1600 × 900, `--screenshot` "post cost" scene GPU ms; without volumes / the earlier 8-block
texture version / the per-fragment list): Shark in swamp rain 6.34 / 8.22 / 7.80, Shark from above 5.64 / 7.38 / 7.27, The Hub 2.62 / 3.82 / 3.35, the Vain
3.43 / 4.83 / 4.28, inside the Skinner's Roam dome 2.80 / 3.82 / 3.64. So drawing every volume (and the sphere and beams) costs less than the
capped version: the far-clip cull drops what cannot show, and the data are uniform reads instead of texture fetches.

**Fog cull** (Meitou's own optimisation; the image does not change; `--no-fog-cull` turns it off). In dense fog, everything beyond a few
thousand units is fully hidden, so the main camera does not draw it. Each frame `FogVolumes.Update` takes the volume the game draws last
(nearest by its node, so everything sorted before it is covered by it) and, if that is a block the eye is inside (all seven planes with a
margin, and its box), computes a *hide distance* R. `FogVolumes.Hidden(min, max, kind)` then says a box is hidden when it lies wholly inside
that block (the 7 plane tests of its extreme corners, plus its box; the block is convex) and its nearest point is at least R from the eye.
Terrain nodes, object instances (and distant towns), characters (colour and motion passes), foliage zones and each foliage instance
(below) ask it (grass is range-limited well inside R and is not asked). Not asked: the shadow cascades (a hidden caster can still throw its shadow on visible ground) and the
water reflection (a different camera); effect spheres and beams are ignored (they only add fog); the sky is not culled (see below). It is a
per-frame decision and is off the moment the eye leaves the block or a different volume is drawn last.

The bound (**Verified** by `FogCullTests`, which draws random eyes and boxes and checks the shader's own formula for rays to points of every box it
hides, to the far clip and to points behind it). Eye E inside the block, a ray from E to a point X inside the block (a point of the hidden box,
or the exit point of a ray that runs on to terrain, water or the sky behind it), path L = |X - E| at least the box's distance. The shader's near
is 0 (the eye is inside), far = L. Its alpha is the ease-in-out curve of `L * density * edge`, and edge is at least the product over the planes of
`saturate(blur(L) * inside_i / 2)`: dist_i is linear along the ray, dist_i(X) is at least 0 (X is in the block), so at the midpoint it is at least
`(inside_i + 0) / 2` with inside_i the eye's distance inside plane i; blur(L) = edgeBlur * saturate(1 / (L * 0.00006)); and the `1 + |ray.y| 0.9` factor
is at least 1. F(L) = L * density * that product is a power of L between its breakpoints (L = 16667, where the blur clamp ends, and
`edgeBlur * inside_i / 2 / 0.00006`, where plane i's saturate leaves 1), so its least value over [R, D] is at R, at D or at a breakpoint; R is the
least value for which that reaches 0.99 (curve 0.9998), by bisection, and no cull when it is not below the far clip D (the sky behind is drawn at
D). Everything behind a hidden box, along the same ray, is therefore covered by the same fog colour at 0.9998 alpha, which is why leaving it out
changes nothing: the pixel shows the sky, water or terrain behind it, whose own fog is the same block's colour. This needs the block to be the one
drawn last, as a volume drawn after it would see a different path for the (now missing) fragment and for the one behind it. Volumes drawn before
it cannot show through 0.9998. At Shark inside "Swamp[SOUTH]" (density distance 4500, ceiling about 2900) R is about 4455 units in a clear eye;
with the eye near a ceiling or a wall, or the far clip over 50000 shrinking the soft edges, R grows (up to "no cull").

Foliage per instance (**Verified** by `Gpu_cull_leaves_out_what_the_fog_hides_as_the_CPU_does`, which runs the kernel against the C# test bit for bit
on random blocks, eyes and spheres, a seventh of them within a dozen units of R). A swamp's foliage zone is big, and the zone test (its box with
the margin of its largest mesh) almost never lies wholly inside the block, so it culled none at Shark; the instances are what matter (trees,
leaves and rocks, the largest GPU cost of that view). The foliage cull kernel (`FoliageShaders.CullCompute`) now also runs `FogHidden` on each
instance that survived its range, frustum and fade tests: a port of `FogVolumes.Covers` on the instance's bounding sphere's box (centre ± radius,
the one the frustum test uses). The main colour views' dispatches (every depth slice) get the block's box, its 7 planes, the eye and R squared in
the `View` buffer (`FogVolumes.WriteCull`, 10 vec4s, `mode.y` = on); the shadow cascades' and the reflection's do not (`mode.y` = 0, and
`FogCull` is only set around the main camera's draw). It is the same test for the meshes, the TERRAIN-mode rocks and the impostors (distant trees:
same instance, same sphere; the quad's silhouette is the sphere's out here). An instance the test drops writes -2 as any other not drawn, so the
compaction and the indirect arguments need no change. The kernel counts those it dropped per chunk, the scan sums them into an extra entry of the
offsets, and the total is read back a frame ring late (summed over the frame's fog dispatches, whose number varies with the cascades) into the
`fog cull` line's "foliage instances"; the CPU path (`MEITOU_GPU_CULL=0`) tests the same in `FoliageCull.CullGroup` and counts the same way.
Measured (**Observed**, 2026-10-08, `--fly-benchmark 300 --fly-radius 1 --fly-speed 0` with `MEITOU_FOLIAGE_TRIS=1 MEITOU_PASS_STATS=1`, on / off
`--no-fog-cull` as interleaved pairs, RTX 4070 shared with other viewers): Shark in swamp rain `--distance 400 --pitch 4`: 3953 of 20357 foliage
instances left out of the main view (4453 of 20500 at `--distance 1500 --pitch 8`), colour-view triangles drawn meshes 727k / 1015k, rocks 2651k /
2968k, impostors 19.6k / 24.8k; the foliage pass's GPU time 2.62-2.69 / 2.92-2.93 ms (three pairs, 0.3 ms), the frame's GPU time 6.2-6.5 / 6.57 ms.
At `--distance 1500 --pitch 8` (a busier GPU: only the pairs compare) 6.0-6.3 / 6.8-7.0 ms for the foliage pass, a tenth of the pass and a larger
share of the frame. Images: identical to at most 4 levels on 7 pixels (thin grass blades near the camera; 3921 pixels differ by 1 from the exposure
and the TAA history seeing a different set of hidden fragments), where the zone-level cull alone differed by at most 1; the views without the block
are unchanged (the ten parity views against `weather`: 0, except The Hub at 13:00, whose own runs differ by up to 7 between two renders).

Measured (**Observed**, 2026-10-08, RTX 4070 shared with other viewers, so only the pairs are comparable, `--screenshot` "post cost" scene GPU
ms with / without `--no-fog-cull`; images identical to at most 3 levels on 3 pixels): Shark `--distance 400 --pitch 4` 6.6 / 10.0 (11 terrain
nodes and 35 objects left out; object triangles 273k / 403k), the same in swamp rain 10.8 / 13.9, a flat view south 7.4 / 8.1, west 6.6 / 7.7 (95
objects left out). The streamed terrain around a `--town` focus is only about 7000 units wide, so the saving in the real game, which loads a
larger area, would be larger. The Fog Islands, the Vain and Skinner's Roam inner wall: no cull (the drawn-last block is thinner than the far clip
allows, or the objects there are not wholly inside it), so no change. The sky is not culled: its rays go up through a ceiling a few thousand units
over the eye, so their path is shorter than R except near the horizon.

**Fog cull by direction** (Meitou's own, 2026-10-09; `FogVolumes.HideDistanceByDirection`; `--ab fog-direction` compares it with the plain bound alone).
The plain bound above takes every plane at its worst (inside_i / 2) at once and every path out to the far clip. Meitou's far clip is 50000, and
there the soft edges thin out (blur falls as 1 / L), so at Shark the plain bound finds no distance at all. Seen from the bench camera
(`--view swamp`, eye 1121 high), the curve input it can promise at 50000 is about 0.83 against the 0.99 needed, so the whole cull was off.
- **Why a tighter bound exists.** "Swamp[SOUTH]"'s seven planes are all tilted roof facets (normals 0.96 to 0.995 up). Along a unit ray d the
  shader's path is L = min(fragment, exit T(d), far), and plane i's distance at the path's middle is exactly inside_i - L dn_i / 2 (dn_i = n_i . d).
  That is at least inside_i / 2 only for the plane the ray leaves by, at least inside_i for the planes it moves away from, and in between for the
  rest. Only near-horizontal rays run long, and they leave by one facet.
- **The bound.** A branch and bound over ray directions (cube-map cells: centre c, chord radius delta from the corners, since the cell is the cone of
  its corners) and path intervals [L0, L1]:
  - dn_i is within |n_i| delta of n_i . c, and |d.y| is at least |c.y| - delta.
  - So the curve input is at least L0 density (1 + 0.9 max(|c.y| - delta, 0)) prod clamp(blur(L1) lower_i), where lower_i is inside_i for dn_i <= 0
    and max(inside_i - L1 dn_i,max / 2, inside_i / 2) otherwise.
  - A cell is dropped when that reaches 0.9905 (0.99 plus room for the shader's float), or when no path of it ends inside the block (L0 dn_i > inside_i
    across the cell).
  - The rest are split, path or directions (whichever moves the middle point more), longest paths first. The first cell that is too small to split
    (2 % of L1) and not proven bounds R by L1 + 1. Nothing is promised when that reaches the far clip.
  - Each solve allows for the eye anywhere within 300 units (inside_i lowered by 300 for the fog, raised for the exits), so it is solved again only
    after the eye has moved that far. That happens on a background task; until it lands only the plain bound applies.
  - The frame uses the smaller of the two proven distances. The box test, the GPU kernel and what is asked are unchanged.
- **Verified** by `FogCullDirectionTests`. Random eyes in the swamp block and the wide test block, then random eyes within the 300 margin of each
  solve, random directions (half of them near the horizon) and fragments from R to the far clip: the shader's own formula gives at least 0.9998 on
  every one. `FogCullTests`' box test now runs with both bounds. Sampling the formula along 400000 directions from the bench eye finds failing
  paths up to 4453 to 4454 units; the bound proves 4492 (about 1 ms per solve, about 1500 cells).
- **Measured** (**Observed**, RTX 4070, `--view swamp` orbiting Shark, 1920x1080 DLSS native, 600 frames, `--ab fog-direction`): 6.10 against
  6.81 ms per frame, 164 against 147 fps. GPU: foliage 1.19 against 1.48, terrain 1.01 against 1.14, objects 0.68 against 0.71 ms.
  - The same still (`--bench-motion still`): 3 pixels differ by 4 levels or more, max 5, mean 0.055, the TAA and exposure noise the cull always had.
  - Not measured: other blocks (the Fog Islands, the Vain), where the plain bound already gave no cull.

**Weather fog cull** (Meitou's own, 2026-10-08; the image does not change to within a few levels; `--no-fog-cull` turns it off with the block cull). The same
`Hidden` / `Covers` also ask a second, simpler question: is the box's nearest point at least `D` from the eye, where `D` is the distance past which the
*weather's* fog (`SkyRenderer.FogCullDistance`, from the uniforms `atmoApply` reads) makes the pixel its own colour. Terrain nodes, objects, characters, foliage
zones and the foliage cull kernel (`FogHidden`: `fog[0].w` = D squared, `fog[1].w` = 1 when the eye's block is there too) use it in the main camera's
colour pass; the sea, the particles and the reflection do not. **Verified** (reading `AtmosphereShaders.atmoKenshiHaze` / `atmoPhysicalHaze` and the sky
pass's fog term, `FogCullTests`): the Kenshi haze returns `mix(colour, rgb, alpha)` with `alpha = saturate(level + curve)`, where `level` (the haze ramp)
and `curve` (the weather's ease-in-out of `dist / fog distance`, times its weight) depend on the distance alone, so there is no vertical dependence; the
ray direction only enters `rgb`. `alpha = 1` alone (the haze, complete at 0.6 of the far clip) is **not** enough to leave a surface out: what stays is the
haze colour `rgb`, which varies with the distance below the dome radius (70000) and is not the sky pass's colour, so a revealed sky pixel would differ. Only
the weather's own term at `curve = 1` makes `rgb` the fog colour and `alpha = 1`; the sky pass applies the same curve at the far clip (its distance, since a
pixel with no geometry is that far), so the pixel is the fog colour whether the surface is drawn or not. That needs: the game's sky and light (the simple
sky's fog never exceeds 0.9), the fog on at weight at least 0.9998 (a smaller weight leaves part of the surface, and `FogInput` blends in and out), and the
sky pass's curve complete at the far clip (fog distance at most about the far clip D = 50000; the base weathers with 190000 give none). D is where the
curve reaches 0.9998 (0.99 of the fog distance, which also gives a transmittance of 0.0002, under 1/255 in every channel); with the physical haze (a viewer
choice, `Altitude.X` = 1) where the linear ramp is complete, and the larger of the two when they are blended. A *placed* fog volume in view that is not wholly
inside D (`FogVolumes.VolumesWithin`) switches it off: the volumes pass reads the scene's depth, and a surface left out (the sky at the far clip in its
place) would change the length of the ray's path through the volume. So it works in the open, where the base game has weather fog: the swamp's weathers have
the fog off (their fog is the placed volumes, the block cull above), so the Shark swamp benchmark is unchanged by it. The F11 / `--screenshot` `fog cull` line
names it ("weather fog beyond 14850: ...").
In Meitou shadows the cascades' far distance is clamped to D (`ShadowPass.RangeCap`, one line in `EffectiveRange`; nothing beyond D shows, so the receivers

**Shading rate over the fog** (`--fog-vrs`, [render-post.md](../render-post.md), "Fog shading rate"): the post fog's own alpha is what sets it. A pass after each frame's scene evaluates `fogVolumesAccumulate` (the function above, unchanged) along the ray to the nearest surface of each 16 x 16 tile and a margin, together with the haze and the weather's fog, and the next frame's opaque scene passes shade 2 x 2 or 4 x 4 pixels with one fragment where at least 0.8 or 0.97 of the surface is hidden. **Observed** on the swamp: 28 % of the tiles coarse at the end of the orbit, GPU total -0.31 +-0.15 ms at 1920x1080 native DLAA.
there are not needed): it moves the cascade splits, so shadows near the camera change slightly (finer texels); that makes it Meitou's `shadows` switch only
(`--faithful shadows` keeps the game's cascades whole). Measured (**Observed**, 2026-10-08, RTX 4070 shared, 1280 x 720 with DLSS, `--world --town "The Hub"
--radius 2 --distance 9000 --pitch 10 --yaw 300 --weather "shek desert storm" --time 12 --no-fog-volumes --faithful shadows`; fog distance 15000, so D = 14850): 20
terrain nodes, 111 objects, 95 foliage zones and 1181 foliage instances left out; the picture differs from the `--no-fog-cull` one by at most 4 levels (mean 0.005 of 255), where two
runs of the same setting are identical. With Meitou's shadows the pictures differ near the camera (mean 0.17, 0.33 % of the pixels over 12 levels, in the
shadow edges only).

Differences from the game (**Observed**, viewer choices):
- Evaluated per pixel in each shader instead of rasterising each hull over the G-buffer. Opaque surfaces get the same result, but the game
  only fogs rays that hit a block's hull (the corners' convex shape), while Meitou fogs the whole region inside the planes; they differ only for
  rays that cross the region outside the hull, which in the base game is underground (the blocks are open below). The sphere's break-up
  pattern samples the hull's near side computed from the ray (the game's rasterised fragment).
- The view culling (D, wedge) and the 512-vec4 list are the viewer's; the game submits every volume and lets Ogre cull it. A volume
  outside the view adds nothing, so the result is the same while the list is not full.
- The game's beam formula divides by zero for a ray along the axis; Meitou clamps those divisors.
- An effect's fade-out starts from full size in Meitou (the fraction is fade-in × fade-out), where the game starts it from the current values; they
  differ only for an effect stopped while still fading in.
- With the simple sky (`--simple-sky`, `B`) the volumes are off.
