# Render distance benchmark: what stops us drawing everything larger than 1 px (2026-10-07)

*In short: "1 px" for trees, rocks, ruins and buildings is beyond the edge of the world (and the 250 000-unit haze), so the target is
"everything in the world", plus small junk to 4 000-77 000 units and grass to 10 000-19 000. At that preset the GPU, not the CPU, gives out
first: 21.8 ms a frame in the forest flight and 45.8 ms at the Hub (1600 x 900, RTX 4070), against 2.5-2.7 ms today. The render thread
needs 7.5-8 ms. The GPU time is not fill: it is triangles and instances drawn at full detail far away. The ranked causes are (1) the
192 MB impostor budget, which makes trees whose atlas was refused draw as full meshes (22 ms at the Hub), (2) TERRAIN-mode rocks, which have
neither LOD nor impostor (21-41 million triangles, 5.6-10.5 ms), (3) medium meshes too small for an impostor class, drawn as meshes (3.8-5.3 ms),
(4) placed objects at 400 000 units (4 ms GPU, 1.3 ms CPU), (5) the CPU's per-view foliage work lists (3.8 ms of the render thread).
Shadows, video memory, fill rate and command recording are not limits. Multithreaded recording (wave 4) loses about 0.3 ms of render thread
at every range measured; mode 0 should be the default.*

Everything here is **Observed** unless marked: RTX 4070 (11.4 GB budget), driver as on 2026-10-07, Release build of branch
`render-distance-benchmark`, 1600 x 900 unless stated, `--time 13`, GPU idle before each run (`nvidia-smi` 0-1 %). Raw logs:
`C:\Temp\agent-R\logs\<run>.log`; the run lists are `C:\Temp\agent-R\plans\*.txt` (one line per run: name, view, frames, environment,
options), run by `C:\Temp\agent-R\sweep.sh` / `run.sh`, and the tables below come from `C:\Temp\agent-R\summarize.js` (`tableA.txt`,
`tableB.txt`, `tableC.txt` in the same folder).

## 1. Method

**Views** (`--fly-benchmark 300`, `MEITOU_PASS_STATS=1 MEITOU_PASS_STATS_SKIP=30`, `MEITOU_FOLIAGE_TRIS=1`):

| View | Options | Camera |
| --- | --- | --- |
| forest | `--world --at -37582,-80684 --yaw -70.5 --pitch 6.1 --distance 10588` | flying, 150 units a frame, circle of 12 000 |
| forest still | the same with `--fly-speed 0` | still |
| Hub | `--world --town "The Hub" --distance 40000 --pitch 3 --fly-speed 0` | still |
| Stack (open desert, far mountains) | `--world --town Stack --distance 40000 --pitch 3 --fly-speed 0` | still |

**Range presets** (foliage `--range-large/medium/small`, grass `MEITOU_GRASS_RANGE` = x the game's 1000, objects `--object-distance`,
`--distant-range`, shadows `--shadow-range`; every other option at its default):

| Preset | large | medium | small | grass | objects | distant towns | shadows |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| D (defaults) | 12 000 | 5 000 | 800 | x4 = 4 000 | 12 000 | 10 | 10 000 |
| C20 (8.11's `all20`) | 20 000 | 10 000 | 4 000 | x4 | 40 000 | 30 | 15 000 |
| C40 (8.11's `all40`) | 40 000 | 20 000 | 8 000 | x4 | 100 000 | 50 | 15 000 |
| CPX (the 1 px target, section 2) | 192 000 | 80 000 | 12 800 | x16 = 16 000 | 400 000, part range 400 000 | 100 | 10 000 |

Per class, one range at a time was swept from D (large 24k-192k, medium 10k-80k, small 1.6k-12.8k, grass x8-x32, objects 48k / 400k,
shadows 15 000).

**Two kinds of run, because each kind falsifies one side** (both **Verified** by the runs named):
- *GPU times come from pipelined runs* (`--fly-pipelined`: no pacing, no wait per frame). The paced benchmark sleeps to 60 frames a second,
  and at light load the GPU clocks down between frames: forest D measured 4.75 ms of GPU paced (`F_D_1`) and 2.52 ms pipelined (`P_D_1`),
  terrain 1.49 against 0.90 ms; at 96k large range both give 7.96-8.00 ms (`F_L96k`, `P_L96k`). The "GPU-bound at the defaults" reading of
  [renderer-native.md 6.6](renderer-native.md#66-progress-wave-4-2026-10-06) (gpu-wait 3.7-4.5 ms) is mostly this downclocking.
- *CPU times come from paced runs.* In a pipelined run the wait for a free frame slot happens in `upd-terrain/post start` (6.1 of its 6.3 ms
  at 192k, `P_L192k` fence column) and in "cpu only".
- Pipelined flights outrun the streaming (no wall-time pacing): the default grass drew 21 000 blades a frame pipelined against 75 700 paced.
  Counts for grass come from the paced runs; mesh and impostor counts agree within a few percent where both exist.

**Instrumentation added** (commit `ace4770`; all statistics, the pictures are unchanged, section 8):
- `MEITOU_FOLIAGE_TRIS=1`: each GPU-culled foliage view's indirect arguments are copied (one buffer copy of ≤ 20 bytes a draw) and read a
  frame ring later: triangles and instance-draws of the meshes and of the TERRAIN-mode rocks, impostor quads, per view kind (colour, shadow,
  reflection), and per mesh name for the colour views (the benchmark's `top` lines). The older `counts … foliage instances` value
  (`LateVisible`) is not used here: it disagrees with the arguments.
- The pre-frame command buffer's GPU time (uploads, cull and grass kernels, impostor bakes) with `MEITOU_PASS_STATS=1` (`gpu  mean` line).
- Objects: instances, triangles and calls per frame by colour / shadow, instances stopped by the game's part distance, and the radii of the
  resolved instances (`counts`, `sizes` lines); `MEITOU_OBJECT_PART_RANGE=<u>` replaces the game's `objects view range` (3000) for parts.
- **`PassMeter` GPU column fixed**: it divided a row's GPU time by the number of times the row ran, so stages drawn once per depth slice
  (terrain, objects, foliage, water) and cascades drawn every second or fourth frame were shown per run, not per frame. At 96k large range
  the foliage row showed 2.57 ms where the frame spent 5.16 ms, and the rows summed to 4.73 of 7.19 ms (`gap_L96k_rt0`, before and after).
  Rows now sum to the frame (first to last stamp); every earlier `PASSCSV` GPU figure for those rows is per run.

## 2. What "1 px" means

Vertical field of view 50° (`WorldCamera.FieldOfView`), so the focal length is `f = (H / 2) / tan 25°` = **965 px at 900 lines, 1158 px at
1080**. Something `D` units across is one pixel at `d₁ = D · f`. The sizes the docs use are bounding radius × largest scale
([formats/foliage.md "Mesh sizes"](formats/foliage.md#mesh-sizes); objects from the benchmark's `sizes` line), and a box whose half-diagonal
is `r` is between `2r/√3 ≈ 1.15 r` and `2 r` across, so each row gives that span (**Verified** arithmetic; sizes **Observed**):

| What | size | d₁ at 1600 x 900 | d₁ at 1920 x 1080 | range today (D) |
| --- | ---: | ---: | ---: | ---: |
| grass blade, median of the 31 GRASS records (mid quad height) | 10.8 | 10 400 | 12 500 | 4 000 |
| grass blade, upper quartile / tallest | 16.4 / 38.8 | 15 800 / 37 400 | 19 000 / 44 900 | 4 000 |
| small foliage, r = 4 (5th percentile of all meshes) | 4 | 4 500-7 700 | 5 400-9 300 | 800 |
| small foliage, r = 20 | 20 | 22 000-39 000 | 27 000-46 000 | 800 |
| small / medium boundary | 40 | 45 000-77 000 | 54 000-93 000 | 800 / 5 000 |
| medium, median of all meshes | 83 | 93 000-160 000 | 111 000-192 000 | 5 000 |
| medium / large boundary | 125 | 139 000-241 000 | 167 000-290 000 | 5 000 / 12 000 |
| large: `CraggyTree` / `BushTree01` | 207 / 626 | 231 000-400 000 / 0.7-1.2 M | 277 000-479 000 / 0.8-1.45 M | 12 000 |
| object parts, Hub p25 (radius 12) | 12 | 13 000-23 000 | 16 000-28 000 | 3 000 (game's part rule) |
| objects, forest median / p90 (116 / 369) | 116 / 369 | 129 000-224 000 / 411 000-712 000 | 155 000-269 000 / 494 000-855 000 | 12 000 |

The world is 295 000 units across, the haze is complete at 250 000 (`--fog`) and the terrain is drawn to 450 000. **So for large foliage,
medium foliage and buildings the 1 px rule never stops anything inside the world**: the target is "everything", and the haze, not the pixel,
is the real limit. For small foliage the target is 5 000-77 000 and for grass 10 000-19 000 (the tallest kinds 37 000-45 000). The CPX
preset is that target, with large 192 000 (more than half the world from any point), grass x16 and small 12 800. Two caveats:
- Building parts with a local radius up to 100 stop at the game's `objects view range` (3000, `ObjectRanges.PartRenderingDistance`)
  whatever `--object-distance` says: 13 787 of the 26 443 resolved instances at the Hub CPX. Lifting it (`MEITOU_OBJECT_PART_RANGE`)
  doubles the objects drawn (forest 3 761 to 7 659 a frame). It is a game rule (fidelity), not a cost limit.
- **Unknown**: how much of the CPX work is already under 1 px. The cull tests ranges, not projected size; section 7 (F4) proposes the test.

## 3. Results per class (forest flight)

GPU ms per frame, pipelined (`P_*`), with the colour views' foliage draws per frame from the tally:

| Run | GPU frame | pre-frame | shadows | terrain | objects | foliage | of it: meshes + impostors | rocks | grass | post | mesh tri (M) | rock tri (M) | impostors (k) | VRAM peak MB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| D | 2.52 | 0.24 | 0.32 | 0.90 | 0.12 | 0.40 | 0.30 | 0.10 | 0.00 | 0.25 | 0.03 | 0.36 | 10 | 3077 |
| large 24k | 3.63 | 0.31 | 0.46 | 0.90 | 0.12 | 1.28 | 0.74 | 0.54 | 0.00 | 0.25 | 0.08 | 1.97 | 75 | 3397 |
| large 48k | 5.86 | 0.51 | 0.47 | 0.90 | 0.12 | 3.29 | 1.52 | 1.75 | 0.02 | 0.27 | 0.09 | 6.60 | 337 | 3725 |
| large 96k | 8.00 | 0.61 | 0.48 | 0.90 | 0.12 | 5.32 | 2.15 | 3.13 | 0.03 | 0.28 | 0.92 | 11.9 | 466 | 3733 |
| large 192k | 11.20 | 0.66 | 0.48 | 0.90 | 0.12 | 8.47 | 2.99 | 5.45 | 0.03 | 0.28 | 2.28 | 21.5 | 492 | 3935 |
| large 192k, impostor budget 2048 | 9.93 | 0.66 | 0.48 | 0.90 | 0.12 | 7.20 | 1.73 | 5.44 | 0.03 | 0.28 | 0.09 | 21.5 | 494 | 4511 |
| medium 10k | 2.65 | 0.27 | 0.37 | 0.90 | 0.12 | 0.44 | 0.33 | 0.10 | 0.01 | 0.26 | 0.08 | 0.36 | 10 | 3163 |
| medium 20k | 3.24 | 0.32 | 0.52 | 0.90 | 0.12 | 0.80 | 0.61 | 0.17 | 0.03 | 0.28 | 0.53 | 0.50 | 11 | 3260 |
| medium 40k | 4.02 | 0.37 | 0.52 | 0.90 | 0.12 | 1.54 | 1.34 | 0.17 | 0.03 | 0.28 | 1.70 | 0.51 | 17 | 3525 |
| medium 80k | 6.57 | 0.40 | 0.54 | 0.90 | 0.12 | 4.01 | 3.77 | 0.21 | 0.04 | 0.28 | 6.27 | 0.72 | 30 | 3589 |
| small 3.2k | 2.54 | 0.25 | 0.34 | 0.90 | 0.12 | 0.40 | 0.30 | 0.10 | 0.00 | 0.25 | 0.03 | 0.36 | 10 | 3125 |
| small 6.4k | 2.71 | 0.26 | 0.41 | 0.90 | 0.12 | 0.46 | 0.35 | 0.11 | 0.01 | 0.25 | 0.11 | 0.42 | 10 | 3223 |
| small 12.8k | 4.15 | 0.35 | 0.81 | 0.89 | 0.12 | 1.41 | 1.10 | 0.30 | 0.01 | 0.26 | 1.25 | 0.88 | 10 | 3505 |
| grass x8 | 2.74 | 0.32 | 0.34 | 0.90 | 0.12 | 0.47 | 0.30 | 0.11 | 0.05 | 0.29 | | | | 3141 |
| grass x16 | 3.92 | 0.77 | 0.44 | 0.90 | 0.12 | 0.82 | 0.34 | 0.16 | 0.32 | 0.56 | | | | 3271 |
| grass x32 | 5.93 | 1.25 | 0.49 | 0.90 | 0.12 | 1.92 | 0.35 | 0.17 | 1.40 | 0.95 | | | | 3383 |
| objects 48k, distant 30 | 2.83 | 0.22 | 0.37 | 0.84 | 0.45 | 0.40 | | | | 0.25 | | | | 3543 |
| objects 400k, distant 100 | 5.05 | 0.31 | 0.42 | 0.82 | 2.58 | 0.44 | | | | 0.26 | | | | 4931 |
| objects 400k + part range 400k | 6.40 | 0.34 | 0.47 | 0.82 | 3.81 | 0.47 | | | | 0.27 | | | | 4913 |
| shadows 15 000 | 2.59 | 0.25 | 0.36 | 0.92 | 0.12 | 0.39 | 0.30 | 0.10 | 0.00 | 0.25 | | | | 3095 |

Draw counts that go with it (`tableC.txt`): large 192k draws 16 411 rock instance-draws and 3 862 mesh instance-draws a frame in colour;
medium 80k 9 163 mesh instance-draws (6.27 M triangles); small 12.8k 12 829 rock instance-draws (`FOLIAGE_ContourStones*`, about 47
triangles each, 5 000 instances of each of four meshes); grass blades (paced) 75 700 at x4, 595 000 at x16, 2.1 M at x32; objects at 400k:
3 761 instances, 4.46 M triangles, 370 calls (7 659, 6.58 M, 597 with the part range lifted). The shadow cascades' foliage stays at 0.4-1.5 M
triangles and 13-16 k impostors in every run: the cascades only take instances within 1.2 x the shadow distance of the eye, so they do not
grow with the foliage ranges (**Verified** by the counts).

Render-thread CPU ms, paced runs (`F_*`; `rt` is the frame's render-thread time without the GPU wait; `fol cull` is the CPU's work-list walk
of the main slices, `c0 cull` the shadow cascades' shared one):

| Run | frame p50 / p95 | rt p50 | cpu p95 | shadows | reflection | objects | foliage | fol cull | c0 cull | upd-foliage | jobs (other threads) |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| D | 6.7 / 12.5 | 2.34 | 5.6 | 1.36 | 0.40 | 0.06 | 0.29 | 0.11 | 0.22 | 0.49 | 0.55 |
| large 48k | 8.8 / 12.3 | 2.38 | 5.7 | 1.41 | 0.41 | 0.06 | 0.61 | 0.33 | 0.27 | 0.35 | 0.56 |
| large 96k | 11.2 / 15.8 | 3.21 | 7.8 | 1.57 | 0.50 | 0.06 | 1.05 | 0.71 | 0.46 | 0.51 | 0.59 |
| large 192k | 15.6 / 20.9 | 4.42 | 9.1 | 1.97 | 0.63 | 0.06 | 1.50 | 1.13 | 0.94 | 0.87 | 0.57 |
| medium 80k | 10.5 / 15.9 | 3.67 | 8.3 | 1.90 | 0.59 | 0.06 | 0.79 | 0.56 | 0.79 | 0.84 | 0.58 |
| small 12.8k | 8.6 / 13.1 | 2.51 | 6.0 | 1.59 | 0.41 | 0.06 | 0.35 | 0.15 | 0.38 | 0.54 | 0.58 |
| grass x16 | 8.1 / 13.5 | 2.62 | 5.6 | 1.35 | 0.40 | 0.06 | 0.37 | 0.13 | 0.25 | 0.72 | 0.55 |
| grass x32 | 12.3 / 17.0 | 6.06 | 10.4 | 1.35 | 0.40 | 0.06 | 0.37 | 0.13 | 0.27 | 3.94 | 0.51 |
| objects 400k | 9.1 / 13.2 | 3.11 | 6.5 | 1.44 | 0.49 | 0.79 | 0.28 | 0.10 | 0.21 | 0.50 | 0.61 |
| objects 400k + parts | 10.2 / 14.5 | 3.66 | 7.6 | 1.45 | 0.50 | 1.35 | 0.28 | 0.10 | 0.21 | 0.47 | 0.70 |

**Fill rate is not the limit** (**Verified** by resolution): large 192k at 800 x 450 / 1600 x 900 / 1920 x 1080 (`P_L192k_450p`, `P_L192k`,
`P_L192k_1080p`): GPU 9.48 / 11.20 / 11.94 ms, the rocks 5.33 / 5.45 / 5.49 ms (constant: primitive-bound), meshes and impostors 2.32 / 2.99
/ 3.17, terrain 0.30 / 0.90 / 1.21 (the terrain is the part that follows pixels). The Hub CPX with the 2 GB impostor budget: 21.6 ms at
800 x 450 against 23.7 at 1600 x 900 (9 % for four times the pixels). The D preset: 1.40 / 2.52 / 3.16 ms.

Rates (from the steps above; **Observed**): TERRAIN-mode rocks about 0.25 ms per million triangles (5.35 ms for 21.1 M more at 192k);
foliage meshes about 0.55 ms per million (3.47 ms for 6.24 M more at medium 80k, more instance-draws per triangle); impostors about 3.3 ns
each (1.73 ms for 494 000 with 94 000 mesh triangles, budget 2048).

## 4. All classes together (the presets)

GPU (pipelined) and render thread (paced) per view; `CPX imp2g` is CPX with `--impostor-budget 2048`:

| View, preset | GPU frame | pre-frame | shadows | terrain | objects | foliage meshes + impostors | rocks | grass | post | rt CPU p50 (p95) | VRAM peak (%) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |
| forest, D | 2.52 | 0.24 | 0.32 | 0.90 | 0.12 | 0.30 | 0.10 | 0.00 | 0.25 | 2.3 (5.6) | 3077 (27) |
| forest, C20 | 4.00 | 0.32 | 0.84 | 0.87 | 0.35 | 0.66 | 0.38 | 0.01 | 0.26 | 3.0 (6.6) | 4041 (35) |
| forest, C40 | 7.19 | 0.46 | 1.24 | 0.84 | 1.15 | 1.77 | 1.13 | 0.04 | 0.28 | 3.3 (6.6) | 4785 (42) |
| forest, CPX | 21.78 | 1.09 | 1.16 | 0.82 | 3.81 | 8.05 | 5.64 | 0.36 | 0.58 | 8.1 (16.0) | 6003 (52) |
| forest, CPX imp2g | 19.68 | 1.09 | 1.16 | 0.82 | 3.80 | 5.99 | 5.62 | 0.37 | 0.58 | 8.0 (11.7) | 6579 (57) |
| forest, CPX imp2g, shadows 15 000 | 20.20 | 1.12 | 1.67 | 0.83 | 3.80 | 5.95 | 5.61 | 0.37 | 0.58 | | 6789 (59) |
| forest still, D / C40 / CPX | 2.75 / 6.99 / 20.84 | | 0.38 / 0.98 / 0.89 | | 0.03 / 0.82 / 3.00 | 0.80 / 2.11 / 8.76 | 0.04 / 1.48 / 5.04 | | | | 2897 / 4464 / 5803 |
| Hub, D | 2.72 | 0.18 | 0.43 | 0.76 | 0.03 | 0.59 | 0.34 | 0.00 | 0.26 | 1.2 (1.5) | 2885 (25) |
| Hub, C20 | 4.37 | 0.20 | 0.89 | 0.73 | 0.31 | 0.88 | 0.97 | 0.00 | 0.26 | 1.8 (2.7) | 3539 (31) |
| Hub, C40 | 7.33 | 0.23 | 0.98 | 0.72 | 1.56 | 1.38 | 2.09 | 0.00 | 0.26 | 2.3 (3.7) | 4501 (39) |
| Hub, CPX | **45.76** | 1.09 | 0.60 | 0.68 | 4.01 | **27.31** | 10.52 | 0.62 | 0.83 | 7.7 (17.2) | 5905 (52) |
| Hub, CPX imp2g | 23.69 | 1.10 | 0.60 | 0.68 | 4.00 | 5.30 | 10.45 | 0.62 | 0.83 | | 6545 (57) |
| Stack, D | 1.87 | 0.16 | 0.33 | 0.92 | 0.00 | 0.04 | 0.05 | 0.00 | 0.25 | 1.1 (1.4) | 2949 (26) |
| Stack, C20 | 2.94 | 0.17 | 0.75 | 0.85 | 0.24 | 0.28 | 0.30 | 0.00 | 0.24 | 1.7 (2.7) | 3525 (31) |
| Stack, C40 | 5.74 | 0.20 | 0.89 | 0.84 | 1.40 | 1.29 | 0.78 | 0.00 | 0.24 | 2.2 (5.0) | 4613 (40) |
| Stack, CPX | **45.81** | 0.92 | 0.61 | 0.81 | 3.34 | **31.59** | 7.83 | 0.20 | 0.40 | 7.0 (19.6) | 5878 (51) |

At CPX the colour views draw (Hub) 46.0 M mesh triangles, 40.8 M rock triangles and 678 000 impostors a frame, objects 7.6 M triangles in 590
calls; the forest 11.7 M, 22.0 M, 506 000 and 6.6 M. Paced frame times at CPX: forest 29.8 ms p50, Hub 53.4, Stack 52.9 (`FC_F_CPX`,
`FH_CPX`, `FS_CPX`). The guard never acted (`entered pressure 0 times`, range scale x1.00) and the cull scratch refused nothing in any
CPX run (**Verified** by the `guard` / `scratch` lines of all 15 CPX logs), so these are the stated ranges. The "rt CPU" column is the
paced runs' render thread (`FC_*`, `FH_*`, `FS_*`; CPX imp2g from the mode-2 runs of 6.1).

**What the GPU draws at the Hub CPX** (the `top` lines, colour views, per frame): `FOLIAGE_Plant_HorsetailType` 8.4 M triangles in 13 610
instances, `FOLIAGE_Plant_Deep-Fir 01` 6.1 M, `Foliage_CYPRUS-TYPE 100-1216 height` 2.2 M, `Thin Tree [Wide] GREY-leaves` 2.1 M,
`FOLIAGE_Plant_FineTree01` 1.9 M, all large trees **with "atlas refused"**: the impostor budget (192 MB, LRU) was full, so they stayed
meshes; `Roctower01-03` 3.4 / 3.0 / 2.6 M, `Foliage_Metal_Tower_Melted-Piece01-05` 1.8-2.0 M each, `CinderBall01` 1.8 M (TERRAIN-mode
rocks); `CacTreeTu_03` 2.8 M (medium, no impostor class). Impostors: `Spore02[Blister]` alone is 611 000 of the 678 000 quads. The Stack
adds `TechRustyJunk_08` / `_02` / `_05` (medium, atlas refused, 5.0 / 3.8 / 2.5 M). Forest at large 192k: `Foliage_Rock_Bouldercluster_MonoTex`
1.5 M, `FOLIAGE_WADI-Boulder02` 1.5 M, `BlackRock02-04`, `Barkworm_Pillar01-03`, `MonuRock*` (all TERRAIN-mode rocks, 0.55-1.5 M each).
Medium 80k: `CacTreeTu_03` 1.8 M, `TechRustyJunk_04/06/07` 1.6 / 0.8 / 0.9 M, `Prehistorsetail` 0.9 M, `Canyon_BlockSmaller_*` — all "no
impostor class" (`ImpostorClass.For` gave none: radius x largest scale under `ImpostorClass.MinimumRadius` 48, or the source failed to load;
**Unknown** which of the two for each, not EMISSIVE or TERRAIN mode: `ImpostorSource.Ineligible` returned null for all of them).

## 5. Which resource saturates first, per class

| Class | First limit | Evidence | Second |
| --- | --- | --- | --- |
| Large foliage | GPU primitives: TERRAIN-mode rocks at full detail (no LOD levels, no impostor) | rocks 0.10 → 5.45 ms, 0.36 → 21.5 M triangles from 12k to 192k, flat with resolution | impostor budget: atlases refused → full tree meshes (Hub CPX 22 ms; forest 192k 1.3 ms); impostor quads (one mesh, `Spore02[Blister]`, 430-610 k) |
| Medium foliage | GPU primitives: meshes with no impostor class drawn as meshes | meshes 0.30 → 3.77 ms, 6.27 M triangles at 80k; budget 2048 changes nothing (`P_M80k_imp2g`) | CPU work lists (`fol cull` + `c0 cull` 1.35 ms at 80k) |
| Small foliage | GPU instance-draws of tiny meshes (rocks ~47 triangles each) and the cascades | +1.6 ms at 12.8k, 12 829 rock instance-draws, shadows +0.5 ms (small rocks within the shadow distance cast) | nothing on the CPU (+0.2 ms) |
| Grass | to x16 (16 000, the 1 px target for median blades): GPU +1.4 ms (blades, the kernels and uploads in the pre-frame, the grass motion pass in `post upscale` 0.11 → 0.81 at x32) | x32: render-thread `upd-foliage` 3.94 ms (page building), GPU +3.4 ms, pages missing within 1 500 units in 181 of 300 frames | |
| Objects | GPU (2.6-3.8 ms at 400k, 4.5-6.6 M triangles); the game's part rule caps half the instances at 3000 | objects GPU 0.12 → 2.58 → 3.81 ms; render thread objects 0.06 → 0.79 → 1.35 ms | VRAM (object textures and meshes, +1.8 GB, 4.9 GB peak) |
| Shadows | not a limit | 10 000 → 15 000: +0.07 ms GPU at D, +0.5 ms at CPX; the cascades do not grow with the foliage ranges | |
| All at CPX | GPU (21.8 / 45.8 / 45.8 ms) long before the CPU (7.0-8.1 ms) and VRAM (51-59 % of 11.4 GB) | section 4 | the render thread's work lists (3.8 ms), shadow spikes |

## 6. Ranked bottlenecks

Ranked by what each costs at the target (CPX), worst view first (**Observed**, from the tables above):

1. **Impostor budget (192 MB) refusing atlases at long ranges** (fixed by F1, 7.1; this is the state measured before): refused trees and junk draw as full meshes. Hub CPX 45.8 → 23.7 ms with
   a 2048 MB budget (**−22.1 ms**), forest CPX 21.8 → 19.7 (−2.1), forest large 192k 11.2 → 9.9 (−1.3). Cost: atlases 221 → 874 MB (+650 MB,
   57 % of the budget at most). It also thrashes: 1 776 atlas loads in 300 frames at large 192k with 192 MB (`P_L192k`, `resident` line).
2. **TERRAIN-mode rocks without LOD or impostor**: 10.5 ms at the Hub CPX (40.8 M triangles), 7.8 ms at the Stack, 5.6 ms forest (22.0 M);
   3.1 ms already at large 96k. Primitive-bound (flat from 800 x 450 to 1920 x 1080). They are excluded from impostors because the terrain
   shader textures them by the biome under each instance ([impostors.md](impostors.md) section 3), and their meshes have no LOD levels
   ([formats/foliage.md](formats/foliage.md)).
3. **Medium (and some large) meshes with no impostor class** drawn as meshes: 5.3 ms at the Hub CPX once the budget is large (5.5 M
   triangles), 3.8 ms forest medium 80k (6.3 M triangles, 9 163 instance-draws).
4. **Placed objects at 400 000**: 4.0 ms GPU (7.6 M triangles, 590 calls) and 1.3 ms render thread (7 659 instances culled on the CPU, plus
   `upd-terrain` 0.8 ms for the object streamer), +1.8 GB VRAM. Half the instances exist only because the part rule was lifted.
5. **The CPU's foliage work lists per view**: `fol cull` main 1.63 + cascades 1.69 + reflection 0.49 = **3.8 ms of the 8.1 ms render
   thread** at CPX (0.11 + 0.22 + ~0.05 at D). They walk every zone and group per view, whatever the GPU then culls. Also **shadow spikes**:
   single frames with `shadows` 10-14.5 ms at CPX (mean 3.2; `FC_F_CPX` worst frames), cause **Unknown**.
6. **Grass beyond x16**: x32 costs 3.94 ms of render thread (`upd-foliage`, page building on the render thread) and +3.4 GPU, and the pages
   cannot keep up with the flight (missing within 1 500 units in 181 of 300 frames, nearest 0). x16 (+1.4 ms GPU, +0.3 ms CPU) meets the
   1 px target for median blades; the tallest kinds want x37-x45.
7. **Impostor count**: 430 000-611 000 quads a frame of one mesh (`Spore02[Blister]`), about 1.5-2 ms at 3.3 ns each; at that distance
   most are well under a pixel (**Unknown** how many: no projected-size count, F4).
8. **Streaming**: settling CPX lays out 3 239-3 895 zones (10.4-11.6 M instances) in 160-204 s (about 20 zones a second); in flight the
   layouts fall behind (forest CPX paced: 221 of 300 frames without the whole layout within the 80 000 near reach, nearest 74 671; grass
   pages missing in 246 frames, nearest 6 188). Pop-in at the edge of a range that is not on screen (Hub, Stack still views: 0).
9. **Pre-frame GPU** (uploads, cull and grass kernels): 0.24 → 1.1 ms at CPX; the foliage cull kernels 65 µs a view at large 96k
   (`MEITOU_FOLIAGE_TIMING=1`, `fL96k_timing`).

**Not bottlenecks** (**Observed**): shadow distance (above); VRAM (peak 6.8 GB, 59 %, at CPX with a 2 GB impostor budget and shadows 15 000;
owners at CPX: object textures 1.4 GB, terrain 1.1 GB, foliage textures 1.1 GB, impostor atlases 0.9 GB, object meshes 0.67 GB; on an 8 GB
card the guard would act); fill rate and fragment shading (section 3); the cull scratch (0 refused); command recording (section 6.1).

### 6.1 Wave 4: is multithreaded recording worth keeping on?

`MEITOU_RECORD_THREADS` 0 / 1 / 2, three interleaved runs each (two each at CPX), paced; render-thread ms p50 (frame p50):

| View, preset | mode 0 | mode 1 | mode 2 (default) |
| --- | --- | --- | --- |
| forest flight, D | 1.96 / 1.91 / 1.93 (6.6 / 6.4 / 6.5) | 2.22 / 2.36 / 2.27 (6.8 / 6.9 / 6.8) | 2.38 / 2.24 / 2.34 (6.8 / 6.6 / 6.9) |
| Hub still, D | 1.02 / 1.01 / 1.01 | 1.17 / 1.15 / 1.16 | 1.22 / 1.23 / 1.26 |
| forest flight, C40 | 2.88 / 2.84 / 2.98 (10.3 / 10.3 / 10.4) | 3.23 / 3.19 / 3.11 (10.5 / 10.6 / 10.6) | 3.26 / 3.28 / 3.16 (10.7 / 10.6 / 10.7) |
| Hub still, C40 | 2.15 / 2.12 / 2.15 | 2.28 / 2.24 / 2.30 | 2.44 / 2.36 / 2.37 |
| forest flight, CPX imp2g | 7.77 / 7.47 (27.4 / 27.1) | | 8.15 / 8.00 (27.7 / 27.5) |

(`W_*` logs, 40 runs.) Every mode-0 run has a lower render-thread time than every mode-1 and mode-2 run of the same set, by 0.15-0.45 ms; the frame follows by 0.1-0.4 ms. The job threads'
summed time stays at 0.4-0.7 ms even at CPX: the render thread's growth at long ranges is preparation (work lists, the objects' CPU cull,
layouts), not recording, so there is nothing for the recording threads to take. **Recommendation**: make mode 0 the default; keep the switch
and the secondary-buffer path (6.5) for when Prepare itself is split across threads. Pixels are the same in every mode (6.6).

## 7. Proposed fixes, with expected gains

Estimates are from the measured rates and counts (**Unknown** until built); the order is by gain at the target.

| # | Fix | Tied to | Expected gain at CPX |
| --- | --- | --- | --- |
| F1 | **Done (7.1).** **Impostor residency by distance**: a budget that follows the card (the object caches' `HighWaterShare` pattern, about 8 % of the driver's budget ≈ 900 MB here) and far atlases held at lower mips (a tree at 30 000 needs 16-32 px frames; the object textures' mip streaming of 8.14 does exactly this), so far trees never fall back to meshes and the LRU stops thrashing | bottleneck 1 | Hub −22 ms and forest −2.1 (measured with a 2048 MB budget); Stack likely similar to the Hub (the same refused trees and junk, not measured); VRAM +0.2-0.65 GB |
| F2 | **LOD for TERRAIN-mode rocks**: decimated levels made at load (quadric simplification, clean-room) chosen per instance in the cull kernel by projected size, the way the kernel already splits mesh / impostor; or depth-and-normal impostors shaded with the biome textures of the instance in the terrain shader | bottleneck 2 | rock triangles ÷10-÷30 beyond ~8 000: Hub 10.5 → ~1 ms, forest 5.6 → ~0.6 ms (−9.5 / −5) |
| F3 | **Done (7.1).** **Impostors for the smaller meshes** (a 32 px frame class under radius 48) and for medium meshes in general | bottleneck 3 | Hub −4 to −5 ms, forest medium 80k −3.5 ms; VRAM + a few MB an atlas at 32 px |
| F4 | **The 1 px rule itself in the cull kernel**: drop an instance whose `2 r s f / d` is under ~1 px (sphere from the record, `f` from the view), instead of fixed class ranges; ranges become "the world" and the cost follows what is visible | bottlenecks 2, 3, 7 | impostor quads (`Spore02[Blister]` 430-611 k) −1 to −1.5 ms; also trims the far rocks and meshes before F2/F3 (**Unknown** how much: count first) |
| F5 | **Far objects**: impostors or decimated far levels for buildings past their last Ogre LOD (their textures already stream, 8.14), and the CPU cull per zone (zone bound first, cached per frame for all views); not a per-instance GPU cull (5.6.3: two instances per work item) | bottleneck 4 | objects GPU 4.0 → ~1 ms (−3), render thread 1.3 → ~0.5 ms |
| F6 | **One foliage work list per frame**: build the chunk lists per zone once at accept (they depend on the eye only through the range test, which the kernel can do), share them between the slices, the reflection and the cascades, and let the kernel reject far zones | bottleneck 5 | render thread −2.5 to −3.5 ms at CPX (3.8 ms now); then find the shadow spikes |
| F7 | **Grass far field**: blades to ~16 000 (x16), beyond that the grass colour in the terrain (blades are sub-pixel there); page building wholly on the workers with batched uploads | bottleneck 6 | keeps the target at +1.4 ms GPU; avoids the x32 render-thread 3.9 ms and the missing near pages |
| F8 | **Far layouts cached**: the large-only far tier (`Tier.Far` / `Tier.Meshes`, 8.11) stored per zone on disk after the first layout, so a long range settles in seconds and keeps up in flight | bottleneck 8 | settle 160-204 s → seconds (**Unknown**); flight backlog 221 → ~0 frames |
| F9 | `MEITOU_RECORD_THREADS` default 0 | 6.1 | −0.15 to −0.45 ms render thread at every range |

Together (rough, **Unknown**): Hub CPX 45.8 → 23.7 (F1) → ~14 (F2) → ~10 (F3) → ~7 (F5) → ~6 ms (F4); forest CPX 21.8 → ~19.7 → ~14.7 →
~12 → ~9 → ~8 ms; the render thread 8 → ~4.5 ms (F5, F6, F9). That is everything in the world to 1 px within about 6-8 ms of GPU on this card.

Not proposed, with the reason (**Observed**): HiZ occlusion (open vistas and long sight lines; the cost is primitives already behind
terrain or not, **Unknown** how many are hidden: a count would decide it); compute-rasterised grass (grass is 0.3-1.4 ms); cascade range
scaling (the cascades do not grow with the ranges); GPU-driven objects (5.6.3).

### 7.1 F1 and F3 done (2026-10-07, branch `impostors-budget-small`)

The budget now follows the card, atlases hold only the mips their nearest instance needs, a plan decides residency nearest first, and meshes under radius 48 have a
small impostor class. Rule, numbers per card size, design and gates: [impostors.md](impostors.md) sections 10 and 11; code notes [renderer-native.md](renderer-native.md) 8.16.

**Measured** (**Observed**, RTX 4070, CPX preset, `--fly-pipelined` GPU frame p50, three interleaved runs against the viewer built from `0a3614a`):

| Run | master | F1 only (`MEITOU_IMPOSTOR_SMALL=0`) | F1 + F3 |
| --- | --- | --- | --- |
| Hub CPX | 46.6, 46.4, 46.4 ms | 23.7 | 20.6, 20.7, 20.6 |
| forest CPX | 21.6, 21.4, 21.4 | 19.6 | 15.2, 15.1, 15.2 |
| Stack CPX (one pair) | 45.6 | | 15.7 |
| medium 80 000 only (forest) | 6.6, 6.7, 6.6 | | 4.0, 4.1, 3.9 |

- Foliage row of the pass table: Hub 39.3 to 13.45 ms, forest 14.1 to 7.97, Stack 39.55 to 9.68. Colour mesh triangles: Hub 47.9 M to 22 k, forest 11.7 M to 0.30 M.
- Impostor VRAM (allocator): Hub 219 to 413 MB (311 atlases, 100 refined; the same run with far mips off holds 874 MB), forest 212 to 494 MB (293 atlases, 154 refined);
  whole process peak at the Hub 5 851 to 5 981 MB. Atlas loads in 300 frames: Hub 3 437-3 593 to 311, forest about 2 250 to 293 (the thrash is gone).
- F1 alone gives -22.7 ms at the Hub and -1.8 at the forest (the estimate was -22.1 and -2.1); F3 adds -3.1 and -4.4 (estimate -4 to -5; forest medium 80 000 alone: -2.6).
- Render thread (paced, cpu-only p50): forest CPX 8.3, 8.3, 8.2 to 8.6, 9.3, 8.6 ms (+0.3 to +1.0), Hub 7.7 to 7.0. Paced frame p50 forest 30.1, 30.0, 30.0 to 23.8, 24.5, 24.1; Hub 54.4 to 27.8.
- Defaults (11.4 GB card): unchanged within noise (forest pipelined 2.6 against 2.5-2.6 ms, paced 6.7 against 6.7-7.0, Hub 2.7-2.8 against 2.7); the default forest flight holds 117 atlases / 367 MB
  against 66 / 204 MB.
- Small cards (`MEITOU_VRAM_BUDGET_MB`): at 4 096 the limit is 328 MB and the plan fits everything (peak 88% of the budget against 84%); at 3 584 the guard's pressure applies (x0.60, limit 215 MB);
  at 2 048 both builds cut every range to x0.15 and draw no impostors (and both abort at the default 95% watchdog; the run needed `MEITOU_VRAM_KILL=0.995`). With `MEITOU_FORCE_INTEGRATED=1` at 4 096 the limit is 205 MB,
  73 admitted, 10 of them coarser than needed. Hub CPX under `--impostor-budget 128` or `256`: 20.6 ms each, against master's 46 ms at 192.
- Pictures: Faithful ten views 0 px against `C:\Temp\base-87c7857\faithful`; `--faithful impostors` 0 px against master; Meitou defaults nine of ten views 0 px, forest 13:00 and 02:00 differ in
  a few small spots (the small impostors; 0 px with them off). `MEITOU_VK_VALIDATION=sync` 0 errors.

**F2 done (2026-10-07, [impostors.md](impostors.md) section 13)**: TERRAIN-mode rocks get billboards baked with the terrain material, per biome. Hub fly benchmark (unpaced, pipelined): colour rock triangles 599 k to 15 k per frame,
GPU frame mean 2.61 to 2.24 ms (**Observed**; this flight's mean, not the 5.6-10.5 ms long-range views the estimate was for), VRAM peak 4.10 to 4.32 GB. Faithful 0 px, validation 0 errors (**Verified**).

Still open after this (**Observed**): ~~TERRAIN-mode rocks (F2) are untouched (Hub 40.8 M triangles, 5.6-10.5 ms), now the largest single item~~ (done, above); the CPU is 0.3-1.0 ms dearer at CPX; the
integrated-GPU heap behaviour is **Unknown** (no such card here).

## 8. Gate for the instrumentation

**Verified** (commit `ace4770`): `dotnet build -c Release` 0 warnings; `dotnet test -c Release` with `KENSHI_PATH` set: 492 passed,
0 failed, 0 skipped; `tools/scripts/parity.sh … --faithful all`, ten views against `C:\Temp\base-87c7857\faithful`: max 0 in all ten
(`C:\Temp\agent-R\gate\faithful`). The tally copies only when `MEITOU_FOLIAGE_TRIS=1`, the pre-frame stamp only with `MEITOU_PASS_STATS=1`,
the part range only with `MEITOU_OBJECT_PART_RANGE`; the object counters and the `PassMeter` change touch no command.

## 9. Open

- **Unknown**: how much of the CPX work is below 1 px (F4 needs that count first), and how much is hidden behind terrain (HiZ).
- **Unknown**: the cause of the 10-14.5 ms `shadows` frames at CPX.
- **Unknown**: for each "no impostor class" mesh, whether its radius is under 48 or its source failed.
- **Unknown**: other cards. Every number is one RTX 4070; on an 8 GB card the guard would cut the ranges before the GPU limit.

## 10. Terrain and foliage pixel cost at the Meitou defaults (2026-10-08)

*In short: at the Meitou defaults the terrain and the impostors are most of the GPU frame, and both are pixel cost (half the render
scale cuts them about 2.5-3x; fewer terrain triangles change nothing). Two changes: terrain patches drawn nearest first (exact,
0 px), and impostors without the shadow receiver's blocker search (Meitou only). The frame drops 3.92 → 3.28 ms at the Blister Hill
overview and 6.52 → 5.62 ms at ground level there. What is left in the terrain is mostly not the layer material.*

**Setup** (**Observed**): RTX 4070, 1920 x 1080, Release, `--fly-pipelined --fly-benchmark 300 --time 13`, Meitou defaults, GPU idle
(an earlier set while another viewer held the GPU gave terrain 3.2-4.2 ms and is discarded). "Okran's Pride" is a region, not a town;
the views are in it at Blister Hill: overview `--town "Blister Hill" --fly-speed 0` and ground level the same with `--distance 1500
--pitch 12`. Numbers are the pass table's GPU column for the main view; repeated runs agree within 0.02 ms unless stated. Experiments
patched the shader text through a temporary environment hook (not in the repository).

**Where the time goes** (master, overview / ground level): frame 3.92 / 6.52, terrain 1.29 / 1.66, foliage meshes and impostors
1.26 / 1.49 (about 55 500 impostor quads and 97 k mesh triangles a frame; Faithful draws 0.31 ms of meshes there), objects 0.40 / 1.67, post 0.39.

**Terrain** (overview after the sort, 1.13-1.16 ms; differences are what each change saves):

| Experiment | Terrain ms | Reading |
| --- | ---: | --- |
| render scale 0.5 | 0.45 | about 70 % of it follows pixels |
| `--terrain-error` 8 / 32 / 64 (2.14 M / 0.46 M / 0.46 M triangles, ground level) | 1.22 / 1.17 / 1.16 | geometry is not the cost on this card |
| `--material-distance` 1000 / 2000 / 4000 / 8000 / 16000 (ground level, full 1.16) | 0.73 / 0.84 / 1.06 / 1.09 / 1.12 | the material beyond 1000 units costs about 0.43 ms (the whole material is nearer the "no biome surfaces" row below) |
| normal from screen derivatives instead of 4 height lookups (up to 8 fetches) | 0.88-0.93 | the height-field normal costs about 0.25 |
| no `kenshiLight` | 0.83 | lighting about 0.3, of which shadows (receiver off) about 0.12 |
| no normal maps / normal maps only within 3000 | 0.80 / 1.09 | |
| no biome surfaces at all / one biome slot | 0.65 / 1.13 | extra biome slots cost little here |
| no cliff layer | 1.06 | |
| biome parameter fetches doubled (same values) | 1.16 | parameter fetches are free (cached) |
| `textureGrad` replaced by implicit derivatives | 1.16 | explicit gradients cost nothing |
| no haze | 1.12 | |
| terrain drawn after objects and foliage | moves the cost to foliage, frame unchanged | the terrain is not shaded much under objects |
| patches nearest first | 1.13 (from 1.29); ground level 1.16 (from 1.66) | **taken** |

A cached, pre-composed material (a clipmap of albedo, normal and gloss around the eye, the owner's idea) could replace only the layer
part beyond the distance where a cache's texels are fine enough: at most about 0.3 ms of 1.16 at ground level beyond 2000 units (the
`--material-distance` rows), and nothing near the eye, where the layers' texel density (about 0.05 units) cannot be cached. The
per-pixel height normal (about 0.25), the lighting and the shadows (about 0.3) stay either way.

**Impostors**: [impostors.md](impostors.md) section 14 (the receiver's blocker search was 0.36 of the 1.16 ms).

**Result** (three interleaved runs each, master `6d7b258` against the branch):

| View | master frame / terrain / foliage | branch frame / terrain / foliage |
| --- | --- | --- |
| overview | 3.90-3.93 / 1.29-1.30 / 1.32-1.34 | 3.27-3.28 / 1.13 / 0.85 |
| ground level | 6.37-6.76 / 1.63-1.72 / 1.53-1.64 | 5.60-5.66 / 1.16-1.17 / 1.13-1.15 |

**Gate** (**Verified**): `--faithful all` ten parity views 0 px against master; Meitou ten views: the five night views 0 px (max 1 at the
Hub), day views mean 0.001-0.09, at most 0.094 % of pixels over 12 (the Hub; max 81), all on far impostor trees' shadow edges.

**Integrated GPUs** (**Unknown**, nothing measured on one): an iGPU about 10-15x slower than this card gives 12-25 ms of terrain at
1080p, as reported (25 ms). On this card the cost is pixels and the triangle count did not matter; an integrated GPU has weaker
geometry throughput relative to fill, so both the render scale (an upscaler at 0.67 shades 44 % of the pixels) and `--terrain-error`
need measuring there.

Scripts: `C:\Temp\perf\run.sh <name> <view> <frames> [options]` (views `okran` = the Blister Hill overview, `low` = ground level;
`EXE=` picks another viewer), `C:\Temp\perf\rows.sh <logs>` prints the main view's GPU rows; logs in `C:\Temp\perf\logs`, pictures in
`C:\Temp\perf\gate`.
