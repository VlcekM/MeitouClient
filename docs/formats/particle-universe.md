# ParticleUniverse

Original93 script/material survey, plugin dynamic-attribute/particle constructors and shaders.
Grammar/attribute/material tables and binary scopes retained. Runtime: ParticleSimulation,
ParticleRenderer/WeatherGroups; [weather](weather.md) owns effect grouping/strength/wind.

## Grammar and attributes

**Verified** line-based // comments; system/technique/renderer/emitter/affector/observer/handler
blocks, optional technique/emitter names, property values and nested dynamic attributes.
No quoted/import/variable syntax observed in shipped scripts. Random attributes sample on use;
life-fraction versus system-age input depends on the consumer. Emitter velocity/lifetime/dimensions
use system age times scale_time; Scale uses life fraction unless since_start_system.
**Verified plugin** `createParticleSystemTemplate`: a system name defined again replaces the earlier
template (logged "already exists; old template deleted"); scripts come from every registered location,
mods included ([overview](overview.md#resource-lookup-and-mod-overrides)).

**Verified plugin** control points sort byX. Linear extrapolates first segment below firstX,
holds lastY beyond last. Spline uses u=min(x/(lastX-firstX),1), WITHOUT subtracting firstX;
even parameter spacing, Hermite/Catmull-Rom y tangents, not interpolation at control-point X.
**Observed** oscillation base+amplitude*wave(frequency*t+phase), exact plugin formula untraced.
all_particle_dimensions takes precedence; xyz_scale excludes per-axis rates. Default visual
quota500 is Verified from constructor, not inferred from current ash density.

## Simulation and rendering

CPU emission/ages/forces/curves/size/rotation/colour/observers mirror decoded inputs; unsupported
plugin behaviour remains a limit. Billboards/material alpha/additive/cutout/depth and weather
wrapping differ from opaque world shading. Original particles are not fogged like opaque meshes.
CAMERA effects use one unit, camera-ahead cube and respawn/wrap geometry; GLOBAL sphere and
GLOBAL_POINT rings are different. Coordinate basis and previous camera movement must match wraps.

Meitou pools simulations/content and warms new weather groups on workers. Region swaps debounce2s;
in-region weather changes warm without that wait. Publish prepared groups between frames;
old particles can remain while new content loads. Missing textures fall back to white, not proof
of original appearance. Reduced-resolution particles use depth-aware composition; density
changes emission/population. [Post](../render-post.md), [weather](weather.md).

### Grammar (Verified)

| Dynamic attribute | Block | Uses |
|---|---|---|
| `dyn_random` | `min`, `max` | 745 |
| `dyn_curved_linear` | `control_point x y` (repeated) | 146 |
| `dyn_curved_spline` | `control_point x y` (repeated) | 73 |
| `dyn_oscillate` | `oscillate_type` (`sine`, `square`), `oscillate_frequency`, `oscillate_phase`, `oscillate_base`, `oscillate_amplitude` | 29 |

### Grammar (Verified) — layout/example

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

### Survey of the 93 scripts (Verified: `meitou-tools particles`)

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

### What a weather gives (Verified: `meitou-tools particles effects`, merged base records)

| Weather | Camera effects → system (extent) |
|---|---|
| `Heavy_Rain`, `Kenshi_Wet_Forest`, `misty rain`, ... | `Kenshi_Heavy_Rain` (CAMERA_RAIN) → `Kenshi_Heavy_Rain` (120; colour white; sky multiplier 0.6) |
| `Kenshi_red_rain` | `Kenshi_red_Rain` → `Kenshi_Heavy_Rain_red` (120; colour `F56568`; 0.25) |
| `Kenshi_black_rain AND ground steam vents` | `Kenshi_black_rain` → `Kenshi_black_rain` (320; colour `9A6B65`; 0.2) |
| `light rain`, `swamp rain no wind` | `rain_light` → `kenshi_rain1` (150; sky multiplier 0) |
| `Kenshi_Ash-Flakes` | `Kenshi_Ash-Flakes_Light` (CAMERA) → `Ash-Flakes_Light` (120; wind affected; 0.25); `poison gas [White]` (WANDERING_GAS, 70) → `Poison_Gas_White` (extent 580, wind affected): not yet |
| `Kenshi_Ash-Flakes_BLACK_and_Rising Steam` | `Kenshi_Ash-Flakes_BLACK` (CAMERA) → `Ash_Flakez_Black` (100); `Kenshi_Rising_Steam` (POINT) |

## Expression reference

- Curves: `range = last x − first x`; `u = min(x / range, 1)`
- Simulation as implemented: `size += value · dt · system scale`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- Files (Verified)
- Grammar (Verified)
- Curves (Verified: Plugin_ParticleUniverse_x64.dll `DynamicAttributeCurved::getValue` / `processControlPoints`, disassembly; OgreMain `SimpleSpline::interpolate`, `recalcTangents`)
- Survey of the 93 scripts (Verified: `meitou-tools particles`)
- Materials (Verified: `data/particles/materials`, `materials/forward/basic.program`, `basic.hlsl`)
- Weather changes (`WeatherGroups`, Observed: a viewer choice)
- What a weather gives (Verified: `meitou-tools particles effects`, merged base records)

## Unknown

- The plugin's defaults other than the Verified quota500 (assumed: size100, box100, velocity100, rate10, life3); the oscillation
  formula and phase unit, Circle (ring or disc), the Randomiser, Vortex, Gravity, SineForce rules, the unit
  of the rotation values; `oriented_shape`; mass.
- How `sky colour multiplier` tints the particles; the group's fade-in and -out times; how often a camera group is renewed.
- Whether the system node is nudged by the small term in `FUN_140101800` (a distance squared along the pivot-to-camera direction; not
  modelled).
