# Native renderer

Current API: `src/Meitou.Game/Rendering/Gpu/` (Core/Shaders included), windowing in `Rendering/Display/`.
Vulkan only; engine contracts below are checked against the named source, not game-format facts.
Feature algorithms: render-* docs/[index](README.md).

## Device, frame and lifetime

GpuContext owns device/libraries/bindless/uploads/frame/globals. GpuFeatures.Require needs
bindless descriptor indexing, multiDrawIndirect/drawIndirectFirstInstance. VulkanDevice requires
Vulkan1.3/dynamic-rendering/synchronization2/timeline; check optional capabilities before use.
FrameLoop: two default slots, pre-frame primary (uploads/culls/bakes) then draw primary, full
barriers around pre-frame. Reuse/retire only after GPU completion. Updating uploads while
recording affects the whole submitted frame; never overwrite a used DeviceBuffer range.

Buffers/Textures/Samplers define owned/borrowed resource bindings. FrameScratch per-slot bump
allocation tracks120-frame maximum+25%headroom, shrinks at>2x need; Cap overflow fails and caller
handles it. Staging can shed unused chunks; descriptor-referenced constants retain storage.
GpuAllocator carves 64 MB blocks; a block goes back to the driver only when empty. Streamed textures (`TextureDesc.Streamed`: world
textures, impostor atlases) never share blocks with long-lived images: each is made on a worker (`CreateTextureOffThread`: the world texture
caches' decode, `ImpostorTextures.MakeOffThread`), so new memory costs no frame; one of 4 MB or more gets memory of its own, freed the moment it is unloaded; the rest take slots in pools of one size each
(rounded to 64 KB, blocks of about 16 MB, filled fullest first, given back once empty), so a freed slot fits the next texture of its size.
Texture sizes cluster (2026-11-01 town: 541 world textures in 15 sizes, 216 atlas maps in 8). **Observed** (VRAM dump, 2026-11-01, ~40 min
of play): with variable-size carving in shared 64 MB blocks, 1286 MB was free but scattered over 64 blocks that long-lived images pinned.
PipelineLibrary caches explicit descriptions and records late creation (LatePipelines). `Prepare` has no caller: the loading warm-up draws what makes them, with the reflection and cloud passes forced on ([bench](bench.md) "Play benchmark").
CommandList records bindings/draw/dispatch/transfer/dynamic rendering, preserving host pass order.

Lost device: submissions, fence waits, acquire and present go through `VulkanDevice.Check`, which writes `DeviceFault`'s report to the log once
before the call throws (`vulkan    device lost at ...`: frames, video memory, then VK_EXT_device_fault's description, fault addresses and vendor
codes where the device has the extension). `MEITOU_DIAG_FAULT=1` adds what the addresses need to be read: `GpuAddressLog` (the ranges the driver
reports bound and unbound, VK_EXT_device_address_binding_report, plus the allocator's placement of each image and buffer in its block; the last
16384 unbound of each are kept) and `PassMarkers` (a host-visible word filled when the pre-frame buffer and each native segment begins, another
when it ends: per frame slot, the pass the GPU began and finished last).

## Synchronization and shaders

The engine's words for the synchronization: `Access` says how a pass uses a resource, `PassUse` says
which resource, and `GpuDependency` says what must be ordered against what. The backend turns them
into its own barriers (Vulkan: merged synchronization2 dependencies; Metal: none - Apple silicon
tracks the hazards). The renderers that know nothing of each other's resources order everything
against everything (`cmd.Order()`); the ones that do declare their uses (`Sync.PassUse`/`ResourceStates`
track whole-resource identity, not independent subresource histories: read/read no barrier, write→access
requires visibility, read→write execution ordering; `GpuPass` applies the merged dependencies and the
label immediately, no pass reordering). Repeated upload writes require transfer ordering;
draw-buffer blits/mips/readback follow command order.

Own GLSL through bundled shaderc; GlslProgramCompiler legacy cache3/native4, stage interface
locations and SPIR-V binding shifts. LegacyProgram set0 named blocks/samplers pushed, set1 dynamic
loose uniforms; resolve handles ahead. Native program layouts come from reflection, not one
assumed global set scheme. FrameGlobals publishes render-thread getters, Version invalidates
resolution, ApplyCount permits within-application caching; jobs consume prepared values.

The renderers state their draw state, their formats and their resources in the GL vocabulary, not
in Vulkan's: DepthFunction, BlendingFactor, FrontFaceDirection, CullMode, PolygonKind,
BlendOperation, ColorMask, PrimitiveType, PixelViewport, PixelRect, InternalFormat, LoadOp,
ClearColour, IndexKind, TextureMinFilter, TextureMagFilter, TextureWrapMode. GlConventions maps
them to Vulkan (the front face flips: GL's counter-clockwise is Vulkan's clockwise; GL's 24-bit
depth is a 32-bit float). A second backend maps them to itself.
Depth: clip depth is 0..1 on both backends (no `VK_EXT_depth_clip_control`, no vertex remap); the stored depth is z/w. The scene
targets (scene depth, reflection, hover mask, markers, the particle and water depth copies derived from the scene's) are one
reversed 32-bit float buffer on Vulkan: cleared to 0, near plane 1, far plane (the view distance) 0, compared GREATER/GEQUAL.
`DepthConvention` is the one place for the clear, the compare, the projection (`Perspective`, `Apply`), the wireframe bias
(`TowardViewer`) and the GLSL helper (`Glsl`: `depthIsSky`, `depthViewZ`, `depthOf`, `depthStep`, `DEPTH_SKY`/`DEPTH_NEAREST`, selected
by the `MEITOU_DEPTH_REVERSED` define the Vulkan compiler adds). Renderers keep stating GL compare semantics; `GlConventions` maps them
literally. Shadow maps, previews, icons and bakes stay conventional (clear 1, Less). Metal is conventional (`DepthConvention.Reversed`
false). View-depth step of one float ulp, derived: reversed `1.2e-7 z` at any distance; conventional `1.2e-7 z^2 / near`.
The scene's near clip plane is `WorldCamera.ClipNear` = `n f / (2 f - n)` (about half of `Near`): **Verified** (swamp-low screenshot,
master against the branch, mean difference 4.0 -> 0.04) that the former -1..1 clip volume over a 0..1 depth matrix drew down to there, and a
near chimney vanished when the reversed scene clipped at `Near`. Metal (always 0..1) keeps `Near`. `Near` stays the rule for the shadows'
fit and the close particles.
The resources and the commands below them are the same question: [resource-layer](resource-layer.md).

BindlessTable has seven typed sampler arrays (BindlessKind bindings0..6). Per-slot descriptor
journal replays at begin/before submission; freed indices wait for consumers. Updating an index
changes every submitted draw using it, including already-recorded draws. Use new indices for
mid-frame binding changes. Tables/views/globals are render-thread state.
`ScalarOf` lists the integer `InternalFormat`s (unsigned or signed samplers); all others sample as
float. Verified by `BindlessTests.Integer_textures_read_back_exact_values_through_the_bindless_arrays`
(integer routing and texels).

## Parallel recording and validation

Recording.Mode0 inline,1 serial secondary,2 default worker secondary. ParallelPass executes
insertion order; workers require>1 job and>=32 draws. RecordJobs cannot allocate/upload or
access mutable render-thread state; InJob guards even inline. Prepare constants/bindings first.
GlConventions keeps bottom-up images; presentation flips once, shader clip/front-face/upscaler
matrices agree. QueryArena reads completed slot timestamps without waits; GPU frame time includes
pre-frame. Parallel CPU stage sums are work, not elapsed duration.
Parity scripts compare10 Faithful views; repeat baseline first, pixel identity for unchanged
output, temporal/FSR baseline noise for nondeterministic output. Synchronization validation checks
resource/barrier/descriptor changes. [Benchmarking](bench.md). Logs/screenshots stay outside repo.
