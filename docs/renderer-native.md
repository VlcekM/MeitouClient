# Native renderer API (proposal)

**Status: built** (2026-10-07). [../DECISIONS.md](../DECISIONS.md) 22 was adopted on 2026-10-06; waves 2 to 4 and phase 8 are done:
`IGl`, `VkGl` and the seam are deleted (8.9), and every renderer records through the native API in `src/Meitou.Rendering/Gpu/`. Sections 0
and 4 describe the starting point and the coexistence seam and are kept as history; the "as built" sections (5.6.1 to 6.6, 8.3 to 8.9)
say what was done. The rest of this note was written as the proposal: the design that the wave-2 foundation agent and the six wave-3
port agents implemented from, and the owner reviewed. It is an engine document: the labels
Verified / Observed / Unknown are for facts about the game, so here claims carry where they come from: *from the code* (a file is named),
*measured* (how is said), *estimate* (with the reasoning), or *open* (a question for the owner, answered in [Owner decisions](#owner-decisions)).

How to read it. Each section opens with a short plain-language summary. The details under it are for the agents. The order follows
the brief: goals (1), the API (2), shaders (3), how old and new code share a frame while the port runs (4), GPU-driven foliage and objects
(5), threads (6), who does what (7), removing the old layer (8), and what it should save (9).

---

## 0. Where we are, in one page

> **Historical** (as written on 2026-10-06, before the port): none of `IGl`, `VkGl` or `src/Meitou.Rendering.Vulkan` exists any more
> (8.9).

The world renderers (`src/Meitou.Rendering`) never talk to Vulkan. They make OpenGL-3.3-style calls through `IGl`
(`src/Meitou.Rendering/Gpu/IGl.cs`), and `VkGl` (`src/Meitou.Rendering.Vulkan/VkGl*.cs`, nine files, 2,806 lines) translates each call
into Vulkan as it arrives. DECISIONS 7 chose this so the renderers did not need rewriting, and DECISIONS 18 kept it when OpenGL was removed.

The price is CPU time. At every draw `VkGl` rebuilds what the draw needs from tracked GL state (`VkGl.PrepareDraw` in `VkGl.Draw.cs`): it packs
the vertex layout into a 20-field `PipelineKey`, looks it up in a dictionary, re-sends the changed dynamic state, copies each dirty
loose-uniform block into a ring, pushes the descriptor set, and binds a vertex buffer for every input location. The renderers add their own
GL-shaped cost on top: uniforms looked up by `(program, name)` dictionary keys, one call per uniform, four `VertexAttribPointer` calls per
instanced draw. docs/render-shadows.md ("Shadow pass cost") measured about 4 µs per draw in real frames. The owner's dense-forest numbers are
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

**Platform.** .NET 10 (`global.json`, SDK 10.0.401 here), Silk.NET 2.23.0 (`Meitou.Rendering.Vulkan.csproj` then, `Meitou.Rendering.csproj` now), warnings are errors, unsafe
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
| `VK_KHR_fragment_shading_rate` (optional: `attachmentFragmentShadingRate`, an R8_UINT image usable as the attachment) | the fog shading rate (`--fog-vrs`, [render-post.md](render-post.md)): the opaque scene passes shade 2 x 2 or 4 x 4 pixels with one fragment where the fog hides the surface. Turned on when present; `VulkanDevice.HasFragmentShadingRate` is false without it (or with `MEITOU_NO_VRS=1`) and nothing of it is built | device extension |

`bufferDeviceAddress` is **not** needed: storage buffers bound as descriptors cover every use here. That keeps clear of the extension-versus-core
issue DECISIONS 17 had to work around for Streamline.

**GPU support (estimate; owner decision 1: required).** The added features are present on every desktop GPU we expect to run Kenshi with a Vulkan 1.3
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

*As built (phase 8 stage 3, 8.9): `Meitou.Rendering.Vulkan` is deleted. `Meitou.Rendering` holds everything above: `Gpu/` (namespace
`Meitou.Rendering.Gpu`, with `Gpu/Core` and `Gpu/Shaders` renamed to `Meitou.Rendering.Gpu.Core` / `.Shaders`), `Gpu/VulkanPresenter.cs`
and `Upscalers/` (`Meitou.Rendering.Upscalers`). The chain is `Meitou.Rendering` ← `Meitou.Rendering.Display` ← game / viewer.*

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
  (`HasDedicatedTransfer`). Moving streaming uploads onto it is not wave-2 work (owner decision 8). It needs either
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
| set 0 `Bindless` | per command buffer | `sampler2D textures2D[]`, `sampler2DArray textures2DArray[]`, `samplerCube texturesCube[]`, `sampler2DShadow shadowTextures[]`, `usampler2D utextures2D[]`, `isampler2D itextures2D[]` (bindings 0 to 5, `BindlessTable.GlslDeclarations`): combined image samplers, partially bound, update-after-bind | once per command buffer (secondaries too) |
| set 1 `Frame` | per frame | `FrameConstants` UBO: atmosphere (everything `SkyRenderer.Apply` sets today), lighting, fog, water height, shadow receiver block (`KenshiShadowReceiver`, 560 B), Meitou shadow block, time; plus the bindless indices of the frame's shared textures (shadow atlas, noise, terrain shadow, blocker map, irradiance and specular cubes, ambient map, terrain heights) | once per frame |
| set 2 `View` | per view / pass | `ViewConstants` UBO at a dynamic offset into the frame's `LinearAllocator`: view-projection (jittered as drawn), eye, frustum planes, jitter, depth-slice planes, clip plane, LOD eye | once per view |
| set 3 `Pass` (optional, per renderer) | per pass | storage buffers the pass reads: material table, instance stores, per-view compacted instances | once per pass |
| push constants (≤ 128 B, the guaranteed minimum) | per draw | material index, instance base, small per-draw values (terrain `uNode` and `uMorph`, grass patch bounds and sizes) | per draw |

**As built (steward, wave 3b; `NativeShaders`, `NativeFrame`).** Two sets and one push range, not four sets:

| Set / range | Contents | Bound |
| --- | --- | --- |
| set 0 `Frame` (push-descriptor set) | binding 0 `FrameConstants` (the 13 atmosphere values, the weather's wetness and dust, and the bindless indices of the 9 frame textures, 288 B), 1 `KenshiShadowReceiver`, 2 `KenshiShadowCaster`, 3 `MeitouShadowReceiver` (the GL buffers as bound now), 4 `ViewConstants` (192 B), 5 `MeshBones` (8 KB, allocated once a frame, unread until a consumer skins) | once per native segment (`NativeFrame.Bind`) |
| set 1 `Bindless` | the table above (`BindlessTable.Declarations(1)`) | once per native segment |
| push constants, 128 B, vertex + fragment, the same range in every native program | `MeshPush` (tint, material values, the bindless indices of the 2 to 6 textures) or a consumer's own (`GrassPush`, 72 B) | per draw, only when the bytes differ |

- *Per segment, not per frame.* The shadow blocks are VkGl buffers it renames, and the caster block is rewritten for each cascade, so they are
  read when the segment begins, as a legacy program reads them at its first `Flush`. The frame block is rewritten only when its bytes changed
  within the frame (the usual case: one 240-byte write a frame). The view goes to a set binding, not a dynamic offset: one write per segment.
- *The pushed set is set 0, the table set 1* (the proposal had the table at 0). With the table at set 0 and set 1 pushed, the next legacy
  program's push of its set 0 crashed the validation layer (SDK 1.4.363) inside `vkCmdPushDescriptorSetKHR`, with no error reported before.
  **Observed**: `SeamTests.A_native_model_segment_then_a_legacy_segment_then_VkGl_each_draw_their_own(pushed: true)` crashed the test host
  (`0xC0000005`), and the forest view crashed at the first terrain-mesh `Flush` after the foliage's segments, under `MEITOU_VK_VALIDATION=1`
  and `=sync`; a minidump of the viewer puts the fault inside `VkLayer_khronos_validation.dll` (no driver frame). A set 1 written per segment
  from the frame's pool (not pushed) fixed the test but not the viewer; legacy programs forced to allocated sets ran clean. With the push at set
  0 for both models (the table at 1) both run clean, 0 errors. Without the layer the two orders draw the same pictures (the ten parity views
  0 px, 7.1). Treated as a layer bug, not reported upstream yet; keep the push at set 0 while legacy programs exist.
- *Frame textures* are bindless entries `NativeFrame` registers per consumer: a new index whenever the texture's view or sampler changed (the
  old one freed after the frames in flight), so a segment recorded earlier keeps what it was given; 7 compares per segment.

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
journal that is replayed into a slot when that slot's frame begins. Registration and freeing happen on the render thread,
and frees are deferred to the frame fence. The sampler is part of the slot, since combined image samplers reproduce GL's per-texture
sampler state exactly.

*Integer arrays (wave 3b steward addition).* A UINT or SINT texture cannot be read through a float sampler (the view's numeric format must
match the shader's sampled type), so the table has `utextures2D[]` (binding 4) and `itextures2D[]` (binding 5), 2D only (no shader in the
tree samples an integer array or cube; the one integer sampler today is the terrain's `usampler2D uCells`, GL `RGBA8UI` =
`R8G8B8A8Uint`). 256 entries each. `BindlessTable.KindFor(format, kind, shadow)` picks the array (UINT → `UTexture2D`, SINT → `ITexture2D`,
depth read with comparison → `Shadow2D`, the rest by kind; integer arrays or cubes and shadow on a non-depth format throw), and
`Register(Texture, Sampler, shadow)` uses it and returns a `BindlessHandle(Kind, Index)`. `BindlessTable.GlslDeclarations` (or
`Declarations(set)`) is the GLSL for the six arrays with `GL_EXT_nonuniform_qualifier`; the native prelude includes it. Reflection reports
runtime-sized sampler arrays (`SamplerInfo.ArrayLength` = -1), and `ShaderLibrary.Native` checks every sampler a program declares in the
table's set against `BindlessTable.Check` (binding, element type, runtime-sized) and refuses a mismatch at load; `ShaderLibrary.Compute`
refuses a runtime array in its own set 0 (bind the table as an extra set: `Declarations(1)`). **Verified** by `BindlessTests`: RGBA8UI,
R16UI and R16I textures read back exact values (`texelFetch`, written to an RGBA32UI target), the reflection of `utextures2D` /
`itextures2D`, a `usampler2D` declared at binding 0 refused.

*Same-frame registration (wave 3b steward addition).* An index registered or updated while a frame is open is usable in that frame. Design:
the journal is also replayed into the open frame's own set at `GpuFrame.End`, before VkGl submits. This is valid Vulkan because every binding
has `UPDATE_AFTER_BIND` (the descriptors are consumed when the command buffer executes, so writing a set that is already bound in the command
buffer being recorded is allowed until submit) and the open frame's set is not pending (its slot's fence was waited on at `BeginFrame`). One
batched `vkUpdateDescriptorSets` per frame, no per-registration write, no extra set copies, nothing new for synchronisation validation
(descriptor writes are host operations on a set no submitted work uses). Rejected alternatives: writing the current set at once on every
`Register` (same validity, more calls); per-frame copies of the whole table (`vkCopyDescriptorSets` of up to ~18,000 entries per frame);
`UPDATE_UNUSED_WHILE_PENDING` alone on a shared set (an in-place update of an index in use would race frames in flight). Consequences:

- **An in-place `Update` during a frame is seen by every draw of that frame that reads the index, including draws recorded before the call**
  (descriptors are read at execution, not at record time). That is not GL's per-draw snapshot. To change what a draw sees part-way through
  a frame (a GL texture whose view or sampler VkGl recreates between two draws), register a new index and free the old one: the export
  below does exactly that. `Update` in place is for an entry not used yet in the frame, or between frames. **Verified** (`BindlessTests`).
- Replay writes each (array, index) once, newest entry first, and `Free` journals a tombstone: at a frame's begin an index freed later is
  not written (its view may already be destroyed: with no frame in flight `DeferDelete` destroys at once), at the end of the open frame it
  still is (the frame's draws may have used it before the free; its view lives until the frame finishes). **Verified**: without the
  tombstone the export test below produced `vkUpdateDescriptorSets` invalid-view errors.

*Export of a GL texture's index.* `IGlInterop.Bindless(glTexture, shadowSampler = false)` returns a `BindlessHandle` for the sampler and
view VkGl would bind now (`Sampled`), in the array `KindFor` selects. While neither the view, the sampler nor the LOD bias changed it
returns the same handle (a dictionary-free compare on the texture object); after a change it registers a new index and frees the old one,
so earlier draws of the frame keep theirs, as in GL. 0, or a texture without storage, gives the stand-in's entry (as `Sampled`); that is
the *float* 2D stand-in (`Kind = Texture2D`), so a port reading an integer array must check `handle.Kind` (or the steward adds a
`Bindless(uint, SamplerInfo)` overload when one needs it). Like `Register` and `Free`, it is render-thread only (`Free` now journals a
tombstone; it is no longer just a deferred push, which matters for wave 4, 6.3). The entries
are freed with the texture's storage (`DeleteTexture`, re-specification). Cost per call: one `SamplerAndView` (cached in VkGl) and one
compare. **Verified** (`BindlessTests`): an RGBA8UI GL texture made as `TerrainTextures` makes `uCells` reads back exact values through its
exported index; the handle is stable while nothing changes and new after `TEXTURE_WRAP_S` changes; both indices are handed out again after
`DeleteTexture` and the frames in flight. Note: the heat haze's mid-frame LOD bias of 0 (below) makes a texture looked up both inside and
outside it alternate between two indices each frame (one register and one deferred free per change); harmless, but a renderer that knows
it should register its own bias-0 entry instead. When the upscaler's LOD bias changes (`ITextureLodBias`, set by `PostProcess.Begin`, reset to 0 for the heat
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
- *As built (steward additions for A2, 2026-10-06; additive).* **`GpuFrame.PreFrame`**: a `CommandList` recording into the frame's upload
  command buffer, which VkGl submits ahead of the frame's own in the same submission, with a full barrier at its end. Whatever is recorded there,
  at any point of the frame's recording (inside a native segment or a host pass too, where a dispatch is impossible), executes before every
  command of the frame's own list and after the uploads recorded before it; the caller places its own barriers between its commands. That is
  where a cull per view goes (5.3 as built). Compute workgroup sizes are written in the GLSL (`local_size_x = 256`; `ShaderLibrary.Compute`
  takes no specialisation). **`ReadbackBuffer`**: host-visible cached memory a copy lands in, read once `ReadbackBuffer.Completed(ctx, frame)`
  (the frame ring's completed frame), never waited for. `CommandList.DrawIndexedIndirect` goes through a cached device entry point like the
  other per-draw commands. **Verified** by `GpuApiTests.A_dispatch_recorded_into_PreFrame_runs_before_the_frames_own_commands` (synchronisation
  validation on): an indirect draw and a copy recorded first in the frame's list read the arguments a dispatch recorded later into `PreFrame`
  wrote; the pixels and the read-back arguments are the dispatch's.

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
  If a renderer cannot reach 0 differing pixels in step O, it stays on step P or, under owner decision 2, is accepted at 1/255 per channel with the case documented.
- **Shared shader text is ported once**, by the foundation as API steward, before wave 3b: the native variants of `Shaders.MeshVertex` /
  `MeshFragment` (used by foliage, buildings and the viewer's mesh and character renderers), `AtmosphereShaders.Functions` (which embeds
  `ShadowShaders.Functions`), `ShadowShaders.DepthFragment` / `MeshDepthFragment`, and `PostProcessShaders.Vertex`. Agents then build their
  own shaders on those.

**As built (steward, wave 3b).** `NativeShaders.Port(legacy, map, pushMembers)` makes a native variant: `#version 330 core` becomes
`#version 450` plus `Prelude(pushMembers)` (the table, the frame, view and bones blocks, the `Push pc` block, `#define gl_VertexID
gl_VertexIndex`); every loose `uniform T a, b;` becomes one `#define` per name from the map (`FrameMap`, `ViewMap`, `MeshMap`, merged by
`Map(own)`), and an unmapped name throws; `layout(std140) uniform Block` gets `set = 0, binding = N` (`BlockBindings`). Nothing else changes:
**Verified** by `NativeShaderTests` (every non-declaration line of `Shaders.MeshFragment` is in the native text, in order; every native variant
compiles through `NativeFrame` with strict rules, its `FrameConstants`, `ViewConstants` and `Push` members sit at the C# structs' offsets,
every sampler is in the table's set). The variants: `MeshVertex(own)` (the consumer maps `uModel`, e.g. to its instance rows),
`MeshFragment()`, `MeshDepthFragment()`, `DepthFragment`, `AtmosphereFunctions`, `ShadowFunctions`, `PostProcessVertex`. The C# sides:
`FrameConstants` (288 B since the weather values, with `Uniforms` and `Textures` tables naming the frame globals), `ViewConstants` (192 B), `MeshPush` (128 B). The
first consumer, the foliage (7.1), drew **0 differing pixels** on every gate picture, so the 1/255 allowance of owner decision 2 was not used
and no `precise` / `invariant` was needed.

### 3.4 Compiler and reflection changes

- `ShaderCompileOptions` gains `Model` (`Legacy` or `Native`) and `Stage` kinds (vertex+fragment pair, or compute). **Legacy output must not
  change**: the cache version stays 3 for legacy programs, and a test keeps the SPIR-V of every world program identical to what master
  produces today. Native programs get their own cache version (4) in the key.
- `SpirvReflection` already knows `BlockKind.PushConstant` and `StorageBuffer`. It gains runtime arrays, specialisation constants and
  workgroup size. For native programs, reflection *checks* the shader against the model (sets, bindings, push-constant size ≤ 128) at load
  in debug builds. The layout comes from the model, not from reflection.
- `InterfaceLocations`, the clip-depth remap and `MEITOU_FRAGMENT` stay as they are for both models.
- *As built (wave 3b).* `ShaderLibrary.Native` compiles with cache version 4, runs `InterfaceLocations.Apply` on the pair as the legacy path
  does, and refuses a push-constant block larger than the layout's range. **Verified**: `LegacySpirvGoldenTests` hashes the vertex and
  fragment SPIR-V of the 27 world programs (with and without the clip-depth remap) against a table generated at master `365bb88`; it passes
  on every steward commit, so the legacy output is byte-identical.

---

## 4. The coexistence seam

> **Historical.** The seam was removed in phase 8 stage 3 (8.9) together with `VkGl`, `IGlInterop` and `GlBridge`. What survives of
> it is in `GpuContext`: the frame loop, `BeginNative` / `EndNative` with their full barriers, host passes and guests, `Interleave`,
> `FrameGlobals`. Read this section as the design the port ran on, not as the current code.

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
    /// (Added in the pilot's step 1.) A native segment inside VkGl's own rendering instance: the pass a VkGl draw would draw into now
    /// is kept open (or begun exactly as a VkGl draw begins it) and stays open after EndNative. No EndRendering, no barriers, no
    /// BeginRendering of the caller's own: the native code records draws only (no copies, dispatches or barriers). EndNative then
    /// invalidates the same caches but places no barrier and keeps the pass. The IGl guard applies as for BeginNative (also on the draw
    /// path, which does not go through the command-buffer getter while a pass is open).
    CommandList BeginNativeInPass(string label);
    /// Records a command that does not disturb a render pass (a timestamp, a debug label) into the frame without ending VkGl's pass.
    void Interleave(Action<CommandList> record);
    /// (Added for the native hosts, wave 3 step H, 4.5.) A native host that has begun its own rendering instance inside its BeginNative segment
    /// announces it: until EndHostPass, a guest's BeginNativeInPass returns the host's list (no pass of VkGl's is touched), and CurrentTargets,
    /// CurrentState and GetInteger(Samples) answer as before from the GL framebuffer binding, viewport, scissor and fixed-function state the host
    /// keeps set. Uploads and timestamps through IGl stay allowed inside it; a clear, draw, blit or flush still throws.
    void BeginHostPass(CommandList cmd);
    void EndHostPass(CommandList cmd);

    /// What VkGl is drawing into right now: the bound draw framebuffer's attachments as textures, and GL's viewport and scissor. While any
    /// VkGl code remains, the GL framebuffer binding is the source of truth for "the current pass" (4.5).
    PassTargets CurrentTargets();

    // export: GL objects to native code (non-owning; valid while the GL object lives)
    Texture Texture(uint glTexture);
    /// The view and sampler VkGl itself would bind for this texture now (its SamplerAndView cache: GL parameters, defined levels, base and
    /// max level, swizzle, the current LOD bias). Identical by construction.
    SampledTexture Sampled(uint glTexture, bool shadowSampler);
    /// (Added for wave 3b.) The bindless entry of what Sampled returns now, usable in the current frame; a new index whenever the view,
    /// sampler or LOD bias changed (the old one freed after the frames in flight), freed with the texture's storage (2.6).
    BindlessHandle Bindless(uint glTexture, bool shadowSampler = false);
    /// The buffer's current version (dynamic buffers move every frame; a static one renamed after a write), marked as used by this frame.
    BufferBinding Buffer(uint glBuffer);

    // import: native objects to GL code
    /// A GL texture name for a native texture (borrowed: DeleteTexture forgets it but does not free the image). Unported code can attach
    /// it to a framebuffer and sample it, with GL sampler parameters of its own.
    uint Import(Texture texture);
    uint ImportBuffer(DeviceBuffer buffer);
}
```

*`VertexArrayStamp` (wave 3b steward addition, `cbc94d9`).* `long VertexArrayStamp { get; }` moves whenever a `VertexArray(vao)` export may be
stale: a buffer some export names gets new storage, a new version or a rename; a vertex array is changed or deleted. A caller that keeps
the stamp per mesh skips the per-draw `VertexArray` call (0.35 us a draw measured in the probe) while it is unchanged. Writes to buffers no
export names do not move it. **Verified** by `SeamTests.The_vertex_array_stamp_moves_exactly_when_an_export_may_be_stale`.
*Changed by the step-O hot-path work (7.1, `3152deb`):* it no longer moves at every frame's begin. Before, an export marked its buffers used
per frame, so the stamp moved per frame and every mesh fetched its export again in the frame's first segment; foliage draws each mesh once
per segment kind, so that was one `VertexArray` call per draw (0.3-0.4 us). Now a buffer an export has named counts as read by every frame
until it gets fresh storage in that frame (`VkGl.ReadByFrame`, `8ec1d22`): its first write of a frame takes new memory and moves the stamp,
exactly as after a draw, and later writes of the frame go in place. Conservative (a named buffer no draw of the frame reads is renamed too
when written). Measured: the forest still run renames 600 buffers in 301 frames before and after, so nothing writes such buffers in steady
state. The first version (`3152deb`) marked the named buffers at each frame's begin instead; that loop cost 43-49 us a frame for 585 buffers
(cold objects), so it was replaced by the check at the write, which costs nothing per frame.

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
    // consumers (LegacyProgram.Create binds every name it finds in its reflection; native-model programs read the frame set, set 0 as built: 2.6)
}
```

- An owner still on VkGl publishes through the export calls. For example `ShadowPass` publishes `uShadowMap` as
  `() => interop.Sampled(atlas, shadowSampler: true)`, and `SkyRenderer` publishes its uniform values from the state `Apply` reads.
  The foundation adds these publish calls to the owners in wave 2, before the agents start. They change no GL call, so no pixel.
- After the owner's port it publishes native objects instead. Consumers do not notice.

### 4.4 Layouts and barriers

- **Every image stays in `GENERAL`** until phase 8 is done. VkGl assumes it everywhere (attachments, sampling, transfers, the upscalers
  with `ImageLayout.General` tags). A native side using optimal layouts would have to transition back at every seam crossing, for every image
  VkGl might touch. Whether optimal layouts are worth it afterwards stays open until after phase 8 (owner decision 5): NVIDIA treats GENERAL like the optimal layouts for
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
    binds it before calling the guest. That is the GL code the host had, kept for the guests until all of them are native. (Not used: both
    hosts went straight to the next case.)
  - *host native, guest native* (wave 3 step H: `ShadowPass` in both modes and `ReflectionPass`; **Verified**, 0 px, 7.1). The host keeps the GL
    side exactly as it was, minus the calls that record: its GL framebuffer stays bound (so the targets, formats and sample count the guests read
    through `CurrentTargets()`, `CurrentState()` and `GetInteger(Samples)` are the host's), and so do the GL viewport, scissor and fixed-function state
    it sets for them (depth test and compare, depth clamp, colour mask, blending: pure state calls, which record nothing). Then:
    1. `cmd = interop.BeginNative(label)` (ends VkGl's pass, full barrier), `t = interop.CurrentTargets()`, `interop.BeginHostPass(cmd)`;
    2. `cmd.BeginRendering` on `t` with the host's load ops: `CLEAR` where the GL code cleared the whole attachment (the atlas when every cascade
       is drawn; the reflection's colour and depth), `LOAD` otherwise;
    3. per cascade (or depth slice): the GL viewport and scissor (state), the caster-bias uniform buffer write (an upload, allowed), a partial clear
       as `cmd.ClearDepth(1, rect)` (`vkCmdClearAttachments` inside the instance, as `VkGl.Clear` records it: a Meitou tile, the reflection's depth between slices),
       then the guest: unchanged, its `BeginNativeInPass` returns `cmd` and records into the host's instance, its `EndNative` ends only its segment;
    4. `cmd.EndRendering()`, anything between instances (the reflection's `Barrier(Full)` and `Resolve(src, dst)` of the 4x colour into the sampled texture),
       `interop.EndHostPass(cmd)`, `interop.EndNative(cmd)` (full barrier, VkGl forgets what it believed bound).
    Guests need **no change**: the three seam calls resolve to "the current pass", which is the host's while a host pass is open. The guard
    stays strict where it protects the host: a draw or clear through `IGl`, `BeginNative` and a second `BeginNativeInPass` while a segment is open throw.
    **Limits for wave 4 (6).** The ambient host pass is one field and the state the guests read is VkGl's one GL mirror, so this is a single-thread
    shape: it moves the pass lifecycle, the clears, the resolve and the barriers to the host and puts the guests' recording into the host's command list.
    Splitting a cascade onto its own thread needs (a) the host pass per thread (a `[ThreadStatic]` or a field of the thread's `CommandList`/job, with
    `BeginNativeInPass` returning that job's list), (b) the viewport and scissor of the job carried by the host instead of read from GL state (the host
    already knows them: they are what it sets per cascade), and (c) each guest taking its cull, depth and bias state from a `DrawState` the host gives
    it instead of from `gl.Enable`/`gl.CullFace` calls it makes before `CurrentState()` (that is the guests' Prepare/Record split of 6.2; the GL calls are
    only state, and the state they set is already in one place per guest). The load ops, clears and resolve are already per-instance, so each cascade
    can be its own primary (6.1: "whole passes that have nothing to share") with the same host code.
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
  (docs/render-shadows.md), so a view costs on the order of 100-300 indirect draws.
- **Later (wave 3b+)**: meshes in one shared vertex and index arena, one `DrawIndexedIndirectCount` per (view, pipeline), the material from
  a per-draw table indexed by `gl_DrawID`. That needs step O's bindless materials.
- **TERRAIN-mode rocks** stay CPU-culled until agent B provides an instanced main-pass TERRAIN-mode path. Today only the depth path is
  instanced (`TerrainShaders.MeshInstancedDepthVertex`). The main pass draws one draw each with a uniform matrix, through
  `TerrainShaders.MeshVertex`. A new instanced main-pass shader is a shader change with its own gate. *Since then:* the terrain's step O made
  both passes instanced (7.1), and the rocks are now culled on the GPU too (5.6.1, "TERRAIN-mode rocks"), drawn through
  `TerrainRenderer.DrawMeshesIndirect` with the terrain's own programs unchanged.
- **Grass**: in step P, a native draw per (page, patch) with push constants for the patch values. GPU-driven grass (page and patch tests,
  density prefix and range into indirect arguments) is a wave 3b item. Its page order (nearest first, `grassOrder`) only affects early
  depth rejection, plus the tie risk of 5.6.

### 5.5 Shadows per cascade, and the reflection

- Each cascade is a view. The candidate set ("in range of the camera eye, within 1.2 × the shadow range") is the view's range cap, measured
  from the camera eye exactly as `DrawDepth` passes `view.Eye`. There is no separate candidate pass.
- In the atlas the casters are drawn depth-only with `LESS` (`ShadowPass.Render`), so the result is the minimum depth and **does not depend
  on draw order** (docs/render-shadows.md makes the same argument for the instanced TERRAIN-mode fix). Cascades are the safest place to start
  GPU-driven drawing.
- The reflection target is 4× multisampled (`ReflectionPass.Samples`), so the foliage there draws **with alpha-to-coverage**
  (`FoliageRenderer.Draw` keys it on `GLEnum.Samples > 1`). The native path must take the sample count from the target, not assume 1. In
  the main scene it is off: the scene is single-sampled (docs/render-post.md).

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
  base runs' exactly (docs/render-foliage.md).
- **A2, GPU port.** The same formulas on the GPU. The remaining divergence is the square root inside the fade (`d` for w). A **cull
  verification mode** (`MEITOU_GPU_CULL_VERIFY=1`) runs the A1 CPU cull as well, reads the GPU's per-view lists back a frame later, and
  reports any difference in visible sets (must be none), order (must be none) and fade (each difference with its ulp distance). The pixel
  gate is 0 differing pixels. If it fails only where the verifier shows fade differences of ≤ 2 ulp, owner decision 2 applies (at most 1/255 per channel, documented); the options were:
  accept with the record, or keep the CPU fade (the GPU decides visibility, the CPU-identical fade is recomputed per instance in the vertex
  shader from the same inputs, at the same precision issue), or emulate a correctly rounded square root.
  **Done (2026-10-06), with the last option**: the kernel's `CrSqrt` takes the driver's `sqrt` and corrects it to the correctly rounded root
  (below), so the fade is bit-identical too and the allowance of owner decision 2 was not used. Verify mode and gate in 7.1 ("step A2").

**Draw order and depth ties.** Inside a batch, the GPU order equals the CPU order (5.3). Between batches, A1 fixes the order. Between depth
slices nothing changes, because every slice is its own view, drawn where the CPU drew it. Grass is drawn after the meshes, as today.

### 5.6.1 As built (step A2, 2026-10-06): `FoliageGpuCull`

*In short: the CPU still walks zones and groups exactly as A1 does, but instead of testing each instance it writes a short work list; three
small compute kernels, recorded so that they run before the frame, test the instances and write the draw arguments; the draws are recorded
where the CPU path drew, one indirect draw per mesh part. Pictures: 0 differing pixels, and the read-back lists equal the CPU's bit for bit.*

Where it differs from 5.2 to 5.5, and why:

- **One cull per view, at the view's own `Draw` call, recorded into `GpuFrame.PreFrame` (2.8), not one dispatch for 8 views.** The views'
  frustums are only known when each caller draws (the cascades inside `ShadowPass`'s host pass, the reflection inside its host pass, the
  slices in `WorldFrame`), a dispatch cannot be recorded inside a rendering instance, and `BeginNative` is refused inside a host pass. The
  frame's upload buffer runs before all of them, so each `Draw` records its cull there and its draws where it is. The frame order of the
  passes is unchanged; nothing outside the foliage files moved. Wave 4 can keep this: the cull is part of a view's Prepare.
- **Instances (5.2)** are a `BufferArena` (`foliage instances`, 64 MB, `Storage`), one range per group (a mesh in a zone) rather than per
  zone, uploaded at the group's first cull (its spheres need the mesh's bounds, known once the mesh is resident), freed when the zone is
  dropped or a whole layout replaces its groups. A full arena is replaced by one twice the size and every group is placed again (a
  generation number per group). The arena is never marked as used by a frame: a range is written (upload buffer) only before any chunk names
  it. Measured use: 2.4 MB in the forest (26,513 non-rock instances), 4 MB at range ×8.
- **Work list (5.3).** `CollectWork` (the zone walk of `CullZones`, unchanged), then `BuildGpuWork`: batches numbered in first-candidate
  order (A1's order), chunks of ≤ 256 instances of one group **grouped by batch** with a counting sort (each batch's groups in work-list
  order, each group's instances in order), so a plain prefix over the chunk list gives each batch a contiguous range in the CPU's emission
  order. A chunk is 32 bytes: arena index, count, and the group's `Range`, `RangeSquared`, `InverseBand` as the CPU computed them
  (`FoliageGroupRange.Of`). The shadow cascades of a frame share one work list (same eye and range, no zone box test: A1's candidates), its
  chunks written once a frame.
- **Kernels** (`FoliageShaders.CullCompute`, `ScanCompute`, `CompactCompute`; 256 threads): *cull*, a workgroup per chunk, writes each
  instance's packed fade (or −1) and the chunk's visible count (a shared-memory atomic: a count, so order-free); *scan*, one workgroup, an
  exclusive prefix of the counts in chunk order (each thread a run of chunks, then a Hillis-Steele scan of the 256 run sums) into the chunk
  offsets, and each draw's `instanceCount` and `firstInstance` from its batch's chunk range; *compact*, a workgroup per chunk, writes each
  visible instance's rows (row 0 w = the fade) at the chunk's offset plus its rank (a shared-memory scan of the visible flags). Per view, in
  device memory of the frame's slot (32 MB chunks, bump-allocated, reused when the slot comes round): 4 bytes of fade and 64 bytes of rows per
  candidate, the counts, offsets and arguments. Barriers: transfer to compute before (the arena's uploads), compute to compute between the
  kernels, compute to indirect, vertex input and transfer after.
- **The split (early depth test, 2026-10-09, **Observed**)**: in the colour views (`Push.split`) the kernels lay each batch out as its fully visible instances, then the ones the mesh shader dithers (fade below 1; a rock only in its impostor transition), so the first can be drawn by programs without a `discard`. *cull* also counts the fading ones per chunk (counts [3n, 4n)); *scan* makes two prefixes, S of the solid counts and F of the fading, and the chunk's solid instances go at S(c) + F(cs), its fading ones at S(ce) + F(c) (the chunk carries its batch's chunk range [cs, ce) as `BatchStart` / `BatchEnd`, 48 bytes now); offsets: S at [0, n], F at [n + 3, 2n + 3], the visible total at 2n + 4; arguments: three sets per draw (the whole batch, its solid instances, its fading ones). With `split` 0 F is 0 and the layout is the old one. See [render-foliage.md](render-foliage.md).
- **Draws (5.4).** The rows are bound at locations 7 to 10 at the view's region, each mesh part is `DrawIndexedIndirect(args, 20 × i, 1)`;
  `FoliageShaders.MeshVertex` is unchanged. Every candidate batch has its arguments, an empty one 0 instances; but a batch whose groups'
  sphere bounds (a box round all its instances' spheres, one unit of slack, computed with the spheres) all lie outside the view is not drawn
  at all (`ShowBatches`): a cascade's work list has no zone test, and recording ~60 empty draws a cascade cost more CPU than the cull saved
  (forest flying: 107 against 26 depth draws a call before this test, 50 after). A batch left out has no visible instance, so the picture is
  the same; the verify mode checks the GPU found none there.
- **TERRAIN-mode rocks** (as first built in A2: on the CPU, `FoliageCull.CullGroup` per view and `TerrainRenderer.DrawMeshes`, then most of
  the foliage cull's CPU time; now on the GPU, 2026-10-06, 7.1 "TERRAIN-mode rocks"). They go into the **same work list and dispatch** as the
  other meshes, as extra batches after the mesh batches:
  - *What the CPU path gives the terrain, and how the GPU reproduces it.* `EmitAll` / `Emit` hand `DrawMeshes` a rock only when its fade
    w ≥ 0.5 (the terrain shader has no dither), with M14 = 0, one placement per mesh part. `GroupMeshes` groups the placements by (vertex
    array, index count, mirroring: `GetDeterminant() < 0`), in order within a group, and in colour writes row 0 w = the biome row
    (`TerrainTextures.FeatureBiomeRow`: the biome map's row at the placement's x, z when that biome is resident, else -1); depth leaves 0.
  - *Batches.* A rock batch is one mesh's plain placements, or its mirroring ones (both kinds can sit in one group): `(asset, mirrored)`,
    numbered by first candidate in the work list (a group's first placement's kind first). Each part of the mesh is one indirect draw of the
    batch. `ShowBatches` covers them (each rock group's sphere box).
  - *Per instance, once per group:* `Ground.W` (unused by anything else) = `FoliageCull.RockBits`: 1024 when the placement mirrors, plus the
    biome map row + 1 regardless of residency (`FeatureBiomeRowAny`), with the same C# calls `GroupMeshes` makes per frame. Made again (and the
    group placed again in the arena) only when the terrain's textures object changes (`TerrainRenderer.FeatureBiomes`).
  - *Per chunk:* `FoliageCullChunk.Flags` (was padding): `Rock`, `Mirrored`. A rock group with both kinds has one chunk run per kind over
    the same arena range, each keeping only its own kind, so a batch holds (work-list group order, index order) as `GroupMeshes` does.
  - *Per view:* the kernels' View buffer grows from 160 to 208 bytes: a residency bit per biome row (256 bits, from
    `TerrainTextures.ResidentBiomeBits` at the view's `Draw`, where the CPU path reads residency too) and the biome-rows switch (colour views,
    the reflection included; off in the cascades).
  - *Kernels:* `cull` adds, for rock chunks, `!(w < 0.5)` (the precise w the fade already uses: exact) and the mirroring match; `compact`
    writes rock rows with row 0 w = biomes ? (resident ? row : −1) : 0. Mesh chunks (flags 0) take the old paths unchanged.
  - *Draws:* the rock draws' arguments follow the mesh draws' in the same arguments buffer; `TerrainRenderer.DrawMeshesIndirect(meshes,
    rows, args)` records them at the old place (after grass) with the same programs, segment set-up (now `OpenMeshSegment`, shared with
    `DrawGroups`), per-mesh native cache (`meshGroups`) and GL state left behind as `DrawMeshes`; one `DrawIndexedIndirect` per (part,
    mirroring). `DrawMeshes` (objects' map features, and the CPU path) is unchanged.
  - *Order.* The CPU path orders the terrain groups by first *visible* placement; the GPU path by first candidate. Only exact depth ties
    between two different rocks in colour could show it (depth passes keep the nearest); measured 0 px (7.1). A second difference: when a rock
    batch is shown but nothing in it is visible, the GPU path still runs the colour set-up (`BindUnits`, `BindHeightUnits`, `textures.Bind`,
    `PrepareConstants`) and records a segment of empty draws, where `DrawMeshes` returns before any of it. Also 0 px; the first suspect if a
    later picture differs where no rock is drawn.
  - *Verify mode:* the CPU's `terrainDraws` grouped as `GroupMeshes` would (biome row filled in colour), compared per (vertex array,
    mirroring) with each rock draw's arguments and rows, all 16 floats bit for bit; a group with CPU placements but no GPU draw is a difference.
- **Statistics.** `DrawnInstances` adds the GPU's count of the view with the same call number a frame ring earlier (copied into a
  `ReadbackBuffer`), since the CPU no longer knows it when it draws; `DrawCalls` counts the indirect draws.

**Parity, as built (5.6).**
- Every decision is `precise` and written out in the C#'s order (`dx * dx + dz * dz`, `((p.x * s.x + p.y * s.y) + p.z * s.z) + p.w`,
  `-s.w * length`), the range as `!(d2 >= rangeSquared)`, the threshold 0.999 passed as the C# float. Vulkan requires single-precision add
  and multiply to be correctly rounded, so these match `FoliageCull` bit for bit.
- **The driver's `sqrt` is not correctly rounded** (**Observed**, RTX 4070, test `FoliageGpuCullTests.CrSqrt_is_the_correctly_rounded_root`:
  176,979 of 1,048,576 squared distances, 17 percent, get a root 1 ulp from `MathF.Sqrt`, none further). `CrSqrt` corrects it: `r` is the
  correctly rounded root of `x` exactly when `x` lies strictly between the squares of the midpoints next to `r` (no tie is possible), and
  both squares are compared exactly as 64-bit integers (`umulExtended` of the mantissas, shifted by the exponents); otherwise `r` moves by an
  ulp, at most four times. Zero, denormal (a ground distance under 1e-19) and non-finite inputs keep the driver's value. Same test: 0 of
  1,048,576 differ, including exact squares and midpoint squares and their neighbours. Without the correction the synthetic cull test fails
  on the first instance whose fade lands on such a root (checked).
- **Verified** by `FoliageGpuCullTests.Gpu_cull_matches_FoliageCull_on_synthetic_data` (six views, 40 groups in 5 batches, a third of the
  instances within ±2e-7 of their range, a fifth of the spheres moved onto a plane's limit, one view with 4 planes, an arena that grows):
  visible sets, order, all 16 floats of every matrix and every draw's arguments equal the CPU's.
- **Verify mode** (`MEITOU_GPU_CULL_VERIFY=1`): each `Draw` runs the A1 CPU cull too (which also feeds the TERRAIN-mode rocks), keeps its
  batches, and copies the GPU's offsets, arguments and rows into a `ReadbackBuffer`; a frame ring later they are compared (visible counts per
  batch, every matrix but the fade bit for bit, the fade by ulp distance, every draw's arguments, the batch order, and that a batch not drawn
  had nothing visible). The summary line is printed when the renderer is disposed.


### 5.6.2 As built (wave 3b, GPU grass, 2026-10-06): `FoliageGrassGpu`

- **Storage.** Every grass page's blades live in one arena (`MEITOU_GRASS_ARENA_MB`, 160 MB by default), a vertex buffer read at instance rate, so
  a blade draw is `Draw(vertices, blades, 0, firstBlade)` from one binding. Each (page, grass type) takes a slot in a table
  (`MEITOU_GRASS_SLOTS`, 32,768) that the kernels read; pages leave the arena after the frames in flight. A page that finds no room is counted
  ("pages without room") and not drawn.
- **Per view** (main slices, the grass motion of the near slice): the CPU writes a patch table into the frame's constants, and two kernels in
  `GpuFrame.PreFrame` choose the pages in view (the same frustum and range tests as the CPU's `PrepareGrass`), the blades each shows (the
  density's prefix, `FoliageGrassGpu.DensityStep`) and the nearest-first order. One `DrawIndirectCount` records them all: 1,042 draws become 2.
- **Switches.** `MEITOU_GPU_GRASS=0` (or `MEITOU_GPU_CULL=0`) keeps the CPU path, the reference. `MEITOU_GPU_CULL_VERIFY=1` runs both and
  compares the GPU's draw list a frame later with the CPU's (set, blade counts, vertex counts, first blades, order): a `gpu grass verify` line at exit.
- **Gate** (master `50b630d` + the grass branch, Release): build 0 warnings; tests 464 passed, 0 skipped (`FoliageGrassGpuTests`: synthetic pages
  against the CPU's decisions, `[Slow]`); `--faithful all` ten views 0 px against the rocks merge; verify mode forest flying 60 frames, grass range 8
  and density 2: 180 views, 125,040 draws, 0 set or order differences; `MEITOU_VK_VALIDATION=sync` forest 13:00 (TAA) and Hub 02:00: 0 errors.
- **Measured** (forest, `--fly-benchmark 200 --fly-speed 0 --faithful all`, grass range 8, density 2, two runs each, the grass agent's build): the
  grass step 0.47 / 0.43 ms CPU on the CPU path, 0.07 / 0.05 ms on the GPU path; draws 1,042 to 2; the grass's GPU time 0.50 to 0.50-0.53 ms
  (the kernels are within the noise). The foliage pass as a whole: 0.63 / 0.58 ms CPU to 0.27 / 0.22 ms.

### 5.6.3 Objects C1/C2: built, not merged (2026-10-06)

Steps C1 (the objects' cull per zone group in a form the GPU can reproduce) and C2 (that cull in compute kernels per view, indirect draws,
verify mode) were built and passed the gate (0 px, verify 0 differences over 2,290 views, sync validation 0 errors), but neither is on master.
They are kept on the branch `objects-gpu-cull` (`136e1b9`, `f86c18d`), whose docs carry the design and the full measurements.

- **Why (Measured).** The objects' CPU cost is in the work items (each zone's (mesh, materials) groups), not in the instances: the game has
  about two instances per item, and the densest view found (Stack, object distance 40,000, distant towns 10) has 231 instances per view. The
  GPU path saves about 0.1 µs per instance but recording a view's dispatch costs 10-46 µs, so C2 was slower than C1 in every view
  (Hub colour 70 → 78 µs per view, a shadow cascade 41 → 54, Stack 158 → 172; the kernels add 24-54 µs of GPU time per view). C1 on its own
  is no faster than master (Hub, 300 still frames, three interleaved runs: the objects pass 0.044 ms on master `a23a0ac`, 0.047-0.054 with C1).
- **Lesson.** Count instances per CPU work item on real content before building a per-instance GPU path. Foliage has thousands per
  item; objects have two.
- **Found on the way (Verified on the RTX 4070, by test on the branch).** The driver's float division is not correctly rounded (28% of
  quotients differ from the CPU's), so a bit-exact kernel needs a corrected division, as `CrSqrt` for the square root (5.6.1).
### 5.7 Size-based ranges, LOD, impostors, occlusion: Meitou-mode, behind switches

These change the picture on purpose, so they are not part of any parity step. They come after A2 and C2, each behind an `Enhancement`
(Meitou default, Faithful = today's ranges and meshes, so `--faithful all` keeps the gate valid):

- **Size-based ranges** (`range`, owner decision 3): an instance's range follows its projected size (bounding radius
  over distance against a pixel threshold), capped by the layer's range × the setting. Large trees go far, small junk stops sooner. This
  is how small things become affordable at several thousand units. The distances are adjustable settings per size class (owner decision 4).
  **Done on the CPU (2026-10-06)** as the `range` switch, by size class per mesh rather than per instance (a group keeps one range, so the
  ChunkRecord's per-view range carries it unchanged): large / medium / small first at 5000 / 2500 / 800 and, since the billboards (8.10), at 12000 / 5000 / 800 by default, Tab sliders and
  `--range-large|medium|small`; FAR layers' large meshes keep the longer of that and 8000 × the setting (docs/render-foliage.md,
  docs/formats/foliage.md "Mesh sizes").
- **LOD selection on the GPU** (objects, C): `MeshLod.Select` / `Blend` per instance in the cull kernel, with two outputs while blending,
  as the CPU emits them. This is parity-relevant (the LOD rule is the game's), so it follows the A1/A2 pattern.
- **Impostors** (`impostors`): hemi-octahedral impostor atlases (12 × 12 frames sized by distance, BC1 albedo + BC5 normal, no depth), baked per mesh
  natively and cached in `%LOCALAPPDATA%\Meitou\impostors`. **Done (phase 8 stage 2 and 8.10, 2026-10-07)**: the `impostors` switch (F7, Meitou
  default), within an atlas VRAM budget (192 MB, LRU), drawn from the impostor distance (default 4000, Tab slider, `--impostor-distance`) as one quad per instance from the same
  cull (the kernel splits a group's instances into mesh and impostor lists, crossfaded over a 10% band by complementary dither, separate
  indirect draws, bindless atlases), lit, fogged and shadowed as the meshes, and cast into the shadow cascades as impostors. Atlases load
  or bake on demand. Details, measurements and the GLSL API are in [impostors.md](impostors.md) (section 7 for the foliage path).
- **Hi-Z occlusion** (`occlusion`): last frame's depth pyramid reprojected. Built for foliage on 2026-10-08 with an exact parallax margin (no pixel differences in
  fly/turn tests); see [formats/foliage.md](formats/foliage.md#occlusion-culling-in-the-viewer-meitou-2026-10-08). Switch `--no-occlusion-cull`.
- **What limits ranges at "1 px"** (2026-10-07, [render-distance-benchmark.md](render-distance-benchmark.md), section 8.15 below): the GPU's
  primitives, from the impostor budget's refusals, TERRAIN-mode rocks without LOD and meshes without an impostor class; not fill, shadows or VRAM.

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
| Host pass (`IGlInterop.BeginHostPass`, 4.5; step H) | one ambient field of `VkGl`, the guests' state is VkGl's GL mirror | a per-job host pass (thread-local or on the job's `CommandList`), its viewport and scissor and a `DrawState` carried by the host, so a guest reads nothing global. The shadow and reflection hosts are native from step H, so this is the only thing left between them and per-cascade jobs |
| `VkGl` (all of it) | one thread, implicit state | not used by recorded jobs: a pass is parallel only once all of its draws are native. Overlays and debug views may stay on the render thread at the end of the frame until phase 8 |
| `RenderJobs` | "one caller at a time (the render thread); bodies must not call back in" | recording jobs are top-level `RenderJobs.For` calls. The foliage CPU cull in `Draw` moves into `Prepare` (or away, with GPU culling), so loops are not nested |
| `FrameRing.BeginFrame/EndFrame` | one thread | unchanged (render thread). `DeferDelete` is already locked |
| `GpuAllocator` | locked (`lock (gate)`) | unchanged. Recording allocates no memory; Prepare does, on the render thread |
| `LinearAllocator` (constants) | — | one per recording thread per slot, chunks from a locked pool |
| Command pools | one per slot (`FrameRing`), plus VkGl's upload pools | one per (thread, slot) for secondaries, reset when the slot comes round |
| `PipelineLibrary` | — | read-only after load. A late creation takes a lock and is counted |
| `BindlessTable` | — | `Register`, `Update` and `Free` on the render thread only (`Free` writes the journal, see 2.6). Recording reads indices |
| `QueryArena` | — | slots handed out with `Interlocked.Increment` |
| `StageClock` | static, render thread | per-job CPU time recorded per job, summed per stage. The render thread's laps keep wall time |
| Renderers' counters (`DrawnInstances`, `DrawCalls`, `PhaseMs`...) | fields written while drawing | written in Prepare, or per job and summed |
| `WorldTexture.Id` (reading it marks the texture used and may start a reload) | read while drawing | read in Prepare only |
| `SkyRenderer.Active` (static) | read while drawing | `FrameGlobals.Constants`, written once per frame before the jobs |

### 6.4 Order and parity

Jobs are executed in the order the single-threaded code recorded them. Each job records the same commands as the single-threaded code
did. The picture is therefore identical, and the gate is the usual 0 differing pixels. Load imbalance is handled by splitting large
renderers' draw lists into several jobs that are still executed in order.

### 6.5 As built (wave 4, 2026-10-06)

*In short: the shadow cascades, the reflection and the main scene are hosts whose rendering takes secondary command buffers. Every native
guest prepares a small job on the render thread, and the jobs are recorded on the job threads when the host ends, then executed in the
order they were queued. The picture is unchanged (0 px). The render thread is no faster: the frame is GPU-bound, and the recording that
moves off it costs about as much as the fork-join that brings it back (6.6).*

**Pieces** (`src/Meitou.Rendering/Gpu/Recording.cs`, `GpuFrame.cs`, `GpuContext.cs`):

- **`RecordJob`**: one guest segment. Prepare runs on the render thread in frame order and fills the job. It reads the GL mirror
  (`CurrentTargets`, `CurrentState`, copied into the job), the exports, pipelines, bindless indices and counters, and writes the frame's
  constants. `NativeFrame.Bind` is split into `Prepare` (render thread, returns a `FrameBinding` value) and a static `Record`.
  `Record(CommandList)` then only issues commands from the job's fields. Jobs come from per-renderer `JobPool<T>`s and go back after
  `ParallelPass.End`. `Size` is the job's draw count.
  Ported so far:
  - terrain: patches and meshes (`PatchJob`, `MeshJob`);
  - foliage: meshes, CPU grass and GPU grass (`MeshJob`, `GrassJob`, `GrassGpuJob`). For GPU grass the compute dispatch stays in Prepare, in PreFrame;
  - objects (`DrawJob`).
- **`GpuContext.Record(label, job)`**: if a parallel pass is open, it queues the job. Otherwise it records the job at once into the segment
  that `BeginNativeInPass` returns (mode 0, or a guest outside a host).
- **`ParallelPass`** (`GpuFrame.Parallel`, one per context): `Begin(host, formats, stage)` comes after the host's
  `BeginRendering(..., secondaries: true)`. The pass then holds an ordered list of entries:
  - queued jobs;
  - inline secondaries (`BeginInline`/`EndInline`): code that records on the render thread now, such as an unported guest, a timestamp or a clear.

  `End()` records the jobs through `RenderJobs.For`, sums the secondaries' `GpuStats` into the frame's, adds each job's CPU time to
  `StageClock.JobMs[stage]`, and calls `vkCmdExecuteCommands` once with every secondary in order. The jobs are recorded on the render thread
  instead when any of these holds:
  - `MEITOU_RECORD_THREADS=1`;
  - a draw log is being written (its order must be the frame's);
  - there is one job only;
  - the jobs' `Size` sum is below `Recording.MinThreadedDraws`. This is `MEITOU_RECORD_MIN_DRAWS`, default 32: a reflection slice or a
    small cascade costs less to record than to wake the workers.
- **Secondaries per (thread, slot)**: `GpuFrame.ThreadPools`, one command pool per frame slot for each of `RenderJobs.Threads`
  (workers + the render thread, `RenderJobs.ThreadIndex`). The buffers are allocated as needed, reused and reset when the slot comes round.
  `CommandList.BeginSecondary` inherits the rendering (`VkCommandBufferInheritanceRenderingInfo` with the host's formats and samples) with
  `RENDER_PASS_CONTINUE | ONE_TIME_SUBMIT`.
- **Hosts**:
  - `ShadowPass.BeginHost`/`EndHost` (stage `shadows`). The Meitou tile clears are `GpuContext.ClearDepth`, a queued `ClearJob` inside the pass.
  - `ReflectionPass` (stage `reflection`). Its depth clear between slices works the same way.
  - The new `SceneHost` in `WorldFrame.cs`. It opens after the frame's `gl.Clear` and spans the sky and every depth slice (stages `terrain`,
    `objects`, `foliage`, `water`, set with `Stage`), and closes after the slice loop. With an upscaler the slices draw into different
    framebuffers, so there is one host per slice. The scene host is off with the terrain wireframe (a VkGl draw) and in mode 0.
- **VkGl inside a host** (`VkGl.Interop.cs`, `VkGl.Framebuffers.cs`, `VkGl.Queries.cs`). Only these calls may touch the primary while the rendering is open:
  - `BeginNativeInPass` returns an inline secondary while the parallel pass is open.
  - `gl.Clear` of the host's own targets records `vkCmdClearAttachments` (`CommandList.Clear`) in its place, with the GL scissor. It
    throws for other targets.
  - Timestamps (`QueryCounter`, `Begin/EndQuery`, `Interleave`) go into a one-command inline secondary, or into the open inline guest.

**Deviations from 6.1 to 6.3:**

- The cascades and the reflection use secondaries in their host's rendering, not one primary per job. The host already existed, and one
  mechanism serves all three hosts. **Observed**: the parity gate is 0 px.
- There is no `LinearAllocator` per recording thread. `Record` allocates nothing: constants, descriptor sets and bindless entries are made
  in Prepare. `RenderJobs.AssertNotInJob` enforces this. It throws when called inside a recording job, and it guards `NativeFrame.Prepare`,
  `LinearAllocator.Allocate`, `GpuFrame.AllocateSet`, `BindlessTable.Register/Update/Free` and `ParallelPass.Begin/BeginInline`.
- `PipelineLibrary.Get` is already locked, so it needs no change; it is called in Prepare. `QueryArena.Allocate` was already interlocked.
  `SkyRenderer.Active` and `WorldTexture.Id` are read in Prepare only. **Verified** by reading every `RecordJob.Record` body: none
  touches textures, the bindless table, pipelines or allocators.
- Counters: a host's secondaries add their `GpuStats` to the frame at `End`. The `MEITOU_PASS_STATS` rows (the `PassMeter`) therefore show
  a host's native draws in the row where the host ends. This changes neither the totals nor the render-thread times. **Observed**
  (forest 13:00 still, core validation 0 errors):
  - the shadows' 325 draws are in the `shadows` row's own line, after the cascades, whose rows show 0;
  - the reflection's 53 draws are in `reflection/rest`;
  - the scene's 311 draws are in `water`.

**Still on the render thread**, recorded inline at their place in a host or outside any host:
- the sky (one draw) and the water (one draw), as inline secondaries;
- timestamps: `PassMeter`, the GL timer queries, `StepTiming`;
- the grass motion pass (a guest of post-processing's velocity pass, not a host);
- the Meitou shadow blocker map;
- post-processing, the debug views and the overlays (VkGl and `LegacyProgram`, outside the hosts).

**The switch**: `MEITOU_RECORD_THREADS`, read by `Recording.Mode`:
- `0`: everything inline into the frame's command buffer, exactly as before wave 4;
- `1`: the hosts use secondaries, recorded serially on the render thread. This tells a secondary-buffer problem from a race;
- anything else (the default, `2`): the job threads.

### 6.6 Progress (wave 4, 2026-10-06)

Commits `2df6b57` (infrastructure: jobs, secondaries, `ParallelPass`, terrain and foliage jobs), `21ec916` (the cascades in parallel,
objects jobs), `5f3c5a3` (the reflection and the scene as hosts, VkGl clears and timestamps inside a host, grass jobs), `fb48f73` (the
32-draw threshold, the last scene close counted as `water`), `e470008` (the `AssertNotInJob` guards).

**Gate** (**Verified**, Release, RTX 4070):
- `dotnet build -c Release`: 0 warnings. `dotnet test -c Release`: 465 passed, 0 skipped. New tests:
  - `GpuApiTests.Secondaries_recorded_on_job_threads_execute_in_the_order_they_were_queued` (`[Slow]`): eight jobs on the job threads
    plus an inline guest between them. It checks the order by the colour columns, the counters, that `AssertNotInJob` throws inside a job,
    and validation.
  - `SeamTests` now clears inside a host through `gl.Clear`.
- Parity at maximum 0 against master `a23a0ac`, 26 pictures per run: ten `--faithful all`, ten Meitou, and six extras (`--debug-shadows 1`
  in both modes, `--water-reflection 4` Port North, `--upscaler taa`, `MEITOU_GPU_CULL=0`, `MEITOU_GPU_GRASS=0`).
  - three runs in mode 2;
  - one run each in modes 0 and 1;
  - four views and the extras with `MEITOU_RECORD_MIN_DRAWS=0` (every pass threaded).
- `MEITOU_VK_VALIDATION=sync`, forest and Port North 13:00 with `--water-reflection 4`: 0 errors. The same with `--upscaler taa` (one host
  per slice): 0 errors. Core validation with every pass threaded: 0 errors.

**Measurements** (**Observed**, 2026-10-06, RTX 4070, 1600 × 900, `--faithful all`, `--fly-benchmark 300`, modes 0 and 2 interleaved, three
runs each, medians; Hub mode 1 three runs after them). The views:
- forest: `--at -37582,-80684 --yaw -70.5 --pitch 6.1 --distance 10588 --time 13`;
- still: forest with `--fly-speed 0`;
- Hub: `--town "The Hub" --distance 40000 --pitch 3 --time 13`.

"cpu only" is the frame without the GPU wait, and the stages are the render thread's wall time. The job threads' summed CPU time is shown
separately (`jobs` line).

| View, mode | frame p50 | cpu only p50 | shadows | reflection | terrain | objects | foliage | water | gpu-wait | jobs: shadows, reflection, terrain, objects, foliage |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| still, 0 | 5.1 | 1.0 | 0.93 | 0.23 | 0.10 | 0.08 | 0.19 | 0.03 | 4.45 | — |
| still, 2 | 4.9 | 1.0 | 0.86 | 0.24 | 0.09 | 0.06 | 0.17 | 0.13 | 4.25 | 0.29, 0.03, 0.04, 0.03, 0.07 |
| fly, 0 | 6.8 | 1.9 | 1.17 | 0.44 | 0.11 | 0.08 | 0.26 | 0.04 | 4.45 | — |
| fly, 2 | 6.9 | 2.3 | 1.20 | 0.52 | 0.10 | 0.07 | 0.28 | 0.15 | 4.42 | 0.39, 0.14, 0.04, 0.04, 0.09 |
| Hub, 0 | 6.2 | 2.8 | 1.44 | 0.55 | 0.14 | 0.10 | 0.40 | 0.05 | 3.68 | — |
| Hub, 1 | 6.6 | 3.4 | 1.90 | 0.70 | 0.13 | 0.09 | 0.48 | 0.29 | 3.59 | (on the render thread) 0.32, 0.12, 0.04, 0.04, 0.10 |
| Hub, 2 | 7.0 | 3.0 | 1.62 | 0.64 | 0.12 | 0.08 | 0.39 | 0.20 | 4.48 | 0.47, 0.14, 0.04, 0.05, 0.13 |

(ms; `water` in modes 1 and 2 includes the scene host's close: the fork-join and `vkCmdExecuteCommands`. With an upscaler, a slice's
host closes before the next slice begins, so that close counts in the next slice's `terrain`; only the last one counts in `water`.)

Reading (**Observed**):
- Recording moves 0.4 to 0.8 ms of CPU a frame onto the job threads, most of it the cascades'. The render thread does not get faster:
  - "cpu only" is even on the still view and 0.2 to 0.4 ms worse on the fly and Hub views.
  - Mode 1 shows the cost of the secondaries alone: about 0.6 ms at the Hub. Threading wins back about 0.4 ms of that.
  - The record loops were small to begin with, about 1.3 µs a draw after waves 3a and 3b (foliage at the Hub, `MEITOU_FOLIAGE_TIMING=1`). The fork-join at each host's end then
    costs as much as the recording it moves.
- The frame is GPU-bound in these views (gpu-wait 3.7 to 4.5 ms of 5 to 7 ms), so the frame time follows the GPU.
- The Hub runs are noisy: the update stages, which this wave does not touch, also move by 0.1 to 0.15 ms between modes.

**Later measurement** (2026-10-07, [render-distance-benchmark.md](render-distance-benchmark.md) section 6.1): mode 0 has the lowest render-thread
time in every set, at the defaults and at long ranges (C40, CPX), by 0.15-0.45 ms; the render thread's growth at long ranges is preparation, not
recording. The gpu-wait above is inflated by the paced benchmark's GPU downclocking (same doc, section 1).

**Open** (**Unknown** whether it pays):
- Overlap instead of fork-join: start a host's jobs while the render thread prepares the next host (the cascades while the reflection
  prepares, the reflection while the scene prepares). This needs no new threads, only an `End` that is not awaited until the
  `vkCmdExecuteCommands`.
- Larger jobs: one job per renderer per host instead of per segment, so fewer secondaries.
- Port the sky and the water to jobs. They are one draw each, so this is for completeness only.

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
7. **Pilot port**: `TerrainRenderer.DrawMeshes` (the TERRAIN-mode mesh path, instanced in depth and in colour: `GroupMeshes` batches
   both), step P. It is
   the coupling that foliage and objects both call, so porting it before A and C start removes their only dependency on B. It also runs
   one full gate on a real renderer before six agents rely on the API.
8. Constructor plumbing: every renderer's constructor gets a `GpuContext` parameter, and `WorldFrame.CreateGpu`, the game and the viewer pass
   it. This is mechanical, and after it no port agent needs to edit `CreateGpu`.
9. `FrameProfiler` timestamps on `QueryArena` through `Interleave` (StageClock keeps working while the stages move).

**Progress (wave 2, 2026-10-06).** Gate for every step: the build, the tests, the ten pictures (`--faithful all`) at maximum 0 against
the base build (master `27be7c2`, itself identical in two runs), and validation (`=1` and `=sync`) on the ten views.

- Step 1 (`3a4021f`): done; 0 px, validation 0.
- Step 2: done. `GlConventions` (formats, blend and compare, address modes, swizzles, front face, topology, vertex formats, the
  dummy-vertex convention, `Scatter`), `SamplerDesc.FromGl` / `SamplerCache`, `GpuDefaults` (dummy vertex buffer and textures),
  `PipelineFactory` (VkGl's pipeline creation, now shared). The sampler key gained the integer-format flag (it decides the border
  colour; VkGl's old key could hand an integer texture the float sampler of an equal key). The gate found a few-pixel rock-view
  difference in 2 of 9 runs; it was the pre-existing race of two uploads into one image (sync validation's 10 WRITE_AFTER_WRITE
  per view), fixed in VkGl (`OrderUpload`): 0 of 9 runs differ since, sync validation 0.
- Step 3: done. The section 2 API and `LegacyProgram`; device features required by `GpuContext` (pulled forward from step 5:
  bindless needs them). Tests (`GpuApiTests`): native triangle against VkGl byte-identical, SPIR-V identity for all 27 world source
  pairs, pipeline preparation and `LatePipelines`, `ResourceStates` table, `QueryArena` read-back, the upload assert, `BufferArena`,
  compute writing indirect arguments for a bindless draw.
- Step 4: done. `IGlInterop` on VkGl (`VkGl.Interop.cs`), the guard (on the chokepoints that record or flush: the command-buffer and
  upload-buffer getters, `Flush`, `BeginFrame` / `EndFrame`; calls that only change GL state do not throw), export (`Texture`,
  `Sampled`, plus `SampledUnit` and `UniformBinding`: what is on a unit or binding point, as a GL program would read it), import.
  `FrameGlobals` publishes by unit and binding point (so a consumer reads what the GL state holds at its draw) from `SkyRenderer`
  (and its uniform values), `ShadowShaders` / `MeitouShadowShaders` (the unit and block owners; `ShadowPass` binds into them) and
  `TerrainRenderer` (heights). `LegacyProgram` reads a published sampler or block it was not given, and `ApplyGlobals()` writes the
  published uniforms. Seam tests (`SeamTests`): the 4.6 cases with synchronisation validation on; the stale-pipeline case fails
  with the invalidation removed (checked).
- Step 5: done. `MEITOU_VK_VALIDATION=sync|gpu` through `VK_EXT_layer_settings`, debug utils without validation, the pipeline cache
  in `%LOCALAPPDATA%\Meitou\pipeline-cache.bin`.
- Master `cec04c8` (VkGl counters and phase timers) merged; native segments add their draws, pipeline binds and pushes to `VkGlStats`
  at `EndNative`, so the pass meter keeps counting ported draws.
- Step 6: done. VkGl logs each draw (`VkGl.DrawLog.cs`) in `CommandList`'s format; VkGl's per-frame rings are registered as host
  memory, so per-frame bytes are logged by hash on both sides. `meitou-tools draw-log-diff a b [--keep-handles]` renames handles by
  first use, ignores labels and comment lines, prints the first differing draws field by field and counts differing draws per program
  and target format. A test draws one triangle through VkGl and the same through `LegacyProgram` with exported objects and checks
  the two lines are equal. **Run-to-run noise:** two runs of the same build differ in 30 of 873 draws at the rock view (13:00): 15
  shadow-depth and 15 colour draws of one instanced mesh program (84-byte vertices, placements at locations 7 to 10), whose batch
  contents and order follow streaming; the pictures are identical. Compare a port's logs per program (its own lines are stable).
- Additive exports for ports: `CurrentState()` (GL's fixed-function state as VkGl would apply it, with `DrawState.Pipeline` and
  `DrawState.Record`) and `VertexArray(vao)` (a VAO's attributes and element buffer). Since the pilot's step 1, `VertexArray` returns the
  **same object** while neither the VAO (a version bumped by every attribute, divisor and element-buffer change) nor the storage of any
  buffer it names (deleted, redefined, renamed version, size) has changed; a port keeps whatever it derived from the export for as long as
  the result is reference-equal to the last one.
- Step 7 (pilot): done. `TerrainRenderer.DrawMeshes` records natively in colour and depth: one native segment per call
  (`DrawGroups`: `BeginNative`, `CurrentTargets`, `CurrentState`, the placements written to the frame's constants, then per group the
  VAO's attributes plus the four placement rows at locations 7 to 10, pipeline, dynamic state with the mirrored winding, vertices,
  `Flush`, indexed instanced draw). Uniforms as `Apply` sets them (`ApplyNative`; atmosphere and heights through `ApplyGlobals`), samplers
  from the GL units after the same binds (`BindUnitSamplers`), so the GL state after the call is what the GL version left. Both native
  programs are created with the GL programs they replace (constructor, `DrawDepth`), never inside a draw. Gate against the pre-pilot
  build (both Release): `--faithful all` ten views 0 px except the rock view at 13:00 (max 14, see below), Meitou default ten views
  0 px, `--debug-shadows 1` forest and Hub at 13:00 and 2:00 0 px, validation `=1` and `=sync` 0 on the ten views, tests 333 passed.
  The terrain programs' log lines equal VkGl's except one known artifact (next item).
- **Draw-log artifact (wave 3: read before diffing an instanced port).** `DrawLog.DescribeVertex` hashes `instances × stride` bytes from
  each binding's offset. For an attribute at an offset inside the record (locations 8 to 10 here, +16/+32/+48) the hashed range runs up to
  that many bytes past the data, into whatever follows in the buffer, which differs between VkGl's ring and the native constants. Those
  locations then differ while the data the shader reads is the same: check the location at offset 0 (7 here), which covers every byte.
- **Rock view noise (pre-existing, both builds).** The pre-pilot build alone, run four times at the rock view at 13:00, gives two pictures
  14 levels apart (`--faithful all`), and its two Meitou-default runs differ at the rock view at 2:00 (max 7). The pilot build lands on the
  same two pictures. It is the instanced mesh program with 84-byte vertices whose instance order follows streaming (step 6's 30 noisy
  draws, and one draw of it in a forest log); the terrain meshes are not involved. The step-2 fix (`OrderUpload`) removed the upload race,
  not this. One silent viewer exit at the rock view at 2:00 (no exception, no crash event) was seen once before the pilot and once with
  it, with other agents' viewers running; six reruns were clean.
- **Pilot CPU (forest still camera, `--fly-benchmark 300 --fly-speed 0 --faithful all`, `MEITOU_MESH_TIMING=1`, three interleaved runs
  each, medians, the machine shared with another agent's viewer).** Before (VkGl): colour 101.8 µs per call, depth 333.5 µs per call.
  After (native step P): colour 176.9 µs, depth 568.9 µs; 13,200 draws in 600 calls (22 per call), +0.3 ms per frame, render-thread
  p50 5.0 against 5.1 ms (within noise). **Step P did not make this renderer cheaper**: its draws were already instanced (22 per call),
  so the fixed cost of a native segment dominates. Measured inside the pilot (stopwatch per phase, 300 frames): `BeginNative` 12 µs,
  `CurrentTargets` + `CurrentState` + the placements' copy + `BeginRendering` ~39 µs, the first `Flush` of a segment ~36 µs (the full
  set-0 push: every global sampler and block is a getter call into the interop), `EndNative` ~5 µs; per draw ~3 µs (export of the VAO
  1.0, dynamic state 0.7, pipeline lookup 0.56, vertex buffers 0.5, index + draw 0.35, `Flush` 0.09). For wave 3: keep a native
  segment per pass, not per call; resolve VAOs and pipelines once per mesh, not per draw (the native model does); set only the dynamic
  state that changes. The API gained, from these measurements: `Flush` skips set 0 when nothing was bound since the last flush in the same
  segment (globals are read at a program's first flush in a segment and after a `Bind`; a GL state change inside a native segment is
  not seen until then), `VertexLayout` returns the previous object when the inputs are equal, frame-global uniforms are written without
  boxing, `FrameGlobals.ApplyCount` lets an owner compute shared getter values once per `ApplyGlobals` (the sky's 13 atmosphere values).
- **Pilot step 1, the hot path (`ea1f20d`, 2026-10-06).** What changed: the segment joins VkGl's open rendering instance
  (`BeginNativeInPass`, 4.2: no `EndPass`, no two full barriers, no `BeginRendering` of ours and no restart of VkGl's pass after it);
  `VertexArray` returns the same export while nothing changed, and each mesh keeps its pipeline, own vertex buffers and indices per
  program (`NativeMesh`, revalidated by one reference compare and one segment number per draw); the placements of all groups go into the
  frame's constants with one copy and are bound once per segment at locations 7 to 10, each draw reaching its group by `firstInstance`;
  viewport, scissor, depth, bias and culling once per segment, the front face only when a mirrored mesh flips it (`CommandList.SetFrontFace`,
  new); `Flush` once per segment; the sampler lookups of `BindUnitSamplers` resolved once per `FrameGlobals.Version`; `LegacyProgram`'s
  dynamic set found through a two-entry most-recent cache instead of a dictionary; the guard now also covers VkGl's draw path (with a pass
  open the draw did not go through the guarded getter). The draw log takes a binding's stride and rate from the pipeline bound at the draw
  (as Vulkan does), so binding before the pipeline logs correctly. Gate: tests 384 passed (new: `SeamTests.An_in_pass_native_segment_...`,
  sync validation on); `--faithful all` ten views 0 px against master `63181e5` (master itself identical in two runs; step 1 run three
  times, all 0, the rock view included); Meitou default ten views 0 px; validation `=1` and `=sync` 0 errors on the ten views; draw-log
  diff against master at forest 13:00 and Hub 2:00: the terrain-mesh lines identical (forest: 1 of 979 draws differs, the known noisy
  84-byte-vertex program; Hub: identical).
- **Pilot cost after step 1 (Measured: forest still camera, `--fly-benchmark 300 --fly-speed 0 --faithful all`, three interleaved runs per
  build, medians, Release; 1 colour and 1 depth call per frame, 17 and 27 draws per call, 574 and 5,985 placements).**

  | | before: VkGl (`954326a`) | step P (master `63181e5`) | step 1 (`ea1f20d`) |
  | --- | ---: | ---: | ---: |
  | colour, µs per call | 73.3 | 104.0 | 103.1 |
  | depth, µs per call | 245.1 | 404.4 | 356.4 |
  | colour, µs per call, warm (`MEITOU_MESH_TIMING=2`) | 46.3 | — | 32.4 |
  | depth, µs per call, warm | 213.6 | — | 202.9 |
  | render thread, cpu-only p50 (ms) | 2.8 | 2.9 | 2.7 |
  | stage `terrain` mean (ms) | 0.37 | 0.33 | 0.34 |
  | stage shadow casters `terrain` mean (ms) | 0.39 | 0.34 | 0.32 |

  `MEITOU_MESH_TIMING=2` calls `DrawMeshes` a second time at once and times that one alone (diagnostic only; its pictures are not for
  comparison). In the warm runs the first call measured colour 68.9 / 94.2 µs and depth 243.9 / 352.3 µs (VkGl / step 1). Grouping and the
  placements' copy (`GroupMeshes`, shared by both paths) is ~28 µs of a colour call and ~215 µs of a depth call (Observed, stopwatch per
  phase).
- **Cold against warm (Observed, stopwatch per phase in instrumented builds, not committed).** Per draw, step 1 records in ~0.6 µs in colour
  and ~2.4 µs in depth on the first call of a frame (depth: mostly data misses on 27 meshes' state), VkGl in ~2.0 µs. The fixed part of the
  first call (uniform writes ~30 µs, `BindUnitSamplers` ~22 µs, `Flush` 9 µs in colour, 42 µs in depth) is first-touch cost: run a second
  time at once, the whole native call is cheaper than VkGl's (table). VkGl's code stays hot because every unported renderer runs it each
  frame; the pilot's code runs once per frame per kind. Ruled out as causes: the GC (`DOTNET_gcServer=0`: no change), W^X
  (`DOTNET_EnableWriteXorExecute=0`: no change), draining write-combined memory (a barrier after the copy: 0.24 µs), a slow context (the
  same small loop repeated runs at 0.094 / 0.022 µs). Expectation (not measured): the gap closes as wave 3 moves more renderers onto the
  same native code (`LegacyProgram.Flush`, `CommandList`), which then stays hot.
- **Result.** Per call, the pilot is still dearer than VkGl when cold (colour +41 %, depth +45 %), cheaper when warm (colour −30 %,
  depth −5 %); per frame it is not worse (cpu-only p50 2.7 against 2.8 ms; the stages that contain the calls are cheaper, since the pass
  restart and barriers VkGl paid after a step-P segment are gone). Per draw, well under 1 µs in colour; in depth the cold per-draw
  cost is not.
- **Step O not done (stopped at step 1).** Three reasons. (1) The terrain fragment program samples `usampler2D uCells` (integer blend
  cells) and `BindlessTable` has only float arrays (`textures2D`, `textures2DArray`, `texturesCube`, `shadowTextures`): the same maths
  needs an integer array first. (2) `BindlessTable.Register` makes an index valid from the next frame that begins, while these textures are
  GL textures found at draw time whose view and sampler VkGl recreates (mip streaming, the LOD bias set by post-processing): a fresh
  index would lag one frame behind what GL binds, and with TAA history a one-frame difference is not pixel-identical. (3) All draws of a
  call share one material, so bindless indices and push constants carry nothing per draw here; set 0 is already pushed once per
  segment (~7 µs colour, ~3 µs depth warm). Step O would add risk for no saving on this renderer. Steward additions for wave 3b: an
  integer-sampler bindless array, and a same-frame registration (an index usable in the frame it is registered in).
- **Bindless gaps closed (steward, 2026-10-06; `5436d51`, `d0e0fd9`, `9797b63`).** Reasons (1) and (2) above are resolved (2.6): integer
  arrays `utextures2D` / `itextures2D` with `KindFor` and `Register(Texture, Sampler)`, `BindlessTable.GlslDeclarations`, reflection of
  runtime sampler arrays and the load-time check of a native program's bindless declarations; registration and update usable in the frame
  they happen in (journal replayed into the open frame's set at `GpuFrame.End`, before submit); tombstoned frees so a slot never replays a
  destroyed view; `IGlInterop.Bindless` exporting a GL texture's current index (copy-on-write on a view, sampler or bias change). Reason (3)
  stands for the terrain meshes. All additive (new enum members appended, new overloads, one new interface member implemented by VkGl).
  Tests (`BindlessTests`, synchronisation validation on): kind by format; integer textures read back exact; a wrong declaration refused;
  register and update in a frame with two frames in flight (seven frames, no waits between them, each frame's target read back); the GL
  texture export. Each same-frame test fails without the `GpuFrame.End` replay (checked: the targets read back 0). Gate (Release, against
  master `ffd2a23`, itself identical in two `--faithful all` runs): tests 389 passed, 0 skipped (with `KENSHI_PATH`); `--faithful all` ten
  views 0 px, the rock view included; Meitou default ten views 0 px; validation `=1` and `=sync` 0 errors on the ten views. No renderer
  uses the table yet, so the pictures prove only that `VkGl` (the new `DestroyTexture` frees, `GpuFrame.End`) is unchanged.
- Step 8: done. Every renderer constructor (and factory) takes the `GpuContext` after the `IGl`: `TerrainRenderer`, `TerrainTextures`,
  `TerrainShadowMap`, `SkyRenderer`, `PostProcess`, `ReflectionPass`, `WaterRenderer`, `WorldObjectRenderer`, `FoliageRenderer`,
  `ShadowPass`, `DebugOverlay`, `FrameProfiler`, the viewer's `Renderer` and `CharacterRenderer`; each keeps it as `Gpu` (the
  `CharacterRenderer` as `Context`: it has a nested `Gpu` class). `WorldFrame.CreateGpu(gl, context, ...)`; the game and the viewer
  pass `display.VkGl.Context`. Owners use it instead of `GpuContext.Of(gl)` (the static `ShadowShaders` / `SkyRenderer.PublishUnits`
  helpers still look it up).
- Step 9: done. `FrameProfiler.Stamp` allocates a `QueryArena` slot and records it with `Interleave` (no pass break); results are read
  a frame ring later with `TryRead`. GL queries remain only when there is no interop. Test: `SeamTests.Profiler_stamps_go_through_the_seam_into_VkGl_passes`
  (stamps inside VkGl's open pass, three per frame in the native arena, GPU frames read, sync validation clean).
- Steps 7 to 9 landed in one commit (their edits share `TerrainRenderer.cs`, `SkyRenderer.cs` and `FrameProfiler.cs`); the gate above
  ran on the combined build.

**Wave 3, agent A (foliage), step P as a probe (2026-10-06, `3ce89a4` on top of master `ffd2a23`).** `FoliageRenderer` records natively:
meshes (colour, and depth for the shadow cascades), grass, and `DrawGrassMotion` (the guest in `PostProcess`' velocity pass). Only
`FoliageRenderer.cs` changed (the shaders are the same text, so the SPIR-V is byte-identical; `FoliageShaders.cs` and everything frozen are
untouched). The point of the probe was to see whether native draws are cheaper on a renderer with many small draws. Verdict below the gate.

- *What it does.* Four `LegacyProgram`s (colour mesh, depth mesh, grass, grass motion) are made in the constructor with every uniform handle
  and sampler slot resolved; the constants GL set on every draw (`uSkinned`, `uAlphaChannel`, `uTint`, ...) are set once. Each draw method is
  split into **Prepare** (cull, texture ids read through `WorldTexture.Id`, the draw list: `meshDraws`, `grassDraws`, `motionDraws`; no IGl
  call that could flush) and **Record** (the list into one native segment of VkGl's open pass, `BeginNativeInPass`): viewport, scissor, depth
  and bias once; the batches' matrices written once into the frame's constants and bound once as the four per-instance rows at locations 7 to 10,
  each draw reaching its batch by `firstInstance` (the GL `instanceBuffer` is never given storage now, so the vertex arrays' exports stay valid
  from frame to frame); per draw only what differs: material uniforms when the material key changed (a record struct compare), textures when
  an id changed, the cull mode when double-sidedness flips, the pipeline, the mesh's own vertex buffers and indices from a per-mesh
  `NativeMesh` (kept while `VertexArray` returns the same export; two pipelines per mesh, because the reflection's 4x target alternates with the
  scene's), `Flush`, the draw. One segment for the meshes and one for the grass, because `StageClock.Sub` (the pass meter's `QueryCounter`)
  sits between them and is an IGl call. The GL state the GL version left (atmosphere units via `SkyRenderer.BindUnits`, cull face off,
  no vertex array) is restored; the GL foliage programs are no longer made.
- *Things a port must do that the pilot did not show.* (1) `DrawState.Cull` is `None` while GL's cull face is off, which loses the mode:
  `gl.Enable(CullFace)` before `CurrentState()` (the foliage toggles culling per material and the shadow pass leaves it off). (2) Alpha-to-coverage
  and the colour mask (red and green for the grass motion, no depth test) come out of `CurrentState()` as GL set them before the segment; the
  motion pass needed no code for them. (3) The draw log's `DescribeVertex` artifact (7.1) hits the last batch of every pass: locations 8 to 10 of
  those draws differ, location 7 is equal. (4) Reading a texture id counts as use and can start a reload: it belongs in Prepare, before the
  segment opens, not in Record.
- *Gate (Release both sides, against master `ffd2a23`, which gave identical pictures in two runs for the Faithful and the Meitou sets).* `dotnet build` 0 warnings; tests
  384 passed, 0 skipped (`KENSHI_PATH` set); `--faithful all` ten views max 0, mean 0; Meitou default ten views max 0; `--debug-shadows 1` forest and
  Hub at 13:00 and 2:00 max 0; validation `=1` and `=sync` on the ten views: 20 runs, 0 errors. Beyond the 7.7 list: `--water-reflection 4`
  (forest, Hub, Port North, zone 14,30 at both times) max 0 and 0 sync errors, **but** those pictures equal the level-2 ones, so they do not show the
  mirrored foliage; the reflection's multisampled foliage draws (alpha-to-coverage on) were checked in the draw log instead (Port North, frame 2:
  96 foliage draws, all equal but the three last-batch artifact draws). `--upscaler taa` (grass motion active) four views at both times max 0, and
  the draw log at the forest with TAA: 696 foliage draws, 298 of them grass and 298 grass motion, all equal but two last-batch artifact draws.
  Draw-log diff (`--faithful all`): forest 13:00 4 of 979 draws differ (the two foliage ones and one terrain-mesh draw only at locations 8 to 10, and
  the known noisy 84-byte-vertex objects program); Hub 2:00 4 of 391 (three foliage draws and one terrain-mesh draw, locations 8 to 10 only).
- **Measured: foliage CPU per draw (three to five interleaved runs per build, medians, Release, forest still camera
  `--fly-benchmark 300 --fly-speed 0 --faithful all`, `MEITOU_FOLIAGE_TIMING=1`, no pass meter; the machine was shared with other viewers and
  builds, so the unchanged `cull` step alone moves 72 to 120 us between runs).** "Before" is the instrumented VkGl build (`7324a98`: the same
  code plus the counters). A step's time includes Prepare; "record" is the native segment alone.

  | | forest still (5 runs) | | trees still (5 runs) | | forest, flying 150 u/frame (3 runs) | |
  | --- | ---: | ---: | ---: | ---: | ---: | ---: |
  | | VkGl | native | VkGl | native | VkGl | native |
  | colour meshes, draws per call | 12.0 | 12.0 | 14.7 | 14.7 | 12.0 | 12.0 |
  | colour meshes, us per draw | 4.43 | 2.05 (record 1.92) | 4.21 | 3.20 (record 3.02) | 5.73 | 3.44 |
  | grass, draws per call | 132.5 | 132.5 | 119.2 | 119.2 | 32.3 | 32.3 |
  | grass, us per draw | 2.78 | 1.99 (record 1.40) | 2.82 | 2.55 (record 1.69) | 5.70 | 4.90 |
  | depth meshes, draws per call | 36.5 | 36.5 | 16.5 | 16.5 | 24.5 | 24.5 |
  | depth meshes, us per draw | 3.30 | 1.26 (record 1.17) | 4.06 | 2.74 (record 2.49) | 4.88 | 2.55 |
  | stage `foliage`, ms | 1.29 | 0.99 | 1.54 | 1.46 | 1.05 | 0.91 |
  | shadow casters `foliage`, ms | 1.63 | 1.43 | 2.13 | 2.09 | 1.19 | 1.05 |
  | render thread cpu-only p50 / p95, ms | 4.0 / 5.4 | 3.2 / 4.3 | 4.8 / 6.3 | 4.8 / 6.7 | 5.2 / 7.8 | 4.8 / 7.5 |

  The same views with the pass meter (`MEITOU_PASS_STATS=1`, two runs per build, the machine quieter): forest stage `foliage` 1.08 / 1.14 ms ->
  0.88 / 0.87 for 342 draws a frame (27 colour mesh, 298 grass, 17 rock groups), i.e. 3.2-3.3 -> 2.6 us per draw; `fol grass` 0.76 / 0.80 -> 0.61 / 0.60 ms
  (2.6 -> 2.0 us per draw); the shadow cascade 3 meshes 0.22 / 0.23 -> 0.09 ms for 73 draws; render thread cpu-only p50 3.53 / 3.85 -> 3.23 / 3.28 ms,
  p95 4.96 / 5.04 -> 4.34 / 4.26. Trees: stage `foliage` 1.31 / 1.32 -> 1.09 / 1.11 ms for 320 draws; p50 4.20 / 4.17 -> 3.84 / 3.89, p95 5.42 / 5.44
  -> 4.87 / 5.14. VkGl's own counters for the stage drop from 209 KB of uniform copies, 624 copies and 3 533 descriptor writes a frame to none
  (the native side writes the same default blocks into the frame's constants, uncounted there; the 316 descriptor pushes stay, since the grass
  changes its sprite on nearly every draw). **GPU time did not
  change** (stage `foliage` 1.75 / 1.60 -> 1.63 / 1.63 ms in the forest, 2.68 / 2.65 -> 2.35 / 2.28 in the trees; the whole frame 9.8 / 10.0 -> 9.9 / 10.1 ms).
  The runs without the meter show the same direction with more scatter (the trees' p50 and p95 are inside it).
- **Observed: where a native draw's time goes (stopwatch per phase inside Record, not committed; forest still, 600 frames, the first ones included).**
  Grass 1.62 us per draw: `Flush` 0.74 (the changed default blocks copied into the constants, set 1 rebound with dynamic offsets, set 0 pushed
  when a texture changed), `VertexArray` export 0.35, pipeline and vertex buffers 0.20, key compare and uniform sets 0.18, texture lookups 0.06,
  the draw call 0.08; 9 us setup and 0.4 us end per segment. Colour meshes 2.0 us: `Flush` 0.85, pipeline, vertex and index buffers 0.40, `VertexArray` 0.35,
  sets 0.18, texture binds 0.15, draw 0.09; 21 us setup per call. Depth meshes 2.1 us: pipeline and buffers 0.56, `VertexArray` 0.58, `Flush` 0.48,
  texture binds 0.29, sets 0.12, draw 0.08; 48 us setup per call (cold: the cascades run once or twice a frame each). The trees view gives the same
  split (grass 1.67, colour meshes 2.25, depth meshes 2.83). Grass's Prepare adds about 0.6 us per draw (the page sort, the frustum tests, two
  `textures.Get` dictionary lookups per patch), which the port left as it was.
- **Verdict on the probe.** Native step P is cheaper per draw than VkGl on foliage: 10 to 62 percent less, 0.3 to 2.4 us a draw, depending on
  the kind of draw (the biggest gains on the meshes, 4.4 -> 2.0 and 3.3 -> 1.3 us in the forest, 24 to 62 percent over the three views; the grass, which
  is where the many small draws are, 2.8 -> 2.0 us in the forest, 10 to 28 percent over the three views, 298 draws a frame, so 0.2 to 0.25 ms of the
  foliage stage, and 0.3 to 0.6 ms of the render thread's p50 in the meter runs, which carry noise of that size). That matches what docs/engine.md predicted for removing the layer above the `vkCmd` calls (1 to 1.5 us), and it is **not** the order
  of magnitude section 1 hopes for (0.1 to 0.3 us per draw): 1.4 to 2 us per draw remains inside the legacy model, because step P keeps VkGl's
  default blocks and descriptor sets, and the VkGl export calls the seam needs (`VertexArray`) are per draw. The rest comes with step O (push
  constants and shared blocks instead of copied default blocks: `Flush` 0.5 to 0.85 us, the biggest part), with a cheaper way to keep a mesh's
  vertex-array export valid (a version number instead of a revalidation per draw: 0.35 us), and with not rebuilding grass's draw list every call
  (0.6 to 0.9 us). The cold-code effect the pilot found (7.1) did not show here, where draws are many and in a row (a plausible reason, not
  measured separately): each of the four programs' loops runs 30 to 130 draws in a row.

**Wave 3, agent C (objects + characters), step P (2026-10-06, on master `8c65331`).** `WorldObjectRenderer` (colour, and depth for the shadow
cascades, which the reflection pass and the distant towns share), and the viewer's `Renderer` and `CharacterRenderer` record natively in VkGl's
open pass. Four commits: the timing switch, the zone order, the viewer, the objects.

- *Objects.* Two `LegacyProgram`s (colour fragment, `ShadowShaders.MeshDepthFragment`) are made in the constructor with every handle resolved; the GL
  programs and the string-keyed uniform cache are gone (the constants GL set per call, `uTriplanarScale`, `uSkinned`, `uHasHead`, are set once).
  `Draw` is split: the cull and level choice, the batches' matrices into one `Frame.Constants` allocation (bound once as the rows at locations 7 to 10,
  each draw reaching its batch by `firstInstance`; the GL `instanceBuffer` never gets storage, so the part vertex arrays' exports stay valid), then
  **Prepare** (`PrepareDraws`: the texture ids through `WorldTexture.Id`, the material as a record struct, wireframe/`MEITOU_LOD_DEBUG` colours; also where
  `meshes.PlainVao` creates VAOs for the TERRAIN-mode path) and **Record** (one segment per call and kind: dynamic state once, per draw the changed
  uniforms and textures, the pipeline and own vertex buffers from a per-part `ObjectNativeMesh` for the last two segment states, the index buffer at the
  level's byte offset as VkGl binds it, `Flush`, `DrawIndexed`). The wireframe pass is a second segment with GL's polygon mode and offset set around it.
  `TerrainRenderer.DrawMeshes` is called as before, after the segment has ended.
- *What objects needed that foliage did not.* Cull stays `None` (the objects are double-sided and GL's cull face is off, which the state export reports
  as `None`: no `Enable(CullFace)` trick). The depth program has no `uCoverage`; it shares the vertex shader, so the fade uniforms exist in both. Each draw
  differs in level (index offset) and in `uFadeMode` (towns), so those are compared per draw like the material.
- *Viewer.* `NativeMeshProgram` (in `Renderer.cs`) is the small shared helper: a `LegacyProgram`, the sampler binds, a segment number and a per-part draw.
  The GL mesh program of `Renderer` is still created, for what making it does (the sampler units and shadow blocks it publishes as frame globals);
  the grid and skeleton lines stay on GL (one upload and draw each, not mesh work). `CharacterRenderer`'s program is native only.
- *Determinism.* The 84-byte-vertex noisy program of 7.1 is partly this renderer: `ObjectStreamer.ZonesNear` enumerated a dictionary in the order
  workers finished zones, and the batches and each batch's instances followed it (two baseline runs differed in the draw order at the Hub). Zones are now
  sorted by position. Two runs of the sorted build give identical draw logs. Observed: the ten parity views and the Hub still view stay at 0 px against
  the unsorted master; at the Hub still view (not a parity view) the sorted order differs from one unsorted run in a few pixels (max 29), which is the
  depth-tie choice of an arbitrary order.
- *Gate (Release; lighter gate of the coordinator, against master `8c65331`).* Build 0 warnings; tests 389 passed, 0 skipped; `--faithful all` ten views 0 px
  (mean 0.0000), the rock view included; `--debug-shadows 1` Hub 13:00 0 px; `--water-reflection 4` Port North 13:00 0 px (as for foliage this picture
  does not show the mirrored objects: the level-3 reflection already has them, so this is a check that the reflection path runs and does not crash);
  `MEITOU_VK_VALIDATION=sync` Port North and Hub still at 13:00: 0 errors; the viewer: `--character "Dust Bandit"` solid and wireframe, a mesh
  (`antilop250.mesh`) solid and wireframe: 0 px. Draw-log diff of the port against the unsorted build at the Hub still view: 920 draws each, the 14 that
  differ are locations 8 to 10 of last batches (the known `DescribeVertex` artifact) and terrain-mesh groups in another order (the pre-sort noise).
- **Measured: Hub still camera (`--town "The Hub" --distance 3000 --pitch 10 --time 13 --fly-benchmark 300 --fly-speed 0`, `MEITOU_OBJECT_TIMING=1`,
  `MEITOU_PASS_STATS=1`, three interleaved runs per build, medians, Release; the machine was shared).** Before is the GL path with the zone sort
  (`97e4e1d`), after the port (`9601e6d`); 43 colour and 34 depth draws per call.

  | | before | after |
  | --- | ---: | ---: |
  | colour batches, us per draw (Prepare + Record) | 5.79 | 3.42 (record only 2.81) |
  | depth batches, us per draw | 5.83 | 3.43 (record only 2.82) |
  | stage `objects`, ms | 0.689 | 0.465 |
  | shadow casters `objects`, cascade 1 / 2 / 3, ms | 0.171 / 0.143 / 0.146 | 0.111 / 0.087 / 0.095 |

  GPU time was not different beyond the run-to-run scatter (the GPU column of the pass meter moves 0.2 to 4.9 ms between runs of one build). The saving
  is the same shape as foliage: 2.4 us per draw, 1.6 to 2.0 us left above the `vkCmd` calls inside the legacy model (`Flush`'s default-block copy and the
  `VertexArray` export are per draw); the cull (58 us a call) and the material compare are untouched by step P.

**Wave 3, agent B (terrain + shadow host), step P (2026-10-06, on master `ee54f0b`).** Files: `TerrainRenderer.cs`, `ShadowPass.Meitou.cs`,
`TerrainShadowMap.cs` (nothing else; the shaders are the same text, so the SPIR-V is byte-identical).

- *What is native.* (1) The terrain patches, colour (`Draw`) and shadow depth (`DrawDepth`): two `LegacyProgram`s (`PatchVertex` with the colour
  fragment and with `DepthFragment`), Prepare (`PreparePatches`: the selected nodes as a `PatchDraw` list with `uNode`/`uMorph` values and the index range)
  and Record (`RecordPatches`: one `BeginNativeInPass` segment; `CurrentTargets`/`CurrentState`, `state.Record`, pipeline and the grid's vertex buffer
  once, then per node two uniforms, `Flush`, the index range bound at its byte offset as VkGl binds it, and the draw). The pipeline and bindings are kept per
  (pass state, vertex-array export) in a small list (the reflection's 4x target alternates with the scene's). The colour uniforms are `ApplyNative`
  (generalised from the pilot's mesh version: `uHeightNormals` 1 and `uFeature` 0 for patches), atmosphere and heights through `ApplyGlobals`, samplers from the GL
  units (`BindUnitSamplers`) after the same GL binds as `Apply`. The wireframe outline (`Wireframe` 1 or 2) stays on the GL program (debug only). The GL
  depth patch program is no longer made. (2) The Meitou blocker map (`UpdateBlockers`): one segment into the blocker framebuffer, the atlas through
  `SampledUnit(0)` after the GL compare-mode-off call, a viewport and `Draw(3)` per tile. (3) The terrain shadow sweep (`TerrainShadowMap.Update`): one segment per
  pass (the GL framebuffer and source texture change per pass, so VkGl begins a new rendering instance with its barrier, which orders the ping-pong).
- *What is not, and why (guest contract).* `ShadowPass.Render`/`RenderMeitou` (the host) still open the atlas on the GL side (framebuffer, viewport, scissor per tile,
  clears including the Meitou per-tile scissored clear, the caster block write). It draws nothing itself (pass meter: its own rows are ~0.04 ms), and
  its guests need exactly this: `BeginNativeInPass` takes the pass from the GL framebuffer binding (4.5, host on VkGl, guest native), and the objects (C) and
  foliage (A) guests call `BeginNativeInPass`/`CurrentTargets`/`CurrentState` inside it unchanged; a VkGl guest's IGl calls would throw inside a native host's
  segment. The host goes native when all three guests are. `TerrainTextures.cs` and `TerrainStreamer.cs` have no draws (uploads and resource creation stay on IGl).
- *Things learned.* (1) A patch draw gets the same per-draw cost as foliage's: `Flush` copies the vertex default block (uNode and uMorph change every draw);
  the instanced form is wave 3b (it changes the shader). (2) Bind the index buffer at the range's byte offset with `firstIndex` 0, as VkGl does, and the draw
  log of the patches is equal to VkGl's draw for draw (a `firstIndex` form would differ in every line for no pixel reason). (3) A segment's fixed cost
  (BeginNativeInPass, CurrentTargets/CurrentState, first Flush, EndNative, cold code) is about 30 us: it pays back from tens of draws, not for the blocker map's two.
- *Gate (Release, against the build of the timing-only commit on master `ee54f0b`, which is master plus counters; scratch in `C:\Temp\agent-B`).* Build 0 warnings;
  `dotnet test -c Release` 389 passed, 0 skipped; `--faithful all` ten views max 0 (one earlier run of the build before the last rebase had the rock view at 2:00 at
  max 8 against one baseline run and max 0 against the other and against a second run of itself: the documented instanced-mesh noise, which master `ee54f0b`
  has since fixed by ordering zones); Meitou default ten views max 0 (one run each); `--debug-shadows 1` forest and Hub at 13:00 max 0; `MEITOU_VK_VALIDATION=sync`
  forest 13:00 and Hub 2:00, 0 errors. Draw-log diff (before the last rebase, forest 13:00 and Hub 2:00, Faithful and Meitou): every terrain patch, blocker and sweep line equal;
  the only differing draws are the known last-batch locations 8 to 10 of foliage and terrain meshes and one objects draw's placement rows (streaming order).
- **Measured (forest still camera, `--fly-benchmark 300 --fly-speed 0`, Meitou default, `MEITOU_TERRAIN_TIMING=1`, three interleaved runs per build, medians, Release,
  the machine shared: runs of the same build differ by 20 percent).** Per call includes the node selection (shared code).

  | | before (VkGl) | native | change |
  | --- | ---: | ---: | ---: |
  | patches colour, us per call (about 128 draws) | 293.5 | 120.7 | -59 % |
  | patches colour, us per draw | 2.29 | 0.94 | -59 % |
  | patches depth, us per call (about 25 draws) | 211.4 | 201.9 | -4 % (inside noise) |
  | blocker map, us per call (2 draws) | 43.9 | 47.7 | +9 % |
  | terrain sweep, us for the whole rebuild (13 passes, once) | 472 | 485 | same |
  | stage `terrain`, ms | 0.72 | 0.30 | -0.42 |
  | stage shadow casters `terrain`, ms | 0.44 | 0.41 | -0.03 |
  | render thread cpu-only p50, ms | 4.8 | 4.2 | noisy |

  GPU time and the flying benchmark were not measured (lighter gate). Verdict: the colour patches are the win (two thirds of the terrain stage); the shadow depth patches
  draw few nodes per cascade (25) and are cold code once per cascade, so the segment's fixed cost eats the saving; the blocker map and the sweep are ports for the
  phase-8 deletion, not savings (a few draws each, or once).

**Wave 3b, agent A (foliage), step O, with the steward's native prelude (2026-10-06, on master `ee54f0b`).** Four commits, in merge order:
`3b4b20d` the prelude and the shared native shader text (3.3; `NativeShaders`, `NativeFrame`, `FrameGlobals.Uniform.TryRead`, the legacy
SPIR-V golden test), `cbc94d9` the seam's `IGlInterop.VertexArrayStamp`, `d368214` the set order (frame set pushed at 0, table at 1: 2.6) and
a native-then-legacy seam test, `ba46aca` the foliage. The three steward commits merge without the foliage one (at d368214, in a checkout of its own: build 0 warnings,
334 tests passed, the 61 that need the game install skipped there); the foliage one needs all three.

- *What changed in `FoliageRenderer`.* The four programs (meshes, mesh depth, grass, grass motion) are native programs made by `NativeFrame`
  from `FoliageShaders.*Native()` (built on `NativeShaders.MeshVertex` / `MeshFragment` / `MeshDepthFragment`; grass with its own
  `GrassPush`, 72 B). No `LegacyProgram`, no default block, no `Flush`: per segment one `NativeFrame.Bind` (frame block, shadow blocks, view,
  table), per draw a push of `MeshPush` / `GrassPush` only when its bytes differ, textures as bindless indices (`interop.Bindless`, checked to
  be `Texture2D`; cached per segment and texture slot, since a view or sampler may change between segments, never inside one). The
  vertex inputs come from the reflection with `LegacyProgram`'s rules (a disabled attribute reads GL's constant through a stride-0 binding).
  Grass motion takes its colour mask and depth state from `CurrentState()` as in step P. The reflection pass and the shadow cascades use the
  same paths (the cascades read the caster block per segment, 2.6).
- *Per-draw work removed besides `Flush`.* `interop.VertexArray` is called only when `VertexArrayStamp` moved (once per mesh a frame until the hot-path work, 7.1, at the
  frame's first draw). The grass Prepare no longer looks up the sprite and colour map by name and recounts the density prefix per draw: both
  are kept on the blade buffer per patch (`Patch`), the texture still touched each frame as `textures.Get` did (use count, reload). The draw
  list itself is still built per call (it depends on the view's frustum).
- *Gate (Release; the coordinator's lighter gate, against master `ee54f0b` built in its own folder).* Build 0 warnings; `dotnet test -c
  Release` 396 passed, 0 skipped (on `13812a2`); `--faithful all` ten views **0 px** (mean 0.0000, max 0, the rock view included); `--debug-shadows 1`,
  `--water-reflection 4` and `--upscaler taa`, each on forest and Hub at 13:00: 0 px; `MEITOU_VK_VALIDATION=sync` forest and Hub at 13:00:
  0 errors (after `d368214`; before it the forest view crashed the layer, 2.6). The 1/255 allowance (owner decision 2) was not needed.
- **Measured: forest still camera (`--fly-benchmark 300 --fly-speed 0 --faithful all`, `MEITOU_FOLIAGE_TIMING=1`), three interleaved runs per
  build, means, Release; the machine was shared with two other agents' builds and runs, so run-to-run scatter is 10 to 20 percent.** Before is
  master `ee54f0b` (step P), after is `ba46aca`. Per call: 12.0 colour mesh draws, 132.5 grass draws, 36.5 depth mesh draws.

  | | before (step P) | after (step O) |
  | --- | ---: | ---: |
  | colour meshes, us per draw (Prepare + Record) | 3.37 | 3.02 |
  | colour meshes, record only | 3.11 | 2.75 |
  | grass, us per draw (Prepare + Record) | 2.89 | 2.16 |
  | grass, record only | 1.93 | 1.35 |
  | depth meshes, us per draw | 2.22 | 2.50 |
  | stage `foliage`, ms | 1.55 | 1.25 |
  | shadow casters `foliage`, ms | 1.94 | 2.09 |
  | render thread p50 / p95, ms | 8.1 / 12.5 | 8.4 / 13.4 |
  | CPU only p50, ms | 4.8 | 4.8 |

  Observed: the colour draws gain 0.35 to 0.75 us a draw (grass most, 30 percent of its record time), the foliage stage 0.3 ms. The depth
  meshes did not gain (2.22 -> 2.50, inside the scatter of the three runs: 2.01 to 2.69). Plausible reasons, not measured: the per-segment `NativeFrame.Bind` (a 6-binding push,
  7 frame-texture compares, the view write) is spread over 36 draws per cascade call, and the depth draws had little `Flush` work to lose.
  The cull (not changed by this step) read 20 percent slower in the after runs in both passes, and the frame-time percentiles moved the other
  way from the stage; with this scatter neither is a result. GPU time: no difference beyond the scatter (gpu-wait 3.2 to 5.1 ms either build).
  A quieter machine is needed for numbers below 0.3 us a draw.
- *A1 / A2.* A1 (the CPU cull in the order a GPU cull can reproduce, `FoliageCull`) is done (5.6). A2 (the GPU cull with the verify mode) is
  **not** done and was not part of this step.
- *Follow-ups.* A2, then GPU-driven grass (5.4: density prefix and range into indirect arguments); 5.7 impostors (need this step's bindless
  materials); the TERRAIN-mode rocks still go through `TerrainRenderer.DrawMeshes` with the terrain's legacy program (574 groups a colour call
  in the forest), so they move with the terrain's step O; a per-segment cost check for the depth cascades (push the frame set once per
  cascade pass rather than per call); report the push-descriptor crash to the validation layer's tracker with the seam test as the repro.

**Wave 3, agent D (sky, clouds, water, reflection host), step P (2026-10-06, written on master `ee54f0b`, rebased onto `223182a` and gated again there).** `SkyRenderer` (the sky and the simple sky; the
clouds are part of the sky shader) and `WaterRenderer` record natively in VkGl's open pass. `ReflectionPass` is unchanged. Files: `SkyRenderer.cs`,
`WaterRenderer.cs`, and the one line of `WorldFrame.cs` that calls `Water.Draw` (the unused `terrain` argument is gone).

- *Sky.* Two `LegacyProgram`s (`sky`, `sky simple`, made in the constructor with every handle and sampler slot resolved) replace the GL programs, the
  empty vertex array and the string-keyed uniform lookups for these draws. `Draw` is **Prepare** (the inverse matrix, the colour uniforms, the cloud,
  moon and star uniforms, the three textures through `interop.Sampled`, `ApplyGlobals()` for `SkyRenderer.Apply`'s values, `BindUnits()` for the
  atmosphere's units, which the frame globals read) and **Record** (one segment: dynamic state from `CurrentTargets()` / `CurrentState()`, the pipeline,
  `Flush`, `Draw(3)`). The depth test and mask are switched on the GL side before the segment opens, so `CurrentState()` reports them. `Apply`,
  `BindUnits` and `PublishGlobals` still publish the same values: nothing in the frame globals changed.
- *Water.* One `LegacyProgram` (`water`, triangle strip), handles for its 16 loose uniforms and the five map samplers and the reflection sampler resolved
  once. The height textures, their rect uniforms and the atmosphere come through the frame globals (`TerrainRenderer.BindHeights` is no longer called, so the
  water no longer binds units 7, 8 and 11 to 16; nothing reads them after it). The quad's vertex array stays a GL resource; its export (`VertexArray`) is
  fetched once per segment and its vertex buffer kept while the export is the same object. Blend (src alpha, one minus src alpha), no culling and no depth
  write are set on GL before the segment, and put back after it as the GL version did.
- *Pipelines.* `NativeSegment` (in `SkyRenderer.cs`, shared by both) keeps the last two segment states (formats, blend, mask, polygon, alpha to coverage,
  depth clamp) and the vertex export they were made for. The scene's pass and the reflection's 4x multisampled pass alternate, so one slot per state
  means no pipeline description is built per draw.
- *Reflection host: left on GL.* `ReflectionPass.Render` has no draws of its own: it creates the multisampled framebuffer and its resolve texture, clears,
  calls `sky.Draw`, `terrain.Draw` and the scene callback, and blits. Opening its pass natively would break the guests' contract (terrain, objects and
  foliage read `CurrentTargets()` / `CurrentState()` from the GL framebuffer binding, 4.5). Its only own draw, the mirrored sky, is the native sky draw
  above, and records into the multisampled target. Its resources stay on `IGl`, as the recipe allows; its clear, blit and timestamp queries are IGl calls
  outside any segment.
- *Gate (Release; lighter gate of the coordinator, against the Release build of master `223182a`, after the rebase; the first gate, against `ee54f0b`, gave the same results; the measurements below are from that first one).* Build 0
  warnings; tests 389 passed, 0 skipped (`KENSHI_PATH` set); `--faithful all` ten views max 0, mean 0.0000; Meitou default ten views max 0 (the sky, the
  haze meaning and the water are in all of them); `--water-reflection 4` Port North 13:00 and 2:00 max 0 (as for foliage and objects, this picture equals the
  level-2 one, so it checks that the multisampled sky path runs, not the mirrored foliage); `MEITOU_VK_VALIDATION=sync` Port North (reflection 4) and The Hub
  at 13:00: 0 errors. Draw-log diff, Port North 13:00 with reflection 4: 522 draws each, 4 differ, all of them locations 8 to 10 of the last batch of a
  foliage, terrain-mesh or object pass (the `DescribeVertex` artifact of 7.1); every sky and water draw, the reflection's included, is equal.
- **Measured: Port North still camera (`--town "Port North" --distance 3000 --pitch 10 --time 13 --faithful all --fly-benchmark 300 --fly-speed 0`,
  `MEITOU_PASS_STATS=1`, three interleaved runs per build, medians, Release; the machine was shared).** "Before" is the GL path of `ee54f0b`. Stage CPU
  times from the pass meter's rows; the stage includes Prepare. `sky-draw` is the stage between the reflection and the slices (viewport, clear, sky), 1 draw;
  `water` is 2 draws (the far and the near slice); `reflection` is the whole host pass, 56.75 draws on average over the frames that draw it.

  | | before | after |
  | --- | ---: | ---: |
  | stage `sky-draw`, ms (1 draw) | 0.0587 | 0.0519 |
  | stage `water`, ms (2 draws) | 0.0709 | 0.0530 |
  | water, us per draw | 35.5 | 26.5 |
  | stage `reflection`, ms | 0.284 | 0.210 |
  | reflection's own rows (`rest`, the sky inside it), ms | 0.0078 | 0.0074 |
  | stage `water` GPU, ms | 0.0194 | 0.0164 |
  | render thread cpu-only p50, ms | 3.17 | 3.08 |

  Runs of one build scatter by 10 to 40 percent (the `reflection` stage most: 0.217 to 0.386 ms before, 0.197 to 0.230 after, and it is mostly the objects
  and terrain guests), so only the water is clearly different: 25 percent less, 9 us a draw. The sky saves about 7 us of a stage that is mostly the GL
  clear; the reflection's sky draw is inside the unnamed rest of the host and its part cannot be seen separately (`rest` 7 to 8 us either way).
  Both renderers draw once or twice a frame, so there is little to save: the per-draw 2 us of 7.1 does not apply (cold code and one-off uniform writes
  dominate), and what is left is the 20 uniform writes and the six `Sampled` calls of Prepare.

**Wave 3, agent F (overlays, settings, debug views, readback), step P (2026-10-06, written on master `6c4ac6d`, rebased onto `d860e8e` and then `13812a2`, gated again on both; the FSR and DLSS runs on `d860e8e` only, the later merge touched overlays).**
Three commits. These are few draws, so the goal was fewer `IGl` calls for phase 8 with identical pictures, not speed (nothing was timed).

- *Overlay.* `DebugOverlay.Flush` is one native segment (`BeginNativeInPass`) with one `LegacyProgram` (made in the constructor, handles resolved once).
  The vertices go into the frame's constants (`Frame.Constants.Allocate`, three attributes of one interleaved range at 0, 8 and 16 bytes, stride 32), the
  atlas stays a GL texture read through `interop.Sampled`. The depth, cull and blend state are still set on GL before the segment, so `CurrentState()`
  reports them; viewport and scissor come from `CurrentTargets()`. The segment is begun **first**, before the constants are allocated: the overlay is also
  drawn after the last frame ended (the key list and the settings panel of a `--show-keys` screenshot), and `BeginNativeInPass` is what opens the frame whose
  constants hold the vertices. Allocating before it would have written them into a frame slot that is reset when the new frame begins (found by reading
  `GpuFrame.Constants` and `VkGl.Cmd`, not by a failure). What GL left behind that is no longer left: the program, unit 0's texture and the vertex array
  binding (nothing reads them after the overlay). `SettingsPanel` has no draws of its own (it uses the overlay), so the time-of-day slider and the Reset
  button needed no change.
- *Profiler.* `FrameProfiler` already stamped through the seam; the GL query fallback is gone (its constructor takes only the `GpuContext`, and throws without
  the interop). The viewer's per-frame GPU timer (`TIME_ELAPSED` query pairs, the `gpu` figure of the statistics panel) is a pair of native timestamps
  (`QueryArena`, recorded with `Interleave`, read through `TryRead` a frame ring later; a pair whose frame is more than 8 frames old is dropped). The
  benchmark's non-VkGl fence queries were dead code (`gl is not VkGl`) and are removed.
- *Debug views.* `ShadowPass.DrawDebug` (modes 1 to 3: the scene-depth view with the receiver's blocks and textures from the frame globals, and the atlas in
  the corner) is two `LegacyProgram`s and a `DrawFullscreen` segment each; the GL state (framebuffer, viewport, the multiply blend of mode 3, depth mask) is
  set around them as before. The compare-mode-off toggle on the atlas is gone: a plain `sampler2D` never compares (risk 2), and `--debug-shadows 1` and `2`
  stay at 0 px. `CaptureDepth` **stays on IGl**: it blits the scene's depth (possibly multisampled, which VkGl resolves with a rendering pass) into a texture of
  its own; a native version needs the depth-resolve pass, which is not worth it for a debug view.
- *Readback.* `FramebufferCapture.SavePng` calls `Finish` (as VkGl's `ReadPixels` flushed the frame), takes the draw target's image from `CurrentTargets()` and
  copies it to host memory with an immediate command buffer (what `GpuContext.ReadBack` does, on the raw image: it has no `Texture` for a renderbuffer). It
  falls back to `ReadPixels` unless the target is single-sampled RGBA8 at least as big as the picture and the read framebuffer is the draw framebuffer
  (`GetInteger` on both bindings; level 0 and layer 0 are assumed, as for every screenshot target). The screenshot and key callers bind `Framebuffer` (both)
  instead of `ReadFramebuffer` and no longer call `ReadBuffer`. **Observed:** the `IGl` count of this file went up (2 to 5), because of the guards and the
  kept fallback; the readback itself is native. Both go in phase 8 with the screenshot targets.
- *What stays on IGl in these files, and why.* The offscreen targets of the screenshots (framebuffer, renderbuffers, the 4x multisampled target and its
  blit in the viewer's mesh and character screenshots): `PostProcess.Target` and the viewer's renderers draw into the GL framebuffer binding (4.5), so
  these go with their guests. `Finish` calls of the frame loops and benchmarks (frame splits, risk 10), `BindFramebuffer(0)` before the overlay, the
  overlay's atlas, `Enable(Multisample)`. The game's `Program.cs` changed only in the screenshot readback.
- *Gate (Release; lighter gate of the coordinator, against the Release build of master `d860e8e`, after the rebase; an earlier run against `6c4ac6d` gave the same results).* Build
  0 warnings; `dotnet test -c Release` 396 passed, 0 skipped (`KENSHI_PATH` set); `--faithful all` ten views max 0, mean 0.0000 (they go through the new
  readback); `--show-keys` The Hub 13:00 (both panels) max 0; `--debug-shadows 1`, `2` and `3` The Hub 13:00 max 0; `--character "Dust Bandit"` max 0;
  `MEITOU_VK_VALIDATION=sync` The Hub with `--show-keys`, The Hub 2:00 with `--debug-shadows 2`, and the character screenshot: 0 errors (the character run
  prints "10 leaked objects" at device destruction, which the build of `d860e8e` prints too).
- *The profiler chart.* No screenshot path draws it (F12 in the interactive viewer). Checked two ways. **Verified** by the new `OverlayTests` (sync validation): a
  panel and the profiler's cpu chart after three profiled frames are drawn into an offscreen target, the panel colour is where it should be, more than a tenth of
  the picture differs from the clear colour, 0 validation errors, and `SavePng`'s pixels equal `ReadPixels`' for the same target. **Observed:** an interactive
  run (`--world --town "The Hub" --quit-after 6`, sync validation, with a temporary patch that switched the GPU chart and the overlay on and printed the timers;
  not committed) ran 400 frames, the native frame timer gave samples (2 to 8 ms) next to the profiler's GPU frames, 0 validation errors. `OverlayTests` and
  `SeamTests` share an xUnit collection because `StageClock` is static (the profiler test failed once when both ran in parallel).

**Wave 3, agent E (post-processing + upscalers), step P (2026-10-06, written on master `223182a`, rebased onto `d860e8e` and then `13812a2`, gated again on both; the FSR and DLSS runs on `d860e8e` only, the later merge touched overlays).** Files:
`PostProcess.cs` and the two vendor upscaler files (`FsrUpscaler.cs`, `DlssUpscaler.cs`); the shader text (`PostProcessShaders.cs`, `UpscaleShaders.cs`) is untouched, so the
SPIR-V is byte-identical (the existing identity test covers all nine programs).

- *What is native.* Every draw of the chain: the SSAO draw and its two blur draws, the velocity pass (motion, and in the vendor path the depth and reactive
  targets), the TAA resolve, the luminance and adaptation passes, the composite, FXAA and the heat haze. Nine `LegacyProgram`s are made in the constructor
  (`SsaoPass`, `BlurPass`, ... `TaaPass`: one small class each with the sampler and uniform handles resolved once); the nine GL programs, the string-keyed
  `uniforms` dictionary and the empty vertex array are gone. A pass is: GL binds the target framebuffer and viewport (`Pass()`, as before), the uniforms
  and `Bind(program, slot, glTexture)` (`interop.Sampled`) are set, then `Fullscreen(program)`: `BeginNativeInPass`, `CurrentTargets`, `CurrentState`,
  `state.Record`, pipeline, `Flush`, `Draw(3)`, `EndNative`. The textures are no longer bound on GL texture units (14 binds and 51 uniform calls a frame
  fewer, from the pass meter's `gltex` and `gluni` columns); `Sampled` is called exactly where the GL code bound the unit (after `GenerateMipmap` for the
  luminance, after the LOD-bias reset for the heat haze), since the view and sampler it returns depend on both.
- *Fog volumes pass (2026-10-08, after phase 8).* A tenth program, `FogPass` (`post fog volumes`), draws the placed fog volumes once over the scene colour (blend one / source alpha, RGB only) between the scene host closing and the particles, which `WorldFrame` now draws in a scene rendering of their own after it; it reads the near and the far slice depth (the far slice has its own depth in every mode, so the slices are separate scene renderings also without an upscaler). The world shaders no longer evaluate the volumes ([formats/fogfeatures.md](formats/fogfeatures.md#in-meitou)). The post cost line has `scene`, `fog` and `particles` sections.
- *Hosts and guests (4.5).* The scene targets stay GL objects (made through `IGl`, bound as framebuffers by `Begin`/`BeginFarSlice`/`BeginNearSlice`): every
  scene renderer still draws into them through `CurrentTargets`. The velocity pass is still a host on VkGl: the segment of the velocity draw ends, then
  `Pass(motion)`, depth test and blending off, `ColorMask(R, G)`, `ObjectMotion` (the foliage's `DrawGrassMotion`, which takes the mask and state from
  `CurrentState()` and keeps the host's rendering instance open through `BeginNativeInPass`), then the mask is restored. That sequence is unchanged, and foliage
  step O (master `d860e8e`) runs inside it with no change on either side. `ITextureLodBias` is set in `Begin` and reset in `RunHeatHaze` at the same points.
- *Vendor upscalers.* `FsrUpscaler` and `DlssUpscaler` record inside `BeginNative`/`EndNative` instead of `BeginExternal`/`EndExternal`: the same barriers, plus a label
  and the cache invalidation of 4.1 that `EndExternal` lacks. They still read the images through `VkGl.ImageOf` (the image, a level-0 view and the real usage
  flags): the borrowed `Texture` from `interop.Texture` carries no usage flags, and Streamline's resource description wants them. Moving the inputs to
  natively owned `Texture`s is phase-8 work together with the render targets themselves (they are GL objects until the scene renderers are native).
- *Gate (Release; lighter gate; against master `d860e8e` built unchanged; scratch in `C:\Temp\agent-E`).* Build 0 warnings; `dotnet test -c Release` 396 passed, 0 skipped (on `13812a2`);
  `--faithful all` ten views max 0 (mean 0.0000), baseline rendered once; Meitou default ten views max 0 (SSAO, TAA and exposure in the chain); at the Hub 13:00 and the
  forest 02:00, max 0 for `--faithful all --heat-haze 1` (FXAA then haze), `--heat-haze 1` (TAA then haze), `--faithful all --upscaler taa`, and `--upscaler taa
  --render-scale 0.5 --heat-haze 1` (render size half the display size); `MEITOU_VK_VALIDATION=sync`: 0 errors for the Hub (Meitou, haze), the forest (Faithful, FXAA, haze), and
  the Hub with FSR and with DLSS. **FSR** (FidelityFX 1.1.4 from `MEITOU_FFX_PATH`, quality, 0.667) is not bit-reproducible: old build against new differs by max 10 and
  mean 0.087 (Hub), new against new by max 7 and mean 0.087, so the difference is the run-to-run scatter (the frame time it is given moves with the wall clock). **DLSS**
  (Streamline from `MEITOU_STREAMLINE_PATH`, quality) came out identical (max 0) between old and new, and between two runs of new. Both run and look as before. The draw log was
  not needed (no difference to explain).
- **Measured (Hub still camera `--town "The Hub" --distance 3000 --pitch 10 --time 13 --fly-benchmark 300 --fly-speed 0`, three interleaved runs per build, medians, Release; the
  machine was shared and runs of one build scatter by 20 to 70 percent; two sets, on master `223182a` and on `d860e8e`).** Stage `post`, CPU, ms:

  | | Meitou (SSAO, TAA, exposure, composite) | | Faithful (exposure, composite, FXAA) | |
  | --- | ---: | ---: | ---: | ---: |
  | | before | after | before | after |
  | set 1 (`223182a`) | 0.35 | 0.37 | 0.10 | 0.11 |
  | set 2 (`d860e8e`) | 0.42 | 0.35 | 0.19 | 0.13 |

  Honest reading: **no CPU saving that the measurement can see.** The pass meter (`MEITOU_PASS_STATS=1`, one run each) has the `post` row at 0.22 ms CPU before and after, with
  the same draws in it (96, 81 of them the grass-motion guest's); the GL state and uniform calls leave (51 `glUniform` and 14 texture binds a frame) and VkGl's draw preparation
  (0.07 ms) goes with them, and the native segments take their place at about the same cost. That is what 7.1 predicted for a few full-screen draws: a segment's fixed cost
  is about the translator's per-draw state work, so the port pays back as phase 8 deleting the GL calls and the programs, not as milliseconds. GPU time was not
  measured separately (the same SPIR-V, pipeline state and targets; the `post cost gpu` rows of the runs above are inside the run-to-run scatter).

After wave 2 the foundation agent stays on as **API steward** for wave 3 (owner decision 6). Agents request additions to `Meitou.Rendering/Gpu/`.
The steward lands them additively (no signature changes), one at a time, and agents rebase. Before wave 3b, the steward also lands the
native shader prelude and the shared native shader variants (3.3), each proven on one consumer.

**Bindless in a step-O port (wave 3b guidance).**
- Textures the renderer owns natively: `ctx.Bindless.Register(texture, sampler)` once, keep the `BindlessHandle`, `Free` it when the texture
  goes (before or together with the texture's `Dispose`; both are deferred to the frames in flight). A sampler change between frames:
  `Update(handle, ...)`. A change inside a frame that earlier draws must not see: register a new index, free the old.
- Textures VkGl still owns (step P resources, `WorldTextureCache`, the terrain's `uCells`, the shadow maps): `interop.Bindless(glName)` (or
  `(glName, shadowSampler: true)`) at the point where the GL version would bind the texture, at least once per native segment, and push the
  returned index. Do not cache the index across frames or across GL calls that may change the texture (uploads, parameters, the bias):
  the handle is cheap to fetch and changes exactly when GL's view or sampler would.
- Shader side: include `BindlessTable.GlslDeclarations` (the prelude will) and keep the maths textual: `#define uCells
  utextures2D[nonuniformEXT(pc.cells)]`, `#define uDiffuse textures2D[nonuniformEXT(pc.diffuse)]`. `nonuniformEXT` is needed only when the
  index can differ within a draw (per-instance materials); a push-constant index is uniform.
- The array follows the texture's format (`KindFor`): an RGBA8UI texture must be read through `utextures2D`, never `textures2D`, and
  `ShaderLibrary.Native` refuses a declaration that disagrees with the table.

**Wave 3, step H (the shadow and reflection hosts native; 2026-10-06, on master `9d25305`).** Files: `ShadowPass.cs` (Faithful atlas), `ShadowPass.Meitou.cs`
(the Meitou cascades; `UpdateBlockers` and `DrawDebug`/`CaptureDepth` are as before), `ReflectionPass.cs`, and the steward's additive seam change (`VkGl.Interop.cs`,
`VkGl.cs`, `VkGl.Queries.cs`, `IGlInterop.cs`, `CommandList.cs`). No guest file (terrain, objects, foliage, sky) changed: they call the same three seam
functions. The shaders are untouched.

- *What changed.* The hosts no longer record anything through `IGl`: `gl.Clear` (the atlas, the reflection's colour and depth, each Meitou tile, the depth
  between reflection slices) and `gl.BlitFramebuffer` (the 4x resolve) are native commands of the host's own segment (4.5, "host native, guest native").
  Everything else the hosts did stays (GL framebuffer bound, viewport and scissor per cascade, depth state, the caster bias in the GL uniform buffer, the
  receiver blocks, the timestamps); those are state calls and uploads, which record nothing into the pass. Seam additions: `BeginHostPass`/`EndHostPass`;
  inside a host pass `UploadCmd` and the query timestamps are allowed (new `GuardNativePass`; `StageClock.Sub` between guests and the caster-bias
  `BufferSubData` need them) and `BeginNativeInPass`/`EndNative` take the guest path; `CommandList.ClearDepth(value, rect)` and `Resolve(Image, Image, w, h)`.
- *Seam test* `A_native_host_clears_and_its_native_guests_record_into_its_rendering_through_the_same_seam_calls`: a host clears by load op over a
  quarter VkGl had drawn, two guests with different programs and scissors record into its instance, then VkGl draws again: read-back pixels, and the guards
  (an `IGl` clear and draw, `BeginNative`, a nested `BeginNativeInPass` and a second `EndHostPass` throw; a buffer upload and a timestamp do not), sync validation 0 errors.
- *Gate (Release; lighter gate; reference = master `9d25305` built unchanged, scratch in `C:\Temp\agent-H`).* Build 0 warnings; `dotnet test -c Release` 397 passed,
  0 skipped; `--faithful all` ten views 0 px (mean 0.0000); Meitou default ten views 0 px (the Meitou cascades are in the host); `--debug-shadows 1` Hub 13:00 0 px;
  `--water-reflection 4` Port North 13:00 and 02:00 0 px; `MEITOU_VK_VALIDATION=sync` with `MEITOU_PASS_STATS=1` on Port North (reflection 4) and the Hub: 0 errors,
  and the pass meter's `StageClock.Sub` stamps inside both hosts do not throw. (As the earlier reflection gates note, the Port North pictures hardly show the
  mirrored objects and foliage: they check that the path runs and the resolve is right, not the guests' content in the mirror.)
- **Measured: shadows and reflection stage CPU (`--fly-benchmark 300 --fly-speed 0 --water-reflection 4`, three interleaved runs per build, medians, Release; the
  machine was shared and runs of one build differ by 20 percent).** Before = master `9d25305`, after = step H. `reflection` is the stage's mean per frame (the pass
  runs every fourth frame with a still camera); per pass is the pass's own CPU mean from the `reflect` line.

  | | Hub before | Hub after | forest before | forest after |
  | --- | ---: | ---: | ---: | ---: |
  | stage `shadows`, ms | 1.64 | 1.53 | 1.44 | 1.48 |
  | stage `reflection`, ms per frame | 0.28 | 0.23 | 0.30 | 0.29 |
  | reflection pass, ms per pass drawn | 0.33 | 0.36 | 0.29 | 0.31 |

  **Observed:** no change beyond the scatter, as expected: the host's own work is a begin and end of one rendering instance and a handful of commands, and the guests are
  the same code. The step is for structure (every world pass now records natively, the prerequisite for wave 4), not for milliseconds.
- *Not done, on purpose.* The blocker map (`UpdateBlockers`) and `TerrainShadowMap`'s sweep are guests-only segments in VkGl's own pass on their own GL framebuffers (no
  other renderer draws into them), so they need no host; `DrawDebug` and `CaptureDepth` stay (agent F's). The state the guests read is still the GL mirror: see the limits in 4.5.

**Wave 3b, the step-O hot path (agent P, as steward; 2026-10-06, written on master `9d25305`, rebased onto `3af1880` and gated there).** Why foliage
step O still cost 2-3 us a draw, and the fixes. Seven commits: `3152deb` and `8ec1d22` VkGl (exported buffers count as read by each frame; the stamp no
longer moves per frame), `b0badb7` `CommandList` (cached device entry points, one-pass vertex-buffer filter), `0ebcc7e` and `e1161d3` foliage Record (bindless indices
cached per segment by texture id), `3068dfc` `BindlessTable.ScalarOf` (no enum name per call), `5bcb8d1` `SampledTexture`/`BufferBinding` equality by
handle. Steward changes are internal or additive; the one contract change is `VertexArrayStamp` (4.2, its test updated).

- *How it was measured.* Release, the forest still camera (`--fly-benchmark 300 --fly-speed 0 --faithful all`). A stopwatch breakdown inside each
  Record loop (not committed: a lap per step; `Stopwatch.GetTimestamp` costs 0.019 us, so every fine "after" figure below carries about 0.02 us of its own
  lap and every fine "before" figure about 0.04 us (two stamps a lap then); the before/after totals come from a coarse mode with two stamps per loop), counters of Vulkan calls, `VertexArray` fetches and `Bindless`
  calls per draw, and a micro-benchmark of the floor (9.1). `dotnet-trace` (EventPipe, `dotnet-sampled-thread-time`) was tried and is of no use at
  this scale: its samples are milliseconds apart and taken at safe points, so a 30 us loop gets a handful of samples on the wrong lines.
- **Observed: where a step-O foliage draw's time went (before; master `9d25305`, the machine shared; us per draw, the first 200 segments skipped).**
  Colour meshes (27 draws a segment): textures 0.40, `Current` (the export) 0.31, vertex buffers 0.19, pipeline 0.11, index buffer 0.09, push 0.08, draw 0.08,
  cull/front flip 0.05; loop 1.45-1.66, segment 1.9-2.2 (`NativeFrame.Bind` 6.3 us a segment). Depth meshes (73 a segment): `Current` 0.43, textures 0.35,
  vertex buffers 0.23, index buffer 0.12; loop 1.26-1.57. Grass (298 a segment): `Current` 0.34, textures 0.33, vertex buffers 0.16; loop 0.98-1.12.
  Two causes, not the API: (1) **one `VertexArray` fetch per draw**: the stamp moved at every frame's begin, and foliage draws each mesh once per
  segment kind a frame, so the "skip while the stamp holds" never skipped (1.00 fetches a draw in all three kinds); (2) **one `Bindless` call per draw
  or more** (1.33 colour, 0.85 depth, 1.48 grass: the per-slot cache missed whenever consecutive draws used another texture, and grass alternates
  sprites), and each call cost 0.12-0.20 us even hot because `BindlessTable.KindFor` called `Format.ToString()` (an allocation and three string searches)
  and comparing two `SampledTexture`s boxed six Silk.NET handle structs (they are not `IEquatable`, so the record's generated equality goes through
  `ValueType.Equals`). The render thread allocated 39 MB in 300 frames, mostly from these two.
- *What changed.* VkGl marks every buffer an export has named as used by each new frame (4.2), so an export stays current across frames: 0.00
  fetches a draw. Foliage caches indices per segment by GL texture id (an array indexed by name with a segment number, no `Array.Fill`): 0.04 calls
  a grass draw. `ScalarOf` from a table built once; `SampledTexture` and `BufferBinding` compare handles: `Bindless` 0.20 -> 0.024 us hot
  (micro-benchmark, 100 textures), render thread 39 -> 12 MB allocated per run. `CommandList` calls its per-draw and dynamic-state commands through
  `vkGetDeviceProcAddr` pointers resolved once and filters vertex buffers in one pass (0.159 -> 0.124 us for the foliage-shaped draw in the
  micro-benchmark). `SuppressGCTransition` was measured and gives nothing (not used).
- **Observed: after (us per draw, coarse; the fine split with its lap cost in brackets).** Colour meshes: loop 0.43, segment 0.66 (textures 0.19, vertex
  buffers 0.15, pipeline 0.09, index buffer 0.09, draw 0.07, push 0.06, `Current` 0.05). Depth meshes: loop 0.50, segment 0.65 (vertex buffers 0.26,
  textures 0.15, index buffer 0.12). Grass: loop 0.26, segment 0.27 (vertex buffers 0.17, draw 0.05, push 0.05, textures 0.04). Vulkan calls a draw:
  4.3 colour, 4.0 depth, 3.0 grass. `NativeFrame.Bind` 3.3-5.0 us a segment: the 13 atmosphere getters 1.7-2.3 (`SkyRenderer` recomputes its values
  once per `ApplyCount`, i.e. per segment), the 7 frame textures 0.8-1.2, push and bind 0.8-1.0, the shadow blocks 0.4-0.5. The depth meshes' missing
  step-O gain (above) was the per-draw export and texture lookups, not the per-segment `Bind` (0.05-0.13 us a depth draw).
- **Observed: step P for comparison (the same instrumentation, after the shared fixes, which help them too).** Objects (`WorldObjectRenderer`, 20 draws
  a colour segment): loop 1.12 us a draw (2.04 before `ScalarOf` and the handle equality): `Flush` 0.51, `VertexArray` per draw 0.41, material uniforms
  and texture binds 0.28, vertex buffers 0.22, index buffer 0.13. Terrain patches (128 draws a colour segment, 214 depth): loop 0.17 us a draw (0.35 before):
  two uniform sets 0.18 [lapped], `Flush` 0.10, index buffer 0.04, draw 0.05. The patches are near the floor even in the legacy model: one program, one
  vertex buffer, a contiguous draw list, and a 32-byte uniform change; the objects pay for a per-draw export, a default-block copy and per-mesh heap objects.
- *Gate (Release; lighter gate, against master `3af1880` built unchanged, scratch in `C:\Temp\agent-P`).* Build 0 warnings; `dotnet test -c Release` 397
  passed, 0 skipped; `--faithful all` ten views 0 px (mean 0.0000); `--upscaler taa` and `--water-reflection 4`, forest and Hub at 13:00: 0 px;
  `MEITOU_VK_VALIDATION=sync` forest 13:00 and Hub 2:00: 0 errors. Renames over a 300-frame run: 600 before and after (the conservative rule renamed nothing more).
  The whole gate was run on `e1161d3` and again on `8ec1d22`, all passing both times.
- **Measured: forest still camera (`--fly-benchmark 300 --fly-speed 0 --faithful all`, `MEITOU_FOLIAGE_TIMING=1`), before = master `3af1880`, after =
  `8ec1d22`, three interleaved runs per build, medians and minima, Release.** The machine was busy (another agent's viewer and builds): every figure is about
  twice what a quiet run gives, and runs of one build differ by 30 percent, but within each interleaved pair the after build was faster on every per-draw row.
  Per draw includes the segment's share (12.0 colour, 132.5 grass, 36.5 depth draws per call, empty calls included).

  | | before, median | after, median | before, min | after, min |
  | --- | ---: | ---: | ---: | ---: |
  | colour meshes, us per draw (Prepare + Record) | 2.36 | 1.64 | 1.95 | 1.21 |
  | colour meshes, record only | 2.15 | 1.38 | 1.79 | 1.03 |
  | grass, us per draw (Prepare + Record) | 1.85 | 1.16 | 1.52 | 0.96 |
  | grass, record only | 1.17 | 0.46 | 0.99 | 0.41 |
  | depth meshes, us per draw | 2.34 | 1.54 | 1.70 | 1.20 |
  | depth meshes, record only | 2.07 | 1.31 | 1.55 | 1.04 |
  | stage `foliage`, ms | 1.02 | 0.89 | 0.85 | 0.62 |
  | shadow casters `foliage`, ms | 1.91 | 1.97 | 1.62 | 1.57 |
  | render thread p50 / p95, ms | 8.4 / 10.5 | 8.2 / 10.8 | 7.3 / 9.0 | 7.3 / 8.8 |
  | CPU only p50 / p95, ms | 3.9 / 5.3 | 3.8 / 5.7 | 3.1 / 4.2 | 2.9 / 3.6 |
  | render thread allocations, MB per run | 39 | 12 | 39 | 12 |

  An earlier set on a quieter machine (three pairs, after = `e1161d3`, the same per-draw code with the per-frame marking loop) gave, as medians, colour
  record 1.10 -> 0.73, grass record 0.70 -> 0.32, depth record 1.00 -> 0.73 us a draw, stage `foliage` 0.58 -> 0.49 ms, shadow casters 1.20 -> 1.28 ms.
  Observed: record time per draw down by a third (meshes) to more than half (grass); the foliage stage by 0.1-0.2 ms. The shadow casters' foliage time did
  not move beyond the scatter: there the cull (460-640 us a cascade call) and the TERRAIN-mode rocks (130-160 us) are most of it, and the meshes' record is
  50-80 us. The frame is GPU-bound in this view (gpu-wait 6-7 ms), so the render-thread percentiles do not move. In the quiet set the CPU-only p95 read
  0.4-0.6 ms higher after; the marking loop (45 us a frame, now gone) explains a little of that, and in this set the p95 is lower in two of the three pairs,
  so it is scatter. Note: the "before" figures differ from agent A's table above (1.1 or 2.2 against 2.75 us record a colour mesh draw) on nearly the same
  code because of the machine's load; compare figures only within one table.

**Wave 3b, agent C (objects), step O (2026-10-06, on master `68c30f8`).** `WorldObjectRenderer` (colour, shadow-caster depth, reflection and distant towns,
which share the code) records with native-model programs: `BuildingLodShaders.VertexNative()` / `FragmentNative()` / `DepthNative()` are
`NativeShaders.Port` of the legacy patched text with its own push block. Commits: `0400580` the port, `1a98868` the timing split. Files:
`WorldObjectRenderer.cs`, `BuildingLodShaders.cs` (the push block, `ObjectPush`), `BuildingLodMesh.cs` (`ObjectNativeMesh.Stamp`), and the two variants added to
`NativeShaderTests.Variants()`. `NativeShaders` and `NativeFrame` untouched.

- *Shaders.* The maths is the shared mesh text, as foliage. The push block is `MeshPush` less what the objects never vary (`uHeadDiffuse`, `uHeadNormal`,
  `uHasHead`, `uSkinned`: the GL code set them to 0 once, now `#define uHasHead false`, the head samplers any valid entry; `uCoverage`, which
  objects do not declare), plus the fade: `uFadeMode` (`pc.fadeMode`, 1 for distant towns), `uFadeRange` (`pc.fadeRange`, vec4, per call) and `uFadeEye`
  (`view.eye`: the legacy code set it to the eye). 128 bytes exactly (`ObjectPush`; the test checks the offsets). `uTriplanarScale` stays a push member
  (1/5000, set in every draw) rather than a constant, so the shader keeps a runtime value there as the GL program had.
- *Recording.* One `NativeFrame` for the renderer; per segment `BeginNativeInPass`, dynamic state (cull `None`: the objects are double-sided and GL's cull
  face stays off), `NativeFrame.Bind`, the four per-instance row bindings (the batch matrices in one `Frame.Constants` allocation per call, as in step P).
  Per draw: texture ids to bindless indices through a per-segment table indexed by GL name (no `Bindless` call after a texture's first use in the segment),
  the part's pipeline, vertex buffers and index buffer from its `ObjectNativeMesh` (kept while `VertexArrayStamp` holds: no `VertexArray` call), the 128-byte
  push when its bytes differ from the segment's last, `DrawIndexed`. The draw list is one renderer-owned `ObjDraw[]` (the part, the level and instance range,
  the GL texture ids and the push template built in Prepare); the material record struct, the per-program uniform copies, `Flush` and the unit samplers
  are gone. Prepare still reads the texture ids through `WorldTexture.Id` (reading counts as use) and still builds a push template per draw.
- **Gate (Release; lighter gate of the coordinator, base = master `68c30f8` built unchanged, scratch in `C:\Temp\agent-CO`).** Build 0 warnings; `dotnet test
  -c Release` 397 passed, 0 skipped (the legacy SPIR-V golden test unchanged: only a native program was added); `--faithful all` ten views **0 px** (mean 0.0000,
  max 0; the rock view included) on `0400580` and again on `1a98868`; `--debug-shadows 1` Hub 13:00 and `--water-reflection 4` Port North 13:00: 0 px;
  `MEITOU_VK_VALIDATION=sync` Port North 13:00 and Hub 2:00: 0 errors. The 1/255 allowance of owner decision 2 was not used, no `precise` / `invariant`
  needed; the dead skinning and head branches (constants) did not change a pixel. As for foliage the Port North reflection picture does not show the mirrored
  objects (the level-3 reflection has them already): that run checks the path, not the mirror.
- **Measured: still cameras (`--fly-benchmark 300 --fly-speed 0 --faithful all`, `MEITOU_OBJECT_TIMING=1`, `MEITOU_PASS_STATS=1`), three interleaved base/new pairs
  per view, medians (minima in brackets), Release; the machine was shared and a run's figures move by 15 percent.** Hub: `--town "The Hub" --distance 3000
  --pitch 10 --time 13`, 42.9 colour and 46.0 depth draws per call; Port North: `--town "Port North" --distance 1500 --pitch 10 --time 13`, 53.8 and 47.7. "Per draw"
  is a call's whole record (segment setup and end included, divided by the call's draws), "loop" the per-draw loop alone (two stamps), both from the same stamps
  as the step-P figures (the "before" column is step P on `68c30f8`).

  | | Hub before | Hub after | Port North before | Port North after |
  | --- | ---: | ---: | ---: | ---: |
  | colour, us per draw (Prepare + Record) | 1.53 (1.43) | 0.66 (0.64) | 1.24 (1.22) | 0.49 (0.48) |
  | colour, record only | 1.28 (1.20) | 0.47 (0.46) | 1.07 (1.05) | 0.36 (0.36) |
  | colour, loop only | n/a | 0.37 (0.36) | n/a | 0.29 (0.29) |
  | depth, us per draw (Prepare + Record) | 1.54 (1.32) | 0.99 (0.92) | 1.26 (1.23) | 0.89 (0.86) |
  | depth, record only | 1.23 (1.04) | 0.70 (0.67) | 1.03 (1.00) | 0.64 (0.64) |
  | depth, loop only | n/a | 0.52 (0.50) | n/a | 0.50 (0.49) |
  | stage `objects`, ms | 0.22 (0.21) | 0.13 (0.13) | 0.16 (0.15) | 0.09 (0.08) |
  | shadow casters `objects`, ms | 0.37 (0.36) | 0.37 (0.36) | 0.42 (0.42) | 0.42 (0.40) |
  | render thread allocations, MB per run | 16 | 16 | 13 | 13 |

  Observed: the colour draws cost less than half (record 1.28 -> 0.47 us a draw), the objects stage 0.22 -> 0.13 ms at the Hub; the loop is 0.29-0.37 us a colour draw
  and 0.49-0.52 a depth draw (the floor of a foliage-shaped draw is 0.12, 7.5), the per-call segment setup and end 3.7-5.5 us (colour) and 7-10 us (depth).
  Not measured: where the remaining 0.2-0.3 us a draw go (the vertex-buffer binds of 4 to 7 attributes, the 128-byte push, which differs on nearly every draw
  because the parts have their own materials). The shadow-caster stage did not move beyond the scatter: a cascade call is 40-50 us of cull, 55 us of recording
  (was 60 to 80) and the TERRAIN-mode meshes, and the stage's own total is 0.37-0.42 ms; the depth recording saved 20 us a call. Allocations did not change (the render
  thread's 13-16 MB are not the objects'). Sources of the segment cost, not changed here: `NativeFrame.Bind` read 3.5 us in a colour segment and 8 us in a depth
  one (temporary stopwatch, not committed; the depth shader reads none of the atmosphere or the frame textures, so a depth-only bind that skips them is a possible steward addition).
- *Follow-ups.* The TERRAIN-mode map features still go through `TerrainRenderer.DrawMeshes` (legacy program, step P); a per-material push template cached across
  frames would take Prepare's 0.2-0.3 us a draw; once meshes are native buffers one interleaved vertex binding per mesh would replace the 4 to 7 binds.

**Wave 3b, agent B (terrain), step O (2026-10-06, written on master `fe3253f`, rebased onto `a6d1a36` and gated there).** The terrain patches (colour, shadow
depth, and the reflection's colour, which is the same path) and the TERRAIN-mode meshes (`DrawMeshes` / `DrawGroups`: colour and depth, called by foliage and
objects with unchanged signatures) record with native-model programs. This closes "Step O not done" of the pilot (above) and the foliage and objects follow-ups
about the TERRAIN-mode meshes. Commits: `25d3ef0` the port, `7e421eb` the grouping and the timing. Files: `TerrainRenderer.cs`, `TerrainShaders.cs`,
`TerrainTextures.cs` (`Ids`: the GL names `Bind` puts on its units), and four variants added to `NativeShaderTests.Variants()`. Nothing shared changed.

- *Shaders.* `TerrainShaders.*Native()` are `NativeShaders.Port` of the legacy texts (patch vertex, colour fragment, mesh vertex and fragment, instanced mesh depth
  vertex, `ShadowShaders.DepthFragment`), bodies unchanged; the legacy texts are untouched, so the SPIR-V golden test is unchanged. The terrain has about 250 bytes
  of its own uniforms (the light colours, the material switches, the height grids' rects, the cell grid and region, and 11 texture indices), more than the
  128-byte push range, and set 0 is `NativeFrame`'s. So the terrain programs have a **third set** (`TerrainShaders.ConstantsBlock`, set 2, binding 0, a dynamic
  uniform buffer, `TerrainConstants`, 224 bytes), written into the frame's constants once per segment (again only when its bytes changed within the frame) and bound
  with a dynamic offset into a persistent set made once per constants chunk (`LegacyProgram`'s pattern for its default blocks). Sets 0 and 1 are the model's, so
  `NativeFrame.Bind` works on these layouts unchanged; set 2 is never pushed (2.6: only set 0 is). The camera, sun direction and fog are `ViewConstants`; a patch's
  `uNode` and `uMorph` are the push block (`TerrainPush`, 24 bytes). Textures by bindless index: the layer arrays in `textures2DArray`, `uCells` (RGBA8UI) in
  `utextures2D`, the rest in `textures2D`. The fragment text's `#extension GL_ARB_derivative_control` line is moved up to just after `#version` (an extension
  directive must come before the prelude's declarations); nothing else moves.
- *Recording.* Per segment: `BeginNativeInPass`, targets and state, dynamic state, `NativeFrame.Bind`, the terrain set; the 11 `interop.Bindless` exports are taken
  once per segment before it opens, with the terrain's own stand-ins (registered once, one per array kind) for a texture that does not exist yet or is of
  another kind (`Bindless(0)` gives the float 2D stand-in only). Patches: the pipeline, the grid's vertex buffer and its index buffer once; per node a 24-byte push
  and `DrawIndexed` with the range's `firstIndex` (the index buffer is bound once, not per range as VkGl did: the draw log differs there, the indices drawn do
  not). Meshes: per group the mesh's pipeline (two segment states kept, stable segment numbers), its own vertex buffers and index buffer from the inline
  `NativeMesh` (kept while `VertexArrayStamp` holds: no `VertexArray` call in steady state), the front face when the winding flips, `DrawIndexed` reaching the
  group's placements by `firstInstance`. The GL state the GL version left is kept (atmosphere units, height and material units, culling and winding after the
  meshes); the GL mesh programs and the GL depth program are no longer made (the GL patch program stays for the debug outline and is what assigns the
  atmosphere's units and publishes the shadow globals).
- *Grouping (`GroupMeshes`).* Was the most expensive part of a depth call: about 6,000 placements a forest cascade call, each looked up in a dictionary, copied
  into its group's array, and copied again into the frame's constants. Now two passes over the caller's list: a placement's group (the previous placement's
  group answers without a lookup: 97 lookups for 5,985 placements), then each placement written once, straight into its group's place in the constants (one
  64-byte line each, the allocation 64-aligned). Group order (by first placement), order within a group and the mirroring test (`GetDeterminant() < 0`) are as before.
  **Observed** (temporary stopwatch inside the passes, forest, not committed): of the remaining ~150 us of a depth call's grouping, ~70 us is writing 383 KB of
  placements into the write-combined constants (11.5 ns per placement, about 5.5 GB/s), ~30 us the determinants (5 ns each) and ~40 us reading the caller's 72-byte
  entries. GPU-driven placements (5.3) would remove all of it; a CPU path cannot go much below the write.
- **Gate (Release; lighter gate; base = master `a6d1a36` built unchanged, scratch in `C:\Temp\agent-BO`).** Build 0 warnings; `dotnet test -c Release` 459 passed,
  0 skipped (`KENSHI_PATH` set; legacy SPIR-V golden unchanged, the four terrain variants added to `NativeShaderTests` with the `TerrainConstants` offsets checked);
  `--faithful all` ten views **0 px** (mean 0.0000, the rock view included); Meitou default ten views **0 px** (the Meitou cascades and blocker map read the terrain);
  `--debug-shadows 1` forest 13:00 and `--water-reflection 4` Port North 13:00: 0 px; `MEITOU_VK_VALIDATION=sync` forest 13:00 (Faithful) and Port North 13:00
  (Meitou, reflection 4): 0 errors. The 1/255 allowance of owner decision 2 was not used; no `precise` / `invariant`.
- **Measured: still cameras (`--fly-benchmark 300 --fly-speed 0 --faithful all --time 13`, `MEITOU_TERRAIN_TIMING=1 MEITOU_MESH_TIMING=1`), forest (as 7.1) and The
  Hub (`--town "The Hub" --distance 3000 --pitch 10`), three interleaved base/new pairs per view, medians (minima in brackets), Release; the machine was shared.**
  Before = master `a6d1a36` with only the timing's warm-up skip added. Both timings now leave out the first 100 calls of each kind: in a first probe without it
  the depth patches' segment read 124 us a call, of which ~100 us were pipeline creation and first registrations spread over 600 calls (with the skip: 22 us).
  "Per draw" is the whole call (patches: node selection included; meshes: grouping included) over its draws; "segment" `BeginNativeInPass` to `EndNative`; "loop" the
  per-draw loop alone. Draws per call: patches 89 colour / 91 depth (forest), 91 / 92 (Hub); meshes 17 colour / 27 depth (forest, 574 and 5,985 placements),
  6.5 / 13.7 (Hub).

  | | forest before | forest after | Hub before | Hub after |
  | --- | ---: | ---: | ---: | ---: |
  | patches colour, us per draw | 0.63 (0.54) | 0.44 (0.36) | 0.52 (0.52) | 0.37 (0.35) |
  | patches colour, segment / loop, us per draw | n/a | 0.18 / 0.091 (0.14 / 0.076) | n/a | 0.15 / 0.080 (0.14 / 0.072) |
  | patches depth, us per draw | 0.62 (0.50) | 0.51 (0.39) | 0.53 (0.53) | 0.45 (0.43) |
  | patches depth, segment / loop, us per draw | n/a | 0.25 / 0.093 (0.17 / 0.080) | n/a | 0.20 / 0.087 (0.20 / 0.079) |
  | meshes colour, us per call | 69.9 (55.3) | 37.8 (30.5) | 20.3 (19.4) | 13.7 (12.9) |
  | meshes colour, us per draw | 4.11 (3.25) | 2.22 (1.79) | 3.10 (2.96) | 2.09 (1.97) |
  | meshes colour: grouping us per call, segment us per call, loop us per draw | n/a | 21.9, 13.2, 0.39 (18.4, 10.0, 0.26) | n/a | 4.3, 7.7, 0.42 (3.9, 7.4, 0.41) |
  | meshes depth, us per call | 282.8 (246.1) | 171.3 (139.3) | 30.8 (30.4) | 21.4 (20.5) |
  | meshes depth, us per draw | 10.47 (9.11) | 6.34 (5.16) | 2.25 (2.22) | 1.56 (1.50) |
  | meshes depth: grouping us per call, segment us per call, loop us per draw | n/a | 149.9, 18.1, 0.36 (124.6, 13.5, 0.26) | n/a | 11.6, 9.1, 0.33 (10.9, 8.9, 0.32) |
  | stage `terrain`, ms | 0.22 (0.18) | 0.21 (0.18) | 0.20 (0.20) | 0.20 (0.17) |
  | shadow casters `terrain`, ms | 0.34 (0.31) | 0.32 (0.29) | 0.35 (0.34) | 0.34 (0.30) |
  | shadow casters `foliage` / `objects`, ms (they call `DrawMeshes`) | 1.63 / 0.27 (1.20 / 0.23) | 1.47 / 0.17 (1.15 / 0.15) | 0.78 / 0.42 (0.76 / 0.42) | 0.83 / 0.30 (0.79 / 0.30) |
  | stage `shadows`, ms | 2.42 (1.86) | 2.13 (1.73) | 1.70 (1.67) | 1.63 (1.54) |
  | render thread allocations, MB per run | 12 | 10 | 10 | 9 |

  Observed: the patch loop is 0.08-0.09 us a draw (step P, the hot-path entry above: 0.17), near the floor; a patch call is now mostly its segment's fixed part
  (13-16 us colour, 18-22 us depth) and the node selection. The TERRAIN-mode meshes are 30-45 percent cheaper a call; in the forest's depth call the grouping
  (placements into the constants) is 87 percent of what is left, the segment 18 us. The terrain stages themselves hardly move (the patches were already cheap in
  step P, and both stages also hold the node selection); the saving shows in the callers' caster stages (objects' 0.27 -> 0.17 ms in the forest, 0.42 -> 0.30 at the
  Hub; foliage's moved inside its scatter, down in the forest and up at the Hub) and in the `shadows` stage. GPU time not measured (same SPIR-V maths, same targets).

**Wave 3b, agent A (foliage), step A2: GPU-driven culling (2026-10-06, written on master `a6d1a36`, rebased onto `ddb16da` and gated there).** The foliage meshes
of every view (the main slices, each shadow cascade, the reflection) are culled by compute kernels and drawn with `DrawIndexedIndirect`; design in 5.6.1, the steward
additions in 2.8. Commits: `4186728` the steward additions (`GpuFrame.PreFrame`, `ReadbackBuffer`, the cached indirect entry point) and their test, `faaccc7` the
kernels, the arena and `FoliageGpuCullTests`, `aab0faa` the renderer (switch, verify mode, statistics), `b3fee4d` the per-group bounds test and the rocks' timing.
Files: `FoliageGpuCull.cs` (new), `FoliageShaders.cs` (`CrSqrt`, `CullCompute`, `ScanCompute`, `CompactCompute`), `FoliageRenderer.cs`; in `Gpu/` additions only.

- *Switches.* `MEITOU_GPU_CULL=0` gives the A1 CPU cull (`FoliageCull`), the default is the GPU; `MEITOU_GPU_CULL_VERIFY=1` runs both and compares (5.6.1).
  `MEITOU_FOLIAGE_TIMING=1` prints, besides A1's line, the cull's dispatch recording per call, the TERRAIN-mode rocks' share of the cull, and a `foliage gpu
  cull:` line (views a frame, groups, candidates per view, GPU time per view from the kernels' own timestamps, arena use and growths).
- **Gate (Release; full gate; base = master `ddb16da` built unchanged, scratch in `C:\Temp\agent-A2`).** Build 0 warnings; `dotnet test -c Release`
  462 passed, 0 skipped (`KENSHI_PATH` set); `--faithful all` ten views **0 px** against the base (mean 0.0000, max 0; the base's two runs also 0 px between
  themselves, the rock view included); Meitou default ten views 0 px; `--water-reflection 4` Port North 13:00, `--upscaler taa` forest 13:00 and `--debug-shadows 1`
  forest 13:00: 0 px. Verify mode: forest screenshot (142 views, 103,217 visible instances), Hub at 40,000 (185 views, 7,123), forest flying 300 frames (1,381
  views, 1,434,985): 0 differences of sets, order, arguments or batch order, every fade equal; before the rebase also clean at the Hub flying, the trees and the
  Port North reflection. `MEITOU_VK_VALIDATION=sync` forest 13:00 and Hub 2:00: 0 errors (before the rebase also Port North with reflection 4). The 1/255
  allowance of owner decision 2 was not used.
- **Measured: A/B through `MEITOU_GPU_CULL` in the same build (`b92d67a`, i.e. `b3fee4d` before the rebase: the rebase brought only the terrain step O, which both
  modes share), `--time 13 --size 1600x900 --fly-benchmark 300`, `MEITOU_FOLIAGE_TIMING=1`, three interleaved runs per mode, medians (minima in brackets),
  Release; the machine was shared.** Views: *forest* (as 7.1, `--fly-speed 0 --faithful all`), *trees* (`--at -39000,-82000 --yaw 45 --pitch 5 --distance 800`,
  still, Faithful), *flying* (the forest, flying, Faithful), *x8* (the forest still, `MEITOU_FOLIAGE_RANGE=8`, the Tab "Foliage draw distance" slider at its top,
  Faithful ranges), *large* (the forest still, Meitou's size-based ranges with `--range-large 12000` and x8). "Cull" is the whole cull of one `Draw` call on the
  render thread (the work list, the chunks, the dispatch recording, and the TERRAIN-mode rocks' CPU cull, shown separately); "meshes" the call's Prepare and Record.
  The timing skips its first 160 calls; the stages and percentiles cover all 300 frames. "CPU only" is the render thread's frame without its GPU wait.

  | | forest GPU | forest CPU | trees GPU | trees CPU | flying GPU | flying CPU | x8 GPU | x8 CPU | large GPU | large CPU |
  | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
  | colour cull, us per call | 61.6 (61.0) | 72.2 (71.8) | 94.4 (93.8) | 112.6 (111.4) | 55.6 (54.1) | 76.2 (76.0) | 315 (315) | 375 (370) | 82.3 (80.4) | 151.5 (150.0) |
  | of which the rocks' CPU cull | 39.2 | | 72.9 | | 17.1 | | 261 | | 27.1 | |
  | depth cull, us per cascade call | 180.4 (174.5) | 340.8 (340.4) | 163.1 (158.2) | 213.7 (211.2) | 93.9 (93.0) | 185.3 (181.4) | 410 (404) | 765 (765) | 73.5 (72.6) | 153.7 (152.7) |
  | of which the rocks' CPU cull | 149.1 | | 145.2 | | 37.7 | | 358 | | 25.4 | |
  | meshes colour / depth, us per call | 28.2 / 61.7 | 10.6 / 27.7 | 39.3 / 43.0 | 13.9 / 20.8 | 36.9 / 78.7 | 18.3 / 36.7 | 33.8 / 65.6 | 19.7 / 32.2 | 27.0 / 46.4 | 11.4 / 16.3 |
  | draws colour / depth, per call | 28.1 / 51.5 | 12.0 / 36.5 | 34.0 / 28.2 | 14.7 / 16.5 | 20.2 / 49.9 | 12.2 / 26.2 | 33.9 / 54.0 | 27.6 / 39.5 | 25.4 / 30.2 | 9.3 / 13.4 |
  | stage `foliage`, ms | 0.50 (0.48) | 0.48 (0.47) | 0.72 (0.72) | 0.72 (0.70) | 0.51 (0.50) | 0.53 (0.53) | 2.00 (2.00) | 2.10 (2.08) | 0.53 (0.52) | 0.69 (0.69) |
  | shadow casters `foliage`, ms | 1.17 (1.16) | 1.18 (1.16) | 1.51 (1.51) | 1.58 (1.56) | 0.92 (0.90) | 0.75 (0.73) | 2.22 (2.19) | 2.64 (2.62) | 0.62 (0.62) | 0.66 (0.66) |
  | CPU only p50 / p95, ms | 1.9 / 2.4 | 2.2 / 2.6 | 2.7 / 3.1 | 2.8 / 3.2 | 2.4 / 4.8 | 2.5 / 5.1 | 4.6 / 5.1 | 5.5 / 6.0 | 1.6 / 2.2 | 1.9 / 2.2 |
  | frame p50 / p95, ms | 9.2 / 11.6 | 9.1 / 11.7 | 10.2 / 12.5 | 10.0 / 12.5 | 10.1 / 13.4 | 10.1 / 13.8 | 8.8 / 13.5 | 10.0 / 14.2 | 5.7 / 10.2 | 6.0 / 10.4 |
  | GPU cull, us per view (kernels' timestamps) | 34.4 (30.1) | | 25.0 (24.0) | | 25.6 (24.0) | | 21.1 (21.0) | | 20.9 (20.7) | |
  | candidates per view (groups) | 48,600 (164) | | 46,710 (167) | | 38,899 (128) | | 71,522 (238) | | 48,659 (144) | |

  GPU time of the foliage passes (`MEITOU_PASS_STATS=1`, two runs each, forest still, Faithful): main foliage 0.77 / 0.80 ms with the GPU cull against 0.69 / 0.41
  with the CPU cull, at x8 1.45 / 0.92 against 0.95 / 0.87; the pass meter's scatter between runs is as large as the difference, so **not resolved**; the extra
  (empty) indirect draws are the likely cost if there is one. The kernels themselves are 20-35 us of GPU a view (the three dispatches and their barriers), run
  ahead of the frame on the same queue; the frame percentiles did not move beyond the scatter.

  Observed:
  - The non-rock instances' cull no longer costs CPU per instance: what is left of a call's cull is the work list (per group), the chunks and the dispatch
    recording (12-20 us a call), and the TERRAIN-mode rocks, which stay on the CPU (5.4) and are now most of it: 149 of 180 us a forest cascade call, 358 of 410 at
    x8 (2,992 rock draws a cascade call in the Faithful forest). With Meitou's size-based ranges at x8 and a 12,000 large range (*large*) the cull is half the CPU
    one in both colour and depth.
  - The depth cull halved (forest 341 -> 180 us, x8 765 -> 410, flying 185 -> 94); the CPU-only frame p50 fell 0.1-0.9 ms, most at x8.
  - Recording costs more: the GPU path draws every batch whose groups' bounds meet the view, visible instances or not (forest depth 51.5 against 36.5 draws a
    call), and an indirect draw records about as fast as a direct one, so meshes per call doubled (depth 28 -> 62 us in the forest). The net per call is still a
    saving everywhere (forest depth 368 -> 242 us, flying 222 -> 173).
  - The stages: the shadow-caster `foliage` stage fell at the trees and x8, held at the forest and *large*, and **rose** when flying (0.75 -> 0.92 ms) although the
    timed per-call total fell there; in the same flying runs the terrain and objects caster stages fell (0.33 / 0.31 -> 0.23 / 0.20 ms), so the `shadows` total
    did not change. Cause **Unknown**: the stage covers all 300 frames and the timing skips the first 160 calls, and while flying new groups keep arriving (their
    first cull fills the spheres, the bounds and the arena upload on the render thread); not separated.
- *Follow-ups.* The TERRAIN-mode rocks to the GPU once `TerrainRenderer.DrawMeshes` takes GPU-made placements (5.4; **done**, next entry); `DrawIndexedIndirectCount` over one mesh arena
  (2.8), which would also remove the empty draws; the rows are reserved at 64 bytes a candidate a view in device scratch (the visible count is not known when
  recording; 4.6 MB a view at x8, in 32 MB chunks per frame slot), so a far larger range wants a bound on the rows or a count read back a frame late; the CPU still
  fills the spheres and uploads them per group at its first cull; grass and impostors were out of scope.

**Wave 3b, agent A (foliage), TERRAIN-mode rocks on the GPU cull (2026-10-06, on master `5827596`, gated there).** The rocks (TERRAIN-mode foliage
meshes) of every view (main slices, each cascade, the reflection) are culled and their placements written by the A2 kernels, in the same dispatch, and drawn
with `DrawIndexedIndirect` through the terrain's TERRAIN-mode programs; design in 5.6.1 ("TERRAIN-mode rocks"). Commits: `58b7553` the kernels (rock chunks:
fade ≥ 0.5, mirroring, biome rows by residency bits), `FoliageCull.RockBits`, the View buffer, and the test; `cbf97ba` the renderer (`FoliageRenderer` rock
batches and draws, `TerrainRenderer.DrawMeshesIndirect` with `OpenMeshSegment` / `CloseMeshSegment` shared with `DrawGroups`, `TerrainTextures`
`FeatureBiomeRowAny` / `ResidentBiomeBits`, verify mode). `DrawMeshes`' signature and the objects' calls are unchanged (objects' map features stay on it);
`MEITOU_GPU_CULL=0` keeps the CPU path for the rocks as for the rest. Nothing in `Gpu/` changed.

- **Gate (Release; lighter gate; base = master `5827596` built unchanged in `C:\Temp\agent-RK`).** Build 0 warnings; `dotnet test -c Release` 463 passed, 0
  skipped (`KENSHI_PATH` set; new `FoliageGpuCullTests.Gpu_cull_matches_the_terrain_mesh_path_for_rocks`: rock and mesh batches in one dispatch, mixed
  mirroring in a group, a third of the fades within ~50 ulp of 0.5, biome rows −1 and across all residency words, both biome modes: every row and argument
  bit for bit; it fails when the kernel's 0.5 is moved to 0.49999, checked); `--faithful all` ten views **0 px** (max 0); Meitou default ten views 0 px;
  `--debug-shadows 1` forest 13:00, `--water-reflection 4` Port North 13:00, `--upscaler taa` forest 13:00: 0 px; `MEITOU_GPU_CULL=0` forest and rock
  13:00 (Faithful) 0 px against the base too. Verify mode: forest screenshot (36 views, 54,802 instances and 72,149 rock placements), rock view (142,
  3,384 and 5,661), Hub at 40,000 (185, 7,123 and 996), forest flying 300 frames (1,379, 1,180,987 and 170,009): 0 differences of sets, order,
  arguments or batch order, every fade and every rock row equal. `MEITOU_VK_VALIDATION=sync` forest 13:00 (Faithful) and Port North 13:00 (reflection 4): 0
  errors. The 1/255 allowance was not used; the changed rock group order (5.6.1) showed no pixel.
- **Measured: A/B through `MEITOU_GPU_CULL` in the same build (`cbf97ba`), `--time 13 --size 1600x900 --fly-benchmark 300 --faithful all`,
  `MEITOU_FOLIAGE_TIMING=1 MEITOU_MESH_TIMING=1`, three rounds with the mode order alternating, medians (minima in brackets), Release; the machine was shared
  and slower than during A2 (the same CPU-mode figures are 1.5-2x A2's), so compare within this table only.** Views: *forest* (as 7.1, `--fly-speed 0`),
  *rock* (the parity rock view, `--at -51468,-14324 --yaw 95 --pitch 2 --distance 300 --fly-speed 0`), *flying* (the forest, flying). "Cull", "meshes",
  "rocks" are `Draw`'s steps per call (rocks: the TERRAIN-mode draw step, `DrawMeshes` or `DrawMeshesIndirect`); the CPU mode's rock cull is inside its
  cull (the groups are culled together), so the per-cascade rock cost is read from the cull and rocks columns together; "terrain meshes" is
  `MEITOU_MESH_TIMING`'s per call (foliage's and objects' calls).

  | | forest GPU | forest CPU | rock GPU | rock CPU | flying GPU | flying CPU |
  | --- | ---: | ---: | ---: | ---: | ---: | ---: |
  | depth cull, us per cascade call | 84.8 (71.7) | 593.0 (573.1) | 29.9 (26.1) | 290.2 (273.4) | 90.6 (88.0) | 214.7 (184.0) |
  | depth meshes (incl. dispatch recording), us per call | 127.5 (115.0) | 52.9 (49.7) | 59.5 (53.1) | 27.7 (27.5) | 114.3 (109.1) | 46.8 (38.0) |
  | of which dispatch recording | 34.0 (29.4) | | 20.7 (19.1) | | 28.7 (27.2) | |
  | depth rocks step, us per call | 23.6 (20.6) | 85.4 (84.5) | 6.2 (5.8) | 6.6 (6.3) | 23.7 (22.4) | 17.5 (16.6) |
  | depth cull + meshes + rocks, us per call | 236 | 731 | 96 | 325 | 229 | 279 |
  | rocks per depth call: indirect draws / CPU placements | 19.5 | 2,992.5 | 7.5 | 33.2 | 17.7 | 101.1 |
  | colour cull / meshes / rocks, us per call | 40.8 / 46.4 / 13.6 | 132.8 / 19.8 / 18.4 | 37.1 / 29.5 / 9.9 | 115.7 / 16.8 / 10.5 | 56.9 / 51.0 / 15.9 | 108.4 / 23.9 / 9.2 |
  | terrain meshes colour / depth, us per call | 23.8 / 22.9 | 40.9 / 172.4 | 14.6 / 18.5 | 15.7 / 18.2 | 21.8 / 22.9 | 19.2 / 25.7 |
  | shadow casters `foliage`, ms | 1.06 (0.84) | 1.82 (1.64) | 0.63 (0.62) | 1.64 (1.63) | 0.94 (0.90) | 0.91 (0.80) |
  | shadow casters `terrain`, ms | 0.13 (0.11) | 0.37 (0.35) | 0.38 (0.38) | 0.41 (0.34) | 0.15 (0.15) | 0.37 (0.36) |
  | stage `foliage`, ms | 0.13 (0.11) | 0.12 (0.11) | 0.11 (0.10) | 0.11 (0.10) | 0.55 (0.52) | 0.45 (0.43) |
  | stage `shadows`, ms | 1.56 (1.28) | 2.63 (2.35) | 1.59 (1.57) | 2.65 (2.51) | 1.56 (1.54) | 1.76 (1.60) |
  | CPU only p50 / p95, ms | 2.2 / 4.4 | 3.2 / 5.4 | 2.1 / 3.7 | 3.1 / 5.3 | 3.0 / 5.9 | 3.0 / 6.5 |
  | frame p50 / p95, ms | 5.8 / 8.9 | 6.4 / 10.1 | 5.3 / 10.8 | 6.5 / 9.4 | 6.9 / 13.9 | 7.0 / 16.0 |
  | GPU cull, us per view (kernels' timestamps, rocks included) | 24.7 (19.5) | | 22.3 (19.7) | | 19.5 (19.0) | |

  Observed:
  - In the still forest a cascade's foliage call fell from ~730 to ~240 us: the rocks' CPU cull (2,992 placements a cascade) and their grouping and
    placement copy in `DrawMeshes` (85 -> 24 us for the step, 172 -> 23 us a terrain mesh depth call) are gone; the shadow-caster `foliage` stage fell
    1.82 -> 1.06 ms, `shadows` 2.63 -> 1.56 ms, the CPU-only p50 3.2 -> 2.2 ms. The rock view gains the same way (casters 1.64 -> 0.63 ms).
  - The GPU-mode meshes step is 2-2.5x the CPU mode's (more draws: every shown batch, empty ones too, and the dispatch recording, 20-34 us a call);
    with few rocks in view (flying) the rocks step costs a little more than the CPU's (24 against 17.5 us: the segment's fixed part for ~18 indirect
    draws, some empty, against ~100 placements), and the shadow-caster `foliage` stage did not move (0.94 against 0.91 ms) while `shadows` and the terrain
    caster stage fell.
  - The terrain caster stage fell in the forest and flying (0.37 -> 0.13-0.15 ms); cause **Unknown** (the rocks are drawn inside the foliage stage);
    not separated.
  - GPU time of the kernels per view is 19-25 us with the rocks in (22k rock candidates a view in the forest, ~90k chunk slots in all); A2 measured
    21-34 us without them on a less loaded machine, so the rocks' GPU share is **not resolved** by these runs.
- *Follow-ups.* The empty indirect draws (in the meshes and rocks steps) would go with `DrawIndexedIndirectCount` (2.8); a rock group with both kinds
  of placement is tested twice (one run per kind); objects' TERRAIN-mode map features still go through `DrawMeshes` on the CPU.

### 7.2 Wave 3: ownership

Each agent owns its files completely: it may edit them, and nobody else may. Call-site counts are `IGl` calls from section 8.

| Agent | Owns (may edit) | IGl calls | Step P | Step O / GPU-driven |
| --- | --- | ---: | --- | --- |
| **A foliage** | `FoliageRenderer.cs`, `FoliageShaders.cs` | 147 | meshes, grass, grass motion (`DrawGrassMotion`, a guest in E's velocity pass), depth | A1, A2 (5.6), then native model, GPU-driven grass, then 5.7 |
| **B terrain + shadow host** | `TerrainRenderer.cs` (except `DrawMeshes`, done by the pilot), `TerrainTextures.cs`, `TerrainStreamer.cs`, `TerrainShaders.cs`, `TerrainShadowMap.cs`, `ShadowPass.cs` and `ShadowPass.Meitou.cs` (except `DrawDebug` and `CaptureDepth`, which belong to F) | 122 + 52 + 48 + 123 + 78, minus the debug views | patches, depth, the atlas host, the Meitou blocker map and terrain shadow sweep | native model; instanced main-pass TERRAIN-mode path (unblocks GPU-driven rocks for A) |
| **C objects + characters** | `WorldObjectRenderer.cs`, `BuildingLodMesh.cs` (`ObjectMeshCache`), `BuildingLodShaders.cs`, `BuildingMaterial.cs`, `MaterialResolver.cs`, `ObjectStreamer.cs`, `WorldObjects.cs`, `WorldTextureCache.cs` (shared with A: A only reads `WorldTexture`), `Model.cs`; the viewer's `Renderer.cs`, `CharacterRenderer.cs`, `CharacterScene.cs`, `Animator.cs` | 39 + 38 + 34 + 116 + 93 | objects, distant towns, the viewer's mesh and character drawing | native model; GPU-driven objects with LOD (optional, C1/C2 like A1/A2) |
| **D sky, clouds, water + reflection host** | `SkyRenderer.cs` (the clouds are part of its sky shader), `AtmosphereShaders.cs` *content* (frozen for others), `WaterRenderer.cs`, `ReflectionPass.cs` | 80 + 52 + 60 | sky, clouds, water, the reflection host (MSAA target and resolve) | native model, `FrameConstants` atmosphere part |
| **E post-processing + upscalers** | `PostProcess.cs`, `PostProcessShaders.cs`, `PostProcessOptions.cs`, `UpscaleShaders.cs`, `Upscaling.cs`, `Upscalers/*` (FSR, DLSS, Streamline) | 181 | (mandatory, owner decision 10) the scene targets (host), SSAO, velocity, TAA, exposure, composite, FXAA, heat haze, vendor upscalers on native images | native model |
| **F overlays, settings, debug** | `DebugOverlay.cs`, `SettingsPanel.cs`, `FrameProfiler.cs`, `FramebufferCapture.cs`, `ShadowPass.DrawDebug` / `CaptureDepth` and their shader text, the viewer's `Program.cs`, `CharacterApp.cs`, `WorldApp.cs`, `WorldApp.Benchmark.cs`, the game's `Program.cs` (its IGl calls: the screenshot target and readback) | 42 + 6 + 2 + debug views + 74 + 11 | (optional, owner decision 10) overlay text and panels, profiler chart, screenshots and readback, the debug views | native model |

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

**The hot-path pattern (from the pilot, `TerrainRenderer.DrawGroups` after step 1; costs in 7.1).** Step P of a renderer that draws
into a pass VkGl has open:

1. Before the segment, on the GL side as the GL version did: `Apply`-style uniform writes (`LegacyProgram.Set*`, `ApplyGlobals()`),
   sampler binds (`Bind(slot, interop.Sampled(...))`; resolve slot lookups once per `FrameGlobals.Version`, not per call). Per-frame
   instance data: one `ctx.Frame.Constants.Allocate` and one copy for all draws of the call.
2. `var cmd = interop.BeginNativeInPass(label)`; `var t = interop.CurrentTargets()`; `var s = interop.CurrentState()`. Use `BeginNative`
   (and your own `BeginRendering` / `EndRendering`) only when the segment needs barriers, copies, dispatches or another target.
3. Once per segment: `cmd.SetViewport(t.Viewport)`, `cmd.SetScissor(t.Scissor)`, `cmd.SetRaster(cull, front)`,
   `cmd.SetDepth(s.DepthTest, s.DepthWrite, s.Compare)`, `cmd.SetDepthBias(...)`, `program.Flush(cmd)`, and the shared instance buffer
   with `cmd.BindVertexBuffers(firstLocation, rows)`.
4. Per draw, only what differs, from a per-mesh cache built on first use: `var va = interop.VertexArray(vao)`; if
   `!ReferenceEquals(cached.Source, va)` or the segment's pipeline state changed (compare one number you bump when the
   `(program, t.Formats, s.Blend, s.ColourMask, s.Polygon, s.AlphaToCoverage, s.DepthClamp)` record changes), rebuild: `ctx.Pipelines.Get(
   s.Pipeline(program.Program, program.VertexLayout(attributes), topology, t.Formats, label))`, `program.VertexBuffers(attributes, 0, own)`,
   `va.Elements`. Then `cmd.BindPipeline` (filtered when equal), `cmd.SetFrontFace` only when it flips, `cmd.BindVertexBuffers(0, own)`,
   `cmd.BindIndexBuffer`, `cmd.DrawIndexed(count, instances, 0, 0, firstInstance)` with `firstInstance` reaching this draw's rows.
5. `interop.EndNative(cmd)`, then restore on `IGl` whatever GL state the GL version left behind (culling, winding, vertex array).

Added by the foliage probe (7.1, `FoliageRenderer`): `gl.Enable(CullFace)` before `CurrentState()` when the segment culls (`DrawState.Cull` is `None` while
GL's cull face is off, which hides the mode); do the texture-id reads and anything that can start a reload in Prepare, before `BeginNativeInPass`;
end the segment before a `StageClock.Sub` (the pass meter's `QueryCounter` is an IGl call); keep a mesh's pipeline for the last two segment states
(the reflection's multisampled target alternates with the scene's); compare a material's uniforms as a value (a record struct) and set them only
when it changed; the constants GL set on every draw can be set once at construction; per-instance rows from the frame's constants bound once and
reached by `firstInstance`; take the colour mask and the depth state of a guest pass (grass motion) from `CurrentState()`, never hard-code them.

Added by sky and water (7.1, agent D): a draw with no index buffer and no or one vertex buffer (a full-screen triangle from `gl_VertexID`, a strip quad)
is `cmd.Draw(n)` with a vertex layout of what `VertexArray` exports (none for the sky); set the GL state the draw reads (depth test and mask, blend, cull)
on `IGl` before `BeginNativeInPass`, so `CurrentState()` sees it, and put it back after `EndNative`. Dropping a renderer's GL programs is safe for the
atmosphere's sampler units: any later `WorldGl.Program` (the terrain's, built first) assigns them and publishes the units. A host whose only work is
framebuffer creation, clears, a blit and calls to its guests (`ReflectionPass`) had nothing to record natively at the time: it was left on GL, and the guest segments
(here the multisampled sky) used `CurrentTargets()`. (Since step H both hosts are native, 4.5.) `NativeSegment` (`SkyRenderer.cs`) is a reusable two-slot pipeline cache for such single draws.

Added by the native hosts (step H, 4.5): a host that owns a rendering instance keeps its GL framebuffer bound and its GL state set (guests read the targets, the sample
count for alpha-to-coverage and the fixed-function state from them) and moves only the commands that record (clears, blits) into its own segment. A guest then needs no
change. Inside a host pass `IGl` uploads and timestamps are allowed (the pass meter's `StageClock.Sub` and a per-cascade uniform-buffer write between guests), anything
that touches a pass still throws. (The guests still end their own segment before a `StageClock.Sub`, as above; not re-checked inside a segment.)

Avoid in the per-draw loop: dictionary lookups, LINQ, `CurrentTargets` / `CurrentState`, `Flush` when nothing was bound, a
`BindVertexBuffers` per instance row. Measure with a per-call stopwatch switch like `MEITOU_MESH_TIMING` (`=2` adds the warm repeat, 7.1): a single port's
first call per frame is dominated by cold code and data, not by the API's per-draw cost.

Added by the post-processing port (7.1, `PostProcess`): a **chain of full-screen draws into different targets** is one `BeginNativeInPass` segment per draw, with the GL side keeping
only `BindFramebuffer` and `Viewport` (that is what `CurrentTargets` reads, and what a guest in the same pass needs). Textures need no GL binds at all: `interop.Sampled(texture, program.SamplerInfo(slot))`
returns what VkGl would bind, but resolve it at the point where the GL code bound the unit (after `GenerateMipmap`, after a LOD-bias change), because the levels and the bias are baked
into the view and sampler. A sampler uniform set with `glUniform1i` has no native counterpart (the slot is the binding), so those calls just go. Such a port is not a saving:
a handful of draws a frame cost the same through a segment as through the translator (7.1, agent E), so do it for the phase-8 deletion and check the pixels, not the milliseconds.

**Step O (from the foliage, 7.1).** On top of the step-P pattern:

1. Shaders: build on `NativeShaders` (`MeshVertex(own)`, `MeshFragment()`, ...) or port your own text with `NativeShaders.Port(legacy,
   NativeShaders.Map(own), pushMembers)`; `own` maps only your uniforms (to `pc.*` members or constants). Declare your push block's C# struct
   with explicit offsets and add the variant to `NativeShaderTests.Variants()`, which checks the offsets against the reflection. Keep the
   whole push block within 128 bytes.
2. One `NativeFrame` per renderer (it owns its frame textures' entries and its set layout), programs from `nativeFrame.Program(v, f, name)`;
   `ctx.Pipelines.Forget(p)` before disposing one.
3. Per segment, after `BeginNativeInPass` and the dynamic state: `nativeFrame.Bind(cmd, program.Layout, in view)` once (any of your native
   programs' layout: they are compatible). Not per draw, and again in every segment (the shadow blocks move between cascades).
4. Per draw: textures by `interop.Bindless(id)` (check `handle.Kind` against the array the shader reads), cached for the segment only, by GL
   texture id, not by slot (`FoliageRenderer.Texture`: an array indexed by name with a segment number);
   `cmd.PushConstants` of the whole struct, skipped when the bytes equal the last push of the segment.
5. Vertex arrays: keep `interop.VertexArrayStamp` per mesh and call `interop.VertexArray(vao)` only when it moved; rebuild the layout and
   buffers only when the returned object differs (`ReferenceEquals`). The stamp holds across frames (4.2), so in steady state there is no fetch at all.
6. Expect 0 px against step P; the pushed frame set stays at set 0 (2.6) as long as legacy programs push theirs.

**The step-O hot path (measured on foliage, objects and terrain patches: 7.1, "the step-O hot path"; the floor in 9.1).** What the objects' and the
terrain's step O should do from the start, in order of what it cost foliage:

1. *Nothing per draw that is not a command.* The floor of a foliage-shaped draw through `CommandList` (seven vertex buffers, index buffer, 128-byte push,
   indexed draw) is 0.12 us hot; raw Silk.NET calls 0.11, cached function pointers 0.09-0.10. A step-O draw at 0.4-0.7 us is therefore 75-85 percent
   renderer-side data access. No export call (`VertexArray`) and no `Bindless` call in the steady state of the loop: both are caches you keep (5 above,
   and texture indices per segment by id); the objects' step P called `VertexArray` per draw (0.41 us of its 1.12): step O keeps the stamp.
2. *No hidden allocation or boxing.* Two of the three big foliage costs were a `Format.ToString()` and record-struct equality over Silk.NET handles
   (`Sampler`, `ImageView`, `Image`, `Buffer` are not `IEquatable`: a `record struct` holding them boxes on `==`). Compare handles (`.Handle`), never enum
   names; check the benchmark's `gc ... allocated (render thread N MB)` line before and after a port (12 MB a 300-frame forest run now, most of it not foliage).
3. *Per-draw data contiguous, few objects per draw.* The terrain patches run at 0.17 us a draw even in step P with `Flush`, because the draw list is one
   struct array and every draw shares the program and vertex buffer. The foliage meshes at 0.43 touch per draw a `GpuPart`, its `NativeMesh`, its
   `BufferBinding[]` and the driver's objects for seven buffers: the vertex-buffer bind is now the biggest single item (0.15-0.26 us cold against 0.06-0.09
   hot). For objects: keep a mesh's pipeline, vertex bindings and index binding inline in one renderer-owned struct array (index per mesh), not in fields
   of per-part classes; and once meshes are native buffers (phase 8), one interleaved vertex binding per mesh instead of one per attribute.
4. *Segments big enough.* A segment costs 6-15 us before its first draw (`BeginNativeInPass`, targets and state, the dynamic state, `NativeFrame.Bind`
   at 3.3-5 us). One segment per pass or cascade with tens of draws; a 2-draw segment (objects in a shadow cascade: 3.9 us a draw) is all overhead.
5. *Measure the loop and the segment separately, with two stamps, interleaved, minimum of the runs.* Per-step laps cost 0.02 us each (subtract them);
   EventPipe sampling cannot see this; this machine's load moves the per-draw figures by a factor of two between runs.
6. *Then look elsewhere.* After this work the foliage cull is 460-640 us per cascade call against 50-80 us of recording; GPU-driven culling (5.3, A2)
   is the next CPU item for foliage, and for objects the cull (58 us a call, 7.1) is already bigger than the recording should be.
7. *Objects, done this way (7.1, agent C step O).* The loop is 0.29-0.37 us a colour draw and 0.50 a depth draw, the whole record 0.5-0.7 and 1.0 including the
   segment: the guidance above took the colour draw from 1.28 to 0.47 us. Lessons: a part's draw list entry should carry its push template built in Prepare
   (only the texture indices are patched per segment); a shader whose consumer never varies a mapped uniform (head textures, skinning) can map it to a
   constant in its `own` map and give the push block's bytes to what it does vary (the fade range) without leaving the 128 bytes; the segment's fixed part
   (setup, `NativeFrame.Bind` 3.5-8 us) is what keeps a two-draw cascade expensive per draw, which only a depth-only bind or fewer segments would change.
8. *Terrain, done this way (7.1, agent B step O).* A renderer whose own per-segment values do not fit the 128-byte push range gets a third set of its own: a
   dynamic uniform buffer in the frame's constants, bound at an offset into a persistent set per constants chunk (`TerrainRenderer.BindConstants`); sets 0 and 1
   stay the model's, so `NativeFrame.Bind` needs no change, and the extra set is never pushed. Register your own stand-in per bindless array you read
   (`Bindless(0)` is the float 2D one only: wrong for `textures2DArray` and `utextures2D`). When `Port` moves a text with an `#extension` line, put that line
   right after `#version`. Time a once-a-frame segment without its first calls: averaged over 300 frames, pipeline creation and first registrations made the
   terrain depth segment read 124 us instead of 22. After the commands, the next cost of an instanced path is often the per-instance upload: 64-byte
   placements into write-combined memory run at about 11.5 ns each (5.5 GB/s), so write each one once, in its final place.
9. *A GPU cull, done this way (7.1, agent A step A2; for the objects' C2).* Record each view's cull into `GpuFrame.PreFrame` where the view is drawn (works
   inside host passes, where a dispatch cannot be recorded), with your own barriers before (transfer to compute) and after (compute to indirect and vertex
   input). Keep the CPU's decisions bit-exact: `precise`, the C#'s operation order, and never the driver's `sqrt` (1 ulp off on 17 percent of inputs on the
   RTX 4070; `FoliageShaders.CrSqrt` is the correctly rounded one). Stable order costs nothing if the chunks are grouped by batch on the CPU and offsets are a
   plain prefix. An empty indirect draw costs as much CPU as a full one: test each group's bounds against the view before drawing its batch, or a cull with no
   zone test (the cascades) records more than it saves. Write the verify mode (CPU and GPU both, compared a frame late through a `ReadbackBuffer`) first; it
   found every problem the pictures did not.

### 7.6 The draw log

The foundation adds a per-draw log on both sides: `VkGl` (in `PrepareDraw`) and `CommandList` (at each draw). One line per draw, with the
stage and pass label, the program (source hash and name), the pipeline state (VkGl's `PipelineKey` fields, the native desc), the dynamic
state values, the attachment images, the descriptor handles (textures as image and sampler handles, which are the same objects on both
sides through export), vertex and index buffer handles and offsets, the counts, and a hash of each default block's bytes.
`MEITOU_DRAW_LOG=<file>` writes it for one frame of a `--screenshot` run. `meitou-tools draw-log-diff a b` compares two logs after mapping
GL names to handles. This turns "20 pixels differ" into "draw 1,812 has another sampler". docs/render-shadows.md records exactly such an
unexplained case.

### 7.7 The parity gate procedure

For every step that claims parity (foundation steps, 3a ports, A1, A2, C1, C2, step O, wave 4):

1. **Pre-port build**: master (or the step's base) in Release. Run `tools/scripts/parity.sh <viewer> <dir1> --faithful all` **twice**
   (`dir1`, `dir2`) and compare them with `parity-compare.sh`. Every view must show maximum difference 0. A view that is not deterministic
   is reported and rerun, and the step cannot be judged on it until it is explained. docs/render-shadows.md has one unexplained rock-view run with
   20 differing pixels. The script renders **five views at 13:00 and 02:00, ten pictures** (docs/engine.md and DECISIONS 2 say the
   same; older entries that say eight views date from before the forest view).
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
| 14 | **GPU culling floats** (5.6) | A2, C2 | A1/C1 alignment, the verifier, owner decision 2 |
| 15 | **Merge conflicts in `WorldFrame.cs`** | everyone | constructor plumbing by the foundation; line-local edits only |
| 16 | **Hardware floor** (bindless, multi-draw) | users with old GPUs or drivers | checked at start with a message; owner decision 1 |

---

## 8. Phase 8: deleting IGl and VkGl

*In short: when every renderer records natively, the translation layer and the GL-shaped interface are dead code and go, together with the
helpers built around them. This section lists what uses them today, so the deletion can be checked off file by file.*

### 8.1 Inventory today (from the code, master `0153744`)

`IGl` declares 90 distinct member names. All 90 are used. DECISIONS 7 counted 88 functions over 957 call sites when it was written. Counted
here: calls on an `IGl` (`gl.` or `Gl.` followed by an `IGl` member name), excluding VkGl itself:

| File | IGl calls | Owner (7.2) | After stage 3 (8.9) |
| --- | ---: | --- | --- |
| `src/Meitou.Rendering/PostProcess.cs` | 181 | E | **0** (since stage 2, 8.7) |
| `src/Meitou.Rendering/ShadowPass.cs` | 123 | B (debug views F) | **0** |
| `src/Meitou.Rendering/TerrainRenderer.cs` | 122 | B | **0** (since stage 2, 8.6) |
| `src/Meitou.Rendering/FoliageRenderer.cs` | 147 | A | **0** (since stage 1, `.Grass.cs` too) |
| `tools/Meitou.ModelViewer/Renderer.cs` | 116 | C | deleted (stage 3 step 1, DECISIONS 23) |
| `tools/Meitou.ModelViewer/CharacterRenderer.cs` | 93 | C | deleted (stage 3 step 1, DECISIONS 23) |
| `src/Meitou.Rendering/SkyRenderer.cs` | 80 | D | **0** (since stage 2, 8.6) |
| `src/Meitou.Rendering/ShadowPass.Meitou.cs` | 78 | B | **0** |
| `src/Meitou.Rendering/ReflectionPass.cs` | 60 | D | **0** |
| `src/Meitou.Rendering/WaterRenderer.cs` | 52 | D | **0** (since stage 2, 8.6) |
| `src/Meitou.Rendering/TerrainTextures.cs` | 52 | B | **0** (since stage 1) |
| `src/Meitou.Rendering/TerrainShadowMap.cs` | 48 | B | **0** |
| `src/Meitou.Rendering/DebugOverlay.cs` | 42 | F | **0** |
| `src/Meitou.Rendering/WorldObjectRenderer.cs` | 39 | C | **0** (since stage 1) |
| `src/Meitou.Rendering/BuildingLodMesh.cs` | 38 | C | **0** (since stage 1) |
| `src/Meitou.Rendering/WorldTextureCache.cs` | 34 | C | **0** (since stage 1) |
| `src/Meitou.Rendering/WorldGl.cs` | 23 | foundation | deleted (stage 3 step 5c) |
| `tools/Meitou.ModelViewer/Program.cs` | 23 | F | **0** |
| `tools/Meitou.ModelViewer/CharacterApp.cs` | 23 | F | deleted (stage 3 step 1, DECISIONS 23) |
| `tools/Meitou.ModelViewer/WorldApp.cs` | 20 | F | **0** |
| `src/Meitou.Rendering/ShadowShaders.cs` | 16 | foundation (`Bind`) | **0** (`Bind`, `PublishGlobals` deleted, step 5c) |
| `src/Meitou.Game/Program.cs` | 11 | F | **0** |
| `tools/Meitou.ModelViewer/WorldApp.Benchmark.cs` | 8 | F | **0** |
| `src/Meitou.Rendering/MeitouShadowShaders.cs` | 8 | foundation (`Bind`) | **0** (`Bind`, `PublishGlobals` deleted, step 5c) |
| `src/Meitou.Rendering/WorldFrame.cs` | 7 | the agents of the calls | **0** |
| `src/Meitou.Rendering/FrameProfiler.cs` | 6 | F | **0** |
| `src/Meitou.Rendering/FramebufferCapture.cs` | 2 | F | **0** |
| **total** | **1,452** | 1,158 in `src/Meitou.Rendering`, 283 in the viewer, 11 in the game | **0**: `IGl` itself is deleted |

*The counts are as taken at master `0153744`; the intermediate counts after step P and stages 1 and 2 are in 8.3 to 8.8 and in the history
of this file. "After stage 3" is **Verified** (2026-10-07): `IGl`, `IGlInterop` and `VkGl` no longer exist, so the tree builds only with no
call left; a `grep -E '\b[gG]l\.[A-Z]'` over each file that remains finds none.*

Besides `IGl`, code uses **VkGl's own public surface**: `VulkanPresenter.cs` (`BeginFrame`, `EndFrame`, `Backbuffer`, `RecordInFrame`),
`FsrUpscaler.cs` and `DlssUpscaler.cs` (`ImageOf`, `BeginNative`, `EndNative`, `Device`; they used `BeginExternal` and `EndExternal` until wave 3 agent E), `VulkanDisplay.cs` (constructs it), and the
game's and viewer's frame loop. **Tests**: `tests/Meitou.Tests/Vulkan/VkGlTests.cs` makes 129 `IGl` calls. The `Vulkan` test folder has 30
tests in all (`CoreTests`, `ShaderCompilerTests`, `ShaderInterfaceTests`, `ShaderReflectionTests`, `VkGlTests`). *After stage 3: the
presenter and the upscalers run on `GpuContext` (8.9); `VkGlTests` and the seam tests are deleted.*

Beyond the draw calls, resource creation also goes through `IGl` in `TerrainTextures`, `WorldTextureCache`, `BuildingLodMesh`, `TerrainShadowMap`,
the foliage meshes and grass pages, `PostProcess` targets and `SkyRenderer` textures. Phase 8 includes moving those to native `Texture` /
`DeviceBuffer` creation through the `Uploader`. That is owner work by the same agents (7.2), since step P may leave resource creation on IGl.
`WorldTextureCache`, `BuildingLodMesh` and the foliage meshes are done (phase 8 stage 1, 8.5); the sky's and the water's in stage 2 (8.6), the `PostProcess` targets too (8.7); the rest (terrain shadow map, overlay atlas, backbuffer) in stage 3 (8.9): no resource is created through GL any more.

### 8.2 End state

Deleted:
- `src/Meitou.Rendering/Gpu/IGl.cs`, `GlEnums.cs` (343 lines), `ITextureLodBias.cs` (replaced by the sampler bias of 2.6), `IGlInterop.cs`.
- `src/Meitou.Rendering.Vulkan/VkGl*.cs` (2,806 lines), and with it the per-draw translation.
- `WorldGl.cs` (`Program`, `Matrix`, `Texture2D` helpers), `SkyRenderer.AssignSamplerUnits` (since stage 2 a forwarder to
  `AtmosphereShaders.AssignSamplerUnits`, 8.6), `SkyRenderer.BindUnits` (empty since stage 2), `ShadowShaders.Bind`, `MeitouShadowShaders.Bind`
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

The phase-8 gate is the usual one: 0 differing pixels against the last build with VkGl present. *Stage 3 (8.9) did all of the above except deleting the legacy shader model, which is still in use. Of the "Updated" items, docs/engine.md is rewritten; DECISIONS 7 and 18 are left to the owner.*

### 8.3 Stage 1, shadows and reflection: as built (2026-10-06, on master `6f4af19`)

Files: `ShadowPass.cs`, `ShadowPass.Meitou.cs`, `ReflectionPass.cs`, and a new `PassTimer.cs` (`PassTimer`, `FrameBlock`). No file in `Gpu/` and
no reserved file changed. `IGl` calls (8.1's count): `ShadowPass.cs` 97 → 24, `ShadowPass.Meitou.cs` 61 → 9, `ReflectionPass.cs` 50 → 27.
Every resource is native now; what is left on GL is the GL mirror the native guests still read (4.5).

**Native now:**
- *The atlas.* It is a `Texture` (`D32Sfloat`, named "shadow atlas"). Its samplers come from `SamplerDesc.FromGl` with the GL parameters it
  had: linear, clamp to edge, compare `LEQUAL` for the receivers, and no compare for the blocker pass and the debug view. It is imported into GL
  (`interop.Import`) only to be attached to the GL framebuffer whose binding the casters' `CurrentTargets` reads.
- *The noise* ("shadow noise", RGBA8) is uploaded through the `Uploader` in the first frame (uploads need an open frame), at the latest when a segment first reads it.
- *The blocks.* The receiver, caster and Meitou blocks are `FrameBlock`s: on the first read in a frame after a write, they write the
  current data into a slice of the frame's constants. The casters' bias is set per cascade before the draw callback, and the
  guests take a new slice in their Prepare. A slice never outlives its frame. This is what VkGl's renaming of the GL buffer gave. `ShadowPass` publishes the seven shadow globals itself
  (the blocks, `uShadowMap`, `uShadowNoise`, `uShadowBlocker`, `uShadowTerrain`). They replace `ShadowShaders.PublishGlobals`, which reads
  units and binding points and stays the source without a `ShadowPass` (`--no-shadows`, the model viewer). *Stage 3: the model viewer was dropped (DECISIONS 23, which replaces owner decision 7) and `PublishGlobals` deleted; without a `ShadowPass` the sky's `ShadowsOffGlobals` (8.6) are the source.*
  - `uShadowTerrain` is `interop.Sampled(TerrainShadowMap.Texture)`: the terrain map is still a GL texture, owned by the terrain agent's file.
  - A texture is published from the point where the GL code bound it to its unit. Before that, readers get the stand-in.
- *The blocker map* ("shadow blocker map", R32F) is drawn in its own `BeginNative` segment and rendering (`LOAD`), with a `DrawState`
  written out (no cull, depth or blending, RGBA mask: what `CurrentState` reported there). It reads the atlas through the plain sampler,
  so the atlas's compare-mode `TexParameter` toggle is gone.
- *GPU timing*: `PassTimer` (the frame's `QueryArena` through `Interleave`) replaces the GL timer queries. `Poll(wait)` keeps its signature
  but no longer blocks.
- *The debug views*: their own rendering on the target's `CurrentTargets`, with the state `CurrentState()` plus what the GL code set (no depth,
  no cull, the multiply for mode 3, blending off for the atlas after the scene view, the inherited blend otherwise). The scene's depth is
  copied with `vkCmdCopyImage` into "shadow debug scene depth".
- *The reflection*:
  - Targets: "reflection colour" (RGBA16F, sampled by the water), "reflection colour msaa" and "reflection depth msaa" (4×).
  - "reflection depth" is made only without MSAA. Before, it was always allocated and never used with MSAA.
  - The resolve is `Resolve(Texture, Texture)`. The sample count comes from the device's limits (VkGl answered `MAX_SAMPLES` with 8, so it is
    4 either way).

**What stays on GL, and why:**
- (a) The guests' GL mirror. Guests read `CurrentTargets()` and `CurrentState()` from VkGl's GL state, so the hosts still keep it:
  - the framebuffer binding (the native targets imported and attached; creation and deletion of that framebuffer);
  - the viewport and scissor per cascade or slice;
  - depth test, mask, function and clamp; the colour mask; blending; the scissor enable;
  - the restore afterwards, and in the reflection 3 `GetInteger` calls, used only when the caller does not say what to restore.

  This accounts for 22 calls in `ShadowPass.cs`, 8 in `ShadowPass.Meitou.cs` and 22 in `ReflectionPass.cs`.
- (b) The reflection's colour keeps a GL name with its GL sampler parameters (`BindTexture` + 4 `TexParameter`). `WaterRenderer` (reserved)
  samples it through `interop.Sampled(reflection.Texture, ...)`, and an imported name would otherwise have GL's default sampler (nearest
  mipmap, repeat). *Gone in stage 2 (8.6): the water samples `ReflectionPass.Sampled`, the native colour with that sampler state; the GL
  name is left only as the guests' framebuffer attachment.*
- (c) State that later GL-state readers inherit:
  - `gl.Disable(CullFace)` after the blocker pass, which the GL version left off. `WorldObjectRenderer` never sets the enable, so it
    inherits it. **Unknown** whether pixels move without it: kept, not tested.
  - The debug views and the depth copy bind the target framebuffer once each, to name it for `CurrentTargets`.
- `ShadowShaders.cs` and `MeitouShadowShaders.cs` are unchanged: `Bind` is still called by `WorldGl.Program` and the model viewer's
  `Renderer`, and their unit-based `PublishGlobals` is the fallback above. *Stage 3: both callers are gone (the model viewer by DECISIONS 23,
  replacing owner decision 7), and `Bind` and `PublishGlobals` with them (8.9).*

**Seam needs (for stage 2 or 3):**
1. A host that supplies its own targets and state, so that group (a) goes:
   - `IGlInterop.BeginHostPass(CommandList cmd, PassTargets targets, DrawState state)`, plus a per-cascade or per-slice
     `SetHostViewport(Viewport, Rect2D)`;
   - while the host pass is open, `CurrentTargets()` and `CurrentState()` answer from these.

   The guests still change GL state before `CurrentState()`: the casters' `Enable(CullFace)`/`CullFace`/`PolygonOffset`, and the sky inside
   the reflection turns the depth test off and on. So either the merge rule is "the host's base state, then the guest's own GL calls", or
   each guest takes a `DrawState` from its host (4.5, Limits (c)).
2. `WaterRenderer` takes the reflection as a native `Texture` (with its own sampler) instead of the GL name. That removes group (b). *Done in
   stage 2 (8.6).*
3. `ShadowShaders.Bind` and `MeitouShadowShaders.Bind` go when no GL program is linked any more (stage 3).

**Learned:**
- **Verified** (code reading and 0 px): VkGl's `SamplerFor` compares only for `shadow && t.Compare`. So the GL version's compare-mode toggle
  around the blocker pass never changed the plain sampler the blocker shader got. The native version keeps two samplers and has no toggle.
- **Verified** (`ShadowPassNativeTests`, and 0 px): an owner's `FrameGlobals.Publish` wins over `ShadowShaders.PublishGlobals` whatever
  their order. The latter returns at once when the receiver block is already published.
- **Observed**: a `LegacyProgram` whose shader declares `MeitouShadowReceiver` throws when the block has no binding. This happened with the
  Faithful debug view, mode 3, in the first attempt. `NativeFrame` substitutes zeros, `LegacyProgram` does not. So a `FrameBlock` starts
  as a zero slice, as the zero-filled GL buffers on the binding points were.
- **VRAM** (`GpuAllocator.Breakdown`):
  - The new names are "shadow atlas" (16 MB at 2048²), "shadow blocker map" (4 MB), "shadow noise", "reflection colour", "reflection colour
    msaa" and "reflection depth msaa" (about 3, 11 and 6 MB at 800 × 450), and "shadow debug scene depth" (debug views only).
  - `gl texture` loses the atlas, the blocker map, the noise and the reflection colour, and `renderbuffer` loses the reflection's three.
  - The shadow names are **Verified** by `ShadowPassNativeTests`. The F12 pie itself was not looked at (interactive).

**Gate** (Release, RTX 4070, against the shared baseline of `6f4af19`):
- Build: 0 warnings. `dotnet test -c Release`: 466 passed, 0 skipped.
- Pixels, all max 0 (mean 0):
  - the ten views `--faithful all` and the ten views in Meitou mode;
  - against base-viewer renders of the same build: `--debug-shadows 1` forest 13:00 in both modes, `--debug-shadows 2` (Meitou) and `3`
    (Faithful), `--water-reflection 4` Port North 13:00 in both modes, `--upscaler taa` forest, `MEITOU_RECORD_THREADS=0` forest, forest
    and zone 14,30 Meitou, Hub Faithful.
- `MEITOU_VK_VALIDATION=sync`, 0 errors on each of: forest 13:00; Port North 13:00 `--water-reflection 4` in both modes; forest
  `--debug-shadows 2`; forest `--debug-shadows 1 --faithful all`.
- New test: `ShadowPassNativeTests` (`[Slow]`, sync validation). A Meitou pass with no casters has the named allocations, the native
  globals (the atlas image behind `uShadowMap`), and a blocker map of 1.0 everywhere.
- Benchmark: not run. Per frame, a GL `BufferSubData` and rename per cascade became a 16-byte constants slice, and the GL timer queries became native timestamps; nothing was added to a per-draw path.

### 8.4 Stage 1 as built: terrain (2026-10-06)

*In short: the terrain's textures, buffers and its shadow map's targets are native, its draws no longer set GL state, and the debug outline
draws natively. Five `IGl` calls are left in the three files (183 at `6f4af19`), all for other code's sake: one GL program whose linking starts
up the sky's and the shadows' globals, and the shadow map's GL name for `ShadowPass.Meitou`. 0 px everywhere.*

**Calls** (`gl.` / `Gl.`, comments excluded): `TerrainRenderer.cs` 98 → 1, `TerrainTextures.cs` 52 → 0, `TerrainShadowMap.cs` 33 → 4.
Commits `b5fa777` (textures), `8ecf886` (shadow map), `63a541c` (renderer).

**What moved.**
- `TerrainTextures`: the two layer arrays (BC3, BC1), the biome parameters (RGBA32F), the cell table (RGBA8UI), the blend map, the overlay and
  colour windows and the two world-wide maps are native `Texture`s, written through the `Uploader` (the streaming steps as before, one
  `UploadBatch` each). `Bind()` and `Ids` are gone (only the terrain used them); `Textures` hands the renderer the native set.
- `TerrainRenderer`: the coarse and fine heights (R16) are native; the fine-window swap retires the old one after 4 frames as before
  (`Dispose`: image and bindless entry freed after the frames in flight). The patch grid is a vertex and an index `DeviceBuffer`; the patch
  pipelines no longer fetch a vertex-array export. `uHeightCoarse` / `uHeightFine` are published as the native textures. The GL state calls
  around the draws (`Enable(DepthTest)`, `Enable/Disable(CullFace)`, `CullFace`, `FrontFace`, `BindVertexArray(0)`, the height and material
  unit binds) are gone; the patches take the pass's state from `CurrentState()` with the terrain's own culling put in (`PatchState`: back
  faces, counter-clockwise), as the meshes already did. The debug outline (`--wireframe`) is the native patch program with
  `TerrainConstants.Wireframe = 1`, a line-mode pipeline and a depth bias of (-1, -1), where GL had `PolygonOffset(-1, -1)` on lines.
  `Apply`, `BindHeights`, `BindHeightUnits` and the uniform-location cache are gone (`BindHeights` was public but had no caller).
- `TerrainShadowMap`: the heights (R16) and the two RG32F targets are native; the sweep is one `BeginNative` segment, each doubling pass a
  rendering of its own into one target with a full barrier after it, the source bound as a native `SampledTexture` (no GL unit).
- `TerrainTexture` (in `TerrainTextures.cs`, used by it and `TerrainRenderer`; the shadow map keeps plain `Texture`s and `SampledTexture`s): a native texture with the GL sampler state its GL version had
  (`SamplerDesc.FromGl`, the upscaler's LOD bias from `GpuContext.LodBias` on mipmapped filters, as `VkGl.SamplerFor`) and its bindless entry,
  re-registered (the old index freed) when the sampler changes, as `IGlInterop.Bindless` does.

**Helpers added in `Gpu/`** (additive): `Uploader.Begin()` → `UploadBatch` (create a texture, write texels or buffer bytes, and record what
goes with the uploads into `Commands`): into the frame's upload command buffer while a frame is open (`PreFrame`), else into a one-shot command
buffer with staging of its own that `Dispose` submits and waits for. `CommandList.GenerateMips(Texture)`: VkGl's `GenerateMipmap` (a full
barrier, per level a transfer barrier and a linear blit from the level above, a full barrier).

**Left on GL, and why** (both need a change outside these files; stage 3 or the owners):
1. `TerrainRenderer` links one GL program (`WorldGl.Program`) and deletes it at `Dispose`. Nothing draws with it: linking it is what runs
   `SkyRenderer.AssignSamplerUnits` (the atmosphere's units, without which `PublishUnits` and `BindUnits` do nothing) and `ShadowShaders.Bind`
   (the zero-filled default shadow blocks on their binding points, and the shadow globals' publication). The terrain renderer is built first and
   is the last world renderer that links a GL program. **Verified**: without it the viewer throws `IndexOutOfRangeException` in the first frame.
   Needed: the sky and the shadows publish their globals without a GL program (`SkyRenderer`, `ShadowShaders`), then this goes. *Gone in
   stage 2 (8.6): the sky publishes its textures natively and the shadows-off blocks; `TerrainRenderer` makes no GL call now. Observed there:
   at `5fcc3e1` the viewer no longer threw without the program (forest 13:00 ran through), but the picture was wrong (mean 16, max 95: the
   atmosphere's cubes and ambient map were the stand-in), so the exception's cause had moved since this note and was not looked for.*
2. `TerrainShadowMap` imports each target into a GL name (`IGlInterop.Import`) because `ShadowPass.Meitou` binds the finished map to a GL unit
   (`Texture`). An import starts with GL's default sampler state (`NEAREST_MIPMAP_LINEAR`, `REPEAT`, so mipmapped: the upscaler's bias would
   apply), so the name is given the linear, clamped state the GL texture had: `BindTexture`, `TexParameter` (four parameters), `BindTexture(0)`,
   and `DeleteTexture` at `Dispose`. Needed: either `Import` taking a sampler state (seam), or the consumer reading the new
   `TerrainShadowMap.Sampled` (the native texture with its sampler) instead of the GL name.
3. The constructors keep their `IGl` parameter (`WorldFrame`, `ShadowPass.Meitou` call them); `TerrainTextures.Create` has an `IGl`-free
   overload. `DrawMeshes` / `DrawMeshesIndirect` still take GL vertex-array names (the foliage's and the objects' meshes) and read them through
   `IGlInterop.VertexArray`, until those meshes are native.

**Learned.**
- **Verified** (the gate below): a native texture matches the GL one to the pixel when it has exactly the levels VkGl's view covered (one
  level for a non-mipmapped filter, `MAX_LEVEL + 1` for the overlay and colour windows, whose GL images had the whole chain allocated) and its
  sampler comes from `SamplerDesc.FromGl` with the GL parameters and the current LOD bias (`--upscaler taa`, a non-zero bias, 0 px).
- **Verified**: a GPU mip chain made with `CommandList.GenerateMips` gives the pictures of VkGl's `GenerateMipmap` (the ground and world colour
  maps, 0 px). A CPU box filter was not tried (**Unknown** whether it would match; the blit's filter is the driver's).
- **Verified**: no successor reads the GL state the terrain used to leave (cull face on or off, counter-clockwise, depth test on, no vertex
  array): with those calls gone the ten views in both modes and the extras are 0 px. The objects, foliage, grass, water, sky and shadow pass
  all set the culling they draw with before `CurrentState()`. A check during the port found the depth test on whenever the patches draw.
- **Observed**: the native outline (`--wireframe`, forest 13:00) is 0 px against the GL one of the base build.
- **Observed**: VkGl opened a frame for an upload made outside one (its `UploadCmd`); the native `Uploader` throws instead, so start-up uploads
  go through `UploadBatch`. The terrain's are made before the first frame in the viewer (`WorldFrame` builds the terrain first).
- **Observed**: VRAM by owner (`GpuAllocator.Breakdown`, forest 13:00 at exit): `terrain textures diffuse` 710 MB, `terrain textures normal`
  355, `terrain colour map` 85, `terrain shadow map` 68 (two targets), `terrain overlay map` 21, `terrain world colour` 21, `terrain heights` 14,
  `terrain shadow map heights` 9, `terrain ground colour` 5, `terrain blend map` 4, `terrain patches` vertices and indices, cells and biome
  parameters under 1: about 1.3 GB out of `gl texture` (1,005 MB and 179 textures left there). The single-level textures and the 7-level windows
  are smaller than their GL versions, which allocated every image's whole mip chain.

**Gate** (Release, RTX 4070): `dotnet build -c Release` 0 warnings; `dotnet test -c Release` 465 passed, 0 skipped; the ten views `--faithful all` and Meitou 0 px max against
`C:\Temp\base-6f4af19`; extras against the base viewer, 0 px: `--debug-shadows 1` forest 13:00, `--water-reflection 4` Port North 13:00,
`--upscaler taa` forest 13:00, `MEITOU_RECORD_THREADS=0` forest 13:00, `--wireframe` forest 13:00, and a low sun for the terrain shadow map
(forest 07:00, Hub 17:00, Meitou). `MEITOU_VK_VALIDATION=sync`: forest 13:00
and Port North 13:00 with `--water-reflection 4` 0 errors; a 150-frame forest fly under sync validation (six fine-height swaps, layer and map
streaming) 0 errors. `--fly-benchmark` 300, `--faithful all`, two interleaved runs each (noisy machine): the `terrain` stage 0.13-0.14 ms
before and after, `upd-terrain` 0.46-0.49 → 0.49-0.52 ms, render-thread allocation 14-20 → 17-18 MB: no change beyond the noise.

### 8.5 Phase 8 stage 1 (objects and foliage) as built

*2026-10-06, off master `6f4af19`.* `FoliageRenderer.cs` (55 `gl.` calls at `6f4af19`; the table's 147 is the older count, before step P), `FoliageRenderer.Grass.cs` (5), `BuildingLodMesh.cs` (36),
`WorldTextureCache.cs` (34) and `WorldObjectRenderer.cs` (12) make 0 IGl calls now:

- **Meshes** are native `DeviceBuffer`s (vertices, indices), uploaded through the `Uploader`, with their vertex attributes built directly
  (`ObjectMeshCache.VertexAttributes`, `FoliageRenderer.VertexAttributes`), no GL vertex array. Allocation names "object meshes" and
  "foliage meshes".
- **Textures** (`WorldTextureCache`) are native `Texture`s, GENERAL layout, full mip chain. Compressed files upload every level; RGBA files
  that need mips upload level 0 and blit the rest in the frame's pre-frame list (`WorldTextureCache.GenerateMipmaps`, linear blits as VkGl's
  `GenerateMipmap` did). Bindless entries carry the sampler state GL set (`SamplerDesc.FromGl`, anisotropy 8, the LOD bias in the sampler):
  each texture keeps two bias slots, so a bias change registers a new entry rather than rewriting a live one. BC4 views swizzle R,R,R,1.
  Caches are named "object textures", "foliage textures" and "impostor textures" (the impostors' own cache).
- **GL state** the renderers set by hand (cull face, alpha to coverage, polygon mode and depth bias for the wireframe, active texture, vertex
  array 0) is now the `DrawState` of each draw (`CurrentState() with { ... }`). Alpha to coverage follows the target's sample count.
- **Foliage GPU timers** are `QueryArena` timestamps recorded through `Interleave`, read without waiting (stale ones dropped after two
  rounds of frames).
- **Still-GL users** (at stage 1: the impostor baker and preview, which read `WorldTexture.Id`; `TerrainRenderer.DrawMeshes` takes GL vertex arrays
  for rocks and terrain-placed foliage) get GL names over the native resources through the additive `Gpu/GlBridge.cs` (seam `Import` /
  `ImportBuffer`, no raw handle sharing). That file is the only GL left on these paths. Stage 2 (8.6) ported the impostors and removed
  `GlBridge`'s texture part, `WorldTexture.Id` and the `WorldTextureCache(IGl, AssetLocator)` overload; the vertex arrays and
  `EnsureFrame` remain for `DrawMeshes`. `WorldObjectRenderer` keeps an unused `IGl` constructor parameter (`WorldFrame` is reserved);
  `FoliageRenderer` uses its one since stage 2 (8.6). Also added: `CommandList.BlitLevel`.

Facts:
- **Verified** (`WorldResourceTests`, sync validation): the native vertex attributes equal what VkGl exported for the GL vertex arrays
  (format, stride, offset, rate, buffer) for both mesh layouts, and `GlBridge`'s vertex arrays export the native buffers with the same
  attributes. The native blit mip chain of a 64x24 RGBA image is byte-equal at every level to VkGl's `GenerateMipmap`.
- **Verified** (gate): no later draw depended on the GL state these renderers used to reset (the sky reads the inherited cull; its
  triangle is unaffected): 0 px.
- **Observed** (forest 13, F11 breakdown): "gl texture" 2,327.9 MB (193 allocations) before, 1,584.8 MB (46) after, with "object textures"
  367.8 MB (51) and "foliage textures" 375.4 MB (96); "gl buffer" 82.6 MB (520) left the top owners, "object meshes" 47.9 MB (220) and
  "foliage meshes" 34.6 MB (294) took it. Host staging stays about 1.6 GB but moves from VkGl's frame ring (1,576 MB before, 736 after) to
  the native frame constants / staging chunks (24 MB before, 864 after).
- **Observed**: the foliage "gpu" statistic reads 1.15 ms against 1.97 ms before on the same view (same timestamp stage); frame times did
  not move by that much, so the old number likely included VkGl work between the stamps. **Unknown** beyond that.

Gate: Release build 0 warnings; quick and full tests (`KENSHI_PATH` set) pass, 0 skipped. Ten views Meitou and `--faithful all`: 0 px
max against the `6f4af19` baseline. Extras 0 px against the base viewer: `--debug-shadows 1` forest 13 (both modes), `--water-reflection 4`
Port North 13, `--upscaler taa` forest 13, `MEITOU_GPU_CULL=0`, `MEITOU_GPU_GRASS=0`, `MEITOU_RECORD_THREADS=0` and `=1` forest.
`MEITOU_VK_VALIDATION=sync` forest 13 and Port North 13 (`--water-reflection 4`): 0 errors. `MEITOU_GPU_CULL_VERIFY=1` forest: 142 views,
0 differences (grass 129 views, 0). Benchmark forest `--faithful all --fly-benchmark 300` (base / this): foliage 0.41-0.43 / 0.33 ms,
objects 0.10-0.11 / 0.09 ms, CPU-only p50 3.5-3.9 / 3.1 ms, render-thread allocations 18 / 17 MB: no regression.

### 8.6 Phase 8 stage 2 (sky and water) as built

*2026-10-06, off master `5fcc3e1`. In short: the sky and the water make no GL call. Their textures, the water's quad and the sky's timer
are native, and their draws take a `DrawState` instead of changing GL state. The sky publishes the atmosphere's textures and the shadows-off
blocks itself, so the terrain's unused GL program is gone. The water samples the reflection natively, so `ReflectionPass` loses its 5 calls
for that handoff. 0 px everywhere.*

**Calls** (`gl.` / `Gl.`, comments excluded, at `5fcc3e1` → now): `SkyRenderer.cs` 57 → 0, `WaterRenderer.cs` 27 → 0, `ReflectionPass.cs`
27 → 22, `TerrainRenderer.cs` 1 → 0. `AtmosphereShaders.cs` 0 → 9: `AssignSamplerUnits` moved there (below).

**What moved.**
- *Sky textures* (`SampledImage`, in `SkyRenderer.cs`): a native `Texture` with the sampler its GL texture had (`SamplerDesc.FromGl`, the
  upscaler's LOD bias on mipmapped filters, as VkGl's `SamplerFor`). Uploads go through `Uploader.Begin()`.
  - The starfield, moon and clouds are RGBA8 with the full chain from `CommandList.GenerateMips` (`WorldGl.Texture2D`'s `GenerateMipmap`).
  - The irradiance and specular cubes are RGBA8 cubes with exactly the file's levels (the GL `MAX_LEVEL`), clamped on S, T and R. Vulkan's
    cube sampling is always seamless, as GL's was with `TEXTURE_CUBE_MAP_SEAMLESS`.
  - The ambient map is one level, linear.
  - Names: "sky stars", "sky moon", "sky clouds", "sky irradiance", "sky specularity", "sky ambient map".
- *Atmosphere globals*: `uAtmoIrradiance`, `uAtmoSpecular` and `uAtmoAmbientMap` are published as these textures once the sky has a state.
  Before, they were the GL units `BindUnits` bound. That is why the sky needed a linked GL program (`AssignSamplerUnits` set the units).
  `BindUnits` is empty now; it stays for its callers in the terrain, objects and foliage files. `Apply(program)` had no caller and is gone.
- *Shadows-off blocks* (`ShadowsOffGlobals`, called by the sky's constructor): zero-filled `FrameBlock`s for the receiver, caster and
  Meitou blocks, published only where nothing is yet. This is what `ShadowShaders.Bind` put on the binding points through the terrain's GL
  program. A `ShadowPass` publishes its own over them whenever it is made. With `--no-shadows` a `LegacyProgram` would otherwise throw on
  the unbound block (8.3, Learned). The shadow textures stay unpublished, so readers get the stand-in, as an empty GL unit gave.
- *`TerrainRenderer`'s `globalsProgram`* is removed (its three lines). The terrain file has no GL call left.
- *`AssignSamplerUnits`* (GL only) moved to `AtmosphereShaders`. `SkyRenderer.AssignSamplerUnits` forwards to it, because `WorldGl.Program`
  (reserved) and the model viewer's `Renderer` call it. Only GL programs need it: the impostor baker and preview (the impostor tool,
  which has no sky) and the model viewer's own. It no longer publishes unit-based globals. *Stage 3: the model viewer is dropped (DECISIONS 23, replacing owner decision 7), the impostor tools are native (8.8), and both `AssignSamplerUnits` are deleted (8.9).*
- *Sky timer*: `PassTimer` replaces the GL timestamp queries. `Draw` polls each time, because a pair is readable only until its frame's
  query pool comes round again. `Benchmark` (`MEITOU_SKY_BENCH`) now reports recording CPU time: the native API has no `Finish` inside a frame.
- *Water*: the five maps are `SampledImage`s:
  - "water colour map" and "water flow map": one level, clamped;
  - "water normal map": mipmapped, repeating. A missing map's 1 × 1 stand-in keeps its trilinear filter;
  - "water parameters a" and "water parameters b": RGBA32F, linear, clamped.

  The quad is a `DeviceBuffer` ("water quad"). Its single vertex input is built directly (`WaterRenderer.QuadAttribute`), and the pipeline
  cache keys on a `VertexArrayBindings` the water owns. `ReflectionPass.Sampled` gives the reflection colour with the sampler its GL name
  had (linear, clamped, no mips). The 5 calls (`BindTexture` + 4 `TexParameter`) are gone. The GL name stays only as the guests'
  framebuffer attachment.
- *GL state*: the sky takes `CurrentState() with { DepthTest = false, DepthWrite = false }`. The water takes `with { Cull = None,
  DepthTest = <has depth>, DepthWrite = false, Blend = src alpha / one minus src alpha }` (blend and depth only with their attachment, as
  VkGl's `CurrentState`).
- Constructors keep their `IGl` parameter (`WorldFrame`, reserved, passes it); it is unused. `WaterRenderer.Create`'s `SkyRenderer` is unused too.

**Facts.**
- **Observed** (a temporary probe of VkGl's GL state at the entry and exit of every sky and water draw: Port North 13:00 in both modes,
  02:00 with `--water-reflection 0`, forest `--no-shadows`):
  - At the sky's entry the depth mask is always on. The depth test is off only in the first frame, and the caller turns it on right after
    the sky in every case (`WorldFrame`, `ReflectionPass`). So the sky's restore (`DepthMask(true)`, `Enable(DepthTest)`,
    `ActiveTexture(0)`) changed nothing a successor read.
  - At the water's entry: depth test on, mask on, cull face off, blend off, active unit 0. Its exit differs only in the blend factors it set,
    and nothing reads those (`DebugOverlay`, the only GL code that enables blending, sets its own; VkGl reports `(One, Zero)` with blending off).
- **Verified** (`SkyWaterNativeTests`, `[Slow]`, sync validation): for a mipmapped clamped, a mipmapped repeating, a one-level and a 1 × 1 RGBA8
  texture and an RGBA32F one, `SampledImage.Sampled()` gives the same Vulkan sampler object as VkGl's `Sampled` of the GL texture it replaces
  (one `SamplerCache`), at LOD bias 0 and 0.75. Same for the format. The water quad's attribute equals VkGl's export of the old GL vertex array
  (format, stride, rate, offset). The sky publishes the atmosphere's names and non-null shadows-off blocks with no GL program, and a later
  `ShadowPass` replaces them.
- **Observed**: at `5fcc3e1`, without the terrain's GL program the viewer did not throw (8.4 #1 said it did), but drew the atmosphere with
  stand-ins (forest 13:00 mean 16, max 95). With the native globals: 0 px.

**Left, and why** (outside these files):
1. `SkyRenderer.AssignSamplerUnits` (a forwarder) and `AtmosphereShaders.AssignSamplerUnits` (9 GL calls) stay while `WorldGl.Program` and the
   model viewer's `Renderer` link GL programs. They go with those programs (stage 3). *Done: deleted in stage 3 step 5c (8.9); the model viewer went in step 1 (DECISIONS 23).*
2. `SkyRenderer.BindUnits()` is an empty method still called by `TerrainRenderer` (3×), `WorldObjectRenderer` and `FoliageRenderer`. The owners
   can drop the calls.
3. The `IGl` constructor parameters of `SkyRenderer` and `WaterRenderer.Create` (unused) go when `WorldFrame` stops passing them.
4. Both draws still open their segment through the seam (`BeginNativeInPass`, `CurrentTargets`, `CurrentState`). The host-pass seam work of
   8.3 (seam needs, 1) covers them.

**Gate** (Release, RTX 4070, against the shared baseline of `6f4af19` and renders of its viewer):
- Build: `dotnet build -c Release`, 0 warnings. `dotnet test -c Release` (`KENSHI_PATH` set): 470 passed, 0 skipped.
- Pixels, all max 0 (mean 0):
  - the ten views `--faithful all` and the ten views in Meitou mode, against the baseline;
  - against the base viewer: Port North `--water-reflection 4` at 13:00 and 02:00 in both modes; `--water-reflection 0` (Meitou) and `1`
    (Faithful) at 13:00; `--debug-shadows 1` forest 13:00 in both modes; `--upscaler taa` forest and Port North 13:00 (a non-zero LOD bias
    on the mipmapped sky and water maps); `--no-shadows` forest 13:00 in both modes; forest at 06:30 and 19:00 in both modes; Port North
    19:00; `--simple-sky` forest 13:00 and Port North 19:00; `MEITOU_RECORD_THREADS=0` forest and Port North 13:00.
- `MEITOU_VK_VALIDATION=sync`, 0 errors on each of: forest 13:00; Port North 13:00 `--water-reflection 4` in both modes; forest `--no-shadows`.
- `--fly-benchmark 150`, Port North 13:00 `--faithful all`, two interleaved runs each (base / this; noisy machine; 150 frames, not the 300
  for reported numbers): `sky-draw` 0.09-0.11 / 0.11-0.12 ms, `water` 0.24-0.25 / 0.23-0.24 ms, CPU-only p50 2.6-2.8 / 2.4-2.7 ms. No change
  beyond the noise. The sky's GPU time in the screenshot log reads as before (Port North 13:00: 0.04 ms).

### 8.7 Phase 8 stage 2 (post-processing) as built

*2026-10-06, off master `5fcc3e1`. In short: every target, texture, pass and timer of `PostProcess` is native; it makes 0 `IGl` calls (88
before) and holds no `IGl`. What is left of GL is the mirror that still-GL code reads: GL names and framebuffers over the native targets, their
binding and four pieces of fixed-function state. That moved into additive `GlBridge` helpers (39 GL calls there, 21 before). 0 px everywhere
the upscaler is deterministic.*

**Native now** (`PostProcess.cs`):
- *Targets* are `Texture`s made through `Uploader.Begin()` (`UploadBatch`), named for the VRAM pie: "post scene colour" (RGBA16F), "post scene
  depth" and "post far slice depth" (`DEPTH_COMPONENT24` → D32 float, `GlConventions.VkFormat`), "post motion" (RGBA16F), "post upscale
  depth" and "post reactive" (R32F), "post taa history" (two, RGBA16F), "post ldr" and "post ldr fxaa" (RGBA8), "post ssao" (two, RG16F at
  half size), "post luminance" (R32F 256², all 9 levels), "post exposure adapted" (two, RG32F 1 × 1). Usage as VkGl gave its images:
  sampled, transfer source and destination, the attachment, and storage for the float colour formats where the device has it (the vendor
  upscalers write their output with compute). One level each except the luminance; VkGl allocated every GL texture's whole mip chain.
- *Sampling*: each read is a `SampledTexture` from `SamplerDesc.FromGl` with the GL filters the target had (linear, or nearest for the depths,
  motion, upscale depth and reactive; `LINEAR_MIPMAP_NEAREST` for the luminance), clamped, anisotropy 1, and the LOD bias as it is at that
  moment (`GpuContext.LodBias`). Nothing bound is `GpuContext.Dummy`.
- *Heat-haze maps* ("post heat haze flow", "post heat haze perturbation"): BC1 with the file's levels, uploaded in one batch each at load,
  sampled trilinear, anisotropy 16, repeating (the NVIDIA -0.25 comes from `SamplerCache` as before).
- *Passes*: each full-screen draw is a rendering of its own (`LOAD`, the whole target, the viewport VkGl derived) in a native segment
  (`BeginNative`), with a fixed `DrawState` (no cull, depth test, write, bias or blending; RGBA; fill) and a full barrier after it, as VkGl
  barriered before every pass. A segment is closed before the vendor upscaler (it opens its own) and before the grass-motion guest. The luminance's mips are
  `CommandList.GenerateMips`. The far slice's depth clear is a rendering of its own with a `CLEAR` load op. The final passes draw into the
  attachment `CurrentTargets()` reports for `Target` (the window's backbuffer or the caller's framebuffer).
- *GPU timing*: native timestamps (the frame's `QueryArena`) in the segment, or through `Interleave` outside one ("start", "scene"). A frame's
  set is read when its slot of the frame ring comes round. `Flush()` keeps its signature but no longer waits, so the last frames in flight
  are counted later (as `ShadowPass.Poll` since 8.3).
- `ReadExposure` reads the adapted texel with `GpuContext.ReadBack`.

**Left on GL, and why** (all in `GlBridge`, all for other code):
1. *The scene's framebuffer and the far slice's*: GL framebuffers over imported names of "post scene colour" and the two depths
   (`GlBridge.Framebuffer`), bound in `Begin`, `BeginFarSlice` and `BeginNearSlice` (`GlBridge.Bind`: framebuffer and viewport). The scene's
   guests and `SceneHost` read `CurrentTargets()`. `WorldFrame` clears with `gl.Clear`. `ReflectionPass.RestoreFramebuffer`,
   `ShadowPass.Render` and `ShadowPass.CaptureDepth` take `SceneFramebuffer` as a GL name.
2. *The grass-motion guest* (`FoliageRenderer.DrawGrassMotion`) draws into "the bound framebuffer" with `CurrentState()`. So the motion
   target keeps a GL framebuffer, and around the call `GlBridge.State` sets what the GL code set: depth test, write, culling and blending off,
   colour mask red and green (then all channels). It gets the near depth as a GL name (`MotionTargets.NearDepth`) that it registers through
   `IGlInterop.Bindless`, so the name carries the nearest, clamped sampler state the GL texture had (`GlBridge.Texture` with filter and
   wrap, an additive overload). An import starts with GL's defaults.
3. *The vendor upscalers* take their textures by GL name (`UpscaleInputs`, `VkGl.ImageOf`): scene colour, upscale depth, motion, reactive and
   the history output are imported.
4. *`Target`* is a GL framebuffer name (the viewer's and the game's offscreen one, 0 for the window). It is bound at the start of `End` to
   read its attachment and again at the end. `DebugOverlay` draws into the bound framebuffer afterwards, and at the end `GlBridge.State`
   leaves depth test and write on and culling and blending off, as `End` did.
5. The LOD bias is set through the seam (`Interop as ITextureLodBias`): `GpuContext.LodBias` is a getter VkGl supplies, with no setter.
   The constructor keeps its `IGl` parameter, unused, because `WorldFrame` is reserved.

**Seam needs (stage 3):**
- (a) The scene's host and guests take native targets and a `DrawState` from the host instead of the GL binding: `SceneHost`, `WorldFrame`'s
  clears, the reflection's and the shadows' restore, and `CaptureDepth`. Items 1 and 2 then go.
- (b) `Target` as a native `RenderTarget` or `Texture` from the presenter and the viewer (item 4).
- (c) `UpscaleInputs` with `Texture`s, so the reserved upscalers no longer need `ImageOf` (item 3).
- (d) `MotionTargets.NearDepth` as a `SampledTexture` (the foliage file is a stage-1 file).
- (e) A settable `GpuContext.LodBias`, replacing `ITextureLodBias` (item 5).

**Learned:**
- **Verified** (gate below): the native targets with one level match VkGl's full-chain GL textures to the pixel. VkGl's view covered only
  the defined level, and the sampler is the same `FromGl` result.
- **Verified**: the luminance sampler takes the upscaler's LOD bias (`LINEAR_MIPMAP_NEAREST` is a mipmapped filter, and `textureLod` in the
  adapt pass adds the sampler's `mipLodBias`). With the bias from `GpuContext.LodBias` at bind time, `--upscaler taa` (bias -0.5) is 0 px.
- **Verified**: the post passes need no GL state from before `End`. The fixed `DrawState` gives 0 px in every option set below, the haze
  included.
- **Verified** (`PostProcessNativeTests`, sync validation, with and without TAA): the named allocations exist, a resize from 64 × 36 to
  80 × 48 in one process works, a grey scene of luminance 0.5 reads back as mean and adapted 0.5, and the target receives 0.5 × 0.55 / 0.5.
- **Observed**: FSR (FidelityFX 1.1.4) is not deterministic between two runs of the same build. The base viewer against itself differs by
  mean 0.008 and max 17. New against base is mean 0.007 with max 25-31, over12 0% in all cases, so it is structurally the same picture.
  DLSS is deterministic: base against base 0 px, new against base 0 px.
- **Observed** (computed from the formats, not measured on the F12 pie): at 1600 × 900 with TAA, the post targets come to about 80 MB, which
  was in `gl texture` before. The haze maps are 2 × 2.8 MB. Before, every GL texture also had its whole mip chain allocated (about +33%).
- **Unknown**: whether code after `End` still needs the culling and blending off that `End` leaves. That state is kept as it was, untested
  without it.
- **Unknown** (not run): the window path, `Target = 0`. Every screenshot, fly benchmark and test draws into an offscreen GL framebuffer; only
  the interactive viewer and the game draw into the window, and neither exits by itself. From the code, framebuffer 0's attachment is VkGl's own
  backbuffer texture (GENERAL layout, blitted to the swapchain by the presenter after the frame). That is the same kind of attachment as an
  offscreen framebuffer's, and `ShadowPass.DrawDebug` already draws natively into it the same way.

**Gate** (Release, RTX 4070, against `C:\Temp\base-6f4af19`):
- Build 0 warnings. `dotnet test -c Release` (`KENSHI_PATH` set): 470 passed, 0 skipped. New: `PostProcessNativeTests` (`[Slow]`, two cases).
- Ten views, `--faithful all` and Meitou mode (which runs TAA at scale 1): 0 px max.
- Against the base viewer, 0 px:
  - `--heat-haze 1` forest 13:00 in both modes, and `--heat-haze 0.6` Hub (no parity view has heat haze in its weather);
  - `--upscaler taa --faithful all` forest; `--upscaler taa --render-scale 0.67 --water-reflection 4` Port North; `--render-scale 0.5` at
    1001 × 613 (odd sizes) Rock;
  - `--size 1280x720` forest and `--size 1920x1080` Hub Faithful (the targets at other sizes);
  - `--debug-shadows 1` and `2` forest (`CaptureDepth` through the scene's GL framebuffer); `--water-reflection 4` Port North;
    `--post-debug ao`;
  - `MEITOU_RECORD_THREADS=0` forest in Meitou mode, and Faithful with heat haze;
  - DLSS (`MEITOU_STREAMLINE_PATH`) forest 13:00 at scale 1 and Port North at 0.67.
- FSR: structurally the same (see Learned).
- `MEITOU_VK_VALIDATION=sync`, 0 errors on each of: forest 13:00 Meitou, Faithful, Faithful + TAA, TAA at 0.5; Port North
  `--water-reflection 4` in both modes; heat haze in both modes; DLSS; FSR.
- Benchmark: forest, Meitou mode (TAA), `--fly-benchmark 300`, base and this build interleaved twice. The `post` stage went from 0.21-0.23
  to 0.25-0.26 ms (+0.03). Every other stage, CPU-only p50 (base 2.7-3.1, this 3.3-3.5 ms, but the stages do not add up to that difference)
  and render-thread allocations (20-21 MB) are within the noise of this machine. The extra likely comes from the GL mirror (the `Target` bind and
  `CurrentTargets` at the start of `End`, the state calls) and a segment of its own for the far slice's clear. **Unknown**: not profiled further.


### 8.8 Phase 8 stage 2 (impostor baker, textures, preview) as built

*2026-10-07, commit `b91980d` of the billboard work, merged on its own.* `Impostors/ImpostorBaker.cs` (96 `gl.` calls at `5fcc3e1`),
`ImpostorPreview.cs` (84), `ImpostorTextures.cs` (15) and `tools/Meitou.ModelViewer/ImpostorApp.cs` (31) make 0 IGl calls now: the baker
renders its frames natively and the atlases are native textures ("impostor atlas ..."). **Verified** by the agent: the baked atlases are
byte-identical to the GL baker's. The GL texture names the baker read from `WorldTextureCache` through `GlBridge` are gone. Gate on the merge:
the ten views in both modes 0 px against `C:\Temp\base-6f4af19`, tests 472 passed, 0 skipped. The drawing of impostors in the foliage path
was first kept on the branch `billboards-wip` (1.1-2.4 GB of VRAM for little frame time); it was finished on `billboards-native`, 8.10.

### 8.10 Billboards finished on the native API (branch `billboards-native`, 2026-10-07)

*In short: the far-foliage impostors are drawn in Meitou mode (the default), at a fraction of the first version's VRAM, and the Meitou
foliage ranges and shadow distance are longer. Faithful is unchanged to the pixel. Details and numbers: [impostors.md](impostors.md).*

- **Port.** The `billboards-wip` commits were cherry-picked onto master 410579b: `IGl.Finish` and `GlBridge.EnsureFrame` became
  `Gpu.Finish()` / `Gpu.EnsureFrame()`, the cull test uses `GpuContext`, the benchmark's VRAM lines use the device. `billboards-wip` is untouched.
- **Format 2 (baker 5).** BC1 punch-through albedo + BC5 normal, no depth map; frame size 64-256 by the on-screen size at 4000 units;
  12 × 12 frames; the bake samples textures with a LOD bias that matches the mesh at that distance (fixes the fuller, pinker crowns);
  frame-pick noise replaced the Bayer matrix. **Observed**: impostor VRAM 495 MB (41 atlases) to 119 MB (49 atlases) in the forest still view,
  200 MB in a flight; all 267 base-game atlases would be 1637 MB resident (5669 MB before), bake-all 25.8 s (407 s), disk 376 MB.
- **Budget.** 192 MB (`--impostor-budget`), LRU eviction of atlases unused for 1 s, a refused atlas retries after 8 s and its mesh keeps drawing.
- **Meitou defaults.** Foliage ranges large 12000, medium 5000, small 800 (were 5000 / 2500 / 800; the size thresholds 40 / 125 keep their
  old range constants, `FoliageSizes.Threshold*Range`); impostor distance 4000; shadow range 10000 (the game's 5000 in Faithful; the CLI
  accepts 1000-15000 in Meitou, 1000-9000 in Faithful). Why (**Observed**, RTX 4070, forest, `--fly-benchmark 300`): with billboards
  the cascades cost 1.65 ms at shadow 10000 against 3.05 ms with the meshes, and 1.98 ms against 3.58 at 15000, so 10000 with
  billboards costs about what the game's 5000 costs with meshes (1.35 ms measured on master); 15000 is the owner's hard limit and the top of the
  Meitou option. 12000 / 5000 were chosen because in a flight they use about the whole 192 MB budget (47 atlases, 200 MB at the end);
  longer ranges would only evict more (**Unknown**: not measured beyond 12000, by the owner's rule).
- **Gate.** `--faithful all`, ten views: max 0 against `C:\Temp\base-6f4af19\faithful` (**Verified**). Meitou with `--faithful impostors
  --range-large 5000 --range-medium 2500 --shadow-range 5000`: 0 px against the Meitou baseline (**Verified**); with the new defaults the
  views differ by design (impostors.md section 7). Tests 463 passed, 0 failed, 0 skipped. Sync validation: 0 errors in the forest views;
  the shutdown message about four leaked `VkImageView`s also appears on master (not from this work).

### 8.12 Billboard fringes, first-flight hitch and cache cap (2026-10-07)

*In short: the dotted silhouette of tree-trunk bulbs is gone (the cut-out is now a vote of the three frames' coverage), a cold-cache fast
flight no longer stalls on baking (allocation churn removed, bakes paced and ordered), and the atlas disk cache has a cap with LRU
eviction and stale-version cleanup. No atlas bytes changed (`BakerVersion` 5), VRAM is the same. Details and numbers: [impostors.md](impostors.md)
sections 4, 5, 6 and 8.*

- **Fringes.** Cause: the per-pixel frame pick cut on the picked frame's own coverage, and frames 8-15 degrees apart put a far-off-centre bulb's
  edge pixels apart (not BC1 alpha, not mips, not the fill). Fix in `ImpostorShaders.impostorSample`: cut-out = weighted vote, colour = a covering
  frame, normal and position blended; casters keep the plain pick (the vote made them cast crown-sized slabs).
- **Hitch.** The render thread allocated 1.7-2.0 GB (8-9 GB in all) in a cold flight: readback copies, 100 MB of atlas levels a bake, 900 MB of
  filter arrays. Now 20 MB (2.2-2.7 GB), gen2 GCs 7-8 to 1-2, GC pauses 35-146 to 4-9 ms; cold p99 / max (flight at 600 units a frame, four
  interleaved runs) base 18.6-29.4 / 20.6-35.3 ms against 15.9-20.4 / 19.7-37.0 ms now, p95 15.1-17.3 against 12.5-14.9 (impostors.md section 8
  has every run; the shared machine's noise is as large as the difference in max).
- **Cache.** `ImpostorCache.Maintain`: 4096 MB default cap (512 until 2026-10-10) (`--impostor-cache-mb`), LRU by last write time, older format / baker versions
  and junk and abandoned temporaries removed.
- **Gate** (Release, RTX 4070, against `C:\Temp\base-87c7857`): `--faithful all` ten views max 0 (**Verified**); `--faithful impostors` ten
  views 0 px against the base viewer rendered with the same option (**Verified**); Meitou default views differ only where billboards are
  drawn (impostors.md section 6); `MEITOU_VK_VALIDATION=sync`, forest and Hub 13:00, 0 errors with a warm cache (6 with a cold cache, fixed in 8.13,
  as in the base build).

### 8.13 Billboards: cold-cache validation and drawing warm-up (2026-10-07)

*In short: the 6 sync-validation errors of a cold impostor cache are gone and the impostor programs and quad are made at load. No atlas byte
and no pixel changed. Details: [impostors.md](impostors.md) section 9.*

- **Errors.** Cause (**Verified**): the first read of the frame globals happened inside the bake's rendering, where the shadow noise's lazy upload
  (`ShadowPass.UploadNoise`) recorded a copy and a transfer barrier. Fix: `ImpostorBaker.PrepareFrameGlobals` prepares once before the first row.
  Forest and Hub with an empty cache, `MEITOU_VK_VALIDATION=sync`: 0 errors (was 6); the ten Meitou views cold: 0.
- **Warm-up.** `ImpostorDraw` (programs, quad) in the first foliage update; the pipeline of each pass's state and formats in the first pass
  without impostors (`WarmImpostorPipeline`). First impostor setup 7.5 + 0.65 ms before; programs and quad gone from it, and no setup of 0.2 ms
  or more when a pass without impostors came first (**Observed**). A run whose first drawn frame has impostors still compiles the pipelines there
  (4 to 5 ms; formats are known only in a pass).
- **Gate.** Atlas md5 of a cold forest bake identical (55 files); `--faithful all` ten views max 0 against `C:\Temp\base-87c7857\faithful`; Meitou
  ten views, cold cache, max 0 against master cd65d05's.

---

### 8.9 Phase 8 stage 3 (no GL-shaped layer) as built

*In short: `IGl`, `VkGl`, the seam, `GlBridge`, `WorldGl` and the project `Meitou.Rendering.Vulkan` are gone. `GpuContext` owns the
frame, the presenter and the upscalers live in `Meitou.Rendering`, and every picture is unchanged to the pixel. 2026-10-07, on top of
`668e7a6` (the stage 2 merges); claims below are labelled as in the rest of the docs.*

**Commits, in order** (each built with 0 warnings and passed the full tests with 0 skipped before it was committed):

| Step | Commit | What |
| --- | --- | --- |
| 1 | `9a54870` | The mesh and character viewer dropped (DECISIONS 23); [character-viewer.md](character-viewer.md) keeps how it worked, tag `model-viewer-last`. |
| 2 | `f33e06f` | Hosts hand their guests `PassTargets` and a `DrawState` (`BeginHostPass`); the GL state mirror goes; native meshes (`MeshBindings`), the terrain shadow map's `Sampled`, the LOD bias on `GpuContext`, native upscaler inputs. `ITextureLodBias` deleted. |
| 4a | `006071e` | `GpuContext` owns the frame loop (`Gpu/FrameLoop.cs`) and the segments (`Gpu/Segments.cs`); the GPU frame time from native timestamps. |
| 4b | `a9d5a27` | Scene clears as load operations; reflection, shadows, the post target, the overlay (its atlas through the `Uploader`) and the capture native; `GlBridge` deleted. |
| 4c | `9b3ba7e` | The window path native: `Gpu/VulkanPresenter.cs` with its own RGBA8 backbuffer on `GpuContext` frames. |
| 5a | `da4eb16` | The renderers, `WorldFrame`, the viewer and the game off `IGl` (constructor parameters, empty `BindUnits`, `Interop` guards, VkGl's stand-ins). |
| 5b | `aa1fb2d` | FSR, DLSS and Streamline moved to `src/Meitou.Rendering/Upscalers` (namespace `Meitou.Rendering.Upscalers`), on `GpuContext`. |
| 5c | `2e71aea` | Tests on `GpuContext` alone: `SeamTests` (12) and `VkGlTests` (4) deleted, `NativeHostTests` (4) added; `WorldGl`, both `AssignSamplerUnits`, `ShadowShaders.Bind` / `PublishGlobals`, `MeitouShadowShaders.Bind` / `PublishGlobals` deleted. |
| 5d | `e771bba` | `src/Meitou.Rendering.Vulkan` deleted (`VkGl`, `VkGlStats`); `IGl.cs`, `IGlInterop.cs`, `GlEnums.cs` deleted; `VulkanDisplay` makes the `GpuContext`; the viewer's `PassMeter` native. |
| 6 | `c71d21c` | Namespaces `Meitou.Rendering.Vulkan.Core` / `.Shaders` renamed to `Meitou.Rendering.Gpu.Core` / `.Shaders`. |
| - | `faae05b`, `cd0402c` | `--fly-benchmark` prints every allocator owner (`vram` line, with device-local MB). |

(No commit is labelled step 3: that work was folded into step 2.)

**Deleted** (lines at `668e7a6`): `src/Meitou.Rendering.Vulkan` (`VkGl.cs` and its ten partial files, `VkGlStats.cs`, the project and
its `AssemblyInfo.cs`: 3,279 lines), `Gpu/IGl.cs` (127), `Gpu/IGlInterop.cs` (146), `Gpu/GlEnums.cs` (343), `Gpu/GlBridge.cs` (140),
`Gpu/ITextureLodBias.cs` (11), `WorldGl.cs` (54), `tests/.../SeamTests.cs` (756), `tests/.../VkGlTests.cs` (317), and the model
viewer's `Renderer.cs`, `CharacterRenderer.cs`, `CharacterApp.cs`, `CharacterScene.cs`, `Animator.cs`, `Camera.cs` (1,961). In all, the
stage changed 121 files under `src`, `tools` and `tests`: 1,852 lines added, 9,024 removed. One project fewer: `MeitouClient.slnx` and the
game, display, viewer and test projects no longer reference `Meitou.Rendering.Vulkan`.

**How the frame runs now** (from the code):
- `VulkanDisplay` creates the device, then `new GpuContext(Device)`, then (with a window) `new VulkanPresenter(Context, vsync)`.
  Disposal runs the other way: presenter, context, `Device.Frames.WaitAll()` (what was released after the frames in flight), Streamline,
  device.
- `GpuContext.BeginFrame` / `EndFrame` / `Finish` (`Gpu/FrameLoop.cs`): the frame's command buffer and an upload command buffer submitted
  ahead of it, each with a full barrier at its start and end; a timestamp at the start and end of the frame gives `GpuFrameMs` a frame
  ring later. `EnsureFrame` opens a frame where something records with none open (loading, offscreen tools).
- Segments (`Gpu/Segments.cs`): `BeginNative` / `EndNative` hand out the frame's list between full barriers. `BeginGuest` draws into the
  open host pass (its list, or with secondaries an inline secondary of its own); **without a host pass it throws** (the path that fell
  back to VkGl's pass is gone). `Interleave` records into the host's rendering when one is open, else into the frame's list.
- Host passes (`Gpu/Passes.cs`): `BeginHostPass(cmd, targets, state)`, `SetPassViewport`, `SetPassState`, `EndHostPass`;
  `CurrentTargets` / `CurrentState` throw when no host pass is open (no fallback any more).
- The window's backbuffer is a native `Texture` the presenter owns; `display.Backbuffer` is what post-processing writes and
  `FramebufferCapture` reads.
- `GpuStats` keeps running totals of its eight counters (`Running`) for meters; the viewer's `PassMeter` (`MEITOU_PASS_STATS=1`) shows
  those plus fence-wait, submit, acquire and present ticks. Its stamps take their query index inside `Interleave`: the benchmark starts
  its clock before the frame is open, and an index taken earlier belonged to the previous frame's pool (the GPU column read NaN until
  this was fixed in 5d). `MEITOU_VKGL_PHASES` is gone with VkGl; `MEITOU_VK_MICRO` stays (`VkCallMicro`, a native segment).

**What is left, and why:**
1. **`LegacyProgram` and the legacy shader model** (`ShaderLibrary.Legacy`, the default block moved to set 1, the stage binding shift)
   stay: referenced from about 20 files and built in about 15 (terrain, objects, foliage and grass, sky, water, shadows, post, overlay, impostors).
   8.2 deletes it only once unused, and it is not. Its output is pinned by `GpuApiTests`: the SPIR-V of 27 programs against SHA-256
   hashes recorded from the build that last matched VkGl byte for byte (`aa1fb2d`; the source diff after it only deletes code).
2. **The GL vocabulary**: ten enumerations (`BlendingFactor`, `DepthFunction`, `FrontFaceDirection`, `GLEnum`, `InternalFormat`,
   `PrimitiveType`, `TextureMagFilter`, `TextureMinFilter`, `TextureTarget`, `TextureWrapMode`) moved from `GlEnums.cs` to the end of
   `GlConventions.cs`, with `SamplerDesc.FromGl`. The renderers still describe formats, filters and vertex types in these words, and
   `GlConventions` is what turns them into the Vulkan values that decide pixels. Replacing them with Vulkan's own types is a rewrite of
   every resource description for no pixel or time gain, so it was not done here.
3. **`VertexArrayBindings`** (the vertex input a GL vertex array described) moved from `IGlInterop.cs` to `LegacyProgram.cs`: the sky's
   and the water's pipeline caches key on it.
4. **History in comments**: about 110 remarks such as "as VkGl did" stay in `src/Meitou.Rendering`; they explain where a rule came from.
   Comments that described the current code wrongly (the seam, `IGlInterop` members, VkGl driving the frame) were rewritten.
5. **Sections 0 and 4** of this note describe the pre-port state and the seam; they are marked historical.

**Gate** (Release, RTX 4070, against `C:\Temp\base-6f4af19` and renders of its viewer; FSR is compared with a tolerance (owner decision 13) because the base
viewer against itself differs by up to 27, mean 0.008, no pixel over 12):
- **Verified** at every step: `dotnet build -c Release` 0 warnings; `dotnet test -c Release` with `KENSHI_PATH` set, 0 skipped (472 tests
  through 5b; 459 from 5c: 17 seam / VkGl / GL-export tests deleted, 4 native host tests added); the ten views in both modes max 0
  (mean 0) against the base renders.
- **Verified** at 5d and again at the end (`cd0402c`): 21 extras against the base viewer, all max 0 except FSR (max 25 at both, within
  the base's own 27). Forest 13:00 unless named: `--debug-shadows 1` in both modes, `2` (Meitou), `3` (Faithful); `--upscaler dlss`,
  `taa`, `fsr`; `MEITOU_GPU_CULL=0`; `MEITOU_GPU_GRASS=0`; `--heat-haze 1`; `--no-shadows` in both modes; `--wireframe`; 1280 x 720;
  `--show-keys`; `MEITOU_RECORD_THREADS=0` and `=1`, and `=0` on Port North `--water-reflection 4 --faithful all`; Port North
  `--water-reflection 4` in both modes; the rock view at 1001 x 613 with TAA at render scale 0.5.
- **Verified**: `MEITOU_VK_VALIDATION=sync`, 0 errors on forest and Port North in both modes and on DLSS (5d, 6, end); the interactive
  viewer 20 s with sync validation, plain and DLSS, 0 errors, and with DLSS while its window is resized twice (984 x 611, 1317 x 732:
  the swapchain and backbuffer rebuilt) 0 errors; the game (`src/Meitou.Game`) 20 s with sync validation, 0 errors, and its
  offscreen screenshot as expected; the impostor preview of `BushTree01`, 12 pictures, max 0 against the base viewer.

**Benchmark** (**Observed**, 2026-10-07, RTX 4070, 1600 x 900, `--time 13`, `--fly-benchmark 300`, `MEITOU_PASS_STATS=1
MEITOU_PASS_STATS_SKIP=80`, base viewer and this build interleaved; medians of three runs, six for the still forest; ms):

| scenario | CPU-only p50 | frame p50 | frame p95 | render-thread CPU p50 | GPU frame |
| --- | --- | --- | --- | --- | --- |
| forest, still camera | 1.0 / 0.9 | 8.4 / 8.2 | 10.75 / 10.7 | 1.00 / 0.96 | 7.16 / 7.00 |
| forest, flying (150 units a frame) | 2.0 / 1.9 | 8.0 / 7.7 | 12.3 / 12.2 | 2.01 / 1.94 | 6.12 / 5.95 |
| The Hub, still camera | 1.0 / 0.9 | 5.5 / 5.3 | 10.3 / 9.7 | 1.02 / 0.98 | 5.48 / 4.97 |

(base / this.) Per stage the render-thread CPU moved by at most 0.01-0.03 ms (shadows, terrain, objects, reflection 0.01 lower; the
rest equal), and the GPU stages within the noise. The still forest is **bimodal on both builds**: of six runs each, two base runs and one
of this build's (a seventh, a trial) ran at 6.0-6.1 ms with 6.2-6.5 ms of GPU, the rest at 7.9-8.6 ms with 6.7-7.3 ms; the GPU clock state,
not the build. **Observed**: no change beyond the noise; the CPU figures are a little lower on this build in all three scenarios.
**Unknown** why: the renderers recorded natively before this stage already; VkGl's remaining per-frame work (its frame hooks, barrier
counting, the GL state mirror) is the likely reason, not measured apart.

**VRAM** (**Observed**, `--fly-benchmark 300` over the forest, the `vram` line): no owner is named `gl ...`; every allocation carries its
renderer's name. Resident objects and foliage are the same as the base (1288 / 1471 MB at frames 150 / 300); the process's working set
was 3.0-3.3 GB against the base's 3.8-4.4 GB. The largest owner by bytes is `frame constants` (880 MB in 110 chunks of 8 MB, 0 MB
device-local): the frame's constants double as the `Uploader`'s staging, and `LinearAllocator.Reset` keeps every chunk of normal size, so
the peak of the loading frames stays allocated in host memory. That predates this stage (unchanged code); fixed after it (owner decision 15: 16 MB of frame constants and 56 MB of upload staging, working set 2.6 GB, same benchmark).

### 8.11 Video memory at large ranges (2026-10-07)

*In short: with the range sliders raised tenfold the GPU ran out of device-local memory and the driver reset the device (a TDR). The
largest owner by far was the foliage cull's per-view scratch (2.0 GB at 20k, 3.8 GB at 40k large range, in the base build); it is now
sized by need (330 MB at 40k). A guard on the driver's memory budget clamps the ranges before the card is full. Pictures at the default
settings are unchanged (0 px, ten views, both modes).*

**Cause** (**Verified**, base build, `MEITOU_VRAM_KILL` watchdog, RTX 4070 with an 11.4 GB budget): the cull wrote each view's fades, counts and
compacted rows into scratch sized for 256 instances of every chunk slot of the work list, so it followed the number of chunk *slots*
(partly empty chunks, one per group and view) instead of the instances: 2.0 GB at all ranges x2 (`--range-large 20000 ...`), 3.8 GB at x4,
and the run at x8 passed 95% of the budget in the first seconds (`base_all80`). The shadow distance multiplies it (every cascade is a view).

**Fixes, with the owner they act on** (**Observed**, `--fly-benchmark 150` over the forest view; device-local MB; ranges `all20` = large 20000 /
medium 10000 / small 4000, object distance 40000, distant 30, shadow 15000; `all40` = 40000 / 20000 / 8000, 100000, 50, shadow 15000):

| owner | base default | now default | base all20 | now all20 | base all40 | now all40 | now max (large 120k) |
|---|---|---|---|---|---|---|---|
| foliage cull scratch | 128 | 20 | 2007 | 248 | 3834 | 330 | 614 |
| foliage instances (arena) | 64 | 16 | 64 | 32 | 128 | 64 | 128 |
| foliage grass scratch | 32 | 2 | 32 | 2 | 32 | 2 | 2 |
| foliage textures | 730 | 667 | 738 | 638 | 826 | 739 | 1052 |
| peak in use (of 11453 MB) | 3504 (31%) | 3248 (28%) | 6250 (55%) | 4394 (38%) | 8621 (77%) | 5052 (44%) | 6857 (60%) |

1. **Scratch by need** (`FrameScratch`): one buffer per frame slot, the largest need of the trailing 120 frames plus 25%, rebuilt when a frame
   overflowed into extra buffers (grow on demand) and made smaller when more than twice the window's need (shrink after about two seconds);
   a cap per slot (cull 512 MB, grass 64 MB). A request beyond the cap leaves that view out of the frame (counted, shown in F11 and the
   benchmark's `scratch` line) and the guard shortens the ranges. The rows are exact now (the sum of the chunks' instance counts, not 256
   per chunk). The pixels do not change (the kernels write the same values to the same places; **Verified**, gate and
   `FoliageGpuCullTests`).
2. **Far zones keep only what can be drawn** (`FoliageRenderer`, `Tier`): with the Meitou `range` switch a zone farther than the longest of the
   small range, the medium range and the grass range holds only its large-class groups (`Tier.Meshes`; its grass is not made), and beyond the
   near reach only the far layers' large groups (`Tier.Far`). A group whose mesh is not decoded has no size yet: it is kept and dropped by
   a prune pass when the size is known. Mesh textures are made lazily: a texture is loaded when its mesh is decoded and first used, not
   when the group is created (dropped groups never load theirs). Faithful mode is untouched (`Tier.Meshes` does not exist there).
   **Observed**: foliage textures 9-13% lower at the same ranges (default 730 -> 667, all20 738 -> 638, all40 826 -> 739 MB), because the large-only far
   groups share the textures of the near ones; the saving is small since most of the foliage's catalog is large-class at some distance.
   **Unknown**: how much more a Data-layer scope (loading only large groups from the .mod records) would save in managed memory; the layout
   still builds every group on the worker before the filter.
3. **Instance records of 68 bytes** (`FoliageInstanceRecord.Pack`, 96 before; **Verified**: 0 px in both modes, ten views, and `0 not lossless`
   records in every run): the arena holds the transform's 12 floats (the fourth column is exactly 0, 0, 0, 1 for a placement and the kernel writes
   it back), the sphere (4) and the rock bits (1). `Ground.X/Y` are the translation's x and z (the same floats, checked per record by bit
   comparison; a record that breaks it is counted in `PackMismatches`) and `Ground.Z` only feeds the sphere's radius on the CPU. A smaller
   encoding (a quaternion and a scale, or half precision) is **not** bit-exact: the GPU's matrix from a quaternion differs in the last bit, and
   the driver's square root is not correctly rounded, so it was not used. The CPU side keeps the 96-byte record.
4. **Budget guard** (`VramGuard`, from `VulkanDevice.VideoMemory()`, VK_EXT_memory_budget, sampled four times a second; **Verified** with
   `VramGuardTests` and runs with `MEITOU_VRAM_BUDGET_MB` set below the card's budget): at 90% of the budget it enters pressure: new zone layouts,
   grass pages, textures and mesh decodes are not started, the caches evict what has been idle for 2 s (instead of 60), and the effective ranges
   (foliage, objects, distant towns, shadow distance when above its default) are multiplied by a scale that falls 12% every 0.75 s down to 0.15
   while the use stays above 80%. It leaves pressure under 80%, and the scale comes back 4% a second after four seconds under 74%. Allocations
   that can be large (a texture's image, scratch growth, an arena growth) ask `Allows` first and are refused past 92%, which also enters pressure
   at once. It logs once (`vram      guard: ...`) and the F11 statistics show the state (`guard: ...`). At the default settings and at `max` on the
   11.4 GB card the use stays under 60%, so it never leaves idle (**Observed**; its scale is exactly 1, so it cannot change a picture then).
   `MEITOU_VRAM_GUARD=0` switches it off.
   **The budget is the driver's, not ours** (**Observed** 2026-10-10): VK_EXT_memory_budget reports what Windows grants this process, the
   card less what other processes hold, and it moves. With other GPU programs running (another viewer, a benchmark) the 12 GB card's budget
   was 7226 MB; with them gone, 11450 MB. At 7.2 GB the guard clamped the ranges and the owner saw no buildings at all.
   **Lean allocator under pressure** (2026-10-10, **Verified** with `CoreTests.A_lean_allocator_gives_back_its_empty_and_spare_blocks_and_keeps_none`):
   entering pressure sets `GpuAllocator.Lean` (the guard's `Allocator`): every empty block and spare goes back to the driver at once, a block
   that empties goes back instead of being kept, and no spares are made; leaving pressure turns it off. The F11 line `room in our blocks`
   splits the gap between `our blocks` and `used` into empty, spare and scattered (free ranges inside part-used 64 MB blocks). **Observed**
   (owner, F11, 2026-10-10): 6.72 GB in blocks, 4.65 GB used; the allocator already gave back all but one empty block per pool, so most of
   that 2 GB is expected to be scattered, which only moving resources out of part-used blocks (defragmentation) would give back.
5. **Headless watchdog** (`VramWatch`, `--screenshot` and the benchmark): a thread that polls the budget every 100 ms and exits the process with
   code 9 when the use passes `MEITOU_VRAM_KILL` (default 0.95, 0 = off), so a test never takes the driver down.

**Timing against the base build** (**Observed**, `--fly-benchmark 300` over the forest view, three interleaved runs each, base / this): flying,
frame p50 7.9 / 8.0 / 8.5 against 7.2 / 7.2 / 8.0 ms, p95 12.6-12.8 against 12.3-12.7, CPU-only p50 1.9-2.1 against 2.0, p95 4.5-4.8 against
4.2-4.3; still camera (`--fly-speed 0`) p50 7.7 / 8.3 / 4.6 against 8.1 / 6.0 / 4.6 ms (the GPU is bimodal on both builds, as in 8.9), CPU-only p50
0.9-1.3 against 0.9-1.4. No change beyond the noise; the CPU figures are a little lower (cause **Unknown**).
At the default the managed heap is 1349 / 1335 MB. At `all40` flying: p50 24 ms (the work is the range's), peak 5.05 GB against 8.6 GB.
Gate: ten views in both modes, max 0 against `6f4af19`'s pictures; `MEITOU_VK_VALIDATION=sync` on two views, 0 errors (the 4 leaked
image views the validation layer reports at device destruction are in the base build too); full test suite 467 passed, 0 failed, 0 skipped.

**Ranges reached** (**Observed**, shadow distance 15000 at most; the driver resets above): `max` = large 120000, medium 80000, small 40000,
object distance 400000, distant 100: the run completes, peak 60% of the budget, 78 ms a frame (the load is real; the guard did not act).
The base build is aborted by the watchdog at `all80` (95% in the first seconds). With `MEITOU_VRAM_BUDGET_MB=6144` (a smaller card) the guard
holds the use at 80-93% at `all80`; at that budget the cull scratch can be refused and foliage drops out for seconds (**Observed**; fixed, see the addendum below).

**Other owners that grow with range** (**Observed**, `GpuAllocator.Breakdown`, the F12 pie): object textures (1.25 GB at all20, 1.7 GB at max;
bounded by the cache's 2048 MB high-water mark), object meshes (273 MB, 800 MB at max; high-water 768 MB, which the cache overshoots while its
pages are in use), foliage textures (0.57 to 1.05 GB; bounded by the catalog), the foliage instance arena (16 MB to 128 MB, now 71% of that), and the
shadow cascades' views through the cull scratch. Not range-dependent: terrain textures (1.07 GB), grass blades (160 MB), upload staging.
The object textures and meshes are cut in 8.14.

### 8.14 Object textures and meshes at large ranges (2026-10-07)

*In short: the two next-largest owners (object textures 1.25 GB at the 20k preset, object meshes 273 MB; 1.7 GB and 800 MB at `max`) were
held in full for everything within range, because the caches' marks only evict what has been idle, and nothing in range is. Both now hold only
what the picture can use at the distance things are drawn at: a texture loses the top mips no pixel of it can sample, a mesh keeps only the LOD
levels its nearest instance can draw. Object textures 1252 -> 797 MB at all20, 1725 -> 1342 MB at `max`; object meshes 273 -> 157 MB at all20,
813 -> 666 MB at `max`. Pictures unchanged (0 px, ten views, both modes).*

**Causes** (**Verified** in the code, **Observed** in the runs below):
1. *The meshes do not overshoot their 768 MB mark because of a bookkeeping error.* `ObjectMeshCache.Bytes` equals the allocator's `object meshes`
   owner (273.1 MB against 273.1 MB at all20; the one buffer kind not counted, the terrain path's `PlainEbo`, is not made in these views).
   The mark never acts on in-range meshes: `MarkInRange` stamps every instance within its draw range as used once a second, `Trim` only
   evicts what has been idle for 8 s (under the mark) or 60 s, and at 120k range everything is in range. 813 MB against 768 MB is just
   what is in range. Evicting in-range meshes would only thrash (the instance un-resolves, the scan asks again). So the mark cannot be made
   to hold by evicting; what is held has to get smaller. (Same for the textures: 1.7 GB under a 2048 MB mark, nothing idle.)
2. *Everything in range is held at full detail.* A 4096 x 4096 BC3 texture is 21.3 MB, 75% of it the top mip, and 20 of the 75 resident
   object textures at the default ranges (43 of 196 at all20) are that size. A mesh keeps one vertex pool and one index list per LOD level
   (and a mesh per manual level) however far away its nearest instance is.

**1. Maps nothing reads are not loaded** (exact; `MakeMaterials`): the normal map only for a part with tangents (or for a material with the
emissive flag, which looks at its key), the second diffuse and normal only for a part with vertex colours (the blend weight) and tangents.
**Observed**: nothing at the default ranges, 3 textures / 8 MB at all20. The textures are made without loading (`deferred`) and loaded by the first read of
their key in `Resolve`, after being told how near they are used.

**2. Mip streaming of the object textures** (`MipStreaming`, `MeshTexelScale`, `WorldTextureCache.Rebalance`):
- *The bound.* A sampled mip level is the footprint of a pixel in texels, log2, from the short axis under anisotropic filtering. The footprint
  of a pixel at distance `d` is at least `d cos^2(a) / f` world units (`f`: the focal length in pixels at the render size, `a`: the angle to the
  screen's corner; a tilted surface stretches only the long axis). One texel of an `N`-texel texture spans `k / N`, `k` being the world length of
  a texture-coordinate unit: the part's `MeshTexelScale` (the larger singular value of the texture-to-surface map per triangle, so a stretched
  triangle, which shows few texels per unit and needs the finest mip first, counts; the value the largest 0.5% of the part's area exceeds, infinity when
  more than that is collapsed) times the instance's scale over the material's `Tile`. With `need = d / k`, the nearest user's, the level is at
  least `log2(need * pixelsPerDistance * N) + bias`, and the texture can lose that many top levels less a margin (`MipStreaming.Margin`, 2 levels: up to one for the sampler's tap rounding
  (long axis divided by a whole number of taps), the rest for the 2x2 derivative estimate, triangles stretched more than the part's value and the time a finer image takes).
  `bias` is the sampler's: the upscaler's `Gpu.LodBias` and the -0.25 the sampler adds on NVIDIA with anisotropy. A drop leaves the lower levels
  as they are: the sampler's lod is the same exact shift (a power of two), so the picture is the same.
- *Not dropped*: triplanar materials (a surface edge-on to a projection axis has a footprint smaller than a pixel on it: no bound), the distant towns' atlas,
  textures whose top level is 512 or less on the larger side (also keeps the level the normal-map swizzle test reads in the chain), and formats
  `TextureQuality.DropTopMips` cannot re-slice (not `DXT*`, DX10, no full chain).
- *Where `need` comes from.* `MarkInRange` (once a second, every instance in its draw range whether seen or not) takes the nearest distance of each material
  set (over the instance's scale), less the distance the camera covers in half a second (the time to refine); `Resolve` offers a new instance's at once
  (`WorldTexture.OfferNow`). `CommitNeeds` makes the pass's smallest each texture's `Need`. A load (first, or after an unload) uses the `Need` at that
  moment: `Load` drops `max(quality setting, streaming)` levels once the file's size is known, on the worker.
- *Refine and coarsen.* A resident texture whose image has more levels dropped than its `Need` allows is loaded again with fewer; the old image is
  drawn until the new one is in (`BeginSwap` frees the old image and its bindless entries after the frames in flight; no stand-in frame). One finer than
  needed for 10 s (at once under the guard's pressure, which also drops one level more) is replaced by a coarser. At most 6 refinements and 2
  coarsenings load at once (the few worker threads are shared with the foliage's layouts). F11 / the benchmark's `resident` line: `mip streaming: N MB
  less than every mip, R refined, C coarsened`.
- `MEITOU_MIP_STREAM=0` switches it off; `MEITOU_MIP_MARGIN=<levels>`, `MEITOU_MIP_TOP=<share>` (the 0.5%), `MEITOU_MIP_SKIP=<name,name>` (keep named textures whole)
  and `MEITOU_MIP_LOG=1` (a line per image loaded with dropped levels) are for experiments.

**3. Meshes keep only the LOD levels near users draw** (`ObjectMeshCache`, `ObjectMesh.NearPass`, `GpuObjectMesh.MinLevel`):
- A mesh is decoded for the nearest placement known when it is requested (the scan's distance, less six mesh radii, as the mesh's size is not known
  before the decode) or, once resident, for its nearest user's LOD value. `LevelFor` is the finest level the viewer's blend (`MeshLod.Blend`, 6% band, taken
  as 7%) can draw at that value times the LOD bias. The decode keeps only the levels from there on: the vertices they use are compacted
  (`PreparePart`, indices remapped, triangle order unchanged), the finer levels' indices and the manual meshes of finer levels are not decoded or uploaded.
  Meshes drawn through the terrain shader (`KeepLevelIndices`), distant meshes and manual (inner) meshes always have every level.
- A mesh in range is remade (`Retarget`: decoded again, uploaded, then swapped in place into the same `GpuObjectMesh`: instances, batches and materials keep
  holding it, the old buffers go after the frames in flight) with finer levels when its nearest user gets inside its level boundary (one level more than needed,
  against the next boundary), and without the finer ones after 10 s (or at once under pressure). An instance that would need a finer level than the mesh holds
  (its mesh was made for farther ones) is not resolved, and the mesh is remade, until it is (`Resolve` returns false; `heldNear` counts those within 3000); an
  already resolved one draws the finest level held in the meantime instead of nothing (`Emit`). At most 2 remakes at once.
  `MEITOU_MESH_STREAM=0` loads every level.

**4. Marks and the guard.** The caches' high-water marks are now the smaller of the old fixed one (768 / 2048 MB) and a share of the driver's budget
(`HighWaterShare`, the old marks over the 11.4 GB card they were tuned on: 6.7% and 17.9%), and three quarters of that under pressure (`VramGuard.BudgetBytes`,
`WorldTextureCache.EffectiveMark`). On the 11.4 GB card they are the old marks exactly; on a 6.3 GB budget 1.1 GB and 0.4 GB. The guard also makes the
streaming itself shed: under pressure the textures' margin is one level less, coarsenings and remakes without finer levels run at once, and refinements and finer remakes
are not started (`MayStart`, `Streaming`).

**Measured** (**Observed**, `--fly-benchmark 150` over the forest view, device-local MB of the allocator's owners; presets as 8.11; base = master `87c7857`; `max` and the timings
on an idle GPU):

| owner | base default | now | base all20 | now | base all40 | now | base max | now |
|---|---|---|---|---|---|---|---|---|
| object textures | 536 | 468 | 1252 | 797 | 1490 | 984 | 1725 | 1342 |
| object meshes | 73 | 59 | 273 | 157 | 395 | 265 | 813 | 666 |
| peak in use (MB, of 11454) | 3353 | 3353 | 4665 | 4025 | 5323 | 4683 | 6909 | 6141 |

(Default and all20 rows: the final build, which also makes meshes for 1.5 s of the camera speed ahead of it; peaks 3289 and 4025 MB. Margin 1.5 levels instead of 2, which gave the faithful portnorth view 31 differing pixels: textures 436 / 687 / 886 MB at default / all20 / all40.)
Each alone (builds with the margin 1.5): all20 textures 1244 -> 697 MB with the mesh streaming off, meshes 273 -> 157 MB with the texture streaming on; each
alone gives its share. At `max` the whole map is in range and most textures have a near user somewhere, so the textures only lose 22% and the meshes 18%.
At a 4400 MB budget at all20 (the guard acting): base peak 4081 MB (93%), ranges x0.60, 796 allocations refused; now 3801 MB (86%), ranges x0.68, 1 refused.

**Timings** (**Observed**, three interleaved runs each, base / now, flying 300 frames and still camera, forest view, idle GPU): default, flying frame p50 6.5 / 6.4 / 6.4 against
6.6 / 6.7 / 6.5 ms, p95 12.3-12.8 against 12.3-12.6, CPU-only p50 1.9-2.0 against 1.9-2.0 (p99 14-18 against 15-24); still p50 6.3-6.4 against 5.9-6.4. All20 flying: p50 7.7 / 8.3 / 8.4
against 8.6 / 8.4 / 8.3 ms, p99 15-20 against 15-21, CPU-only p50 2.5-2.6 against 2.6 (p99 8-9 against 9-15); still p50 7.1-7.2 against 7.2. `max`: p50 34.0 against 34.3 ms. No
change beyond the noise; the refinements add CPU p99 at all20 (a texture's image is made in one step, 4 to 13 ms for a 21 MB image when the allocator takes a new block; the
base builds the same images at load). On a GPU shared with other processes the noise was larger than any difference (flying p50 +0.6 ms for the new build in one set, none in the
quiet one).
All40 (three interleaved runs, final build, idle GPU): flying p50 9.6 / 9.8 / 9.8 ms base against 10.0 / 10.1 / 10.1 now, p95 12.2-12.6 against 13.0, p99 15-18 against 23-24, CPU-only p50 2.9 against 3.0, CPU p99 9-10 against 13-16; still p50 8.5 against 8.7 (p99 14-15 against 14). About 0.3 ms at p50 and a few ms at p99 in flight, from the refinements' and remakes' CPU work; the max (230 against 170-185 ms) is the first frames' loading and is shorter with the streaming.


**Pop-in** (**Observed**; the benchmark's `pop-in    objects` line, new build only; `MEITOU_MIP_STREAM=0 MEITOU_MESH_STREAM=0` is the base's behaviour: 0 in all of them): parts drawn untextured 0 of 150 frames at
default, all20, all40 and max; textures waiting for a finer image while the old one is drawn in 9 / 22 / 32 / 19 frames (most 6 / 16 / 22 / 6 at once); instances within 3000
held back for their mesh to be remade 0 frames; remakes for finer levels in flight in 2 / 7 / 9 / 4 frames. The foliage `pop-in` lines vary a great deal from run to run on both builds
(all40, frames without the whole layout within the near reach, three to seven runs each: base 4, 20, 24, 31, 37, 73, 79; now 14, 21, 23, 28, 84, 92, 118: the 84, 92 and 118 were before the limit on
refinements and remakes in flight, which keep the worker threads free for the layouts). The picture after a 150-frame flight (`--fly-benchmark --screenshot`, all20, forest) equals
the base's: 0 px.
The `pop-in    objects` line also counts instances drawn at a coarser level than their distance picks because the mesh they use had not been remade yet: 30 frames at default and 26 at all20 (most 4 instances at once, in the 7% blend band of a level boundary, drawing the held level instead of blending it with the next finer; seen in the sun's shadow pass, whose cascade frustum reaches instances that the once-a-second mark had not counted near). It is the flying camera outrunning the one-second mark; not seen as a difference in the pictures (0 px). The streaming also runs under `--faithful`, and the faithful ten views are among the gates.


**Gate** (**Verified**): ten parity views, `--faithful all` and default, 13:00 and 02:00, max 0 against `87c7857`'s pictures; six far views at all20 (forest, forest at 3000, the Hub at 15000
and 40000, Port North at 8000, zone 14.30 at 20000) with the streaming off and on: 0 px except the Hub at 40000, 1 level in a few pixels (the 0.5% of triangles `MeshTexelScale` ignores;
with the share 0 it is 0 px but a third of the saving is left). `MEITOU_VK_VALIDATION=sync` on two views and a flight: 0 errors (the same 4 leaked image views at device destruction as the base).
Unit tests `ObjectStreamingTests` (the bound, the texel scale, the level, the compaction, the marks); full suite 484 passed, 0 failed, 0 skipped.

**Open** (**Unknown**): the bound ignores the 0.5% most stretched triangles and relies on the 2-level margin for them: a view with a mesh more stretched than that may differ in a few pixels
(seen: 1 level at the Hub from 40000; the margin 1.5 gave 62 at most in the faithful Port North view on a curved wall before it was raised to 2). The foliage textures (0.57 to 1.05 GB) could use the same
mechanism (their sets, k per mesh and the atlas are different; out of this change). Textures and meshes of the in-range set could also be skipped by screen coverage (a texture whose every user is a few pixels wide
needs no top mip whatever its nearest distance), which would help `max` most. Vertex stride: the object vertex keeps unused bone indices and weights (20 of 84 bytes) for the layout the shaders
share; dropping them would save 24% of the mesh memory with no visual change but needs the program layouts reworked.

**Addendum: the four leaked image views, and the scratch under the guard** (2026-10-07)

1. **The leak** (**Verified**: `MEITOU_VK_VALIDATION=sync`, offscreen `--screenshot` of Port North, a temporary live-texture registry that named the
   owners, then the same run clean). `GpuContext.Dummy` makes one stand-in texture per sampler slot (2D, array, cube, and one more kind) as a
   `Texture.Borrow` over an image that `Defaults` owns and cuts a view on it; `Texture.Dispose` is the only thing that destroys a texture's views,
   and nothing called it for the borrowed wrappers, so each kept its view: four `VkImageView`s (VUID-vkDestroyDevice-device-05137). They are now
   kept in a list and disposed in `GpuContext.Dispose`, before `Defaults`. **Verified**: after the change the offscreen viewer, the interactive
   viewer (`--quit-after 20`, a clean close) and the game (`--quit-after 15`) with sync validation print no leak message and 0 errors. The
   statement in the list above that the base build has the same four views is true for the base and no longer for this branch.
2. **Scratch under the guard** (`MEITOU_VRAM_BUDGET_MB=6144`, forest view, `all80` = large 80000 / medium 40000 / small 16000, objects 200000,
   distant 100, shadows 15000, `--fly-benchmark 600`). **Observed** before: the cull scratch refused 1853 times in 388 of 600 frames (the
   guard sits at 91-93%, so every scratch buffer was refused by `Allows`; each slot's rebuild also freed its old buffers first, leaving the frame
   with none). Three changes, in the order of how much they gave:
   - *Scratch asks for priority*: `VramGuard.AllowsPriority` refuses only past 94.5% (`PriorityCeiling`, under the watchdog's 95%), against 92% for
     caches and streaming, and a grant above 92% enters pressure (caches evict, ranges fall). `GpuAllocator.FitsInFreeSpace` lets a buffer that
     the allocator can carve from free space in blocks it already holds skip the guard (it takes nothing from the budget; 2.4 GB of 6 GB held by
     the allocator was free at the time).
   - *Squeeze*: a refused scratch steps the range scale down at once (at most every 50 ms, only under pressure) instead of waiting for the next
     0.75 s step, so a view is shortened within a few frames instead of being left out for seconds.
   - *A rebuild keeps what it has*: `FrameScratch` makes the replacement before freeing the old buffers and keeps the largest old one when the
     replacement is refused (`KeptOnRefusal`).
   **Observed** after: 4-6 frames of 600 with a refused scratch (the first 50 frames, before the guard has a reading; two runs: 6 and 4 frames,
   12 and 10 requests), against 388; allocations refused for everything 6729-10342 (before) against 44-46; peak 94.8% of the budget (no
   watchdog abort). With the guard idle (default settings, default budget) nothing changes: `AllowsPriority` equals `Allows` below 92% and the
   squeeze needs pressure. **Unknown**: whether a card whose budget is shared with other applications needs a lower priority ceiling; the
   start-up frames still lose a view or two (the demand there, 150 MB per frame, is before any clamp) and fall back to nothing, not to the last
   frame's rows (that was not built: the rows are exact per frame).

### 8.15 Render distance benchmark (2026-10-07)

Measurement only; the full write-up is [render-distance-benchmark.md](render-distance-benchmark.md). In short (**Observed**, RTX 4070, 1600 x 900):
- "1 px" (`d = size x f`, `f` = 965 px at 900 lines) is beyond the world for large foliage, medium foliage and buildings, 5 000-77 000 for small
  foliage and 10 000-19 000 for median grass. At that preset (CPX: large 192 000, medium 80 000, small 12 800, grass x16, objects 400 000) the GPU
  takes 21.8 ms (forest flight) to 45.8 ms (Hub, Stack) against 2.5-2.7 ms at the defaults; the render thread 7-8 ms; VRAM 51-59 %.
- Ranked: the 192 MB impostor budget refusing atlases (full tree meshes; −22 ms at the Hub with 2048 MB), TERRAIN-mode rocks without LOD or impostor
  (21-41 M triangles, 5.6-10.5 ms, primitive-bound), meshes without an impostor class (3.8-5.3 ms), objects at 400 000 (4 ms GPU, 1.3 ms CPU), the
  CPU's per-view foliage work lists (3.8 ms of the render thread). Fill rate, shadows (the cascades do not grow with the ranges) and VRAM are not limits.
- Wave 4: `MEITOU_RECORD_THREADS=0` has the lowest render-thread time at the defaults, at C40 and at CPX (0.15-0.45 ms less than mode 2); proposed default 0.
- Two measurement fixes for this note's earlier tables: the paced benchmark lets the GPU clock down at light load (forest defaults 4.75 ms of GPU paced
  against 2.52 pipelined, so 6.6's "GPU-bound" reading is mostly clocks), and `PassMeter` divided GPU rows by their runs, not by frames (stages drawn per
  depth slice and cascades drawn every second or fourth frame were under-counted; fixed).

### 8.16 Impostor budget that follows the card, far mips and the small class (2026-10-07)

*In short: the impostor limit is a share of the driver's video-memory budget instead of 192 MB, a plan keeps what fits nearest first and degrades the far atlases by mips before leaving any out,
and meshes under radius 48 get a 32 px impostor class. CPX Hub 46.4 to 20.6 ms and forest 21.5 to 15.2 ms on the GPU; defaults and the Faithful pictures unchanged. Rule, tables and measurements:
[impostors.md](impostors.md) sections 10 and 11; [render-distance-benchmark.md](render-distance-benchmark.md) 7.1.*

Files: new `ImpostorBudget.cs`; `FoliageRenderer.Impostors.cs` (plan, refine, `SkipFor`, `AtlasBytes`, `EstimateClass`), `FoliageRenderer.cs` (`MeshAsset.Triangles`, class estimate), `Impostors/ImpostorLayout.cs`
(`ImpostorClass.For`, `Transition`, `BakeDistance`), `Impostors/ImpostorBaker.cs`, `Gpu/Core/VulkanDevice.cs` (`IsIntegrated`), `WorldFrame.cs`, `WorldApp.Benchmark.cs` (pop-in line),
`tools/Meitou.ModelViewer/ImpostorApp.cs` (`--class small`); tests `ImpostorBudgetTests`, `ImpostorTests`.

- **Budget** (**Verified**, tests): `clamp(8% x B, 48 MB, 1024 MB)` and at most 12% of `B` on a discrete GPU, 5% and 256 MB on an integrated one, 192 MB when `B` is unknown, x0.75 under `VramGuard` pressure;
  `B` is the guard's budget (device-local heaps, VK_EXT_memory_budget). 164 / 328 / 655 / 983 / 1024 MB at 2 / 4 / 8 / 12 / 16 GB discrete, 102 / 205 / 256 / 256 / 256 integrated. `--impostor-budget` overrides.
  Integrated GPUs: **Unknown** (no card; `MEITOU_FORCE_INTEGRATED=1` tests the numbers).
- **Far mips**: the albedo keeps the levels the nearest wanted instance can sample (one level of margin, `MEITOU_IMPOSTOR_MIP_MARGIN`), the normal map one fewer; refined or coarsened by reloading the atlas from the
  disk cache on a worker and swapping the textures after the upload (old ones deferred-deleted), 3 at a time. Picture-neutral: 0 px in the ten views (with half a level: max 2, with none: max 17).
- **Plan** (once a second): wanted atlases sorted by nearest distance (resident ones x0.8), mips, then over the limit the farthest take up to 3 more levels, then are left out; resident ones left out twice
  (once under pressure) are dropped. Replaces the refuse-and-retry-after-8-s loop that reloaded the same atlases thousands of times.
- **Small class**: radius 2 to 48 and 100 or more triangles, 32 px frames, grid 12, transition `ImpostorDistance x R / 48`, bake bias at its own distance. Atlases are 0.28 MB; 139 of the base game's 218 small meshes qualify.
- **Pop-in**: the benchmark prints `pop-in    impostors:` (left out, admitted but not resident, coarser than wanted, refines in flight).
- **Gate**: `dotnet test -c Release` 513 passed, 0 failed, 0 skipped; Faithful ten views 0 px, `--faithful impostors` 0 px against master, Meitou defaults 0 px in nine views and a few small spots in two (the small class), validation 0 errors.

### 8.18 Impostors for TERRAIN-mode rocks (F2, 2026-10-07)

*In short: rocks drawn through the terrain shader get a hemi-octahedral impostor per (rock mesh, biome), baked with the terrain's own mesh material. Colour-view rock triangles in the Hub flight
599 k to 15 k per frame, GPU frame mean 2.61 to 2.24 ms (unpaced fly benchmark). Meitou mode only; Faithful pictures 0 px. Design, numbers and limits: [impostors.md](impostors.md) section 13.*

Files: new `TerrainRenderer.RockBake.cs`; `TerrainShaders.cs` (`RockBakeFragmentNative` / `RockBakeVertexNative`, `TerrainBakePush`, the mesh shader's crossfade dither `vFade`), `TerrainTextures.cs`
(`IsResident`, `BiomeKey`), `Impostors/ImpostorShaders.cs` (`RockFragmentNative`, `ImpostorRockPush`), `Impostors/ImpostorLayout.cs` (`ForRock`, `Knee`), `Impostors/ImpostorSource.cs` (rock key),
`Impostors/ImpostorBaker.cs`, `Impostors/ImpostorDraw.cs`, `FoliageRenderer.cs` / `.Impostors.cs` (variants per biome row, segments, work list), `FoliageShaders.cs` (cull and compact: biome row, dither lane), `WorldFrame.cs` (one line, `f.Terrain`).

- **Verified** (Faithful ten views 0 px against `C:\Temp\base-87c7857\faithful`; `--faithful impostors` 0 px against the base viewer with the same flag): the terrain mesh shader's new `vFade` input is 0 unless a rock is in its crossfade band, so nothing changes outside Meitou rocks. The golden SPIR-V hashes of "terrain mesh" were updated (`GpuApiTests`, `LegacySpirvGoldenTests`).
- **Verified**: `MEITOU_IMPOSTOR_ROCKS=0` gives 0 px in all ten Meitou views against `C:\Temp\mi\meitou`; with it on, the differing views are the far rocks only (forest, rock, zone14_30 at 13:00: mean 0.67-0.73, max 95-219; Hub 0.018; Port North 0).
- **Verified**: `MEITOU_VK_VALIDATION=sync`, empty cache (canyon view and a 900-frame Hub flight): 0 errors.
- Gate: `dotnet build` 0 warnings; `dotnet test` all passed.

---

### 8.6 Phase 8 stage 2: impostors native and drawn (2026-10-07)

Files: `Impostors/ImpostorBaker.cs`, `ImpostorTextures.cs`, `ImpostorShaders.cs`, `ImpostorPreview.cs`, new `ImpostorDraw.cs`;
`tools/Meitou.ModelViewer/ImpostorApp.cs`; `NativeProg.cs` (moved out of `FoliageRenderer`); `FoliageRenderer.cs` and new
`FoliageRenderer.Impostors.cs`, `FoliageCull.cs`, `FoliageShaders.cs`, `FoliageGpuCull.cs`; `Enhancements.cs`, the option, switch and
slider lines of `WorldFrame.cs` / `WorldApp.cs`; `WorldTextureCache.cs` and `Gpu/GlBridge.cs` (impostor parts removed);
`WorldApp.Benchmark.cs` (VRAM lines, `MEITOU_BENCH_SHADOW_RANGE`).

- **Ported** (from the code): the baker records each row into the frame's pre-frame list and reads it back after the frame completes
  (`ImpostorBakeJob`, stepped per frame); the atlas textures are native `Texture`s ("impostor atlas albedo/normal/depth", GENERAL,
  bindless, trilinear clamp, re-registered on an LOD-bias change); the preview and `--impostor-preview` / `--impostor-bake-all` render
  with native passes (4× MSAA, resolve, readback). Impostor files no longer use `IGl`.
- **Verified**: native atlases byte-identical to the base GL baker's (BushTree01), `--impostor-preview` pictures 0 px, `BakerVersion`
  unchanged; the ten parity views 0 px with `--faithful all` and with Meitou and `--faithful impostors`; the GPU split bit-identical to
  the CPU (test and verify mode; impostors.md 7); `MEITOU_GPU_CULL=0` and `MEITOU_RECORD_THREADS=0` 0 px from the default; sync
  validation 0 errors in the forest (13:00, impostors on) and Port North (13:00, `--water-reflection 4`).
- **IGl count**: `FoliageRenderer.cs` 0 → 1. `Settle` (offscreen loading) waits in a loop without frames, but a bake's rows are read
  after their frame completes, so it calls `IGl.Finish` (end the frame, wait, begin the next) while a bake needs one. Stage 3 replaces it
  with the native frame loop's equivalent.
- **Drawing, shadows, pictures and measurements**: impostors.md section 7.

### 8.17 Object mesh reload churn (2026-10-07)

- **Symptom** (owner, F11 at 400 000 objects after a session of flying): object meshes 5060 unloaded / 5010 reloaded with the VRAM guard
  at 56%. A 60 s fly benchmark (`--range-large 120000 --range-medium 80000 --object-distance 400000 --fly-benchmark 3600`, The Hub,
  12 000 circle, every place revisited every ~8 s) reproduced it: 318 unloaded, 294 reloaded.
- **Cause**: `ObjectStreamer.Scan` asked for the mesh of every unresolved instance within the object distance, but `MarkInRange` keeps a
  mesh in use only while an instance is within `min(range, Limit)`, and a building part's `Limit` is the game's part distance (3000,
  `ObjectRanges.PartRenderingDistance`). A part beyond it was loaded, never drawn or marked, unloaded after the idle minute
  (`StreamingTuning.IdleSeconds`), its instances unresolved, and asked for again at once. Not the caches' high-water marks: lifting them
  into the free VRAM changed nothing (318 / 306).
- **Fix**: `Scan` asks only within `min(objectRange, Limit)` (`Limit` is kept on an unload, so after the first load of a part's mesh its
  distance is known), and `MarkInRange` also keeps a mesh used by an instance that is not drawn but within what `Scan` asks for
  (`RequestMargin`, 600), so the two agree everywhere. Parts beyond their distance move to the zone's `Parked` list, walked only when the
  eye comes within the zone's largest part distance: left in `Unresolved` they cost `upd-objects` 0.12 → 0.36 ms at 400 000.
- **Measured** (same flight, `--fly-pipelined`; the paced benchmark's GPU times swung 4.3-8.1 ms between identical runs as the GPU
  downclocked, so they are not comparable): reloads 270 → 0; the 270 unloads left are far parts loaded once before their part distance was
  known. Resident object meshes 866 → 596 (713 → 626 MB). Frame p50 4.1 / 4.1 ms, GPU 2.47 / 2.52 ms, `upd-objects` 0.12 / 0.13 ms. Draws
  identical (223 instances, 237.8k triangles, 63 calls a frame in colour); "part-limited" falls 463 → 36 because those instances now wait
  parked instead of resolving and being skipped. Faithful ten views 0 px.
- **Left**: foliage meshes 184 unloaded / 61 reloaded and foliage textures 28 / 25 in the same flight (the 60 s idle rule; not examined).

### 8.19 Farther Meitou defaults and landmarks (2026-10-07)

Files: `Landmarks.cs` (new: `LandmarkClass`), `ObjectStreamer.cs` (`LandmarkZone`, `ScanLandmarks`), `WorldObjectRenderer.cs` (`DrawReal`, `LandmarkReach`), `Enhancements.cs`
(the `reach` switch), `WorldFrame.cs` (options, help, slider), `WorldApp.cs`, `OgreMeshReader.TryReadBounds`, `FoliageSizes.cs`; tests `LandmarkTests`, `OgreMeshReaderTests`.

- **New Meitou defaults** (**Verified**: `LandmarkTests`, `--help`; Faithful pictures 0 px, below). A new switch `reach` (`F8`, `--faithful reach`) carries the three
  values that were one number for both modes: objects at full detail to **20000** (Faithful 12000; the depth slices split at 1.1 x that, so 22000), the terrain LOD error **16 px**
  (Faithful 10) and the landmark distance (below). The foliage class ranges belong to the existing `range` switch, so they were Meitou-only already: large **50000**
  (was 12000), medium **12000** (was 5000), small 800 (`FoliageSizes.Default*`; `--faithful range` is still the game's per-layer ranges).
  `--object-distance`, `--terrain-error` and `--landmark-distance` given on the command line win in both modes, in any order with `--faithful`. "Reset to defaults" needed no
  code: the panel snapshots its sliders when it is made, so it returns to the values of the mode the viewer started in.
- **Landmarks** (Meitou only): a placement is a landmark when its world bounding radius (mesh bounds radius times the largest scale of its transform) is at least
  **2000** (`LandmarkClass.MinRadius`). A whole-world survey (`MEITOU_LANDMARK_SURVEY=1`, base game: 26 444 placements in 913 zones, 817 meshes, laid out and measured in 0.7-0.9 s) gives
  world radius p50 84 / p90 255 / p99 1085 / p99.9 3027 / max 38181; placements at or above 1000: 326 (73 meshes), 1500: 144 (44), 2000: **90 (31)**, 3000: 33 (15), 5000: 1. The largest
  building part of an ordinary building is the Ancient Factory shed at 1488; every placement from 2000 up is a map feature (**Verified** by the survey). The qualifying meshes
  (placements at 2000+ of the mesh's total, world radius range): `Ashland_Skylink` 1/1 (38181, the satellite), `Bones_Town_Ribs` 3/3 (2140-4928), `Canyon-Dangler05Z` 3/3 (3479-4288), `AshlandRibs` 14/19
  (2166-4065), `Ashland_Baselink` 1 (3863), `Vast_Land-Tube002` 1 (3686), `Ashland_Massdev` 1 (3557), `Tower Core RUIN` 2/2 (3506), `Vast_Land-Tube001` 1 (3396), `Canyon-Dangler04Z` 3/3 (2007-3340), `Fin Building` 1 (3230),
  `Ancient RIG platform01` 2/2 (3047), `GirderGroup_Loose01` 8/10 (3027), `Ashland_Wave-Ring` 1/2 (3012), `Patagonia_RockSlab01` 8/14 (2039-3003), `RottenAshlandTower` 1/2 (2974), `First Civ_ Full_Wreck` 6/6 (2910),
  `Tarsand_Pipeline01` 7/11 (2638), `FloodedForest_Rotten-Stem03` 6/8 (2604), `Fossil Spine 16K` 2/2 (2593), `BrownCanyon_DropBeams01` 1/5 (2403), `CLiff_Curved` 2/16 (2202-2390), `Bones_Wall_Ribs_Small` 1 (2390),
  `Bones_Wall_Ribs` 1 (2332), `Turbine_HallDamaged01` 6/6 (2275), `RUINTUBE01` 1/3 (2187), `Canyon-Dangler03Z` 1/3 (2185), `Mafic_HugeRockSlabs` 2/32 (2095-2152), `FeatureBluff001` 1/79 (2121),
  `ROCK-Arch_Type_01` 1/4 (2092), `Ashland_Mechanok` 1 (2046). No building part qualifies. The density near 2000 is continuous (no gap), so the threshold is a judgement: 1500 would add 54 placements,
  mostly cliff blocks (`FeatureBluff00x`, 79 + 47 + 51 + 29 placements between 860 and 2100) that are scenery, not skyline. `JunkSat01` / `JunkBall01` (radius 355) are foliage meshes, not placed objects, so
  the large foliage range (50000) draws them, not the landmarks.
- **How it works** (**Verified** by the pictures and benchmark below, design **Observed**). The radius must be known before the mesh is loaded or a landmark 100 000 units away could never be asked for: the bounds chunk is read
  without the geometry (`OgreMeshReader.TryReadBounds` seeks over the sub-chunks by their length fields; on an unknown id, a truncated file or no bounds chunk it gives up and the whole file is read, as `ObjectMeshCache.Decode`
  does: about 55% of the meshes did, 0.7-0.9 s for all of them in parallel; the probe equalled the decode on the 45 meshes checked). Streaming zones out to the landmark distance was rejected: at 150000 that is
  about a thousand zones, and `Scan` would walk every unresolved far instance every frame. Instead all populated zones are laid out once on the workers when the renderer starts (`LandmarkClass.Collect`, the zone layout is
  held back until it is done, about 0.7 s), the 90 landmarks are kept in one pseudo-zone (`ObjectStreamer.LandmarkZone`, never unloaded) and left out of the zones by the same test (so nothing is drawn twice). Every
  path then uses one range per instance, `min(range, Limit)`, with `range` = `LandmarkReach` (`max(object distance, landmark distance)` x the VRAM guard's scale) for a landmark and the object distance otherwise:
  `ScanLandmarks` (mesh requests, within the range plus the instance's own radius instead of the guessed 600), `MarkInRange` (keep-alive), `DrawReal` (the CPU cull and fade band, also for the shadow cascades and
  the reflection, whose clamp to 3000 now applies to the landmark distance too), the mesh LOD levels and texture mips (the objects' own rules: far forms by distance, `Rebalance`, mip streaming). **Observed** (`MEITOU_LANDMARK_LOG=1`, which lists each resolved landmark's levels and the finest level held, on the Ribs view at 35 km): all but one of the 26 landmark meshes resolved there have a single LOD level, so
  there is no coarser form to hold; `Bones_Town_Ribs`, the only one with two, holds level 0 because its nearest placement is 26 000 away. The request passes `distance - radius` as the placement distance and the decode takes six radii off it, which for a mesh with levels and a giant
  radius would clamp to the finest level until `Rebalance` coarsens it (10 s): **Unknown**, since no landmark mesh of the base game has levels to show it. Texture mip streaming for landmarks was not measured separately.
  The 90 landmarks are walked every frame (`upd-objects` unchanged, 0.15 ms). Faithful (`--faithful reach|all`, or `--landmark-distance 0`) builds no list, so its draw order and pictures do not change.
  Starting without Meitou reach builds no list and no slider; `F8` later moves the distances but not the landmarks.
- **Pictures, Faithful** (**Verified**): ten parity views with `--faithful all` against `C:\Temp\base-87c7857\faithful`: max 0 in all ten, before the landmark code and after.
- **Pictures, Meitou** (**Observed**; old = this build with `--faithful reach --range-large 12000 --range-medium 5000`, i.e. master's defaults; new = the new defaults; pairs in `C:\Temp\agent-landmark\meitou-old|new`, diffs next to the new ones):
  mean difference / pixels over 12 / max: forest 13:00 3.24 / 8.7% / 157, hub 13:00 2.89 / 9.8% / 177, zone 14,30 13:00 1.96 / 6.2% / 108, portnorth 13:00 0.15 / 0.3% / 205, rock 13:00 0.09 / 0.1% / 118, and at 02:00
  forest 0.55, hub 0.48, zone 14,30 0.38, portnorth 0.03, rock 0.03 (all under 0.7%, max 39). What changes: the horizon is populated. Forest: the rock stacks and hoodoos on the far cliff (large foliage meshes
  between 12000 and 50000, impostors beyond 4000) stand on it where the old picture shows bare terrain; Hub: trees and bushes in the middle distance where there were dark empty flats, and the far ridge carries
  vegetation; the terrain at 16 px is coarser at a distance (silhouettes of far ridges differ by a texel or two). Close views (rock, port north) differ in the far background only. The ten views show no landmark (none
  within 150000 of them is in view); a landmark picture (Bones_Town_Ribs from 35 km, `--at -1018,77000 --yaw 20 --pitch 3 --distance 35000`, with and without `--landmark-distance 0`, mean 0.38, max 113) shows
  the rib cage and a tower on the horizon only with landmarks (`C:\Temp\agent-landmark\try\ribs_lm.png` / `ribs_nolm.png`).
- **Benchmark** (The Hub, `--radius 2 --fly-benchmark 3600 --fly-pipelined`, `MEITOU_PASS_STATS=1`, RTX 4070, other agents' viewers on the same GPU so the run-to-run spread is about 0.1 ms GPU and 0.3 ms CPU;
  each configuration twice, the table gives both, then the isolating runs once):

  | Configuration | GPU frame mean (ms) | frame p50 / CPU p50 (ms) | upd-objects (ms) | objects colour: instances, k tri | VRAM peak (MB) | object meshes unloaded / reloaded |
  | --- | --- | --- | --- | --- | --- | --- |
  | master's defaults (`--faithful reach --range-large 12000 --range-medium 5000`) | 1.80 / 1.79 | 2.1 / 1.9 (both) | 0.16 / 0.15 | 81 / 62, 85 / 64 | 3080 / 3078 | 0 / 0 |
  | new defaults, landmarks 150000 | 2.33 / 2.24 | 2.8 / 2.6, 2.7 / 2.5 | 0.15 / 0.15 | 145 / 129 (both) | 4209 / 4273 | 0 / 0 |
  | new defaults, `--landmark-distance 0` | 2.34 / 2.29 | 3.4 / 3.1, 2.6 / 2.4 | 0.20 / 0.15 | 137 / 125, 140 / 127 | 3974 / 3974 | 0 / 0 |
  | new reach, old foliage ranges | 1.66 | 2.1 / 1.9 | 0.14 | | 3633 | 0 / 0 |
  | old reach, new foliage ranges (`--faithful reach`) | 2.33 | 2.8 / 2.6 | 0.15 | | 3654 | 0 / 0 |

  Reading: the new defaults cost about **+0.5 ms GPU (+28%), +0.6 ms CPU p50 and +1.1-1.2 GB VRAM** on this flight, all of it from the foliage ranges (50000 / 12000: more impostors and meshes resident, foliage textures
  582 to 773 MB, impostor atlases 95 to 176) and the object range (object textures 261 to 593 MB); the new reach alone is cheaper on the GPU than master (1.66 against 1.80, the 16 px terrain) and costs
  +0.55 GB. The landmarks cost no time that shows on the Hub flight (GPU 2.33/2.24 against 2.34/2.29; 8 landmark instances a frame, +4k triangles, `upd-objects` the same) but **+0.24-0.3 GB of video memory**
  (object meshes 131 to 206 MB, object textures 593 to 752 MB: the huge meshes' textures). Around Ashland, where a dozen are in
  view (`--at 95000,108000 --fly-radius 20000`): 68 against 71 instances, +78k triangles of 445k, 0 unloaded / 0 reloaded either way (the GPU means 3.42 and 4.56 ms are terrain streaming noise there).
  **No reload churn**: 0 object meshes unloaded or reloaded in every run at the default idle time; with `MEITOU_UNLOAD_IDLE=3` on a 40000 circle (every place revisited each 17 s, so unloading is legitimate) landmarks at 150000 /
  400000 / off give 155 / 155 / 179 unloaded and 155 / 131 / 123 reloaded: no landmark-specific reloads (at 400000 every landmark is always in range, and the reloads are the same as without).
- **Integrated GPU** (owner runs the viewer on an integrated-GPU laptop, about 30 fps at the old defaults): the new defaults cost more there. The measured cost on the RTX 4070 is above; it is the foliage ranges that carry it. Nothing
  integrated-specific was changed (no iGPU defaults); the impostor budget already drops to 5% / 256 MB on an integrated GPU (8.16, **Unknown** on real hardware), and `--faithful range` or `--range-large 12000 --range-medium 5000`
  returns the old foliage cost.
- **Notes on the numbers**: "master's defaults" is this build's binary with `--faithful reach --range-large 12000 --range-medium 5000` (the Faithful parity at 0 px shows the switch changes nothing else), not master's executable.
  The benchmark's `sizes` line now reports max 1773, not 38181: the landmarks left the zones and `SizeSurvey` walks the zones only. `WorldObjects.Describe()`'s counters (buildings, features, layouts) also count the one whole-world
  layout of `Collect`, so the `objects` log line is inflated in Meitou. The collection took 0.7-0.9 s on the workers in quiet runs and 5 s in one run with other viewers using the machine; zone layout waits for it.
- **Open**: (a) the 90 landmarks are full meshes (most have one LOD level, the biggest, Skylink, 7 MB), not impostors, so a very large one is drawn with all its triangles: not measured separately. (b) Rock landmarks
  (`Patagonia_RockSlab01`, `FeatureBluff001`, `Mafic_HugeRockSlabs`, `CLiff_Curved`, `ROCK-Arch_Type_01`) are probably terrain-mode map features, drawn through `TerrainRenderer.DrawMeshes` one draw each (not checked per record):
  that mesh path overlaps what the rock impostor work changes. (c) About 55% of the bounds probes fall back to a whole read; cause not examined (cost is under a second). (d) `F8` at run time moves the distances but never builds or removes the landmark list, so runtime-Faithful is not
  pixel-identical to a Faithful start; the gate covers the start mode. (e) VRAM is the driver's budget per process; under other processes' load the run-to-run budget swung 3.4-11.5 GB, and a 95% watch abort (`MEITOU_VRAM_KILL`) hit one default-range run at a 3456 MB budget.

### 8.20 Weather particles (2026-10-08)

`ParticleRenderer` ([formats/particle-universe.md](formats/particle-universe.md)) is a plain `LegacyProgram` pass in the style of the water: one
instanced quad strip per technique, the instances (64 bytes: four vec4 at vertex locations 1 to 4, per-instance rate) written by the CPU
simulation straight into `ctx.Frame.Constants` (a `Transient`: no stall, no persistent buffer) and bound with `BindVertexBuffers`; blend,
texture and depth flags from the particle material per draw; a guest segment recorded after the water in the near depth slice, only when some
group has a particle (so a clear weather records nothing and the parity views stay at 0 px). Particles nearer than the slice's near plane
are drawn with a projection of their own and no depth test (`WorldFrame.Draw`). Colour writes only: the scene target's alpha is the
characters' mask.

Part two (2026-10-08): the pass draws every unit of every group (weather groups and the map placers, culled by distance and frustum, sorted far
to near) and, before a unit's particles, its fog volumes (a second small program: a quad at the sphere's nearest point, the ray-sphere chord in
the fragment shader, depth-tested; a full-screen quad without depth test when the eye is inside; since 2026-10-08 replaced by the game's
sphere formula in every world shader, docs/formats/fogfeatures.md). The simulation of the units runs on the thread
pool from the end of `Update` to the start of `Draw`. Instances are built in a managed array and copied once into the transient constants.
The placers draw whatever the weather, so a clear-weather picture over an Ashlands volcano is no longer 0 px against the old reference; elsewhere
(no placer in the active range) nothing is recorded, as before.

**Reactive mask (TAA and FSR/DLSS).** Particles write no motion vectors and the history keeps ~90 % of the pixel behind a thin streak, so rain
nearly vanished on a moving camera (checked with `--orbit-step 0.4` on `Heavy_Rain deadlands`). `PostProcess.RunUpscale` draws the frame's
particle quads again (`ParticleRenderer.DrawCoverage`, the same buffers and matrices, additive, no depth test) into the motion target's alpha
(the TAA's reactive channel; the shader takes half of it) or, with a vendor upscaler, into the `post reactive` R32F target (cleared when there is
no water); the fragment writes `clamp(30 · alpha, 0, max)` (max 2 for the TAA, 1 for the reactive texture), brightness × alpha for non-alpha
blends. **Observed**: the streaks are clearly stronger than without it but still a little softer than with no temporal pass. Hidden particles also
mark their pixels (no depth test): those pixels are a little less stable. The same pass in `--faithful` (no upscaler) does not run.

### 8.21 Upload steps and the driver's memory calls (2026-10-10)

*In short: in the fast flight the render thread's "upload" stalls were mostly not the uploads. They were the .NET collector pausing the thread
inside a copy, the driver's memory calls (`vkAllocateMemory`, `vkFreeMemory`, `vkCreateImage`) made inline under the allocator's lock, an
8 MB shore field written in one frame up to four times a second, and atlas levels written in one piece. The memory calls now run off the
render thread, the big writes are cut into slabs, and every queue defers a step whose kind has been costing more than what is left of its budget.*

**How it was found.** `MEITOU_STREAM_LOG=1` now also prints, for every slow step and slow foliage update, where the thread's time went
(`UploadProfile`, `src/Meitou.Rendering/Gpu/UploadProfile.cs`: per-thread ticks of the frame begin, the fence wait, a new or freed memory block,
a staging chunk, the copy into staging, image and buffer creation and the collector's pauses since the previous step; a line
`alloc new|freed block ... on a worker|main thread` for any block call of 1 ms or more). **Observed** (instrumented master e814965, fast flight,
command below, 2026-10-10):

- *Collector pauses land in whatever step runs.* A grass page step that copied 0 bytes took 22.5 ms, 22.4 of them a GC pause; another 15.2 ms
  with 15.0 of pause. In the new build 12 of the 25 slow foliage updates in six runs are pauses of 8 to 80 ms. The process allocated 25 to
  31 GB in a 3600-frame run (2.0 GB of it on the render thread); `ShoreBake` alone made about 44 MB per bake in the large object heap, about
  a third of everything.
- *Driver memory calls.* A 64 MB `vkAllocateMemory` took 0.1 to 1 ms in the usual case with outliers of 4 to 52 ms; `vkFreeMemory` of an
  emptied block 0.5 to 5 ms (one of 33 ms; 50 ms on a worker in the new build). `Free` released emptied blocks and `Allocate` created them
  while holding the allocator's lock, so a slow call also held up every other thread's allocation. Each impostor bake created three render
  target images (`impostor bake target`); in the new build's profile a bake start that still has to (a class not among the kept three) shows
  `CreateImage` of 0.4 to 10 ms.
- *Whole levels in one step.* An impostor atlas level was one step: up to 9 MB (a 3072² BC1 albedo), 2 to 3 ms of copying into staging alone.
  A foliage update's `uploads` column (the queue plus the impostor atlas uploads and the bake step) reached 20 to 34 ms against its 2 ms budget.
- *The shore field.* One 8 MB RG32F image per rebake, written in a single step (4 to 6 ms), 180 times in the 3600-frame flight.
- *Frame begin.* Not in the flight: while loading or settling, the first step of a texture queue pays `BeginFrame` (the slot fence, deferred
  deletions, the bindless apply): 11 to 26 ms, in `FrameBegin`.

**What changed** (all in the streaming and upload code; the job bodies on the workers are untouched):

- `GpuAllocator`: a block is created *outside* the lock; once a pool has missed twice, its next 64 MB block is made ahead on a thread-pool
  worker (`SpareBlocks`), so a pool that keeps growing no longer calls `vkAllocateMemory` on the render thread; an emptied block is released from
  the bookkeeping at once and `vkFreeMemory` runs on a worker; out of memory first gives the spares back and retries.
- `StepCosts` (`src/Meitou.Rendering/StepCosts.cs`): an exponential average (rate 0.25) of what steps of a kind have cost. `UploadQueue.Run`,
  the foliage update's upload loop and `WorldTextureCache.Pump` always run the first step of a call (so a queue never starves) and start a
  later one only while the budget has not passed *and* it is expected to end within the budget plus 1 ms. A heavy kind therefore goes first in a
  call of its own instead of on top of other work, and one stall (a collection) is forgotten after about six steps. The 1 ms slack is
  deliberate: a first version without it deferred steps of a typical size half a step early, which cuts the throughput of a saturated queue;
  its runs showed grass pages missing near the camera more often (below), though the spread between runs is as large, so that is a guess.
- `ImpostorTextures`: a level is written in slabs of whole block rows of about 1 MB (`SlabBytes`, `StepsOfLevel`), and the foliage's atlas
  loops stop after 2 ms (`UploadMsPerFrame`) besides the 6 MB byte budget. `ImpostorBaker` keeps the render targets of its last three
  (grid, frame size) classes (`RentTargets`/`ReturnTargets`) instead of creating and freeing three images per bake.
- `ShoreField`: see [render-water.md](render-water.md) "Shore distance field": the cause of the 180 uploads and the fix (a field with no
  waterline in it is one texel; the rest is laid out on the worker and written 256 rows a frame into a recycled texture).
  `ShoreBake.Scratch` pools the bake's arrays.

**Result.** **Observed**, 2026-10-10, Release, 1600 x 900, RTX 4070 shared with other agents' viewers (so noisy; judged on counts and
maxima, base and new runs alternated, the baseline built from the same commit e814965; seven base runs, eight new):
`MEITOU_STREAM_LOG=1 MEITOU_BENCH_SKIP=60 meitou-viewer.exe --world --at -60564,-45142 --distance 1400 --pitch 25 --size 1600x900 --fly-benchmark 3600 --fly-speed 150 --fly-radius 60000 --log-spikes --particle-prewarm 0`
The first five new runs were made before the 1 ms slack (above), the last three with the committed build.

| 3600-frame flight | base (7 runs) | new (8 runs) |
|---|---|---|
| allocated, whole process / render thread | 25.7 to 31.3 GB / 2.03 to 2.06 GB | 17.6 to 22.6 GB / 0.55 to 0.67 GB |
| gen2 collections; pauses in total | 9 to 15; 100 to 711 ms | 5 to 9; 83 to 322 ms |
| `slow ... step` lines (a step over 3 ms) | 3 (a 21 ms texture step, two 6 ms `map slab` steps) | 3 (7.5 and 15.7 ms, both a collector pause; a `map slab` of 4.1 ms, 2 MB copied slowly) |
| slow foliage updates (over 8 ms), all runs | 26; 24 blamed on uploads (the `uploads` column, which includes the impostor stage), three of 34 ms | 32: 17 inside a collector pause, 12 in the impostor bake step, 3 other (grass layout, meshes); the queue's `steps` column never over 0.6 ms outside a pause |
| largest slow update per run, runs without outside load | 15.4 to 34.4 ms (5 runs) | 9.5 to 24.2 ms (6 runs; the 24.2 and 16.8 are pauses) |
| shore field | 180 uploads of 8 MB, 8 MB in one frame | 53 uploads (2 MB a frame over 4 frames) and 131 one-texel fields |
| frame time, runs without outside load (p99 / max / frames over 33 ms) | 22.9 to 24.3 / 34.0 to 52.6 ms / 1 to 7 | 22.7 to 27.2 / 31.8 to 52.6 ms / 0 to 12 |

The slow updates did not go away; their kind changed. In the base build the long ones were the `uploads` stage (the pause cannot be told
from the upload there, the base has no attribution; the instrumented base showed pauses of 15 to 22 ms inside steps that copied nothing). In the
new build the upload steps are short and what is left is a collection landing in some step (17 of 32) and the impostor bake's CPU. The frame
time does not change visibly: the flight is bound by the GPU (p50 about 11 to 13 ms, CPU about 5 ms). A run is spoiled when another viewer
takes the card: one new run had the driver's budget fall to 5.1 GB (`vram guard ... paused`, 94 frames over 33 ms) and two base runs had
maxima of 190 and 301 ms; those are not in the frame-time rows. The pop-in lines are not comparable to better than noise: grass pages missing
within 1500 units in 0 to 93 frames in the base runs (0 in five of seven), and in the new ones 0 to 262 (154 in the spoiled run, then 262, 0, 95
and 53 before the slack; 12, 0 and 0 with it); the workers are below normal priority and starve when the machine is busy.
Pictures: `image-diff` of `--screenshot` (Port South `--at 89750,-93430 --distance 1500 --pitch 15 --size 1280x720 --upscaler off --no-particles`,
and the flight's start view `--at -60564,-45142 --distance 1400 --pitch 25`) against the same-commit baseline, final build: mean 0, max 0 for both.

**Not changed, still open.** (1) The collector's pauses (10 to 80 ms) are the biggest single source of long steps left; they belong to the
allocation work (the remaining render-thread allocation is 0.55 to 0.67 GB a run). (2) The impostor bake: a step costs 3 to 4 ms of CPU per
frame by design (rows recorded per step), `vkCreateImage` shows 8 to 10 ms when a new (grid, frame size) class appears, and recording a row
took 13 ms once without any memory call or pause in the profile (`slow bake record ...` lines; the mesh upload at the bake's start and the
read-back were not slow in the two runs that have those lines) and 25 to 45 ms twice in one loaded run before those lines existed. **Unknown**: what in `RecordRow` takes
that long (a first-use pipeline or descriptor is a guess). (3) Terrain height windows are 4.5 MB per swap (about 200 in the flight) and object
and foliage textures reach 12 to 13.5 MB in one frame; they run under a time budget and were not split further. (4) Settling and loading
still pay `FrameBegin` (11 to 26 ms) in the first step; it is a one-off per load, not a flight stall. (5) The timing test
`ShoreFieldTests.Full_size_bake_time` ([Bench]) fails at 112 to 132 ms against 100 on this shared machine; the bake itself is unchanged.
### 8.22 Allocations while travelling: fewer and cheaper collections (2026-10-10)

The travel stutter notes (docs/viewer.md, "What makes travelling stutter") had GC as one of three causes: at `--fly-speed 150` the workers allocated 24 GB a minute, nine
generation-2 collections ran, and single blocking pauses were 14 to 25 ms. This round cut the allocations; the numbers below are the check.

**Method.** `MEITOU_JOB_STATS=1 MEITOU_BENCH_SKIP=60 meitou-viewer.exe --world --at -60564,-45142 --distance 1400 --pitch 25 --size 1600x900 --fly-benchmark 3600 --fly-speed 150
--fly-radius 60000 --particle-prewarm 0`, Release, RTX 4070, Ryzen 5 5600X (12 threads), 4-heap server GC, warm caches; `--particle-prewarm 0` keeps the particle stall of the other fix out of it.
`MEITOU_ALLOC_STATS=1` (new, `tools/Meitou.ModelViewer/AllocationLog.cs`, from the runtime's own event stream) prints every collection (generation, blocking or background, reason, time
suspended, the frame it fell in, the heap sizes after it and what each generation promoted) and the sampled allocation by thread and type, with the large-object heap (LOH) and pinned object
heap (POH) marked. `MEITOU_JOB_STATS` sees only `BackgroundWork` jobs: pool threads (the shore bake's `Parallel.For`) and the render thread are in the second tool only. **A run counts only
when the VRAM guard had the whole 11,451 MB budget and never entered pressure** (another viewer on the card cuts the budget, the streaming then loads and reloads far more, and the
numbers double: the same baseline gave 27 to 30 GB and 11 to 14 generation-2 collections that way); the rest of the table is runs that passed this test, base and new interleaved.

**What the collections were** (**Observed**, baseline profile). About 80% of the bytes were large-object-heap arrays (21.2 of 24.9 GB). Each time the LOH's allocation budget ran out
the runtime started a background generation-2 collection (reason "large"; 9 of them in the flight), whose two short suspensions cost 0.5 to 8 ms. The blocking pauses were the other
kind: a generation-0 or -1 collection that copied 47 to 80 MB of survivors, 13 to 20 ms each (four in the flight). The survivors were not in-flight work but what the streaming keeps
*resident*: a foliage zone's 66 KB ground heights, its 16 KB density maps and the record arrays of its instance groups are all under the 85 KB large-object threshold, so each was
allocated in generation 0, copied to 1 and again to 2, and died tens of seconds later.

**What was done** (all in `Meitou.Data` / `Meitou.Rendering`, commits on this branch):

| Allocator (baseline, MB a flight) | What it was | Now |
|---|---|---|
| shore bake `ShoreBake.cs` (about 7,400 incl. 1,470 on the render thread; invisible to `MEITOU_JOB_STATS`) | six full-size scratch arrays and a fresh interleaved field every bake | scratch arrays kept between bakes (`Scratch`, `Take`/`Give`), the field made interleaved on the worker and returned after upload (`ShoreGrid.Rent`/`Release`): 12 MB, render thread 13 MB LOH |
| zone layouts `FoliageRenderer.cs` job (3,357) | the cache read built a 56-byte `FoliageInstance[]` per zone, a `GroupBy` and then 96-byte `FoliageInstanceRecord`s, from a file read into a new array | `FoliageLayoutCache.TryLoadGrouped` + `FoliageWorld.LoadGrouped`: file in a pooled buffer, two passes straight to exact-size record arrays; 930-980 MB |
| texture, terrain-layer and impostor-atlas reads (`WorldTextureCache.cs`, `TerrainTextures.cs`, `ImpostorCache`, 1,790 and 615 MB; the atlas load and refine jobs, 1,770 and 1,830 MB) | the whole DDS / atlas read, then the top mips thrown away (the top mip is 75% of a chain) | `DdsReader.ReadKept`: file read from the first kept level; `ImpostorAtlas.Read(stream, firstLevel)`: the skipped levels inflated into one scratch buffer (still checksummed); 930-1,080 and 560-620 MB; the atlas jobs only 17% less per refine (8.1 to 6.7 MB), none per load |
| `Textures.cs` view cache | a boxed `ComponentMapping` key per lookup, 130 MB a minute on the render thread | swizzle packed into the key tuple: 0 |
| resident zone data (heights, density maps, record groups) | small arrays promoted by every blocking collection | `ZoneArrays.Uninitialized`: pinned object heap, never copied |

**Result** (**Observed**, 3,600 frames, whole process, runs that passed the test above; "pauses" is the sum the runtime reports; the collection counts are cumulative as
`GC.CollectionCount` gives them, so gen0 counts every collection: `6 / 5` with 4 of generation 2 is two blocking collections of generation 0 or 1; (A) = with `MEITOU_ALLOC_STATS=1`):

| Run | gen2 | gen0 / gen1 | pauses | allocated | render thread | frames over 20 / 33 ms |
|---|---|---|---|---|---|---|
| baseline `0834d0d` a | 8 | 12 / 9 | 122 ms | 23.7 GB | 1.90 GB | 89 / 13 |
| baseline b | 9 | 13 / 10 | 104 ms | 24.2 GB | 2.02 GB | 134 / 2 |
| baseline c (A) | 9 | 13 / 10 | 96 ms | 24.9 GB | 2.03 GB | 108 / 5 |
| baseline d (A) | 10 | 15 / 11 | 103 ms | 25.3 GB | 2.03 GB | 122 / 0 |
| baseline e (A) | 9 | 13 / 10 | 110 ms | 23.8 GB | 1.90 GB | 81 / 4 |
| new, before the pinned heap, a | 3 | 6 / 4 | 88 ms | 12.8 GB | 0.55 GB | 108 / 23 |
| new, before the pinned heap, b | 3 | 6 / 4 | 110 ms | 12.4 GB | 0.29 GB | 124 / 11 |
| **new** a | 4 | 6 / 5 | 51 ms | 13.3 GB | 0.55 GB | 61 / 1 |
| **new** b | 4 | 6 / 4 | 47 ms | 12.7 GB | 0.42 GB | 63 / 1 |
| **new** c | 4 | 6 / 5 | 43 ms | 13.6 GB | 0.42 GB | 88 / 0 |
| **new** d | 4 | 6 / 5 | 49 ms | 13.8 GB | 0.42 GB | 78 / 2 |
| **new** e (A) | 4 | 6 / 5 | 46 ms | 12.9 GB | 0.42 GB | 127 / 24 |
| **new** f (A) | 4 | 6 / 5 | 54 ms | 12.3 GB | 0.29 GB | 46 / 0 |
| **new** g (A) | 4 | 6 / 5 | 59 ms | 13.7 GB | 0.42 GB | 131 / 1 |

Allocation is down 48-50%, the render thread's from about 2.0 GB to 0.3-0.55 GB, generation-2 collections from 8-10 to 3-4 a flight (about 2.7 a minute of the flight itself,
not the 2 aimed at), the blocking collections of generation 0 or 1 from 4-5 to 2 (the baseline's were 10 to 27 ms, the new build's 8 to 21 ms), the sum of pauses from 96-122 ms to 43-59 ms
(the pinned heap is the half of that: 88 and 110 ms before it). The `worst` lines of the new runs are particles, water and `gpu-wait` (the other fixes' causes) and the collections' own
frames (`GC pause 20.5`, `6.0`). No pause under 5 ms: the background collections' two suspensions are 0.5 to 6.6 ms, the blocking ones 8 to 21 ms, and those are not proportional to the
promoted bytes (28 MB took 20.8 ms, 78 MB took 19.5 ms in the baseline), nor to the time to stop the threads (0.0 to 4.0 ms in the instrumented runs).

**Outliers, not explained.** Two more runs of the final build, both with `ALLOC_STATS`, are left out of the table because of one long pause each: a blocking generation-0 collection of
66.9 ms (106 ms of pauses all told), and a background one whose second suspension took 124.2 ms, 69.5 ms of it waiting for the threads to stop (140 ms all told; that frame also waited
118 ms for the GPU). The second ran while `dotnet build` / `dotnet run` of mine were going on the same machine, the first while other agents' work may have been; the seven runs in the table
(new a to g, four of them, c, d, f and g, after these two and with nothing of mine running) had none, and none of the five baseline runs had. A pause that is mostly threads taking 70 ms to reach a safe point looks like starvation of a
below-normal worker (the thread that triggers a collection suspends the others) on a busy CPU rather than anything the changes do, but it was **not isolated**: if it shows up on a
quiet machine, `ALLOC_STATS` now prints the stop time of every pause to start from.

**Picture check** (**Observed**, `--world --town "The Hub" --radius 2 --distance 3000 --pitch 20 --size 1600x900 --screenshot x.png`, and the same with `--radius 3 --distance 9000 --pitch 15`,
base `0834d0d` against the final build with its own re-keyed layout cache, `meitou-tools image-diff`): the two base pictures of a view differ from each other by mean 0.0062 and max 9
(near) and mean 0.0118 and max 3 (far) out of 255, no pixel over 12 (the viewer is not bit-reproducible run to run: streaming order and temporal history); base against new gives mean 0.0067 / max
10 and mean 0.0110-0.0120 / max 3-4, so the same noise, with the same zones, meshes and grass pages in the start-up lines. Not checked: a flight's frame-by-frame pictures.

**Not reached.** Generation 2 is still about 4 a flight (target 2 a minute): the LOH is still 9.6 of 12.3 GB, and what is left is the impostor atlases (`FoliageRenderer.Impostors.cs`
refine and load jobs, 3.2 GB in about 680 jobs of 3-7 MB, a refine is a whole reload at another mip level), the DDS reads (1.0 GB), the terrain height windows (0.95 GB) and layer
decodes (0.6 GB + 0.4 GB), `BuildingLodMesh` (0.7 GB) and the grass pages (1.4 GB in 10,000 jobs, small arrays awaiting upload). They all end in an upload step, which is the other
agent's code (`UploadQueue`, `Uploader`, the atlas upload): pooling them means returning the buffer after the last upload step has run, and the level arrays would be power-of-two sized,
which `ImpostorTextures.BytesFor` and the upload read as `Length`. The impostor refines are also the most behaviour-bound churn (450-700 jobs a flight as the plan changes with the
camera): fewer would need hysteresis in the plan, which changes pictures. What the blocking collections of 8 to 21 ms spend their time on is **Unknown** (not the copied bytes, not the thread stop).

**GC settings tried** (**Observed**, not clean runs: a second viewer shared the card, VRAM budget 6.3-7.4 GB, so only the same build under the same conditions is comparable):
`DOTNET_GCgen0MaxBudget=0x6000000` (96 MB a heap, 384 MB over the four) gave, in three runs, 4-6 generation-2 collections, 10-14 collections in all and 108-203 ms of pauses; the
same build without it, same conditions, had 3-6, 7-16 and 104-231 ms. No difference worth a setting, which fits the survivors being the resident set, not work in flight (a smaller
budget only moves the same copying to more collections). Not adopted. `System.GC.Gen0MaxBudget` is honoured from `runtimeconfig.json` (**Verified** with a test app that prints the
collection counts), `System.GC.Gen0Size` is not. The 32 MB budget and 8 heaps (`DOTNET_GCHeapCount=8`) were queued but never got a quiet card.

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

docs/render-shadows.md's ~4 µs per draw was measured in real frames. There each draw touches different objects with cold caches, the vertex block
is copied whenever a vertex uniform changes (8 KB for mesh programs), and the renderer's own C# work counts too. The spike runs hot caches
and one program, so its figures are lower bounds for both paths. **Estimate**: in real frames, step P costs ~0.3-0.6 µs per draw and the
native model ~0.1-0.2 µs, against ~2-4 µs today: roughly 5-10× fewer CPU microseconds per draw for step P, and 15-30× for the native model.

**Actual against the spike (measured after the step-O hot-path work, 7.1; forest still camera, Release).** The floor was measured again in the
repository's own API: a test (not committed) on a headless device, 20,000 draws a frame cycling 64 meshes, median of 7 frames, validation off,
tiered compilation off.

| Path (floor, hot caches) | µs per draw |
| --- | ---: |
| `CommandList`: 7 vertex buffers + index buffer + 128-byte push + `DrawIndexed` (the foliage mesh shape) | 0.12 (0.16 before `b0badb7`) |
| the same through Silk.NET's `Vk` directly / through cached `vkGetDeviceProcAddr` pointers / plus `SuppressGCTransition` | 0.11 / 0.09-0.10 / 0.09 |
| `CommandList` push + draw; draw only | 0.07; 0.03 |
| `interop.Bindless(id)`, 100 textures | 0.024 (0.12-0.20 before `3068dfc` and `5bcb8d1`) |

| In real frames (loop / with the segment's share) | µs per draw |
| --- | ---: |
| foliage colour meshes, step O | 0.43 / 0.66 (1.45-1.66 / 1.9-2.2 before) |
| foliage depth meshes, step O | 0.50 / 0.65 (1.26-1.57 before) |
| grass, step O | 0.26 / 0.27 (0.98-1.12 before) |
| objects colour, step P | 1.12 / 1.20 |
| terrain patches colour, step P | 0.17 / 0.18 |

So the spike's 0.18-0.3 µs is reached by draws whose data is contiguous and shared (grass, terrain patches), and missed by a factor of about 2 by the
foliage meshes, whose remaining cost is cold per-mesh data (vertex-buffer bind with seven bindings, index buffer, texture indices), not the API
(7.5, "the step-O hot path"). Silk.NET's own overhead is about 0.005 µs a call (its vtable lookup and cast), and the managed-to-native
transition is within the noise (`SuppressGCTransition` measured 0.087-0.114 against 0.091-0.118 µs).

### 9.2 Per pass (estimates)

- **Draw overhead.** The forest still camera drew 977 draws per frame after the TERRAIN-mode fix (docs/render-shadows.md). At ~4 µs that is ~3.9 ms
  of draw overhead per frame. At step P (~0.4 µs) it is ~0.4 ms, and with the native model (~0.15 µs) ~0.15 ms.
- **Foliage** (owner's forest figure 8.7 ms CPU). The parts are per-instance culling (docs/render-shadows.md: ~1.2-1.4 ms per cascade with the
  candidate cache, and more in the main slices), the per-batch upload, and the draws. Step P removes most of the draw part. A2 removes the
  culling and the upload: the CPU keeps the zone and group walk (hundreds to a few thousand groups) and ~100-300 indirect draws per view.
  **Estimate**: under 1 ms for foliage across all views, from 8.7. The remaining zone walk could also move to the GPU if it shows up.
- **Shadows** (owner's figure 6.1 ms CPU). The foliage casters' share goes as above. Terrain patches (~0.9 ms for cascade 3, docs/render-shadows.md)
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

## Owner decisions

The owner's answers (2026-10-06) to the questions this design left open. They are numbered as the questions were, and the text above
refers to them as "owner decision N".

1. **Hardware floor** (1, 2.6): required. The device must have descriptor indexing (`runtimeDescriptorArray`,
   `descriptorBindingPartiallyBound`, `descriptorBindingVariableDescriptorCount`, `descriptorBindingSampledImageUpdateAfterBind`,
   `descriptorBindingUpdateUnusedWhilePending`, `shaderSampledImageArrayNonUniformIndexing`) and multi-draw indirect
   (`multiDrawIndirect`, `drawIndirectFirstInstance`); without them the program refuses to start with a message naming what is missing.
   No push-descriptor fallback for the native model. `drawIndirectCount` and `shaderDrawParameters` (wave 3b) are enabled where present
   and reported in `GpuFeatures`.
2. **Exactness** (3.3, 5.6): parity-port steps (step P, A1, C1, wave 4) must give 0 differing pixels. The later native-model and GPU-driven
   steps (step O, A2, C2) may differ by at most 1/255 per channel, and every such case is documented (view, pixels, cause from the draw
   log or the cull verifier).
3. **Switch names** (5.7): `range` (size-based ranges), `impostors`, `occlusion`; separate `Enhancement` switches, Meitou default.
4. **Draw distances** (1, 5.7): adjustable settings, not fixed numbers. Tab-panel sliders per size class, for example large (ruins,
   wrecks), medium (junk, rocks) and small (litter, bushes), with defaults around 5000 / 2500 / 800 units (12000 / 5000 / 800 since 8.10, 50000 / 12000 / 800 since 8.19), plus command-line options. This
   is for agent A's `range` switch in wave 3b; the wave-2 API needs nothing for it (the per-view range cap of 5.3 takes the setting).
5. **Image layouts** (4.4): stay in GENERAL; revisit after phase 8.
6. **API steward** (7.1): the foundation agent stays on through wave 3 and lands API additions. No transfer queue in wave 2 (this also
   answers question 8).
7. **The viewer's mesh and character modes** (7.2): the character renderer is ported later, by agent C. The plain mesh viewer may be
   retired later, not by the foundation. *Replaced 2026-10-06 by DECISIONS 23: phase 8 dropped the mesh and character viewer (stage 3
   step 1); [character-viewer.md](character-viewer.md) keeps how it worked for the later native character renderer.*
8. **Transfer queue** (2.2): not in wave 2 (decision 6).
9. **Documentation drift** (7.7): "eight" parity views corrected to "ten" in docs/engine.md and DECISIONS 2.
10. **Step P everywhere?** (3.2): step P stays mandatory for post-processing (E) as for A to D; it is optional for overlays (F).
11. **`LegacyProgram` after phase 8** (8.9, "What is left" 1): kept for now; moving its users to the native shader model is a later step.
12. **The GL vocabulary** (8.9, "What is left" 2): kept in `GlConventions`; no rewrite to Vulkan's own types for now.
13. **FSR in the parity gate** (8.9, Gate): FSR is not deterministic run to run, so an `--upscaler fsr` view passes when it differs from
    the base by no more than the base viewer differs from itself (max 27, mean about 0.01, no pixel over 12 when measured); every other
    view stays at 0.
14. **DECISIONS 7 and 18**: 7 is marked superseded by 22, and 18's "IGl stays" sentence is marked replaced by 22 and phase 8.
15. **Retained staging memory** (8.9, VRAM): freed above a limit. The `Uploader` stages through a per-slot allocator of its own
    (`GpuFrame.Staging`, `upload staging N`) instead of the frame constants; its reset frees the regular chunks beyond 4 (32 MB) and
    beyond those the slot's last cycle used, which is safe because no descriptor set refers to staging. The frame constants keep their
    chunks (descriptor sets are made for them).

## Foliage draw batching: measured and not built (2026-10-09)

Question: would one shared vertex / index buffer, a per-draw record read by `gl_DrawID` and one `vkCmdDrawIndexedIndirectCount` per state bucket
(instead of one indirect draw per mesh part and LOD level, each with its own pipeline, vertex / index binds and push constants) pay? Measured first
(RTX 4070, `--view swamp` and `--view hub`, 2560x1440, TAA, `--no-particles`, 600 frames, GPU-bound: render thread 2.05 ms against GPU 6.50 ms).
The bench counts now carry `hist <view> draws <n> inst`: the cull's indirect draws per frame by instance count (0 / 1-2 / 3-15 / 16+).

- **Draws (Observed)**, per frame. Swamp colour 160 / 44 / 119 / 40 (363 in the cull's tally, 543 submitted); shadow cascades summed 68 / 103 / 43 / 10; reflection 7 / 3 / 20 / 1.
  Hub colour 82 / 35 / 90 / 50; shadows 41 / 31 / 20 / 10. So 44% of the swamp's colour draws are empty and 12% have 1-2 instances.
- **Price of a draw (Observed)**: experiment, not kept: the mesh job's draw loop repeated 20 times with every draw's arguments zeroed (same pipeline binds,
  vertex / index binds and push constants, 0 instances). Foliage GPU 1.99 to 2.30 ms, shadows 0.33 to 0.51: **about 0.016 ms per sweep of 543 empty draws
  (0.03 microseconds a draw)** in the colour view, 0.009 ms in the cascades. State changes between draws cost the GPU almost nothing.
- **Where the foliage stage goes (Observed)**, swamp, GPU ms: stage 1.99; with the mesh draws skipped 1.14 (grass, impostors, the rest: **57%**); with the mesh
  draws scissored to 32x32 pixels (vertex, setup and draw cost remain, no fragment work) 1.39. So the meshes are 0.85 ms, of which 0.60 is fragment work (alpha-tested leaves)
  and **0.25 ms vertex, setup and draw overhead at most**. Shadow cascades: meshes 0.17 ms of 0.33, 0.10 of it without fragments. Reflection: 0.05. Hub: foliage 0.53 ms all told.
- **CPU (Observed)**: recording one sweep of 543 draws is about 0.12 ms (20 sweeps added 2.45 ms to the frame), roughly 6 sweeps per frame in the swamp (colour, up to 4 cascades,
  reflection), on the job threads; the render thread's foliage is 0.69 ms colour + shadows 0.60. The frame is GPU-bound, so this does not move the frame time.
- **Verdict**: the ceiling for batching is the 0.25 + 0.10 + 0.05 ms above, and the state-change part of it is about 0.05 ms across all views. Below the 0.1 ms bar; **not built**.
  Better targets, if the foliage stage is to shrink: the grass / impostor part (1.14 ms, 57% of the stage) and the mesh fragment work (0.6 ms: overdraw of cut-out leaves).
  **Unknown**: whether many small draws cost more on another GPU (an integrated one, a different driver) than on this one.
