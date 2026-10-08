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
beam FUN_14010e840, the hull builder FUN_14010b6a0, the per-frame inside test FUN_140109720 with FUN_140109370 / FUN_140109ad0.

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

## How the game draws them

- **Objects** (Verified (decompiled)): class `FogVolume` (vtable: destructor, setDensity, setColour, setInside, contains), with
  `FogSphere`, `FogCylinder` (the beam) and `FogPlaneVolume`; `MainFog` (the weather fog's shared parameters) derives from it too. A
  `FogController` singleton keeps them. Each volume gets a material clone of `FogSphere` / `FogBeam` / `FogPlaneVolume` (post/fog.material,
  `scene_blend alpha_blend`, `depth_write off`), and the editor's ones a visibility flag 0x1000 (**Observed**: whether the main camera's mask
  includes it was not checked; the volumes are visible in game).
- **Block mesh** (Verified (decompiled), FUN_14010b6a0): the corners where three planes meet inside the other four, one polygon per plane
  through its corners, render queue 82 (**Observed**: the call is through a vtable slot read as `setRenderQueueGroup`). Queue 82 is drawn by
  the "Fog volumes and Particles" scene pass after the atmosphere haze quad (`main.compositor`: water 81 to 82, then the haze, then 82 to
  85; with Ogre 2.x's exclusive `rq_last` the volumes are drawn once, after the haze; **Observed**).
- **Inside test every frame** (Verified (decompiled), FUN_140109720): for each volume, `contains(sphere(camera position, r + 0.1))` with r
  a camera value (**Observed**: presumably the near clip distance), then `setInside`: inside, the pass culls the other faces and turns the
  depth check off (so the back faces cover the screen); outside, front faces with the depth test. Nothing else per frame changes a volume:
  the setters are called only by the constructors, the loader and the editor (**Observed**, direct calls in the call graph; virtual calls
  from the editor). No weather, season, region or time-of-day value reaches them, except the light below.
- **Block shader** (`fog_planes_fs`, Verified: the shipped HLSL): per pixel, the ray from the eye and the G-buffer distance D (the far clip
  for the sky). For each plane the ray's crossing; planes facing the eye raise `near` (from 0), the others lower `far` (from D); `near` is
  capped at D. Edge softening: `edgeBlur × saturate(1 / (far × 0.00006))` (softer far away), and at the midpoint of the path the product over
  the seven planes of `saturate(distance inside the plane × edgeBlur)`, times `1 + |ray.y| × 0.9`. Alpha: the global fog's ease-in-out curve
  (`2a²` below 0.5, `1 − 2(a − 1)²` above) of `saturate((far − near) × density × edge)`; colour `colour × max(0.08, sunColour.w × 1.6 ×
  saturate(3 sunY + 0.2))` (sunColour.w the daylight scale, [lighting.md](lighting.md)). So by day the fog is 1.6 × its colour in the HDR
  scene, and at night it keeps 0.08 of it: faintly visible, unlike the haze, which goes black. With the eye inside (`near` 0) that is the
  result. With the eye outside, the volume's colour moves towards the haze colour at its near side (weight `saturate((near − 12000) ×
  0.0001)`: from 12000 to 22000 away), the haze's alpha scaled by the volume's, then the weather fog of `near` is laid over it, alpha
  `saturate(a + weather a) × volume a`. The result is alpha-blended over the hazed scene.
- **Sphere and beam shaders** (`fog_sphere_fs`, `fog_beam_fs`, Verified: the shipped HLSL): the path through the sphere (its far end pulled
  in by a slow sine pattern once the near side is beyond 10000) or the capped cylinder with an end fade, same curve and colour × `sunColour.w` (no 1.6,
  no 0.08 floor, no haze blending).
- **Why the swamp looks the way it does** (from the above): standing in Shark, the eye is inside Swamp[SOUTH] (density distance 4500) and
  usually Swamp_N-W-Mid (2000): things 2000 to 4000 away are mostly fog, the sky (D = 50000 but the path ends at the ceiling, ~2600 above,
  lengthened by `1 + |ray.y| × 0.9`) is covered, and the colour is the volumes' grey-brown × 1.6, desaturating everything behind it.

## Not known

- Whether a mod's `fogfeatures.dat` replaces the base file or adds to it (**Unknown**: the loader opens one resource by name from the
  "Landscape" group; how that group orders mod folders was not traced). The level editor saves the whole list into the active mod.
- The camera value used for the inside test's sphere (above), and which faces the game culls in each state (the mode numbers were not
  mapped to Ogre's enum).
- Version 1 files (no names) were not seen.

## In Meitou

`FogFeatures` (`Meitou.Data.World`) reads the file into `FogFeature` records (all three types; `Contains`, `Corners`, `SectionBounds` for
blocks). `FogVolumes` (`Meitou.Rendering`) uploads every block as one row of an 11-texel-wide RGBA32F texture (`uFogVolumes`: the seven
planes as (normal, w), (colour, density), (edgeBlur, 0, 0, 0), and a box round the block between heights -1000 and its top, which the
shader tests first so that rays missing the block cost two fetches), and each frame picks up to 8 blocks whose box lies within 30000 of
the eye in x, z, nearest first, drawn farthest first (`uFogVolumeSelect0/1`, 1 + the row, 0 ends the list), with the eye and the light
(`uFogVolumeEye`). Past that range a volume's near side is beyond 30000, where the game's formula gives it the haze's colour with the
haze's alpha and the haze is already complete, so leaving it out changes almost nothing. `FogVolumeShaders` holds the block shader's formula as GLSL; `atmoApply` (the haze every world shader
ends with) now applies it after the haze with the shader's own point and distance, and the sky pass with the haze's far distance D. The
viewer prints the picked volumes (`fog vols` line) with `--screenshot`; `--no-fog-volumes` turns them off.

Differences from the game (**Observed**, viewer choices):
- Evaluated per pixel in each shader instead of rasterising the block's hull over the G-buffer. Opaque surfaces get the same result, but
  the game only fogs rays that hit the hull (the corners' convex shape), while Meitou fogs the whole region inside the planes; they differ
  only for rays that cross the region outside the hull, which in the base game is underground (the blocks are open below).
- The selection (at most 8, within 30000) and the box test are the viewer's; the game draws all of them, sorted by Ogre's transparent order (distance to the
  node, which sits at the corners' mean; Meitou sorts by the cross-section's centre).
- Spheres and beams are not drawn (none in the swamp). The EFFECT fog volumes of twisters ([weather.md](weather.md#fog-volumes)) keep
  their own stand-in in the particle pass; the game draws them with `fog_sphere_fs` too.
- Particles are not fogged by the volumes (in the game they share queue 82 to 85 with them; their order is Unknown).
- With the simple sky (`--simple-sky`, `B`) the volumes are off.
