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

A fragment stage's `discard` is compiled as a demote on Vulkan (`ShaderCompileOptions.DemoteDiscard`, set where the device has
`shaderDemoteToHelperInvocation`, core in 1.3; `GlslProgramCompiler.OptionLines` adds the extension and `#define discard demote`): the
fragment is dropped and its invocation runs on as a helper, so derivatives and implicit-LOD lookups after it stay defined in the 2x2 quad.
Metal keeps `discard`.
- **Verified** (opcode scan of the shader cache, 2026-11-14): the bundled shaderc (Silk.NET.Shaderc.Native 2.23.0, Vulkan 1.3 target) turns a
  plain `discard` into `OpTerminateInvocation`, after which derivatives are undefined; with the option a Hub run's 241 new modules hold
  `OpDemoteToHelperInvocation` (44 of them) and neither `OpTerminateInvocation` nor `OpKill`.
- **Verified** (validation layer, `--headless --no-gi`): the modules need the device feature (VUID-VkShaderModuleCreateInfo-pCode-08740
  without it, 0 errors with it). The `StorageImageExtendedFormats` capability needs no enabled feature.
- **Verified** (`--screenshot --new-game --ticks 60`, RTX 4070): demote against terminate differs in 102 of 1228800 pixels (largest step
  32/255), at dithered objects' edges; two runs of one build are identical.

### Vertex bindings

Each vertex attribute is a binding of its own: binding = location, the attribute at offset 0 of it, the stride the vertex's, and the buffer bound
at the attribute's offset in the vertex (`Pipelines.FillVertexInputs`, `LegacyProgram.Attribute`). A binding other than the first therefore
has less than a whole stride left for the buffer's last vertex. Every buffer with the vertex usage is made 256 bytes longer than asked
(`GpuAllocator.VertexTailBytes`, at least the largest stride, 64), so the last vertex has a whole stride in every binding.
- **Verified** (Vulkan spec, "Shader Out-of-Bounds Memory Access"): with robust buffer access a vertex input read "may instead be bounds
  checked against a range rounded down to the nearest multiple of the stride of its binding", and one read outside lets the binding's others
  behave as outside too. The device does not enable `robustBufferAccess`; what a driver does without it is its own.
- **Verified** (`--screenshot` of #661's save and camera, RTX 4070, a scratch build zeroing bytes 12-43 of each object mesh's last vertex):
  the picture has the report's dark, texture-smeared triangles in the same places (the half of each `carpet1.mesh` quad that uses its last
  vertex). With the tail and without the zeroing the picture is pixel-identical to the one before, and the
  validation layer reports 0 errors.

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

### Shader overrides

Debug aid (`ShaderOverrides`): with `MEITOU_SHADER_DIR=<dir>` every GLSL source given to the compiler is written once to
`<dir>/<hash>.<stage>.glsl` (first 16 hex digits of its SHA-256; stages `vertex`, `fragment`, `compute`, `tess-control`, `tess-evaluation`),
and a file there whose text differs from the source is compiled instead, before the SPIR-V caches are keyed. Grep the folder for a
function, edit the file, start the game again: no rebuild. A changed C# source gets a new hash (a new file); delete the folder to start clean.

## Render origin

Every scene pass projects eye-relative positions: a vertex program forms `position - uEye` (exact near the eye) and multiplies it by a view-projection without
the eye's translation, `RelativeRotation * projection` (`WorldFrame.Draw`; the eye formed in double, see [character-renderer.md "Precision"](character-renderer.md)),
never by `CreateTranslation(eye) * worldViewProjection` or a world view-projection, whose float translation row cancels numbers of 50000 and leaves errors of a
float step (0.004 units at 50000). `RenderOrigin.Relative` rewrites a legacy program's `uViewProjection * vec4(p, 1.0)` that way (terrain, water, impostors);
foliage, grass, objects, characters, debris, particles and the overlays form it themselves. A pass without the precise matrix (shadow cascades, reflection,
bakes, previews) takes `RenderOrigin.ViewProjection(vp, eye)`, the same convention at float precision. Shading keeps world positions. `WorldCamera.View` is
built from the angles at the origin, then translated: a look-at from the float eye turned the view at each eye step, and one at `eye + forward` rounds that
point to the eye's float step (a turn of about 2 px at 50000).
**Verified** with the viewer (`--at -47700,47500 --distance 15 --orbit-step 0.002 --sequence 24`, swamp grass, no AA, frame-to-frame mean difference):
before, the picture stood still and jumped 0.4-0.7 every 7-9 frames; after, 0.39-0.41 every frame. At 0.02 degrees a frame the per-frame image shift's
jitter fell from 0.16 to 0.08 px near and from 0.06 to 0.02 px far.

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
