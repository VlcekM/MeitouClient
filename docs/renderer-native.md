# Native renderer API (proposal)

**Status: Proposed** ([../DECISIONS.md](../DECISIONS.md) 22, which would replace 7). Nothing here is built yet. This is the design that the
wave-2 foundation agent and the six wave-3 port agents implement from, and the owner reviews. It is an engine document: the labels
Verified / Observed / Unknown are for facts about the game, so here claims carry where they come from: *from the code* (a file is named),
*measured* (how is said), *estimate* (with the reasoning), or *open* (a question for the owner, collected in [Open questions](#open-questions)).

How to read it. Each section opens with a short plain-language summary. The details under it are for the agents. The order follows
the brief: goals (1), the API (2), shaders (3), how old and new code share a frame while the port runs (4), GPU-driven foliage and objects
(5), threads (6), who does what (7), removing the old layer (8), and what it should save (9).

---

## 0. Where we are, in one page

The world renderers (`src/Meitou.Rendering`) never talk to Vulkan. They make OpenGL-3.3-style calls through `IGl`
(`src/Meitou.Rendering/Gpu/IGl.cs`), and `VkGl` (`src/Meitou.Rendering.Vulkan/VkGl*.cs`, nine files, 2,806 lines) translates each call
into Vulkan as it arrives. DECISIONS 7 chose this so the renderers did not need rewriting, and DECISIONS 18 kept it when OpenGL was removed.

The price is CPU time. At every draw `VkGl` rebuilds what the draw needs from tracked GL state (`VkGl.PrepareDraw` in `VkGl.Draw.cs`): it packs
the vertex layout into a 20-field `PipelineKey`, looks it up in a dictionary, re-sends the changed dynamic state, copies each dirty
loose-uniform block into a ring, pushes the descriptor set, and binds a vertex buffer for every input location. The renderers add their own
GL-shaped cost on top: uniforms looked up by `(program, name)` dictionary keys, one call per uniform, four `VertexAttribPointer` calls per
instanced draw. docs/viewer.md ("Shadow pass cost") measured about 4 µs per draw in real frames. The owner's dense-forest numbers are
foliage 8.7 ms and shadows 6.1 ms of CPU against 7 ms of GPU, so the frame is CPU-bound.

The plan, which the owner approved in waves:

| Wave | Who | What |
| --- | --- | --- |
| 2 | one foundation agent | the native API (`src/Meitou.Rendering/Gpu/`), the seam that lets old and new code share a frame, the TERRAIN-mode mesh path as a pilot, tests and tools |
| 3a | six agents in parallel (A to F) | **parity ports**: each renderer records Vulkan directly with the *same* SPIR-V; 0 differing pixels |
| 3b | the same agents | **native model** (frame/view constant buffers, push constants, bindless materials) and GPU-driven foliage (A) and objects (C, optional) |
| 4 | one agent | multithreaded command recording |
| phase 8 | one agent | delete `IGl`, `VkGl` and the GL-shaped helpers |

---

## 1. Goals, non-goals, constraints

*In short: make drawing cheap enough on the CPU that foliage, junk and objects can be drawn far out, without changing a single pixel of
the Faithful picture while the work happens. Stay on Vulkan 1.3 and Silk.NET, keep our GLSL, and ship no vendor SDK.*

### Goals

1. **CPU cost per draw down by an order of magnitude**: from ~2-4 µs (VkGl) to ~0.1-0.3 µs (measured on the same draws: section 9).
2. **GPU-driven foliage and objects**: the visible set, the LOD and the draw arguments of tens of thousands of instances are computed on the
   GPU per view (main slices, reflection, each shadow cascade), so CPU cost no longer grows with instance count. That is what makes
   draw distances of several thousand units for small things (junk, rocks, bushes) affordable.
3. **Pixel parity during the migration**: every port passes the gate in [section 7](#77-the-parity-gate-procedure): 0 differing pixels
   against the build before it, in the ten views of `tools/scripts/parity.sh` with `--faithful all`.
4. **Ports in parallel**: six agents work at the same time on separate renderers, with the rest of the frame still on VkGl.
5. **Multithreaded recording** (wave 4) and then **no GL-shaped layer at all** (phase 8).
6. **Profiling keeps working**: `StageClock`, `FrameProfiler` and the `--fly-benchmark` lines report the same stages throughout.

### Non-goals

- No new rendering features in wave 3a. Size-based ranges, impostors and occlusion culling are Meitou-mode features of wave 3b and later,
  behind `Enhancement` switches (section 5.7).
- No second backend. Vulkan stays the only one (DECISIONS 18). The API is Vulkan-shaped, not an abstraction over several APIs.
- No render graph that reorders, merges or culls passes. Order stays exactly what the code says, because order is part of the picture
  (depth ties).
- No mesh shaders, ray tracing or work graphs.
- No change to the frames-in-flight count (2, `VulkanDeviceOptions.FramesInFlight`) or to the upload model's semantics in wave 3.

### Constraints

**Platform.** .NET 10 (`global.json`, SDK 10.0.401 here), Silk.NET 2.23.0 (`Meitou.Rendering.Vulkan.csproj`), warnings are errors, unsafe
code allowed (`Directory.Build.props`). Tiered compilation and PGO are off in the game and the viewer (DECISIONS 11, 19). That matters
for the native API: hot paths are compiled once and optimised, so small wrapper structs and `[MethodImpl(AggressiveInlining)]` work as
intended.

**Vulkan 1.3**, as today (`VulkanDevice.ScoreDevice` rejects anything below 1.3 or without dynamic rendering, synchronization2 and timeline
semaphores). What the device enables today (from `VulkanDevice.cs`, the feature structs after "Turn off everything we did not ask for"):

| Already on | Notes |
| --- | --- |
| dynamic rendering, synchronization2, timeline semaphores | required |
| host query reset, scalar block layout, uniform buffer standard layout | for VkGl's queries and glslang's loose-uniform blocks |
| extended dynamic state (core), EDS2/EDS3 sub-features, depth clip control, vertex input dynamic state, push descriptors (KHR) | where present |
| fillModeNonSolid, depthClamp, samplerAnisotropy, textureCompressionBC, independentBlend, imageCubeArray, shaderInt16, formatless storage images | core 1.0 features |
| shaderFloat16, 16-bit storage, subgroup size control, private data | for FSR's and NGX's shaders |
| `descriptorIndexing`, `bufferDeviceAddress` | **only when Streamline asks** (`Wants12`) |

What the native path needs on top (the foundation turns them on and checks them at device creation with a clear error):

| Needed | Why | Where it is defined |
| --- | --- | --- |
| `multiDrawIndirect`, `drawIndirectFirstInstance` | indirect draws whose arguments the GPU writes, `firstInstance` pointing into a compacted instance buffer | Vulkan 1.0 optional features |
| `drawIndirectCount` | draw counts written by the GPU (wave 3b, once draws share mesh buffers) | Vulkan 1.2 feature |
| `shaderDrawParameters` | `gl_DrawID` / `gl_BaseInstance` in multi-draw shaders (wave 3b) | Vulkan 1.1 feature |
| `descriptorIndexing` with `runtimeDescriptorArray`, `descriptorBindingPartiallyBound`, `descriptorBindingVariableDescriptorCount`, `descriptorBindingSampledImageUpdateAfterBind`, `descriptorBindingUpdateUnusedWhilePending`, `shaderSampledImageArrayNonUniformIndexing` | bindless textures (section 2.6) | Vulkan 1.2 features |
| `VK_EXT_debug_utils` whenever available, not only with validation | pass labels in RenderDoc and Nsight (today it is enabled only together with the validation layer: `debugExt = layer && ...`) | instance extension |

`bufferDeviceAddress` is **not** needed: storage buffers bound as descriptors cover every use here. That keeps clear of the extension-versus-core
issue DECISIONS 17 had to work around for Streamline.

**GPU support (estimate, open question 1).** The added features are present on every desktop GPU we expect to run Kenshi with a Vulkan 1.3
driver (NVIDIA since Maxwell, AMD since GCN on current drivers, Intel Xe, the Steam Deck's RADV). This has not been checked against a
driver database. Only the RTX 4070 in this machine has been tried. A device without them would fail at start with a message.
Keeping a push-descriptor fallback for such devices is possible, at the price of a second descriptor path.

**Shaders.** Our own GLSL 3.30 sources (`*Shaders.cs`) stay the source of truth, compiled by `GlslProgramCompiler` with the shaderc it ships
(DECISIONS 20). Kenshi's HLSL stays a reference for facts only (ROADMAP "Technology choices").

**No SDK DLLs shipped.** Nothing of the Vulkan SDK, Streamline or FidelityFX goes into the repository or the build output, as today
(DECISIONS 16, 17). The validation layer is a developer tool from the Vulkan SDK (installed here: 1.4.363).

---

## 2. The API surface

*In short: a thin, Vulkan-shaped C# layer. Renderers create their GPU objects once at load (buffers, textures, pipelines) and record each
frame with a command list that does exactly what it is told. No hidden state tracking, no lookups by name while drawing, no lazy pipeline
creation. It lives in `Meitou.Rendering` itself (folder `Gpu/`), which takes the Vulkan reference, so the renderers can use it without a dependency cycle and without a new project.*

### 2.1 Project layout

Today `Meitou.Rendering` has no Vulkan reference, and `Meitou.Rendering.Vulkan` references `Meitou.Rendering`, because it implements `IGl`
and the upscaler interface `IUpscaler` (`Upscaling.cs`). Renderers cannot reference the Vulkan project without a cycle. The proposal:

```
Meitou.Rendering (renderers + Gpu/: native API, Core, Shaders; Silk.NET; also Meitou.Core, Meitou.Data) ← Meitou.Rendering.Vulkan (VkGl, presenter, upscalers) ← Display / Game / viewer
```

- **`src/Meitou.Rendering/Gpu/`** (the folder that holds `IGl` today; the project gains Silk.NET.Vulkan, .Extensions.EXT/KHR, .Shaderc): `Core/` and `Shaders/` **move** here from
  `Meitou.Rendering.Vulkan` with their namespaces unchanged for now (`Meitou.Rendering.Vulkan.Core`, `...Shaders`), so `VkGl` and the tests
  compile untouched. The native API is added under the namespace `Meitou.Rendering.Gpu`. The namespaces are renamed in phase 8. Owner decision (2026-10-06): no new project; a folder rule replaces the project boundary (renderers use the public native API, not `Core/` internals).
- **`Meitou.Rendering`** gains the Silk.NET Vulkan references. Renderers use Silk.NET's enums (`Format`, `CompareOp`, `CullModeFlags`,
  `PipelineStageFlags2`...) directly. There is one backend, so duplicating them would only add a translation step.
- **`Meitou.Rendering.Vulkan`** keeps `VkGl`, `VulkanPresenter` and the upscalers until phase 8 (section 8).

### 2.2 Device, queues, frame

```csharp
namespace Meitou.Rendering.Gpu;

/// One per device. Owns what every renderer shares. Created by VulkanDisplay next to VkGl, from the same VulkanDevice.
public sealed class GpuContext : IDisposable
{
    public VulkanDevice Device { get; }          // moved Core/VulkanDevice: queues, allocator, frame ring, pipeline cache
    public GpuFeatures Features { get; }         // what section 1 requires, checked at creation
    public ShaderLibrary Shaders { get; }        // section 3
    public PipelineLibrary Pipelines { get; }    // 2.5
    public BindlessTable Bindless { get; }       // 2.6
    public SamplerCache Samplers { get; }        // 2.4, the same rules VkGl uses
    public Uploader Uploads { get; }             // 2.3
    public GpuFrame Frame { get; }               // the frame being recorded (one at a time)
    public DrawLog? Log { get; set; }            // 7.6, null unless a tool turns it on
}

/// The frame in flight being recorded. Begun and ended by the host. While VkGl exists, VkGl.BeginFrame/EndFrame drive it (section 4).
public sealed class GpuFrame
{
    public long Number { get; }                  // FrameRing.FrameNumber
    public int Slot { get; }                     // FrameRing.Slot
    public CommandList Commands { get; }         // the frame's primary command buffer (wave 4: per-thread lists, section 6)
    public LinearAllocator Constants { get; }    // host-visible, reset when the slot comes round
    public QueryArena Timestamps { get; }        // 2.10
    public ResourceStates States { get; }        // 2.9
}
```

- **Queues.** The graphics queue does everything, as today. `VulkanDevice` already finds a dedicated transfer family
  (`HasDedicatedTransfer`). Moving streaming uploads onto it is optional work for the foundation (open question 8). It needs either
  concurrent sharing on streamed resources or queue-family ownership transfers, and a timeline-semaphore wait in the frame's submit.
- **Frame ring.** `FrameRing` stays as it is: 2 slots, a fence per slot, deferred deletion (`DeferDelete` is already thread-safe). The
  native per-frame state hangs off the slot.
- **Submission.** Only the render thread submits, under `VulkanDevice.QueueLock`, as today.

### 2.3 Buffers

```csharp
public enum BufferUse { Vertex = 1, Index = 2, Uniform = 4, Storage = 8, Indirect = 16, TransferSrc = 32, TransferDst = 64 }

/// Device-local, written through the Uploader (static meshes, persistent instance stores, indirect argument buffers).
public sealed class DeviceBuffer : IDisposable { public ulong Size { get; } public Buffer Handle { get; } /* GpuBuffer underneath */ }

/// A slice of host-visible per-frame memory (constants, CPU-written instance data, CPU-written indirect arguments). Valid until the
/// slot comes round again. Allocation is a pointer bump: no lock, one allocator per recording thread (section 6).
public readonly unsafe struct Transient { public Buffer Handle { get; } public ulong Offset { get; } public byte* Pointer { get; } public ulong Size { get; } }
public sealed class LinearAllocator { public Transient Allocate(ulong size, ulong alignment); public Transient Write<T>(ReadOnlySpan<T> data) where T : unmanaged; }

/// A device-local buffer suballocated in ranges that live as long as their owner (a foliage zone's instances, an object page): first-fit
/// with coalescing, frees deferred to the frame fence. The GPU-driven paths keep instance data here (section 5).
public sealed class BufferArena { public ArenaRange Allocate(ulong size, ulong alignment); public void Free(ArenaRange range); public DeviceBuffer Buffer { get; } }

public sealed class Uploader
{
    /// Copies into a device buffer or texture through the frame's upload command buffer, which is submitted ahead of the frame's own,
    /// exactly as VkGl's uploads are (VkGl.cs: uploadCmd, "before: uploadCmd"). An upload is therefore seen by the whole frame.
    public void Write(DeviceBuffer target, ulong offset, ReadOnlySpan<byte> data);
    public void Write(Texture target, int level, int layer, in Rect region, ReadOnlySpan<byte> data);
}
```

Writing a buffer the frame has already drawn from is a bug in the native API. `VkGl` renames such a buffer (`VkGl.Buffers.cs`, `Write`); the
native API asserts instead. Data that changes per frame belongs in a `Transient`. This is the main behavioural difference port agents must
handle (section 7.8, risk 9).

### 2.4 Images, targets, samplers

```csharp
public enum TextureUse { Sampled = 1, Storage = 2, ColourTarget = 4, DepthTarget = 8, TransferSrc = 16, TransferDst = 32 }
public sealed record TextureDesc(Format Format, int Width, int Height, int Levels = 1, int Layers = 1, int Samples = 1,
    TextureKind Kind = TextureKind.Texture2D, TextureUse Use = TextureUse.Sampled | TextureUse.TransferDst, string Name = "");

/// An image in GENERAL layout (section 4.4) with a default view. Views of other level/layer ranges or swizzles are cached on it.
public sealed class Texture : IDisposable
{
    public TextureDesc Desc { get; }
    public Image Image { get; }
    public ImageView View(int baseLevel = 0, int levels = -1, int baseLayer = 0, int layers = -1, ComponentMapping swizzle = default);
    public ImageView Attachment(int level = 0, int layer = 0);
}

/// GL's sampler state as a value. FromGl replicates VkGl.SamplerFor exactly: the code moves to Meitou.Rendering/Gpu and VkGl calls it,
/// so both paths share one function.
public readonly record struct SamplerDesc(Filter Min, Filter Mag, SamplerMipmapMode Mip, bool Mipmapped,
    SamplerAddressMode U, SamplerAddressMode V, SamplerAddressMode W, bool Compare, CompareOp Op, bool TransparentBorder,
    bool IntegerFormat, float Anisotropy, float LodBias)
{
    public static SamplerDesc FromGl(TextureMinFilter min, TextureMagFilter mag, TextureWrapMode s, TextureWrapMode t, TextureWrapMode r,
        bool compare, DepthFunction func, bool transparentBorder, float anisotropy, bool integerFormat, float lodBias);
}
public sealed class SamplerCache { public Sampler Get(in SamplerDesc desc); }   // applies the NVIDIA −0.25 rule (DECISIONS 9) inside
```

The format and sampler rules that decide pixels are moved, not rewritten. `VkGl.VkFormat` maps `DEPTH_COMPONENT24` to `D32_SFLOAT` and
`RGB8` to `RGBA8`, and has no sRGB formats. `VkGl.SamplerFor` covers the NVIDIA anisotropic bias, the upscaler bias on mipmapped samplers
only, nearest filtering for integer formats, `MaxLod 0.25` without mips, and the border colours. `VkGl.Factor` and `VkGl.Compare` map the
blend factors and compare functions. All of these move into `Meitou.Rendering/Gpu` as shared static functions, which `VkGl` then calls. Native and
translated draws then cannot disagree on a sampler or a format (risks 2 and 3).

### 2.5 Pipelines

```csharp
/// Everything a graphics pipeline is made from. Built at load. Hashing it happens once, never per draw.
public readonly record struct GraphicsPipelineDesc(
    ShaderProgram Program, VertexLayout Vertex, PrimitiveTopology Topology, AttachmentFormats Targets,
    BlendState Blend, ColorComponentFlags ColourMask, PolygonMode Polygon, bool AlphaToCoverage, bool DepthClamp,
    SpecializationKey Specialization = default, string Name = "");

public sealed class PipelineLibrary
{
    /// At load: creates the pipelines (on the streaming threads; vkCreateGraphicsPipelines is thread-safe on one VkPipelineCache).
    public Task Prepare(IEnumerable<GraphicsPipelineDesc> descs);
    /// While drawing: the prepared pipeline. A miss creates it synchronously and counts it in Stats.LatePipelines, which the tests
    /// require to be 0 after the warm-up frames.
    public GraphicsPipeline Get(in GraphicsPipelineDesc desc);
    public ComputePipeline Get(in ComputePipelineDesc desc);
}
```

- **Dynamic state** is the same set VkGl uses (`VkGl.CreatePipeline`): viewport, scissor, cull mode, front face, depth test, write, compare,
  bias enable and bias. Blend, colour mask, polygon mode, alpha-to-coverage and depth clamp stay pipeline state, as in VkGl's `PipelineKey`.
  EDS3 is present on the test machine, but making them dynamic gains little once pipelines are prepared, and costs a device requirement.
- **Fixed conventions copied from VkGl** (they decide pixels). `NegativeOneToOne` depth clip when `HasDepthClipControl`, else the
  vertex-shader remap compiled in. GL's counter-clockwise front face is Vulkan's `Clockwise`. Rasterisation samples come from the target.
  Alpha-to-coverage only with more than one sample (`VkGl.PrepareDraw`).
- **Variants.** At parity (wave 3a) a variant is a different `GraphicsPipelineDesc`: blend, cull, target formats and samples. **No
  specialisation constants at parity**: turning a uniform into a constant lets the driver fold code and fuse operations differently, so
  results can differ in the last bit (risk 13). Specialisation constants are for new Meitou paths only.
- **Disk cache.** `VulkanDeviceOptions.PipelineCachePath` exists and `CoreTests` covers it, but the game and the viewer never set it (only the
  tests do). The foundation sets it to `%LOCALAPPDATA%\Meitou\pipeline-cache.bin`, next to the shader cache.

### 2.6 Descriptors: the model, by frequency

**Choice: bindless textures plus small per-frequency sets and push constants for the native model (wave 3b). The legacy layout is kept
only for parity ports (wave 3a).**

| Set / range | Frequency | Contents | Bound |
| --- | --- | --- | --- |
| set 0 `Bindless` | per command buffer | `sampler2D textures2D[]`, `sampler2DArray textures2DArray[]`, `samplerCube texturesCube[]`, `sampler2DShadow shadowTextures[]`: combined image samplers, partially bound, variable count, update-after-bind | once per command buffer (secondaries too) |
| set 1 `Frame` | per frame | `FrameConstants` UBO: atmosphere (everything `SkyRenderer.Apply` sets today), lighting, fog, water height, shadow receiver block (`KenshiShadowReceiver`, 560 B), Meitou shadow block, time; plus the bindless indices of the frame's shared textures (shadow atlas, noise, terrain shadow, blocker map, irradiance and specular cubes, ambient map, terrain heights) | once per frame |
| set 2 `View` | per view / pass | `ViewConstants` UBO at a dynamic offset into the frame's `LinearAllocator`: view-projection (jittered as drawn), eye, frustum planes, jitter, depth-slice planes, clip plane, LOD eye | once per view |
| set 3 `Pass` (optional, per renderer) | per pass | storage buffers the pass reads: material table, instance stores, per-view compacted instances | once per pass |
| push constants (≤ 128 B, the guaranteed minimum) | per draw | material index, instance base, small per-draw values (terrain `uNode` and `uMorph`, grass patch bounds and sizes) | per draw |

Why this choice:

- **Push descriptors** (VkGl today, `KHR_push_descriptor`): measured at 0.11-0.12 µs per draw for one texture write, and 0.30 µs per draw
  with the foliage program's full set 0 (2 blocks + 13 samplers) pushed each time (section 9). More importantly, an indirect or multi-draw
  call cannot change descriptors between its draws, so a GPU-driven batch with many materials cannot use them.
- **Descriptor indexing (bindless)**: a material switch becomes a push-constant write (0.06-0.07 µs per draw, push constants included). It
  works with multi-draw indirect because the material index can travel with the draw (`gl_DrawID` or the instance). And it removes the
  texture-unit juggling that `SkyRenderer.AssignSamplerUnits`, `ShadowShaders.Bind` and `MeitouShadowShaders.Bind` do today, where units are
  carved from the top of 64.
- **Sets per frequency** turn the per-program copies of the same values into one write per frame. Today `SkyRenderer.Active.Apply` writes
  14 atmosphere uniforms into each program's default block several times per frame (terrain, objects, foliage meshes, grass), and each copy
  is followed by a whole-block copy at the next draw. The foliage program's vertex block alone is 8,272 bytes, because `uBones[128]` from
  `Shaders.MeshVertex` sits in it (measured, section 9).

**The bindless table.** One set per frame slot, so a slot is never written while a frame in flight reads it. Registrations go into a
journal that is replayed into a slot when that slot's frame begins. Registration and freeing happen on the render thread outside recording,
and frees are deferred to the frame fence. The sampler is part of the slot, since combined image samplers reproduce GL's per-texture
sampler state exactly. When the upscaler's LOD bias changes (`ITextureLodBias`, set by `PostProcess.Begin`, reset to 0 for the heat
haze), the affected slots are rewritten. The bias changes when the upscaler or render scale changes, not every frame. **One catch**:
`PostProcess` sets the bias to 0 in the middle of the frame for the heat-haze pass only. In the native model the heat haze's two textures
are registered with a bias-0 sampler of their own, which is what the pass does today.

**Parity ports do not use this model.** They use the *legacy layout*, the one VkGl builds from glslang's automatic bindings (section 3.2).

### 2.7 Command recording

```csharp
/// A command buffer being recorded. Thin: every method records what it says. It keeps only the redundancy filter the caller asks for
/// (the last pipeline and the last vertex and index buffers, cleared at BeginRendering). Not thread-safe; one per thread (section 6).
public sealed unsafe class CommandList
{
    public CommandBuffer Handle { get; }
    // rendering
    public void BeginRendering(in RenderingDesc targets);          // dynamic rendering: attachments, load/store ops, clear values, area
    public void EndRendering();
    public void BindPipeline(GraphicsPipeline pipeline);
    public void SetViewport(in Viewport v); public void SetScissor(in Rect2D r);
    public void SetRaster(CullModeFlags cull, FrontFace front);
    public void SetDepth(bool test, bool write, CompareOp op);
    public void SetDepthBias(bool enable, float constant, float slope);
    public void BindVertexBuffers(uint first, ReadOnlySpan<BufferBinding> bindings);
    public void BindIndexBuffer(BufferBinding binding, IndexType type);
    public void BindSets(PipelineLayout layout, uint first, ReadOnlySpan<DescriptorSet> sets, ReadOnlySpan<uint> dynamicOffsets);
    public void PushDescriptors(PipelineLayout layout, uint set, ReadOnlySpan<WriteDescriptorSet> writes);   // legacy layout only
    public void PushConstants<T>(PipelineLayout layout, ShaderStageFlags stages, in T value, uint offset = 0) where T : unmanaged;
    public void Draw(uint vertices, uint instances = 1, uint firstVertex = 0, uint firstInstance = 0);
    public void DrawIndexed(uint indices, uint instances = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0);
    public void DrawIndexedIndirect(Buffer args, ulong offset, uint count, uint stride = 20);
    public void DrawIndexedIndirectCount(Buffer args, ulong offset, Buffer count, ulong countOffset, uint maxCount, uint stride = 20);
    // compute and transfer
    public void BindPipeline(ComputePipeline pipeline);
    public void Dispatch(uint x, uint y = 1, uint z = 1); public void DispatchIndirect(Buffer args, ulong offset);
    public void FillBuffer(Buffer b, ulong offset, ulong size, uint value); public void CopyBuffer(Buffer src, Buffer dst, in BufferCopy region);
    public void Resolve(Texture src, Texture dst);                  // the reflection's 4x target (VkGl: CmdResolveImage)
    public void Blit(Texture src, Texture dst, Filter filter);      // mip generation, VkGl's glBlitFramebuffer cases
    // sync, timing, labels
    public void Barrier(in BarrierBatch batch);                     // 2.9; normally emitted by ResourceStates, not by hand
    public void Timestamp(QuerySlot slot, PipelineStageFlags2 stage = PipelineStageFlags2.AllCommandsBit);
    public void BeginLabel(string name); public void EndLabel();   // VK_EXT_debug_utils, no-op without it
}
```

A pass is recorded in a fixed shape. Everything a draw needs is resolved at load into plain fields (pipeline objects, layout, binding
numbers, uniform offsets), so the loop does no lookup:

```csharp
cmd.BeginLabel("foliage meshes");
cmd.BindPipeline(meshPipeline);
cmd.BindSets(layout, 0, [bindless, frameSet, viewSet], [viewOffset]);
foreach (ref readonly var d in draws)          // built during Prepare (section 6)
{
    cmd.PushConstants(layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, d.Constants);
    cmd.BindVertexBuffers(0, [d.Vertices, d.Instances]);
    cmd.BindIndexBuffer(d.Indices, IndexType.Uint32);
    cmd.DrawIndexedIndirect(argsBuffer, d.ArgsOffset, 1);
}
cmd.EndLabel();
```

### 2.8 Compute and indirect

- **Compute pipelines** come from GLSL 450 compute sources through the same compiler (a new shader kind for `GlslProgramCompiler`; today it
  compiles only vertex and fragment pairs). Workgroup size is a specialisation constant.
- **Indirect arguments** use `VkDrawIndexedIndirectCommand` (5 × uint32, 20 bytes) in a `DeviceBuffer` with `Indirect | Storage` usage,
  written by compute (section 5.4). The draw calls read them after a barrier from compute shader writes to `DRAW_INDIRECT` reads.
- **`DrawIndexedIndirectCount`** is for wave 3b, once meshes share one vertex and index arena so one call can cover many batches. In wave 3a
  and the first GPU-driven step, each batch is one `DrawIndexedIndirect` with `count = 1`. That keeps the vertex shaders unchanged
  (section 5.4) and still costs only ~0.06-0.10 µs per draw (measured).

### 2.9 Rendering and synchronisation: declared uses, immediate recording

**Choice: no render graph. Each pass declares what it reads and writes when it begins, and a per-frame resource state tracker emits the
synchronization2 barriers between consecutive uses.** Recording stays immediate and in program order.

```csharp
public enum Access { ColourTarget, DepthTarget, DepthRead, Sampled, StorageRead, StorageWrite, TransferSrc, TransferDst, IndirectRead, VertexRead, UniformRead, Present }

public readonly record struct PassUse(object Resource, Access Access, ImageSubresourceRange? Range = null);

/// Tracks the last access of every resource touched this frame and emits the barrier a new access needs (stage and access masks from a
/// fixed table). Layouts stay GENERAL during the migration (section 4.4), so the barriers are memory and execution dependencies only.
public sealed class ResourceStates
{
    public void Use(CommandList cmd, ReadOnlySpan<PassUse> uses);   // called by GpuPass.Begin; merges all needed barriers into one vkCmdPipelineBarrier2
    public void AssumeFullBarrier();                                // after a seam crossing (section 4): everything is ordered
}

public ref struct GpuPass   // using var pass = ctx.Frame.BeginPass("shadows c2", cmd, uses); ... (BeginLabel / EndLabel / timestamps)
```

Why not a render graph: our frame order *is* the picture. Depth slices clear depth between them, cascades share an atlas, the reflection is
reused across frames. A graph that reorders or merges passes would buy little on desktop GPUs and would make the parity gate harder to
reason about. Why not hand-written barriers everywhere: six agents writing barriers independently would get them wrong somewhere. The
validation layer's synchronisation checks catch some of that, but only at run time and only on the paths a run takes.

VkGl orders everything with a full memory barrier before each render pass (`VkGl.EnsurePass`). Native passes are allowed to be finer.
Where a native pass meets VkGl code, the seam puts full barriers on both sides (section 4).

### 2.10 Timestamps, statistics, debug labels

- **`QueryArena`**: one timestamp pool per frame slot (as `VkGl.Queries.cs`, reset from the host when the slot comes round, using the same
  host-query-reset feature). Slots are handed out with an interlocked counter, so secondary command buffers can take them (section 6).
  Results are read a few frames late, never waited for.
- **`StageClock` and `FrameProfiler`.** `StageClock.Lap(stage)` keeps measuring render-thread CPU time. `FrameProfiler.Stamp` today writes
  a GL timestamp through `IGl.QueryCounter`. It moves to `QueryArena`, recorded into the frame's command buffer at the lap, through the seam's
  pass-neutral path while VkGl is still active (section 4.2). Stage names and indices stay as they are (`StageClock.Names`).
- **`GpuStats`**: per frame and per thread, summed at the end of the frame. Draws, indirect draws, dispatches, pipelines bound, late
  pipelines, constant bytes, upload bytes, and CPU recording ticks. `VkGlStats` stays as long as VkGl does. The `--fly-benchmark` and F11
  lines print both.
- **Debug labels**: `BeginLabel` / `EndLabel` per pass and per renderer (the StageClock stage names), `SetName` on every resource (the device
  already has `SetName`). Labels cost about one API call each and are recorded at pass granularity, so they stay on in release builds.

---

## 3. Shaders

*In short: the GLSL stays. For the parity ports we keep the shader text byte-for-byte, so the GPU runs the very same machine code and the
pixels cannot change. Only the CPU side that feeds the shaders is rewritten. In a second, separately checked step, the shaders' inputs are
reorganised (shared constant buffers, push constants, bindless textures) while their maths stays exactly the same.*

### 3.1 What the compiler does today (from the code)

`GlslProgramCompiler` (`Shaders/GlslProgramCompiler.cs`, cache version 3) takes a vertex and fragment GLSL 3.30 pair and:

1. injects interface locations so the stages agree (`InterfaceLocations.Apply`), and optionally appends the clip-depth remap;
2. compiles each stage with the shaderc it ships, at optimisation level zero (names kept for reflection), forced to `450 core`, with
   relaxed Vulkan rules (loose uniforms go into `gl_DefaultUniformBlock`), auto-bound uniforms and auto-mapped locations, and
   `MEITOU_FRAGMENT` defined in the fragment stage;
3. shifts every `Binding` decoration by the stage base (vertex 0, fragment 32) by patching the SPIR-V (DECISIONS 20);
4. caches the pair on disk under a SHA-256 of the sources, the options and the version.

`VkGl.LinkProgram` then moves each stage's default block to set 1 (binding 0 vertex, 1 fragment) by patching decorations
(`MoveDefaultBlock`), and builds set 0 for push descriptors from the reflection (`VkGl.CreateLayouts`).

The whole chain is deterministic: a pinned compiler, no optimiser, patches applied in place. This is the strongest parity lever available.

### 3.2 Step P: parity ports use the same SPIR-V and the same layout

**A native parity port compiles the same source strings with the same compiler and options, applies the same two patches, and builds the
same set 0 / set 1 layout.** The modules handed to `vkCreateShaderModule` are then byte-identical to what VkGl creates. The foundation turns
that into a unit test for every program: compile through `ShaderLibrary.Legacy`, compile through VkGl's path, compare the bytes.

```csharp
/// The program VkGl would build for this source pair: same SPIR-V, same set 0 (push descriptors at glslang's bindings) and set 1 (the
/// default blocks as dynamic uniform buffers). Native code writes uniforms by handles resolved at load and binds textures by slot.
public sealed class LegacyProgram
{
    public static LegacyProgram Create(GpuContext ctx, string vertexGlsl, string fragmentGlsl, string name);
    public ShaderProgram Program { get; }
    public PipelineLayout Layout { get; }
    public UniformHandle Uniform(string glName);        // at load; -1 / invalid when the program does not use it (GL's inactive uniform)
    public SamplerSlot Sampler(string glName);
    public BlockSlot Block(string glName);              // named uniform blocks (the shadow receiver and caster blocks)
    // per draw (no lookups):
    public void Set(UniformHandle u, float v); public void Set(UniformHandle u, int v); public void Set(UniformHandle u, in Vector3 v); ...
    public void Set(UniformHandle u, in Matrix4x4 m);
    public void Bind(SamplerSlot s, in SampledTexture texture);
    public void Bind(BlockSlot b, in BufferBinding buffer);
    /// Copies the default blocks that changed since the last flush into the frame's allocator and binds set 1 with their offsets.
    /// Pushes set 0 when a slot changed. Call right before a draw.
    public void Flush(CommandList cmd);
}
```

Rules the `LegacyProgram` must keep (all from VkGl, risk 7):

- **The CPU copy of each default block persists across frames**, like a GL program's uniform state (VkGl keeps `VertexDefault` /
  `FragmentDefault` per program for its lifetime). Values set once at load, such as sampler units, `uWireframe` 0 or `uSkinned` 0, stay set.
- **Conversions** follow `VkGl.Scatter`: a float into an int or bool member converts, an int into a float member converts, bools are stored
  as 0/1. The function moves to `Meitou.Rendering/Gpu` and both paths call it.
- **Missing textures** read as the GL default: a 1×1 (0, 0, 0, 1) texture, and a depth dummy cleared to 1 for shadow samplers
  (`VkGl.SamplerTexture`). The renderers rely on this: `Bind(0, 0)` in the foliage, objects and post code means "no texture". The dummies
  move to `Meitou.Rendering/Gpu`.
- **Disabled vertex attributes** read (0, 0, 0, 1) as float or (0, 0, 0, 1) as int (`VkGl.InitDummies`), with a stride-0 binding. Here
  `aBones` and `aWeights` of `Shaders.MeshVertex` are disabled for foliage and objects.
- **Frame-global uniforms and textures** (atmosphere, shadow blocks and maps, terrain heights) are set by name through `FrameGlobals`
  (section 4.3), the same values the GL path sets.

What step P buys: no state tracking, no pipeline hashing, no string-keyed lookups, one vertex-buffer bind call instead of one per location,
and pushes only of the slots that changed. Measured on the foliage mesh draw: 2.0 µs (VkGl, as `FoliageRenderer.DrawMesh` issues it)
against 0.18-0.30 µs (section 9).

### 3.3 Step O: the native model, same maths

A second, separately gated change per renderer moves its shaders to the native descriptor model (section 2.6):

- **A prelude** (`NativeShaders.Prelude`, owned by the foundation, then frozen) declares the bindless arrays, the `FrameConstants` and
  `ViewConstants` blocks, and the push-constant block. It is compiled with relaxed rules **off**, so a leftover loose uniform is a compile
  error, not a silent default block.
- **The maths stays textually identical.** Old uniform names become macros over the new members, for example
  `#define uAtmoTau frame.atmoTau` and `#define uViewProjection view.viewProjection`. A sampler becomes a macro over a bindless lookup:
  `#define uDiffuse textures2D[nonuniformEXT(pc.diffuse)]`. The bodies of `AtmosphereShaders.Functions`, `ShadowShaders.Functions` and
  `Shaders.MeshFragment` are not edited. The values reaching the arithmetic are bit-identical, since the same C# float goes into a UBO
  instead of a default block. The arithmetic is the same source.
- **Why this can still change pixels** (risk 13). The SPIR-V differs (loads from other blocks, indexed sampler arrays), and a driver's
  compiler may schedule or fuse the same expressions differently, for example contracting `a * b + c` into an FMA in one module and not in
  the other. GLSL's `precise` and `invariant gl_Position` limit this, and both are allowed in step O where a gate fails. The gate decides.
  If a renderer cannot reach 0 differing pixels in step O, it stays on step P until the owner decides (open question 2).
- **Shared shader text is ported once**, by the foundation as API steward, before wave 3b: the native variants of `Shaders.MeshVertex` /
  `MeshFragment` (used by foliage, buildings and the viewer's mesh and character renderers), `AtmosphereShaders.Functions` (which embeds
  `ShadowShaders.Functions`), `ShadowShaders.DepthFragment` / `MeshDepthFragment`, and `PostProcessShaders.Vertex`. Agents then build their
  own shaders on those.

### 3.4 Compiler and reflection changes

- `ShaderCompileOptions` gains `Model` (`Legacy` or `Native`) and `Stage` kinds (vertex+fragment pair, or compute). **Legacy output must not
  change**: the cache version stays 3 for legacy programs, and a test keeps the SPIR-V of every world program identical to what master
  produces today. Native programs get their own cache version (4) in the key.
- `SpirvReflection` already knows `BlockKind.PushConstant` and `StorageBuffer`. It gains runtime arrays, specialisation constants and
  workgroup size. For native programs, reflection *checks* the shader against the model (sets, bindings, push-constant size ≤ 128) at load
  in debug builds. The layout comes from the model, not from reflection.
- `InterfaceLocations`, the clip-depth remap and `MEITOU_FRAGMENT` stay as they are for both models.

---

## 4. The coexistence seam

*In short: during the port, native passes and VkGl passes record into the same command buffer of the same frame. Whenever control passes
from one to the other, the side that was drawing closes its pass, a full barrier is placed, and VkGl forgets what it believed was bound.
Images stay in the one layout VkGl uses (GENERAL), so neither side has to know what the other did with them. Either side can use a
resource the other created. That lets any renderer be ported first, with the rest of the frame unchanged.*

### 4.1 What exists already (from the code)

VkGl already lets foreign code record into its frame. `BeginExternal()` ends the open pass, places a full barrier and returns the
command buffer. `EndExternal()` places another full barrier. `RecordInFrame(Action<CommandBuffer>)` is the same with a callback.
`ImageOf(uint)` hands out the Vulkan image behind a GL texture. `VulkanPresenter`, `FsrUpscaler` and `DlssUpscaler` use exactly these.
Every image is created and kept in `GENERAL` layout (`VkGl.ToGeneral`).

What is missing: `EndExternal` does not invalidate VkGl's caches. VkGl skips `vkCmdBindPipeline` when the pipeline equals `lastPipeline`
(`VkGl.Draw.cs`, `PrepareDraw`). It skips set 0 pushes and the set 1 bind while `boundProgram` and `pushEpoch` are unchanged
(`BindResources`). It re-sends dynamic state only when `dynamicStateDirty` is set, which only happens at a new pass. A native segment that
binds its own pipeline therefore makes the next VkGl draw use the native pipeline. The spike reproduced exactly that: a VkGl draw after a
native segment that bound another pipeline with a compatible layout. The validation layer reported **0 errors**, because the command
stream is valid, only wrong. Draw logs (section 7.6) and the pixel gate catch this kind of error. Validation does not.

### 4.2 The interop interface

```csharp
namespace Meitou.Rendering.Gpu;

/// Implemented by VkGl. Renderers reach it as GpuContext.Interop while VkGl exists (null afterwards).
public interface IGlInterop
{
    /// Ends VkGl's open pass, places a full barrier and returns the frame's command list. Until EndNative, any IGl call throws
    /// (debug and release): an IGl call can flush the frame (Finish, ReadPixels, waiting queries end and restart the frame in
    /// VkGl.Flush), which would invalidate the command list held by the native code.
    CommandList BeginNative(string label);
    /// Full barrier, then invalidates every VkGl cache of command-buffer state: lastPipeline = default, dynamicStateDirty = true,
    /// boundProgram = null, pushEpoch++ (forces set 0 to be re-pushed and set 1 rebound), passActive = false. Vertex and index buffers
    /// are rebound at every VkGl draw already.
    void EndNative(CommandList cmd);
    /// Records a command that does not disturb a render pass (a timestamp, a debug label) into the frame without ending VkGl's pass.
    void Interleave(Action<CommandList> record);

    /// What VkGl is drawing into right now: the bound draw framebuffer's attachments as textures, and GL's viewport and scissor. While any
    /// VkGl code remains, the GL framebuffer binding is the source of truth for "the current pass" (4.5).
    PassTargets CurrentTargets();

    // export: GL objects to native code (non-owning; valid while the GL object lives)
    Texture Texture(uint glTexture);
    /// The view and sampler VkGl itself would bind for this texture now (its SamplerAndView cache: GL parameters, defined levels, base and
    /// max level, swizzle, the current LOD bias). Identical by construction.
    SampledTexture Sampled(uint glTexture, bool shadowSampler);
    /// The buffer's current version (dynamic buffers move every frame; a static one renamed after a write), marked as used by this frame.
    BufferBinding Buffer(uint glBuffer);

    // import: native objects to GL code
    /// A GL texture name for a native texture (borrowed: DeleteTexture forgets it but does not free the image). Unported code can attach
    /// it to a framebuffer and sample it, with GL sampler parameters of its own.
    uint Import(Texture texture);
    uint ImportBuffer(DeviceBuffer buffer);
}
```

### 4.3 Frame-global resources: `FrameGlobals`

Several values reach every program in the frame. `SkyRenderer.Apply` sets the atmosphere uniforms and binds the irradiance, specular and
ambient textures to the top three units. `ShadowShaders.Bind` points the receiver and caster blocks at binding points 6 and 7 and the shadow
map and noise texture at units `combined − 4` and `− 5`. `MeitouShadowShaders.Bind` adds block 8 and units `− 6` and `− 7` for the terrain
shadow and blocker maps. `TerrainRenderer.BindHeights` puts the height textures on units 7 and 8. The foundation adds a registry so native
consumers get the same resources by name, whatever side their owner is on:

```csharp
public sealed class FrameGlobals
{
    // owners publish once per frame (or when the value changes)
    public void Publish(string name, Func<SampledTexture> texture);          // "uShadowMap", "uShadowNoise", "uAtmoIrradiance", ...
    public void Publish(string name, Func<BufferBinding> block);             // "KenshiShadowReceiver", "KenshiShadowCaster", "MeitouShadowReceiver"
    public void Publish<T>(string name, Func<T> uniformValue) where T : unmanaged;   // "uAtmoTau", "uAtmoParams", ... (step P)
    public FrameConstants Constants { get; }                                 // step O: the same values as one struct, uploaded once
    // consumers (LegacyProgram.Create binds every name it finds in its reflection; native-model programs read set 1)
}
```

- An owner still on VkGl publishes through the export calls. For example `ShadowPass` publishes `uShadowMap` as
  `() => interop.Sampled(atlas, shadowSampler: true)`, and `SkyRenderer` publishes its uniform values from the state `Apply` reads.
  The foundation adds these publish calls to the owners in wave 2, before the agents start. They change no GL call, so no pixel.
- After the owner's port it publishes native objects instead. Consumers do not notice.

### 4.4 Layouts and barriers

- **Every image stays in `GENERAL`** until phase 8 is done. VkGl assumes it everywhere (attachments, sampling, transfers, the upscalers
  with `ImageLayout.General` tags). A native side using optimal layouts would have to transition back at every seam crossing, for every image
  VkGl might touch. Whether optimal layouts are worth it afterwards is open question 5: NVIDIA treats GENERAL like the optimal layouts for
  these uses, but AMD may lose colour and depth compression. No AMD hardware has been measured.
- **At every crossing a full barrier** (`ALL_COMMANDS`, memory write to read and write) is placed by `BeginNative` and `EndNative`, and
  `ResourceStates.AssumeFullBarrier()` is called. This is no worse than today: VkGl already places one before every render pass
  (`EnsurePass`), every blit, every mip generation and every external segment.
- **Inside native code**, `ResourceStates` emits precise barriers between native passes.
- **Uploads** from both sides go into the same upload command buffer, submitted ahead of the frame. The native `Uploader` uses VkGl's
  upload command buffer while VkGl exists (exposed by the foundation), so their relative order is the call order, as today.

### 4.5 Passes, targets and ordering

- **Same command buffer, same order.** Native segments are recorded inline where the old calls were, so the GPU executes in the same order
  as before. No reordering is possible by construction.
- **Rendering instances.** A native renderer that draws into the current pass begins its own dynamic rendering on
  `interop.CurrentTargets()` with `LOAD` / `STORE`, and sets the viewport and scissor GL had. Clears stay where the GL code does them
  (`WorldFrame.Draw` clears colour and depth before the sky, and depth between slices). A native host that takes over a clear uses
  `CLEAR` load ops only when the GL code cleared the whole attachment at that point.
- **Hosts and guests.** Three classes run other renderers' draws inside their own targets: `ShadowPass.Render` (casters per cascade in an
  atlas tile, through the `CasterDraw` callback), `ReflectionPass.Render` (terrain, objects and foliage mirrored, into a 4× MSAA target
  resolved to a texture) and `PostProcess` (the scene framebuffers, and the grass motion of `FoliageRenderer.DrawGrassMotion` inside the
  velocity pass through `PostProcess.ObjectMotion`). A host and its guests can be on different sides:
  - *host on VkGl, guest native*: the guest uses `CurrentTargets()`, because the host bound a GL framebuffer as today.
  - *host native, guest on VkGl*: the host creates native targets, `Import`s them into GL names, attaches them to a GL framebuffer and
    binds it before calling the guest. That is the GL code the host had, kept for the guests until all of them are native.
- **State the GL code inherits.** Native code may not rely on state left behind by earlier calls. For example, `WorldFrame.Draw` enables the
  depth test with `LEQUAL` before the slices, `ShadowPass` uses `LESS` with depth clamp and a scissor per tile, and the foliage leaves culling
  disabled. Each port lists the GL state in effect at its draws (the draw log shows it) and sets it explicitly.

### 4.6 Seam invariants (tests the foundation adds)

1. VkGl → native → VkGl in one frame, with three different pipelines and textures: correct read-back pixels, and 0 validation errors with
   synchronisation validation on.
2. The stale-pipeline case of 4.1 produces the correct picture (it fails before the invalidation is added).
3. An `IGl` call between `BeginNative` and `EndNative` throws.
4. `Sampled(name)` returns the same handles VkGl pushes for that texture in a VkGl draw (compared in the draw log).
5. An imported texture rendered by GL and sampled natively, and the reverse, read back identically.

---

## 5. GPU-driven foliage and objects (agents A and C)

*In short: today the CPU tests every tree, bush and rock against the view each frame, copies the survivors into a buffer and issues the
draws. The plan moves that per-instance work to compute shaders: instances live on the GPU permanently, and per view (each depth slice,
the reflection, each shadow cascade) the GPU decides what is visible, how faded it is, and how many of each mesh to draw. The CPU issues one
small indirect draw per mesh batch. To keep pixels identical, the GPU must produce the same instances in the same order as the CPU does now.
That is achievable for the visible set and the order, but not guaranteed for the last bit of the fade value, so this step has its own gate
rules and an owner decision.*

### 5.1 What the CPU path does (from `FoliageRenderer.cs`)

Per call of `Draw` (each depth slice, the reflection, each shadow cascade through `DrawDepth`):

1. **Cull** (`CullZones`): for each laid-out zone, the zone box against the frustum (main views only, with a margin from the zone's mesh
   bounds), then each group (one mesh of one layer in that zone) whose range covers the zone. Groups in range go into `cullWork`. In
   parallel (`RenderJobs.For`), each instance gets ground distance `d = Vector2.Distance(p.xz, eye.xz)`, `d >= range` is out,
   `centre = Vector3.Transform(asset.Centre, t)`, `radius = asset.Radius × scale`, fade `w = clamp((range − d) / band, 0, 1)`, then
   `SphereVisible` (plane · centre + w < −radius · |n|). Survivors are packed as the transform with the fade in `M14` (`2` when w ≥ 0.999).
2. **Shadow cascades**: the first cascade drawn records every instance in range of the camera eye (`maxRange = 1.2 × shadow range`) as
   `ShadowCandidate`s. Each further cascade tests only those (`CullCandidates`).
3. **Emit**: outputs are merged in `cullWork` order into per-mesh `Batch`es. `active` lists the batches **in the order of their first
   visible instance**. TERRAIN-mode rocks go to `terrainDraws` instead (drawn through `TerrainRenderer.DrawMeshes`, only when w ≥ 0.5).
4. **Upload**: one stream buffer, each batch's matrices contiguous.
5. **Draw**: per batch, per mesh part, one `DrawElementsInstanced` with the batch's range as per-instance attributes 7-10
   (`FoliageShaders.InstanceLocation`). Then grass per page (nearest first) and per patch. Then the TERRAIN-mode rocks.

`WorldObjectRenderer.Draw` follows the same pattern with LOD: `MeshLod.Blend` puts an instance into two batches while it blends between
levels, with `M14` / `M24` holding its dither interval. Distant towns and stand-ins come from `ObjectStreamer.ZonesNear`.

### 5.2 Instance storage per zone

```text
FoliageInstanceStore: BufferArena (device-local, Storage), one range per laid-out zone, filled once when the zone is accepted
  InstanceRecord (std430, 96 bytes):
    vec4 row0, row1, row2, row3   // the transform as FoliageRenderer keeps it (Matrix4x4, M14 = 0)
    vec4 sphere                   // centre = Vector3.Transform(asset.Centre, t), radius = asset.Radius × scale: computed on the CPU
                                  // at accept time with the same C# as CullZones, so GPU and CPU use the same floats
    vec4 ground                   // position x, position z, scale, group index within the zone
  GroupRecord (per zone, per group): first instance, count, mesh batch id, layer range at setting 1, transition, max scale
```

Zones arrive and leave as today (`Accept`, `Drop`). Their ranges are freed at the frame fence. Objects (C) use the same store with their own
record. It adds the LOD distances index and the bounding value used by `MeshLod`.

### 5.3 The per-frame cull: views, chunks, stable compaction

**Views.** Up to 8 per frame, in one dispatch: the main camera's depth slices (`WorldCamera.Slices`), the reflection's slice (foliage
meshes only, `maxRange = FoliageDistance`, the band rule `range < full ? min(fullBand, range × 0.25) : fullBand`), and the shadow cascades
drawn this frame (the Meitou shadows draw some cascades only every second or fourth frame). Each view has its frustum planes with the
plane-normal lengths precomputed on the CPU, its LOD eye, its range cap, and a flag for the zone-box test.

**Work list (CPU, render thread, cheap).** The CPU still walks zones and groups, as `CullZones` does: hundreds to a few thousand groups,
against tens of thousands of instances. It writes a `ChunkRecord` per ≤ 256 instances of a candidate group (instance range, batch id,
group order index, per-view range and inverse band, view mask). **The chunk order is the `cullWork` order.**

**Kernels** (GLSL 450 compute, `precise` on all culling arithmetic):

1. `foliage_cull`: one workgroup per chunk. Per instance and per view in the chunk's mask, the same tests in the same operation order as
   the C# (section 5.6). It writes a visibility bit per (instance, view) and, per (chunk, view), the visible count (a workgroup reduction).
2. `foliage_scan`: per view, an exclusive prefix sum of the chunk counts **within each batch, in chunk order**. One workgroup per view
   handles a few thousand chunks with a two-level scan. It writes each chunk's output offset, each batch's instance count, and the
   indirect arguments (below).
3. `foliage_compact`: one workgroup per chunk again. Each visible instance is written to its view's output buffer at
   `chunkOffset + (number of visible instances before it in the chunk)`, using a workgroup ballot and prefix. The fade is packed into `M14`
   the way the CPU does (`2` when w ≥ 0.999).

This is **order-stable**: inside a batch, instances come out in (chunk order, index order), which is the CPU's emission order. Counts are
sums, so atomics (if a variant uses them) do not affect the result.

### 5.4 Indirect arguments and draws

- **Output**: per view, a device buffer of compacted `mat4` (64-byte stride), with `Vertex | Storage` usage. It is bound as the
  **per-instance vertex buffer** at locations 7-10, so `FoliageShaders.MeshVertex` and `BuildingLodShaders.Vertex` do not change at all.
- **Arguments**: per (view, batch, mesh part) one `VkDrawIndexedIndirectCommand`. The CPU fills `indexCount`, `firstIndex` and
  `vertexOffset` once per part. The scan writes `instanceCount` and `firstInstance` (the batch's base in the view's output).
- **Draws**: per view, per batch, per part, the CPU records the material (step P: `LegacyProgram` slots; step O: a push constant), binds
  the part's vertex and index buffers and the view's instance buffer, and records `DrawIndexedIndirect(args, offset, 1)`. A batch with
  nothing visible draws 0 instances, at ~0.06-0.10 µs (measured). The forest view had 64 instanced batches in its far cascade
  (docs/viewer.md), so a view costs on the order of 100-300 indirect draws.
- **Later (wave 3b+)**: meshes in one shared vertex and index arena, one `DrawIndexedIndirectCount` per (view, pipeline), the material from
  a per-draw table indexed by `gl_DrawID`. That needs step O's bindless materials.
- **TERRAIN-mode rocks** stay CPU-culled until agent B provides an instanced main-pass TERRAIN-mode path. Today only the depth path is
  instanced (`TerrainShaders.MeshInstancedDepthVertex`). The main pass draws one draw each with a uniform matrix, through
  `TerrainShaders.MeshVertex`. A new instanced main-pass shader is a shader change with its own gate.
- **Grass**: in step P, a native draw per (page, patch) with push constants for the patch values. GPU-driven grass (page and patch tests,
  density prefix and range into indirect arguments) is a wave 3b item. Its page order (nearest first, `grassOrder`) only affects early
  depth rejection, plus the tie risk of 5.6.

### 5.5 Shadows per cascade, and the reflection

- Each cascade is a view. The candidate set ("in range of the camera eye, within 1.2 × the shadow range") is the view's range cap, measured
  from the camera eye exactly as `DrawDepth` passes `view.Eye`. There is no separate candidate pass.
- In the atlas the casters are drawn depth-only with `LESS` (`ShadowPass.Render`), so the result is the minimum depth and **does not depend
  on draw order** (docs/viewer.md makes the same argument for the instanced TERRAIN-mode fix). Cascades are the safest place to start
  GPU-driven drawing.
- The reflection target is 4× multisampled (`ReflectionPass.Samples`), so the foliage there draws **with alpha-to-coverage**
  (`FoliageRenderer.Draw` keys it on `GLEnum.Samples > 1`). The native path must take the sample count from the target, not assume 1. In
  the main scene it is off: the scene is single-sampled (docs/viewer.md, "Post-processing").

### 5.6 Parity: what can match exactly, and what cannot be promised

**Visibility decisions.** Vulkan requires single-precision add, subtract and multiply to be correctly rounded. With `precise` (no
contraction into FMA) and the C#'s operation order, the GPU reproduces scalar C# arithmetic exactly. RyuJIT does not contract scalar
`a * b + c` on its own. Two parts of today's code are not of that kind:

- `Vector2.Distance` uses a square root. .NET's is correctly rounded (`sqrtss`); Vulkan's `sqrt` is not required to be. `d >= range` can
  therefore flip for an instance within an ulp of its range.
- `(range − d) / band` divides. Vulkan allows 2.5 ulp for division.
- `Vector3.Transform` and the matrix code in .NET 9+ may use FMA where the hardware has it, which `precise` GLSL would not reproduce. That
  is why section 5.2 precomputes the sphere centre on the CPU.

**Proposal: a reference-alignment step first (A1), then the GPU port (A2).**

- **A1, CPU only, VkGl still drawing.** Change the CPU culling to a form the GPU can reproduce: centres precomputed at accept time (bit-identical
  to today's per-frame computation, as it is the same call), plane-normal lengths computed once per frustum (also bit-identical), the band
  as a precomputed reciprocal `(range − d) × invBand` (this changes w in the last bit), the range test as `dx² + dz² >= range²` (this can
  flip an instance within an ulp of its range), and the **batch order made independent of visibility**: batches in the order of their
  first *candidate* group in `cullWork`, not their first visible instance. Gate A1 against the build before. Expected: 0 differing pixels.
  A non-zero result must be traced to a named instance (draw log), because the only pixels that can move are depth ties between different
  meshes and dither thresholds within an ulp.
  **Done (2026-10-06)**: `FoliageCull` (`FoliageInstanceRecord` of 5.2, spheres filled once per group when the mesh's bounds are known,
  `FoliageGroupRange` with range² and 1 / band, `FoliageCullView` with the normal lengths, batches in first-candidate order, the cascades'
  too); scalar `sqrt(dx² + dz²)` measured bit-identical to `Vector2.Distance` (2M samples), the reciprocal fade within 1 ulp (tests
  `FoliageCullTests`). Gate: 0 differing pixels in all ten views (`--faithful all` in Debug, Meitou with `--faithful range` in Release), except the rock view, which differs between
  two runs of the base build itself (21 pixels at 13:00, 6 at 02:00, one spot at (717-724, 315-323)); A1's rock pictures equal one of the
  base runs' exactly (docs/viewer.md, "Foliage").
- **A2, GPU port.** The same formulas on the GPU. The remaining divergence is the square root inside the fade (`d` for w). A **cull
  verification mode** (`MEITOU_GPU_CULL_VERIFY=1`) runs the A1 CPU cull as well, reads the GPU's per-view lists back a frame later, and
  reports any difference in visible sets (must be none), order (must be none) and fade (each difference with its ulp distance). The pixel
  gate is 0 differing pixels. If it fails only where the verifier shows fade differences of ≤ 2 ulp, the owner decides (open question 2):
  accept with the record, or keep the CPU fade (the GPU decides visibility, the CPU-identical fade is recomputed per instance in the vertex
  shader from the same inputs, at the same precision issue), or emulate a correctly rounded square root.

**Draw order and depth ties.** Inside a batch, the GPU order equals the CPU order (5.3). Between batches, A1 fixes the order. Between depth
slices nothing changes, because every slice is its own view, drawn where the CPU drew it. Grass is drawn after the meshes, as today.

### 5.7 Size-based ranges, LOD, impostors, occlusion: Meitou-mode, behind switches

These change the picture on purpose, so they are not part of any parity step. They come after A2 and C2, each behind an `Enhancement`
(Meitou default, Faithful = today's ranges and meshes, so `--faithful all` keeps the gate valid):

- **Size-based ranges** (`foliage-range`, name open, open question 3): an instance's range follows its projected size (bounding radius
  over distance against a pixel threshold), capped by the layer's range × the setting. Large trees go far, small junk stops sooner. This
  is how small things become affordable at several thousand units. The draw-distance targets themselves are open question 4.
  **Done on the CPU (2026-10-06)** as the `range` switch, by size class per mesh rather than per instance (a group keeps one range, so the
  ChunkRecord's per-view range carries it unchanged): large / medium / small at 5000 / 2500 / 800 by default, Tab sliders and
  `--range-large|medium|small`; FAR layers' large meshes keep the longer of that and 8000 × the setting (docs/viewer.md "Foliage",
  docs/formats/foliage.md "Mesh sizes").
- **LOD selection on the GPU** (objects, C): `MeshLod.Select` / `Blend` per instance in the cull kernel, with two outputs while blending,
  as the CPU emits them. This is parity-relevant (the LOD rule is the game's), so it follows the A1/A2 pattern.
- **Impostors** (`impostors`): hemi-octahedral impostor atlases (12 × 12 frames, BC3 albedo + BC5 normal + BC5 depth), baked per mesh
  through `IGl` and cached in `%LOCALAPPDATA%\Meitou\impostors`. Drawn beyond the per-instance transition distance as one quad per
  instance from the same cull, crossfaded with the mesh by complementary dither. Needs bindless (step O). The baker, format, cache,
  sampling GLSL (`ImpostorShaders`) and the `--impostor-preview` check exist (from the code); wiring them into the foliage path and the
  `Enhancement` switch is the foliage path's step. Details, measurements and the GLSL API are in [impostors.md](impostors.md).
- **Hi-Z occlusion** (`occlusion`): last frame's depth pyramid reprojected, a two-phase cull. Conservative in theory but float-sensitive in
  practice, so it is a Meitou-mode switch.

---

## 6. Threading model (wave 4)

*In short: once every world pass records natively, several threads can record different parts of the frame at once (each depth slice,
each shadow cascade, each renderer), each into its own small command buffer, and the main thread stitches them together in the original
order. The order of the picture does not change. What has to change is that drawing code may no longer modify shared state while
recording: everything that decides what to draw happens first, on the render thread, and recording only reads the result.*

### 6.1 Command buffers

**Choice: secondary command buffers inside the primary's dynamic rendering**
(`VK_RENDERING_CONTENTS_SECONDARY_COMMAND_BUFFERS_BIT`, `VkCommandBufferInheritanceRenderingInfo`), executed in a fixed order with
`vkCmdExecuteCommands`. Barriers, clears, compute dispatches, resolves and timestamps between passes stay in the primary.

Why not one primary per thread, each beginning and ending its own rendering: splitting one logical pass (the near slice: terrain, objects,
foliage, water) into several rendering instances adds load and store work. Measuring that costs less than designing around it, but
secondaries are the standard way to parallelise one pass. Primaries per thread remain the choice for whole passes that have nothing to
share (shadow cascades, the reflection): one primary per job, submitted in order in one `vkQueueSubmit`.

### 6.2 What runs where

```text
render thread                                  record jobs (RenderJobs workers + the render thread)
-------------------------------------------    ----------------------------------------------------
Update (streaming, uploads, bindless journal)
Prepare per renderer and view:
  CPU culling (if not GPU-driven), LOD,
  material and texture residency (WorldTexture.Id
  touches), instance and constant data written
  into Transients, the draw lists
Build the job list in frame order  ─────────►  Record(job): secondary command buffer
  (pass uses declared, barriers computed)        only reads the Prepare results
Execute secondaries in order, submit  ◄──────
```

The rule that makes this work, and that wave-3 ports should already follow: **every renderer splits into `Prepare(view)` (render thread,
may change its own state) and `Record(CommandList, view)` (any thread, reads only)**. A port that keeps the shape `Draw = Prepare + Record`
in wave 3 makes wave 4 a scheduling change rather than a rewrite.

### 6.3 What must become thread-safe, or be kept off the recording threads

| Thing (file) | Today | In wave 4 |
| --- | --- | --- |
| `VkGl` (all of it) | one thread, implicit state | not used by recorded jobs: a pass is parallel only once all of its draws are native. Overlays and debug views may stay on the render thread at the end of the frame until phase 8 |
| `RenderJobs` | "one caller at a time (the render thread); bodies must not call back in" | recording jobs are top-level `RenderJobs.For` calls. The foliage CPU cull in `Draw` moves into `Prepare` (or away, with GPU culling), so loops are not nested |
| `FrameRing.BeginFrame/EndFrame` | one thread | unchanged (render thread). `DeferDelete` is already locked |
| `GpuAllocator` | locked (`lock (gate)`) | unchanged. Recording allocates no memory; Prepare does, on the render thread |
| `LinearAllocator` (constants) | — | one per recording thread per slot, chunks from a locked pool |
| Command pools | one per slot (`FrameRing`), plus VkGl's upload pools | one per (thread, slot) for secondaries, reset when the slot comes round |
| `PipelineLibrary` | — | read-only after load. A late creation takes a lock and is counted |
| `BindlessTable` | — | registration on the render thread only. Recording reads indices |
| `QueryArena` | — | slots handed out with `Interlocked.Increment` |
| `StageClock` | static, render thread | per-job CPU time recorded per job, summed per stage. The render thread's laps keep wall time |
| Renderers' counters (`DrawnInstances`, `DrawCalls`, `PhaseMs`...) | fields written while drawing | written in Prepare, or per job and summed |
| `WorldTexture.Id` (reading it marks the texture used and may start a reload) | read while drawing | read in Prepare only |
| `SkyRenderer.Active` (static) | read while drawing | `FrameGlobals.Constants`, written once per frame before the jobs |

### 6.4 Order and parity

Jobs are executed in the order the single-threaded code recorded them. Each job records the same commands as the single-threaded code
did. The picture is therefore identical, and the gate is the usual 0 differing pixels. Load imbalance is handled by splitting large
renderers' draw lists into several jobs that are still executed in order.

---

## 7. Parallel work plan

*In short: one agent first builds the shared foundation and freezes it. Then six agents each take one group of renderers, touching only
their own files, and prove after every step that the picture has not changed by a single pixel. Files shared by everyone are either
prepared by the foundation beforehand or left untouched.*

### 7.1 Wave 2: the foundation (one agent)

Deliverables, in order. Each step lands only after the gate (7.7) has passed:

1. `src/Meitou.Rendering/Gpu/` with `Core/` and `Shaders/` moved there, the Silk.NET references added to `Meitou.Rendering`, nothing else. Gate: the build, the tests, and the 10 views
   identical.
2. The shared pixel-deciding functions moved out of VkGl (formats, samplers, blend factors, compare ops, `Scatter`, the dummy textures and
   vertex buffer), with VkGl calling them.
3. The native API of section 2 and the `LegacyProgram` of 3.2, with unit tests: golden-image tests (a native triangle against the same
   triangle through VkGl, read back byte-identical), SPIR-V identity for every world program (legacy model), pipeline preparation,
   `ResourceStates` barrier tables, `QueryArena`.
4. The seam (section 4): `IGlInterop` on VkGl with the invalidation of 4.2, the IGl-call guard, `CurrentTargets`, export and import,
   `FrameGlobals` with publish calls added to `SkyRenderer`, `ShadowPass`, `ShadowPass.Meitou` and `TerrainRenderer.BindHeights` (these
   additions do not change any GL call), and the seam tests of 4.6.
5. Device features of section 1, the disk pipeline cache path, `VK_EXT_debug_utils` without validation, and `MEITOU_VK_VALIDATION=sync`
   (synchronisation validation through `VK_EXT_layer_settings`; `=gpu` for GPU-assisted validation of bindless indices).
6. The draw log (7.6) on both sides, and `meitou-tools draw-log-diff`.
7. **Pilot port**: `TerrainRenderer.DrawMeshes` (the TERRAIN-mode mesh path, instanced in depth, one draw each in colour), step P. It is
   the coupling that foliage and objects both call, so porting it before A and C start removes their only dependency on B. It also runs
   one full gate on a real renderer before six agents rely on the API.
8. Constructor plumbing: every renderer's constructor gets a `GpuContext` parameter, and `WorldFrame.CreateGpu`, the game and the viewer pass
   it. This is mechanical, and after it no port agent needs to edit `CreateGpu`.
9. `FrameProfiler` timestamps on `QueryArena` through `Interleave` (StageClock keeps working while the stages move).

After wave 2 the foundation agent stays on as **API steward** for wave 3 (open question 6). Agents request additions to `Meitou.Rendering/Gpu/`.
The steward lands them additively (no signature changes), one at a time, and agents rebase. Before wave 3b, the steward also lands the
native shader prelude and the shared native shader variants (3.3), each proven on one consumer.

### 7.2 Wave 3: ownership

Each agent owns its files completely: it may edit them, and nobody else may. Call-site counts are `IGl` calls from section 8.

| Agent | Owns (may edit) | IGl calls | Step P | Step O / GPU-driven |
| --- | --- | ---: | --- | --- |
| **A foliage** | `FoliageRenderer.cs`, `FoliageShaders.cs` | 147 | meshes, grass, grass motion (`DrawGrassMotion`, a guest in E's velocity pass), depth | A1, A2 (5.6), then native model, GPU-driven grass, then 5.7 |
| **B terrain + shadow host** | `TerrainRenderer.cs` (except `DrawMeshes`, done by the pilot), `TerrainTextures.cs`, `TerrainStreamer.cs`, `TerrainShaders.cs`, `TerrainShadowMap.cs`, `ShadowPass.cs` and `ShadowPass.Meitou.cs` (except `DrawDebug` and `CaptureDepth`, which belong to F) | 122 + 52 + 48 + 123 + 78, minus the debug views | patches, depth, the atlas host, the Meitou blocker map and terrain shadow sweep | native model; instanced main-pass TERRAIN-mode path (unblocks GPU-driven rocks for A) |
| **C objects + characters** | `WorldObjectRenderer.cs`, `BuildingLodMesh.cs` (`ObjectMeshCache`), `BuildingLodShaders.cs`, `BuildingMaterial.cs`, `MaterialResolver.cs`, `ObjectStreamer.cs`, `WorldObjects.cs`, `WorldTextureCache.cs` (shared with A: A only reads `WorldTexture`), `Model.cs`; the viewer's `Renderer.cs`, `CharacterRenderer.cs`, `CharacterScene.cs`, `Animator.cs` | 39 + 38 + 34 + 116 + 93 | objects, distant towns, the viewer's mesh and character drawing | native model; GPU-driven objects with LOD (optional, C1/C2 like A1/A2) |
| **D sky, clouds, water + reflection host** | `SkyRenderer.cs` (the clouds are part of its sky shader), `AtmosphereShaders.cs` *content* (frozen for others), `WaterRenderer.cs`, `ReflectionPass.cs` | 80 + 52 + 60 | sky, clouds, water, the reflection host (MSAA target and resolve) | native model, `FrameConstants` atmosphere part |
| **E post-processing + upscalers** | `PostProcess.cs`, `PostProcessShaders.cs`, `PostProcessOptions.cs`, `UpscaleShaders.cs`, `Upscaling.cs`, `Upscalers/*` (FSR, DLSS, Streamline) | 181 | (optional, open question 10) the scene targets (host), SSAO, velocity, TAA, exposure, composite, FXAA, heat haze, vendor upscalers on native images | native model |
| **F overlays, settings, debug** | `DebugOverlay.cs`, `SettingsPanel.cs`, `FrameProfiler.cs`, `FramebufferCapture.cs`, `ShadowPass.DrawDebug` / `CaptureDepth` and their shader text, the viewer's `Program.cs`, `CharacterApp.cs`, `WorldApp.cs`, `WorldApp.Benchmark.cs`, the game's `Program.cs` (its IGl calls: the screenshot target and readback) | 42 + 6 + 2 + debug views + 74 + 11 | (optional, open question 10) overlay text and panels, profiler chart, screenshots and readback, the debug views | native model |

**Frozen after wave 2** (only the steward edits them, additively):
- `src/Meitou.Rendering/Gpu/**` (native API, Core, Shaders), `src/Meitou.Rendering.Vulkan/VkGl*.cs`, `Gpu/IGl.cs`, `Gpu/GlEnums.cs`, `Gpu/IGlInterop.cs`, `FrameGlobals`.
- Shared shader text: `Shaders.cs`, `ShadowShaders.cs` (its `Functions`, `DepthFragment`, `MeshDepthFragment` text), `MeitouShadowShaders.cs`
  text, `AtmosphereShaders.cs` (D owns its *meaning* but changes it only through the steward, since every renderer includes it), and the
  native prelude.
- `WorldGl.cs`, `StageClock.cs`, `Enhancements.cs`, `RenderJobs.cs`, `BackgroundWork.cs`.
- `WorldFrame.cs`: each agent may change **only the lines that call its own renderer**. The foundation's step 8 removes the need to touch
  `CreateGpu`. Conflicts are then line-local and trivial.

### 7.3 Order and merging

- **Wave 3a**: A to F in parallel, in worktrees off the foundation's tip. Each port is pixel-neutral, so merges commute. Before merging, an
  agent rebases onto the current master and runs the gate again against **that** master's build.
- **Wave 3b** starts per renderer once the steward has landed the prelude and shared native shaders. Within A: A1 → A2 → native model →
  GPU grass. Within C: C1 → C2 if C takes GPU-driven objects.
- **Wave 4** starts once every world pass is native. Overlays and debug views may be the last to go.
- **Phase 8** last (section 8).

### 7.4 Couplings agents must know about

| Coupling (from the code) | Between | How it is handled |
| --- | --- | --- |
| `TerrainRenderer.DrawMeshes` called by `FoliageRenderer` and `WorldObjectRenderer` | B ← A, C | ported by the foundation pilot. A and C call it as a native API with their native command list |
| `TerrainRenderer.BindHeights` called by `WaterRenderer` | B ← D | `FrameGlobals` names `uHeightCoarse`, `uHeightFine` and the rect uniforms, published by the foundation in wave 2 |
| `SkyRenderer.Active.Apply` in terrain, objects and foliage | D ← A, B, C | `FrameGlobals` (atmosphere values and textures) |
| `ShadowShaders.Bind` / `MeitouShadowShaders.Bind` in every `WorldGl.Program` | B ← all | `FrameGlobals` (blocks 6, 7, 8 and the four textures) |
| `ShadowPass.Render` runs the casters of B, C and A per cascade | B hosts A, B, C | 4.5: guests on either side |
| `ReflectionPass.Render` runs the sky, terrain, objects and foliage | D hosts D, B, C, A | 4.5 |
| `PostProcess.ObjectMotion` runs `FoliageRenderer.DrawGrassMotion` inside the velocity pass | E hosts A | 4.5. A's guest draw writes only red and green (`ColorMask(true, true, false, false)` today), so its native pipeline needs that colour mask |
| `WorldTextureCache` used by objects and foliage | C, A | C owns it. A reads `WorldTexture` (its GL name through `Sampled`, later its bindless index). C keeps a GL name (`Import`) as long as A is on VkGl |
| `Shaders.MeshVertex` / `MeshFragment` in foliage, buildings and the viewer | A, C | frozen. The native variant comes from the steward (3.3) |
| `ITextureLodBias` set by `PostProcess` | E → all samplers | `SamplerCache` / bindless bias (2.6). E calls the same setter |

### 7.5 What each agent does, step P (the same recipe for all)

1. Record the renderer's GL draws on the parity views with the draw log (7.6) and keep the log as the reference.
2. Replace the renderer's `IGl` draw calls with native recording through `LegacyProgram`, bracketed by `BeginNative` / `EndNative` and using
   `CurrentTargets()` where it draws into a pass it does not own. Resource creation (textures and buffers) may stay on `IGl` in step P and
   be used through export. Moving it to native resources is phase-8 work by the same owner.
3. Split each draw method into `Prepare` and `Record` (6.2).
4. Draw-log diff: the native log must equal the reference (same programs, the same pipeline state, dynamic state, the same texture and buffer
   handles, the same default-block bytes, the same counts, in the same order). Then the gate.

### 7.6 The draw log

The foundation adds a per-draw log on both sides: `VkGl` (in `PrepareDraw`) and `CommandList` (at each draw). One line per draw, with the
stage and pass label, the program (source hash and name), the pipeline state (VkGl's `PipelineKey` fields, the native desc), the dynamic
state values, the attachment images, the descriptor handles (textures as image and sampler handles, which are the same objects on both
sides through export), vertex and index buffer handles and offsets, the counts, and a hash of each default block's bytes.
`MEITOU_DRAW_LOG=<file>` writes it for one frame of a `--screenshot` run. `meitou-tools draw-log-diff a b` compares two logs after mapping
GL names to handles. This turns "20 pixels differ" into "draw 1,812 has another sampler". docs/viewer.md records exactly such an
unexplained case.

### 7.7 The parity gate procedure

For every step that claims parity (foundation steps, 3a ports, A1, A2, C1, C2, step O, wave 4):

1. **Pre-port build**: master (or the step's base) in Release. Run `tools/scripts/parity.sh <viewer> <dir1> --faithful all` **twice**
   (`dir1`, `dir2`) and compare them with `parity-compare.sh`. Every view must show maximum difference 0. A view that is not deterministic
   is reported and rerun, and the step cannot be judged on it until it is explained. docs/viewer.md has one unexplained rock-view run with
   20 differing pixels. The script renders **five views at 13:00 and 02:00, ten pictures**. docs/engine.md and DECISIONS 2 still say eight
   (the forest view was added later), and should be corrected separately.
2. **Port build**: the same command into `dir3`. Compared to `dir1`: **maximum 0 and mean 0.0000 in all ten**. "0 differing pixels"
   means the largest channel difference is 0. The tool's "share over 12" is not enough.
3. **Meitou default too** (step P and wave 4): the same without `--faithful`. TAA is deterministic for screenshots (fixed warm-up,
   `InstantAdaptation`), so a pure port changes nothing there either. Step O and the GPU-driven steps gate the Faithful pictures. Meitou
   pictures differ only where a Meitou feature says so.
4. **Renderer-specific views**: A, B, C and F also gate `--debug-shadows 1` at the forest and The Hub (the atlas drawn into the picture),
   as the TERRAIN-mode fix did. D gates a reflection-heavy view (Port North is in the set). E gates `--upscaler taa`, and FSR / DLSS when the
   libraries are present (the vendor upscalers' own output is not guaranteed deterministic: for them the gate is the inputs, compared by
   reading back colour, depth, motion and the reactive mask).
5. **Validation**: `MEITOU_VK_VALIDATION=1` and `=sync` on the ten views: 0 errors. Validation runs are separate from parity runs, because
   validation slows recording 10-100× (measured: 2-50 µs per draw in the spike) and the timing-dependent streaming would then differ.
6. **Draw-log diff** on two views, for ports.
7. **Benchmark**: `--fly-benchmark` before and after (three interleaved runs each, medians, as DECISIONS 5 and 19 do), and the forest still
   camera (`--fly-speed 0`). Report the stage CPU times and GPU time.
8. Write the result into the step's commit message and docs/engine.md.

### 7.8 Risk list

| # | Risk | Where it bites | Mitigation |
| --- | --- | --- | --- |
| 1 | **Depth-tie order changes**: draws or instances in another order change which of two equal depths wins (`LEQUAL` in the main pass) | batch order, instance order, grass page order, slices; GPU compaction | native step P records in the old order; A1 makes the batch order explicit; stable compaction (5.3); draw-log order check |
| 2 | **Sampler and filter state** differs: the NVIDIA −0.25 anisotropic bias, upscaler bias only on mipmapped samplers and reset for the heat haze, `MaxLod 0.25` without mips, nearest for integer formats, anisotropy clamp 16, compare only for shadow samplers of compare-mode textures, view level ranges from defined levels and base/max level | every textured draw | one shared `SamplerDesc.FromGl` (2.4); `Sampled()` exports VkGl's own objects in step P; bindless registration uses the same desc |
| 3 | **Formats and sRGB**: `DEPTH_COMPONENT24` is `D32_SFLOAT`; RGB8 expands to RGBA8; nothing is sRGB (`VkFormat` has no sRGB format, and `FramebufferSrgb` is ignored) | targets, textures | shared `VkFormat`; a test that no native texture uses an sRGB format |
| 4 | **Clip and winding conventions**: depth clip −1..1, GL CCW = Vulkan CW, no y flip until the presenter | every pipeline | shared pipeline defaults; golden-image tests |
| 5 | **Upscaler integration**: FSR and DLSS take GL names and `BeginExternal` today; Streamline tags carry the GENERAL layout; FSR wants storage-capable float targets (`VkGl.Allocate` adds storage usage) | E | E moves them to native `Texture`s with the same usage flags, still GENERAL; DLSS needs `slSetVulkanInfo` and the device extensions as now |
| 6 | **Validation layers miss "valid but wrong"** (4.1, reproduced) and slow everything down | the seam, ports | draw logs and pixels decide; validation in separate runs; sync validation through layer settings; GPU-assisted validation for bindless indices |
| 7 | **Uniform semantics**: default blocks persist across frames, int/float/bool conversions, inactive uniforms (location −1) ignored | step P | `LegacyProgram` per 3.2, with the shared `Scatter` |
| 8 | **Inherited GL state** the old code relied on silently | every port | the draw log shows every draw's full state |
| 9 | **Buffer renaming** semantics: VkGl renames a static buffer written after a draw this frame; the native API asserts | renderers that rewrite buffers mid-frame (instance and uniform buffers) | per-frame data in `Transient`s; the assert finds the rest |
| 10 | **Hidden frame splits**: `Finish`, `ReadPixels` and waiting queries end and restart VkGl's frame (`VkGl.Flush`) | screenshots, readbacks, the profiler | the IGl guard between `BeginNative` and `EndNative`; native readbacks go through a fence, never inside recording |
| 11 | **Alpha-to-coverage** depends on the target's sample count | reflection (4×) | take samples from the target (5.5) |
| 12 | **Baseline non-determinism** | the gate itself | double baseline run (7.7) |
| 13 | **Compiler scheduling** differs once SPIR-V differs (step O, specialisation constants) | step O | step P first with byte-identical SPIR-V; `precise` / `invariant` where needed; owner decision on failures |
| 14 | **GPU culling floats** (5.6) | A2, C2 | A1/C1 alignment, the verifier, open question 2 |
| 15 | **Merge conflicts in `WorldFrame.cs`** | everyone | constructor plumbing by the foundation; line-local edits only |
| 16 | **Hardware floor** (bindless, multi-draw) | users with old GPUs or drivers | checked at start with a message; open question 1 |

---

## 8. Phase 8: deleting IGl and VkGl

*In short: when every renderer records natively, the translation layer and the GL-shaped interface are dead code and go, together with the
helpers built around them. This section lists what uses them today, so the deletion can be checked off file by file.*

### 8.1 Inventory today (from the code, master `0153744`)

`IGl` declares 90 distinct member names. All 90 are used. DECISIONS 7 counted 88 functions over 957 call sites when it was written. Counted
here: calls on an `IGl` (`gl.` or `Gl.` followed by an `IGl` member name), excluding VkGl itself:

| File | IGl calls | Owner (7.2) |
| --- | ---: | --- |
| `src/Meitou.Rendering/PostProcess.cs` | 181 | E |
| `src/Meitou.Rendering/FoliageRenderer.cs` | 147 | A |
| `src/Meitou.Rendering/ShadowPass.cs` | 123 | B (debug views F) |
| `src/Meitou.Rendering/TerrainRenderer.cs` | 122 | B (`DrawMeshes`: foundation pilot) |
| `tools/Meitou.ModelViewer/Renderer.cs` | 116 | C |
| `tools/Meitou.ModelViewer/CharacterRenderer.cs` | 93 | C |
| `src/Meitou.Rendering/SkyRenderer.cs` | 80 | D |
| `src/Meitou.Rendering/ShadowPass.Meitou.cs` | 78 | B |
| `src/Meitou.Rendering/ReflectionPass.cs` | 60 | D |
| `src/Meitou.Rendering/WaterRenderer.cs` | 52 | D |
| `src/Meitou.Rendering/TerrainTextures.cs` | 52 | B |
| `src/Meitou.Rendering/TerrainShadowMap.cs` | 48 | B |
| `src/Meitou.Rendering/DebugOverlay.cs` | 42 | F |
| `src/Meitou.Rendering/WorldObjectRenderer.cs` | 39 | C |
| `src/Meitou.Rendering/BuildingLodMesh.cs` | 38 | C |
| `src/Meitou.Rendering/WorldTextureCache.cs` | 34 | C |
| `src/Meitou.Rendering/WorldGl.cs` | 23 | foundation |
| `tools/Meitou.ModelViewer/Program.cs` | 23 | F |
| `tools/Meitou.ModelViewer/CharacterApp.cs` | 23 | F |
| `tools/Meitou.ModelViewer/WorldApp.cs` | 20 | F |
| `src/Meitou.Rendering/ShadowShaders.cs` | 16 | foundation (`Bind`) |
| `src/Meitou.Game/Program.cs` | 11 | F |
| `tools/Meitou.ModelViewer/WorldApp.Benchmark.cs` | 8 | F |
| `src/Meitou.Rendering/MeitouShadowShaders.cs` | 8 | foundation (`Bind`) |
| `src/Meitou.Rendering/WorldFrame.cs` | 7 | the agents of the calls (clears and state around the sky and slices: foundation) |
| `src/Meitou.Rendering/FrameProfiler.cs` | 6 | F |
| `src/Meitou.Rendering/FramebufferCapture.cs` | 2 | F |
| **total** | **1,452** | 1,158 in `src/Meitou.Rendering`, 283 in the viewer, 11 in the game |

Besides `IGl`, code uses **VkGl's own public surface**: `VulkanPresenter.cs` (`BeginFrame`, `EndFrame`, `Backbuffer`, `RecordInFrame`),
`FsrUpscaler.cs` and `DlssUpscaler.cs` (`ImageOf`, `BeginExternal`, `EndExternal`, `Device`), `VulkanDisplay.cs` (constructs it), and the
game's and viewer's frame loop. **Tests**: `tests/Meitou.Tests/Vulkan/VkGlTests.cs` makes 129 `IGl` calls. The `Vulkan` test folder has 30
tests in all (`CoreTests`, `ShaderCompilerTests`, `ShaderInterfaceTests`, `ShaderReflectionTests`, `VkGlTests`).

Beyond the draw calls, resource creation also goes through `IGl` in `TerrainTextures`, `WorldTextureCache`, `BuildingLodMesh`, `TerrainShadowMap`,
the foliage meshes and grass pages, `PostProcess` targets and `SkyRenderer` textures. Phase 8 includes moving those to native `Texture` /
`DeviceBuffer` creation through the `Uploader`. That is owner work by the same agents (7.2), since step P may leave resource creation on IGl.

### 8.2 End state

Deleted:
- `src/Meitou.Rendering/Gpu/IGl.cs`, `GlEnums.cs` (343 lines), `ITextureLodBias.cs` (replaced by the sampler bias of 2.6), `IGlInterop.cs`.
- `src/Meitou.Rendering.Vulkan/VkGl*.cs` (2,806 lines), and with it the per-draw translation.
- `WorldGl.cs` (`Program`, `Matrix`, `Texture2D` helpers), `SkyRenderer.AssignSamplerUnits`, `ShadowShaders.Bind`, `MeitouShadowShaders.Bind`
  (units and binding points no longer exist).
- The legacy shader model (`LegacyProgram`, `MoveDefaultBlock`, the stage binding shift) once no program uses it. GLSL sources stay, in the
  native model.
- `VkGlTests`. Its checks of GL semantics are replaced by golden-image tests of the native API and by renderer-level tests. The seam tests go
  with the seam.

Moved:
- `VulkanPresenter` and the upscalers into `Meitou.Rendering`, on native `Texture`s. `Meitou.Rendering.Vulkan` is then empty and removed:
  one project fewer than before the migration.
- The moved `Core/` and `Shaders/` namespaces renamed to `Meitou.Rendering.Gpu.*`.

Updated: docs/engine.md "Backend interface" and "Vulkan backend" rewritten for the native API. DECISIONS 7 marked superseded by 22 (if
adopted), and 18's "the GL-shaped IGl over VkGl stays" sentence amended. The memory note on `IGl` staying is updated by the owner.

The phase-8 gate is the usual one: 0 differing pixels against the last build with VkGl present.

---

## 9. Expected CPU cost, and how the profiler keeps working

*In short: a throwaway measurement on the RTX 4070 recorded the same draws through VkGl and directly. A typical foliage mesh draw costs about
2 µs through VkGl and the renderer's GL-style calls, against about 0.2-0.3 µs recorded directly with the same shaders, and well under 0.1 µs
in the native model. GPU-driven culling then removes the per-instance CPU work altogether. The numbers below say what is measured and what
is estimated.*

### 9.1 Measurements (spike, not in the repository)

A console program in the session's scratchpad, built against this worktree, headless `VulkanDevice` on the RTX 4070, Release,
tiered compilation off as in the game, 20,000 draws of a tiny triangle per frame, median of 7 frames, validation off. The machine was
shared with other agents, so the absolute values are noisy, and the ratios are what matters.

| Path | µs per draw |
| --- | ---: |
| VkGl, simple program, no state change between draws | 0.25-0.32 |
| VkGl, simple program, a matrix uniform per draw | 0.40-0.55 |
| VkGl, simple program, matrix + texture per draw | 0.42-0.53 |
| **VkGl, the real foliage mesh program, a part drawn as `FoliageRenderer.DrawMesh` issues it** (4 texture binds, 15 uniform calls through a `(program, name)` dictionary, VAO bind, 4 `VertexAttribPointer`, instanced draw) | **2.0** (of which VkGl's draw preparation 1.08) |
| direct, the same program, same SPIR-V, legacy layout: fragment block copy (384 B) + dynamic offsets + full set-0 push (2 blocks + 13 samplers) + one vertex-buffer call (11 bindings) + index + draw | 0.30 |
| direct, as above, pushing only the 2 changed textures | 0.18 |
| direct, simple program, legacy layout (block copy, offsets, VB/IB, draw) | 0.09-0.10 |
| direct, push constants (64 B) + draw | 0.06-0.07 |
| direct, `vkCmdDrawIndexedIndirect` with count 1 | 0.06-0.10 |
| direct, bare `vkCmdDrawIndexed` | 0.03 |

The foliage program's vertex default block is **8,272 bytes** (`uBones[128]` from `Shaders.MeshVertex`), its fragment block 384 bytes, with
2 named blocks, 13 samplers and 11 vertex inputs. With validation on, every path costs 2-50 µs per draw. The spike also checked the seam
direction "VkGl draws, then a native segment records into the same frame" (0 validation errors) and the stale-pipeline hazard of 4.1
(also 0 errors: the hazard is invisible to validation).

docs/viewer.md's ~4 µs per draw was measured in real frames. There each draw touches different objects with cold caches, the vertex block
is copied whenever a vertex uniform changes (8 KB for mesh programs), and the renderer's own C# work counts too. The spike runs hot caches
and one program, so its figures are lower bounds for both paths. **Estimate**: in real frames, step P costs ~0.3-0.6 µs per draw and the
native model ~0.1-0.2 µs, against ~2-4 µs today: roughly 5-10× fewer CPU microseconds per draw for step P, and 15-30× for the native model.

### 9.2 Per pass (estimates)

- **Draw overhead.** The forest still camera drew 977 draws per frame after the TERRAIN-mode fix (docs/viewer.md). At ~4 µs that is ~3.9 ms
  of draw overhead per frame. At step P (~0.4 µs) it is ~0.4 ms, and with the native model (~0.15 µs) ~0.15 ms.
- **Foliage** (owner's forest figure 8.7 ms CPU). The parts are per-instance culling (docs/viewer.md: ~1.2-1.4 ms per cascade with the
  candidate cache, and more in the main slices), the per-batch upload, and the draws. Step P removes most of the draw part. A2 removes the
  culling and the upload: the CPU keeps the zone and group walk (hundreds to a few thousand groups) and ~100-300 indirect draws per view.
  **Estimate**: under 1 ms for foliage across all views, from 8.7. The remaining zone walk could also move to the GPU if it shows up.
- **Shadows** (owner's figure 6.1 ms CPU). The foliage casters' share goes as above. Terrain patches (~0.9 ms for cascade 3, docs/viewer.md)
  become cheap native draws with a push constant per node instead of two uniform calls and a block copy.
- **GPU cost of the culling (estimate).** 96 bytes per instance read once per dispatch, all views tested in one pass: 500,000 instances
  ≈ 48 MB, on the order of 0.1-0.2 ms at the 4070's bandwidth, plus the compaction writes (64 bytes per visible instance per view). It has
  to be measured, because the frame's GPU time (7 ms in the forest) then becomes the limit.

### 9.3 Profiler and StageClock

- `StageClock` stages and names stay. Laps stay where they are in `WorldFrame.Draw`, so a stage's CPU time is comparable before and after
  each port.
- GPU timestamps: `FrameProfiler` writes through `QueryArena` and `Interleave` while VkGl exists (section 2.10). After phase 8 it writes into
  the frame's command list directly. GPU-driven views add their compute dispatches to the stage that owns them (foliage, shadows).
- `VkGlStats` (draws, passes, pipelines, uploads, renames, pushes, draw preparation time) sits next to `GpuStats` (2.10) while both exist.
  `--fly-benchmark` prints both, so a port shows draws moving from one line to the other.
- Wave 4: the benchmark reports the render thread's wall time per stage (as now) and the recording jobs' summed CPU time per stage
  separately, because parallel time no longer adds up to frame time.

---

## Open questions

For the owner. Each names the section it comes from.

1. **Hardware floor** (1, 2.6): require descriptor indexing (bindless), multi-draw indirect, draw-indirect-count and shader draw parameters,
   and refuse to start without them? Or keep a push-descriptor fallback (a second descriptor path to maintain)? Driver support has not been
   checked beyond the RTX 4070.
2. **Exactness where the GPU cannot promise it** (3.3, 5.6): if step O or the GPU culling (A2, C2) leaves pixel differences that the
   verifier traces to compiler scheduling or to ≤ 2 ulp fade differences, accept them with the record, or stay on step P / the CPU fade? And
   may A1-style reference-alignment steps change the Faithful picture where they are traced to ulp-level dither flips?
3. **Names of the new switches** (5.7): `foliage-range` (size-based ranges), `impostors`, `occlusion`. Separate switches or one
   "draw distance" switch? Meitou default as usual?
4. **Draw-distance targets** (1, 5.7): "4-5k units" for foliage, junk and objects. Today the foliage setting already defaults to 4× the
   game's ranges (`FoliageRenderer.RangeSetting`, `MEITOU_FOLIAGE_RANGE`, default 4). Is 4-5k the target for small things (junk, rocks, bushes)
   at full density, with large trees further?
5. **Image layouts after phase 8** (4.4): stay in GENERAL (simple, fine on NVIDIA), or move to optimal layouts with `ResourceStates`
   tracking them (possibly faster on AMD; not measured, no AMD hardware here)?
6. **API steward** (7.1): keep the foundation agent running through wave 3 to land additive API changes, or have agents propose them to the
   owner?
7. **The viewer's mesh and character modes** (7.2): port `tools/Meitou.ModelViewer/Renderer.cs` and `CharacterRenderer.cs` (209 IGl calls)
   as they are, or retire them in favour of the world renderer's object path, which characters will need in the game anyway?
8. **Transfer queue** (2.2): move streaming uploads onto the dedicated transfer queue in wave 2, or later? It removes the upload command
   buffer's GPU time from the frame, and its stutters, but adds ownership-transfer or concurrent-sharing work.
9. **Documentation drift** (7.7): docs/engine.md and DECISIONS 2 say "eight" parity views. `parity.sh` renders ten. Correct them in the
   foundation's first change?
10. **Step P everywhere?** (3.2): step P is proposed as mandatory for A, B, C and D (large shaders with shared includes) and optional for E
    and F (small, self-contained shaders, where going straight to the native model is cheap to gate). Agree?
