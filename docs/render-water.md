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
River coverage and biome scum are incomplete; object-contact foam is the off-by-default [switch below](#contact-foam).

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

## Contact foam

Meitou look, not in the game; the `contactfoam` switch (Tab "Water contact foam"), off by default, not yet seen on a GPU. Look and cost **Unknown**. A second Meitou program
(`WATER_CONTACT`, made on first use), so off the water has none of it; Vulkan only. Both reach 1200 units, fading over the last 400, and join the foam `amount`, so they take the lace, light and colour of the water's other foam.

- **Contact**: `sceneThickness` under a band of 1.5 to 4 units (wandering with the foam's noise), where the ray's end stands 1 to 3 units or more above the terrain under the point:
  rocks, posts, walls and legs; the shore keeps its own foam. Needs the scene depth: with `--no-water-refraction` the switch asks for the copy itself within its reach. The last 2 units'
  fade (Edges) lets the foam through. Shallow bumps of displaced ground can pass the test: **Unknown** if it shows.
- **Wakes** (`GameHost.UpdateWakes`, the 8 nearest within 800 units with feet under Y 100, moving over 1 unit/s; `WaterRenderer.SetWakes`): a ring round the body and two arms at 19.5 degrees trailing as far as the speed in units, up to 40. Live in the constants: nothing lasts on the water behind them.

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
