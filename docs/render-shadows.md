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

## Validation and limits

Check low sun, cutout trees, interiors, cascade boundaries, camera/clock cuts, reflection and
far terrain. Compare redraw spikes/p99 as well as mean; cached passes can hide costs in averages.
**Observed** moving swamp views: far-cascade redraws cause spikes; scheduling/coarser casters can
reduce tails without a significant mean gain. Fog-cap and extra-cascade rules must preserve
clear views and Faithful behaviour. Source formulas/unknowns remain in the format reference.
