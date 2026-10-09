# Shadow rendering

Part of the world view of `meitou-viewer` (and the game, which shares `src/Meitou.Rendering`); options, keys and the rest of the world mode are in [viewer.md](viewer.md#world-mode).


The sun's shadow map as the game's CSM mode draws it ([formats/shadows.md](formats/shadows.md)): four cascades in one atlas
(`--shadow-quality <0|1|2>`, 1024² / 2048² / 4096², default 2048²; `--shadow-range <u>`, default 5000 in Faithful and 10000 in Meitou (1000-9000 and 1000-15000; the F5 toggle swaps the default if the range was not touched); `--no-shadows`), drawn
before the reflection and the main pass from the terrain, objects and foliage meshes. `--debug-shadows 1|2|3` shows the cascade
maps, the term per cascade, or the term over the picture.

That is the Faithful choice of the `shadows` switch (F5, `--faithful shadows`). The default is the **Meitou shadows**
([formats/shadows.md](formats/shadows.md#meitou-shadows-the-shadows-switch)): the same atlas, quality and range, but cascades
fitted from the camera's near plane to the range (all four in use), drawn on a schedule (the first every frame, the
others every 4th, 16th and 32nd frame unless the camera or the sun made them stale, each kept with the matrices it was drawn with;
[formats/shadows.md](formats/shadows.md) "Schedule": the stage's GPU time 0.72 to 0.30 ms with a still camera, 0.92 to 0.34-0.46 with a turning one, 2026-10-08), a soft receiver (16-tap rotated disk,
contact-hardening penumbrae from a blocker search, cascades blended, a fade instead of the cut-off at the range's end) and the
terrain's own shadow out to the horizon, so mountains shadow valleys at low sun. The debug view 2 still tints by the game's
cascade selection. It is also the cheaper one (**Observed**, same caveats as below, against `--faithful shadows`): Hub
fly-benchmark shadow stage 2.43 against 3.31 ms CPU; forest camera 2.1-2.3 against 3.6 ms CPU and 0.29 against 0.77 ms GPU
(details in the format doc).

**Shadow pass cost** (**Observed**, 2026-10-05, RTX 4070, Debug build, 1600 x 900, `--faithful all --time 13`; the machine shared with
other agents' viewers, so spread is large). `--screenshot` logs print the pass per cascade (`WorldFrame.DetailedStats`: terrain
triangles, objects and foliage instances, draw calls and CPU ms, and the foliage's steps: culling, upload, meshes, TERRAIN-mode rocks)
and the foliage main pass per depth slice.
- *Where the time went*: at the forest camera (`--at -37582,-80684 --yaw -70.5 --pitch 6.1 --distance 10588`) the shadow pass cost
  ~17-27 ms of CPU against ~1.5 ms of GPU. Cascades 0 and 1 are unused there (the camera's near plane is beyond their split), cascade 2
  draws nothing, and cascade 3 drew 11,214 foliage instances in 6,051 draw calls: 64 instanced batches of trees and bushes and 5,978
  single draws of TERRAIN-mode rocks (`TerrainRenderer.DrawMeshes`, one draw per placement and part with its matrix as a uniform),
  which alone took ~23 ms. The foliage candidate cache shared by the cascades was working (`cull (cached)` in the log, ~1.2-1.4 ms a
  cascade); the cost was VkGl's per-draw work (~4 µs: pipeline lookup, the loose-uniform block copied to the ring for the new matrix,
  descriptor offsets, vertex and index buffer binds) times thousands of draws. The main pass had the same pattern on a smaller scale:
  574 rocks, ~2.8 ms.
- *Fix*: the TERRAIN-mode meshes (foliage rocks and TERRAIN-mode objects) are drawn instanced, one draw per (vertex array, index count,
  mirrored or not), the placement as four per-instance rows at locations 7 to 10 (`TerrainShaders.MeshInstanceLocation`) and, in the
  main pass, the feature's biome row in row 0's w (it only reaches the position's unused w) read as a flat varying
  (`TerrainShaders.MeshFragment`). Exactness: the shaders compute the position with the uniform form's arithmetic; the shadow map is
  depth only with a Less test, so the order of the draws cannot change a texel; in colour only two different placements at exactly
  the same depth could show the order. Measured against the build before (itself identical in two full runs): 0 differing pixels on
  all ten parity views (`tools/scripts/parity.sh`, `--faithful all`) and on `--debug-shadows 1` (the atlas drawn into the picture) at
  the forest and The Hub. The rock view with `--debug-shadows 1` (90 rocks on screen) was identical in 8 of 9 runs; one run differed
  in 20 pixels (largest 14/255) at a distant building, not a rock, with the same streaming and residency counts in its log; the
  build before was identical in 9 of 9. Unexplained at the time. Most likely explained 2026-10-06: two texture uploads into the same
  image in one upload command buffer raced (synchronisation validation: 10 WRITE_AFTER_WRITE hazards per view); a build with step-2
  timing changes showed few-pixel rock-view differences in 2 of 9 runs, and with the uploads ordered by a barrier 0 of 9 and sync
  validation clean (docs/engine.md "Vulkan backend", "Memory and order").
- *Numbers* (`--fly-benchmark 300 --fly-speed 0`, a still camera, three interleaved runs each, medians): forest, draws per frame
  7,485 -> 977, shadow stage 25.3 -> 5.7 ms (its foliage part 23.9 -> 4.2), foliage main pass 7.5 -> 4.1 ms, render-thread CPU per
  frame (p50, commands recorded) 36.9 -> 13.3 ms; The Hub (`--town "The Hub" --distance 40000 --pitch 3`; 46 rocks in the main pass, 3 in the shadow map), draws
  777 -> 746, shadow stage 3.2 -> 3.1 ms, CPU per frame 6.7 -> 6.6 ms (within noise).
- *Not done*: reusing last frame's map when the sun, every cascade's snapped box and the LOD eye are unchanged. It would be exact
  only if every change to the casters (streamed meshes and textures arriving or unloading, foliage pages, terrain height windows, LOD
  and range settings) invalidated it, and there is no single version counter for that yet; a missed case would show stale shadows in
  motion that the still-picture gate cannot catch. Left: the foliage candidate pass (~1.3 ms for the first drawn cascade, which records
  every instance within 1.2 x the shadow range, and ~1.2 ms to test them against each further cascade) and the terrain patches
  (~0.9 ms for cascade 3).


**Spikes of the Meitou schedule** (**Observed**, 2026-10-09, RTX 4070, `--view swamp --size 1920x1080 --upscaler dlss --render-scale native`, DLSS verified, per-frame trace of which cascades were drawn against the stage's GPU time).
- *Cause*: the stage costs 0.2 ms when only cascade 0 is drawn and about 1.1 (cascade 1), 1.3-1.5 (2) and 1.6-1.8 ms (3) for each far cascade drawn on top, so its cost is the number of far cascades drawn in a frame. Of 684 frames 404 drew none (mean 0.24 ms, 21 % of the stage's time), 251 drew one (mean 1.16, max 2.46 ms, 63 %), 25 drew two (2.54, up to 3.64) and 4 drew three (3.4); the 29 frames with two or more are the p99 (3.2 ms) and 17 % of the time. They came from coincidences: the cadence (1, 4, 16, 32) and the "camera orbited out of the box" redraws (cascade 3: 59 of its 79 draws, cascade 2: 23 of 66) are independent, and nothing kept them apart. Not the cause: the terrain map and landmark map (built once, in the first frame), range-drift or sun-jump redraws of all cascades (one, at frame 0), the blocker tiles (a fullscreen triangle per redrawn tile, negligible).
- *Fix* (`ShadowSchedule.Pick`, CPU only, no shader change): at most `FarBudget` (1) far cascades are drawn per frame; the other wanted redraws are owed and go on the following frames, the longest-waiting first, and are forced after `MaxWait` (3) frames whatever the budget. A cascade with no map yet is never deferred, cascade 0 is not counted. The total demand is about 0.5 far draws a frame, so the queue is short (traced: 38 deferrals in 684 frames, mostly one frame). A deferred cascade keeps the matrices it was drawn with, as every cached cascade does, and its staleness test (box coverage, sun turn) still holds the next frame. `MEITOU_SHADOW_BUDGET=0` and `--ab shadow-spread` give the old behaviour.
- *Paired result* (`--ab shadow-spread --ab-period 64`, 1024 frames, orbit): shadows stage mean 0.74 against 0.74 ms (the same work), p95 1.92 against 2.19, p99 2.36 against 2.93, max 2.60 against 3.84; GPU total p99 8.11 against 8.84 ms, frame p99 8.55 against 9.35 ms (means unchanged, 6.23 against 6.25). A still camera (384 frames, only cadence redraws): shadows p99 1.89 (spread) against 1.75, no gain there (the redraws do not collide); the frame and total p99 differences are noise. Picture A against B: orbit 119 pixels differ by 4/255 or more, none by 12 (max 8); still 3 pixels by 4 or more (max 5). So the delay of a cascade's redraw by a frame or two is not visible; the two sides' absolute numbers drift with other programs on the GPU, only the pair counts.
- *Not done*: the p99 now is a single cascade 2 or 3 redraw (1.3-1.8 ms); lowering it further needs splitting a cascade's draw over frames (by tile quadrant, with the old content kept), and the mean is the same work as before.
