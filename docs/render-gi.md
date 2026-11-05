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

Fine/coarse terrain:256² quads, spacing16/160 (4096/40960 width), heights sampled on workers.
Object range6000; resident finest mesh parts build bottom levels, max32/frame; unused600 frames
can retire. Foliage radius>=150, range4000, max14336 instances, cached candidates invalidate by
movement/deletion/build progress. Do not trace grass, characters or water as geometry.
Origin tracks eye in64-unit steps with1024-unit hysteresis; lazy scene epochs coordinate commits.
Shared top level fits16384 instances, prefers fast trace, reuses identical instance data.
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

Interior meshes, furniture inside building masks (layout furniture, and player-placed furniture/stations
standing in another building's hull), and indoor characters take indirect light of
luminance max(GI, original ambient); its hue mixes ambient to GI hue by smoothstep(0.1, 0.25, GI/ambient)
(Hub bar, **Observed**: GI/ambient 0.15-0.3 round lamps, 0.05-0.15 elsewhere, nowhere above 0.3).
Direct sun/lamps and the probe feedback remain unscaled. `GiProbes.InteriorGain` <= 1 disables it.
**Observed** (Hub bar): corners/upper walls read under a quarter of the ambient (dark probe-cell
blotches under the former 4x gain capped at ambient); full GI hue at ambient luminance reads
saturated yellow on unlit walls; a linear GI-share mix nearly matches GI off.

## Fog and resolve

`GiFogHide` chooses slower updates for probes hidden by placed fog without removing bounce
geometry or blindly shortening rays. Visible indirect light may depend on hidden surfaces.
Resolve divisor2: scene writes flagged normal/albedo into an extra RGBA16UI target; low-res
resolve reconstructs position from the scene depth; full-res apply depth-weights four neighbours.
Unflagged/background surfaces must remain unchanged. Every program drawing into the scene pass
must write that target (`WithAlbedo`, `none` for unlit ones), including the GPU-indirect variants:
an unwritten output is undefined (in practice the surface behind, whose GI then lands on the
front fragment; seen on grass and on crossbow bolts, i.e. `DebrisRenderer`, until it used `WithAlbedo`). Resolved surfaces lose the
per-fragment specular-occlusion term unless [Reflections](#reflections) run. Strong depth/normal discontinuities need rejection.
Surface bit9 marks interiors; signed low-resolution depth preserves that class (0 empty, negative
interior). Upsampling rejects the other class so the gain cannot bleed onto outdoor surfaces.

## Reflections

`ssr` Enhancement, off by default, Vulkan only; `Gi/SsrShaders.cs`, `GiShaders.Reflect`, `PostProcess.Reflections.cs`. Replaces the sky cubemap's
reflection (`uAtmoSpecular`) on surfaces that write the surface target; water writes 0 and keeps `ReflectionPass`; characters, foliage and grass write 0 too (they
read the probes in shader), so they mirror nothing but appear in what others mirror. Runs after the resolve, before shafts, fog volumes and SSAO.
With the switch on, `uGiParams.z` gains 2: every lit fragment writes the target, `1 + gloss * 62` in bits 10-15 and bit 8 only if it takes the probes (so
without probes, and on interiors with **Indoor GI** off, reflections run with no resolve). Off, the words written are those of before.

- **March** (resolve size, 1/2 without it): gloss above 0.25, reflected from the stored normal, 40 screen steps with inverse depth linear, 5 halvings, up to 3000 units.
  A rejected hit: from behind (stored normal), or within 4-12 units (a normal-mapped ray under its own surface). Fades at screen edges (6%) and the last quarter.
- **Rays** (needs a probe trace this frame and storage on RGBA16F): the same ray for what the march left, up to 4000 units, lit as a probe ray (no specular, no haze);
  a miss proves the sky open. No characters, grass or water. No rays: the sky, occluded.
- **Shade** (same size): `seen - sky`, hazed as the fragment was, per unit of BRDF. `sky` is `kenshiLight`'s read of the cube, recomputed, so nothing is counted twice;
  `seen` mixes occluded sky to the hit by gloss 0.25-0.45. Every surface passes here: **specular occlusion** is recovered from the resolve's texel
  (`1 + lum((probes - sky) * share) / lum(sky)`, clamped: `kenshiLight`'s ratio) and needs no extra read of the probes.
- **Apply** (full size): depth-weighted upsample as the resolve's, times this pixel's Lazarov BRDF, specular colour 0.04 (only characters have metalness).

A wet surface goes to gloss about 0.45 (`makeWet`). With an upscaler each frame samples the GGX lobe for TAA/DLSS to average; without one the mirror direction is used.
GI per fragment (`--gi-resolve 0`) leaves the reflections off: its fragments already occlude a term the shade cannot recover.
Rays and march per block use one surface; `sky` is that pixel's, so a noisy normal inside a block leaves a small error at grazing angles.
Thresholds, range, look and cost: **Unknown** (not run on a GPU).

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
- **Probe relocation.** DDGI moves probes out of geometry instead of only marking them invalid. In towns many probes sit inside walls.
