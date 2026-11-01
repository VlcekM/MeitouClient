# Native renderer

Current API: `src/Meitou.Rendering/Gpu/` (Core/Shaders included), windowing in Rendering.Display.
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
GpuAllocator carves 64 MB blocks; a block goes back to the driver only when empty. Streamed world textures and impostor atlases (`TextureDesc.Streamed`)
have pools of their own, filled fullest block first; one streamed block under half used is drained at a time (`IsDraining`): the texture
caches move its images out (taken from another cache, or loaded again; an atlas is remade at its levels, one a second) and it is freed once empty (given up after 10 s without progress).
**Observed** (VRAM dump, 2026-11-01, after ~40 min of play): 1286 MB free scattered over 64 image blocks shared by streamed textures and
long-lived images, which pinned them.
PipelineLibrary caches explicit descriptions and records late creation (LatePipelines). `Prepare` has no caller: the loading warm-up draws what makes them, with the reflection and cloud passes forced on ([bench](bench.md) "Play benchmark").
CommandList records bindings/draw/dispatch/transfer/dynamic rendering, preserving host pass order.

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
The resources and the commands below them are the same question: [resource-layer](resource-layer.md).

BindlessTable has seven typed sampler arrays (BindlessKind bindings0..6). Per-slot descriptor
journal replays at begin/before submission; freed indices wait for consumers. Updating an index
changes every submitted draw using it, including already-recorded draws. Use new indices for
mid-frame binding changes. Tables/views/globals are render-thread state.

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
