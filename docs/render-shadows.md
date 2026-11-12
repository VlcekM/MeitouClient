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

Meitou bias and stability (`MeitouShadowFit`, `ShadowPass.Meitou`, `msCascade`): the casters are drawn
with no bias and the receiver carries it all: a normal offset of 0.25-0.5 texel, its plane's depth
change over one texel each way and a quarter texel. Building parts cast with both sides
(`WorldObjectRenderer.CastersBothSides`; the faithful CSM keeps the colour pass's culling).
**Observed** (a Shek town at 15:10, cascades against the traced sun, pixels lit by more than 25/255
where the traced sun is dark): 0.182 % with the caster bias of before (in fact 2 texels fixed plus
2 x the slope capped at 8, with a normal offset of 0.5-2 texels: lit bands under stair treads and cart
sides), 0.058 % with both sides cast (the bands at wall feet and beside stairs, which no bias changed:
**Unknown** which open meshes let the sun through), 0.013 % with both. No acne seen on a face close-up,
dunes, a town from afar under a low sun. Texel grids are anchored at the
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

Enhancement `traced`, off by default (on from the Ultra tier); what a character wears casts as cloth (`CasterCutout`: its cut-out read at mip 4, kept from a quarter of the threshold: hole by hole a hood's weave let single rays through, a stipple of sun on the face under it, **Observed**); its grass shadows have their own switch (`PostOptions.TracedGrass`, "Traced grass shadows", on at Extreme only: **Observed**, RTX 4090 1440p native, a grass plain, the scene's traced pass 4.4-6.5 ms with them and 1.7-1.8 ms without; a limit on their range down to 600 units and a larger least caster size measured no gain); the sun-shadow setting's `Traced` (`SunShadowMode` Off | Cascades | Traced, `SunShadows.Get/Set`). Off draws no maps and traces
nothing: every receiver reads lit (surfaces, impostors, the reflection, foam, the lit dust), the light shafts and the contact shadows do not run, the clouds' shadow and the
ground's relief self-shadow stay, and the probes' sun term keeps its own rays (the GI is not a shadow setting). From KenshiNative's traced sun (`knSunShadowRay`, `kn_grass_card.hlsl`).
`TracedSunPass`, `TracedSunShaders`, `TracedGrass`, `TracedCharacters`, `PostProcess.TracedSun`, `kenshiLight`.

Runs when the `gi` scene and the GI resolve run, on the Meitou shadows, with ray queries and storage for RGBA16F (`WorldFrame.BeginFrame`); else the frame is the cascades'.
Metal: never. `--debug-shadows 4` draws over the scene: blue where the cascades light the sun, green where the rays let it through, red where they block it, dim where no sun
falls; with it the log prints every 120th frame whether the frame traces and why (`LogTraced`). **Observed** (siwa-test6 save, 08:40): traced over every lit surface of the view; against the cascades its mean change is 0.17/255 there, a close-up whose first cascade is already fine.

**Frame.** `kenshiLight` leaves the sun out of the colour of every lit surface but impostors (`atmoReceiver` -1: the quad is not the surface), at every depth
(`ShadowPass.TracedWhole`), and writes it, shadowless but for clouds, relief, the terrain map and the landmark map (the cascades left out, `msTraced`), to a third scene
attachment (RGBA16F, location 2, `WithAlbedo`), before the haze. Bit 16 of the surface target (RGBA32UI) marks such a pixel, bits 17-18 its receiver (0 solid, 1 grass,
2 foliage, 3 character). After the characters, before `CaptureScene` (refraction sees the sun), a compute pass traces one ray a pixel into an RGBA16F mask (visibility,
blocker distance), the penumbra filter softens it, and a fullscreen pass adds term x mask x the haze's multiplier (`atmoApplyCoefficients`).

**No cascades.** Traced, the shadow pass draws no cascade (`ShadowPass.RenderTraced`): it keeps the terrain and landmark maps and publishes the receiver blocks with no cascade.
What draws no traced pixel reads the **sun map** instead (`Gi.TracedSunVolume`, after the traced scene's update, before the reflection): rays down the sun's direction from a plane, one per texel of three nested squares of 1024 x 1024 (half sides 256, 1536 and at least 4096 units), each snapped to its texels in world space, R32F, the distance to the first caster (solids, characters, both terrain grids; no grass). The nearest square is traced every frame, the second every other frame, the widest every fourth (`ShadowPass.SunMapMask`); a square not traced keeps the basis, the place and the plane it was traced with in its tile matrix, so what it holds stays where it was taken as the eye and the sun move (**Observed**, one session, paused frame, alternated: 0.15 ms of GPU, the pictures differ as two frames of the same setting do; `MEITOU_SUN_MAP_SPREAD=0` traces all three every frame). It sits in the blocker binding (`uShadowBlocker`, unused without cascades), its matrices in the first cascade tiles (`msVolumeOn`, `msVolumeAt`: four texels compared, bilinear; a surface reads it 2 units towards the sun, `msVolumeSurface`). **Observed** (the user, DLSS): a volume of one ray per view froxel, 64 slices exponential in depth, stamped one copy of a caster per slice in the shafts and swam with the camera. Readers: the light shafts (`shaftShadow`), the lit dust (`particleSun`), and through
`kenshiShadowMeitou` the water's foam, the skin's light entry, impostors and the reflection (outside the view's volume: lit, but for the terrain and landmark maps).

**Rays** go to the sun's centre, the same every frame (no noise for the upscaler to average, no history to trail). There is no receiver identity as in KenshiNative's
G-buffer, so what it skips by name is skipped along the ray. Solids leave the plane the depth shows by half a pixel's footprint, slope-scaled; cards leave from the sun's face
and skip 3 footprints plus 0.5. Queries, each after the last missed:
- objects (as far as the shadows' range, at least the probes' 6000), foliage, small foliage (`0x10`: radius 6 and up; see "Casters by size on screen"), characters (`0x40`), alpha-tested in
  candidates; a part an object's material cuts reports candidates through `ForceNoOpaque`;
- the fine terrain grid along the whole ray from the grid's own height under the point (a ray down within the relief's 12-unit amplitude; a point farther from the grid, a
  roof, leaves from where it is), lifted 3 units (+0.2 % of the distance): the grid holds no relief and not the curved outline, so a crest the relief raised must not see
  over a ridge its trough is shadowed by (**Observed** in play: lit ripple crests inside a dune's shadow), and the 24-unit blind range it had let the ground at a cliff's
  foot see the sun through the cliff; the coarse grid (`0x08`) from 320 along the ray. Back faces are skipped as the probes skip them, but not from interiors (bit 9);
- grass (`0x20`), only as far as the ray can stay under the tallest blade, fading out by amount (not noise) over the last quarter of its reach.

**Casters by size on screen** (`GiScene.CasterPixels`, 3): small foliage and characters cast while their bounds are at least 3 pixels across at the frame's lens (`PixelsPerRadian`), up to the shadow distance; small foliage takes the largest on screen first within its 5000 instances. Fixed ranges (3000 for small foliage, 4000 for characters) left a tree or a character plainly in view without a shadow, further off or through a long lens (**Verified**, t_flats_shore, the same tree and character from 2500, 5000 and 9000 units at a matching lens: shadows at 2500 only before, at all three after). Through a lens longer than the game's the object range grows by the lens's factor, to 24000 at most. Cost at Gut (13184 foliage instances against 4326 with no small foliage): 0.02 ms of GPU (**Verified**, paused pair).

**Characters** (receiver class 3): a low-poly body's facet can face away from the sun while its smooth shading (and skin's band past the terminator) is still lit; a
ray from it stopped inside the body, a dark triangle (**Observed** on a face: eye corners, nostrils, the temple). There the ray starts under the facet, the nearest back of a
face within 0.8 units (`BodyChord`) is where it leaves the body, and blockers count from that point on. Its sun-facing facets skip blockers within 0.03 units (`BodyContact`: a crease between two facets drew hair-thin dark lines and notches). A shadow edge on a character is at least 0.025 units wide each way
(`BodyPenumbra`): the body's own shadows are polygons, and skin spreads light that far.

**Penumbra** (`PenumbraFilter`, shared with the lamps, no history): the rays write, where blocked, the penumbra's radius at the receiver (the sun's tangent 0.00465, the real
0.267 degrees, times the blocker's distance; KenshiNative's 0.015 widened far penumbrae threefold). A pixel the light does not reach (turned away from it: its term is 0, or no traced
pixel) is no receiver: the filter keeps it and no neighbour reads it. Read as lit (no ray is asked there), a ripple's far slopes lit their sunward neighbours inside a
shadow and a caster's dark side lit the ground at its foot (**Observed** in play: lit crests in a building's shadow on displaced dunes, a lit band along the building's
foot, a lit rim round a character's boots). A row pass takes the mean radius of 16 fixed sunflower taps within 12 pixels on the pixel's own surface (within the plane
the depth shows, not the shading normal a relief or a trail tilts), in pixels, at least 1 and at most 16, and averages along the row with a box whose end taps weigh their fraction; a
column pass does the same at that radius. The weights are continuous, so a wide penumbra is a smooth ramp: the former 16-tap disc with taps rounded to pixels left bands
along wide penumbrae (**Observed** in play). The one-pixel least radius anti-aliases contact edges (the rays alone stair-stepped them under DLSS, **Observed** in play); the
rays leave from the jittered pixel's own depth, so the upscaler sees the edge move with its jitter. A still view gives the same mask every frame; under motion nothing
trails. Contact hardening follows from the blocker distance.

**Characters** are in the rays (`TracedCharacters`, [render-lights](render-lights.md#traced-lamp-shadows)): the nearest 128 whose shadow can be in view, within 4000 and past it by their size on screen ("Casters by size on screen"), at the level the
colour pass draws, skinned with the frame's palettes, so a surface's ray leaves the same triangles it drew and a body shadows itself only where it does (armour layers, hair over
a face). No character layers are drawn.

**Grass.** `TracedGrass`: patches of pages within the reach across the ground, nearest first, at most 4 builds and 600k triangles a frame and 512 MB, 1024 instances, unused
for 600 frames dropped. The reach is the traced range's view depth taken to the frame's corners (`ShadowPass.TracedDepth`), within 1200 to 2400; the rays test the same reach
by distance from the eye. **Observed** (zone 14,30, 16:00, before this rule): a fixed 1200 by distance from the eye, faded from 900, left everything past 900 without grass
shadows: at boom 1400 the whole view; at boom 900 the far half. Now 152 patches, 90 MB at boom 900. A kernel expands the arena's blades at rest into a vertex buffer (65536
blades a patch at most), one rigid instance each. Blades whose foot or top is in a drawn interior mask's box collapse to a point (KenshiNative's fix, by box: over-cuts a
non-convex mask's concavity). A changed mask set or density rebuilds the page. A blade's ray ignores blades within 8 units (the 4-unit blind range and half its fade): the
sway and overlapping twin cards. Patches within 800 units of the eye across the ground (`SwayReach`, at most 1M triangles a frame, nearest first) sway with the drawn blades (same phase and length as the blade program, top edge): the kernel writes their vertices again and their structures are refitted, the top level refitted over them. Past that the cards hold still.

**Scene fill.** Objects within the probes' 6000 go in first, then the foliage, then the objects out to the shadows' range while room is left past the reserve for grass and
characters (a longer range filled the instances and cut the trees). A foliage group's spheres and bounds are filled when the traced scene asks for them (the culls filled them
only for groups they drew): **Observed** (Obedience, 08:00, offscreen) the block held no foliage until its 120-frame rebuild, so the trees cast neither shadows nor beams in the
fog. The fine terrain grid's ray origin is taken down to the grid under displaced ground, never up onto it (a wall's foot against a cliff the grid passes above).

**Not traced** (they cast no sun shadow while traced): construction sites (drawn clipped by their progress; the whole mesh would cast the finished building), debris
(bolts, dropped items) and birds (no caster in any mode), particles, objects past the range or drawn only as impostors, foliage past the shadows' range (nearest first within the instance budget; small foliage and characters under 3 pixels across), grass
past the reach, characters past the 128 nearest. Landmarks keep their map.

**Limits.** Blood on surfaces (`RecordSurfaces`) blends before the sun is added. The objects' traced transforms and level are the GI's: rotors and doors are frozen as the
GI sees them; an object's texture tile is not read in the alpha test. The penumbra filter works on what the screen shows: a blocker's shadow whose taps fall on another
surface keeps its hard edge there. **Unknown**: the saw-toothed terrain shadows reported on displaced dunes were not reproduced offscreen (dunes round the siwa-test6 save at
06:00-07:00 and 18:24); the fine grid was 16 apart over the heightmap's 18 (bilinear facets), now 8 apart through a Catmull-Rom surface. **Observed** costs: see Measured.

**Observed** (RTX 4090, 1920 x 1080 with DLSS, the median GPU frame time of 36, a still camera, shafts on). Cascades / traced / Off: the Hub at 17:12
2.47 / 2.08 / 1.71 ms, the siwa-test6 close-up 2.34 / 2.37 / 1.94, dunes at 06:24 2.04 / 1.62 / 1.34. Traced passes (an earlier run): rays and penumbra 0.07-0.10 ms,
the volume 0.09-0.11 ms, a close-up character 0.06 ms. With a still camera the frame-to-frame change is DLSS's own (0.09-0.16/255 mean), traced or cascades alike.

## Soft grazing sun

`grazesun` (Meitou, optional, off; `GrazingSun`, `atmoSunNormal` in `AtmosphereShaders`, `uAtmoParams.w`). The game lights a normal map's bumps with N·L times the cast shadow and nothing else
(**Verified**, `deferred.hlsl`, `lightingFunctions.hlsl` `CalcPunctualLight`), so a surface the sun grazes breaks into lit and black patches, and bumps stay lit on a surface facing away.
With the switch the sun's diffuse and specular terms take `mix(surface normal, mapped normal, smoothstep(0.1, 0.4, N·L of the surface))`: the map fades out of the sun's light below
24 degrees and is gone at 6; the sky's light, the lamps and the probes keep the mapped normal. Terrain (its height-field normal) and objects (the vertex normal) set the surface normal
(`atmoSurfaceNormal`); characters, foliage cards and impostors do not, so they are unchanged. **Observed** (save of a cracked plain, the sun 19.6 degrees up and 11.6 over the ground, whose
horizon towards the sun is 8.6 degrees: no cast shadow): the bright and black plates become evenly lit ground with the cast shadows left; at 13 h the same view changes by 4.0/255 mean
(the sun never passes 36 degrees at latitude 54). Not a model of the bumps' shadows: no bump shadows another, the map only weighs less.

## Validation and limits

Check low sun, cutout trees, interiors, cascade boundaries, camera/clock cuts, reflection and
far terrain. Compare redraw spikes/p99 as well as mean; cached passes can hide costs in averages.
**Observed** moving swamp views: far-cascade redraws cause spikes; scheduling/coarser casters can
reduce tails without a significant mean gain. Fog-cap and extra-cascade rules must preserve
clear views and Faithful behaviour. Source formulas/unknowns remain in the format reference.
