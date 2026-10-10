# Light shafts (Meitou)

Status: **first version** (2026-10-10). A Meitou switch, `shafts` (on by default, `--no-shafts` / `--faithful shafts` turn it off). It has no
counterpart in the game. Kenshi lights its haze and its weather fog the same everywhere (`atmoKenshiHaze` in `AtmosphereShaders`,
[formats/sky.md](formats/sky.md)), so shadows end at the ground and the air above a shadowed valley glows like the air in the sun.
This is engine work, so each claim says where it comes from: *from the code*, *measured* (how), or *open*.

## What it does

*From the code.* It has three parts: it **shadows the haze that already exists** (below), it **shadows the placed fog volumes**, and it adds a thin
**air layer** near the ground that lights up towards the sun (both have their own sections further down). The game's haze is `mix(colour, rgb, α)`: the
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

## Placed fog volumes

*From the code.* The fog volumes pass (`PostProcessShaders.FogVolumes`) includes `LightShaftShaders.FogVolumeShadow` and so defines
`FOG_VOLUME_SHADOW`. Each block, and each sphere and beam that is not additive, scales its colour by `1 − darkening × (1 − share)`. The share
is the mean of four reads of the inject atlas's sun visibility (its third channel, bilinear across cells, linear between slices) along the
part of the path the eye sees into: from the near side to `0.7 / density` further at most (the volume's curve is complete about `1 / density` in).
At first it averaged the whole path down to the ground, so shadow deep inside thick fog and on the valley floor under it showed through as
terrain-shaped blotches, and reads snapped to whole slices, which made bands (seen at Fog Islands, 2026-10-10). Additive volumes glow on their own and are left alone. The other programs that include `FogVolumeShaders.Functions` (SSAO's
air visibility, the fog shading rate) do not define it, so their SPIR-V is unchanged (the hash pins prove it). While the shafts are off the darkening
is 0 and the scale is exactly 1.

*Observed* 2026-10-10: in the swamp at sunset, the fog walls under the canopy go much darker and the forest behind them shows through. Whether
that is too strong under the swamp's rain clouds is **open**: the darkening compares the sun's colour with the sky's and does not know about cloud cover
(see "Cloud cover" below for the fade added later that day).

## Cloud cover

*Observed* 2026-10-10: in the swamp's rain (`Kenshi_Wet_Forest`, clouds density 1) the trees cast crisp, dark shafts across the whole sky through the
rain haze, which reads wrong under a full overcast. The game's weather never touches the sun ([formats/weather.md](formats/weather.md) "Sky and
clouds"), so the sun colour alone cannot tell. *From the code.* `WorldFrame.ShaftCloudFade` scales the darkening `k` by
`1 − shade · smoothstep(from, 1, c)`, with `c` the sky's cloud density after the weather's 30 s transition (`SkyRenderer.CloudDensity`, so `--clouds`
wins), `shade` 0.85 (`--shafts-clouds`) and `from` 0.3 (`--shafts-clouds-from`). Clear weathers (c 0) keep the shafts as they were; a full overcast
keeps 15 % of them. The ground shadows are not touched (that would be a separate switch). Whether 0.85 is right is **open** until it has been looked at
in the swamp rain.

## Air layer

*From the code.* `--shafts-air <x>` (default 1, Tab slider "Light shafts air", 0 for none): a medium of density `x · 1.5e-5` per world unit at the
ground under the eye, falling off exponentially above it (scale height `--shafts-air-height`, 800). A fourth pass (`LightShaftShaders.Scatter`, RGBA16F
atlas) marches each cell's slices front to back. Per slice it adds `T · (1 − e^(−σΔ)) · S · (1 − α)`, where `T` is the transmittance so far, σ the
density at the slice's middle, Δ its length and α the game's haze alpha there (the layer sits in front of the haze, not on top of it). The radiance
`S` is the sun's irradiance `π² · sunLight` (what a white Lambert surface lit by `kenshiLight` receives) times the slice's sun visibility times a
Henyey-Greenstein phase with `g = --shafts-air-phase` (0.6, bright towards the sun), plus the sky's ambient radiance from above. The apply pass
then gives `(scene − dark · haze) · T + S`, and `T · (sky factor)` on the sky.

**By the time of day** (`WorldFrame.ShaftAirByTime`, added 2026-10-10): the density is also multiplied by a morning-mist ramp, `--shafts-air-dawn`
(1, Tab slider) at sunrise and at sunset, easing (smoothstep) to `--shafts-air-day` (0: no air layer at midday) over `--shafts-air-ramp` (5.5) hours after sunrise and
before sunset, using the clock's `sunrise` and `sunset` (5 and 23 in the base game). At night it holds the dawn value, which does not matter
because the shafts need the sun. The stats line prints the factor (`x0.97 by the hour` at 05:36). Defaults chosen by the owner 2026-10-10 after trying 2.5 and 0.3.

*Observed* 2026-10-10: in clear weather at 22:24 to 22:42, looking into the sun past the Hub's hill, beams fan out from the hill's edge across the sky,
with a glow round the sun. With the sun high it only adds a light haze in the distance.

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

With the fog volume shadow and the air layer (same command, same day, nothing else on the GPU): `shafts` 0.54 ms mean (p99 0.72), the fog volumes pass +0.11 ms (0.29 against 0.18); the whole GPU frame +0.50 ms (6.02 against 5.52).

## Open

- **No temporal reprojection of the grid itself.** Without a temporal upscaler (FXAA) the jitter is fixed, a still pattern rather than a shimmer.
- The pass runs only while the sun shadows are on (`ShadowPass.Enabled`): without them every sample would be lit.
- **No lamp light** in the air.
- **The physical haze and the simple sky** weight the slices by an approximate alpha by distance (the direction is left out).
