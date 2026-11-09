# Metal Effects Parity I (MEP-I)

Status: **MEP-1, MEP-2, MEP-3 and MEP-5 accepted (2026-10-24); MEP-4 (tessellation/terrain relief) was evaluated and
removed, so it is not shipping.** MEP-I is not full Vulkan parity because Metal keeps the flat/parallax terrain path.

Starting point: `metal-spike` at `7eb04a96` (`docs: plan Metal Effects Parity I (MEP-I) milestones`), after the
effect-parity audit `84193e4a` and the upstream `57a7968a` merge. Read
[the effect-parity audit](../HANDOFF.md#effect-parity-audit-2026-10-24) for the measured/known gaps.

## MEP-1 implementation (2026-10-24)

The code is implemented and the MetalFX path is accepted in game (desert/dust/LOD views, 2026-10-24).

- **Particle coverage** is passed through `IWorldPostChain.Particles`: full-size quads write coverage, and
  `PostComposite.ParticleCoverage` upsamples the half/quarter-size accumulations. The low-res upsample uses the
  backend's depth convention, fixing Metal's 0..1 depth weights. **TAA only:** MetalFX gets no particle coverage at
  all. Full-size rain/dust in its reactive mask made distant objects shimmer, so both full- and low-resolution
  coverage stay in TAA's motion alpha (the MetalFX spatial path has no temporal mask and is unaffected).
- **Sun motion** moved into the shared `SunMotionTracker`, used by both `PostProcess` (Vulkan) and `PostTaa` (Metal),
  so Metal no longer passes zero sun uniforms.
- **TAA velocity depth** follows the scene depth convention (`DepthConvention.Glsl`): conventional 0..1 on Metal,
  reversed on Vulkan. It also flips the depth sample for Metal's top-down targets
  (`uFlipDepthY`) while keeping the projection NDC for the motion vector. This also makes the motion vectors and
  water reactivity use the correct view depth on Metal.
- **TAA reset** now includes a camera cut (>5000 units), and MetalFX gets the same reset.
- **MetalFX motion convention:** the scaler takes render-pixel motion in its y-down framebuffer convention. The
  velocity shader's NDC is y-up, so MetalFX's Y scale is now positive (X stays negative); the old negative Y scale
  mirrored temporal history vertically.
- **SMAA** was ported into `PostComposite` (edges, weights, blend; heat-haze and final-target ordering match Vulkan).
  Metal still has no separate anti-aliasing slider, so SMAA is reachable through `--smaa`/the saved options.
- **FogRate** is documented as a performance-only difference: Metal has no fragment-shading-rate attachment and
  renders fog/shafts at full rate. No no-op toggle was added.

**Acceptance:** the MetalFX temporal path was validated in the desert with distant terrain, dust and LOD transitions;
the visible corruption was traced to the MetalFX motion-vector Y convention. A later far-object shimmer was traced
to full-size particle coverage, so MetalFX no longer receives any particle mask; TAA keeps it. MetalFX spatial 0.4 is
clean. TAA remains labelled experimental until it gets a dedicated validation pass, and SMAA remains opt-in/no-temporal.

**Goal:** close the remaining visual effect gaps between the Vulkan reference and Metal so Metal can move from an
opt-in preview to a parity/default candidate. This iteration is about **visual effects**, not GPU culling, Hi-Z,
grass compute or general frame-rate work.

**Working rule:** Vulkan stays the reference and the default while MEP-I runs. Do not hide a missing effect behind a
Metal-only visual change; if a fallback is unavoidable, hide or label the setting and document the difference. The
experimental GPU foliage cull stays off and unexposed.

## Current baseline

- Release build: **0 warnings / 0 errors**.
- Non-Metal quick suite: **543 passed / 24 skipped / 1 known upstream failure** (`ItemIconRenderTests`, 0.964 clothing
  overlap against its 0.99 threshold; clean `origin/master` also reports 0.964).
- Metal upload tests are still excluded while the host's `MTLCompilerService` XPC is unhealthy.
- Metal smoke after the merge: 120 s `--new-game Wanderer`, 800x600, 8120 MB budget, 6,693 frames / 56 fps, no GPU
  timeout. A 30-minute soak and the full visual acceptance are still owed.
- The water, character, duel and still paths have been exercised; not every effect has a deterministic still yet.

## Milestones, in order

| ID | Milestone | Main outcome |
|---|---|---|
| **MEP-1** | **Post Effects Parity** | Particle coverage, sun-motion reprojection, SMAA/FogRate decisions and TAA/MetalFX stability |
| **MEP-2** | **Reflection Quality** | Reflection MSAA/resolve and sampling parity at water views |
| **MEP-3** | **Debris Lighting** | Full atmosphere lighting and haze for debris within Metal's sampler budget |
| **MEP-4** | **Tessellation & Terrain Relief** | evaluated and removed (2026-10-24): performance/fallback risk outweighed the benefit; not shipping |
| **MEP-5** | **Water Lighting Parity** | accepted 2026-10-24: native bindless water, full atmosphere path and foam shadows |

### MEP-1: Post Effects Parity

**Purpose:** make the temporal and anti-aliasing paths behave like Vulkan's, and remove no-op effect paths.

Work items:

- **Particle coverage in the reactive mask.** Vulkan's `PostProcess.MarkParticleDraws`/`ParticleRenderer.DrawCoverage`
  adds weather-particle coverage to the upscaler's reactive mask so rain and dust keep their strength under temporal
  history. `MetalPostChain.MarkParticleDraws`/`MarkParticles` are currently no-ops, and `PostTaa` has no coverage
  pass. Port the coverage pass to the seam chain and cover both MetalFX and TAA.
- **Sun-motion reprojection.** `PostTaa` currently sets `uSun`/`uSunMotion` to zero. Reuse Vulkan's
  `PostProcess.PrepareSunMotion` logic (or move it behind a shared helper) so moving clouds/sun do not ghost under
  temporal reconstruction.
- **SMAA decision.** Vulkan has three SMAA passes (`PostProcess.SmaaPasses`); `PostComposite` has no SMAA and the Metal
  UI does not expose it. Either port SMAA to `PostComposite`, or explicitly keep it Vulkan-only and document that the
  Metal anti-aliasing choices are FXAA/MetalFX/TAA. Do not add a visible setting that does nothing.
- **FogRate decision.** Vulkan uses a shading-rate target for fog/shafts (`FogRateImage`); Metal has no
  equivalent VRS attachment and `MetalPostChain.BuildShadingRate` is a no-op. Treat this as a **performance/quality
  difference**, not a missing visual pass: Metal renders the fog at full rate. Document it and do not claim parity for
  the VRS path. If a non-VRS fallback is added later, keep it behind a measured quality gate.
- **TAA stability.** The selectable Metal TAA path still ghosts on camera motion. Either fix its motion reprojection
  and object/particle terms, or keep TAA labelled experimental and make MetalFX Balanced the tested default. Do not
  make TAA the default while it ghosts.

Likely files:

- `src/Meitou.Game/Rendering/Metal/MetalPostChain.cs` (`MarkParticleDraws`, `MarkParticles`, `BuildShadingRate`, `RunGiResolve`,
  `End`)
- `src/Meitou.Game/Rendering/Post/PostTaa.cs` (sun motion, particle coverage, velocity)
- `src/Meitou.Game/Rendering/Post/PostComposite.cs` (SMAA if ported)
- `src/Meitou.Game/Rendering/Post/PostProcess.cs` and `PostProcess.Merged.cs` (reference behavior and shared helpers)
- `src/Meitou.Game/Rendering/Particles/ParticleRenderer.cs` (`DrawCoverage`, `HasCoverage`)

Acceptance:

- Rain, dust and moving clouds compare against Vulkan without new trailing/ghosting.
- MetalFX and TAA both run with the correct reactive-mask inputs; MetalFX remains the default if TAA is still
  experimental.
- No visible anti-aliasing or fog setting is a no-op on Metal.
- Build 0/0, quick non-Metal suite, fixed-scene Metal/Vulkan comparison, and a 120 s Metal run with weather particles.

### MEP-2: Reflection Quality

**Purpose:** finish the reflection pass's visual parity after the shared `ReflectionPass` was enabled on Metal.

Work items:

- **Done: MSAA/resolve.** `IGpuContext.MaxSampleCount` is the shared device capability; Vulkan keeps its
  colour+depth sample-count intersection and Metal asks `supportsTextureSampleCount` (2x/4x, 8x where available).
  `ReflectionPass` builds the same multisampled colour and depth targets on both backends, resolves colour into the
  sampled texture the water reads, and leaves the existing backend-deferred release on resize. The Metal MSAA targets
  use private storage; the reflection fog reads sample 0 of the multisample depth.
- **Done: level 2 characters.** The reflection's level 2 now draws `CharacterRenderer` (the game's queue 50) on both
  backends, after objects/foliage as in the main view. Levels 1-4 now mean terrain, characters, placed objects and
  foliage respectively; level 4 also contains characters and objects.
- **Done: settings and diagnostics.** `--reflection-samples <n>` selects the target sample count (1 disables MSAA;
  the device may cap it). The level and range sliders remain visible because both work. The reflection stats line
  reports single-sample versus the actual MSAA count.
- **Checked in code: memory/lifetime.** Targets are re-made when the size or requested sample count changes; the
  Metal backend defers their release through the frames in flight. Four-sample reflection colour+depth is a small
  fraction of the 8120 MB High budget at typical Retina sizes.
- **Accepted in game (2026-10-24):** water edges/horizons, level-2 characters, levels 1-4, resize/alt-tab and the
  MSAA A/B passed the user's visual check.

Likely files:

- `src/Meitou.Game/Rendering/Frame/ReflectionPass.cs`
- `src/Meitou.Game/Rendering/Frame/WorldFrame.cs`
- `src/Meitou.Game/Rendering/Metal/MetalContext.cs` and `src/Meitou.Game/Rendering/Gpu/{IGpuContext,GpuContext}.cs`
- `src/Meitou.Game/Rendering/Characters/CharacterRenderer*.cs`

Acceptance:

- Reflection levels 0-4 behave and look equivalent at the sea, river and Hub water views.
- No reflection aliasing regression versus Vulkan's reference path.
- Resize, alt-tab, night and rain views stay stable.
- Build/tests and side-by-side stills at a fixed camera/time.

### MEP-3: Debris Lighting

**Purpose:** replace Metal's simple debris lighting with Vulkan's atmosphere-lit and hazed path.

**Accepted in game (2026-10-24).** `DebrisRenderer` is now a native-model program on both backends: the
same `kenshiLight`/`atmoApply` fragment Vulkan used, with every frame and material texture read through the bindless
table. The debris diffuse map gets a stable bindless index per loaded texture; the model matrix, simple-sky fallback
lighting and per-part cut-out state travel in one 128-byte push block. Metal no longer has a debris-specific lighting
approximation.

The sampler-budget helper is `src/Meitou.Game/Rendering/Gpu/Shaders/SamplerBudget.cs`: it counts a stage's direct samplers
outside the native bindless set and fails with the shader's name instead of an opaque Metal pipeline error. Debris and
water use it at program creation; both have 0 direct fragment samplers.

The Hub/desert gameplay check passed. Bone/limb visibility was not separately exercised because the test character
died before losing a limb; luckyren Mode remains the quickest explicit debris check if it is revisited. **MEP-3 is
the preview gate:** a public test may ship as an explicitly labelled Metal preview, with MEP-4 listed as a known
limitation.

### MEP-4: Tessellation & Terrain Relief (evaluated, not shipped)

**Purpose:** bring Vulkan's tessellation-dependent terrain effects to Metal.

**Result (2026-10-24): removed from the runtime.** Metal reports `HasTessellation == false`, so the flat/parallax
path, the hidden tessellation toggles and the existing terrain material remain. Ground displacement, rock smoothing
and terrain-outline refinement are a **known Metal limitation**.

What was tried and why it was rejected:

- Metal has no tessellation control stage. A first prototype wrote each three-control-point patch's edge/inside half
  factors on the CPU; Metal's post-tessellation vertex stage read the raw control points through per-patch-control-point
  vertex descriptors. That path was **~3 fps** in the loaded Hub view: ~103 ms/frame of terrain height sampling plus
  ~25 ms/frame for rocks.
- Moving both factor kernels to a Metal GPU compute pre-pass removed the CPU cost and reached **~35 fps** at 800x600
  points (1600×1200 pixels), versus the ~54 fps MEP-3 baseline in that scene. Tessellation itself still adds geometry
  and GPU work; the drop was not acceptable.
- The deferred-colour path then exposed `NotSupportedException: no Metal primitive for PatchList` when a factor
  allocation failed and the job fell back to an indexed draw while the program still had patch topology. A safe
  per-patch flat fallback, factor-allocation failure handling and a full visual/performance validation would still be
  required.
- The code was removed rather than left behind a disabled flag. The UI must keep hiding the tessellation toggles.

If this is revisited, it needs a hard Metal performance budget, near-range tessellation/quality caps, a per-patch
flat fallback (including factor-buffer allocation failure) and matched visual/soak validation. Do not re-enable it
as a hidden default or a visible no-op.

### MEP-5: Water Lighting Parity (accepted)

**Purpose:** remove the `METAL_WATER` lighting compromise and finish water parity.

The water now uses the native model on both backends: one `WaterConstants` block per segment and every water texture
(including heights, ocean/shore/river maps, reflection and refraction) in the bindless table. The fragment and vertex
programs therefore have **0 direct samplers**, and the full `AtmosphereShaders.Functions` path is compiled instead of
the `METAL_WATER` subset. The shared water body still uses Meitou's own water formula (Vulkan's reference); the
restored atmosphere path supplies the cloud-shadow tile and `kenshiShadow` for foam. `MetalMeitouFragment`,
`BindMetalSamplers` and the two-layer parameter packing are gone. Reflection/refraction and the MEP-2 MSAA decision are
unchanged, and `SamplerBudget.Require` checks the native water programs at creation.

**Acceptance (2026-10-24):** the user accepted the in-game swamp and desert water; build 0/0; quick suite 424 passed,
2 skipped; Metal screenshot runs compiled and rendered both water views without a shader or GPU error. The 30-minute
post-merge soak remains a separate gate. MEP-5 does not restore full Vulkan parity on its own because MEP-4
tessellation is not shipping; terrain displacement/rock smoothing/outline refinement remain documented limitations.

## Cross-cutting requirements

- **No Metal-only behavior.** If a Vulkan effect cannot be ported, hide the setting and document it as a known
  limitation rather than silently substituting a different look.
- **Vulkan reference.** Run a Vulkan comparison for every effect milestone; two baselines are allowed because the
  temporal path is noisy. Compare regions/means, not bytes.
- **Temporal comparisons.** Use the same camera, time, weather and simulation ticks; first compare at native/no
  temporal, then with MetalFX/TAA. Cloud/water time still follows real time on Metal, so stills are not byte-comparable.
- **Memory.** Keep the 5120/6144/8120 MB soft budget behavior; water remains a large consumer, but MEP-5 adds no
  per-frame map upload. Do not raise the default to hide leaks.
- **GPU cull.** Leave the experimental GPU foliage cull off and hidden. It is not part of this iteration.
- **Visual acceptance.** The user validates visual changes manually; an automated build/test pass is not visual
  acceptance.
- **Upstream.** Rebase/merge `origin/master` before closing MEP-I; the branch is `metal-spike`.

## Preview and parity gates

| Gate | Requirement |
|---|---|
| **Preview test** | After MEP-3: build 0/0, non-Metal tests, Metal smoke, no GPU timeout, known gaps documented; Vulkan remains default |
| **Remaining effects gate** | After MEP-5: MEP-1..3 and MEP-5 accepted, MEP-4 recorded as evaluated/removed, Vulkan comparisons, 30-minute soak, no hidden no-op settings. Not full parity while tessellation is absent |
| **Default switch** | Only after a separate performance/stability decision and an explicit decision on the tessellation limitation; not part of MEP-I |

## Validation commands

Metal (do **not** set `DYLD_LIBRARY_PATH`):

```bash
dotnet build MeitouClient.slnx -c Release --no-restore
dotnet run --project src/Meitou.Game --no-build -c Release -- \
  --renderer metal --windowed --size 800x600 --new-game Wanderer --quit-after 120
```

Vulkan reference:

```bash
DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet run --project src/Meitou.Game --no-build -c Release -- \
  --renderer vulkan --windowed --size 800x600 --new-game Wanderer --quit-after 20
```

Non-Metal quick suite (known upstream item-icon failure):

```bash
DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet test tests/Meitou.Tests/Meitou.Tests.csproj -c Release --no-build \
  --filter 'FullyQualifiedName!~Metal'
```

For effect comparisons, use fixed camera/time/weather and the water views in
[metal-backend](metal-backend.md#resume-notes-facts-already-established). Keep `MTL_DEBUG_LAYER=1` for validation only,
not performance numbers.

## Risks and blockers

- **Metal sampler limit:** debris and water use the native bindless table and have 0 direct fragment samplers.
  `SamplerBudget.Require` guards both at program creation.
- **Tessellation:** the largest engineering risk; isolate it and stop the milestone if the Metal pipeline path cannot be
  made stable.
- **TAA/MetalFX:** temporal artifacts are hard to compare; use native/no-temporal baselines first.
- **MTLCompilerService XPC:** the full Metal test suite may remain unavailable until a full shutdown/power-on; do not
  treat that as a code failure.
- **Memory/stability:** run the 30-minute soak again after the merge before public shipping; the 120 s smoke is only
  a smoke.
- **Existing no-op/quality issues:** TAA ghosting, FogRate/VRS and Hi-Z are documented; do not accidentally ship them as
  “done” without a decision.

## Immediate next action

MEP-4 is **closed as not shipping**; keep the flat/parallax fallback and the hidden tessellation toggles. MEP-5 is
accepted. The MEP-I effects work is complete apart from the tessellation limitation; the 30-minute post-merge soak and
the separate performance/default-switch decision remain. Keep the experimental GPU cull off.
