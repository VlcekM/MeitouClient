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

Enhancement `traced`, off by default; the sun-shadow setting's `Traced` (`SunShadowMode` Off | Cascades | Traced, `SunShadows.Get/Set`: Off draws no maps and traces nothing,
the sun shaded by clouds and the ground's relief alone; the shafts and lit dust then see no shadow). From KenshiNative's traced sun (`knSunShadowRay`, `kn_grass_card.hlsl`).
`TracedSunPass`, `TracedSunShaders`, `TracedGrass`, `TracedCharacters`, `PostProcess.TracedSun`, `kenshiLight`.

Runs when the `gi` scene and the GI resolve run, on the Meitou shadows, with ray queries and storage for RGBA16F (`WorldFrame.BeginFrame`); else the frame is the cascades'.
Metal: never. `--debug-shadows 4` draws over the scene: blue where the cascades light the sun, green where the rays let it through, red where they block it, dim where no sun
falls; with it the log prints every 120th frame whether the frame traces and why (`LogTraced`). **Observed** (siwa-test6 save, 08:40): traced over every lit surface of the view; against the cascades its mean change is 0.17/255 there, a close-up whose first cascade is already fine.

**Frame.** `kenshiLight` leaves the sun out of the colour of every lit surface but impostors (`atmoReceiver` -1: the quad is not the surface), at every depth
(`ShadowPass.TracedWhole`), and writes it, shadowless but for clouds, relief, the terrain map and the landmark map (the cascades left out, `msTraced`), to a third scene
attachment (RGBA16F, location 2, `WithAlbedo`), before the haze. Bit 16 of the surface target (RGBA32UI) marks such a pixel, bits 17-18 its receiver (0 solid, 1 grass,
2 foliage, 3 character). After the characters, before `CaptureScene` (refraction sees the sun), a compute pass traces one ray a pixel into an RGBA16F mask (visibility,
blocker distance), the penumbra filter softens it, and a fullscreen pass adds term x mask x the haze's multiplier (`atmoApplyCoefficients`). The cascades are still drawn for
what reads them: the light shafts, the lit dust, the water's foam, the skin's light entry, the reflection, impostors; water writes no traced pixel.

**Rays** go to the sun's centre, the same every frame (no noise for the upscaler to average, no history to trail). There is no receiver identity as in KenshiNative's
G-buffer, so what it skips by name is skipped along the ray. Solids leave the plane the depth shows by half a pixel's footprint, slope-scaled; cards leave from the sun's face
and skip 3 footprints plus 0.5. Queries, each after the last missed:
- objects (as far as the shadows' range, at least the probes' 6000), foliage, small foliage (`0x10`: radius 6 and up within 3000), characters (`0x40`), alpha-tested in
  candidates; a part an object's material cuts reports candidates through `ForceNoOpaque`;
- the fine terrain grid from a point 3 units (+0.2 % of the distance) above the surface, along the whole ray: the grid is not the drawn ground (displacement, curved outline),
  and the 24-unit blind range it had let the ground at a cliff's foot see the sun through the cliff; the coarse grid (`0x08`) from 320 along the ray. Back faces are skipped
  as the probes skip them, but not from interiors (bit 9);
- grass (`0x20`), only as far as the ray can stay under the tallest blade, fading out by amount (not noise) over the last quarter of its reach.

**Penumbra** (`TracedSunShaders.Filter`, no history): round each traced pixel 24 fixed sunflower taps within 12 pixels on its own surface (within its plane by half the search
footprint) give a mean blocker distance; the penumbra radius is that times the sun's tangent (0.00465, the real 0.267 degrees; KenshiNative's 0.015 widened far penumbrae
threefold), at most 16 pixels, and the mask is averaged over 24 taps of that disc on the same surface. A pixel with no blocker round it keeps its ray. A still view gives the
same mask every frame; under motion nothing trails. Contact hardening follows from the blocker distance.

**Characters** are in the rays (`TracedCharacters`, [render-lights](render-lights.md#traced-lamp-shadows)): the nearest 128 within 4000 whose shadow can be in view, at the level the
colour pass draws, skinned with the frame's palettes, so a surface's ray leaves the same triangles it drew and a body shadows itself only where it does (armour layers, hair over
a face). No character layers are drawn.

**Grass.** `TracedGrass`: patches of pages within the reach across the ground, nearest first, at most 4 builds and 600k triangles a frame and 512 MB, 1024 instances, unused
for 600 frames dropped. The reach is the traced range's view depth taken to the frame's corners (`ShadowPass.TracedDepth`), within 1200 to 2400; the rays test the same reach
by distance from the eye. **Observed** (zone 14,30, 16:00, before this rule): a fixed 1200 by distance from the eye, faded from 900, left everything past 900 without grass
shadows: at boom 1400 the whole view; at boom 900 the far half. Now 152 patches, 90 MB at boom 900. A kernel expands the arena's blades at rest into a vertex buffer (65536
blades a patch at most), one rigid instance each. Blades whose foot or top is in a drawn interior mask's box collapse to a point (KenshiNative's fix, by box: over-cuts a
non-convex mask's concavity). A changed mask set or density rebuilds the page. A blade's ray ignores blades within 8 units (the 4-unit blind range and half its fade): the
sway (the traced card is at rest) and overlapping twin cards. Grass shadows do not sway.

**Not traced** (they cast no sun shadow while traced): construction sites (drawn clipped by their progress; the whole mesh would cast the finished building), debris
(bolts, dropped items) and birds (no caster in any mode), particles, objects past the range or drawn only as impostors, foliage past 4000 (small foliage past 3000), grass
past the reach, characters past the 128 nearest or 4000. Landmarks keep their map.

**Limits.** Blood on surfaces (`RecordSurfaces`) blends before the sun is added. The objects' traced transforms and level are the GI's: rotors and doors are frozen as the
GI sees them; an object's texture tile is not read in the alpha test. The penumbra filter works on what the screen shows: a blocker's shadow whose taps fall on another
surface keeps its hard edge there. **Unknown**: the saw-toothed terrain shadows reported on displaced dunes were not reproduced offscreen (dunes round the siwa-test6 save at
06:00-07:00 and 18:24); the fine grid was 16 apart over the heightmap's 18 (bilinear facets), now 8 apart through a Catmull-Rom surface. **Observed** costs: see Measured.

**Observed** (RTX 4090, 1920 x 1080 with DLSS; the GPU shared with another job, runs varied 2-4x): rays 0.05-0.43 ms, penumbra filter 0.08-0.39 ms (most in a close-up,
where penumbrae are widest); the cascades, still drawn, 0.5-1.3 ms. With a still camera the frame-to-frame change is DLSS's own: 0.12/255 mean and 0.11 % of pixels over 4,
traced or cascades alike (siwa-test6, six frames).

## Validation and limits

Check low sun, cutout trees, interiors, cascade boundaries, camera/clock cuts, reflection and
far terrain. Compare redraw spikes/p99 as well as mean; cached passes can hide costs in averages.
**Observed** moving swamp views: far-cascade redraws cause spikes; scheduling/coarser casters can
reduce tails without a significant mean gain. Fog-cap and extra-cascade rules must preserve
clear views and Faithful behaviour. Source formulas/unknowns remain in the format reference.
