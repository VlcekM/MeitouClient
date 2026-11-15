# Metal terrain shader plan

Status: **phase 0 and items 1–4 measured; normal-map variant kept; distance material LOD tried and rejected**
(2026-10-23). This is the follow-up to the Metal GPU-cull work. The goal is to reduce the terrain pass cost on Metal
without changing the picture, and keep Vulkan as the reference path.

Current evidence:

- Native 1600x1200 Metal capture: 32.15 ms frame, main scene 22.3 ms, terrain 11.3 ms (35.13%),
  objects 4.3 ms, foliage meshes 2.4 ms, grass 1.1 ms, cull pre-frame 0.91 ms.
- Steady Metal trace: ~21.7 ms main command buffer, ~46 fps; CPU cull ~44 fps.
- Vulkan `--log-spikes`: total 22.3 ms, terrain 13.75 ms (62% of GPU), particles 2.05, post 1.44,
  cull 1.16.
- The main terrain encoder is wide (occupancy/bandwidth/texture work), not ALU-bound. The counters showed low
  ALU limiter in the main encoder and higher ALU limiter in smaller shadow/prep encoders.
- Private static terrain textures/grid buffers are behind `MEITOU_METAL_PRIVATE_STATIC=1`; the first A/B was
  inconclusive, so shared storage remains the default.
- Apple silicon supports counter sampling only at stage boundaries; mid-encoder per-stage Metal timestamps are
  not available. Do not build a multi-frame Xcode workflow (one trace is ~3 GB). Use one frame plus
  `MEITOU_METAL_TRACE=1`.

## Results (2026-10-23)

Raw Metal view: 800x600 points (1600x1200 pixels), `--view hub`, no population/objects/foliage, `--no-vsync`,
`MEITOU_METAL_NO_TAA=1`, `MEITOU_METAL_SLOT_WAIT=serial` (the serial wait is what makes `LastGpuMilliseconds`
readable in the trace):

| Measurement | GPU ms | Change |
|---|---:|---:|
| baseline | ~14.2 | — |
| `--material-distance 1` | ~11.8 | −17% |
| `--anisotropy 1` | ~14.8 | noise |
| `--terrain-error 32` | ~14.2 | noise |
| no-normal compile variant (`MEITOU_TERRAIN_NO_NORMAL=1`) | ~12.3 | −12% |

- The material path, not geometry or anisotropic filtering, is where the cost is.
- The normal-map-off path is a real 12% win, but it changes the picture. It is kept as a quality-switch variant;
  the default normal-on path is unchanged.
- A loaded variant (fixed map-ready/debug/grid/wireframe branches) was ~13.9 ms and a solid-pass
  `early_fragment_tests` variant was ~13.8 ms: both inside the noise floor, so they were removed.
- Full scene at MetalFX temporal 0.67: ~16.5 ms; normal maps off ~16.3 ms. The terrain shader is less of the
  shipping frame at the upscaled size.
- Fast math: Metal's default already enables it. Forcing `MEITOU_METAL_FAST_MATH=0` raised the same view to
  ~33 ms; do not set `FastMathEnabled=false`.
- Half precision: `mediump` is emitted as `float` by this SPIR-V-Cross version. Explicit `float16_t` does translate
  to `half`, but the terrain would need a Metal-only program and a device capability check; not justified by the
  current wide/ALU-light profile.
- Matched Vulkan: no-object view terrain 7.83 ms, total 14.62 ms; full scene terrain 11.40 ms, total 22.51 ms.
  The two backends are not uniformly different on terrain; compare the same view and scene.

### Rejected follow-up: distance material LOD

After the first review we tried a distance-based terrain material LOD: a uniform normal-map fade, then a per-patch
split between the normal-on near program and the no-normal far program.

- Uniform fade alone: no measurable win. The shader still contains the normal-map samples and their register
  pressure even when the distance branch skips them.
- Per-patch split, forced `0,1` (all far): ~13.5 ms versus 14.2 baseline and 12.7 with normal maps fully off.
- Per-patch split at a realistic `500,1500`: ~14.0 ms, noise.
- The near patches cover enough of the screen that dropping only the far patches cannot remove the main cost. A
  finer split (smaller patches, a separate near/far material pass, or a baked/virtual material) would be needed to
  get more, and the shipping normal-map cost is only ~0.2 ms at MetalFX 0.67.

Decision: reverted. Do not keep a per-patch material LOD in the terrain path without a finer material split and a
new measured gate.

## Measurement protocol

Use the same view, seed, time, weather, population, upscaler and render scale. Two measurements:

1. **Raw comparison** — 800x600 points (1600x1200 pixels), `MEITOU_METAL_NO_TAA=1`, no AA/upscaler.
2. **Shipping-like** — the actual MetalFX/upscaler setting at the same window; record the scale and upscaler.

```sh
# Metal, raw
MEITOU_METAL_NO_TAA=1 \
MEITOU_METAL_TRACE=1 \
MEITOU_METAL_GPU_BUDGET_MB=7168 \
dotnet run --project src/Meitou.Game --no-build -c Release -- \
  --renderer metal --windowed --size 800x600 \
  --new-game --no-vsync --fps-limit 0 \
  --time 12 --distance 2000 --pitch 20 \
  --faithful clouds,cloudshadows --no-gi --quit-after 30
```

For the single-frame breakdown:

```sh
MTL_CAPTURE_ENABLED=1 \
MEITOU_METAL_NO_TAA=1 \
MEITOU_METAL_GPU_BUDGET_MB=7168 \
dotnet run --project src/Meitou.Game --no-build -c Release -- \
  --renderer metal --windowed --size 800x600 \
  --new-game --no-vsync --fps-limit 0 \
  --time 12 --distance 2000 --pitch 20 \
  --faithful clouds,cloudshadows --no-gi
```

Wait for the world to warm up, press `Ctrl+/`, and inspect the `terrain` debug group. Capture once; do not capture
multiple frames. **Quit the game before opening the trace in Xcode.** On this 16 GB M1 Pro, replaying a 2.6 GB trace
while the game was still running produced a WindowServer GPU progress timeout and a reboot on 2026-10-23; see
[docs/metal-performance-memory.md](metal-performance-memory.md#gpu-frame-capture). Prefer the steady trace and stage
counters when a capture is not essential.

For Vulkan pass comparisons:

```sh
DYLD_LIBRARY_PATH=/opt/homebrew/lib \
dotnet run --project src/Meitou.Game --no-build -c Release -- \
  --renderer vulkan --windowed --size 800x600 \
  --new-game --no-vsync --fps-limit 0 \
  --upscaler off --render-scale 1 --no-fxaa \
  --time 12 --distance 2000 --pitch 20 \
  --faithful clouds,cloudshadows --no-gi --log-spikes --quit-after 30
```

Ignore the first ~10 seconds and the occasional ~950 ms drawable stalls seen in windowed runs. Repeat an A/B at
least twice; a difference under ~5% is not a decision.

## 0. Isolate terrain before changing the shader

Do these with the existing switches before any shader work:

- `--anisotropy 1` against the default: tests texture filtering.
- `--material-distance 1` (ground colour only; diagnostic, changes the picture): tests layer texture sampling.
- `--terrain-error 32` against 16: tests geometry/LOD.
- `--no-objects --no-foliage` against the full scene: confirms the terrain share and removes overdraw noise.
- Normal maps off: the viewer has the `normal-maps` A/B; for the game, add a temporary
  `MEITOU_TERRAIN_NO_NORMAL=1` (or expose `TerrainRenderer.NormalMaps`) only if needed.
- `terrain-layer-skip` off: use the viewer A/B or a temporary `MEITOU_TERRAIN_LAYER_SKIP=0`.

Decision gate: continue only if one of these points at a shader/material lever rather than geometry/fill/present.
If `--material-distance 1` or anisotropy is the big mover, continue with the shader items. If neither moves, a full
MSL rewrite is unlikely to pay.

## 1. Fast math / Metal compile options

Current state: `MetalShaders.Library` uses `new MTLCompileOptions()`; Metal's default already enables fast math.
`MEITOU_METAL_FAST_MATH` can force it on or off for an A/B, but leaving the environment unset preserves the
default. `MEITOU_METAL_FAST_MATH=0` made the raw view ~33 ms, so the diagnostic must not be used as a default.

Plan:

- Add a temporary `MEITOU_METAL_FAST_MATH=1` A/B that sets `FastMathEnabled = true` on the Metal compile options.
- Measure the full frame and the terrain group; compare the terrain seam/normal/height behaviour.
- If the change is useful, decide whether to apply it to all Metal programs or only the terrain/native programs.
  Keep the default conservative until visuals are accepted.
- Consider `LanguageVersion = MTLLanguageVersion.Version30` only if it produces a measurable change; do not
  change it for its own sake.

Likely win: small. Main scene is not ALU-bound. Treat a sub-2% change as inconclusive.

## 2. Function constants / specialized variants

Current terrain fragment shader branches on `uNormalMaps`, `uTextured`, `uFeature`, `uDebug` and layer weights.
`TerrainRenderer.NormalMaps` is already a real quality switch; the viewer has `normal-maps` and `terrain-layer-skip`
A/Bs.

Plan:

1. Start with the **normal-maps off** variant, because it removes the second set of layer samples and is a real
   quality switch. Compile a separate terrain fragment program with the normal blocks removed
   (`#define`/preprocessor variant), rather than a runtime uniform branch.
2. If that shows a win, add a **feature** variant (the one-biome `uFeatureBiome` path) and keep the uniform path for
   mixed terrain.
3. Only then investigate Metal function constants. The current SPIR-V-Cross path may support specialization
   constants; if not, a direct MSL fragment variant is the alternative. Do not add a second shader source just for
   a branch that is already cheap.

Result: the no-normal variant is implemented (`TerrainShaders.FragmentNative(normalMaps: false)`,
`TerrainRenderer.NormalMapsOn` and the `*NoNormal` programs). The fixed height/feature/loaded branches and the
normal-on branch were below noise and were not kept. The `MEITOU_TERRAIN_NO_NORMAL` environment is the game A/B;
the viewer's existing `N`/`normal-maps` toggle selects it too.

Measure pipeline compilation time and the number of variants; shader cache/pipeline creation must not regress load
time. The default (normal maps on) must remain visually identical.

## 3. Explicit half precision

Only after 0–2. The capture showed low ALU, so this is a register/occupancy/bandwidth experiment, not a guaranteed
speedup.

Plan:

- Inspect the generated MSL with `MEITOU_METAL_DUMP=/tmp/meitou-metal` and identify the color/normal/UV
  intermediates.
- Try `half`/`mediump` only for sampled color/normal values and temporary shading values. Keep world positions,
  height blending and UV world coordinates in `float`; the terrain coordinates are large.
- If using GLSL, add `mediump`/`RelaxedPrecision` and `MslEnable16BitTypes`; if the translator does not preserve
  the intended precision, use a Metal-only fragment variant.
- Do **not** regex-replace `float4` with `half4` in generated MSL. It can break layouts, positions and precision.
- A/B at raw and shipping scale; check terrain seams, distant shimmering, normal-map shading and shadows.

Likely win: uncertain. Revert if the image changes or the frame improvement is below the noise floor.

Result: not implemented. A probe showed `mediump` still translates to `float`; explicit `float16_t`/`f16vec4` does
translate to `half`, but using it in the terrain needs a Metal-only shader path and a shaderFloat16 capability check.
Given the wide/ALU-light profile, that does not clear the bar for a direct MSL fallback yet.

## 4. `[[early_fragment_tests]]`

The solid terrain program has no `discard`, and the renderer already has a depth prepass and late colour. The
attribute may be redundant; the experiment is still cheap.

Plan:

- Add `layout(early_fragment_tests) in;` to a **solid-only** terrain fragment variant (not the interior-cut or
  other discard variants) and confirm the generated MSL contains `[[early_fragment_tests]]`.
- Measure terrain with depth test/write in the late-colour pass. Do not enable it where the shader can discard.
- If there is no measurable change, remove it; the hardware is already doing the useful early test.

Result: implemented and confirmed in the MSL (`[[ early_fragment_tests ]]`); 13.8 ms no-object and 21.0 ms full scene
versus 20.8 ms with it off. No measurable change, so it was removed.

## 5. `constexpr sampler`

This is the least promising item. `TerrainTexture` uses a sampler with anisotropic filtering and a dynamic
`ctx.LodBias` from the upscaler. A `constexpr sampler` can hardcode filter/anisotropy but cannot carry the dynamic
LOD bias.

Plan:

- Confirm the sampler state and LOD bias in the generated MSL and in `TerrainTexture.Sampled()`.
- Prototype only if the A/B shows sampler filtering is a real cost: a `constexpr sampler` with
  `filter::linear`, `mip_filter::linear`, `address::repeat`, `max_anisotropy(8)` and explicit
  `sample(..., level(...))` where necessary.
- Measure texture-filter counters and image quality. If it requires dropping the upscaler bias or changing terrain
  mip selection, keep the current sampler.

Result: not pursued. The anisotropy A/B was noise, so there is no evidence that sampler filtering is the cost.

## Direct MSL fallback

Only consider a direct MSL terrain fragment if one of the above needs a Metal feature that cannot be expressed
through GLSL/SPIR-V-Cross:

- function constants / specialization;
- a specific half or early-test attribute;
- a `constexpr sampler` with explicit LOD control.

Keep the shared GLSL for Vulkan. Add the MSL as a **Metal-only terrain program** that uses the same
`TerrainConstants` and bindless argument-buffer ABI; do not fork the C# terrain renderer. Gate it behind an env or
option, compare images and timings, and remove it if it does not clear the acceptance bar.

Do not start with a full MSL rewrite merely to use "Metal tricks". The current translated shader already uses
texture arrays, bindless argument buffers, explicit gradients, a depth prepass and late colour. The likely missing
wins are specialization, sampling and fill, not the language.

Result: not started. The measured no-normal saving is real but is a quality switch, and no default-path variant
cleared the noise floor. Revisit only with a matched capture that shows terrain is the shipping bottleneck.

## Acceptance

- Build 0 warnings / 0 errors; quick suite and Metal tests pass.
- Terrain GPU time down >=10% in the matched capture, or overall frame GPU down >=5%; no CPU/present regression.
- User confirms the picture on the normal Metal path; Vulkan remains visually unchanged.
- Revert or leave off any variant below the noise floor.
- Record the final measurement in `docs/metal-performance-memory.md` and update `HANDOFF.md`.

Result: the default-path gate was not met. The normal-maps-off variant is ~12% faster and is kept for that quality
switch; the default normal-on path is unchanged. The distance material LOD was measured and reverted. Build, quick
suite and Metal tests pass.

## Files

- `src/Meitou.Game/Rendering/Terrain/TerrainShaders.cs` — terrain GLSL and variants.
- `src/Meitou.Game/Rendering/Terrain/TerrainRenderer.cs` — programs, normal maps flag, draw paths.
- `src/Meitou.Game/Rendering/Terrain/TerrainTextures.cs` — layer arrays and samplers.
- `src/Meitou.Game/Rendering/Metal/MetalShaders.cs` — Metal library/compile options and program creation.
- `src/Meitou.Game/Rendering/Metal/MslTranslator.cs` — SPIR-V-Cross options.
- `tools/Meitou.ModelViewer/BenchSession.cs`, `WorldApp.TerrainAb.cs` — existing terrain A/Bs.
- `docs/render-terrain.md`, `docs/metal-performance-memory.md` — terrain and Metal performance notes.

Useful research starting points: Apple's "Optimize Metal Performance for Apple silicon", "Modern Rendering with
Metal", "Discover Metal 3" and the Metal sample code; read them for the specific features above, not as a reason to
rewrite the shader.
