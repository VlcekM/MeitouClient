# Water rendering

Part of the world view of `meitou-viewer` (and the game, which shares `src/Meitou.Rendering`); options, keys and the rest of the world mode are in [viewer.md](viewer.md#world-mode).

- **Water** (`WaterRenderer`; [formats/terrain.md](formats/terrain.md#water)): one surface at Y = 100, a
  quad centred on the eye and 1.5 times the view distance wide, so the sea reaches the horizon in every direction, past
  the world's edge too (the game's own distant plane stops at the edge; the infinite sea is the viewer's choice). Beyond
  the edge the water colour and parameters fade over 30000 units from the edge pixels to the open sea (the most common
  parameters among the outer ring of the maps), so the last pixels do not stretch outwards, and the water counts as deep.
  It has colour from `watercolourmap.png`, flow from `flowmap.png`, the `water.png` normal map scrolled three
  times, and per-pixel biome parameters (`BiomeField` over `blendinfo.dat` + `blendmap.png`). Shallow water is
  see-through near the camera, and it is opaque beyond 4000 units, as in the game. With reflections (default; `R` or `--no-reflections` toggles) it reflects the mirrored scene (`ReflectionPass`: sky, terrain, objects at 2000 units or nearer (Meitou; the game-like 3000 with `--ab refl-cull` off) drawn about Y = 100 into a half-resolution RGBA16F texture, drawn 4x multisampled and resolved (without it the mirrored shoreline showed stair steps, magnified by the normal-map distortion), with oblique near-plane clipping at the water; the Fresnel and normal-map distortion are the old shader's); without, it reflects the sky colour. The glint widens and dims with distance and is capped, so a far sea shows no blown-out disc.
- **Meitou water** (the `water` switch, `F9`, default on; `--faithful water` is the game's flat water above, unchanged to the pixel;
  `WaterRenderer` with `WaveSet`, `WaterFoam`; branch `meitou-water`, 2026-10-08). Visual only: the water the game uses stays a plane at Y = 100.
  - *Mesh.* A polar grid round the eye instead of the quad (`--water-grid <n>` segments, default 256, 128 for integrated GPUs): rings
    from 1 unit out to 6000 spaced as finely across as along (about 84000 vertices at 256), then one ring at the plane's extent. One draw.
  - *Open water.* Tessendorf's FFT ocean (`OceanSpectrum`, `OceanWaves`, since 2026-10-08; it replaced four Gerstner waves whose
    wavelength and speed changed with the wind, which made a change of weather look like a change of game speed). A fetch-limited JONSWAP
    spectrum (fetch 30 km, wind U = weather wind speed / 10 m/s, 1.5 to 20) spread round the wind by Q(s) |cos(θ/2)|^2s (s after
    Mitsuyasu, narrowest at the peak, normalised with Γ) gives each wave vector its amplitude, with random phases from a hash of the texel,
    so a new wind keeps the pattern. Three cascades of 256² wave vectors over tiles of 250, 47 and 9 m (ratios that are not whole numbers,
    so the tiles never line up), each keeping its own band of wave numbers (a cascade starts at 6 times its own fundamental). On the GPU each
    frame: evolve to the game clock (ω = √(g k) quantised to multiples of 2π / 600 s, so the clock is taken modulo 600 s and float phases
    stay small), an inverse FFT over rows and then columns (Stockham radix 2 in shared memory, four complex fields packing the eight real
    ones: displacement x, y, z, the height's two slopes and the horizontal displacement's three derivatives), then two RGBA16F texture arrays
    with mips. The vertices take the displacement at the mip whose texels are as far apart as the grid's vertices (finer waves would alias
    into facets), out to 3000-5500 units; the fragment takes the slopes per pixel, so the shading never shows the grid. Foam where the
    Jacobian of the horizontal displacement falls below 0.7 (folding crests in a wind). A change of wind of more than 10 degrees or 12 %
    builds a new spectrum on a worker thread and blends it in over 10 game seconds; nothing is computed while the clock stands still. The
    game's scrolled normal map stays, at a third of its strength near the eye and full beyond 6000 units (its second and third samples
    rotated and rescaled against its 250-unit tile). The waves die out in water shallower than 20 units and towards the shore (the breakers
    take over), and are weaker in sheltered water. **Verified** (`OceanTests`): the spectrum pairs each wave with its mirror's conjugate,
    is empty at k = 0, on the Nyquist row and column and outside each band; the spreading integrates to 1; the GPU's transform of one wave
    per cascade gives Dy = 2A cos(k·x − ωt) and D = −2A k̂ sin(k·x − ωt) at every texel tested, at two times, under synchronisation
    validation.
  - *Shore.* Per pixel (and vertex) the distance to the waterline and its exposure come from the shore distance field below (until
    2026-10-08 the distance was estimated as depth / bottom slope, which bent and branched the crests in small bays), the direction to the
    shore as the field's gradient. Breakers (every 8 s, 110
    units apart, in sets: each one about 0.3-1.9 times the height of 3.5-5.3 units) run along that distance, so their crests follow the depth
    contours. They build from 460 units out, steepen (a brighter, greener, less see-through face, a darker trough ahead) and break at
    35-120 units from the waterline (varying along the shore and with the size), bursting white, then run in as a low bore of whitewater
    with lace trailing behind it, stronger in some stretches than others (a foam-texture noise); at the beach they run up as a thin sheet to
    where the ground is 1.4-3.2 units above the water (the run-up height grows with the wind), at most about 120 units inland so low flats behind a beach stay dry, and back, leaving wet sand that dries
    until the next one. The grid is lifted to the run-up's top along the beach and the fragment cuts the sheet's edge. Only exposed
    shores get surf (the field's exposure), so ponds, swamp channels (Shark) and sheltered bays stay calm. The geometry gets only a smooth
    hump per breaker (none where the vertices are more than a fifth of a breaker apart); the steep front, lip and trough are shading, so no
    vertex shows. Sizes, timing and whether a wave breaks vary along the shore (below). The shore fades out from 7000 to 10000 units from the eye.
  - *Surf in sections* (2026-10-08, `shoreAt`'s `waveAt`, **Observed** in pictures at several `--water-seconds`; the numbers are tuned by eye).
    The breakers used to be one unbroken line at one distance from the waterline, all breaking at once, so the surf read as an
    outline drawn round the coast. Now each wave has a number (the wave count, `uShore.x`, which runs on and wraps at 4096; the wave at a
    place keeps its number as it travels in) and slow, coarse value noise on the *shore point* it is heading for (the pixel moved along
    the shore distance field's gradient by its distance, so the noise is constant across the wave and varies only along the shore;
    its pattern drifts about 1 unit a second on a loop periodic in the wrap, so there is no jump when the count wraps) decides three things.
    **Phase**: an offset of up to about ±1 cycle with features of 200-500 units (plus the old 3000-unit term) bends the crests against the
    shore by up to some 15 degrees, so the break point, which is at a fixed distance, travels along the crest (peeling). **Size**: sets of about
    seven waves (a sine of the wave number, irregular, shifted along the shore) take each wave from 0.55 to 1.6 times the
    breaker height. **Breaking**: a noise of about 620 units per wave (more breaking for the bigger waves) gives stretches a few hundred
    units long where the wave rolls in without breaking (lower, its break point moved to the waterline, no collapse into a bore, foam
    and bore scaled to 0.35), so there are gaps between the pieces of whitewater. The amount was raised on the
    user's "more foam" and then set halfway back (2026-10-08): the noise's offset 0.71 (0.62 at first), foam in the gaps 0.275 (0.2), the
    surf's coverage capped at 0.85 (0.8) and the erosion threshold 0.075 + 0.475 × age (0.1 + 0.55 × age); a quiet set of small waves
    still leaves stretches without whitewater. Then all foam ×1.5 on the user's word (2026-10-08): the surf's coverage (its cap 0.95)
    and the open water's (the Jacobian's ramp 2.5 to 3.75, the lingering foam ×1.5). The same `Shore` values (size, `brk`, `breakAt`, the
    phase) feed the vertex hump, the lip, burst, bore, trail, feather, the run-up and the sand's wetness, so geometry and foam agree.
    The offsets fade out (with the shore field's gradient length) on the axis between two shores, where the nearest shore jumps, so an
    inlet shows no seam (**Observed** in `MEITOU_WATER_DEBUG=1` at Port North's inlet: the bands bend there as the distance does).
    Per-wave values change at the troughs (where the breaker profile is nil) and are blended over the trough ahead of the next wave's front, so they
    are continuous in space and time; the sand's wetness (a jump at each crest by design) remembers the size of the last crest to pass.
    Outside the surf zone (more than 700 units out) the extra noise is not evaluated. **Unknown / not done**: tying the heights to the FFT
    ocean's own swell. A wave's height would have to be read where and when it was offshore, and the ocean has no memory of that (its
    height at a point oscillates with the swell, so reading it at the break would make a wave grow and shrink on its way in); the set
    envelope above stands in for it.
  - *Foam.* `WaterFoam` bakes a tileable 256² texture at load: R a lace of bubble rims (cellular noise, F2 − F1), G a five-octave
    value noise, B fine bubble walls (96 cells, about 17 units a repeat as the shader uses it). The shader blends R and G at two scales
    (about 110 and 37 units a repeat) and lets more through the more foam there is; within 150-600 units of the eye the bubbles show in
    it, lit on their walls. Foam on the open water lingers: the ocean's assemble kernel keeps a foam value per texel and cascade, renewed
    where that cascade's surface folds (its Jacobian below 0.75) and fading to a third in 4 game seconds elsewhere, so a crest leaves foam
    behind it (in the displacement texture's fourth channel; **Observed** in pictures, no test). No texture is downloaded: all are baked.
  - *Water colour* (2026-10-08). The shader reads the biome's water colour (the colour map, the sea's outside the world) for more than the
    body tint: `clean` is 0 for dark water (luma under 0.06; the sea outside the world, the west sea and the Black Desert lake around (8700, -13300) have colour-map pixels of about 0.05, **Verified** by reading `watercolourmap.png`) and 1 from luma 0.3,
    cut by up to three quarters by how warm it is (red over blue: rust, olive). Only clean water glows through its crests, and a breaker's
    lip is tinted by the water's colour and only ever brightens the water (before, a fixed green-blue was added to every water, which
    drew a teal line along every wave; the lip only brightens now, so on dark water the steep face reads as the thin dark streak that its
    tilted normal gives, **Observed** at 700 units on the west coast). The foam takes the colour: its albedo is white (0.72, a tenth tinted by the
    water's hue) on clean water and, as the water darkens or turns warm, a dirty grey-brown of the hue at 0.42-0.6 of that brightness (a rust
    on red water); dark water also foams less (the foam amount to 0.7, the opacity from 0.92 to 0.8). The foam is lit as the land is: in the
    game's sky mode by `foamLight` (the diffuse terms of `kenshiLight`: the sun colour times the biome's ambient map alpha times
    `kenshiShadow`, the irradiance cube times the ambient map's colour and the environment factor, no specular; a normal tilted 60 % to
    straight up). It dims at night (**Observed**: at 1:00 on the west coast the foam outlines are gone, where before they stayed as a faint
    outline; the dark water's lower amount and albedo contribute) and follows the biome's ambient map; that it falls into the shadow of cliffs and buildings is wired in (the same `kenshiShadow` as the land)
    but **Unknown** in a picture: the shadow term read 1 on every water pixel of the west-coast view at 16:00 (a debug view), and
    in the pictures at Port South (7 and 18) and the west coast (11 and 16) no cast shadow crosses surf. In the simple sky it is the old formula times the shadow. It was lit by the sun colour and a fixed share of the sky's zenith colour with no shadow, and its albedo
    was a fixed 0.72, which against black water and a dark biome glared. The weather does not dim the sun in the viewer's lighting model
    (the game's own lighting has no weather term, [formats/lighting.md](formats/lighting.md)); what dims the land under rain is its
    wetness, the haze and the biome's ambient map, so the foam is as bright as land of the same albedo. The shadow is looked up for all
    water within 8000 units, outside the foam's branch (the Meitou receiver takes derivatives, defined only in uniform control flow); **Observed** at 1920 × 1080 on a beach (`--fly-benchmark 300`, three interleaved runs each): the
    water pass 0.26-0.27 ms before and after, so its cost is below the noise of a shared GPU.
  - *Surf foam that wears away* (2026-10-08, `WaterRenderer` fragment shader). Until now the surf's coverage (burst, bore, trail, swash) went
    through the open water's world-fixed lace, so the band sat in a static pattern as a solid stripe with a clean cut-out edge. Now the
    surf has its own pattern, read in the wave's frame: q = p + dir (dist − L g) (dir the unit direction to the shore, dist the distance to
    it, L the 110-unit wavelength, g the breaker phase). That is the point's nearest shore point shifted by the phase, so a point carried
    in by a wave keeps its q (the wave moves along the shore's normal, the nearest shore point does not change) and the lace drifts in with
    the wave instead of swimming through a fixed pattern. Three scales of the baked texture (clumps about 270 units, blotches 110, rims 37
    and 12-17 units) are summed; the threshold rises with the foam's age, `age` = 0.6 × the share of the way from the break point to the
    waterline the wave has run (taken at the crest the point trails: g − 0.5 wavelengths) + 0.5 × the distance behind the crest (g from
    0.5 to 0.95) ± a clump jitter, so the fresh break is dense and the foam further in and further back is lace with holes, then patches.
    The bands' own edges are ragged: the clump noise shifts g by up to ±0.11 wavelength and the distance by ±18 units before the burst,
    bore, trail and feather envelopes, and the trail now follows the crest back out to the break point (it used to stop at the break point
    in space, which cut the band's offshore edge off). The swash sheet uses the same age (thinning as it recedes) and its front is
    broken by the lace. The open water's foam (Jacobian, the FFT's lingering foam, the waterline's thin edge, rivers) is unchanged and
    still world-fixed. **Observed** in pictures at Port South's beach (distance 350-2500, water clock at 0, 3 and 5.5 s): the stripe is
    broken into streaks and patches, with feathered edges, thinning towards the shore. **Unknown**: whether it holds at other shores (the
    nearest-shore-point coordinate jitters where the shore field's gradient is noisy, and stretches where a concave shore focuses it).
    Cost (**Observed**, same setup as above, noisy): the water pass 0.28-0.33 ms GPU against 0.33 ms before (CPU 0.19-0.26 against 0.25) (three more texture reads within the shore fade
    distance of the eye).
  - *Living surf foam* (2026-10-09, `WaterRenderer` fragment shader, `popCells`). The wave-frame lace above slid in rigidly like a decal,
    every wave reading the same strip of texture and the age only moving one global threshold, so the same lines appeared and vanished.
    Three changes, all in the surf's branch:
    - *A wave of its own.* `Shore.wave` keeps the breaker's number (floor of the phase count, wrapping at 4096: the wave whose crest is at
      g 0.5 of the point's cycle). The lookup q0 = p + dir (dist − L g) is offset by a hash of it (0 to 997 units on each axis), so each
      wave reads a different part of the tiled texture. The number changes at g 0/1, in the trough, where the envelopes (burst and bore
      from g 0.42, trail to g 0.95, ± the clump's 0.11) leave no foam; **Observed** with a temporary debug view of the number: the
      boundaries are smooth lines in the clear water between the bands, and screenshots of the same spot at water clock 0, 3 and 5.5 s
      show differently patterned waves.
    - *Cells that burst.* An animated cellular pattern in the (seeded, stretched) wave frame, cells about 22 units (3 × 3 neighbours, one
      scale, points jittering on 10 and 20 s cycles), gives each cell a lifetime between 0.25 and 1 of the foam's age; once the age passes
      it the cell's middle is cut out of the lace (−0.45 × smoothstep(0.04, 0.3, F2 − F1) over an age of 0.12), the walls between cells
      untouched. The global threshold now rises 0.3 × age instead of 0.475, so holes open cell by cell while the walls linger as lace. A
      first try that also brightened the walls of burst cells drew thin forked "antlers" on the bands' back edges where little else was
      left; the walls are now only spared, never added.
    - *Stretched with travel.* The along-normal part of the lookup for the blotches and rims is scaled about the crest by 1 / S, S = 1 + 1.1
      × ageS² (ageS: the age without the clump jitter), so fresh foam is round and the old foam behind the crest and further in runs out
      into streaks along the direction of travel (up to 2.1 times). The texture gradients are scaled the same way; the clump (the bands'
      envelope) is not stretched. The lookup stays in the wave frame, so nothing smears and no cross-faded phases are needed.

    **Observed** in pictures at Port South's beach (distance 250-2500, water clock 0-6.5 s): the foam's pattern differs from wave to wave
    and from moment to moment, with streaky back edges; the white area over five views is within 1% of before (counted bright pixels,
    −9% to +20% per view). Cost (**Observed**, a temporary GPU timestamp pair round the water draw, 1600 × 900, three runs each, medians):
    0.286 / 0.292 / 0.262 ms at distance 350 (clock 0, 3) and 800, against 0.271 / 0.273 / 0.260 ms before: about +0.015-0.02 ms near the
    shore. **Unknown**: how it moves in a running game (judged from stills a fraction of a second apart).
  - *Rivers* (2026-10-08, `RiverFlowBake`, `FlowMapTests`, `RiverFlowTests`). The game's `flowmap.png` does not follow the river channels ([formats/terrain.md](formats/terrain.md)), so the viewer bakes its own map at load from the whole-world heights the world already holds (one texel per flow-map texel, 144 units, RGBA8, 16 MB, about 0.45 s on the CPU, `MEITOU_RIVERS=0` leaves it out for comparisons). Water is a river where its half-width (the largest distance to land within 5 texels) is under 3 texels (fading out to 6: a lake or the sea is not a river); the river texels' axis is the principal axis of the river texels within 10 texels, and has to be clearly elongated (ponds and marsh are not); the sense comes from the valley: the lowest land beside each river texel is regressed along the axis, the senses are made consistent along each connected river (spread from texel to texel), and one vote of the clamped slopes per river decides which way is down (a river shorter than 24 texels, or whose vote is not clearly downhill, is no river). R, G the direction, B the half-width, A the weight, dilated three texels onto the banks. In the shader (`Shore.river`, `flow`, `speed`): the direction is refined by the shore field's channel axis (sampled 40 units each way) only within about 25 degrees of the map and in a narrow channel (half-width under 2-4 texels; round a pond the isolines circle and the first version, which trusted them within 50 degrees, made the water spin in a pinwheel); where neighbouring map texels disagree the filtered direction shortens and the river fades there to still water (before, it snapped to +x along the texel grid: the straight seams). The bake smooths the fitted directions (4 passes of 3 × 3 weighted averaging) and fades the weight where the directions within 2 texels disagree (mean length under 0.55-0.85); **Verified** on the game's heights (`RiverFlowWorldTests`): of about 16 000 neighbouring texel pairs with weight none turns by more than 37 degrees (2 before the smoothing, so the seams and the pinwheel came from the shader); the speed falls from 30 (narrow) to 14 units a second (wide), times 1.5 in the shallows to 0.8 beyond 60 units depth. In a river the normal map's three layers are carried with the flow by two cross-faded phases (2.5 s cycle, renewed where their weight is zero, so the pattern does not stretch where the flow turns); the foam's two scales are stretched three and a half times along the flow into streaks and carried the same way, with a thin trace everywhere and a white rush in rapids (fast, shallow stretches, in patches of the foam noise); the open waves, the breakers and the swash are switched off by the river weight (`oceanScale` and the shore's exposure are multiplied by 1 - weight). `MEITOU_WATER_DEBUG=2` shows the weight (red) and flow direction (green, blue). **Observed** in pictures (SW rivers at (-65016, 87768) and (-99144, 97416); the NW river): the stretches carry coherent directions along their bends, and the surface moves (two screenshots 0.8 s apart differ by a mean of 2.9 of 255 over the picture); the direction's sign is **Unknown** to be right (no ground truth: the game's own rivers flow nowhere visible). The real rivers are shallow streams (about a decimetre to a metre of water) that mostly show the ground through them, so the effect is subtle; coverage is partial: rivers whose water mask is broken into pieces under 24 texels long at this resolution, or whose banks give no clear fall, stay still. Cost (**Observed**, `--fly-pipelined --fly-benchmark 300` at (-99144, 97416), 1920 × 1080, RTX 4070 shared with other jobs): the water pass 0.08 ms with rivers against 0.06 ms without (stage line 0.16 against 0.11); +16 MB video memory.
  - *Light and colour* (2026-10-08). **Refraction**: before each of its draws (one per depth slice) the scene pass ends, the slice's colour
    is copied at half size (`CaptureRefraction`, one blit), and the pass opens again; the water looks the copy up bent by its normal (less
    in thin water and far off) and composites itself opaque, instead of blending over the scene. `--no-water-refraction` keeps the
    blending (the game's alpha from depth). **Absorption**: what is under the water is dimmed per channel by exp(−σ L), σ = the biome's
    opacity (its parameter map's alpha, the game's alpha per unit of depth) × (4.5, 1.6, 1.1), L the path through the water to the terrain
    (so in clear water red goes first: turquoise shallows over sand, dark deep water; a strongly coloured biome water, by the saturation
    of its colour map, filters towards its own colour instead: olive swamps, red lakes), and the water's own colour scatters in for what is absorbed; the
    floor fades out by 4400 units, as the game's water turns opaque at 4000. **Clarity** (2026-10-10, Tab panel, Meitou only): L is the depth
    over view.y (floored at 0.05), so at grazing angles it is up to 20 times the depth and distant shallows read as opaque body colour, which
    is why the water looked see-through only near the eye. "Water clarity x" divides σ (also the alpha of the `--no-water-refraction`
    path; default 0.5 since 2026-10-10, was 1, the game's opacity; 0.25–8); "Far water clarity" k makes L = depth / view.y^(1−k) (0 the true slant path; 1 the plain depth; default 0.25).
    "Water see-through distance" D (default 10000, the game 4000; 1000–100000) replaces the fixed 4000–4400 cutoff: the floor fades out
    over D to 1.5 D (smoothstep) when refracted, the blended path goes opaque over 0.9 D to 1.25 D. From high above the game's 4000 left
    only a clear disc under the camera, darker water all round it.
    **Glint occlusion** (2026-10-10): the sun's specular is multiplied by how much the mirror shows sky there (its luminance over the
    analytic sky's in the same direction, smoothstep 0.5–0.85), as the game multiplies its specular by the reflection's brightness; before,
    the low sun's streak ran straight through the dark reflection of the hills in front of it.
    **Caustics**: on the floor seen through shallow water, two
    drifting copies of the foam lace (cell rims) multiplied and read a mip or three down, the blur growing with the depth (0.15 mips a unit),
    at a quarter of the strength they had until 2026-10-08 (they covered a whole beach); gone in the shallows (zero under 1.5 units, full from 7),
    fading over 18 units of depth, out between 500 and 2500 units from the eye, and in rain (to a fifth at the heaviest). **Crest light**: where
    an ocean crest stands more than about 1 unit high and the eye looks towards the sun, a glow of light through the thin top, in the
    water's own colour (before 2026-10-08 a fixed green-blue, teal on every water): a faint blue-green is added only to *clear* water (see
    "Water colour" above); none in water under 6 units deep (full at 40), and dimmed in rain.
    **Glitter**: beyond 800-4000 units a broader glint lobe let through where a fine moving pattern peaks, so the far sea sparkles in a
    band instead of fading flat. All of it in the Meitou shader only.
    **Glint under clouds** (2026-10-10): the game's specular ignores the weather, so in Shark's heavy rain (`Heavy_Rain`, clouds density 1) the
    swamp water still mirrored a bright sun. The Meitou water scales the glint's colour by `1 − 0.95 · smoothstep(0.3, 1, o)` (`WaterRenderer.
    GlintCloudFade`, `GlintCloudShade`), `o` how overcast the sky is: the cloud density after the weather's transition (`--clouds` wins) or the
    rain's `rainAmount` where that is more (`WorldFrame.Overcast`, [render-shafts.md](render-shafts.md) "Cloud cover"); `swamp rain no wind` keeps
    about 24 % of the glint. The Faithful water keeps it whole.
    *Observed* the same day at Shark in `Heavy_Rain` at 16:00: the owner judged it good.
  - *Clock.* Everything runs on the game clock (`GameHours`; the viewer's heat-haze hours, still for a picture): paused water stands
    still, game speed speeds it up. `--water-seconds <s>` starts it at s game seconds, for pictures of a moment. `MEITOU_WATER_DEBUG=1`
    shows the shore fields (red distance / 400, green breaker phase, blue exposure).
  - *Cost* (**Observed** 2026-10-08, 1920 × 1080, Port South's beach at distance 900, `--fly-benchmark 300` with `MEITOU_PASS_STATS=1` and
    the clock running, one run each, so noisy): the ocean's compute pass 0.21-0.37 ms GPU (256², timestamps round it; `--water-ocean 128`
    is a quarter of the transform), the water pass 0.30 ms GPU with refraction (the copies included), 0.12 ms without, against 0.03 ms for
    the Faithful water; the shore bake 40-95 ms on a worker thread. Memory: about 21 MB (spectrum 6, work buffers 9,
    textures 4) plus the 8 MB shore field (up to about 32 MB since 2026-10-10: two recycled textures kept for the next bake, one being written, and the shown one). The reflection pass is unchanged (the mirror stays the plane at Y = 100).
  - *Shore distance field* (`ShoreField`, `ShoreBake`; read by the water shader since 2026-10-08). A 1024² grid of 10-unit texels (±5120 units round the eye) holding (signed distance to the waterline, exposure). The
    waterline is where 100 − height changes sign between neighbouring texels, placed by linear interpolation of that difference, so it is
    sub-texel; the distance is to those points (8SSEDT: two sweeps carrying the nearest crossing point, O(N²)), positive over water and
    negative over land, clamped to ±4000, then one 3×3 binomial Gaussian so the gradient has no kinks. **Exposure** is
    smoothstep(600, 1200, reach), reach = the largest distance-to-shore of any water within 1500 units (measured on 8-texel blocks, a disc
    dilation over them, bilinearly upsampled): a pond, swamp channel or narrow bay whose water is never more than about 600 units from a
    shore within 1500 units is 0, open sea (1200 units of open water within 1500) is 1 (until 2026-10-08 200 / 450 within 500, which let
    Shark's town pond, about 400 units from shore at its middle, get surf; **Observed** in a picture). **Islets** get no surf: the exposure is multiplied by smoothstep(π·250², π·450², area of the
    land mass of the nearest shore), the land masses found by a flood fill of the land texels (4-connected; one touching the grid's edge
    counts as large), averaged per 8-texel block and over the 3×3 blocks round it before the upsample, so the surf fades over a few hundred
    units where the nearest shore switches from an islet to the coast behind it. A rock in open sea is as exposed as the coast, and until
    then breakers ringed it on every side and met in the middle (**Observed**, the user's picture, and at an islet of about 150 units
    off Port South at (89750, −93430): a ring before, no surf after, the mainland's surf beside it kept). `ShoreFieldTests` checks a
    150-unit islet (exposure under 0.05 round it) against a 700-unit island (over 0.95). It adds about 15 ms to the bake (70-77 ms against
    56-81, **Observed**, the bench test). Past the grid's edge the water counts as open sea (reach =
    max) when the nearest edge block is water, else as land. The bake runs on a worker thread from a `HeightSnapshot` (an immutable copy of
    the terrain's coarse grid, fine window and band, taken on the render thread by `TerrainRenderer.Snapshot()`; `HeightAt` is defined
    through it); `ShoreField.Update(eye, snapshot)` starts a bake when nothing is baked yet or the eye is more than 2560 units (a quarter of
    the width) from the centre in X or Z, the centre snapped to whole texels so the field does not swim, and uploads the finished grid
    as an RG32F texture (8 MB, linear, clamped); the old texture is released four frames later (since 2026-10-10 kept for the next
    bake, see "Upload cost" below). **Verified** (unit tests
    on synthetic terrain, `ShoreFieldTests`): on a straight beach at three angles and on a circular island the distance is within 3 units
    (0.3 texel) of the true one with the right sign; a 150-unit pond and the inside of a 300-unit-wide channel have exposure under 0.05,
    open sea more than 0.95; the gradient direction turns less than 30 degrees between neighbouring water texels near a straight shore.
    **Observed** (2026-10-08, desktop CPU with 12 logical cores, Release, 1024², a height function of two sines and a plane): the bake
    took about 63 ms per grid (mean of 5; the height sampling alone about 10 ms; the sweeps ran 35 ms with managed arrays, before they were
    made pointer-based on one packed array). The rebake rule means one bake per 2560 units flown. The GPU upload was not measured (no GPU test).
  - *Upload cost* (2026-10-10; the numbers and the command are in [renderer-native.md](renderer-native.md) 8.21). **Cause** of the many
    8 MB uploads: not a bug but the rule above at speed: at 150 units a frame (9000 units a second) the eye leaves the quarter-width box every
    17 frames, so the 3600-frame fast flight rebaked and re-uploaded 180 times (**Observed**, `upload shore field` row of the spikes table:
    180 uploads, 1440 MB, 8 MB in one frame, 4 to 6 ms of staging copy on the render thread), and **131 of 184 bakes (71 %) came out with no
    waterline in the field at all** (inland, or open sea), which is one value everywhere. **Fixed**: `ShoreGrid.IsUniform` finds those on the
    worker and the field is then a 1 x 1 R32G32 texture (the shader clamps to the edge and takes `Rect` as before, so every texel reads the
    same: pictures identical, `ShoreFieldTests`); a field with a coast is laid out (interleaved) on the worker into one of two reused arrays
    and written to a recycled 1024² texture (up to two retired ones are kept) 256 rows (2 MB) a frame, the new texture replacing the shown
    one, with `Rect`, once whole (the very first field, and a screenshot's, is written at once); no new result is taken while an upload is in
    progress. `ShoreBake.Scratch` keeps the bake's arrays between bakes (the bake allocated about 44 MB each, `ShoreFieldTests` checks
    59 KB instead of 2.3 MB per bake after the first). **Observed** after: 53 uploads of 2 MB a frame (424 MB in the run) and 131 one-texel
    fields; the field on the render thread costs 1.0 to 1.3 ms in a frame that writes rows and nothing worth a log line otherwise, except the first
    one or two fields of a run (2 to 3 ms: the first, whole, write) (`water     shore field ...` lines of `MEITOU_STREAM_LOG=1`). Pictures
    at Port South (coast) and at the flight's start: `image-diff` against the same-commit baseline, mean 0, max 0.

Verified with screenshots (2026-10-04, saved outside the repo): Shark (houses on stilts and walkways over swamp water), Port North (coast with sun glitter), the whole world (sea around the land), and The Hub towards the horizon (terrain to the edge of the map, fading into the haze); the sky part is in [render-sky.md](render-sky.md).

## Reflection cost: shadows and multisampling (2026-10-09)

- **No sun shadows in the reflection** (Meitou; `ReflectionPass.NoShadows`, default on, `--ab refl-shadows`; Faithful shadows keep them). While the pass records, `GpuContext.Globals.OverrideBlock` swaps the frame's `KenshiShadowReceiver` block for an all-zero one (the shadows-off block), so every receiver (terrain, objects, foliage, the atmosphere's sun term) sees a lit surface and `kenshiShadow` returns 1 before touching the map; no new pipelines, no shader change. What the game does for its reflection target is **Unknown**, so Faithful (`ShadowPass.Meitou` false) keeps them as before. **Observed** (swamp target view, `--upscaler dlss --render-scale native`, 600 frames, `--ab refl-shadows`, period 1 and 16): the `reflection` GPU stage 0.53 to 0.46 ms (-0.06 +-0.01 and -0.06 +-0.02, median -0.05/-0.06); less than the 0.15 to 0.27 ms the receiver was estimated to cost in the pass (shadows.md), because the half-resolution mirror pass is mostly vertex, draw and fill bound. The frame total moved -0.33 and -0.17 ms in those runs but that includes shadow-scheduling noise from other work and is not attributable. Picture (`--upscaler off --no-particles --bench-motion still`, `MEITOU_REFL_AGE=0` so each side draws its own reflection): 1,692 pixels differ by 1/255 or more, none by 4, max 3 (the swamp's shadows are faint in the mirror; views with long tree shadows on the shore were not compared: **Unknown**).
- **Multisampling** (`ReflectionPass.Samples`, default `DefaultSamples` = 4, unchanged; `--reflection-samples n`, `--ab refl-msaa` = the option's value against `MEITOU_REFL_AB_SAMPLES`, default 1). **Observed** (same view, 600 frames, `--ab refl-msaa`): the `reflection` stage 4 samples 0.46 ms, 2 samples 0.43 ms (-0.03 +-0.01), 1 sample 0.38 ms (4 against 1: -0.08 +-0.01, 2 against 1: -0.04 +-0.01 paired); so 4x costs 0.08 ms more than none at half resolution. Picture (stills, as above): 4 against 1: 14,005 pixels differ by 1/255 or more, 3 by 4, max 10; 4 against 2: 8,919, none by 4, max 3; 2 against 1: 13,334, 3 by 4, max 10. The swamp orbit has little mirrored shoreline, so the stair steps the earlier note describes were not looked for here (**Unknown** in views with a straight shore or buildings on it); the default is left at 4 for the user to decide.
