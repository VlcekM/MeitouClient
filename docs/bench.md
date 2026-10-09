# Benchmarks, start-up time and JIT mode

How to measure the world view of `meitou-viewer`: the benchmark harness, where start-up time goes and the load caches, and the JIT switches. Options are in [viewer.md](viewer.md).

## Benchmark harness

One run answers "what does switch X cost or save, and does it change the picture" on a fixed camera, with numbers that can be trusted on a
shared GPU (2026-10-09; `WorldApp.Bench.cs`, `src/Meitou.Rendering/Bench/`, `BenchHarnessTests`). The runs are offscreen, as `--screenshot`:
the world loads (cold start, about 35 s; about 20 s with a warm foliage layout cache), settles as the screenshot path does, then the harness draws frames the way the interactive viewer does: the next frame is recorded while the GPU still draws the one before (two in flight, no vsync).

```
meitou-viewer --view swamp --ab shadows --ab-period 64 --bench-frames 600     # A/B of one switch
meitou-viewer --view dust --ab particles                                      # per-frame alternation (the default period 1)
meitou-viewer --view swamp --bench-motion turn --bench-frames 600             # one configuration, camera turning
meitou-viewer --view hub --bench-frames 600 --bench-out master.json           # run each build, then:
meitou-viewer --bench-compare master.json perf.json
```

- **Named views** (`--view <name>`, `NamedViews.Table`, the one table; expanded in place into options, so options after it override it):
  `swamp` = `--world --town Shark --radius 2 --distance 2000 --pitch 30 --yaw 300 --time 12 --bench-motion orbit` (Shark from above as in play, orbiting the town; until 2026-10-09 it was `--distance 3000 --pitch 10` and still, which put the eye between two big rocks that filled most of the picture, so numbers from before are not comparable); `swamp-rain` = the same with
  `--weather "swamp rain no wind"` (the swamp's rain weather, [formats/weather.md](formats/weather.md); with the default seed 1 the scheduler
  already picks that weather at day 0 in `swamp`, so the two currently draw the same, and `swamp-rain` is the one that stays rainy if the scheduler
  changes); `dust` = `--world --town Heft --radius 2 --distance 3000 --pitch 10 --yaw 300 --weather "Dust Storm Approach" --time 12`;
  `hub` = `--world --town "The Hub" --radius 2 --distance 9000 --pitch 10 --yaw 300`. Size and upscaler are the usual options (`--upscaler`; `--size`, default 1280x720 for a bench run, 1280x960 otherwise).
- **A/B** (`--ab <name>`, `--bench-frames <n>` total measured frames over both sides, default 600): side A is Meitou / on, side B Faithful / off.
  The registry `AbToggles` (`Register(name, get, set)`; `--ab` with an unknown name lists them) holds the Faithful / Meitou switches under their ids (`ao`, `dither`, `haze`,
  `aa`, `shadows`, `range`, `impostors`, `reach`, `water`, `particles`, `lod`; `aa` reallocates the targets, so it is for long periods only), and `occlusion`
  (the Hi-Z foliage cull), `fog-cull`, `fog-volumes`, `shadow-pass`, `foliage-draw`, `grass` (the blades only), `foliage-meshes` (meshes and impostors, not the TERRAIN-mode rocks), `lod-far` (see Measured, swamp target view), `objects-draw`, `water-draw`, `reflections`. A new switch that can flip at run time is one `Register` call.
  Sides alternate every frame, or every `--ab-period k` frames; with k > 1 the first `--ab-drop n` frames after each switch (default min(k/4, 16)) are left out.
  **A feature with history needs a period longer than its history**: the Meitou shadows' cached cascades redraw on a schedule of up to 64 frames, so `--ab shadows` needs
  `--ab-period 64` or more (at period 1 each side sees the other's cache state). Before the numbers count, both sides run four warm-up blocks (pipelines, caches).
  The old measurement env vars still work (`MEITOU_OCC_ALT`, `MEITOU_LOD_ALT`, `MEITOU_SHADOW_CADENCE`, and the fly benchmark's `MEITOU_FLY_TURN`, `MEITOU_BENCH_ORBIT`); `--ab occlusion`, `--ab lod`
  and `--bench-motion turn|orbit` do the same without them (`MEITOU_SHADOW_CADENCE` sets the schedule being measured, not a side, so it stays).
- **Output**: a table per kind and `--bench-out <file>` JSON (default `%TEMP%\meitou-bench-<view>-<ab|single>-<time>.json`): the frame row (the time between one frame's submission and the next's, so with frames in flight it is the real frame time and `1000 / mean` is the fps; an `fps` line under it gives 1000 / mean, 1000 / median and 1000 / p99), GPU ms per stage (timestamps,
  `FrameProfiler.OnGpuFrame`, read a few frames late and matched to their frame by a tag) with `other` and `total`, the post-processing sections (`PostProcess.OnCost`), and the render thread's ms per stage
  (`cpu:*`; `gpu-wait` is the wait for the GPU, not work, and is left out of `cpu:total`; recording jobs on other threads are not in it), each with mean, median, p95, p99, max for each side. A/B adds the
  difference A - B with its 95% interval: frames are grouped in blocks (one frame at period 1), each A block's mean is paired with the next B block's, and the interval is 1.96 standard errors of those pair
  differences, so slow drifts of a shared GPU cancel; `*` marks an interval that excludes zero with a difference of at least 0.005 ms; `med` is the median of the pair differences. Also per side the main view's
  counts per frame (terrain, objects incl. the reflection pass, foliage triangles and draws, grass blades, characters), taken in 36 extra frames after the timing so the read-backs cost nothing there.
- **Frames in flight and the attribution of the stage times** (2026-10-09): the harness no longer waits for the GPU after each frame (`--bench-serial` brings that back; the JSON says which mode ran in `frameMode`). Each frame carries its number (the tag) through `FrameProfiler` and `PostProcess`, whose timestamps are read when the frame ring comes round (two frames later, at the begin of the slot's next frame) and handed back with the tag, so every GPU and post number lands on its own frame and its A/B side; the end of the run draws three empty frames to bring the last ones in. The GPU `total` is now the frame's own first-to-last stamp (before, it was the context's latest completed frame, another frame once frames overlap; the `other` row is that total less the staged parts). At A/B period 1 the *frame* row (and its paired difference) is smeared: with frames overlapping, one frame's interval is set by the GPU time of the side before it as much as its own, so use a longer period for frame-time differences of a switch (stage rows are unaffected). `cpu:gpu-wait` is now about 0 in the default mode (the wait for a free frame comes before the profiler starts a frame, as in the viewer). Metadata: view, command line,
  `git describe` (`-dirty` when the tree has changes), GPU name, resolution, upscaler and render scale, motion, `MEITOU_*` variables set.
- **One configuration** (`--bench-frames` without `--ab`): the same JSON with side A only; `--bench-compare a.json b.json` prints the first side of each next to each other with B - A and an interval from the spreads
  (frames treated as independent, so it is optimistic against drift; it warns when the view, resolution, upscaler, motion or GPU differ). For master against a branch, run each build with the same options.
- **Picture** (A/B): after the timing, one still of each side from the start camera with the frame clock frozen and the same number of frames drawn (at least 70) for each, so the upscaler's jitter phase and the
  caches agree. Reported: pixels whose largest channel difference reaches 1, 4 and 12 of 255, the mean difference per channel and the maximum; `<out>-A.png`, `-B.png` and `-diff.png` (a heat map: black equal, blue at 1/255 through
  cyan, yellow, red to white at 32/255 and over). Weather particles are not frozen exactly (rain streaks differ a little between the two stills), so for a pure picture comparison add `--no-particles`.
- **Motion** (`--bench-motion still|orbit|fly|turn`, default still; `CameraMotion`, shared with `--fly-benchmark`): `orbit` turns round the target by `--bench-orbit` mouse pixels a frame (default 3, 0.005 rad each), `fly` flies the
  circle of `--fly-radius` / `--fly-speed` per frame (streaming then loads while it is measured), `turn` rotates about the eye `--bench-turn` degrees a frame (default 0.5, the camera-rotation case for the shadows and the reflection).
  The p95, p99 and max columns are what to read for spikes. The motion runs through the warm-up too, and the picture is taken back at the start camera.
- **GPU lock**: while measuring (not while loading) a run holds `%TEMP%\meitou-gpu.lock` (the file is held open without write sharing and holds the PID, so a crashed holder frees it by itself; a PID left in
  it is reported as a stale lock when the next run takes it over); other bench runs queue and print who they wait for; the wait is printed (`gpu-lock waited`) and kept in the JSON. `--no-gpu-lock` skips it. Programs that do not take the lock
  (the interactive viewer, other tools) still disturb the numbers: keep them closed, or read the paired difference, which is built to survive that.
- **Measured** (2026-10-09, RTX 4070, 1280x960, TAA, master e9dc2c7 plus the harness; each run took 44-51 s end to end including the cold load):
  `--view swamp --ab shadows --ab-period 64`: GPU total 3.59 ms (Meitou) against 4.29 (CSM), -0.69 +-0.04; the shadows stage 0.29 against 1.05 ms; 243 846 pixels differ by 1/255 or more, 15 054 by 12 or more.
  `--view dust --ab particles`: GPU total 3.05 ms (low resolution) against 40.1 ms (full size), -37.1 +-0.05; 14 534 pixels differ by 12 or more.
  `--view swamp --bench-motion turn --bench-frames 600`: GPU total mean 4.19, p95 5.40, p99 6.25, max 6.35; frame wall time mean 6.81, p99 9.06.
- **Measured, frames in flight** (2026-10-09, RTX 4070, `--view swamp --size 2560x1440 --upscaler dlss`, 600 frames, still camera): frame mean 8.07 ms (median 8.05, p95 9.00, p99 9.64, max 11.16) = 123.9 fps (124.3 at the median frame, 103.7 at p99); GPU total mean 8.06 ms, render thread 2.31 ms, so the run is GPU-bound and the frame time is the GPU time, as expected (max of the two). With `--bench-serial` the same view gives frame 10.30 ms (97.1 fps) against GPU 7.84 and CPU 2.27: the old way added the two (about 10.2 before). Both runs share one stage table: foliage 2.1, post 2.2, terrain 1.5, objects 0.7 ms.
- **Measured, swamp target view** (**Observed**, 2026-10-09, RTX 4070, `--view swamp` (orbiting) `--size 1920x1080 --upscaler dlss --render-scale native` with `MEITOU_STREAMLINE_PATH` set, 768 frames, `--ab-period 16` (shadows 64)): GPU total 7.56 ms mean (132 fps), p99 11.06; stages foliage 1.83, terrain 1.26, post 1.26 (DLSS 0.76), reflection 0.98, objects 0.78, shadows 0.62 mean but 3.40 at p99 (the cached cascades redrawing as the camera orbits: the spikes). What each switch saves (A - B, +-95%): objects-draw 1.87 +-0.09 (all passes), foliage-meshes 1.56 +-0.06, water-draw 1.29 +-0.08, range 1.10 +-0.09, reflections 0.91 +-0.07, grass 0.38 +-0.07, shadows 0.13 +-0.21 (mean; the Meitou cascades), occlusion -0.02 +-0.09 (nothing to hide looking down); impostors save 1.59 and lod 0.48. `--ab lod-far` (the generated rock levels' rougher levels and distance-growing tolerance, on by default since 2026-10-09; **Observed**, same view, 768 frames, period 16): total -0.40 +-0.17 ms (7.52 against 7.92), foliage -0.26 +-0.05, shadows -0.07 +-0.05; the picture does not show it (0 pixels over 12; the swamp's fog hides the far plants), a clear camera shows twig edges (mean 0.1-0.3, see formats/foliage.md). **DLSS silently falls back to TAA without `MEITOU_STREAMLINE_PATH`** while the header still says dlss: check the log for `using TAA`. Before 2026-10-09 an `--ab` run with a moving camera crashed in the still pictures (the settles back at the start uploaded with no frame open).
- **Known gaps**: a GPU stage's time is the gap between its timestamps, so work the driver defers lands in the stage after it (full-size particles show up as `upscale` in the post sections; the totals are right);
  the picture diff is not exact with weather particles (above); the frame row is the submission-to-submission interval, so a frame that stalls the CPU shows in it but a hidden GPU stall does not (that is the real frame time); the interactive viewer's F11 number was not compared in this session (**Unknown**: its window size and present mode were not matched); one view and one resolution per run;
  `--ab` needs the switch to be flippable at run time without a reload (all listed ones are).
## Start-up time and the load caches

Where a start spends its time (2026-10-09, `--view swamp --screenshot`, RTX 4070, 12 cores; stage times are the viewer's own log lines):

| Stage | Cold (no foliage cache) | Warm (foliage layout cache) |
|---|---|---|
| Whole start to the saved picture (wall clock) | 36.0 s (a run without the cache, 36.0 s again on the run that filled it) | 12.2 s |
| Game data, terrain, objects ("loaded in") | 1.8 s | 1.8 s |
| GPU set-up (biome textures 2.2 s, rivers 0.7 s, water; cumulative) | 4.0 s | 4.0 s |
| Terrain streamer settle | 1.1 s | 1.1 s |
| Objects settle | 0.6 s | 0.6 s |
| Foliage settle | 25.2 s | 1.4 s |
| Rest (runtime start, device, first frames, read-back, PNG) | about 3 s | about 3 s |

- The stages run one after the other on the main thread; only the foliage settle overlaps work (three worker threads). In the cold foliage settle the zone layouts are the critical
  path: the workers spent 72.8 s of placement (3 workers: 24 s) and the last layout finished at 25.2 s, while grass pages ended at 1.0 s and mesh decode, textures and generated levels
  kept up with the layouts (last busy at 24.7 s, 0 s and 23.7 s). Warm, everything is done by 1.4 s: 414 hits loaded in 311 ms of worker time (89 MB).
- Two cold runs in a row differ by under 2% in every stage (game data 418 / 409 ms, biomes 2258 / 2177 ms), so the OS file cache is not what matters here (the game files were already
  cached; a true first start after a reboot was not measured: **Unknown**).
- **Foliage layout cache**: [formats/foliage.md](formats/foliage.md#layout-cache-in-the-viewer-meitou-2026-10-09). The key is a hash of the layout code's source (Meitou.Data and Meitou.Core), not module ids, so a new worktree or a rebuild with other changes keeps its hits: in a worktree that had never run, the first swamp bench run (1440p DLSS) took 42 s with 0 hits, 414 misses and 77.6 s of placement; the next, after a rebuild with an edit in Meitou.Rendering, took 20 s with 414 hits (260 ms to load). Before, every new build path started cold (46 s, 0 hits, 74 s of placement in this worktree).  `--no-load-cache` (or `MEITOU_NO_LOAD_CACHE=1`) bypasses it (reads and writes); the log shows
  `foliage   layout cache <hits> hits, <misses> misses ...` and the settle's `busy until` line.
- Next biggest item: GPU set-up, 4.0 s of the 12.2 s warm start: 2.2 s loading the 133 biome texture pairs (1064 MB of BC3 / BC1 read and uploaded in one go before anything else), then the
  river flow map bake (0.7 s) and water. Then the 3 s fixed rest, the terrain streamer (1.1 s) and the game data and objects load (1.8 s).

## JIT mode: `--tiered-jit`, `--pgo`

The viewer and the game ship with tiered compilation and tiered PGO off (every method compiled once, fully optimised; DECISIONS 11, 19). `--tiered-jit` turns tiering on, `--pgo` tiering plus dynamic PGO (PGO needs tiering), `--no-tiered-jit` / `--no-pgo` force them off. The JIT mode is fixed when a process starts, so the switches start the same executable again with `DOTNET_TieredCompilation` / `DOTNET_TieredPGO` set (they override the runtimeconfig: **Observed** 2026-10-09 with a test program whose methods tier up only with the variables) and return its exit code; the log says `jit       tiered compilation on, PGO on (relaunched)` (`JitSwitches`).

**Observed** 2026-10-09, `--view swamp --size 2560x1440 --upscaler dlss`, 3000 frames, RTX 4070: GPU-bound, so fps is the same in every mode (about 123). Render-thread CPU median: tiering off 2.22 ms, `--pgo` 1.82 ms (still) and 4.02 / 3.09 ms (flying), but plain tiered PGO had a 23 ms frame from a recompile while standing still. ReadyToRun (precompiled) code with tiering off is never replaced by the JIT and ran the render thread at 3.90 ms against 2.33 ms; it did not shorten the start either (40 s per run in every mode), so ReadyToRun only makes sense together with tiering.

