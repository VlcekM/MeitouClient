# Sun shadows

Original CSM/RTW/settings/caster/receiver research from exe, shipped HLSL/materials and data.
Meitou scheduling/range/array choices: [render-shadows](../render-shadows.md). Source scopes,
settings and split expression are retained below.

## CSM fit and casters

**Verified** one float32 atlas, cascades tiled ceil(sqrt(n)); four→2x2, tile column=i/2,row=i%2.
Point sampling/white border; queues10..80 use shadow-caster materials. Splits use near1,
far Shadow-Range,lambda0.95 with INNER splits halved: default1,35.3,96.1,376.2,5000.
Light z points away from sun; choose +Z up near vertical sun. Stable diameter
max(sqrt((f+n)^2*k+(f-n)^2),2*f*sqrt(k)), k=(aspect*t)^2+2*t, t=tan(fov/2).
The extra2*t differs from textbook squared tangent. Bound camera-near..splitFar, grow depth
max(0.025*depth,2)+D/65536; snap XY texels and depth quantum. Preserve caster bias/coverage,
terrain and cutout material rules, including materials with no shadow caster.

## CSM receiver

**Verified HLSL** cascade selected by projection z WITHOUT dividing w, compared to split-next-1;
actual boundary about split+4 for near5/far50000. Beyond last split fully lit, no far fade.
Transform light-space point with per-cascade scale/trans. No per-tile bound check: taps can
read neighbouring cascade; white border only outside whole atlas. Receiver bias0.

HEX12 offsets: (1,0),(-0.5,0.866),(-0.5,0.866),(2.5,0.866),(1,1.732),(-0.5,2.598),
(-2,1.732),(-2,0),(-2,-1.732),(-0.5,-2.598),(1,-1.732),(2.5,-0.866).
Duplicate is intentional fidelity; do not fix the asymmetric mean(0,0.144).
Noise=red white-noise.png64² at atlasUV*1024, point/wrap. Shift X bynoise*0.25,
rotate angle=noise*2*3.1415, scale csmRadius*0.6*0.5. Project offset to light-normal plane
and use its third component as depth delta: mixed UV/depth units and Y convention are original.
Mean comparisons produces lit fraction; ambient forced0. Shadow multiplies sun diffuse/specular/
translucency, not ambient/environment.

## RTW and engine differences

RTW mode2 uses separate warp/receiver pipeline; not reproduced as CSM. Settings mode/quality/
range and original restart semantics are in tables/[settings](settings.md). Meitou soft shadows,
minimum-radius impostor filtering, fifth cascade, fog cap and terrain-horizon sweep are engine
features; preserve Faithful formulas and independent switches. [Lighting](lighting.md).

### Settings (Verified: exe settings loader, writer and options screen; `settings.cfg`)

| `settings.cfg` key | Values | Meaning |
| --- | --- | --- |
| `shadow mode` | 0, 1, 2 | 0 "Disabled", 1 "CSM", 2 "RTWSM" (the options list's labels). A missing key reads as **0**. |
| `shadow quality` | 0, 1, 2 | The map side, from the table **{1024, 2048, 4096}** indexed by the value. A missing key reads as **0** (corrected: an earlier version said it left the previous value; the loader's fallback local is 0 and nothing else writes it before). |
| `Shadow Range` | 1000 … 9000 | How far shadows reach (world units; options slider 1000 to 9000). A missing key reads as **5000**; a value of 10 or less becomes **10000**. |

### Splits (Verified: exe CSM split set-up) — layout/example

```
split[0] = 1,   split[4] = Shadow Range
split[i] = 0.5 · ( λ · near · (far / near)^(i/4) + (1 − λ) · (near + (far − near) · i/4) )      i = 1..3
```

## Expression reference

- Settings: `shadow mode=0`; `shadow quality=0`; `Shadow Range=5000`; `shadow mode=2`
- Splits: ` split[0] = 1, split[4] = Shadow Range split[i] = 0.5 · ( λ · near · (far / near)^(i/4) + (1 − λ) · (near + (far − near) · i/4) ) i = 1..3 `
- Cascade fitting: `D = max( √((f + n)² k + (f − n)²), 2 f √k )`; `n = split[i]`; `f = split[i + 1]`; `k = (aspect · t)² + t + t`; `t = tan(fovY / 2)`; `texel = D / tile pixels`; `biasParams = (fixed, 4.0, 0.04, 0)`; `fixed = max(0.75 · |2 / size.z|, 0.001)`; `shadowParams = (0, 0.05, 0, ·)`
- Casters: `pos.z = max(pos.z, 0)`
- Meitou shadows: `uShadowAtlas.w = 1`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- Settings (Verified: exe settings loader, writer and options screen; `settings.cfg`)
- The map (Verified: exe CSM creation)
- Splits (Verified: exe CSM split set-up)
- Cascade fitting (Verified: exe per-cascade fit, disassembly where the decompiler was unclear)
- Casters (Verified: `shadowcaster.hlsl`, `shadows.material`, `terrain.hlsl`, the `.material` files; Observed where noted)
- Receiver (Verified: `deferred.hlsl` `main_fs` with `CSM`, `shadowFunctions.hlsl` `computeShadowMultiplier`)
- How the term enters the lighting (Verified: `deferred.hlsl`)
- The RTW mode (mode 2; Verified from the shaders and the exe RTW class unless noted; not reproduced yet)
