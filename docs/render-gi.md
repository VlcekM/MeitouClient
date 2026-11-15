# Global illumination

Meitou DDGI implementation: `Gi/GiScene.cs`, `AccelerationStructure.cs`, `GiProbes.cs`,
`GiFogHide.cs`, `GiResolveShaders.cs`, `GiShaders`; PostProcess resolve/apply. Engine choices,
not original-game lighting rules. Original ambient: [formats/lighting.md](formats/lighting.md).

## Device and switch

Ray-query setup is requested by default unless `--no-gi`; unsupported hardware prints GI off.
The `gi` Enhancement/`--faithful gi` selects probe lighting versus unchanged flat ambient.
Graphics > Lighting > **Indoor GI** (`interiorgi`, default on) switches room surfaces, indoor
furniture and characters between GI and the original ambient; it requires **Indirect light**.
The preference is saved with the other graphics toggles and applies immediately. Bypassing
indoor GI leaves outdoor sampling, probe updates and direct sun/lamps unchanged.
Require acceleration_structure/ray_query/deferred_host_operations and core buffer-device-address.
RayInput buffers carry address/build-input usage; no copied geometry required. Compile GLSL460
ray-query shaders with explicit descriptor bindings. The feature is optional for device support.

## Scene and lifetime

Fine terrain: 512² quads 8 apart (4096 wide) through a Catmull-Rom surface over the heightmap's 18-unit samples (`HeightSnapshot.SmoothHeightAt`; the drawn ground curves its edges); coarse: 256² quads 160 apart (40960 wide), bilinear. Heights sampled on workers.
Object range6000; resident finest mesh parts build bottom levels, max32/frame; unused600 frames
can retire. Foliage radius>=150, range4000, max14336 instances, cached candidates invalidate by
movement/deletion/build progress. Probes see no grass, characters or water (rays mask `0x0F`; the grass and small foliage the traced sun and the lamp shadows add sit under masks of their own: [Traced sun](render-shadows.md#traced-sun)).
Origin tracks eye in64-unit steps with1024-unit hysteresis; lazy scene epochs coordinate commits.
Shared top level fits16384 instances, prefers fast trace, reuses identical instance data; with characters in it (lamp shadows) it is refitted each frame, built again every 30.
Terrain refits unchanged topology (periodic full rebuild); retire structures after GPU consumers.

Terrain rays cull back faces; objects/foliage are double-sided. **Verified** RayQueryTests winding.
Structure geometry records and hit-fetch addresses must refresh when mesh buffers are replaced;
source geometry lifetime and built acceleration-structure lifetime differ. Hit records carry
vertex/index addresses/stride/kind plus texture indices/cutout threshold. Alpha-tested foliage
uses candidate-hit coverage; null/unresident textures need valid fallback. Lamps add diffuse
light at hits, enabling bounce lighting.

## Probes

Two toroidal cascades,32x32 columns of8 probes: horizontal/vertical spacing128/96 and512/384,
ray lengths4000/16000. With the camera target in/beside a viewed interior, the near grid uses32/24
spacing around it (1024 across) and follows its height in96-unit steps; the far grid stays unchanged.
Only under the building's cover (`InteriorVolume.Covered`: a floor, stairs or unwalkable-roof triangle
straight above, from 5 units up) and never on a construction site (ruins being rebuilt included): the
hull's height range holds walkable roofs (Hub bar terrace at +71, **Verified** by a probe: contained,
not covered; the bar room covered). The GI traces no construction-site instances (drawn clipped).
The near grid fades to the far cascade above its top layer, rather than extending that layer's light upwards.
Ground bases use five height samples, updated incrementally on scroll and resampled on density/height
changes. Invalidate relocated tiles before sampling; reset history for changed positions.
Irradiance:8² octahedral texels plus border RGBA16F (alpha valid); distance moments:16² plus
border RG16F. Seam borders copy opposite edges.

32 spherical-Fibonacci rays/probe with rotated directions (64 for comparison). Misses sample
sky ambient; hits combine albedo, shadowed sun, lamp light and previous probe irradiance.
Interior lamps with loaded owner masks cast visibility rays at bounce hits; furniture/walls
between a hit and the lamp block that source, while openings can let light outside. [Lamps](render-lights.md).
Negative backface distances mark solid occupancy; >=25% backface rays lowers probe validity.
Blend validity with the irradiance's hysteresis: noisy occupancy cannot toggle a probe's full
contribution each update. A moved/new probe starts without history; persistently invalid probes
fade to zero (snap below0.001). Cosine-weight irradiance, cosine^50 distance moments; hysteresis
adjusts with ray count/cadence.
Four-phase round robin; far probes every second round, fog-hidden probes slower. Preserve the
same effective history when updates slow; a newly invalid tile cannot contribute stale light.

Eight neighbouring probes use trilinear, facing and Chebyshev visibility weights. Near cascade
wins, far fills outside near, sky outside both. A point whose near probes are all invalid looks
up again one cell along its normal before falling back to the far cascade.
**Observed** (Hub bar, debug colouring): at the 32-unit room grid every probe round wall/corner/floor
points can sit in walls, the raised floor or furniture; the far cascade then painted them near black. GI replaces envDiffuse; per-fragment path also
scales specular ambient by luminance ratio clamped0..1. Reflection probe use is a separate switch.

Also the doors of a building in the interior view, whatever floor they name (15 base-game door leaves are floor-1 parts, shown on every floor: as outside parts they took the outdoor probes, dark inside the walls, a black door from inside, **Observed** in the Bane of Okran bar at night). Also a part of a building in the interior view at or below the viewed floor (floor 0 too: the top of a wall the view cuts open sits under the hidden wall above it, and the hybrid cache flickered there, **Observed** in the probe-leak save: frame-to-frame range along the rim 1.24 mean / 6.3 p99, paused, against 0.53 / 3.3 with this): its slab is seen from inside the cut-away room. **Observed** (Ancient Lab with the Forgotten Buildings ceiling planes, views 1 and 2): such a slab took the raw GI, which the hidden floors above and a one-sided ceiling plane over it blackened (mean luminance 11 against 31 with Indirect light off, the game's flat ambient; **Verified**, decompiled: the game has one ambient, no indoor lighting mode, and its sun and shadow maps light a viewed floor as outdoors). Taking the indoor floor (`WorldObjectRenderer.IndoorFurniture`) brings it to 43, as with GI off; other buildings' floors are unchanged (+-1).

Interior meshes, furniture inside building masks (layout furniture, and player-placed furniture/stations
standing in another building's hull), loose items in a hull (shelves: the hybrid cache gave them nothing, **Observed** black in the probe-leak save with hybrid GI only), and indoor characters take indirect light of
luminance max(GI, original ambient); its hue mixes ambient to GI hue by smoothstep(0.1, 0.25, GI/ambient)
(Hub bar, **Observed**: GI/ambient 0.15-0.3 round lamps, 0.05-0.15 elsewhere, nowhere above 0.3).
The floor is capped at luminance 0.6 (`InteriorFloorCap`), its hue fading to grey as the cap bites: by day the sky's ambient lit shut rooms evenly, in the sky's colour (**Observed**, a Hub building at 11:30 with its roof hidden: mean 42/255 uncapped, 20 capped). Direct sun/lamps and the probe feedback remain unscaled. `GiProbes.InteriorGain` <= 1 disables it.
**Observed** (Hub bar): corners/upper walls read under a quarter of the ambient (dark probe-cell
blotches under the former 4x gain capped at ambient); full GI hue at ambient luminance reads
saturated yellow on unlit walls; a linear GI-share mix nearly matches GI off.

## Fog and resolve

`GiFogHide` chooses slower updates for probes hidden by placed fog without removing bounce
geometry or blindly shortening rays. Visible indirect light may depend on hidden surfaces.
Resolve divisor2: scene writes flagged normal/albedo into an extra RGBA16UI target; low-res
resolve reconstructs position from the scene depth; full-res apply depth-weights four neighbours.
The resolve runs once a frame: after the scene, or, when the water copies the scene (`WaterRenderer.NeedsCapture`), over the opaque scene before that copy (`RunGiResolve(early)`): the water writes no surface, so the floor seen through it kept the sky's ambient while the ground beside it took the probes' (**Verified**, a pond at night, GI off, probes, hybrid, before and after: a pale band in the shallows with the resolve's texels as its edge with the resolve after the water, none before it).
Unflagged/background surfaces must remain unchanged. Every program drawing into the scene pass
must write that target (`WithAlbedo`, `none` for unlit ones), including the GPU-indirect variants:
an unwritten output is undefined (in practice the surface behind, whose GI then lands on the
front fragment; seen on grass and on crossbow bolts, i.e. `DebrisRenderer`, until it used `WithAlbedo`). Resolved surfaces lose the
per-fragment specular-occlusion term unless [Reflections](#reflections) run. Strong depth/normal discontinuities need rejection.
Surface bit9 marks interiors; bits 10-15 the reflections' gloss, bit 16 traced and 17-18 the receiver are the traced sun's (the target is RGBA32UI); signed low-resolution depth preserves that class (0 empty, negative
interior). Upsampling rejects the other class so the gain cannot bleed onto outdoor surfaces.

## Hybrid GI

`gihybrid` (Meitou switch, **off by default**, Graphics > Lighting > Hybrid GI). `--gi-hybrid <relocate,clouds,cache>` turns it on with
the listed parts only. Each part is also a plain on / off row under Graphics > Lighting > Indirect light ("GI probes out of walls", "GI cloud shadows", "GI radiance cache"), read each frame. Vulkan only. Relocation and clouds: look and cost **Unknown**; the radiance cache: below.

**Probe relocation** (`relocate`; RTXGI's rule, `GiShaders.ProbeRelocate`, needs the resolve). Offsets stay in world units per atlas
tile, in `offsets`; relocation reads the frame's rays before the blend. Over a quarter back faces: through the closest back face, half the
clearance past it. A front face nearer than the clearance (0.2 of the smaller spacing): towards the farthest hit, half the clearance, when
that lies across from the closest one. Clear: back towards the grid place by at most a quarter of the clearance an update
(RTXGI goes all the way; single random-rotation rays make that jitter), never past the closest hit. A move past 0.45 of a cell is dropped, and that
probe stays invalid through the blend's own count; a sleeping probe keeps its place. History is kept across a move, and a scrolled tile drops its offset.
`GiShaders.ProbeOffsetPublish` writes three bytes (half a cell, 127 none) as one exact integer into the frame slot's `uGiBase`, in the rows below
the column bases, and `uGiGrid[2c+1].w` says they are valid: no new binding or frame constant. `GiShaders.Relocating` patches the reads into the
trace, the resolve and the programs that keep reading the probes themselves (`WithAlbedo(inShader: true)`), so the other world
shaders and `ProbeSampling` keep their text; the patched programs carry one uniform test per probe while off.
The per-fragment path (`--gi-resolve 0`) and the gi-reflection bench do not read offsets, so relocation runs only with the resolve.

**Cloud shadows** (`clouds`). The sun at a probe's hit takes `atmoCloudShadow`, the lit fragments' own function, so shaded land stops bouncing sunlight
and probe light follows the clouds at the hysteresis' pace. The sky seen by a miss is not dimmed. Shafts: [render-shafts](render-shafts.md).

**Radiance cache** (`cache`; `GiCache`, `GiCacheShaders`, needs the resolve). World-space cells in a hash table (`Capacity` 2^18 slots, a key in one of 8
consecutive ones; 152 bytes a slot, about 40 MB), keyed by the cell (position / size, floored), its level and the axis nearest the surface's normal (six, interiors apart).
Size `FinestCell` 4 units, doubled per level so a cell keeps about 1/`CellsPerHeight` (96) of the view's height at its eye distance (the whole view's angle, so screenshot
tiles agree); the next coarser level blends in over the top 40 % of each level's distances, the second-nearest axis within 0.2 of the diagonal. Out to `Range` 4000.
Per frame (`gi cache`): **evict** slots no texel touched for 600 frames (120 past half full, 30 past 5/8); **touch**: each low texel of the resolve (same pixel; the flat
normal from the depth, but where it turns over 45 degrees from the stored shading normal (a silhouette, a crest, a pixel-thin part) the plane through one neighbour
per axis nearest that normal, else the shading normal: no texel takes a plane it is not on) makes its cells (two levels x two axes at most) and adds its position and normal into their sums (12-bit fixed point within the cell),
listing each cell once; **trace**: one workgroup per listed cell, `RaysPerCell` 32 cosine rays from its surface point (the texels' mean, but along the cell's axis the texel farthest out: a cell holds every surface facing its axis, and the mean of two stair treads lies under the upper one, where every ray ends at once; **Verified** on the user's save at -51509, 2820, 15.18 h: black smudges on a stair's treads with the mean, none with the farthest; averaged over up to 64 frames across the surface, and this frame's along its normal: the average lagged behind a surface that moves, inside it, and the cell went black while it moved; **Verified** on a town gate opening, pictures back to back before and after: black blocks on the leaf before, none after, the still view within the difference between two runs of one build; a rotor's blades not checked),
`Reach` 150, shaded as a probe ray's (`GiShaders.HitShade`; a miss reads the probes at the ray's end in its direction; a back face is shaded as a surface, since drawn-one-sided canvas is hit from below and reading the probes at the ray's start there varied per frame, **Observed** probe-leak2 deck under the tents, paused frame-to-frame luma range 1.8 -> 0.6, 0.56 with GI off; clamp 16
a channel). Each frame traces one of `Ring` 16 direction subsets (a Fibonacci lattice shifted by the subset's R2 point and a per-cell random shift); its mean replaces
that subset's slot (E5B9G9R9 word) and the cell's light is the mean of the subsets it holds. A still scene's cell therefore holds a fixed set of 512 rays and does not
change; a change of light is followed linearly over 16 frames, except that a subset traced again whose light moved by over `ChangeShare` 25 % of the cell's (in a
still scene the same directions give the same light) drops the cell's other subsets, and a cell holding fewer than `FirstSubsets` 4 traces 4 at once (as a new one). **gather**: per low texel, per level and axis, the 8
cells whose centres surround it, weighted trilinearly x exp(-|tangent-plane distance of the cell's point| / half a cell) x facing^8 x subsets held; alpha = the
subsets' share x 4 (full from 4), x the found weight over `MatchFloor` 0.25 of the found cells' trilinear weight (cells only on another plane or facing away leave the
texel to the probes; at 0.05 the cache took the cloth and stalls' edges and a still picture changed more, 0.052), fading over the last quarter of `Range`. The resolve takes it over the probes' light by alpha (indoor gain, haze unchanged); the probes' share is first capped at the cache's own light there (fading to
the probes alone over the last quarter of `Range`): the probes do not see contact shade, so where the cache is unsure they may darken a texel, never brighten it.
No screen-space history: a surface uncovered by a character or turned back into view finds its cells as they were (kept while untouched); only never-seen surfaces start
new cells. The cache does not trace characters (`GiMask`), so they leave no trail in it; characters and grass keep their in-shader probes.
**Observed** (8-bit mean |difference| with 99th percentile; 1280x720, no upscaler; probes only / the former screen-space near field / the cache).
Hub (new game, 14:00, still camera after 600 frames, 15 frame pairs): frame-to-frame 0.059 / 0.093 / 0.044, the cache with its trace frozen 0.044 (the rest is the
scene's own motion); pixels changing where the probes-only picture does not: 11.2 % for the near field, 1.0 % for the cache. Orbit 1.5 deg/frame, frame 24 against the
settled picture: 0.96 / 3.3, 0.96 / 4.0 (a bright band trailing the character), 0.95 / 3.0 (none). A character running past: 2.45, 2.49, 2.51 (rows below the horizon;
the character's own pose dominates). User saves: `siwa-test6` orbit 0.5 deg/frame 1.09 / 3.0, 0.77 / 2.0, 0.78 / 2.0; `gi-flicker` (night, lamps) still 0.025, 0.012,
0.012 (the probes alone drift in the dark); `gi-fireflies` (night, water): the isolated bright pixels are the same count with all three (and with the GI off: not the GI's).
Sun jump 14:00 to 17:30: within 0.2 of the near field's error each frame (most of the lag is the probes'). With DLSS (1600x900) the orbit error is the upscaler's (2.3-2.5 for
all three). About 20,000 cells listed a frame and 47,000 held after an orbit (Hub). Cost at 2560x1440, DLSS Quality (low target 854x480), quiet GPU: `gi cache`
0.36-0.37 ms, `gi near` 0.55-0.57 ms. `interior` save (17:14, rain): the room's surfaces read the game's ambient (the GI is below it, Indoor GI's floor), the same with all
three; `gi-grain`: still 0.115 / 0.132 / 0.104, pixels changing beyond the probes' 7.0 % / 0.9 %.
**Unknown:** an interior lit above the ambient floor (the `gi-interior2` save renders black offscreen at night); dune crests (thin lines there keep alpha 0.3-1: the cells' facing weight; with the probes capped at the cache's light they can only darken there); thin surfaces sharing a coarse cell with the floor
beside them (the plane weight keeps other cells off, not the cell's own mean point).
## Reflections

`ssr` Enhancement, off by default, Vulkan only; `Gi/SsrShaders.cs`, `GiShaders.Reflect`, `PostProcess.Reflections.cs`. Replaces the sky cubemap's
reflection (`uAtmoSpecular`) on surfaces that write the surface target; water writes 0 and keeps `ReflectionPass`; characters, foliage and grass write 0 too (they
read the probes in shader), so they mirror nothing but appear in what others mirror. Runs after the resolve, before shafts, fog volumes and SSAO.
With the switch on, `uGiParams.z` gains bit 2 (bits: 1 resolve, 2 reflections, 4 traced sun): every lit fragment writes the target, `1 + gloss * 62` in bits 10-15 and bit 8 only if it takes the probes (so
without probes, and on interiors with **Indoor GI** off, reflections run with no resolve). Off, the words written are those of before.

- **March** (resolve size, 1/2 without it): gloss above 0.25, reflected from the stored normal, 40 screen steps with inverse depth linear, 5 halvings, up to 3000 units.
  A hit counts up to 6 units behind the depth or 1.5 march steps. A rejected hit: from behind (stored normal), or within 4-12 units (a normal-mapped ray under its own surface). Fades at screen edges (6%) and the last quarter.
- **Rays** (needs a probe trace this frame and storage on RGBA16F): the same ray for every glossy texel, against the traced scene and its characters, up to 4000 units,
  lit as a probe ray (no specular, no haze). A character hit takes its part's diffuse and head maps (mip 1 here, 4 for the probes), the darkening the colour pass applies and the hair's tint; skin tone, dyes and vests are left out. A hit replaces the march (world space first); a miss keeps what the march found (grass, water, particles are not traced) and
  proves the sky open past it. No rays: the march, then the sky, occluded.
- **Shade** (same size): `seen - sky`, hazed as the fragment was, per unit of BRDF. `sky` is `kenshiLight`'s read of the cube, recomputed, so nothing is counted twice;
  `seen` mixes occluded sky to the hit by gloss 0.25-0.45. Every surface passes here: **specular occlusion** is recovered from the resolve's texel
  (`1 + lum((probes - sky) * share) / lum(sky)`, clamped: `kenshiLight`'s ratio) and needs no extra read of the probes.
- **Cone** (in Shade): the hits within the disc a rough surface's cone covers at their distance (radius = distance x half the roughness squared, at most 16 texels; 16 samples find the distance, 32 average) are averaged on a fixed spiral, over texels of this surface only.
- **Apply** (full size): depth-weighted upsample as the resolve's, times this pixel's Lazarov BRDF, specular colour 0.04 (only characters have metalness).

A wet surface goes to gloss about 0.45 (`makeWet`). The ray is the lobe's dominant direction (`kenshiLight`'s sky lookup: towards the normal as the surface roughens), the
same every frame and from the march's fixed step offsets: a lobe sampled anew each frame (as before) left the upscaler noise it could not average out.
GI per fragment (`--gi-resolve 0`) leaves the reflections off: its fragments already occlude a term the shade cannot recover.
Rays and march per block use one surface; `sky` is that pixel's, so a noisy normal inside a block leaves a small error at grazing angles.
**Observed** (`siwa-test6`, the user's settings: DLSS at 0.67, paused, hidden HUD, live frames through the control channel; mean |frame - previous| over 7 frames, 8-bit):
the stochastic lobe 0.214 with the reflections, 0.155 without (noise on the crate's metal and the machine); the fixed direction 0.181, 0.174 without. Night town
(`gi-fireflies`): 0.052 against 0.044. No pixel brighter than both the frames around it and its 5x5 median by 24 levels, still or orbiting, in either save, with
any switch off (no fireflies on dry ground found; the bright points on that save's water are there with the GI off and with the game's water). Cost at 2560x1440
DLSS: `ssr` 0.11-0.12 ms either way. Thresholds and look: **Unknown**.

## Performance and verification

`gi-top-reuse`, `gi-rays`, `gi-far`, `gi-top-trace`, `gi-terrain-refit`, `gi-lazy-scene`,
`gi-top-shared`, `gi-resolve` are A/B probes. Different schedules/ray counts may converge
differently; compare both settled and cold lighting. GPU GI cost includes scene builds,
trace/blend and barriers; resolve/apply may be accounted in post. Fixed camera results do not
predict moving-camera rebuilds. **Observed checks:** zero GI-off mean screenshot
change in Hub/swamp; moving-scene synchronization validation checks structure/address lifetime.
**Observed indoor check:** Hub bar (`4933-Newwworld-INGAME`),32/24 grid:47 of63 probes within
the mask valid (irradiance-atlas alpha readback), nonzero bounced light. Settled gain1/4 captures
show brighter walls/stairs/furniture at gain4; resolve2 and per-fragment paths retain shadows and
lamp colour. These are manual checks, not image regressions.
Indoor GI off/on captures through the live switch confirm ambient bypass in both resolve2 and
per-fragment paths while the main GI switch stays enabled.
**Observed temporal check:** same fixed Hub bar camera,256 warmup then96 frames: blending
validity reduced the worst count of pixels changing by >16/255 per frame from2447 to187.
Random-ray occupancy previously replaced alpha with0/1 each update, causing visible flashing.

## Open

- **Resolve:** specular occlusion for the resolved surfaces with [Reflections](#reflections) off; foliage in the resolve (its pixels' normals are
  noisy; it reads without the visibility test now).

- **Terrain resolution.** The fine grid matches the drawn terrain only roughly (16 against the fine window's spacing). Building it from
  the streamed fine window directly would remove the culling workaround.
- **Compaction** of the object structures, if the memory grows with longer ranges.
- **Probe update cost.** The fixed part (full barriers; the update could run on an async compute queue beside the shadow pass) and
  a cheaper bounce lookup in the trace (no visibility test at hits).
