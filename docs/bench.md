# Benchmarking

The world viewer's offscreen harness measures frame cost and visual effects of runtime switches.
Implementation: `tools/Meitou.ModelViewer/BenchSession.cs`, `src/Meitou.Game/Rendering/Bench/`, `BenchHarnessTests`.
Options: [viewer.md](viewer.md).

## Runs

```text
meitou-viewer --view swamp --ab shadows --ab-period 64 --bench-frames 600
meitou-viewer --view dust --ab particles
meitou-viewer --view swamp --bench-motion turn --bench-frames 600
meitou-viewer --view hub --bench-frames 600 --bench-out baseline.json
meitou-viewer --bench-compare baseline.json candidate.json
```

The world loads and settles before measurement. Two frames are in flight without VSync;
recording overlaps GPU execution. `--bench-serial` waits between frames and does not represent
normal pipelined throughput.

`--view` expands a `NamedViews.Table` preset; later options override it. Presets include
`swamp`, `swamp-rain`, `swamp-low`, `dust` and `hub`. Specify resolution, upscaler, render scale,
weather, time, seed and camera when comparing builds. Rain presets avoid dependence on initial
scheduled weather. A camera code preserves an exact view.

## A/B methodology

`--ab <name>` alternates a registered switch: A on/Meitou, B off/Faithful.
`--bench-frames` is the measured total across both sides (default 600); `--ab-period` sets frames
per side. Long periods suit target reallocations and shadow cadences. An unknown name lists
`AbToggles`; consult the registry for supported names. `--sway-step` advances grass motion;
offscreen sway otherwise stays fixed.

Compare A-minus-B and confidence intervals, means, medians, p95/p99 and stage costs. Differences
within uncertainty are inconclusive. GPU clocks, other programs, motion, cached cascades,
warm-up and streaming affect results. Alternate builds and repeat comparisons without a runtime
switch. Record conditions beside retained results. One view does not establish a general speedup.

Check the log's actual upscaler: DLSS falls back to FSR, then TAA, without its runtime (Streamline on Windows; libmeitou_ngx.so and the DLSS model on Linux) or an NVIDIA card. Two settled
baseline stills establish temporal noise before comparison. Use A/B images and
`meitou-tools image-diff` to inspect changed pixels.

Throughput is approximately the slower of overlapping CPU/GPU work, rather than their sum.
A faster GPU stage may leave a CPU-bound frame unchanged. Parallel CPU stage sums are not
elapsed time. Evaluate cached-shadow redraws by tail latency as well as mean cost.

The frame profiler splits the tail into `ui` (overlays/UI), `submit` (window-copy recording and frame
submission), and `driver-wait` (present call, including queue-lock waits; CPU only). CPU values are
elapsed time, not CPU utilisation; submission may also block inside the driver. GPU `ui` ends before
the window copy; GPU `submit` measures that copy and the command tail, not the CPU submission call.
Offscreen frames have no present wait. Render-thread benchmark totals exclude `driver-wait`.
In the game the head is split too: `host` (the host's frame work before the world draw), `blood` (the host's blood marks and pools, then
the terrain decal layers redrawn), so `upd-terrain` and `gi` hold only their own work.
`sim` (game only) is the simulation ticks run on the render thread before the frame's clock starts (`FrameProfiler.Outside`); it counts in the total. The tick run ahead during the world draw ([simulation](simulation.md#tick-ahead)) is not in it: its wall time is the `tick ahead` row.
It opens (not plotted) to the world ticks per frame and the mean tick's ms, the tick phases (`World.Profile`, `TickProfile`; on while F12 shows; a
parallel phase is wall time up to its barrier), `outside ticks` (control ticks, host orders), the ten costliest systems (all their spans: serial calls, parallel phases, effect applies), then the
parts systems mark inside their own time (`TickProfile.Part`; Ragdoll's `rag setup`, `rag step` (the physics), `rag back`, `rag sweep`) and, per parallel
phase, `<phase> cpu` (its jobs' time summed over the threads) and `<phase> slow` (each system's slowest job, summed): wall near `slow` is one job holding up the rest.
F12's legend groups stages (update, pre-frame, scene, present) into one summed row and line each; a click on a `+` row opens it (plotting
its stages instead), `-` closes it.
The orange line on both charts and the header's `frame` (mean of the last 30, fps, max) is the whole frame's wall time, one `BeginFrame` to the next:
the stage total plus what no stage holds (the host's work before the frame, the waits for a free frame). `upd-chars` opens to its render-thread parts (`StageClock.Part`; not plotted; the pass meter shows them as subs): `fill` (the host's draw list),
`textures` (streaming pump), `gather` (characters in range, appearances), `pose` (the parallel posing and part placement), `materials`,
`bolts`, `portraits`, `hover` (hover masks) and `icons` (item and building icons). The offscreen bench reports them as `upd-chars/<part>`.

## Geometry and pass diagnostics

`--bench-tris` reports pass triangle counts and projected size distributions. Inspect main-view,
reflection and shadow work separately; geometry is repeated across views. Screen-size histograms
identify subpixel work that draw counts conceal.

`MEITOU_PASS_STATS=1` enables pass timing; `MEITOU_PASS_STATS_SKIP` excludes warm-up.
`MEITOU_SHAFT_STAMPS=1` separates shafts grid passes. Compare matching instrumentation settings.
Details: [renderer-native.md](renderer-native.md), [render-shafts.md](render-shafts.md),
[render-distance-benchmark.md](render-distance-benchmark.md).

## Loading and runtime settings

Measure cold and warm starts separately. `--no-load-cache` bypasses cache reads/writes.
Foliage logs report layout hits/misses and settle/busy times. The layout key hashes relevant
source rather than module/build-path identity; see [formats/foliage.md](formats/foliage.md).
OS file-cache state is separate. GPU setup, biome texture uploads, river-map generation and
streamer settling remain costs with warm caches. Separate start-up from steady-state frames.
The game's `--quit-after` log reports exposed-menu FPS, longest frame and stalls over 100ms separately from covered loading work.
With `--log-spikes`, the game's exit also prints the profiler's per-stage GPU run summary once the last frame's timestamps
arrive. A spike frame whose simulation took over 20 ms also notes each system and part of it from 2 ms (`sim <name>`). A tick run ahead over 20 ms is noted in the frame whose draw waited for it (`sim ahead tick`, `sim ahead <system>`); that wait is the `ui` part `world end`. Its render-thread list ends with the parts of `ui` (everything from the world's last stage to the submission: world end, markers, steer, hud, debug, settings, cinema, loading, pointer, composite) and of `upd-chars` from 1 ms. The exit summary also lists the run's collections and its most allocated types (the runtime's allocation samples, `BenchGcListener`). In an interactive game run, `--windowed --size WxH` holds that window size; without `--size` it starts maximized.

The game/viewer disable tiered compilation and tiered PGO. Experiments can use
`DOTNET_TieredCompilation` / `DOTNET_TieredPGO`; record these settings. ReadyToRun and tiering
affect code generation and loading. Compilation/GC stalls matter to tail latency.

The flight benchmark (`--fly-benchmark <frames>`) prints a `gc` line: gen0/gen1 counts, pause total and bytes allocated during the flight.
`--fly-csv <file>` writes a row per frame: frame number, frame and CPU ms (as the `flight` and `cpu only` lines), this process's video memory and budget (VK_EXT_memory_budget, driver allocations
included), our allocator's blocks and their used bytes. `<file>.owners.csv` holds `GpuAllocator.Breakdown` every 30 frames (long format:
frame, owner, MB, device-local MB, count); that walk runs after the frame's time is taken but can lengthen the next frame's interval when pipelined.
Allocated bytes vary about 20% between identical runs (what streams in depends on timing) and a single blocking gen2 dominates the pause total,
so compare several alternating runs of two builds (copy each build's output folder). To rank allocation sites, run the viewer under
`dotnet-trace collect --providers Microsoft-Windows-DotNETRuntime:0x1:5 -- <viewer> ...` (GC events with `AllocationTick` stacks, one sample per
~100 KB) and count only events after the induced compacting gen2 of `FinishLoading` (the flight). Tracing lengthens GC suspensions: take pause
numbers from untraced runs.

## Play benchmark

`MeitouClient --load <save> --play-bench <dir>` plays a scripted session in the real game (frame limit and VSync off) and quits
(`PlayBench`). Segments: settle, idle, orbit, zoom to the farthest, pan, a 30 s travel at 3000 units/s, return, time x2
and x5, talk, trade, container, doors, pick-up, paths (a squad or each member sent 20-200 m in a random direction every 0.5 s), walk,
fight (six spawned enemies, `--scene-npc`), loot, the panels opened cold then warm (with limbs and build), a grazing horizon view, the overview, flights (each player character in turn, twice, 6 s each, as a double click on its portrait: a character flight when they are on).
Each interaction targets the nearest candidate within 150 m and retries every 3 s; a modal message box is closed and logged.
`MEITOU_BENCH_ONLY=settle,zoom` keeps only the named segments (the run is deterministic from the save, so a hitch comes back at its tick).
A later `--vsync` keeps vsync on (the frame limit stays off); the summary's run line says which.

**Metal** (`--renderer metal`, `MetalPlayBench`): the same script, sections and columns, with these differences.
- `gpu_total_ms` is the frame's command buffer (`GPUEndTime - GPUStartTime`) minus its overlap with the previous frame's
  (consecutive frames overlap on the GPU, so whole spans over-count); it arrives when the ring slot is reused, so the last
  three frames have none. Upload batches and the GPU-cull pre-frame are separate command buffers and not in it. No
  `gpu_<stage>` columns: Apple silicon samples counters only at encoder boundaries ([metal-performance-memory](metal-performance-memory.md)).
- `vram_mb` is `MTLDevice.currentAllocatedSize`. `late_pipelines` counts render pipeline states (all are made on first use);
  `MEITOU_METAL_TRACE=1` logs each state and each MSL library with its build time.
- `loop_begin-frame` holds the frame-slot wait; the `nextDrawable` wait comes at the end of the frame (the scene is committed
  first, so the GPU works while it waits) and is in `loop_render` and the `driver-wait` stage, which `render cpu ms` sums:
  with vsync on that column reads about the frame's idle time, not CPU work. `loop_view` also holds the navmesh,
  building and interior updates (the Metal loop runs them after the audio).
- The render-thread stages (`cpu_*`) come from the shared `WorldFrame` laps; `cpu_sim` stays 0 (`update_ms` has the ticks).
- The summary's run line names the device, scene and window sizes, the upscaler, the pacing, the GPU budget
  (`--metal-gpu-budget`) and the memory guard's state at the end. The saved graphics apply; `--benchmark-baseline` is
  not applied on Metal, so its numbers do not compare with the benchmarks page.
- The Metal loop saves vsync, frame limit, full screen, upscaler and graphics at exit whatever the run's options were:
  point a bench run at a copy of `meitou.user.json` (the file is read from the working directory first).

| budget | guard | idle fps / p99 | orbit | zoom | pan | gpu ms |
|---|---|---|---|---|---|---|
| 5120 MB | 92% after load, ranges clamped to x0.15, terrain material cache not made | 66 / 43.1 | 66 / 27.4 | 68 / 41.2 | 74 / 18.3 | 13.6-15.0 |
| 8120 MB | ok, 65% at the end | 65 / 41.1 | 62 / 29.0 | 66 / 19.9 | 72 / 19.1 | 13.9-16.1 |

The larger budget restores the material cache and the full draw ranges at about the same frame rate (more is drawn
for the same time); the frame is GPU-bound (busy time about equal to the frame). One pair. The game's default budget follows
the Mac's memory (metal-performance-memory).

`frames.csv` has one row per frame: interval, loop sections (`BenchLap`), render-thread stages (`StageClock`), GPU stages, world tick steps
and the slowest system (the world's `TickProfile`, as F12's sim rows), video memory (`vram_mb`, Vulkan, read every 30 frames), GC counts, bytes and pause (`BenchGcListener`: the
runtime's events in process), pipelines created while drawing. `summary.md` has per-segment percentiles, the largest stages, steps and
systems, the stutters (over 20 ms and twice the segment's median) and the collections. A frame's interval holds the previous frame's tail.

Attribution tools: `dotnet-trace collect --providers Microsoft-Windows-DotNETRuntime:0x40000019:5 -- MeitouClient ...` records the JIT
(`MethodJittingStarted` to `MethodLoadVerbose`, per thread), allocation samples and contention with stacks, without perturbing frames much.
The sample profiler (`Microsoft-DotNETCore-SampleProfiler`) suspends the runtime every millisecond: use it for where time goes, not for
timings, and expect samples parked on GC polls inside hot loops.

| hitch | cause | change |
|---|---|---|
| gen0/gen1 GCs 20-190 ms | ~1 GB gen0 budget | `System.GC.Gen0MaxBudget` 16 MB ([engine](engine.md) "GC") |
| first moving ragdoll 200-250 ms, then 10-60 ms ticks | JIT of Bepu's generic solver and collision code (single-threaded and, with a thread dispatcher, multithreaded paths) | `RagdollPhysics.WarmUpOnce` steps both: the first moving tick 21 ms |
| overview panel 440 ms | world map (8192², BC1) decoded to RGBA8 and uploaded in the frame | read ahead, uploaded as BC1 (`UiPainter.Prefetch`) |
| 150 ms as a town fills | up to four new character appearances built in one frame | `CharacterRenderer.BuildBudgetMs` |
| ~35 ms on the first cloudy weather | volumetric cloud renderer made on demand | made with the first frame |
| fps: terrain 3.4 of 7 ms with the parallax off | the march's registers allocated anyway (a tenth of the warps) | colour programs without the march when it is off ([terrain](render-terrain.md) "Registers"): 2.4 ms, frame -0.9 ms |
| 0.7-3.8 s in the reflection stage, once per session | pipelines first made in play (the reflection's scene when water first comes into view; the clouds; a camera cut drew without the fog shading rate attachment, a second pipeline of everything); seconds each with a cold driver cache (the first run after a shader or driver change), 0.16 ms median with a warm one | `ReflectionPass.Warm` and `SkyClouds.Warm` during the loading warm-up; a cut draws with a neutral rate image |
| 120-240 ms ticks every 15 (issue #124: a town save, ~1600 characters loaded) | the NPC first-aid sweep ran the relation tests (each `World.SystemOf` a LINQ scan of the systems) on every candidate in sight, healthy ones included; the relation tests' squad-memory lookups walked a sorted tree several times a pair | `SystemOf` remembered per type; a sweep's candidates are only those with a first-aid need; NPC looting collects the lootable bodies once a pass, not per looter; the memory hashed for lookups, one per direction a test; both looks staggered by slot over their 15 ticks instead of all on one: ticks p95 121 -> 8 ms, p99 170 -> 11 (headless, same save) |
| path answers held ~3 s once a session (headless, #124 save) | the long-range terrain grid (`CoarseRoute`) was built by the first long route, and every path thread asking meanwhile waited on it | built on a background task during loading (`CoarseRoute.Warm`) |
| a follower walked towards the world origin after spawning and asked for a path there | a spawned character's goal was (0, 0) until its first think set one; a follower waiting it out steered at it | `CharacterTable.Spawn` starts the goal at the character's own spot |
| 20-80 ms on first use (AI, dialogue, screens) | JIT: each method compiled on its first call (no tiering) | `JitWarmup`: the Meitou assemblies compiled ahead on three background threads, simulation then rendering first (~38000 methods, about 3 s; `--no-precompile`): the game thread's JIT 4.5 -> 2.3 s, none left in play |

Detailed logs/screenshots belong outside the repository. Evidence for original-game claims
belongs with the corresponding research and its Verified/Observed/Unknown label.
