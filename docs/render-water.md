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

River coverage and biome scum are incomplete; object-contact foam and character wakes are the off-by-default [switch below](#contact-foam-wakes-and-ripples).

## Contact foam, wakes and ripples

Meitou look, not in the game; the `contactfoam` switch (Tab "Water contact foam, wakes and ripples"), off by default. Vulkan only. A second Meitou program pair
(`WATER_CONTACT`, made on first use), so off the water has none of it. Contact foam and the field's foam reach 1200 units, fading over the last 400, and join the foam
`amount`, so they take the lace, light and colour of the water's other foam.

- **Wave field** (`WaterRipples`): a damped wave equation on a 512² window of 0.5-unit texels round the camera's target, world-anchored and toroidal as the
  [ground trails](render-terrain.md#ground-trails) (snap 32 texels; gained strips wiped, all after a jump; a 24-texel sponge at the edge, so waves show within 104 units).
  State per texel (halves): height, its rate, foam, the water the bodies took. Symplectic Euler, isotropic nine-point Laplacian, fixed 1/90 s steps of game time (at most 6 a
  frame, none while paused). Speed sqrt(g d), g 98.1 units/s², d the water depth clamped to 0.4..3 units (6 to 17 units/s), from a 128² depth map baked on the CPU from the
  frame's terrain heights when the window moves; height and rate 0 on land, so waves stop at the shore and slow in the shallows. Damping 0.5/s on the rate, viscosity 0.12 units²/s.
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
- **Faults**: a body capsule not finite, with a radius outside 0..30 units or far from the window is dropped (counted at exit, the first one logged);
  a depth not finite is land; every state value is bounded (height ±4 units, rate ±200, foam 0..1) and one not finite is reset to 0 when read and
  written, and the water reads a bad texel as calm, so no single fault spreads through the field or raises the water over the land (it once drew the
  whole window black).

## Safe work skips

Surf branches only for surf/swash contribution. Zero ocean strength skips displacement/slope
reads with explicit fragment gradients. Caustics skip beyond2500, at depth<=1.5 or sun below
horizon; bubble reads skip beyond600. Reuse depth/shore reads when compatible.
Refraction copy omitted when the nearest water (or the near plane) is >=max(1.5*clamped clarity distance,10000):
transmission is zero and swash faded. Higher clarity restores the copy. Do not infer absence
from terrain/reflection visibility. `--ab water-lean` isolates shader/copy skips; geometry quality
is independent. A scene with no nearby water can skip water work, with conservative bounds.
