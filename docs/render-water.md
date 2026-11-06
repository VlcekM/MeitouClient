# Water rendering

`WaterRenderer`, `ReflectionPass`, ocean/shore/river bake and WaterShaders implement two modes.
Original water/material records: [formats/terrain.md](formats/terrain.md). Meitou motion is visual;
gameplay water stays Y100. Evolution follows the game clock and stops paused.

## Surface and waves

Faithful: flat surface and game shading/reflection rules. Meitou: camera-centred polar grid,
default256 segments; `--water-grid 128` reduces geometry with visible quality differences.
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
Shore waves (Meitou look, not in the game): offshore the baked distance is eased towards a ~105-unit Gaussian blur of itself (three
10-texel box passes) from 40 to 250 units out, not within 32 texels of the field's edge (`ShoreBake.Relax`); the exact distance creases
where the nearest shore switches (a bay's middle, off a point or a pier), and the crests that follow its lines kinked there while their
foam's frame flipped along a straight seam. Where the field's slope over 24 units is under about 0.8 (two fronts meeting) a crest keeps
30% of its size. Along each crest its size varies 0.5 to 1 (240-unit noise, a pattern of its own per wave). Each wave's run-up varies
0.55 to 1 along the beach (70-unit tongues) and the wet line 0.7 to 1; the swash front's foam band wanders from 0.04 to 0.3 of the
run-up (40-unit noise), thins in over its edge and takes the finer lace without the cell holes (a tiled pattern on the sand).
**Observed** (Bark lake by the pier, offscreen sequences): the straight seam is gone and crests bend and fade there; swash close-ups
**Unknown** (no swash on sand was caught in the test views). River coverage and biome scum are incomplete; object-contact foam and character wakes are the off-by-default [switch below](#contact-foam-wakes-and-ripples).

## Shading and reflection

Water combines Fresnel, normal distortion, depth absorption/refraction, sky/scene reflection,
caustics, crest light/glitter and persistent foam. Surface colour/clarity depend on biome/Tab
settings. Foam uses diffuse sun/ambient/weather lighting, reads the sun shadow and cloud-shadow
tile, and dims at night, not emissive white. Metal uses the same native bindless water program as
Vulkan; there is no Metal-only water fragment. The reflected scene remains mirrored around Y100
despite waves. Reflect level/range, frustum,
clip plane, water coverage and reuse age determine work; use original rules in Faithful.
Meitou reflection skips unnecessary casters/shadows and shortens object/foliage reaches. The half-resolution
target uses 4x MSAA by default on both backends (`--reflection-samples 1` disables it); level 2 reflects
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
- **Foam**: renewed in a 1.2-unit ring where a moving body cuts the surface (by the rate it displaces water) and where the slope passes 0.45 (full at 0.9, at most 0.7);
  spreads a little and fades to a third in 2.5 s.
- **Shading**: written each frame into an RGBA16F storage image (height, slopes, foam; one level); the water's vertex stage adds the height where its vertices are under 3 texels apart, the
  fragment subtracts the slopes from its own (reflection, refraction, glint and the floor's bend follow) and adds the foam; all fade over the window's last 12 units.
  Where a pixel spans 1.5 to 4 texels the height and slopes fade out (the foam stays): the field has no mips.
- **Cost** **Observed** (NVIDIA RTX, offscreen 1280x720, sandbox walk, GPU timestamps round the compute, `water ripples:` line at exit): 5 to 10 us a step, about
  5 us for the write-out; 19 to 35 us for the 3 steps of a 1/30 s frame (about half that per frame at 60 fps). The water fragment's extra cost (one bilinear fetch and
  a few operations) is **Unknown**, not measured apart. Nothing runs while the switch is off, no water lies in the window, the nearest water in view is past 1200
  units, or no body has been in the water for 20 game seconds (the field is then left as it is). The depth map is baked again every 120 frames (terrain streaming).
- **Unknown**: capsules from axis-aligned pick boxes are rough for bones slanted in the binding pose; no splash particles; objects and projectiles do not push the water;
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
