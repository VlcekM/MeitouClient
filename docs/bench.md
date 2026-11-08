# Benchmarking

The world viewer's offscreen harness measures frame cost and visual effects of runtime switches.
Implementation: `WorldApp.Bench.cs`, `src/Meitou.Game/Rendering/Bench/`, `BenchHarnessTests`.
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
(`GameHost.PlayBench.cs`). Segments: settle, idle, orbit, zoom to the farthest, pan, a 30 s travel at 3000 units/s, return, time x2
and x5, talk, trade, container, doors, pick-up, paths (a squad or each member sent 20-200 m in a random direction every 0.5 s), walk,
fight (six spawned enemies, `--scene-npc`), loot, the panels opened cold then warm (with limbs and build), a grazing horizon view, the overview, flights (each player character in turn, twice, 6 s each, as a double click on its portrait: a character flight when they are on).
Each interaction targets the nearest candidate within 150 m and retries every 3 s; a modal message box is closed and logged.
`MEITOU_BENCH_ONLY=settle,zoom` keeps only the named segments (the run is deterministic from the save, so a hitch comes back at its tick).

`frames.csv` has one row per frame: interval, loop sections (`BenchLap`), render-thread stages (`StageClock`), GPU stages, world tick steps
and the slowest system (the world's `TickProfile`, as F12's sim rows), video memory (`vram_mb`, Vulkan, read every 30 frames), GC counts, bytes and pause (`BenchGcListener`: the
runtime's events in process), pipelines created while drawing. `summary.md` has per-segment percentiles, the largest stages, steps and
systems, the stutters (over 20 ms and twice the segment's median) and the collections. A frame's interval holds the previous frame's tail.

Attribution tools: `dotnet-trace collect --providers Microsoft-Windows-DotNETRuntime:0x40000019:5 -- MeitouClient ...` records the JIT
(`MethodJittingStarted` to `MethodLoadVerbose`, per thread), allocation samples and contention with stacks, without perturbing frames much.
The sample profiler (`Microsoft-DotNETCore-SampleProfiler`) suspends the runtime every millisecond: use it for where time goes, not for
timings, and expect samples parked on GC polls inside hot loops.

**Observed** (2026-10-24, save `mec-tou-nu`, 2560x1440, DLSS at 1.0, RTX 4090): GPU-bound, 130-190 fps (5-7.6 ms GPU; terrain ~3.5,
post 0.9, objects 0.7, particles 0.5, shadows 0.45); render thread ~3 ms; world tick ~1 ms. Hitches found and their causes:

| hitch | cause | change |
|---|---|---|
| gen0/gen1 GCs 20-190 ms | ~1 GB gen0 budget | `System.GC.Gen0MaxBudget` 16 MB ([engine](engine.md) "GC") |
| first moving ragdoll 200-250 ms, then 10-60 ms ticks | JIT of Bepu's generic solver and collision code (single-threaded and, with a thread dispatcher, multithreaded paths) | `RagdollPhysics.WarmUpOnce` steps both: the first moving tick 21 ms |
| overview panel 440 ms | world map (8192², BC1) decoded to RGBA8 and uploaded in the frame | read ahead, uploaded as BC1 (`UiPainter.Prefetch`) |
| 150 ms as a town fills | up to four new character appearances built in one frame | `CharacterRenderer.BuildBudgetMs` |
| ~35 ms on the first cloudy weather | volumetric cloud renderer made on demand | made with the first frame |
| fps: terrain 3.4 of 7 ms with the parallax off | the march's registers allocated anyway (a tenth of the warps) | colour programs without the march when it is off ([terrain](render-terrain.md) "Registers"): 2.4 ms, frame -0.9 ms |
| 0.7-3.8 s in the reflection stage, once per session | pipelines first made in play (the reflection's scene when water first comes into view; the clouds; a camera cut drew without the fog shading rate attachment, a second pipeline of everything); seconds each with a cold driver cache (the first run after a shader or driver change), 0.16 ms median with a warm one | `ReflectionPass.Warm` and `SkyRenderer.Warm` during the loading warm-up; a cut draws with a neutral rate image |
| 120-240 ms ticks every 15 (issue #124: a town save, ~1600 characters loaded) | the NPC first-aid sweep ran the relation tests (each `World.SystemOf` a LINQ scan of the systems) on every candidate in sight, healthy ones included; the relation tests' squad-memory lookups walked a sorted tree several times a pair | `SystemOf` remembered per type; a sweep's candidates are only those with a first-aid need; NPC looting collects the lootable bodies once a pass, not per looter; the memory hashed for lookups, one per direction a test; both looks staggered by slot over their 15 ticks instead of all on one: ticks p95 121 -> 8 ms, p99 170 -> 11 (headless, same save) |
| 100-130 ms frames every few seconds (issue #124 save, in the game) | the character renderer zeroed its bone palettes in a new managed `byte[]` each frame before copying them up (~135 MB/s, megabytes per array, so on the large object heap): a background gen2 every ~4 s, gen0 pauses 22-27 ms and gen1 50-90 ms meanwhile (**Observed**, GC trace of the running game) | the frame's upload memory cleared in place; since 2026-11-02 not cleared at all (every live character's range is written, an icon clears its own): `upd-chars` 0.58-0.63 -> 0.50-0.53 ms (**Observed**, play bench, paired) |
| the same, after that fix: gen0 20-40 ms, gen1 60-107 ms | each path thread's A* scratch was dropped after any search over 65536 nodes, so the searches that ran to the iteration limit (unreachable goals) rebuilt their table and nodes each time: ~280 MB/s, mostly on the large object heap (**Observed**, same trace) | the scratch kept at whatever size a search grew it to |
| path answers held ~3 s once a session (headless, #124 save) | the long-range terrain grid (`CoarseRoute`) was built by the first long route, and every path thread asking meanwhile waited on it | built on a background task during loading (`CoarseRoute.Warm`) |
| a follower walked towards the world origin after spawning and asked for a path there | a spawned character's goal was (0, 0) until its first think set one; a follower waiting it out steered at it | `CharacterTable.Spawn` starts the goal at the character's own spot |
| 20-80 ms on first use (AI, dialogue, screens) | JIT: each method compiled on its first call (no tiering) | `JitWarmup`: the Meitou assemblies compiled ahead on three background threads, simulation then rendering first (~38000 methods, about 3 s; `--no-precompile`): the game thread's JIT 4.5 -> 2.3 s, none left in play |
| character flights between far-apart squad members (stutter final boss save, 5 members across the map, 1.5 s flights; two baseline runs: frames p50 24-29 ms, p95 69-80 while flying, 12-15 / 30-41 in the 1.5 s after landing, 8-9 / 19-21 at rest; GPU 7-9 ms throughout, VRAM steady) | the simulation, not the textures ("Keep textures" changes nothing): the focus moves to the landing at the flight's start, its 3 x 3 block loads 150-500 characters within a few ticks (stored town squads, saved platoons) while the old block lingers 4 s and its towns 60 s (`UnloadGraceSeconds`), so the loaded count climbs 710 -> 1660 over ten hops; a tick costs about 1.5 ms + 4.5 us a loaded character at rest (4.6 ms at 715, 9.2 at 1660) and 12-13 ms while flying (the newcomers' spawn ticks, first thinks, sight and ragdoll setups); the sim is 48% of the excess over a rest frame; the render thread's arrival stages are the rest (`StageClock`): `upd-foliage` 11%, `shadows` 9%, `upd-chars` 7%, `gi` 4% (candidates, from the code: zone layouts accepted unbounded per frame; every cascade uncovered each frame while the camera crosses kilometres; gather and pose over every loaded character); GC pauses of 4-12 ms in a tenth of the flight frames (the spawns' allocation). Only 15% of flight frames ran two ticks; capping the streaming threads at 3 changed nothing in a paired run (CPU contention is not it) | the far cadence ([simulation](simulation.md#far-cadence): a character beyond the draw reach of the camera and 1500 of every player character steps once in 4 ticks; about 650 of ~1350 loaded skipped a tick while flying) and the far cascades left undrawn while the host flies the camera ([render-shadows](render-shadows.md) "Flights"; 420 of the flights segment's frames, 0 elsewhere): **Observed** (one interleaved pair, FC6a against FL0d, 2026-11-03) flying frames p50 28.9 -> 26.2 ms, mean 34.8 -> 31.2, sim 14.0 -> 11.6 ms a frame (a tick 13.7 -> 12.3), shadows 3.2 -> 2.4; the 1.5 s after landing 19.3 -> 18.7 mean; at rest unchanged (7.6 / 7.2 ms a tick). Of the flying tick the parallel phases fell: act 2.6 -> 2.0, think 1.4 -> 1.0, sight 1.2 -> 0.9, move 0.8 -> 0.7; With the serial phases on the cadence too (the sweeps' `World.Turn`, the publish's shared snapshots; pair FS1a against FL0e, 2026-11-03): a flying tick 12.8 -> 11.4 ms, sim 11.8 -> 10.8 a frame, shadows 2.9 -> 2.3, frames unchanged (p50 26.3 against 26.6: the render thread's arrival stages are the longer pole, `upd-foliage` 2.6, `shadows` 2.3, `upd-chars` 1.9, `gi` 1.5, `ui` 1.3); the slow world did not move (1.46 ms a frame both): its cost is the ragdoll system (0.6-2.9 of its 1.5-4.9 ms a tick in the headless probe), not the sweeps, whose cost is per tick (town duty's scan of every slot, first aid's treated list). The ragdoll levers ([simulation](simulation.md#ragdolls) "Scene": 4-unit tile cells, a minute's tile life, 8 tiles and 4 starts a tick, the step single-threaded below 256 awake bodies) take a landing's 45-60 ms ragdoll tick (80-100 ground tiles and 20 builds for a zone's downed characters re-ragdolling at once) down to the budgets' 1-4 ms in the probe (setup loop at most 3.4 ms a tick). In the game (pair FS3a against FL0h, 2026-11-03, on a machine running 30-50% slower than in the morning's pairs, both runs alike, so frame times are not comparable across pairs): flying tick 16.0 -> 15.1 ms, sim 20.3 -> 19.1 a frame, frames within noise; the flights' frames with a ragdoll tick over 8 ms 34 -> 18, the worst 36 -> 34 ms, 17 of the 18 without a GC pause: the suspects are the ground probe's heightmap block reads (up to 4 starts x 5 ms a tick, the first in each 1,152-unit block) and the dispatcher above 256 awake bodies under the game's thread contention; neither is attributable from the bench's columns (top system only). Next levers: the heightmap blocks read on the zone gather's worker (`RagdollGeometry`) instead of on demand in the tick, the physics dispatcher's thread count, the render thread's arrival stages (foliage accepts bounded, zone layouts), the GC pauses of the spawns' allocation (10-25 ms, landing in whatever runs, the physics step in the probe's trace), the reloaded downed characters' re-fall (20 bodies simulated for 7 s after each landing, 0.8 ms a tick), a shorter grace for camera-block-only zones. Measured with `MEITOU_BENCH_ONLY=settle,flights`, the flights segment split by hop phase (`frames.csv` has the `far` column) |

Largest remaining allocation (shares of the ~160 MB/s before the fixes): the screens rebuild their controls and property dictionaries every frame (`MyGuiPresentation.Arrange`, ~6% with the HUD's),
snapshot records per character (~2%), particle affector delegates (~1%). The HUD's arrangement is reused while its bindings are equal (`UiBindings.ContentEquals`); the life bars are bound in steps of 1/1024 (`BarStep`, about half a pixel of a 4K bar), else hunger and blood moved them every tick and the arrangement was remade on ~30% of frames, now on a clock change or the 30-call refresh (**Observed**, stutter final boss save; ~0.1 ms a frame), and the painter keeps each caption's lines and widths (`UiPainter.LayOutText`).
**Observed** (2026-11-02, quicksave at 1920x1080, HUD only, paired runs): the HUD's render-thread cost 0.83 -> 0.69 ms a frame (layout 0.15 -> 0.05, paint 0.47 -> 0.43); what remains is binding and arranging the rest (~0.2 ms) and emitting the quads and glyphs (~0.4 ms).
The rest of the arrangement (bindings, squad bar, HUD text, open screens) is reused too (`PlayerInterface.ArrangedBase`): remade when the shown snapshot, the mouse, the window, the mode, the open screens, the speed, the modifiers or any input changed, while something in it animates (title, editor, map, biome banner, portrait or tutorial flashes, a held scrollbar), or after 50 ms; name tags, floaters, speech, the notice and the hover label are added every frame. **Observed** (2026-11-02, stutter final boss save, play benchmark, three clean interleaved pairs): `ui` 1.11-1.25 -> 0.87-0.91 ms idle, 1.04-1.38 -> 0.90-1.01 orbiting; allocation 0.82 -> 0.63 MB a frame idle.
The painter keeps each control's output too (`UiPainter.Cache.cs`): the quads and glyphs it added to the overlay pass, its opaque area and edit boxes, replayed for a control equal in content (fields and property values, not the object: the arrangement makes new ones each tick) in the same hover and pushed state. A control is recorded the second frame it is seen; never one with a focused caret or an icon still loading or drawn by the renderer; all records go when a painter texture, a UI font atlas (`UiFont.Changes`), the size or the mode changes. **Observed** (same save and method, three pairs): `ui` 0.99-1.06 -> 0.56-0.77 ms idle, 1.03-1.09 -> 0.61-0.68 orbiting; allocation ~0.70 -> 0.47-0.61 MB a frame; the UI showcase screenshots (`--ui-showcase`) of both builds differ only in the world's noise and a notice's timing.
Per-frame garbage removed after an allocation trace of the idle segment (**Observed**, 2026-11-02; shares of the sampled bytes before): the overlay's `PassTargets` record per flush (~9%, a pass needs only the scissor), boxed `EffectUnit` enumerators and a `Concat` in the particle walks (~7%, now indexed), a closure per character in `CaptiveSystem.BodyToEat` (~6%), the character audio's and the task bars' per-character closures (~3%), the zones' interior walks (`Zone.Interiors` is an array, `AllZones` the dictionary's values; ~2%) and `NativeFrame`'s zero bone block (a static array). In a trace after the fix none of them shows; timings were not compared (other builds loaded the machine).
Second round, same method: `ProductionSystem.SlowWorld`'s town lookup (a closure per character a tick, ~1.6%), `MedicalState`'s part summary (a record per change, now a value under a lock, ~2%), `EnclosedAt`'s site walk in the publish (a boxed enumerator per character), the door sounds (a key string per door a frame; `DoorSounds` is keyed by the snapshot's (building, door)), `TownDutySystem.Contraband` (two closures per cage) and `ZoneNavMesh.WithoutPruned` (a vertex dictionary and LINQ per polygon; its output mesh is needed). None shows after; what remains is spread thin (largest steady: debris names, bindless descriptor writes, biome gathering, ~1-2% each) besides first-play audio decoding.
Simulation side (**Observed**, headless probe of a ~1600-character town save, 2026-11-01): ~0.21 MB a tick (1.72 MB before): `World.Publish` reuses its character snapshots and layer lists (docs/simulation.md "Snapshots"), and `BodySystem.HungerFloor` and `AnimationSystem.PainHold` no longer allocate per character. Collections per 1800 ticks (a minute at 1x): ~41 before the snapshot reuse, 5-6 after.
Pipelines are made on first use (`PipelineLibrary.Prepare` has no caller): the loading warm-up's frames make what they draw. Still made in play: the blood
decals', a portrait's, the hover outline's and the build menu's icon pipelines (one each per session). Every frame's count is in `frames.csv` (`late_pipelines`).
ReadyToRun does not compile Bepu's generic instantiations ahead, so it does not replace `RagdollPhysics.WarmUpOnce`; what the game thread still compiles is
framework generics over the game's types (sorts, LINQ).
Commands are given on wall time, so two runs' simulations part after a few minutes (from "paths" the squad stands elsewhere): compare segments before that.
Precompiled code is slower and, without tiering, never replaced (**Observed**, self-contained publishes, render thread per frame): JIT 3.05 ms,
ReadyToRun 5.17, composite ReadyToRun 3.96; launch to play 21.7, 25.5, 20.0 s. The release stays JIT with `JitWarmup`.
Tried, not kept (**Observed**, Nsight GPU Trace and the bench at a save's view, 2560x1440, render scale 1): the SMs idle 29% of the frame over ~350
wait-for-idles, but leaving out the native segments' seam barriers saved only 0.13 ms (the passes mostly read what the previous wrote). The GPU
"objects" stage is stage-switch timestamps: the object draws themselves take ~0.06 ms. Terrain's remaining 2.4 ms is its materials: GI, haze or the
shadow filter off change it by 0.1 ms at most, anisotropy off by 0.12.

**Observed** (2026-11-02, stutter final boss save, 1920x1080 DLSS, RTX 4070, two interleaved pairs; the second branch run's orbit was
disturbed by other load, every stage up): one reversed scene depth ([renderer-native](renderer-native.md) "Depth") against the two
slices, render-thread medians idle / orbit: terrain 0.17-0.18 -> 0.08 ms, foliage 0.90-0.92 -> 0.72-0.74 / 0.71-0.72 -> 0.53-0.62, water
0.06 -> 0.04, objects within noise (0.12-0.14); GPU frame 7.46-7.47 -> 7.07-7.13 ms idle, 7.48-7.55 -> 7.18 orbiting; frame p50 7.3-7.4
-> 7.1-7.2 ms. Pictures (viewer, offscreen, 1600x900, upscaler off): the parity views within 0.07 mean levels of master except views
that differ run to run on master itself (hub: master against master 0.6-1.4, the branch against three master runs 0.2-1.6; forest at
night: GI noise, 0.15 either way); named views, TAA, DLSS and Faithful within 0.11. Pass `--upscaler` (or `MEITOU_STREAMLINE_PATH`) to
both sides: a build with the Streamline DLLs beside it takes the configured DLSS, one without falls back to TAA.

Detailed logs/screenshots belong outside the repository. Evidence for original-game claims
belongs with the corresponding research and its Verified/Observed/Unknown label.
