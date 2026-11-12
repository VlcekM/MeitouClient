# Water rendering

`WaterRenderer`, `ReflectionPass`, ocean/shore/river bake and WaterShaders implement two modes.
Original water/material records: [formats/terrain.md](formats/terrain.md). Meitou motion is visual;
gameplay water stays Y100. Evolution follows the game clock and stops paused.

## Surface and waves

Faithful: flat surface and game shading/reflection rules. Meitou: camera-centred polar grid,
default 1024 segments (a vertex every 0.6 % of its distance from the eye, about 8 pixels at 1080p; 256 was 30); `--water-grid 256` reduces geometry with visible quality differences (Graphics > World > Water: "Water grid segments" and "Water wave resolution" (the FFT size), read at start-up). The waves a vertex carries are those its spacing can hold, so their shape at a world point depends on the eye's distance: **Observed** (an open beach, paused, the same moment with 256 and 1024 segments): at 256 the swell 150 to 300 units out was long smooth crests where 1024 draws the choppy waves, and breakers' crests changed shape as the camera moved (the user). The grid's triangles are drawn from
the outer rings in (the eye's fan last): the water writes no depth, so a displaced crest in front of water further out must be drawn after it,
else the water behind a wave showed through it.
Three256² FFT cascades cover250/47/9m tiles with separate wave-number bands. JONSWAP wind spectrum,
mirror-conjugate coefficients and normalized directional spreading; zero/Nyquist/out-of-band
modes excluded. Evolve omega=sqrt(g*k), quantized to2pi/600s; modulo clock keeps phases precise.
Stockham row/column FFT packs displacement/slopes/derivatives into two mipmapped RGBA16F arrays.
Vertices choose mips by spacing; fragment slopes preserve fine shading. Jacobian<0.7 produces
crest foam. Wind change>10deg or12% rebuilds on a worker and blends10 game seconds.
**Verified** OceanTests single-mode transform and symmetry under synchronization validation.

Wave displacement fades in shallow/sheltered/shore water; surf uses baked distance/gradient and
exposure, with breaker/swash/foam envelopes, rather than bottom-slope distance approximation.
Game normal maps remain with reduced near strength. Shore-field bakes/publishing must not stall
frames; retain ready textures until replacements finish. RiverFlowBake derives channel flow from
heights because source flowmap RG does not follow channels; suppress ocean/surf in river channels.
### Shore waves

Meitou look, not in the game. One swell from a fixed direction (`ShoreTravel.Direction`, the same everywhere, so no weather or moved field moves a crest)
arrives as a plane wave over water deeper than 60 units and runs on at c = 22·sqrt(depth/60) units/s (depth clamped to 1..60; 14 over the beach, for the
run-up). Its arrival time is a first-arrival (Dijkstra, 8 neighbours) march over the shore field's grid, 250 units inland at most, softened by 16 binomial
passes so crests round where two arrivals meet; the walls and posts standing in the water (`BuildingFootprints.PlaneCut`: the level buildings' collision cut at
Y 100, rasterised every quarter texel) block it, so crests stop at a wall and bend round its ends. What is left of the swell (0..1) is the mean of the texels it
came from, thinned by land and walls beside its way (0.1 each, not those ahead), so narrow inlets and lees hold less. Both go in the shore field's B and A
(RGBA32F). The shader's phase is the wave count at (clock − arrival): a wave every 8 s on average, in sets of seven over which the period swings ±20%
(`waveCount`; the clock wraps at 4096 sets); the local wavelength is c times that period. Breakers break where the depth is 1/0.78 of their height (the
break distance from the local floor slope); past the break and in a film their height is held to 0.78 of the depth. Per-wave size noise rides with the
wave. The breaker front's slope is the height difference over 6% of a wavelength, at most 0.35 (over 2%
it made a thin steep strip that reflected as a bright greenish line ahead of every crest; found by cutting each term in turn). The breaker face's glow shows only within 80 units of the break (along the whole swell it drew a band ahead of every crest).
Surf foam lies where noise clumps and blotches in the wave's frame pass a narrow threshold
(rising with its age, holes even when fresh, frayed by the froth), with streaks of the blotch noise stretched 5:1 along the waves' way; its density
follows that texture linearly (0.3 to 1.2, capped at 1, so the filtered distant foam keeps its mean), never a flat white sheet, and the thickest whitewater is capped at 0.88 so holes remain; shaded by the froth at two turned fixed world scales. Every foam
pattern is world-space at fixed scales, so zooming never changes its size or shape: detail under a pixel is filtered by the texture's mips, and the
finest octave (the bubbles' walls) fades by the pixel's footprint (0.12 to 0.3 units), never by the eye's distance.
**Froth** (`WaterFoam`, the foam texture's A; every foam: surf, swash, rivers, open water, contact, wakes and splashes): not cellular. Five octaves of
domain-warped value noise less round holes of widely varying size (four scales, mostly small, about half the cells empty), so a rising threshold (more
age, less foam) leaves a sheet with holes, then irregular clusters, then dots; B holds bubble rings of 0.13 to 0.4 units for close views. The cellular lace
(R, a single-scale network of rims) read as a honeycomb of thin lines on aging foam and is left to the floor's caustics. Open-water foam drifts with the
current and, on still water, wanders half a unit over about a minute. The waterline: no still foam
ring (an exposed shore gets a little with each wave). On the swash the foam's lace rides the sheet: shifted by level / beach slope along the
waves' way, so it runs up with the uprush and back with the backwash; the sand behind dries in patches (two noises, 9 and 23 units) over the wave's cycle. Contact foam needs 2.5 to 4.5 units of standing (the ground relief made false lines). The water's
edge fades over 2 units along the ray and 0.6 of depth, whichever is longer (0.5 and 0.2 for the swash), smoothstepped. Ponds and sheltered water keep
the open sea's waves ×0.3 (by exposure) and the normal map's slopes whole (×0.08 and ×0.3 left a lagoon as flat as a mirror: rejected by the user); the far glitter keeps its mean only (its mask showed
the foam texture's cells, still, in a band of distance); the swash's sun glint ×0.25. **Observed** (offscreen sequences, Bark lake): crests come in from the open water, bend into the
bay and stop at the pier. Cost **Observed** (viewer bench, 2560x1440 with the default upscaler, RTX, Bark beach, 400 frames): the water stage 0.28 to 0.31 ms;
the bake 260 ms on its worker. **Unknown**: piers whose collision is posts block a little of the swell; the one fixed direction leaves lee shores calm.

River coverage and biome scum are incomplete; object-contact foam and character wakes are the off-by-default [switch below](#contact-foam-wakes-and-ripples).

## Shading and reflection

Water combines Fresnel, normal distortion, depth absorption/refraction, sky/scene reflection,
caustics, crest light/glitter and persistent foam. Surface colour/clarity depend on biome/Tab
settings. Foam uses diffuse sun/ambient/weather lighting, reads the sun shadow and cloud-shadow
tile, and dims at night, not emissive white. Metal uses the same native bindless water program as
Vulkan; there is no Metal-only water fragment. The reflected scene remains mirrored around Y100
despite waves. Reflect level/range, frustum,
clip plane, water coverage and reuse age determine work; use original rules in Faithful.
Meitou reflection skips unnecessary casters/shadows and shortens object/foliage reaches. The full-size
target (half size magnified the mirror image at grazing angles: silhouettes in blocks in the thin water of a beach, **Observed** before and after) uses 4x MSAA by default on both backends (`--reflection-samples 1` disables it; the "Reflection samples" row, off to 8x, remakes the targets live); level 2 reflects
characters, level 3 placed objects and level 4 foliage. Fog in reflection must evaluate reflected world geometry
against the appropriate air/water bounds; ordinary camera fog cannot be applied indiscriminately.

## Edges

Both waters measure their depth as the game does ([terrain](formats/terrain.md) "LOD, water and sun"): the distance
along the view ray from the surface to what is behind it, from the scene depth copied before the water
(`CaptureScene`, Vulkan only; Metal and Meitou without refraction keep the slant path to the terrain's height).
Faithful uses it as the game's alpha. Meitou absorbs along it and fades the last 2 units into the scene behind
(0.5 for the swash); both widen that fade to the two stored depths' precision (`2 * depthStep`, derived: 2.4e-7 z on
Vulkan's reversed float depth, 0.03 units at 120000; 2.4e-7 z^2 / near on Metal's conventional one), so distant shores
do not shimmer. Ground relief, rocks and legs meet the water without a line. The swash keeps its sheet's thickness:
its surface is raised over the beach and cut back to the run-up. Beside it, the raise above this moment's run-up is
subtracted (the breaker hump only where the vertices carry it, as in the vertex stage), else the water stood a few
units deep at a dry waterline (the stepped line along the heightmap contour). Wet sand blends over the full-size scene,
not the half-size copy. SSAO fades under the water plane the same way ([post](render-post.md) "SSAO").
The refracted colour is read depth-aware (`sceneUnder`): the half-size copy's four texels are weighed by how near the depths they average are to the pixel's own, and the surface's bend is dropped where it would cross a silhouette (3 to 10 % of depth). Plain filtering gave a leg or a blade in the water some of the bright floor beside it under its own thin water: a light rim along its edge (**Observed**, swimmers in the Flats Lagoon).
The depth copy is gated like the refraction: Meitou only with refraction on (`--no-water-refraction` pays neither),
Faithful only while the nearest water is nearer than 4000 units; with reflections off the nearest water is unknown, so it then runs
whether or not water is in view. **Observed** (2560x1440, DLSS, RTX 4090, Shark): about 0.03 ms with Meitou water (its
pass restart was there), 0.05 ms with Faithful (a pass restart plus a full-size depth copy).

## Contact foam, wakes and ripples

Meitou look, not in the game; the `contactfoam` switch (Tab "Water contact foam, wakes and ripples"), off by default. Vulkan only. A second Meitou program pair
(`WATER_CONTACT`, made on first use), so off the water has none of it. Contact foam and the field's foam reach 1200 units, fading over the last 400, and join the foam
`amount`, so they take the lace, light and colour of the water's other foam.

- **Contact**: `sceneThickness` under a band of 1.5 to 4 units (wandering with the foam's noise), where the ray's end stands 1 to 3 units or more above the terrain under the point:
  rocks, posts, walls and legs; the shore keeps its own foam. Needs the scene depth: with `--no-water-refraction` the switch asks for the copy itself within its reach. The last 2 units'
  fade (Edges) lets the foam through. Shallow bumps of displaced ground can pass the test: **Unknown** if it shows.
- **Wave field** (`WaterRipples`): a damped wave equation on a 512² window of 0.5-unit texels round the camera's target, world-anchored and toroidal as the
  [ground trails](render-terrain.md#ground-trails) (snap 32 texels; gained strips wiped, all after a jump; a 24-texel sponge at the edge, so waves show within 104 units).
  State per texel (halves): height, its rate, foam, the water the bodies took. Symplectic Euler, isotropic nine-point Laplacian, fixed 1/90 s steps of game time (at most 6 a
  frame, none while paused). Speed sqrt(g d), g 98.1 units/s², d the water depth clamped to 0.4..3 units (6 to 17 units/s), from a 128² depth map baked on the CPU from the
  frame's terrain heights when the window moves; height and rate 0 on land, so waves stop at the shore and slow in the shallows. Damping 0.5/s on the rate, viscosity 0.12 units²/s.
- **Sources** (`GameHost.UpdateWakes`, `CharacterRenderer.WaterCapsules`): every character in the window with feet below Y 102, its posed body as one capsule per skinned
  bone (round the bone's pick boxes over all skinned parts, along their longest side; bones under 0.5 units skipped), those reaching the top 3 units of water. Each step that
  starts a frame, a texel takes the capsules' chord through the top of its column (`min(depth, 3)`, four samples, a texel's soft edge); 0.4 of the change since the last push
  goes into the height. So a moving leg raises a bow and leaves a hollow, stepping in or falling in raises a mound, a standing body makes only its idle motion's ripples, and the
  V of a wake is the waves' own: a Mach cone when faster than the waves (derived: asin(17/30), 35 degrees each side for a 30 units/s walk). **Observed** (sandbox floor 5
  units under water, 30 units/s walk): a bow wave, a V of ripples spreading behind and foam along the legs' path.
- **Shallow water**: a body's push is scaled by sqrt(3 / the column's depth) (0.5 to 3 units), so a step in ankle-deep water splashes and rings as
  visibly as in deep water; its churn foam fills the displaced water too, and the field's foam shows through the water's fade at the waterline.
- **Foot splashes** (`GameHost.Splashes`): each ground event of a foot (the game's footstep timing, [audio](audio.md#characters-footsteps-combat-voices) "Ground effect") whose bone
  is at most 6 units under the water, over ground under the water (not a floor), adds a splash to the next step: a crown 0.8 units high (rising at 10 units/s) on a ring of
  radius 1.6 to 1.9 units round a hollow, which runs out as a ring wave, and foam (0.75 to 0.98) over 2 radii, thickest at the crown; strength 0.5 to 1 by the
  character's speed (full at 50 units/s), less for a deeper bone. Where the water does not reach the ankle bone (no game splash there) the biome's shallowest
  `water effects` system is fired too. At most 32 splashes wait for a step. The field's foam (and the contact foam) is as thick on dark water as on clear
  (only its colour changes), unlike the water's own foam.
- **Foam**: renewed in a 1.2-unit ring where a moving body cuts the surface (by the rate it displaces water) and where the slope passes 0.45 (full at 0.9, at most 0.7);
  spreads a little and fades to a third in 2.5 s.
- **Shading**: written each frame into an RGBA16F storage image (height, slopes, foam; one level); the water's vertex stage adds the height where its vertices are under 3 texels apart, the
  fragment subtracts the slopes from its own (reflection, refraction, glint and the floor's bend follow) and adds the foam; all fade over the window's last 12 units.
  Where a pixel spans 1.5 to 4 texels the height and slopes fade out (the foam stays): the field has no mips.
- **Cost** **Observed** (NVIDIA RTX, offscreen 1280x720, sandbox walk, GPU timestamps round the compute, `water ripples:` line at exit): 5 to 10 us a step, about
  5 us for the write-out; 19 to 35 us for the 3 steps of a 1/30 s frame (about half that per frame at 60 fps). The water fragment's extra cost (one bilinear fetch and
  a few operations) is **Unknown**, not measured apart. Nothing runs while the switch is off, no water lies in the window, the nearest water in view is past 1200
  units, or no body has been in the water for 20 game seconds (the field is then left as it is). The depth map is baked again every 120 frames (terrain streaming).
- **Faults**: a body capsule not finite, with a radius outside 0..30 units or far from the window is dropped (counted at exit, the first one logged);
  a depth not finite is land; every state value is bounded (height ±4 units, rate ±200, foam 0..1) and one not finite is reset to 0 when read and
  written, and the water reads a bad texel as calm, so no single fault spreads through the field or raises the water over the land (it once drew the
  whole window black).
- **Unknown**: capsules from axis-aligned pick boxes are rough for bones slanted in the binding pose; the foam does not drift with the water; objects and projectiles do not push the water;
  the fixed wave speed cap gives shallow-water (non-dispersive) wakes in deep water too, not the 19.5-degree Kelvin wedge.

## Safe work skips

Surf branches only for surf/swash contribution. Zero ocean strength skips displacement/slope
reads with explicit fragment gradients. Caustics skip beyond2500, at depth<=1.5 or sun below
horizon; bubble reads skip beyond600. Reuse depth/shore reads when compatible.
Refraction copy omitted when the nearest water (or the near plane) is >=max(1.5*clamped clarity distance,10000):
transmission is zero and swash faded. Higher clarity restores the copy. Do not infer absence
from terrain/reflection visibility. `--ab water-lean` isolates shader/copy skips; geometry quality
is independent. A scene with no nearby water can skip water work, with conservative bounds.

## Verification and limitations

**Verified** MeitouWaterTests.Refraction_slice_keeps_transparent_floor_and_shore_runup checks
near water, high-clarity far water, short-clarity shore and the15000 default boundary. Grid tests
check both topology/winding. **Observed** close-shore screenshots show256/128 differences;
a still does not establish moving-wave quality. GPU savings on a shared card remain uncertain.
Use a fixed water clock, wind, clarity, resolution/upscaler and texture quality for comparisons.
**Unknown:** incomplete river and scum interactions, and behaviour over all winds/biomes. A
shutdown VkImageView leak was observed in validation;
zero rendering-time errors alone does not establish clean destruction.
