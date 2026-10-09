# Light shafts (Meitou)

Status: **first version** (2026-10-10). A Meitou switch, `shafts` (on by default, `--no-shafts` / `--faithful shafts` turn it off). It has no
counterpart in the game. Kenshi lights its haze and its weather fog the same everywhere (`atmoKenshiHaze` in `AtmosphereShaders`,
[formats/sky.md](formats/sky.md)), so shadows end at the ground and the air above a shadowed valley glows like the air in the sun.
This is engine work, so each claim says where it comes from: *from the code*, *measured* (how), or *open*.

## What it does

*From the code.* It **shadows the haze that already exists** and adds no second fog. The game's haze is `mix(colour, rgb, α)`: the
surface's colour, the haze colour `rgb` (SkyX's in-scattering, then the weather fog's colour) and its alpha `α` (the 0.06 D → 0.6 D
ramp plus the weather fog's ease-in-out curve, both functions of distance only). A shadowed version keeps the surface's part and scales
the haze's part by the share `L` of the haze that the sun reaches along the ray:

    result = colour · (1 − α) + rgb · α · (1 − k · (1 − L))

So the pass subtracts `k · (1 − L) · rgb · α`. It gets `rgb · α` from `atmoApplyHaze(vec3(0), eye, point)`, the same function every
world shader ends with. Air in the sun keeps the game's haze exactly. `k` (*darkening*) is the strength (`--shafts-strength`, default 0.8,
a Tab slider) times the sun's share of the light the air scatters: luminance of the sun colour over sun plus sky ambient
(`WorldFrame.ShaftDarkening`). It fades to nothing as the sun goes below the horizon, so the sky's own light keeps shadowed air from
going black, and night is untouched.

The sky has no haze over it (the skydome is drawn as it is), so it is scaled instead: `1 − k · (1 − L) · sky weight · α(far)`, with sky
weight 0.5 (`--shafts-sky`). That gives the dark wedge a ridge casts into the sky when you look into a low sun.

## The grid (`LightShaftShaders`, `PostProcess.Shafts.cs`)

*From the code.* There are three full-screen fragment passes after the scene and the GI resolve, before the placed fog volumes (which
blend over the hazed scene, so the correction has to come first):

1. **Inject** (atlas, RG16F). A froxel grid of `cells across × (cells across × aspect) × slices` (default 160 × 90 × 64), with
   exponential slices along the ray from `--shafts-near` (50) to where the haze alpha stops growing (`SkyRenderer.HazeCompleteDistance`: the ramp's end
   stretched by the haze strength, or a full weather fog's completion; at most the view distance). The
   slices are tiled 8 per row into one 2D atlas, so no 3D images are needed. Each cell takes `--shafts-grid`'s sample count (default 2)
   of shadow lookups, stratified over the slice's depth and jittered within the cell (interleaved gradient noise, seeded
   by the frame under a temporal upscaler, fixed without one), and writes `(Δα · lit, Δα)` with `Δα` the haze alpha gained across the slice.
2. **Integrate** (atlas, R32F). For each cell, the sums over its slice and every nearer one: `L = Σ Δα·lit / Σ Δα`, or 1 where no
   haze has gathered yet.
3. **Apply** over the scene colour, with the fog volumes' blend `scene · a + colour`. It rebuilds the pixel's distance the way the fog
   volumes pass does (near slice depth, then far slice depth, then the water plane where it is in front). It reads `L` bilinearly across
   cells and linearly between the slices' far ends.

The temporal upscaler (TAA, FSR or DLSS) averages the per-frame jitter, because the passes run at the render size before it.

**Shadow term of a point in the air** (`shaftShadow`). It is one hardware compare in the first cascade whose box holds the point. There
is no filter, and there is no surface to offset along its normal. The Meitou receiver adds its fade at the shadow range, its terrain term
(mountains out to the horizon, `msTerrain`) and its landmark map. The Faithful receiver's cascades end at the last split.

## Measured

*Measured* 2026-10-10 on an RTX 4070, `--view swamp --size 1920x1080 --upscaler dlss --render-scale native --ab shafts --bench-frames 600`
(GPU timestamps, the post chain's `shafts` section):

| Grid | Shafts, mean (p99) |
|---|---|
| 160 × 90 × 64, 2 samples (default) | 0.28 ms (0.47) |
| 96 × 54 × 32, 1 sample (`--shafts-grid 96,32,1`) | 0.14 ms (0.50) |

The full-resolution apply pass is most of the floor. At 960 × 540 the default grid took 0.23 ms.

**What it looks like.** In clear weather the effect is slight. The game's haze barely starts within shadow range (it ramps in from
0.06 D, 3000 units at the default D), so there is little air to shadow. In the weather fog (dust storms, the shek desert storm, fog
complete at 15000 to 35000 units), with a low sun (sunset is 23:00, `GLOBAL CONSTANTS`), hills and buildings throw clear shadows into
the fog, and ridges cast dark wedges into the sky toward the sun. A debug view, `--shafts-debug`, writes red = `L`, green = the haze
alpha at the pixel, blue = the haze colour's brightness.

## Open

- **The placed fog volumes** (fogfeatures, the twisters' dust balls) are not shadowed. They blend over the scene after this pass.
- **No temporal reprojection of the grid itself.** Without a temporal upscaler (FXAA) the jitter is fixed, a still pattern rather than a shimmer.
- The pass runs only while the sun shadows are on (`ShadowPass.Enabled`): without them every sample would be lit.
- **No brightening.** Lit air keeps the game's Rayleigh haze. A forward-scattering glow round the sun (Mie phase) is not added.
- **No lamp light** in the air.
- **The physical haze and the simple sky** weight the slices by an approximate alpha by distance (the direction is left out).
