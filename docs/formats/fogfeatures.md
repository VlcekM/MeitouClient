# Fog volumes

Original `fogfeatures.dat` and weather-volume research. **Verified** file parser consumes install
exactly and matches FUN_14010f060; shaders/materials and binary constructors define drawing.
The retained table lists all28 surveyed volumes. Meitou evaluates volumes in a fullscreen pass;
[weather](weather.md), [sky](sky.md) own other fog, [shafts](../render-shafts.md) shadows it.

## Binary and geometry

Little-endian FF01/FF02, count; FF02 name=u8 length+bytes. Unknown magic treated as v0.
Type2 sphere,3 beam,4 seven-plane block; RGBA floats (file alpha ignored), density=1/distance,
nonpositive distance→1. Fields/order below. **Verified** inside block is n·p<-d for all7 planes;
GPU plane.w=-d. Wrong sign moves blocks across the world. Sphere shader radius0.96*stored,
beam0.8*stored despite full hulls. Cylinder start/end define axis/length, edgeBlur=1/edge.

## Optical evaluation

**Verified shaders** clip eye ray interval near/far to scene distance D; sky uses far clip.
Ease curve E(a)=2a² below0.5,1-2(a-1)² above. Block edge at interval midpoint multiplies
saturate(plane-depth*edgeBlur*saturate(1/(far*0.00006))) over7 planes, then1+0.9*abs(rayY).
Alpha=E(saturate(path*density*edge)). Block colour scales max(0.08,daylight*1.6*saturate(3sunY+0.2));
outside it blends toward near-side haze over12000..22000, then overlays near weather fog.
Volume wholly beyond scene contributes nothing.

Sphere alpha=E(saturate(path*density))*recordAlpha, colour*daylight (no block night floor/haze).
Far crossing beyond10000 is modulated by world-position sine/cosine break-up. Beam clips infinite
cylinder by end planes; end fade=1-(1-e)^2 from midpoint end distances; alpha includes fade.
Original beam divides by1-(ray·axis)^2 and ray·axis without guards; handle parallel rays explicitly
in our reader/renderer without assigning false original guarantees.

## Order, effects and limits

**Verified/Observed scoped** queue82 volumes follow atmosphere haze, sorted back-to-front by node
squared distance (sphere centre, beam start, block mean corners). Inside hull switches culling
and depth test. Static volumes are not weather-controlled: swamp/Fog-Islands/Vain haze persists.
Effects can use sphere/cylinder offsets, alpha/additive mode and lifetime fades; record alpha
clamps0..1, hidden at0. Sphere radius grows/fog-distance drops from10r during fade, cylinder4*own
distance; colour alpha unchanged. Effects' local-node contains test and inside-camera visibility
have special cases. Original weather particles are not automatically fogged like opaque geometry.

Meitou ray-clips and blends placed/effect volumes in matching order, with near/far depth and
world coordinates. Fog hide distances used for culling/shadows must be conservative in all view
directions and include colour/additive cases. Effect ground movement, exact inside-beam behaviour,
mod registration and uncertain visibility flags remain scoped in source evidence/unknowns.

### File layout (Verified: FUN_14010f060, and a scratch parser that consumes the install's file exactly, 2026-10-08)

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

### The base game's volumes (Verified: the install's file)

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

The sphere and the two beams (**Verified**: the install's file; positions rounded):

| Name (as stored) | Type | Colour | Distance (edge) | Shape |
|---|---|---|---|---|
| Skinners Fog Wall Extra#140805Sphere#140805 dome (-10159, -24…) | sphere | 1.00, 0.84, 0.72 | 13500 | centre (-13125, -3698, -24794), radius 10000 |
| Quarry (-47001, -56232) | beam | 1, 1, 1 | 2000 (1000) | (-46550, 1095, -56004) to (-46598, 1794, -56003), radius 7502.5: a flat disc |
| Hellish Cylinder (-113904, 62140) | beam | 1, 1, 1 | 7500 (1000) | (-113904, -3161, 62140) to (-113904, 2476, 62140), radius 10000: upright |

## Expression reference

- File layout: `density = 1 / distance`; `edgeBlur = 1 / edge`; `d' = -constant`; `normal · p + d = 0`; `planes[i] = (normal, -d')`; `w = -d`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- File layout (Verified: FUN_14010f060, and a scratch parser that consumes the install's file exactly, 2026-10-08)
- The base game's volumes (Verified: the install's file)
- The weather effects' fog volumes (Verified (decompiled), FUN_1400fb070 and the fades)
- Particles are not fogged (Verified (decompiled) and the shipped materials)

## Not known

- The camera value used for the inside test (above).
- Version 1 files (no names) were not seen.
- Where the level editor saves the file (above). Meitou has no level or fog editor (`FUN_14010d8d0` FogEditor window; **Verified** it exists, not ported).
