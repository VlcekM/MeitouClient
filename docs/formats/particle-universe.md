# ParticleUniverse scripts and the weather particle effects

The particle effects of the game: the `.pu` scripts of the ParticleUniverse plugin (`Plugin_ParticleUniverse_x64.dll`), the Ogre
materials they draw with, and how the weather's camera effects (rain, ash) place and show them. Written in our own words from the
shipped files and from the plugin's behaviour as seen in them; no plugin source was read and the plugin is not linked. Attribute names
are format identifiers. Labels as in [../README.md](../README.md): **Verified** = checked in the shipped files (or in the shader
sources), **Verified (decompiled)** = read in the named function of `kenshi_x64.exe` (decompiled outside the repository, nothing here
is translated code), **Observed** = a reading that fits the data, **Unknown**. The weather side (which effects a weather has, the effect
groups by `type`) is in [weather.md](weather.md#effects-particles).

Implemented in `Meitou.Data.Particles` (reader, simulation, effect groups) and `Meitou.Rendering.ParticleRenderer` (the pass); tests
in `tests/Meitou.Tests/World/ParticleTests.cs`; `meitou-tools particles` prints the survey below, `meitou-tools particles effects`
lists the EFFECT records with their systems.

## Files (Verified)

- `data/particles/scripts/*.pu`: **93 scripts, 93 systems** (one `system` each; the file name is not the system name: `Ash-Flakes_Light`
  is in `Ashland_MultiFlakes.pu`, `Volk-Cloud` in `Volc-Pumper.pu`). Three system names occur twice (`HackBlood-system`,
  `Local-swirl-devil`, `Kenshi_Mist_Cloud`); an EFFECT's `particle system` finds the first. One more file, `Ash-Flakes_CameraCover`
  (no extension), holds a system of that name that no EFFECT names (the EFFECT called `Kenshi_Ash-Flakes_CameraCover` uses
  `Ash-Flakes_Light`); the game's `*.pu` pattern does not load it, so it is not read.
- `data/particles/materials/*.material`: **93 Ogre materials**, `data/particles/textures/` (PNG, a few DDS), `data/particles/models/` (four
  meshes for the mesh renderer, which one script uses). A material named by a technique but not found: `SandBlown_Streaks` (the
  effect `Desert-SandBlown_Streaks`); textures not found: `Haboob_Finger.png`, `TwisterDust_Large.png`, `SteamRise01.png`, `EX-DustMite`.
- 200 `technique` blocks (199 Billboard renderers, 1 Sphere), 228 emitters and 639 affectors; the survey below counts them.

## Grammar (Verified)

Line based, like Ogre scripts but without their variables, imports or quotes (none occur): `//` starts a comment; a line followed by a
line that is only `{` opens a block, any other line is a property (`name value...`); `}` closes.

```
system <name>                      // top level; <name> is one word
{
    <property> <value>...
    technique <name?>              // the name may be missing
    {
        renderer Billboard { ... } // type, then a block
        emitter Box <name?> { ... }
        affector Colour <name?> { ... }
        observer OnPosition { handler DoExpire { } ... position_y less_than -5.85 }
    }
}
```

A property whose value is `dyn_random`, `dyn_curved_linear`, `dyn_curved_spline` or `dyn_oscillate` is followed by a block with the
attribute's parameters:

| Dynamic attribute | Block | Uses |
|---|---|---|
| `dyn_random` | `min`, `max` | 745 |
| `dyn_curved_linear` | `control_point x y` (repeated) | 146 |
| `dyn_curved_spline` | `control_point x y` (repeated) | 73 |
| `dyn_oscillate` | `oscillate_type` (`sine`, `square`), `oscillate_frequency`, `oscillate_phase`, `oscillate_base`, `oscillate_amplitude` | 29 |

Reading (**Observed**, from the scripts' numbers): `dyn_random` is drawn uniformly when the value is used (per particle for emitted
properties); a curve is read at the particle's **life fraction** (0..1) in an affector and at the emitter's age in seconds in an emitter
attribute (the `emission_rate` curves); a spline is a smooth curve through its control points (Catmull-Rom is used); an oscillation is
`base + amplitude · wave(frequency · t + phase)` with `t` in turns (sine of 2π(frequency·t + phase); square: ±1 by the half period). The
exact plugin formulas are **Unknown**.

## Survey of the 93 scripts (Verified: `meitou-tools particles`)

| Part | Types and counts |
|---|---|
| Renderer | Billboard 199, Sphere 1 |
| Billboard type | `point` 179 (the default), `oriented_self` 10, `oriented_shape` 4, `oriented_common` 3, `perpendicular_common` 3 |
| Billboard origin | `center` 160 (default), `bottom_center` 31, `center_left` 6, `top_center` 2 |
| Rotation type | `texcoord` 153 (default), `vertex` 46; `sorting true` on 13 |
| Emitter | Circle 60, Point 59, Box 53, SphereSurface 49, Line 6, Slave 1 |
| Affector | Colour 194, Scale 149, TextureRotator 109, Vortex 53, LinearForce 45, Randomiser 36, Gravity 14, SineForce 9, FlockCentering 8, ForceField 7, Jet 6, Align 5, GeometryRotator 3, ScaleVelocity 1 |
| Observer | OnPosition 5, OnTime 3, OnEmission 1, OnClear 1 |
| Event handler | DoPlacementParticle 10, DoExpire 5, DoStopSystem 1 |
| `emits` (an emitter that makes other things) | `technique_particle` 1, `emitter_particle` 1 |

Only the system attributes `category` (59), `scale` (38), `scale_velocity` (18), `fast_forward` (5), `iteration_interval` (2),
`keep_local`, `lod_distances`, `nonvisible_update_timeout`, `smooth_lod` occur.

### Attributes in use

- **System**: `scale x y z` (the shipped values range from 0.02 to 100), `scale_velocity`, `fast_forward time interval` (always `1 1`),
  `category` (`kenshi_particles`, `kenshi_p2`, `General`), `iteration_interval 0.1`.
- **Technique**: `visual_particle_quota` (119 of 200; when missing, the plugin's default **500** is assumed: **Observed** for
  `Ash-Flakes_Light`'s three flake techniques, which then hold 3 × 500), `emitted_emitter_quota`, `emitted_technique_quota`,
  `emitted_affector_quota`, `emitted_system_quota`, `material`, `default_particle_width` / `height` / `depth` (missing: 100),
  `position`, `keep_local`, `enabled`, `lod_index`, `spatial_hashing_cell_dimension`, `behaviour` (`Slave`).
- **Renderer** (Billboard): `billboard_type`, `billboard_origin`, `billboard_rotation_type`, `common_direction`, `common_up_vector`,
  `sorting`, `accurate_facing`.
- **Emitters** (all): `emission_rate`, `time_to_live`, `velocity`, `angle`, `direction`, `position`, `mass`, `duration`, `repeat_delay`,
  `colour`, `start_colour_range`, `end_colour_range`, `all_particle_dimensions` or `particle_width` / `height` / `depth`, `orientation`
  (and `start_` / `end_orientation_range`), `force_emission`, `keep_local`, `emits`. Box: `box_width` / `box_height` / `box_depth` (x, y,
  z extents). Circle: `radius`, `step`, `emit_random`, `auto_direction`. SphereSurface: `radius`, `auto_direction`. Line: `end`,
  `min_increment`, `max_increment`, `max_deviation`. Slave: `master_technique_name`, `master_emitter_name`.
- **Affectors** (all): `enabled`, `position`, `exclude_emitter`, `mass_affector`. Colour: `time_colour t r g b a` (a list, t = life
  fraction), `colour_operation multiply` (84 of 194; the others replace). Scale: `x_scale`, `y_scale`, `z_scale`, `xyz_scale`.
  TextureRotator: `rotation`, `rotation_speed`, `use_own_rotation`. LinearForce: `force_vector`, `force_application average`.
  Vortex: `rotation_axis`, `rotation_speed`. Randomiser: `max_deviation_x/y/z`, `time_step`. Gravity: `gravity`. Jet: `acceleration`.
  SineForce: `force_vector`, `min_frequency`, `max_frequency`. ForceField: `force`, `forcefield_size`, `octaves`, `frequency`,
  `persistence`, `amplitude`, `delta`, `movement`, `movement_frequency`, `worldsize`, `ignore_negative_y`. Align: `resize`.
  FlockCentering: `position`. GeometryRotator: `rotation_axis`, `rotation_speed`, `use_own_rotation`. ScaleVelocity: `velocity_scale`,
  `since_start_system`.
- **Observers**: OnPosition: `position_x|y|z less_than|greater_than value` (all five use `position_y less_than -5.85`), `handler`;
  OnTime: `on_time greater_than seconds`, `since_start_system`, `observe_interval`; OnEmission: `observe_particle_type`,
  `observe_until_event`; OnClear. Handlers: DoExpire (ends the particle), DoPlacementParticle (10: `force_emitter`; makes an emitted
  particle at the place; the rain scripts have two of these and no emitter that emits, so they do nothing visible), DoStopSystem.

## Materials (Verified: `data/particles/materials`, `materials/forward/basic.program`, `basic.hlsl`)

- One technique, one pass per material. `depth_write off` on 92 (`Kenshi_Rain_Splash_basepart` has it on), `lighting off` on 79,
  `tex_address_mode clamp` on 90 texture units (the rest take the default). `scene_blend`: `alpha_blend` 50, `add` 41, `colour_blend` 2. `depth_check` is
  written `off` on 4, on or default (on) for the rest. A second texture unit `content_type compositor rt_interiormask` (50 materials)
  feeds the interior clip.
- Programs: vertex `Basic_Coloured_Ambient_VP` (54 materials) or `Basic_Coloured_Texture_VP` (38), fragment `Basic_Texture_Clipped_FP_HLSL`
  (38) or `Basic_Texture_FP` (the others). What `basic.hlsl` does, **Verified**: the output colour is `texture · colour · vertexColour`,
  where `colour` is the fragment program's `colour` parameter (1 1 1 1; 5 5 5 1 on three materials, 1.5 on two) and the vertex colour is
  the particle's colour; the `AMBIENT` vertex programs scale the vertex colour's rgb by `clamp(5 · sunDirection.y + 0.2, 0.1, 1)` (the
  shared sky parameter, so particles dim to a tenth at night); there is **no fog term** in any particle shader (particles are not hazed);
  the `CLIP_INTERIOR` fragment programs discard fragments behind the interior mask (`rt_interiormask`): not reproduced (the viewer has no
  interiors mask).
- Blending is Ogre's: `add` = one + one, `alpha_blend` = source alpha / one minus source alpha, `colour_blend` = source colour / one
  minus source colour. The scene's alpha channel is not written by the viewer's particle pass (it carries the characters' mask).

## Simulation as implemented (`ParticleSimulation`)

CPU, deterministic (a seeded `System.Random` for emissions; each particle's own seed hashed for the affectors, so passes can run in
parallel), struct-of-arrays per technique, pool sized by the quota. Time advances only through `Advance(dt)` (steps of at most 1/20 s).
What follows is what the scripts require and what we chose; unless a line says **Verified** it is **Observed** or **Unknown**.

- **Emission**: an emitter accumulates `emission_rate · dt` (times a group multiplier: the wind-span rule below) and spawns whole
  particles while the technique's pool has room. `duration` / `repeat_delay`: the emitter works for `duration` seconds, then rests for
  `repeat_delay` and starts again; without a delay it stops for good. Box: uniform in `box_width × box_height × box_depth` (x, y, z)
  round `position`, times the system scale. Circle: on the **perimeter** of a circle of `radius` in the xz plane (**Observed**: the
  scripts' rings of puffs; the plugin's disc or ring is **Unknown**). SphereSurface: on the sphere. Point: at `position`. Line: along
  `position`..`end`. Slave and the other `emits` kinds: not simulated.
- **Direction**: `direction` (default up) within a cone of `angle` degrees half-angle (uniform over the cap); `auto_direction`: outward
  from the emitter's centre. Speed `velocity · scale_velocity`; `time_to_live` seconds of life.
- **Size**: `particle_width`/`height`/`depth` or `all_particle_dimensions` if the emitter gives them, else the technique's defaults (100
  when missing), times the system scale. A particle's colour is the emitter's `colour`, or random between `start_colour_range` and
  `end_colour_range` (**Observed**: `kenshi_weather_ash1` gives the smoke a 0.11..0.47 alpha range).
- **Colour affector**: a piecewise linear ramp over the life fraction. `colour_operation multiply`: emitted colour × ramp; otherwise
  the ramp **replaces** the colour (**Observed**: `Poison_Gas_White` emits colour `0 0 0 0` and relies on a white ramp without an
  operation).
- **Scale affector**: its value (read at the life fraction) is a **rate, units per second added** to the size, per axis, plus `xyz_scale`
  for all three (**Observed**: `Poison_Gas_White` puffs of 80..150 units with `xyz_scale` rising from 0.56 to 61: as a factor they would
  grow to thousands of units, as a rate by about 200 over their 3..5 seconds).
- **TextureRotator**: `rotation` degrees at the start and `rotation_speed` degrees per second, both drawn once per particle (**Observed**
  units); drawn as a rotation of the quad (the `texcoord` and `vertex` rotation types look alike here).
- **LinearForce**: adds `force_vector · dt` to the velocity (`average`: the velocity moves half way to the vector). **Vortex**: turns the
  particle's position and velocity about `rotation_axis` through the affector's position at `rotation_speed` radians per second (the unit
  is **Unknown**). **Randomiser**: every `time_step` seconds (every step when 0) the position is nudged by a random amount up to
  `max_deviation` per axis, scaled so a step shorter than 1/60 s gives the same drift (the plugin's rule is **Unknown**; for the ash it
  makes a falling flake flutter). **Jet**, **Gravity** (an attraction to the affector's position), **SineForce**: simple readings, **Unknown**.
  ForceField, Align, FlockCentering, GeometryRotator, ScaleVelocity: not simulated (a few scripts each).
- **OnPosition + DoExpire**: ends particles whose own position (world space unless the technique keeps particles local) passes the
  threshold; OnTime + DoExpire by age. Verified in the scripts: all five OnPosition observers are `position_y less_than -5.85` with two
  DoPlacementParticle handlers and a DoExpire. As the plugin compares the particle's world position, in the game rain is not ended by
  this plane (the world's ground is far above y = −5.85) but by its life; `Kenshi_Heavy_Rain` then keeps about `6400 · 0.65` = 4000
  particles alive, which fits its quota of 5000 (**Observed**).
- **`fast_forward 1 1`** (rain scripts): the system is stepped for 1 second in steps of 1 when it starts. The viewer's own start-up (below)
  replaces it.
- **System `scale`** multiplies the emitters' extents, radii and positions and the particle sizes (**Observed**; the EFFECT loader's
  size measure multiplies the Box extents by it too, **Verified (decompiled)**); `scale_velocity` multiplies the emitted speeds.
- **keep_local**: positions stored relative to the system.

## Billboards (the pass)

`ParticleRenderer`: one instanced quad strip per technique (the instance record is 64 bytes: position, rotation, direction, size,
colour), written each frame into the frame constants (`Transient`, a ring per frame slot) and drawn into the scene's HDR target after
the water, in the near depth slice, depth tested and **not** written, colour channels only (the alpha channel is the characters' mask).
Nothing is recorded when no group has a particle.

- `point`: faces the camera, rotated by the particle's rotation. `oriented_common`: its up axis is `common_direction`, turning about it to
  face the eye. `oriented_self`: its up axis is the particle's direction of travel (a particle that does not move falls back to the
  camera's up). `perpendicular_common`: a flat quad facing `common_direction` with `common_up_vector` up. `perpendicular_self`: facing the
  direction of travel. `oriented_shape` (4 scripts): drawn as `oriented_self` (**Unknown**).
- Origin: the quad's anchor point is the particle's position (`bottom_center`: the quad extends from the position along its up axis, which
  for a falling rain streak is along its travel). Width scales x, height y; depth is not used by billboards.
- Material: the texture (mipmapped, clamped), blend and depth flags as above; the colour is multiplied by the material's `colour` and, for the
  ambient programs, by `clamp(5 sunY + 0.2, 0.1, 1)` (sunY = the sun direction's y). No fog, no lighting, no sorting yet (additive blends
  need none; the 13 `sorting true` techniques are drawn in pool order).
- **Near plane**: the viewer's near plane grows with the camera's height (up to 200) where the game's is a few units; a particle nearer than
  the slice's near plane is drawn with a projection with a 0.5 near plane and no depth test (nothing of the scene is nearer than that
  plane). Camera rain is then visible at every zoom, as in the game.
- Not done: the interior mask clip, the `sorting` order, the mesh (Sphere) renderer, `oriented_shape`, soft particles.

### Low-resolution particles (Meitou `particles` switch)

**Why the dust storm cost 5.6 ms (Observed, 2026-10-08, RTX 4070, Release, `--world --town Heft --radius 1|2 --distance 3000 --pitch 10 --yaw 300
--upscaler dlss --weather "Dust Storm Approach" --time 12`, 1280 x 960):** 16396 particles, all of one material (`Sand-Wisp`, alpha blend, depth
checked), sprites about 600 units wide, mean alpha 0.09, a quarter of them nearer than the slice's near plane (no depth test). The shader is one
texture read times a colour, so the cost is blending fill, not shader work, vertices or sorting: a render scale of 0.5 (a quarter of the pixels) took the
pass from 5.98 to 1.52 ms, and with the scissor cut to one pixel (all vertex work kept) the draws cost 0.18 ms. The sprites overlap on the order of a hundred
layers per pixel.

**What Meitou does** (`PostOptions.LowResParticles`, default on; `--faithful particles` / `--no-particles-low` draw everything at full size, byte-identical to before):

- `ParticleRenderer.Prepare` collects as before and tags each draw (one technique of one unit) with a size: only `alpha_blend` and `add` materials qualify;
  the mean screen size of the draw's sprites (square root of the mean area in render pixels) picks the size, from 80 px up a quarter-size target (1/4 per
  axis), from 20 px up a half-size one (1/2), below that full size (rain streaks and flecks stay sharp and cost almost nothing). A size is used only when its draws
  add up to at least 6 screens of sprite area (`MinLayers`), since its two extra passes cost about as much as 10 million pixels of blending; otherwise they go back to full size.
  `colour_blend`, `modulate` and opaque materials always draw at full size (see the caveat below). `--particle-divisor 1|2|4` forces one size for measuring.
- A low-resolution size needs `PostProcess.BeginParticlesLow`: the scene's depth reduced to the target's size by a depth-only pass (`PostProcessShaders.ParticleDepth`,
  the farthest depth of each block, written as the target's depth so the hardware depth test and early-Z still reject hidden sprites). The draws then go into an RGBA16F
  accumulation target cleared to 0 with the blend (one, one minus source alpha): an alpha particle writes (rgb x a, a), an additive one (rgb, 0), so rgb is the colour added and
  alpha the opacity (1 minus the transmittance). Every blend of the two kinds is an affine map of what is behind (`dst * (1 - a) + c`), so they compose **exactly** in draw order.
  The nearer-than-the-near-plane draws keep their own projection and no depth test.
- `CompositeParticlesLow` blends the target over the scene (`scene * (1 - a) + rgb`, red, green and blue only: the alpha is the characters' SSAO mask) with an
  upsample (`PostProcessShaders.ParticleComposite`) that weights the four nearest texels by their bilinear weight over (0.01 + the relative difference of their linear
  depth to this pixel's), so a sprite behind a near edge does not bleed over it. Pixels whose four texels are empty skip the depth reads.
- The DLSS / FSR / TAA reactive mask: the full-size draws are redrawn into it as before; the low-resolution ones add 30 x max(opacity, brightest channel) from their accumulation
  target (`ParticleCoverage`, bilinear) instead of being drawn again at full size.

**Measured** (same view, GPU ms per frame from the post chain's timestamps, several runs each because the card is shared; one run in ten is disturbed by another process):

| | Faithful (full size) | Meitou (auto: quarter size here) |
| --- | --- | --- |
| particle draws and composites | 5.6 to 6.2 ms | 0.03 depth + 0.96 to 1.03 draws + 0.03 to 0.08 composite = about 1.1 ms |
| reactive-mask coverage (the DLSS run, "particle coverage" stamp; it was inside the old `upscale` number) | **62.7 ms** | 0.04 ms |
| forced half size (`--particle-divisor 2`) | | 2.6 ms |
| forced full size through the same path (`--particle-divisor 1`) | | 8.1 ms (premultiplied RGBA writes cost more than the colour-only blend) |

Against the full-size picture of the same frame (`meitou-tools image-diff`): mean difference 0.17 of 255, largest 8, no pixel over 12 (quarter size, the dust view);
0.08 mean, largest 2 for a view inside the dust at 500 units; Heavy_Rain (4041 small sprites) draws at full size in both modes (0.05 ms; mean 0.001, largest 2: run to run noise of the weather).
Faithful (`--faithful particles`) against the picture before the change: 0 differing pixels. **Known differences**: the upsample can leave a faint soft edge of dust where a sprite
meets a thin foreground silhouette; draws of other blends drawn at full size come before the low-resolution composite, so a `colour_blend` or `modulate` draw that overlaps a low-resolution
one in screen space is ordered differently from the game's far-to-near order (none of the 93 base-game weather systems mixes them with big sprites; **Unknown** for mods); the
reactive mask for the low-resolution draws is derived from the accumulated opacity, not summed per particle (**Observed** equivalent for dust).

## The camera effects (`CameraEffectGroup`)

For an EFFECT of type CAMERA, CAMERA_RAIN or CAMERA_ACID_RAIN ([weather.md](weather.md#spawning-verified-decompiled-fun_1409dcaf0-fun_140103210-behaviour-from-fcsdef-where-marked)):

- **Verified (decompiled)** (the camera group's functions `FUN_140101800`, `FUN_140100dc0`, `FUN_140101a20`, `FUN_140105080`): the system's
  node is set at the **camera node's** position and updated every frame, so it moves with the camera; its particles live in world
  space. A wrapping rule is added to every technique: with `d` the effect's `size`, the centre of the cube is the camera node's position
  plus its orientation applied to `(0, 0, d)` (the camera looks along its node's +Z: `Camera::lookAt(UNIT_Z)` in the rig's creation
  function), i.e. **`d` ahead of the camera along its view direction**, and every particle coordinate is moved by whole multiples of the
  edge `2d / 1.5` until it lies inside the cube round that centre. (`weather.md` called `d` "a camera value": it is the effect's `size`, the
  float at offset 0x7c of the effect data the EFFECT loader computes, and the camera's boom plays no part.)
- **Verified (decompiled)**: the EFFECT loader sets that size to the largest of the Box emitters' width·scale.x, height·scale.y,
  depth·scale.z (and the Circle emitters' radius·scale.x), times 0.5, times 1.5, i.e. **0.75 × the largest extent** (the Box depth
  default being 100). So the cube's edge is that extent itself: 120 for `Kenshi_Heavy_Rain` (d = 90), 150 for `kenshi_rain1` (d = 112.5),
  120 for `Ash-Flakes_Light`, 320 for `Kenshi_black_rain`.
- **Observed**: particles are emitted round the camera (the system's origin) and wrapped into the cube ahead of it, so after a moment
  they fill the cube; the viewer keeps the simulation's coordinates near an anchor (the camera position when the group starts, moved when
  the camera is 1500 away) so single precision does not stutter the flakes.
- **Start-up** (**Observed**, a viewer choice): a new group is stepped for the longest particle life of its system (at most 40 s;
  `--particle-prewarm <s>` replaces it, 0 starts empty) so particles are present at the first picture; the screenshots rely on it. The game
  has only `fast_forward`. The first groups of a view are warmed at once on the render thread; the groups of a later weather change are not
  (next section).
- EFFECT fields used: `colour multiplier` (rgb tint of the particles), `wind affected` (the particles drift with the wind × `wind speed
  mult` units per second), `wind direction emission` (the emitted horizontal direction follows the wind), `min/max wind span rate` (the
  emission rate scale: 0 at the minimum wind speed, 1 at the maximum; with only a minimum, 0 below it). **Not used yet**: `sky colour
  multiplier` (rain 0.6, 0.25, 0.2; ash 0.6 / 0.25; its use is **Unknown**), `particle fade out delay`, `min/max time to live` (the group's
  life: a forced weather keeps its group), the weather's strength and the `count` / respawn times (one group per entry; **Observed**: for a
  camera group the count says how many may exist at once and the respawn times how soon a replaced one starts, which only the scheduler needs).
- The weather's wind: a forced weather has no scheduler, so the adapter uses the weather's `wind speed max` along +x (**Observed**, to be
  replaced by the scheduler's wind). The other group types, the placers, fog volumes and lightning are built since part two
  ([weather.md](weather.md#the-groups-in-detail-verified-decompiled-where-marked-the-rest-observed)).

## Weather changes (`WeatherGroups`, Observed: a viewer choice)

The game removes a region's effect groups and makes the new weather's the moment its weather changes ([weather.md](weather.md#spawning-verified-decompiled-fun_1409dcaf0-fun_140103210-behaviour-from-fcsdef-where-marked), **Verified (decompiled)**),
and every region's groups exist all the time, so nothing starts cold. The viewer shows the camera's region only and a new group has no
particles, so a change costs a **warm-up**: the group's schedule (units placed in 0.5 s steps over the group's longest particle life, at most 40 s)
and then the simulation of the time that queued up, unit by unit. What it costs, with the shipped weathers
(`meitou-tools particles warmup`, Release, 2026-10-10, second pass of each effect so that the compiling is out, on a PC that was running other viewers: good
to a factor of 1.5 at best, read the ratios; "a frame" is one 1/60 s frame of the warmed group):

| Weather → effect (type) | units | particles | warm-up | schedule | simulation, one thread | simulation, parallel | a frame, serial / parallel |
|---|---|---|---|---|---|---|---|
| `Heavy_Rain` → `Kenshi_Heavy_Rain` (CAMERA_RAIN) | 1 | 4125 | 1 s | 0.0 ms | 5.4 ms | 5.4 ms | 0.19 / 0.20 ms |
| `Kenshi_red_rain` → `Kenshi_red_Rain` (CAMERA_RAIN) | 1 | 4125 | 1 s | 0.0 ms | 6.9 ms | 7.7 ms | 0.27 / 0.25 ms |
| `Sand stream ambient` → `Sand-Stream ambient` (GLOBAL) | 5 | 3730 | 10 s | 0.2 ms | 62 ms | 31 ms | 0.47 / 1.03 ms |
| `shek desert storm` → `shek desert` (GLOBAL) | 1 | 1997 | 10 s | 0.1 ms | 44 ms | 47 ms | 0.18 / 0.17 ms |
| `purple desert ambience` → `purple desert` (GLOBAL) | 1 | 2998 | 20 s | 0.1 ms | 151 ms | 144 ms | 0.37 / 0.41 ms |
| `Kenshi_Ash-Flakes` → `Kenshi_Ash-Flakes_Light` (CAMERA) | 1 | 1544 | 20 s | 0.0 ms | 25 ms | 10 ms | 0.03 / 0.03 ms |
| `Kenshi_Ash-Flakes` → `poison gas [White]` (WANDERING_GAS) | 70 | 10477 | 5 s | 0.3 ms | 62 ms | 35 ms | 0.68 / 1.63 ms |
| `Twister Storm` → `Twister-Chuff` (WANDERING) | 1 | 1219 | 4 s | 0.0 ms | 14 ms | 14 ms | 0.13 / 0.12 ms |

The schedule is cheap (at most 0.3 ms; the first group of a kind in a process is 2 to 4 ms slower for the compiling: **Observed** in the flights, before `EffectGroups.PrimeJit`),
the simulation is the cost (5 to 150 ms of one thread), and for the single-unit groups, which are most of them, a parallel loop gains nothing; for the
many-unit ones it halves the simulation and more than doubles the cost of every later frame (the last column), which is why a frame's units do not go to a parallel loop (`EffectSimulator`).

Until 2026-10-10 the viewer did all of it on the render thread in the frame after the change (`ParticleRenderer.SetWeather` rebuilt, the next `Update`
pre-warmed), and rebuilt again at every flip of the camera's region: `particles` frames of 74 ms (flight at 2100 units/s) and 244, 87, 85 ms (9000 units/s),
and the checkerboard of small regions at a coast (Vain and Dreg) flipped the weather several times within a few hundred frames. Now:

- **The two halves of a pre-warm** (`EffectGroup.BeginWarm`, `EffectGroup.Simulate` / `SimulateSerial`; `Prewarm` is both). The schedule places units with the world's
  ground height, which only the render thread may ask (the terrain's height grid swaps as it streams), so a worker is given a frozen copy of the world
  (`EffectWorld.Frozen`: the terrain's immutable `HeightSnapshot`, taken on the render thread by `FreezeGroundHeight`, which any thread may read; the area, radii and
  density are copied) and the groups go back to the live world (`EffectGroup.Rebind`) before they are swapped in. The schedule costs at most 0.3 ms (the table; the first
  group of each kind is slower for the compiling, which `EffectGroups.PrimeJit` does on a worker at start-up, as the viewer runs with tiered compilation off).
  The simulation touches only the units and runs on any thread.
- **A change is made on the side** (`WeatherGroups`, `EffectSet`, `EffectWarmer`): once wanted, the new groups are created on the render thread (0.6 to 0.75 ms in the
  logged build starts: 0.5 to 0.6 of it the world's `Frozen` copy, 0.06 to 0.08 the groups, 0.04 to 0.06 the queuing; **Observed**, `MEITOU_PARTICLE_LOG=1`), queued for the warm-up thread, which runs their schedules and simulates their queued time
  (one unit after the other, at below-normal priority: the frame keeps its cores and the pool its workers), and
  when it is done the next `Update` swaps them for the old groups (with the frame's own simulation finished, so nothing runs on either). The old weather's
  particles are drawn until then; the new ones appear fully formed (the same particle counts as the old on-thread pre-warm: same seeds, same steps, `WeatherGroupsTests`; a
  frame three after a swap shows the new rain over the whole picture, one before it still the old weather's dust). The very first groups
  of a view (and every change with `BackgroundWeather` false) are still made and warmed at once on the calling thread, so a still picture has its particles: that is
  the 40 to 60 ms `particles` frame 1 of a view. **The warm-up thread is made once, at start-up** (`WeatherGroups` constructor): the first version started a thread for every change
  and the render thread paid 3.4, 4.2, 5.6, 9.6, 17.6, 24.6 and 72.8 ms of `weather change` in the frames that started one (**Observed**, three flights of 2026-10-10 with the log
  split into the change, the texture preload and the rest); with a persistent thread that only has to be signalled, three flights had no such frame over 1.4 ms. The
  cause is probably that `Thread.Start` does not return before the new thread has run, and a below-normal thread waits for a core when other programs keep them
  busy (**Unknown**, not isolated: the stalls went when the start did).
- **Hysteresis on the region** (`WeatherEffectInput.Region`, the scheduler's `WeatherState.RegionName`): the game already switches the camera's region only
  once the camera is 500 units outside its current cell ([weather.md](weather.md#region-at-the-camera-verified-decompiled-fun_1409e8f70)), so
  the flips seen in a flight are genuine border cells of alternating colour, not a failure of that rule. The viewer adds a **dwell**: a change to the groups of
  another *region* is only started when the weather has stayed what it is for `--particle-dwell` seconds (default 2), and forgotten if the weather returns to the shown one
  before (or while the new groups are being warmed: that warm-up is cancelled). A change *inside* the camera's region (the scheduler's next weather, the Tab panel's reroll, a
  forced weather, which names no region) waits for nothing. A weather shared by two regions (`clear nothing`, `Default`) is one list object, so crossing between
  such regions changes nothing at all. A camera that stays in a region gets its weather's particles about 2 s plus the warm-up after crossing in, a viewer
  choice against the game's immediate change; the sky, fog, rain level and wetness are not delayed.
- **Textures**: a particle's texture is read and decoded on a worker (`BackgroundWork`) when its group is created, uploaded when first drawn; a technique whose
  texture is still being decoded is left out of the frame (the first frames of a view wait for it). Before, the first draw of a texture decoded its PNG on the render thread: 3.3 to 5.4 ms.

**Numbers** (**Observed**, 2026-10-10, Release, `MEITOU_STREAM_LOG=1 MEITOU_BENCH_SKIP=60 meitou-viewer.exe --world --at -60564,-45142 --distance 1400 --pitch 25 --size 1600x900
--fly-benchmark 3600 --fly-speed 150 --fly-radius 60000 --log-spikes`; slow flight `--fly-speed 35`; the new builds with `MEITOU_PARTICLE_LOG=1`, which prints a line for every frame
whose particle work takes over 2 ms on the render thread and one at every swap). The runs alternate between the baseline (master 0834d0d) and the new code, one viewer at a time, on a PC
that other programs were using, so the frame-time percentiles differ from run to run (p99 21 to 36 ms for both): read the `particles` stage of the worst frames and the counts over 33 ms.
The runs A to D were builds on the way (A, B: the frame's simulation on its own thread, `BeginWarm` still on the render thread; C, D: `BeginWarm` on a worker, a thread
started for each change); E and F are the code described above.

| Fast flight | `particles` stage, worst frames | max frame | frames over 33 ms |
|---|---|---|---|
| baseline A | 218.9, 97.0, 73.5 ms | 228 ms | 8 |
| new A | 4 frames over 2 ms, worst 9.8 | 37.6 ms | 2 |
| baseline B | 207.1, 133.2, 81.1, 55.1, 20.9 ms | 222 ms | 28 |
| new B | 4 frames over 2 ms, worst 8.7 | 55.7 ms | 3 |
| baseline C | 365.8, 145.0, 110.0, 80.5 ms | 379 ms | 26 |
| new C | 3 frames over 2 ms, worst 72.9 (the thread start) | 116 ms | 32 |
| new D | 3 frames over 2 ms, worst 17.8 (the thread start) | 67 ms | 18 |
| baseline E | 296.6, 122.4, 109.6, 86.0 ms | 308 ms | 47 |
| **new E** | 1 frame over 2 ms (2.0) | 35.3 ms | 1 |
| **new F** | none (a 22.0 ms stage in a frame with a 21.3 ms GC pause) | 39.4 ms | 3 |

Slow flight: the baseline's `particles` stage in its worst frames was 74.3 and 17.8 ms (max frame 85 ms, 3 over 33 ms) and 73.8 and 27.0 ms (81 ms, 6); new E had no frame with
particle work over 2 ms (max frame 30.0 ms, none over 33 ms). The baseline code with the same log (the earlier instrumented run, fast flight) had 76 frames with particle work over 2 ms, 26 of
them over 10 ms and 10 over 30 ms (max 287.5 ms); E, F and the slow E together have one such frame, apart from the
first frame of a view (40 to 60 ms: the first groups are made and warmed on the spot). What is left in the worst frames of the new runs is not particles: foliage layout (`upd-foliage` 25 to 55 ms),
GC pauses (up to 21 ms), shadows, reflection and the GPU wait.

## Pools, threads and cost (Observed: measured 2026-10-08, Release, `--fly-benchmark 150`)

- **Lazy pools**: a technique's arrays start at 128 particles and double up to its quota (`ParticleSimulation.InitialPool`), so the 43 placers
  and a dozen systems cost memory only for what is alive. `EmissionStopped` skips emission for a unit whose life is over.
- **Units**: one `EffectUnit` per handler, each with its own simulation; `Advance()` runs a unit's queued time in steps of 1/30 s (1/10 s while a
  backlog of more than 3 s is caught up, at most 40 steps a frame; a unit with more than 1 s left is not drawn). Units farther than 4000 units
  step at 20 Hz, beyond 9000 at 10 Hz (the time queues up). **Background**: after the light phase (schedule, place, move, queue time) the main
  thread hands the units to `EffectSimulator` and goes on recording; `Draw` waits (`Sync`). The wait is what the render
  thread pays: 0.15 ms for Heavy_Rain (one unit), about 0.9 ms for the Great Desert streamers (19 units, 2 ms of simulation on the pool;
  2026-10-08, when it ran on the pool).
- **Threads** (**Observed**, 2026-10-10): the frame's simulation used to be `Task.Run` plus `Parallel.ForEach` on the shared thread pool. A task queued
  behind a burst of the other systems' `Parallel.For` (shore bake, texture and impostor encodes, ...) started 10 to 70 ms late: in an instrumented fast flight
  (`MEITOU_PARTICLE_LOG=1`, the baseline code with the same log: 76 frames with particle work over 2 ms, 65 of them the render thread waiting in `Sync`, up to 61.9 ms
  for a simulation of one unit that ran in 0.2 to 1 ms but had been queued 10 to 60 ms before it started). Now `EffectSimulator` has a thread of its own (above normal priority) and the
  units of a frame are claimed one at a time by it and by whoever waits for the job, so a worker the CPU has not given a core yet costs nothing: the waiter runs the
  units itself. A frame's units cost only 0.5 to 1.2 ms in all (the "a frame" columns above), so a parallel loop gained nothing and lost frames whenever one of its helpers was not
  scheduled; and the affector passes' own `Parallel.For` (`ParticleSimulation.ForRange`) now starts at 16384 particles instead of 4096 (Heavy_Rain with 4125 particles: 0.47 ms serial,
  0.51 ms parallel).
- **Instances are built in ordinary memory** (`ParticleSimulation.Collect` into a reused array, depth partition there) and copied once into
  the frame's write-combined constants: filling them field by field and partitioning in place cost 5.5 ms for 18000 particles, 0.3 ms now.
- **Measured main-thread cost** per frame (update + draw, wait included): Heavy_Rain 0.5 ms, great desert streamers about 2 ms (1.2 ms
  update: the groups' light phase and placers); GPU mean with versus without particles (`--no-particles`) 4.4 versus 4.5 ms (rain) and 5.6
  versus 4.9 ms (streamers). The benchmark and `--orbit-step` runs advance the frame clock when weather particles exist (1/60 s a frame), so
  the particles move.
- **TAA**: particles have no motion vectors; under the history blend a thin fast streak keeps about 10 % of its strength once the camera
  moves. The pass is drawn again into the upscalers' reactive mask (`ParticleRenderer.DrawCoverage`, [renderer-native.md](../renderer-native.md) 8.20).

## What a weather gives (Verified: `meitou-tools particles effects`, merged base records)

| Weather | Camera effects → system (extent) |
|---|---|
| `Heavy_Rain`, `Kenshi_Wet_Forest`, `misty rain`, ... | `Kenshi_Heavy_Rain` (CAMERA_RAIN) → `Kenshi_Heavy_Rain` (120; colour white; sky multiplier 0.6) |
| `Kenshi_red_rain` | `Kenshi_red_Rain` → `Kenshi_Heavy_Rain_red` (120; colour `F56568`; 0.25) |
| `Kenshi_black_rain AND ground steam vents` | `Kenshi_black_rain` → `Kenshi_black_rain` (320; colour `9A6B65`; 0.2) |
| `light rain`, `swamp rain no wind` | `rain_light` → `kenshi_rain1` (150; sky multiplier 0) |
| `Kenshi_Ash-Flakes` | `Kenshi_Ash-Flakes_Light` (CAMERA) → `Ash-Flakes_Light` (120; wind affected; 0.25); `poison gas [White]` (WANDERING_GAS, 70) → `Poison_Gas_White` (extent 580, wind affected): not yet |
| `Kenshi_Ash-Flakes_BLACK_and_Rising Steam` | `Kenshi_Ash-Flakes_BLACK` (CAMERA) → `Ash_Flakez_Black` (100); `Kenshi_Rising_Steam` (POINT) |

`Ash-Flakes_Light` has four techniques: `Technique4` (the 120 × 25 × 120 box, 50 flakes of 0.4 units, life 20 s, falling 1..11 units/s,
`Ash-Flake_Light` texture) and three of 0.7 / 0.6 / 0.5 units (`Ash_Flakez_01..03`) that fall from a default 100³ box and are capped at the
default 500 each: 1550 flakes in all, each drifting by a Randomiser. `Kenshi_Heavy_Rain`: 5000 quota, oriented-self streaks of 0.85 × 4
from the bottom centre, a 120 × 120 (× 100 deep) box 15 above the node, 6400/s, direction (0.2, −1, 0), speed 90..182, life 0.3..1 s,
colour 0.32 grey added; `kenshi_rain1`: 1000 quota, a 150³ box, 1000/s, speed 120, streaks 0.15 wide and 4..16 long from the top centre,
alpha ramp 0 → 1 → 1 → 0 on the life.

## Unknown

- The plugin's defaults where a script is silent (taken: quota 500, size 100, box 100, velocity 100, rate 10, life 3); the exact curves
  (spline basis, oscillation phase unit), Circle (ring or disc), Scale as a rate, the Randomiser, Vortex, Gravity, SineForce rules, the unit
  of the rotation values; `oriented_shape`; mass.
- How `sky colour multiplier` tints the particles; the group's fade-in and -out times; how often a camera group is renewed.
- Whether the system node is nudged by the small term in `FUN_140101800` (a distance squared along the pivot-to-camera direction; not
  modelled).
