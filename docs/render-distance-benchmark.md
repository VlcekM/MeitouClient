# Render distance limits

Draw-distance design and measurement method. Current controls are in [viewer.md](viewer.md);
benchmark usage is in [bench.md](bench.md). Feature rules are in [render-foliage.md](render-foliage.md),
[render-objects.md](render-objects.md) and [impostors.md](impostors.md).

## Projected size

For vertical field of view 50 degrees (`WorldCamera.FieldOfView`), focal length in pixels is
`f = (H/2) / tan(25 degrees)`: **965 px at 900 lines, 1158 px at 1080**. An object `D` world
units across spans one pixel at distance `D*f`. A box of half-diagonal `r` has a projected width
between approximately `2r/sqrt(3)` and `2r`. **Verified** arithmetic; dimensions below are
**Observed** from base-game GRASS records, resolved meshes and object bounds.

| Geometry | Dimension | One-pixel distance at 900 lines | At 1080 lines |
| --- | ---: | ---: | ---: |
| Median grass blade height (31 GRASS records) | 10.8 | 10,400 | 12,500 |
| Upper-quartile grass | 16.4 | 15,800 | 19,000 |
| Tallest grass | 38.8 | 37,400 | 44,900 |
| Small foliage, radius 4 | 4 | 4,500-7,700 | 5,400-9,300 |
| Small foliage, radius 20 | 20 | 22,000-39,000 | 27,000-46,000 |
| Small/medium boundary | 40 | 45,000-77,000 | 54,000-93,000 |
| Median foliage radius | 83 | 93,000-160,000 | 111,000-192,000 |
| Medium/large boundary | 125 | 139,000-241,000 | 167,000-290,000 |
| `CraggyTree` radius | 207 | 231,000-400,000 | 277,000-479,000 |
| `BushTree01` radius | 626 | 0.7-1.2 million | 0.8-1.45 million |
| Hub object-part radius, p25 | 12 | 13,000-23,000 | 16,000-28,000 |

A one-pixel rule can retain large objects across most of the world; fog, occlusion and streaming
must also bound work. Bounding radius alone does not prove that an instance has visible coverage.
Use triangle-size/coverage diagnostics rather than treating these distances as recommended settings.

## Measurement protocol

Hold seed, time, weather, resolution, render scale and upscaler fixed. Sweep one size class or
feature at a time, then test combined ranges. Test a dense forest, a town and an open desert,
both settled and moving. Separate main-view, reflection, shadow and pre-frame costs.

Example forest camera: `--world --at -37582,-80684 --yaw -70.5 --pitch 6.1 --distance 10588`.
Use `--fly-speed 0` for a still flight benchmark. Detailed pass timings use
`MEITOU_PASS_STATS=1 MEITOU_PASS_STATS_SKIP=30`; current triangle diagnostics use `--bench-tris`.

**Observed** (RTX 4070, 1600x900 forest, paired paced/pipelined runs): at light load the GPU
clocks down between paced frames (4.75 ms paced versus 2.52 ms pipelined); at heavy load both
were about 8 ms. Frame-slot waits can land inside CPU stage timings. Pipelined flights can also
outpace streaming, changing the rendered instance set. Match counts and isolate waits before
attributing a cost difference to a renderer.

## Cost interpretation

- Missing or evicted impostors fall back to meshes: atlas residency can increase geometry cost
  even when VRAM use appears low. Measure retained atlas mips, fallback instances and churn.
- TERRAIN-mode rock materials depend on the instance's biome. Their impostor/cache rules must
  preserve that dependency; see [impostors.md](impostors.md).
- Medium and small foliage can be primitive- or instance-bound. A lower resolution does not
  address geometry that remains unchanged. Measure screen-size LOD, culls and shadow copies.
- Grass has page construction, uploads, compute selection, drawing and motion-vector costs.
  Test streaming coverage during flight, not only a fully settled view.
- Object part distances are compatibility rules as well as workload limits; lifting them changes
  Faithful output. Account for object textures and meshes when measuring memory.
- GPU culling still needs CPU zone/group work lists for each view. Record their cost separately
  from secondary-command recording and GPU cull kernels.
- Cached shadow cascades create periodic spikes. Use tail latency, cadence and caster counts.
- Impostor quads may themselves be subpixel work. Count projected size before raising ranges.

**Observed** (RTX 4070 range sweeps, 1600x900, forest/Hub/Stack): extreme all-class ranges became
GPU-bound before exhausting available VRAM. This is evidence about those views/settings, not a
current performance guarantee. Re-run the protocol after changing LOD, residency or streaming.

**Unknown:** how much geometry a range-only cull retains below one pixel without a projected-size
survey; the general cause of long-range shadow outliers without per-frame diagnostics.
