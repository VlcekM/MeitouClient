# Shadows

`ShadowPass`, `ShadowLod`, `ShadowFogCap`, `ShadowZoneBox`; original cascade/settings/shader
rules: [formats/shadows.md](formats/shadows.md). Engine API: [renderer-native](renderer-native.md).

## Modes and resources

Faithful reproduces researched cascade fits/splits/casters/receiver. Meitou fits useful camera
ranges, stable grids and soft filtering, plus terrain-horizon shadow beyond local caster range.
Shadow-quality default2 uses4096²; Tab supports1024/2048/4096. Cascade arrays include a fifth
Meitou cascade; consumers must use current layer count/splits, not assume four tiles everywhere.
Depth-array views, samplers, receiver blocks and frame-global publication must agree.
Shadow maps keep conventional depth (cleared to 1, compared Less), unlike the scene's reversed one; casters write the
cascade matrix's 0..1 depth as `gl_FragCoord.z` on both backends (no depth scale; `uShadowBias.w` is written 1, unused).

Meitou bias and stability (`MeitouShadowFit`, `ShadowPass.Meitou`): caster bias in the cascade's
texels, 1 fixed plus 2 x the slope capped at 4 (the game's 0.75 world units and up to 2 % of the
box depth detached post and wall shadows under a low sun); the receiver's 0.5-2 texel normal offset
keeps acne off (**Observed**, Hub at 17:30, no acne at 1080p). Texel grids are anchored at the
camera's focus point, moved only while the sun turns and the point is 200+ units away (anchored at
the eye, the first cascade's grid slid boom x sun turn under the squad). Least filter radius 1.5
texels: a 0.06° sun turn changed 0.36 % of a Hub view by more than 8 levels, 0.56 % at 1 texel.

## Scheduling and casters

Near cascades refresh rapidly; far cascades cache/stagger according to camera/sun changes.
`shadow-spread` budgets one due far cascade per frame; invalidation must not reuse an incompatible
matrix. GPU culls and CPU zone/group work lists are per cascade. `shadow-zone-box` excludes
nonintersecting zone bounds before building lists. Record secondaries in stable caster order.
Object casters keep only what the colour pass draws: the dither range at its middle (one of two
blending levels; a distant town only beyond the camera's fade). Ignoring it, the Hub's baked distant
mesh (the intact town) cast roof shadows into its ruins and platforms at every distance (**Observed**,
Hub 14:00: matches `--distant-range 0` after the fix, not before).

**Flights** (Meitou, engine choice): while the host flies the camera (a character flight or a squad tour hop under way, `WorldFrame.CameraFlying`, not the hold between hops, nor a cut or a pan) the far cascades are not drawn, a map drawn now covering the wrong ground the next frame; the near cascade (and cascade 1 of five) is. A far cascade drawn before the flight is stale: the published cascades stop before the first stale one, so the receivers shadow only within the near cascade's depth (plus the terrain and landmark maps) until the camera lands and the far cascades are drawn again as uncovered, over the budget's frames (one a frame, forced after three). The log's shadow line and the play bench's summary count the frames skipped (**Observed**: the flight phases of the flights segment, 0 on the settle, idle, orbit, zoom, pan, travel and overview segments). An eye-speed rule was tried first (15,000 units a second on two frames running) and fired on eased camera cuts.

`shadow-coarse` / generated object levels select geometry against cascade texel size, not colour
pixel size. Preserve silhouette and alpha coverage; impostor casters have their own fade/plane.
`shadow-fogcap` can shorten reach only when placed fog conservatively hides every relevant
receiver. Shadow-map rendering and shadow receiving are distinct costs; omit receiver work only
where lighting cannot contribute. Minimum-radius impostor filter omits blocker search.

Receiver cost (`kenshiShadowMeitou`, **Observed** play bench, user settings, RTX 4090): the cascade
term is about 0.45 ms of the terrain's 2.3 ms. The blocker search pays for itself: it returns lit
when nothing is nearer the sun, and without it the terrain took 0.2 ms more. The cascade blend band,
terrain term and landmark term each cost under 0.05 ms. The full-tier filter takes the disk's even
points first and returns when all 8 agree, adding the odd 8 only in a penumbra (same sum there):
terrain -0.1 ms, screenshots within 7 levels.

## Traced sun

Port of KenshiNative's traced sun shadows (`knSunShadowRay`, `kn_grass_card.hlsl`); Enhancement `traced`, off by default. `TracedSunPass`, `TracedSunShaders`,
`TracedGrass`, `PostProcess.TracedSun`, `kenshiLight`. **Unknown** (not run on a GPU): look, cost, memory, every constant below.

Runs when the `gi` scene and the GI resolve run, on the Meitou shadows, with ray queries (`WorldFrame.BeginFrame`); else the frame is the cascades' as ever. So `traced` needs
`gi`, and Faithful shadows leave it inert. Metal: never.

**Frame.** Near the eye `kenshiLight` leaves the sun out of the colour and writes it, shadowless but for clouds, relief and the character layers, to a third scene attachment
(RGBA16F, location 2, `WithAlbedo`), before the haze. Bit 10 of the surface target marks such a pixel, bits 11-12 its receiver (0 solid, 1 grass, 2 foliage, 3 character),
for every program including those that read the probes themselves (they leave bit 8 clear). After the characters, before `CaptureScene` (refraction sees the sun), the pass
closes: a compute pass traces one ray a pixel into an RGBA8 mask, only where the term is not zero; a fullscreen pass adds term x mask x the haze's multiplier at that
position (`atmoApplyCoefficients`, one per pixel: the haze is linear in the colour, so no fragment evaluates it twice). The reflection, the water's foam, impostors (`atmoReceiver`
-1: the quad is not the surface) keep the cascades.

**Range.** The share traced falls from 1 to 0 over the band where the last cascade with a character layer blends into the next, by view depth, as the cascades do:
all but the last two cascades (`ShadowPass.ActorLayers`; five cascades: about 1500 units at range 10000, less in a close-up). Past it the cascades, terrain and landmark terms.

**Characters** are not in the traced scene. Each of those cascades has a second layer in the array (past the cascades, `ActorDraw`) with its matrix and only the characters
(debris and birds cast nothing in any mode); the fragments read those layers, and their blocker layers, for the traced share (`msActors`). The cascades stay whole because the
shafts, the particles, the foam, the reflection and the rest of the frame read them, so tracing saves none of their cost. At 4096 the three layers add 192 MB of depth and 48 MB of blockers. Skinned structures: not built.

**Rays** go to a point of the sun's disc (radius tangent 0.015, KenshiNative's size; with a temporal upscaler anew each frame by interleaved noise, without one the disc's centre, hard).
There is no receiver identity as in KenshiNative's G-buffer, so what it skips by name is skipped along the ray. Solids leave the plane the depth shows by half a
pixel's footprint, slope-scaled; cards leave from the sun's face and skip 3 footprints plus 0.5. Four queries, each after the last missed, cheapest shadow first:
- objects, foliage, small foliage (`0x10`: radius 6 and up within 3000, the sun's alone), alpha-tested in candidates; a part an object's material cuts (its map in the record's sun fields)
  reports candidates through `ForceNoOpaque`, for this ray and at no cost to what the probes see of it;
- the fine terrain grid from 24 units, the coarse (`0x08`) from 320: a grid's own facets are not the drawn ground. Back faces are skipped as the probes skip them, but not from interiors (bit 9): a
  room under a hillside stays dark;
- grass (`0x20`), only as far as the ray can stay under the tallest blade, faded by noise over the last quarter of its reach.

**Grass.** `TracedGrass`: patches of pages within 1200 across the ground, nearest first, at most 4 builds and 600k triangles a frame and 512 MB, 512 instances, unused for 600 frames dropped; a
kernel expands the arena's blades at rest into a vertex buffer (65536 blades a patch at most), one rigid instance each. Blades whose foot or top is in a drawn interior mask's box (building space,
tighter than the X/Z boxes; KenshiNative's fix, by box, not by triangle: over-cuts a non-convex mask's concavity) collapse to a point. A changed mask set or density rebuilds the page. A blade's ray ignores blades within 4 units,
fading the next 8 in by noise: the blind range covers the sway (the traced card is at rest) and overlapping twin cards. Grass shadows do not sway. The probes never see the grass (mask `0x0F`).

**Interiors.** The objects are `RayInstances`' (no construction sites, hidden floors, closed-around buildings), so a site casts no traced shadow.

**Limits.** Blood on surfaces (`RecordSurfaces`) blends before the sun is added, which then lands on it. The objects' traced transforms and level are the GI's: rotors and doors are frozen as the GI sees them; an object's texture
tile is not read in the alpha test. One ray a pixel relies on the upscaler: no denoiser.

## Validation and limits

Check low sun, cutout trees, interiors, cascade boundaries, camera/clock cuts, reflection and
far terrain. Compare redraw spikes/p99 as well as mean; cached passes can hide costs in averages.
**Observed** moving swamp views: far-cascade redraws cause spikes; scheduling/coarser casters can
reduce tails without a significant mean gain. Fog-cap and extra-cascade rules must preserve
clear views and Faithful behaviour. Source formulas/unknowns remain in the format reference.
