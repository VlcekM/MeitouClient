# Metal backend

A second native path for the renderer, next to Vulkan. Status: **content-testing feature-complete**
(`--renderer metal`, macOS): the game's own GLSL, the engine's own compiler, the engine's own uniform
block, the scene, shadows, Meitou water, skinned characters, the HUD and the faithful panels. The
interactive camera/selection, title, New Game, save/load, audio and the shared trade/dialogue/build/editor
interaction layer are in (a new game opens the character editor). Remaining work is polish/optimization, not
content access.

Bindings: `SharpMetal 1.1.0` (IsaacMarovitz; Metal, MetalFX, QuartzCore, the Objective-C
runtime). `Silk.NET.GLFW` has no `glfwGetCocoaWindow`, so the `NSWindow*` comes from the same
brew `libglfw.3.dylib` the Vulkan path loads.

## What works

| Mechanism | Result |
|---|---|
| Device, `CAMetalLayer` on the GLFW window, clear and present | works (in the game) |
| The engine's GLSL -> SPIR-V (shaderc) -> MSL (spirv-cross) -> runtime compile | works |
| The engine's uniform block (std140), uploaded unchanged | works |
| Storage buffer (std430), dynamic indexing | works |
| Runtime-sized texture **and** sampler tables, dynamically indexed (tier-2 argument buffer) | works |
| Compute pass writing an rgba16f storage image, sampled back | works |
| Barriers between passes | **not needed** - Apple silicon tracks hazards itself (the engine's `Access` and `GpuDependency` say what must be ordered; the backend decides) |

A picture of the sky alone, without the world, has the raw linear colours: the post chain (the tonemap and the grade the Vulkan
path's picture goes through) is a later pass, so the two windows compare **structure** here — the gradient's shape, the sun's
place, the clouds — not the tone. The pixel diff against Vulkan becomes the check when both draw the same passes.

## Tessellation (evaluated, not shipped)

Metal has no tessellation control stage, so a 2026-10-24 MEP-4 prototype wrote explicit triangle factors and used
post-tessellation vertex stages. CPU factor generation cost ~103 ms/frame for terrain and ~25 ms/frame for rocks (3 fps
at the Hub); a GPU compute pre-pass got the same view to ~35 fps against the ~54 fps MEP-3 baseline. The deferred-colour
path also exposed `NotSupportedException: no Metal primitive for PatchList` when factor allocation failed and the job
fell back to an indexed draw under a patch-topology pipeline. The prototype was removed: `HasTessellation` is false,
terrain keeps the flat/parallax path, and the tessellation toggles stay hidden. Revisiting it needs a hard performance
budget, near-range/quality caps, a per-patch flat fallback and full visual/soak validation.

## The shader chain

The engine's own `GlslProgramCompiler` produces the SPIR-V; `Silk.NET.SPIRV.Cross` 2.23.0 (with
its `.Native` package) turns it into MSL **in process** - no subprocess, no `spirv-cross` CLI.
The options that matter:

| Option | Value | Why |
|---|---|---|
| `MslVersion` | 30000 | Metal 3 (macOS 13+); tier-2 argument buffers with runtime-sized arrays need it |
| `MslPlatform` | `Macos` | |
| `MslEnableDecorationBinding` | on | the MSL resource indices equal the SPIR-V binding numbers, so a Metal backend mirrors the Vulkan binding scheme instead of carrying a per-shader mapping table |

Three facts about the translation:

- **The engine's legacy shader path goes to MSL as it is.** spirv-cross splits a combined image
  sampler into a `texture2d<float>` and a `sampler` at the same index - exactly Metal's model -
  so no GLSL rewrite is needed for the fixed samplers. Only the runtime-sized bindless table
  needs the rewrite (below).
- **The uniform block keeps its std140 layout.** The offsets the engine's `ShaderReflection`
  reports (`uInverseViewProjection` @0, `uSkySun` @64, ...) are the MSL struct's offsets, so the
  bytes the engine's `GlUniforms.Scatter` writes are the bytes a Metal constant buffer takes.
- **One library per stage.** spirv-cross names both stages' entry point `main0` and their
  interface structs `main0_out`, so two stages in one source collide: the vertex function comes
  from one library, the fragment function from the other.

On Metal the stages have their own binding tables, so the Vulkan scheme (the fragment stage's
bindings at 32, to keep them apart from the vertex stage's) is not needed: the fragment stage's
bindings start at 0.

**The one binding table per stage.** Metal maps a resource to its MSL index by the binding
decoration alone, set ignored, so two sets with the same binding number would land on one
`[[buffer(n)]]` (the terrain's set 0 and set 2 both have a binding-0 block; the default block sits
at set 1). Every resource's binding therefore flattens to base + set × 8 + binding, in both places
that must agree: the SPIR-V before the translation (`SpirvPatch.RemapBindings`) and the runtime
binds (`MetalShaders.Flatten`, called by the command list's binds). The vertex stage's base is 16 —
the indices below it are the vertex buffers', at Vulkan's own slot numbers (the pipeline's
`[[stage_in]]` descriptor) — and the stage's top index, 30, belongs to the push constants (this
spirv-cross has no option for them, so `MetalShaders` moves the block from the `[[buffer(0)]]` it
assigns; the block is found by the engine's name for it, `Push`). Both stages' tables hold 31
entries, so the scheme fits the engine's sets (bindings within a set stay under 8).

## The bindless table

`BindlessTable` declares seven **combined** image samplers. Metal has no combined sampler, and
spirv-cross refuses the case outright ("Argument buffer runtime array currently not supported for
combined image sampler"). The way through is **separate textures and separate samplers**, which the
backend rewrites into the GLSL before compiling (the engine's compiler already rewrites sources).

The verified model (the MSL that spirv-cross makes, the pipeline reflection, and the host's
`--table` draw), on Metal 3:

- the table's set becomes a **tier-2 argument buffer** (`MslArgumentBuffers` +
  `MslArgumentBuffersTier` 2, in the **constant** address space); the shader takes it as
  `constant spvDescriptorSetN& [[buffer(n)]]` — n is the set's number, and the backend moves it to
  a fixed free index (28) so it cannot collide with the ordinary bindings, in both stages. The
  device-address-space variant (`CompilerMslSetArgumentBufferDeviceAddressSpace`) samples nothing
  that is put in by hand; the constant one does;
- the set's arrays are **sized** (the table's capacities): a sized array is a real member
  (`array<texture2d<float>, 2048>`) with a real offset in the buffer. spirv-cross's *unsized*
  arrays are a trap: they come out as one-element members aliased onto each other ("Overlapping
  binding"), which cannot hold distinct texture and sampler handles;
- **the members' offsets are sequential from 0, one entry (8 bytes) per element**; the `[[id]]`
  decorations in the MSL do not move them (the pipeline reflection reports `textures2D` at 0).
  The order is the members' declaration order: the texture arrays in kind order, then the
  sampler arrays. All seven kinds are declared;
- **per-entry samplers work**: the rewrite declares a parallel sampler array per kind
  (`sampler samplers2D[capacity]`), and every use constructs the pair with the same index —
  `sampler2D(textures2D[i], samplers2D[i])` — so GL's per-texture sampler state survives. A
  sampler only has a `gpuResourceID` valid in an argument buffer if its descriptor sets
  `SupportArgumentBuffers` (Metal's default is no), or sampling reads nothing;
- the runtime writes the handles into one shared buffer per frame slot: a texture's handle is
  `MTLTexture.gpuResourceID`, a sampler's `MTLSamplerState.gpuResourceID`, at the members'
  offsets (`MetalBindlessTable`). A resource read only through the argument buffer must also be
  told to the encoder (`useResource`), as `BindBindlessTable` does;
- **every** descriptor set of a program translated with argument buffers becomes one (spirv-cross's
  `MslArgumentBuffers` is global). A set's buffer members are `constant T*` pointers, so the
  runtime fills them with the buffers' **GPU addresses** (`MTLBuffer.gpuAddress` + offset; the CPU
  contents pointer is a different address on Apple silicon) at the flattened ids, and binds the
  set's buffer at the stage's set index (the vertex stage's `15 + set`, the fragment's `set`, the
  table at 28, the push constants at 30 — Metal's buffer table ends at 30, so the sets cannot be
  spread by 8);
- a stage drops the arrays it never reads, so the members' offsets differ per program (they are
  packed in declaration order): the runtime parses each stage's MSL struct and keeps one filled
  argument buffer per (layout, frame slot) (`MetalTableLayout`, `MetalBindlessTable.BufferFor`);
- a program whose fragment writes a second colour output (the terrain's GI albedo) needs the second
  attachment: the scene pass carries an `Rgba8ui` target (`MetalContext.Albedo`) and the pipeline
  declares it, or Metal drops the draws.

The rewrite (`MetalShaders.RewriteBindless` + `SpirvPatch.BindlessFetchImages`), verified by the
`--table` dump and `spirv-val` on both stages:

- capacities, in `BindlessKind` order: `[2048, 512, 128, 128, 128, 128, 64]`, all seven declared
  (the shadow kinds were added with the shadow step); the argument buffer packs them in declaration
  order, and `MetalTableLayout` reads each stage's offsets from its MSL;
- the float kinds are the separate arrays + sampler pairs above; the integer kinds have no
  separate type in GLSL, so they stay combined and sized (`usampler2D utextures2D[128]`) and the
  SPIR-V patch drops the sampler part: the variable's, the array's, each chain's and each load's
  types become the plain image's, and the stages' `OpImage` (100) extractions become
  `OpCopyObject` (83) of the already-plain image. `OpImage` = 100, `OpCopyObject` = 83, `OpPhi` =
  54 — and an instruction's word count includes the op word itself;
- the shadow kinds are the split depth image + compare sampler pairs (`shadowTextures`,
  `shadowArrays`), read by the same bindless lookups as the float kinds.

Two sharp edges:

- `MTLRenderPassDescriptor.ColorAttachments.Object(0)` returns a struct copy, so a value-type
  property (the clear colour) set on the copy is lost and the pass clears to uninitialised
  memory. Write it back with `SetObject`.
- The `CAMetalLayer`'s drawable size follows the view's bounds, and the view has no bounds while
  the window is being created: attach the layer and let the window lay out, or set the size from
  the window's size times the scale factor. Without it the drawable is 1x1 and the window shows
  one magnified pixel.

## The backend so far

- `src/Meitou.Metal/` - the backend (macOS only, outside the solution so the Windows build never
  sees it): `MslTranslator` (SPIR-V -> MSL), `MetalUniformBlock` (the engine's uniform block on
  the CPU), `MetalSky` (the spike's hand-driven sky), `MetalDisplay` (the window, the layer, the present).
- `src/Meitou.Metal.Host/` - the host: the sky in a window of its own.
- The game: `--renderer metal` (macOS only). The game boots without the Vulkan renderer, the
  simulation runs, and the sky is drawn by Metal. The sky is the game's **simple** sky (the
  colour model, no textures); the camera's own view and the clock's own sun.

The game reaches the Metal backend through a conditional project reference (the `MEITOU_METAL`
constant), so the Windows build compiles the game without any of it.

## The device behind the seam

`MetalContext` implements `IGpuContext` (docs/resource-layer.md), sized to the sky pass: every
operation a later pass needs first throws (`InvalidOperationException` naming it). With it:

- `MetalFrame` (the `IGpuFrame`): a slot ring of three (the layer's drawable depth) - one drawable,
  one command buffer and one constants ring per slot (a shared `MTLBuffer`, the CPU bump-allocates
  slices; unified memory, so no staging split for the constants).
- `MetalCommandList` (the `IGpuCommandList`): renderings (one colour + one depth attachment), the
  dynamic draw state (Metal's encoder state maps one to one: viewport, scissor, cull, winding,
  depth test/write/compare, depth bias, fill), `BindPipeline`, `BindProgramResources` and
  `BindSets`, labels, `Draw`, mip generation. **The stages have their own binding tables**: a
  set's slots bind straight onto the encoder at the SPIR-V binding numbers (the MSL is compiled
  with `MslEnableDecorationBinding`), so no argument buffer exists yet and none is needed - a
  texture at [[texture(n)]], its sampler at [[sampler(n)]], a block at [[buffer(n)]] per stage.
- The services: `MetalShaders` (the engine's compiler, fragment bindings at 0; the MSL per stage),
  `MetalPipelines` (an `MTLRenderPipelineState` per description; depth clip mode is the *encoder's*
  on Metal, set when the pipeline binds), `MetalSamplers` (Metal's sampler has no LOD bias; the
  description's is applied at bind, as clamps), `MetalUploads` (a batch is its own command buffer:
  blit copies from a shared staging ring, committed in order before the frame's own).
- Resources: textures (`MTLTextureDescriptor`, shared storage - Apple silicon's unified memory is
  the device-local memory), views (`NewTextureView`), buffers (shared `MTLBuffer`s), persistent
  sets (the layout and bindings recorded; the command list binds them out with dynamic offsets).
- The frame loop and the present are the context's (as `GpuContext.BeginFrame`/`EndFrame` are the
  Vulkan display's): the renderers draw into an offscreen Rgba8 backbuffer (the engine's format, as
  `VulkanPresenter` has), and the present is a **flipped copy into the drawable** - Metal's blit
  can do neither the flip nor the format, so a five-line MSL pass samples the backbuffer. The
  host's pass model (`BeginHostPass`) mirrors `GpuContext`'s.
- The frame's timestamps are the seam's `IGpuTimestamps` now (the Vulkan `QueryArena` implements
  it); Metal's hands out slots and reads nothing - Metal's GPU counters are a sampling-point model
  of their own (MTLCounterSampleBuffer, later if wanted).
- The backend constructs the engine's handles, so `Meitou.Rendering` grants `InternalsVisibleTo`
  `Meitou.Metal` (as it does to the viewer, the game and the tests).

What the sky-sized scope does not have yet (each member throws): compute, the bindless table,
indirect draws, dispatches, buffer/texture copies, in-pass clears, swizzled views, secondary
command lists. The vertex vocabulary is in: vertex and index buffers, indexed draws and push
constants (the flatten above), with the quad testbed in the host (`--tri`) and the same per-stage
binding scheme for the native model (`MetalShaders.Native`).

The host's `--shot out.bmp` reads the backbuffer back (a blit into a host buffer; a texture is not
CPU-mappable even in shared storage - `MetalContext.Readback`) and writes a BMP, so the sky's
picture is checkable without a screen shot: night is near black, dawn warm, noon bright.

## What the passes state

The renderers above `Gpu/` describe a draw in the engine's GL vocabulary, not in Vulkan's words
(docs/renderer-native.md "Synchronization and shaders"):

| What | The vocabulary | Vulkan's words it replaced |
|---|---|---|
| The draw state | `DrawState`, `BlendState` | `CullModeFlags`, `FrontFace`, `CompareOp`, `BlendFactor`, `BlendOp`, `ColorComponentFlags`, `PolygonMode` |
| The geometry | `PrimitiveType`, `VertexInput` | `PrimitiveTopology` |
| The targets | `AttachmentFormats`, `PixelViewport`, `PixelRect` | `Format`, `Viewport`, `Rect2D` |
| The shaders | `ShaderProgram`, `GraphicsPipelineDesc`, `ShaderReflection` | (already neutral) |

`GlConventions` maps the vocabulary to Vulkan (the front face flips, GL's 24-bit depth is a
32-bit float, the sRGB and the integer formats are named), and a second backend maps it to
itself. The passes that still speak Vulkan are the ones that touch the resources themselves:
the descriptor sets, the images and the buffers.

## What the second backend still needs

- The **physical** sky: the game's DDS textures (the starfield, the planets, the clouds) as
  Metal textures, and the atmosphere's own uniforms.
- The rest of the passes: the terrain, the objects, the post chain.
- A seam for the passes above `Gpu/`. `Gpu/` has no backend abstraction: two abstract classes
  (`Uniform`, `RecordJob`), and eight types carry Vulkan handles (`Image`, `ImageView`,
  `Sampler`, `Buffer`, `GraphicsPipeline`, `ComputePipeline`, `CommandBuffer`, the frame slot).
  The neutral descriptions (`GraphicsPipelineDesc`, `PipelineLayout`, `ShaderProgram`) are
  already backend-neutral, and the passes above `Gpu/` only use the neutral types: the sky pass
  needs `Pipelines`, `Globals`, `Samplers`, `Uploads`, `LodBias` and eight `CommandList` calls
  (`BindPipeline`, `Draw`, `SetViewport`, `SetScissor`, `SetRaster`, `SetDepth`,
  `SetDepthBias`).
- The **bindings and the resources**: the passes now hold the engine's own handles (the samplers,
  the views, the textures, the buffers, the sets), state a draw's resources as a flat binding list,
  and record into an `IGpuCommandList` (the commands in the engine's words). What is left in the
  passes is the GI's acceleration structure and the vendor upscalers' command buffers, both
  Vulkan-only on purpose.

## The road to a Metal frame
Each step ends with something visible; the order is the cheap one first.

| Step | Work | What it gives |
|---|---|---|
| Done | The sky on Metal, in the game (`--renderer metal`) | the strategy: the shaders, the uniforms, the present |
| Done | The state and the formats in the GL vocabulary | the passes state what they want in neutral words |
| Done | The handles (the samplers, the views, the textures, the buffers, the sets) and the binding list | the renderers hold no Vulkan object |
| Done | The synchronization in the engine's words (the accesses and the dependencies) | the backend decides the barriers |
| Done | The command list as the engine's interface (`IGpuCommandList`) | the renderers state commands, not Vulkan's |
| Done | The context's interface (the resources and the services) | a backend can be another one |
| Done | The frame's interface (`IGpuFrame`), the pipelines (`GpuPipeline`) and the programs (`GpuProgram`: the index and the engine's compile result — sources, SPIR-V, reflection, sets) as the engine's handles | the frame, the pipelines and the programs are backend-neutral; the Vulkan objects live in the backend's tables (the device's, the pipeline library's, the shader library's) |
| Done | The sky path on the seam: the recording model (`BeginGuest`/`BeginNative`/`Interleave`), the device facts (`IsIntegrated`, `UniformBufferAlign`), the services behind interfaces (`IGpuShaders`/`IGpuPipelines`/`IGpuSamplers`/`IGpuUploads`), the frame's constants ring and timestamps (`WriteConstants`, `Timestamps`), the persistent sets (`CreateSet`) — and `SkyRenderer` with its helpers and the legacy program's flush holding no Vulkan type | the sky pass runs on `IGpuContext` alone; a second backend implements the seam and the sky draws |
| Done | The Metal device: `MetalContext`, `MetalFrame`, `MetalCommandList` and the four services behind the seam ([resource-layer](resource-layer.md)), sized to the sky pass (everything else throws); the offscreen backbuffer and the flipped present pass; the host's `--shot` readback | a Metal device that can run any pass whose vocabulary the command list has; the sky runs on it (the host's window, the frame loop, the real `SkyRenderer`) |
| Done | The game's `--renderer metal` on the device: the real `SkyRenderer` with the world's sky textures and switches, the scene's host pass as `SceneHost` opens it, the session's hour and camera; `--screenshot` (one picture, then out) | the sky in the game, verified: the gradient follows the camera and the hour, the clouds and stars are the world's own; the tone is raw linear until the post chain's step |
| Done | The vertex vocabulary: vertex and index buffers, indexed draws, push constants; the stages' one binding table flattened (base + set × 8 + binding, the vertex buffers keep Vulkan's slots, the push constants the top index); the native-model programs (`MetalShaders.Native`) | the terrain's own draw vocabulary, verified by the host's quad (`--tri`: the four corners' colours and the centre's interpolation land exactly) |
| Done | The bindless table's MSL shape, verified: tier-2 argument buffers, sized arrays, per-entry sampler arrays | the model the backend's table runtime builds to (below) |
| Done | The flatten's kind refinement: a vertex module's buffers shift by the vertex base, its images and samplers flatten from 0 (`RemapBindings` classifies by the SPIR-V storage class, `Flatten` takes the kind) | the terrain's vertex-stage table samplers fit the stage's sampler table |
| Done | The bindless table's GLSL rewrite (`--table`'s seven kinds through the real path): the float kinds become separate sized texture and sampler arrays, the integer kinds stay combined and sized with their samplers dropped in SPIR-V, the shadow kinds use split depth image and compare sampler arrays; verified: both stages' patched SPIR-V pass `spirv-val`, the argument buffer's members are `texture2d/texture2d_array/texturecube/texture2d<uint>/texture2d<int>` arrays then `sampler` arrays | the table's MSL shape at the capacities |
| Done | The table's runtime (`MetalBindlessTable`: the journal, a shared argument buffer per frame slot, the GPU resource ids, the sampler flag, `useResource`) and `BindBindlessTable` at buffer 28 | the terrain's textures can bind; verified by the host: red → blue across the quad, the update's swap visible |
| Doing | The terrain in the game: the Vulkan-free widening (`IGpuContext` through `TerrainRenderer`, `NativeFrame`, `TerrainTextures`), the programs, the textures, the pass (depth + GI-albedo attachment), the CDLOD draws at the camera. **Open:** the draws are recorded with a valid pipeline, buffers and a trivial shader, yet produce no fragments (depth and albedo both empty); the same program shape draws in the host, so the difference is still unexplained — see the note below | the world appears |
| Then | Compute and the post chain (the tone parity) | the picture is the game's, not raw |
| Then | The objects, the characters, the foliage, the shadows, the water, the UI | the whole frame, and `--renderer metal` becomes the macOS default |

### What kept the terrain's fragments away (fixed)

The patch draws were recorded but produced nothing. Metal's validation layer names the causes
(`MTL_DEBUG_LAYER=1`):

- **An argument buffer struct's members are laid out by declaration order, not by `[[id]]`.** The
  id names the binding (the flattened index), but the entry a member reads is its position in the
  struct. The table's layout already followed that rule; the descriptor sets did not — the runtime
  wrote set 0's view block at entry 19 (its id) while the shader read entry 0 (the frame block),
  so the view matrix was garbage and every patch vertex landed outside the clip volume. Sets are
  now filled per stage by the position parsed from the stage's MSL (`MetalSetLayout`), one
  argument buffer per stage and set, at the stage's set index.
- **Metal checks the pushed bytes against the MSL block's size.** The terrain's push block is 24
  bytes of members, 32 in the shader (std430 rounds the struct to 16), so `PushConstants` now
  pads to the 16-byte alignment before `SetVertexBytes`/`SetFragmentBytes`; a short push fails the
  draw's length check.
- A stage that does not read a set has no struct for it: the bind skips that stage rather than
  binding a buffer nothing reads.

The terrain draws now (patches, culling, the depth and GI-albedo targets) with its real material
and atmosphere (verified with the `--screenshot` comparison at the same camera and hour): the
layer arrays, the biome maps and the physical sky's atmosphere cubes are all sampled through the
bindless table. Three facts this step settled:

- **The terrain quadtree is in world coordinates.** The Metal host placed its camera at
  `(o.X ?? 0, o.Z ?? 0)` and looked at the world origin, not at `scene.Focus` where the region is
  loaded; it now uses the focus (or `--at`), as the Vulkan camera rig does.
- **The diffuse layer array is BC3 (DXT5), the normal array BC1.** `MetalConventions` mapped only
  BC1, so the diffuse array's allocation threw, `TerrainTextures` fell back to no array, and the
  terrain sampled the stand-in (black albedo — the picture was dark blue from the sky's ambient).
  DXT5 now maps to `BC3RGBA` and DXT3 to `BC2RGBA`, both as 4x4 blocks of 16 bytes.
- **The frame inputs are the Vulkan path's.** The host runs the weather scheduler
  (`WorldWeather`) and `WeatherSurfaces`, passes the sky `fogDistance + 3*max(eyeY, 0)`, sets
  `camera.ViewDistance` from the light's fog distance, `render.TerrainPixelScale` from the render
  height, and streams the fine heights and biome maps with `TerrainStreamer` (without it the
  overlay/colour windows stay at state 1 and the terrain shows only the ground colour).

What the Metal picture still lacks is later steps' work, not this one's: the post chain's tone
mapping and exposure (step 2), the shadows (step 4), the objects, foliage and water (steps 3, 5,
6) and the UI (step 8).

**Compute (step 2's backend half).** A compute program is translated like the native model's
(every set an argument buffer, push constants at <c>buffer(30)</c>) and the GLSL's
<code>local_size_x/y/z</code> is its threadgroup; <code>Dispatch(x, y, z)</code> therefore means
threadgroups, as Vulkan's <code>vkCmdDispatch</code> does. The pipeline is an
<code>MTLComputePipelineState</code>. A storage image's argument-buffer member is its texture's
<code>gpuResourceID</code> and the encoder marks it <code>Read|Write</code>. <code>Order</code>
ending the compute and blit encoders is the seam: a command buffer orders its encoders and Apple
silicon tracks the hazards inside one. <code>FillBuffer</code> clears only (Metal fills with one
byte; no caller asks for another value). Verified with an 8x8 dispatch into an RGBA8UI storage
image through set 0's argument buffer, read back as (2, 3, 99, 255).

**Shadows (step 4's first slice).** The seam now carries the host-pass handoff and the device capabilities
the shadow pass needs (`BeginHostPass`/`SetPassViewport`/`EndHostPass`, `DepthClamp`, `RecordsSecondaries`), so
`ShadowPass` and `TerrainShadowMap` run on `IGpuContext`; Metal opens no secondaries and clamps depth. The Metal
host draws terrain, objects, foliage and characters into the cascades before the scene, and the receivers sample the
atlas. Two backend facts this surfaced:

- **A new Metal render encoder carries no pipeline state**, so the redundancy filter must be reset in
  `BeginRendering`: the multi-pass shadow atlas otherwise drew a later pass without `setRenderPipelineState`.
- **A depth-only pipeline has no colour attachment at slot 0**; the pipeline builder skips it when the format is
  `None`.

The noise texture uploads through an upload batch (the seam's `IGpuUploads` has no direct `Write`).

**Objects and buildings (step 3).** `WorldObjectRenderer` and its caches (`WorldTextureCache`, `ObjectMeshCache`) take
`IGpuContext`. The seam gained what their uploads and draws use: `HasRayQuery`, `EnsureFrame` and `StandIn2D` on the
context, batches from `IGpuUploads.Begin` (with `GenerateMips` on their command list) for the mesh and texture
slabs, `IGpuFrame.WriteConstants` for the per-instance matrices, and `GpuProgram.Info` for the vertex reflection.
The Metal host makes the renderer with the Vulkan distances and LOD bias, settles it on the first open frame (the
still's meshes and textures all resident), calls `SetView`/`Update` per frame, draws it after the terrain, and
draws its depth into the cascades. Metal vertex formats `Rgb32f`, `Rgba16f` and `Rgba8ui` were added for the
building meshes. The buildings stand where Vulkan draws them; characters, foliage and the UI remain (steps 5, 7
and 8).

**Foliage, grass and rocks (step 5's first slice).** `FoliageRenderer`, `FoliageGrassGpu`, `NativeProg` and
`BufferArena` take `IGpuContext`; the seam gained `FramesInFlight`, `VideoMemoryBudget`, `DeferDelete` and `Finish`.
The Vulkan-only pieces stay out of the Metal path for now: `FoliageGpuCull` (the compute cull), `FoliageGrassGpu`'s
kernels and `ImpostorBaker`/`ImpostorDraw` (the billboard bakes). The host runs the CPU cull (A1), the CPU grass
path and the terrain-shader rocks, settles them on the first open frame, updates and draws them after the objects,
and adds them as cascade casters. Two backend facts this surfaced:

- **Metal rejects GL's stride-0 constant vertex attribute.** A disabled attribute (the foliage shaders' bone
  inputs) reads the generic value from a repeating 16-byte-step dummy array instead, with a stride of 16: every
  vertex fetches the same (0, 0, 0, 1). `IGpuContext.ConstantVertexAttributes`/`DummyVertexArray` carry the
  distinction; Vulkan keeps the stride-0 binding.
- **A non-indexed draw may start at another instance.** The grass draws a blade range with `firstInstance` (the
  first blade in the arena); `Draw` passes it as Metal's `baseInstance`, and `firstVertex` is supported too.

**The far billboards (impostors).** `ImpostorBaker`, `ImpostorTextures` and `ImpostorDraw` take `IGpuContext`; the
seam gained `IGpuFrame.PreFrame` (Metal's frame list, recorded before its passes), `IGpuReadback` with
`CreateReadback`/`FrameCompleted`, and `CopyTextureToBuffer`. Metal implements the bake's halving blit as a fullscreen
sampling pass (Metal's blit cannot scale), copies the row into a shared readback buffer, and reports a frame completed
when the drawable ring has turned. **Re-enabled after the frame-slot fence:** the atlas path was previously disabled
because some atlases drew as red/black bands or a gray full-screen card. With slot reuse now fenced, a far-foliage still
loaded 146 atlases (244 MB) cleanly and a 60 s auto-camera run used 172 atlases (up to 256 MB) at 47 fps with no
validation errors. `MEITOU_METAL_NO_IMPOSTORS=1` remains as a diagnostic fallback; the Metal settings again expose
"Far impostors" and the impostor distance sliders.
Two Metal fixes the bake surfaced:

- **Argument buffers are padded to 16 bytes.** A one-member set was allocated 8 bytes and failed Metal's validation;
  the set size is rounded up.
- **A sub-rectangle upload still needs the destination level's full row stride.** The terrain's overlay and colour-map
  tiles are repacked with that stride and their x offset in the upload batch; BC5's 16-byte block was also missing
  from the texel-size table.

The GPU culls remain the step's rest (Metal takes the CPU cull and the CPU grass path).

**The composite (the first slice of the chain).** The Metal scene draws into an RGBA16F target and
`PostComposite` runs the game's own tone-map shader (`PostProcessShaders.Composite`, through
`LegacyProgram`) into the backbuffer. The fullscreen pass flips `vUv.y` on Metal: Vulkan's negative
viewport stores the scene flipped into its targets, Metal's do not (the present flips the picture
instead). The exposure is the game's own too: the luminance at 256² (16 taps), its nine-level mip
chain, and the 1×1 RG32F adaptation against the previous frame inside the sky's band
(`uAuto` 1; a still picture uses the instant blend). Both ping-pong adaptation targets are seeded
before smoothing starts, and the pass reads the previous frame's output rather than the target it
writes; the old uninitialized/self-read history made the title flicker dark/bright.
The upscalers/TAA and `PostProcess`'s widening
to the seam remain step 2's rest. At the same view the picture's mean channels are within a few
percent of the Vulkan screenshot's with GI and shadows switched off (98/78/72 against 96/71/67);
the large per-pixel differences left are the missing shadows and GI (steps 4 and the game loop).

### The plan for the remaining steps (kept exact, for resuming cold)

**Step D — verify the table in the host** (`--table`): the quad samples `textures2D[uIndex]` and
`textures2D[uIndex + 1]`, mixed by u across the quad, with the other kinds' index 0 black dummies
(the rewrite's five kinds run). Verified shots: bottom-left red, top-right blue, centre
(127, 0, 128); after `Update` swaps the two entries at frame 3, the picture swaps. The runtime is
`MetalBindlessTable` (the Vulkan table's journal contract, one shared buffer per frame slot, ids by
`TableTextureId`/`TableSamplerId`).

**Step E — the terrain in the game.**

1. The Vulkan-free widening (as `SkyRenderer` got in step 2): `TerrainRenderer(GpuContext` →
   `IGpuContext`, same for `NativeFrame` and whatever `TerrainProgram` wraps;
   `DeviceBuffer.Create` gets an `IGpuContext` path (it already wraps `ctx.CreateBuffer`);
   `gpu.Device.Limits.MinUniformBufferOffsetAlignment` → the seam's device fact
   (`UniformBufferAlign` exists on `IGpuContext`). Keep the Vulkan parity byte-for-byte: the
   widening must not change the Vulkan path's code paths, only their parameter types.
2. The game's Metal path (`GameHost.Metal.cs`): give the host pass a **depth attachment** (a
   per-slot depth texture at the backbuffer's size — the context owns it, resized with the
   backbuffer); the pass's state becomes `DrawState.Scene` (depth tested); the sky draws, then
   `terrain.Draw(viewProjection, eye, frustum, options, light)` with the light from
   `sky.Prepare`'s second return and a minimal `WorldRenderOptions`.
3. The terrain's boot in the Metal path: `TerrainTextures.Prepare(install, db)` (the CPU bakes),
   the heights (already in the ctor), the material's texture setup (find where the layers'
   arrays are created and bound — `TerrainRenderer`'s texture part), the constants set (set 2).
4. The patch draws (CDLOD) are the scope; the TERRAIN-mode meshes' indirect draws
   (`DrawIndexedIndirect`) come after — implement them then, or defer the meshes with a line in
   the doc.
5. Verification: the two-window comparison (the terrain's relief and layer colours, same
   `--time`/`--pitch`/`--distance`); the recipes all stay green: build 0/0, tests 424 pass /
   1 known fail, the Vulkan screenshot 0 differing pixels (warm runs; a cold first run may show
   the ~40 ±1 TAA edge pixels), the Metal game's sky shot unchanged.

**Step F — the remaining steps, in dependency order.** The terrain draws; what follows is breadth, not
research. Each step ends with the same gates: build 0/0, tests 424 pass / 1 known fail, the Vulkan
screenshot unchanged, and a Metal shot compared with the Vulkan one at the same view.

1. **The frame's inputs (small).** Give the Metal path the real `WorldFrame`/`FrameGlobals`: the
   atmosphere LUTs and textures, the sun and light, fog, and the camera's world framing. Today the
   terrain's frame textures are stand-ins, so it is dark. Ends when the terrain's colours and relief
   match Vulkan's at the same hour and camera.
2. **Compute (large).** `MTLComputePipelineState` and `MTLComputeCommandEncoder`, dispatches, compute
   sets as argument buffers, storage images/textures, then the post chain's passes: tone mapping,
   TAA, fog volumes, the HiZ pyramid, the GPU culls. `MetalCommandList.Dispatch` and the compute
   pipeline path throw today; they become the first work. Ends when the offscreen picture matches
   Vulkan's before the present.
3. **Indirect draws and the objects (medium).** `DrawIndexedIndirect(Count)` and
   `DrawIndirectCount`, buffer fills/copies, and the conversion between Vulkan's indirect argument
   layout and Metal's. The material and program path is already in place. Ends with the buildings
   and objects standing where Vulkan draws them.
4. **Shadows (medium).** The depth-only passes and the cascades; set 0's receiver and caster blocks
   already exist and hold zeros. Ends with the terrain's and objects' shadows.
5. **Foliage, grass, impostors (medium-large).** The GPU culls of step 2 feed the indirect draws of
   step 3; add the card masks, grass, LOD and the impostor bakes. Ends with the vegetation matching
   Vulkan's at the same view.
6. **Water (medium).** The reflection and refraction passes and the surface's compute, reusing steps
   2 and 3.
7. **Characters and skinning (medium).** The skinning buffers and the character draw list through the
   seam; the animation data already uses storage buffers.
8. **UI (started).** The HUD, inventory, stats, overview/map and menu draw on Metal through the shared
   `DebugOverlay`/`UiPainter`; the pointer/keyboard controls for those panels and the shared settings menu work.
   Title/save/new-game flows and the shared trade/dialogue/build/editor layer are wired; the pointer sprite and free
   camera remain.
9. **The physical sky (small).** `SkyRenderer`'s uniforms come from a Vulkan `GpuContext`; the
   atmosphere model (CPU) must become something both backends build their uniforms from.
10. **The full game loop (broad, mechanical).** The Metal game path is a minimal frame (sky +
    terrain). A full game widens the real `GameHost` render flow — objects, population, overlays,
    UI — to `IGpuContext`. Much of it is type-changing; the surface is large.
11. **Parity and the default.** The window comparison, tone and timing, resize and HDR, then
    `--renderer metal` becomes the macOS default.

Two names collide with Silk.NET.Vulkan's and are qualified where the file still imports it:
`InternalFormat` and `PrimitiveType`. Both collisions go away when the file sheds its Vulkan
imports.

The parity check for a step that changes the Vulkan path: the game's offscreen picture
(`--screenshot`, the same view) is byte for byte the same before and after. The temporal path
makes that check noisy at times: two runs of the *same* build can differ on about forty edge
pixels by one level (the TAA's history), so a step's picture is compared against several runs of
the baseline, not one.

## Where the port stands (resume here)

**Drawing on Metal today:** the sky, weather and atmosphere; the terrain (heights, biomes, the streamer); the
objects and buildings; the sun's shadows (terrain, objects, foliage and character casters); foliage meshes, grass
and the terrain-shader rocks (the CPU cull and CPU grass, not the compute ones); the foliage meshes and grass (the
impostor bakes and far billboards are disabled until their atlas is fixed); the scene's HDR target, the game's tone map,
auto exposure and a seam-only TAA; the faithful water;
skinned characters (the `--crowd` harness, the `--new-game` player squad and the population draw list; the town
population and navmesh now run in the live loop); the world's lamps and room lights; the weather's and map placers'
particles (the low-resolution targets are shared; the near-plane close pass is still Vulkan-only); the HUD and the faithful panels
(inventory, stats, overview/map, menu), with portraits, item icons and the panels' own pointer/keyboard controls;
the hover outline, move marker and navmesh debug overlay (Shift+F11).
`GameHost.Metal.cs` is now a playable loop: WASD/arrows, Q/E, wheel and middle-drag
move the strategy camera; edge scrolling, left click/box select, right-click move orders and the HUD keys work;
F7 hides the interface, F8 saves a screenshot, Ctrl+/ writes one frame's Metal capture (`.gputrace`), F11 lists the frame, F12 the fps line, Alt+Enter is borderless.
The shared settings menu is now wired on Metal too (F10; Display, Graphics, Interface, Controls, Advanced and Audio),
including the MetalFX preset (which owns the render scale), VSync, frame limit and borderless full screen, persisted in
`meitou.user.json`. The frontend shell is in: the staged Meitou title (or the faithful title image), New Game with the
start chooser, save/load slots and confirmations, the in-game Esc menu, the loading screen and sky transitions, and the
audio system (main theme, interface sounds, ambience, music and world emitters, with the saved volume sliders).
Still missing: the pointer sprite, the free camera and the cinema camera.

**Current Metal mode (2026-10-22).** Terrain textures, water, foliage, terrain shadows and cloud shadows are on.
Turn any of them off for diagnosis with `MEITOU_METAL_FOLIAGE=0`, `_WATER=0`, `_SHADOWS=0`, `_CLOUD_SHADOWS=0` or
`MEITOU_METAL_NO_TERRAIN_TEXTURES=1`; far impostors are on again by default. The former black/miscoloured terrain and the acid-pool
water GPU fault were the same Metal bug: `MetalUploads.Write` applied a sub-rectangle's x offset to both the packed
source row and the destination origin, shifting every map-window piece. Screenshots at `-16134,26409`, `-44047,250`
and `-12213,36572`, plus 15-second all-feature runs, are clean. Saved shadow settings now control shadows normally
instead of being forced off.

**Shared world construction and frame draw (2026-10-22).** `SceneHost` takes `IGpuContext`; the seam carries the
Vulkan secondary-pass staging (`BeginHostPassParallel`, `SetPassStage`, `EndHostPassParallel`) and `ClearDepth`, and
Metal's scene pass runs through the same `SceneHost` instead of a hand-rolled native segment. `WorldFrame.CreateGpu`
also takes `IGpuContext`: it builds terrain, textures, streamer, sky/weather, particles, lamps, objects, foliage,
water, shadows and the memory guard on either backend. The full `PostProcess` and GI are created only when the
context is Vulkan; the reflection pass and Meitou water are seam-neutral. Metal supplies its allocation sample for
`VramGuard` and uses `MetalPostChain` (`PostComposite`/`PostTaa` behind `IWorldPostChain`).

`WorldFrame.Draw` now runs through that interface and owns the shared frame: weather and sky preparation, streaming,
shadows, the scene host, the post-chain scene effects (fog volumes, shafts, SSAO), particles, the post chain,
and the finished-picture overlays. Metal's local scene/post loop is gone; its host keeps the title staging, the
interface and the present. Vulkan still uses `PostProcess`'s GI and far-depth slice; Metal's chain reports one depth
slice and no GI. The shared draw runs the reflection pass and low-resolution particles on both backends.

**Combat feedback and deterministic stills (2026-10-22).** `SetUpBlood` takes `IGpuContext`; Metal checks the blood
setting and makes the decal store and debris renderer before a loaded save's decals are read. `UpdateBlood` runs
before each `WorldFrame.Draw`, applying luckyren Mode, draining `World.BloodMarks`, ageing decals and moving debris.
The still preparation is shared (`PrepareStill`): `--ticks`, `--move-to` and the duel/title per-tick work run once
before the frame loop, and Metal then freezes the simulation while `--ui-panel`/`--ui-showcase` warm up and capture.
Vulkan keeps its own post/capture path. See [metal-content-testability-plan](metal-content-testability-plan.md).

**Current priority: the frontend shell and effects; optimization follows.** MetalFX now gives the frame-rate
headroom (Balanced 0.50 by default, Performance 0.40 spatial, Native fallback), so the next work is completing the
game rather than moving the benchmark. The clean Release window remains ~30-32 fps at 0.50, while the macOS Vulkan
reference is 36-40; Metal's GPU is already faster, so the remaining gap is CPU/present and cache/native retention.
The measurements, targets and acceptance gates stay in [metal-performance-memory](metal-performance-memory.md);
`--render-scale` and `MEITOU_METAL_TRACE=1` remain the comparison tools. The 2026-10-21 slice wired the real vsync
setting and drawable trace, released the map-tile/source caches, added the idle LOH trim, fixed the frame
autorelease pool and added the Metal settings/FX presets; the plan document records the numbers and what remains.

**What we do next.** The HUD, the main faithful panels, the title, New Game, save/load, the in-game menu, audio
and the shared trade/dialogue/build/editor interaction layer are wired. The remaining UI work is:

1. **Runtime acceptance for the shared screens.** Open a trade, a dialogue, the build menu and the character editor
   on Metal; check their state, pointer actions and layout against Vulkan. The host callbacks (build services,
   rocks, factions, tooltips, editor previews) now come from `ConfigurePlayerInterface`.
2. **Full UI input.** The panel keys (I/C, M/T/Y, Esc, F7) and pointer hover/clicks/scroll work; the loop now also
   feeds the camera, selection, box select, move orders, F8/F11/F12, F10 settings and Alt+Enter. The highlight key
   labels ground items and lights the signs. Still missing: the pointer sprite and the cinema camera.
3. **Still acceptance.** `--ui-panel <name>` and the full `--ui-showcase` panel set now go through the shared still
   preparation and Metal's frozen warm-up loop; verify the pictures and the `--ticks`/`--move-to` state.
4. Acceptance for the UI slice: the Hub's HUD and panels match Vulkan at the same size; every panel that does not
   need a modal simulation state opens; a resize re-lays them out; `MTL_DEBUG_LAYER=1` is clean.

**After the UI:** the character motion pass; the remaining post (`FogRate`, dynamic motion, GPU culls/grass);
resize and HDR/EDR; then the GI decision, parity, memory/tone and the macOS default. The Meitou water (waves,
shore, foam, reflection, refraction) and clouds are in.

**What 100% still needs.** Assuming 100% means the full game is playable through `--renderer metal` and matches
Vulkan at default settings, the remaining scope is:

1. **The rest of the real game loop (largest).** The first playable slice is in: camera bindings, edge/turn/zoom,
   left click and box select, right-click move orders, the HUD keys, F7/F8/F11/F12 and Alt+Enter. Still to do:
   the loop uses `WorldFrame.CreateGpu`, `SceneHost` and `WorldFrame.Draw` through `IWorldPostChain`; the host keeps
   the title staging and interface. Also warm-up, resize/fullscreen, HDR/EDR and the remaining present details.
   Title -> new game -> play, save/load/quickload, options, `ScreenAction`, the new-world rebuild, the loading
   screen, audio/UI sounds, the sky transitions, mesh/building/surface hits and floor stepping are wired.
2. **UI actions and screens.** HUD, inventory, stats, overview/map, menu, title, New Game, save/load and the
   in-game menu render and act; the shared trade/dialogue/build/editor screens and their host callbacks are wired.
   Still missing: the pointer sprite and a runtime pass over the shared screens (including the deterministic
   `--ticks`/`--move-to` stills).
3. **Rendering parity.** The Meitou volumetric fog; post (`FogRate`, full TAA with character/grass motion, MetalFX
   reactive mask); and the character motion/stage/hover passes.
4. **GI decision.** On this Mac Vulkan also reports `gi off: the device has no ray queries (VK_KHR_ray_query)`,
   so the local parity shots are no-GI; on a machine where Vulkan traces GI the target includes it. Either port
   `GiScene`/`GiProbes` to Metal ray tracing or make no-GI the documented macOS baseline.
5. **Performance and robustness.** GPU culls and grass kernels, VRAM/texture budgets, Metal timestamps, the
   intermittent `encoding in progress` validation assertion at the inland water view, and the known arm64
   `GoldenHashTests` failure.
6. **Parity and the default.** Window comparisons, tone/timing, resize/HDR, memory, then make `--renderer metal`
   the macOS default and remove the duplicate host.

**Practical order:** the frontend shell (title, New Game, save/load, audio) and the shared trade/dialogue/build/editor
interaction layer are in; next is its runtime acceptance pass and the remaining effects, then the
[performance and memory plan](metal-performance-memory.md) (same-window benchmark, present pacing, GPU culls/indirect,
cache/native lifetime, memory soak), water refraction, post/GI, parity and default. If no-GI is accepted as the
macOS look, the GI item leaves the target; the upscaler and timestamps are quality/performance work, not
correctness blockers.

**The next steps, in order.** Each ends with the gates below (build 0/0 and a Metal shot under
`MTL_DEBUG_LAYER=1`; tests and a Vulkan comparison only when the step changed shared code).

### 1. The Metal operations the rest needs — done, with two limits

**Done:** `DrawIndexedIndirect` (a loop of `drawIndexedPrimitives…indirectBuffer`; Metal has no multi-draw),
`Resolve` (a render pass whose colour attachment is the MSAA source and whose `resolveTexture` is the target),
`CopyBufferToImage`, `Blit`/`BlitLevel` (a blit when the levels match, a sampling pass over level views when they
scale), swizzled views (Metal texture views; every texture carries `PixelFormatView`), partial / in-pass
`Clear`/`ClearDepth` (a scissored fullscreen triangle), and `PushConstants` offsets (the stage's block kept and the
named range set in it). MSAA textures are `Type2DMultisample`; a clear pipeline names every attachment of the pass,
including the GI mask at colour slot 1. The Metal.Host `--table` and the game's scene pass ran all of the above under
`MTL_DEBUG_LAYER=1`.

**Limits:** `DrawIndexedIndirectCount`/`DrawIndirectCount` have no Metal count-buffer command. Every slot is issued;
the arguments at and after the count must be zero (a zero index / vertex count draws nothing). The GPU grass's order
kernel must zero its draw list when that path comes to Metal. Timestamps are still no-ops.

Keep `RecordsSecondaries` false: Metal records inline into one list.

### 2. The post chain's rest

`PostComposite` now runs exposure, the tone map and grade, the fog volumes, SSAO, FXAA and the heat haze on Metal.
`PostProcess` is still Vulkan-typed for the passes that remain. Port, in this order:

1. **TAA and the upscaler.** The **TAA slice is done** in `PostTaa` (seam-only): the game's velocity and TAA shaders,
   a render-size RGBA16F motion target, two RGBA16F histories, the same 8-phase `Jitter` on the scene projection and the
   pass, `uBlend` 0.1 and a reset on the first frame/cut. The Metal host now renders the scene jittered and composites
   the TAA output; the scene depth carries `Sampled`. MetalFX is behind `IUpscaler`: temporal at the preset scales
   0.77/0.67/0.59/0.50/0.40, with spatial as an explicit 0.50/0.40 alternative; the saved Metal settings choose it,
   balanced by default. Object motion (grass and characters) is shared through the post-chain seam. MetalFX gets a
   reactive mask from the water's analytic coverage only: weather-particle coverage made distant objects shimmer, so
   it is TAA-only (full- and low-resolution particle alpha). MEP-1 also added the shared sun-motion term and the Metal
   0..1 depth convention in the velocity shader. TAA also resets on a camera cut. Still to do: the remaining
   `PostProcess` passes (FogRate, GI resolve); FogRate is a performance-only difference on Metal (no VRS
   attachment).
2. **Fog volumes and light shafts.** The **fog volumes are now drawn on Metal** by `PostComposite` over the scene
   (`uFogVolumeData`, `uFogVolumeEye` and `uFogVolumeInfo` are published by `FogVolumes`). SSAO, FXAA, the heat haze
   and the light shafts (`LightShaftShaders` inject/integrate/scatter/apply) run there too. The shafts use the
   fragment integrate/scatter path; the compute scan is Vulkan-only. Still to do: the fog-rate target
   (`PostProcess.FogRate`), which the shafts and the volumetric fog read. Metal has no fragment-shading-rate
   attachment, so the FogRate path stays Vulkan-only unless a non-VRS fallback is added.
3. **The HiZ pyramid and the GPU culls** (`HizPyramid`, `FoliageGpuCull`, `FoliageGrassGpu`): `FoliageGpuCull`
   now works on Metal through `IGpuContext`, argument buffers and a separate pre-frame command buffer, and is live
   for meshes and TERRAIN-mode rocks. `FoliageGrassGpu` and `HizPyramid` remain Vulkan-only; grass still uses the CPU
   path. The detailed porting order and Metal indirect-draw constraints are in
   [metal-gpu-cull-plan](metal-gpu-cull-plan.md).

### 3. Water (plan step 6)

**Meitou water is on Metal:** `WaterRenderer` takes `IGpuContext`; `OceanWaves`, `ShoreField` and the shared
`ReflectionPass` are seam-neutral. Water uses the native model on both backends: its own `WaterConstants` block (set 2)
and the bindless table for the atmosphere, heights, biome maps, ocean/shore/river fields, reflection and refraction.
There are no direct fragment samplers, so the full shared `AtmosphereShaders.Functions` path is compiled; foam reads
`kenshiShadow` and the cloud-shadow tile. The reflection pass draws the mirrored sky, terrain, characters, objects and
foliage with the same 4x (device-capped) MSAA colour+depth targets and colour resolve as Vulkan; `--reflection-samples 1`
disables the path for an A/B. Refraction uses the shared half-res scene copy into a colour+sampled target; the faithful
surface remains the fallback when the Meitou switch is off. Swamp and desert water were accepted on 2026-10-24 (MEP-5).

### 4. Characters and skinning (plan step 7)

**The first character slice is on Metal.** `CharacterRenderer`, `CharacterContent`, `MorphArena`,
`ReflectedProgram` and `CrowdHarness` take `IGpuContext`; the bone palettes, materials, morphs and instances go
through `IGpuFrame.WriteConstants`, meshes upload through `IGpuUploads` batches, and `ReflectedProgram` binds
Metal's 16-byte dummy attribute arrays for a disabled input. The Metal host creates the viewer's `--crowd`
harness or the simulation's population draw list; `--new-game` starts the player's squad through
`population.StartPlayer`, and `DrawDepth` is called in the shadow cascades.

Verified: `--crowd 6` on open ground (`--at -50000,8000`, `--distance 180`) draws six skinned characters (30
parts, 52k triangles, 23 calls) with validation clean; `--new-game` at the Hub draws the player (4 parts, 4 calls).
A closer open-ground view shows the characters' cast shadows in the cascades. The Vulkan viewer's same
`--crowd 6` view draws the same six characters (26k triangles; its LOD picks a coarser mix). The Hub's town
residents are mostly indoors on Vulkan too (in a play test they were only in the bar and one tower), so the
outdoor still shows only the player; the `--crowd` harness covers the outdoor crowd. The squad portraits and item
icons are now drawn (the HUD and inventory shots verify them). Not yet: the character motion pass for TAA and the
hover/stage passes.

### 4b. Interiors and shadows

**The interior view is on Metal.** `UpdateInteriorView` now reads `ActiveObjects`, sets the building's
`InteriorView` (the viewed floor's parts and furniture, the floors above hidden), fills `aboveFloor` for the
character draw list, and feeds the HUD's `Floor`, `SurfaceMoveHit` and `ClosedBuildingHit` callbacks. PageUp/PageDown
and the HUD floor arrows step `viewedFloor`. `InteriorMaskRenderer` takes `IGpuContext`; Metal records it at scene
size with the scene's jittered near-view projection, then sets it on the terrain, objects and foliage for their
colour draws. The mask's `texelFetch` reads the same pixel grid as the scene, so no V flip is involved. Observed at
the Hub: entering a house shows its floor and furniture and hides the near walls and roof; its indoor characters
are drawn, and characters above the viewed floor are left out.

**Shadow casters now write the right depths on Metal.** The shared caster shader recovers the cascade matrix's
depth from `gl_FragCoord.z` with `uShadowBias.w`: 2 for Vulkan's −1..1 depth-clip-control mapping, 1 for Metal's
0..1 NDC (`IGpuContext.ClipDepthZeroToOne`). Before this Metal applied the Vulkan recovery, so every caster's depth
was shifted toward the sun: cast shadows were misplaced or missing and moved with the view. The Metal host now also
passes the sky's real sun height, the fog/atmosphere range caps, far-cascade coarse casters to terrain and objects,
the landmark shadow map, and the temporal shadow noise. The map's world-derived lookup also flips v on Metal: Vulkan's raw
render targets are bottom-up (its present flips the backbuffer once), Metal's are top-down, so the cascade and landmark
receiver matrices otherwise sample the maps vertically mirrored. The shadow map is still drawn before the scene, after the
interior view; upper-floor casters stay ([closed interiors](render-objects.md#closed-interiors)).

### 5. The UI (plan step 8)

The UI is the shared immediate-mode `DebugOverlay`/`UiPainter` over the finished backbuffer. It was already
backend-neutral once `DebugOverlay` took `IGpuContext` (`UiPainter` had no `GpuContext` types); the Metal host boots
`PlayerInterface` for its presentation (`LoadUi`, `BindUi`, `ArrangeUi`, `Draw`) and creates the painter.

**What renders on Metal:** the faithful HUD at the Hub (information/medical, squad bar, clock, money, speed,
orders, jobs), the inventory with item icons, the stats and overview/map windows, and the pause menu. Portraits
are drawn into the character renderer's atlas and the item icons come from the install's files or the renderer.
`--ui-panel inventory|stats|overview|menu` opens one in a still; `--ui-showcase` now uses the same panel set and
shared still preparation as Vulkan. Runtime images (portraits, icons, editor stage) flip their V on both backends,
because both draw them with the engine's y-up clip space. Metal uses the shared character draw list, so the editor
stage applies race, gender, slider and clothes changes instead of the interface-less world list.

**What is not wired:** the pointer sprite and the cinema camera. The shared trade/dialogue/build/editor screens use the
host callbacks `ConfigurePlayerInterface` supplies (build services, rocks, factions, tooltips, editor previews); the
character editor opens on a new game. The settings panel, title, New Game, save/load and audio are wired; the panel
keys and pointer hover/clicks/scroll work, and camera movement, selection and orders belong to the interactive loop.

### 6. The remaining effects

`VolumetricCloudRenderer` is seam-neutral and runs through `SkyRenderer` on both backends. `ParticleRenderer`
(weather and map placers) is wired, and the half/quarter-size particle targets are shared by both backends. Metal
still skips the near-plane close pass because a large dust sprite crossing it produced a black frame and a GPU
reset. `WorldLamps`,
`HoverOutlineRenderer`, `MarkerRenderer` (the movement marker and stealth arrows) and `NavmeshOverlayRenderer` are wired; `InteriorMaskRenderer` is
wired (section 4b). The faithful sky path the parity shots use already works. Decide the default between the
faithful and Meitou look.

### 7. The full game loop (plan step 10)

The interactive host: input and camera controls (`MetalDisplay` events), the title/New Game/save/load and audio
paths, resize, HDR/EDR and the present details, warm-up frames, `--screenshot`/`--ui` parity. The shared
`WorldFrame.CreateGpu`, `SceneHost` and `WorldFrame.Draw` run on `IGpuContext`; what remains is runtime acceptance
of the shared frame on Metal (title, editor, overlays, resize, settings) and removing the now-duplicated host
scaffolding.

### 8. Parity and the default (plan step 11)

Window comparison at several views, tone and timing, memory budgets, then `--renderer metal` becomes the macOS
default.

### Decisions still open

- **GI**: the probe GI needs ray queries; Metal's ray tracing is a different model. Vulkan on this Mac reports GI
  off (MoltenVK exposes no `VK_KHR_ray_query`), so local parity is no-GI; on a machine where Vulkan has ray
  queries the target includes GI. Either port `GiScene`/`GiProbes` to Metal ray queries (a project of its own) or
  accept the no-GI look as the macOS baseline. **Observed** (`Meitou.MetalSpike --rt-bench`, M1 Pro): one
  524k-triangle terrain acceleration structure took about 75–100 ms to build (88 MB plus 97 MB scratch), while
  the 131k-ray probe batch took about 0.25–0.31 ms. That is traversal only; real scene builds, object/foliage
  structures, refit and resolve are still unmeasured. The trace cost looks promising; memory and scene-build
  cadence are the constraints to check in a port. The in-game Metal path is **experimental and opt-in**
  (`MEITOU_METAL_GI=1`): a save load with it enabled froze the test M1 Pro, so normal launches leave it off.
- **Volumetric clouds and fog**: parity shots use `--faithful clouds,cloudshadows`; the Meitou cloud/fog path is
  step 6.
- **Upscaler**: MetalFX Balanced at render scale 0.50 is the Metal default, with Native as the fallback when
  MetalFX is unavailable; the choice is saved in `meitou.user.json`. TAA remains available for comparison; MEP-1
  fixed its Metal depth convention and sun-motion term, but it is still labelled experimental. The **F10 settings
  panel** exposes the MetalFX scale presets Native 1.00, Super Quality 0.77, Quality 0.67, Balanced 0.59,
  Performance 0.50, Performance 0.40, Spatial 0.50/0.40 alternatives and TAA 0.50, plus VSync, frame limit,
  borderless full screen and the live Metal graphics controls. The preset owns the render scale, so there is no
  separate custom-scale slider for MetalFX. The lowest supported MetalFX scale is 0.40: 0.33 (3x) crashed before the
  first frame on this device, and a hand-edited lower value is clamped.
  `MEITOU_METAL_NO_TAA=1` is the clean native comparison (no jitter, no temporal resolve);
  `MEITOU_METALFX_SPATIAL=1` starts in the performance tier; `MEITOU_METAL_SETTINGS=1` opens the settings panel for
  a still.

### Resume notes (facts already established)

- Binding: one table per stage at `(kind base) + set*8 + binding`; the bindless table at `buffer(28)` both stages;
  push at `(30)`. Argument-buffer structs lay members out by declaration; **the buffer is padded to 16 bytes**.
- Metal facts found on the way: a new render encoder has no pipeline state (reset the filter in `BeginRendering`);
  a depth-only pipeline skips colour slot 0; a sub-rectangle upload needs the destination level's full row stride
  (the batch repacks); disabled GL attributes use a repeating 16-byte dummy array (stride 16), not stride 0;
  non-indexed draws take `firstVertex`/`baseInstance`; BC5 is 16 bytes a block.
- **Storage buffers flatten like uniform blocks.** `SpirvPatch.RemapBindings` must shift SPIR-V StorageClass
  **12** (`StorageBuffer`), not 11 (`Image`): the character skin/morph buffers stayed at `[[id(6)]]`/`[[id(8)]]`
  while the runtime looked for 21/23, so every skinned vertex collapsed to a point. The corrected class makes the
  vertex-stage storage members take `15 + set*8 + binding`, the same table as the uniforms.
- **The Metal constants ring is chunked, not grown in place.** A slice's pointer is written after later
  allocations (the character palettes, then materials, then instances); replacing the constants buffer on growth
  would leave those writes in the old ring and the GPU reading zeros. `MetalFrame` uses a new 1 MB shared chunk
  for each overflow and resets all chunks at the frame's begin.
- **UI target orientation and scissors.** The engine's y-up clip space stores renderer images upside down on both
  backends, so `ImageFlipV` is applied on both (Metal's targets have row 0 at the top, but its viewport maps that
  clip space top-down). `RenderTargetsBottomUp` still selects the depth/post convention. UI scissor rectangles are
  top-left on Metal and bottom-up on Vulkan; using Vulkan's flip on Metal shifted clipped controls (the trader stock
  grid and HUD name plates) down by the panel height.
- **Cube stand-ins.** A dummy cube's `TextureDesc.Layers` is 1: its six faces are the cube type's slices, not array
  layers. Passing 6 made `GpuTexture.View` ask for 36 slices and `MTL_DEBUG_LAYER=1` abort at the first cube
  sampler (the UI's first frame after the painter was added). `MetalContext.Dummy` now passes 1.
- **Live performance and memory (Apple M1 Pro, Release).** The view cache and the impostor CPU release changed the
  picture: at 0.50 render scale, **30-32 fps**, CPU 32-35 ms and GPU 19-22 ms; at 0.83, 21-27 fps and GPU 30-36 ms.
  Under Debug the same views read about 20 fps. The macOS Vulkan report is 36-40 fps at 0.50. The remaining CPU cost is
  foliage recording/culling, shadows, the HUD and GC; the GPU is not the first limit at 0.50. A trace
  (`MEITOU_METAL_TRACE=1`) prints frame CPU/GPU milliseconds, the resource tables (textures/views/buffers/sets/chunks)
  and the native window state. The live process before these fixes had 97,000 live texture views and 6.9 GB in
  `IOAccelerator`; `MetalContext.View` now caches a view by texture and level/layer/swizzle key, and released views are
  retired until teardown (a recorded frame can still reference them). 173 resident impostor atlases also held their
  CPU levels: after the last upload `ImpostorTextures.DropCpu` keeps only the metadata. The managed LOH fell from 2.8
  GB to 1.2 GB and the live RSS from ~2.3 GB to 1.4-1.6 GB; the remaining LOH is the terrain's source maps.
- **Window present and sizing.** The present fragment divides the drawable pixel position by the **drawable** size,
  not the backbuffer size: on a scaled display the drawable is larger, and dividing by the backbuffer showed only
  its top-left part in the window. `--screenshot` reads the backbuffer, so it cannot catch this; the live window is
  the gate. `MetalDisplay.SyncLayer` sets the CAMetalLayer's frame, bounds, contentsScale and `drawableSize`
  explicitly before every `NextDrawable` (a frame-only change leaves `drawableSize` at its old pixel size). A
  title-bar zoom/fullscreen can make AppKit replace the content view's layer without a GLFW size event; the same
  function detects the replacement, reattaches the Metal layer and restores the picture. Tested with a drag
  (1280x856 -> 900x600 -> back) and with AppKit's zoom; the zoom's restored frame/HUD was captured. A missing
  drawable sleeps 1 ms rather than spinning the main thread.
- **Debug text scale.** The HUD scales with the screen; the fixed-pixel debug overlay (F11/F12) must be scaled by
  framebuffer/points (2 on the Retina test display), or it is half size.
- **Far foliage.** The purple/red far trees are the impostor atlases (some assets are red, e.g.
  `FOLIAGE_DeadPineType-red`); the same view on Vulkan shows the same purple impostors. Impostors are enabled by
  default again; `MEITOU_METAL_NO_IMPOSTORS=1` is the diagnostic fallback.
- **Population and navmesh.** The live Metal loop boots the simulation with `interactive: true`, loads the navmesh
  zones round the start and calls `UpdateNav` each frame. The Hub shows its wall guards and residents again; the
  background population costs frames, so measure the live window rather than the still's first-frames average.
- **Tick ahead and rotors.** The live Metal loop sets the shared `tickAhead` mode (Dynamic/Always) and wraps
  `WorldFrame.Draw` in `BeginTickAhead`/`EndTickAhead`; stills, the title and the editor keep it off. `UpdateBuildings`
  also runs upstream's rotor pass, so wind generators and other `ROTATION_OUTPUT` parts turn on Metal.
- **Play benchmark.** `--play-bench` is Vulkan-only: Metal rejects it with a message because the report's late-pipeline
  and display timings are Vulkan's.
- **UI/input scope.** `GameHost.Metal` boots `PlayerInterface` for presentation (`LoadUi`, `ArrangeUi`, `Draw`),
  portraits, item icons and the map callbacks. It wires the panels' keyboard/pointer controls (I/C, M/T/Y, Esc,
  F7, F10 settings, hover/click/scroll), the strategy camera (WASD/arrows, Q/E, wheel, middle drag, edge scroll),
  selection/box select and right-click move orders. Title, new game, save/load, audio and the shared trade, dialogue,
  build and editor callbacks are wired; the pointer sprite and free camera remain. Upstream's S3TC DDS prefetch is
  ported, so the overview map uploads compressed instead of decoding it in the opening frame.
- MSAA: `TextureDesc.Samples > 1` is `Type2DMultisample` (`Type2DMultisampleArray` for arrays), one mip level;
  `Resolve` is a render pass with the source as its colour attachment, `ResolveTexture` the target, and
  `StoreAction = MultisampleResolve`. A clear pipeline must name every attachment of the pass (the GI mask is slot 1).
  The reflection uses this path at 4x by default (`--reflection-samples 1` disables it), with private MSAA colour/depth
  and the multisample depth read at sample 0 by the reflection fog.
- Texture views: the textures carry `PixelFormatView`; a non-identity `GpuSwizzle` is
  `newTextureViewWithPixelFormat:textureType:levels:slices:swizzle:` (the BC4 masks' `RrrOne`). Metal's blit cannot
  scale: `BlitLevel` copies equal-sized levels and renders a sampling pass over level views when they differ.
- Partial clears: a fullscreen triangle under the rect's scissor (colour and/or depth outputs), then the previous
  viewport/scissor are restored and the pipeline filter reset. Sets the depth-stencil state for the clear.
- A Metal `drawPrimitives…indirectBuffer` is one command; `DrawIndexedIndirect` loops, and the count variants issue
  every `maxCount` slot (the slots at/after the count must hold zero counts).
- Metal screenshots are not byte-comparable run to run: cloud/water time still follows the real frame time, so the
  picture differs even when `--ticks` freezes simulation state. Compare regions / means, or freeze that clock for a
  pixel comparison.
- Metal's fragment stage allows 16 direct samplers. Debris and water use the native bindless model on both backends,
  so both have **0 direct fragment samplers**; water's atmosphere, heights, biome parameter arrays, ocean/shore/river
  fields, reflection and refraction all live in the table. `Gpu.Shaders.SamplerBudget` checks the direct count at
  program creation. The displacement and slope cascades still share one six-layer array.
- Debris now uses the shared `kenshiLight`/`atmoApply` fragment: the atmosphere, shadows, GI, lamps and its diffuse map
  come through the bindless set, with the model matrix, fallback light and per-part diffuse/cut-out values in one
  128-byte push block. That removes the old Metal-only simple sun/ambient path.
- UI shots: the Metal HUD is part of every screenshot. `--new-game --ui-panel inventory|stats|overview|menu`
  opens a panel; `MEITOU_METAL_OVERLAY=1` adds the frame-stats lines at the top-left. The Vulkan HUD/panel still
  renders (verified with `--ui-panel inventory` after the shared changes).
- Character shots: `--crowd 6 --no-population --at -50000,8000 --time 12 --distance 180 --pitch 18
  --no-objects --no-foliage --no-water --faithful clouds,cloudshadows --no-gi` (the open hillside; six characters).
  `--new-game --time 12 --distance 250 --pitch 20 --faithful clouds,cloudshadows --no-gi` at the Hub (the player
  squad, the HUD and the interactive camera/selection/orders). Release at 0.50 render scale: 30-32 fps; at 0.83:
  21-27; the Debug build reads about 20. `MTL_DEBUG_LAYER=1` drops it to ~2 fps, so measure without the layer.
- Water test views: the sea is at the world edge; `--at 44352,-122544 --yaw 0 --distance 800 --pitch 15 --time 8
  --day 1 --faithful water,clouds,cloudshadows --no-gi` is the top coast (Great Desert), with the sea to the horizon.
  The river/lake `--at -45060,6348 --yaw 30 --distance 800 --pitch 20` is inland water. The Hub is inland: a shot there
  shows no water at all. With `--day 1` on both backends (Metal `--faithful clouds,cloudshadows`), the Metal sea
  matches Vulkan's faithful water: the water regions average RGB ~[14, 28, 32] vs [16, 30, 34], the sky ~[105, 141, 147]
  vs [109, 143, 151]; the horizon is ~9 darker in Metal (haze/exposure). Both paths now draw the HUD.
- TAA (Metal-host slice): `PostTaa` runs the game's `UpscaleShaders.Velocity` (fragment, into RGBA16F) and
  `UpscaleShaders.Taa` (into a display-size RGBA16F history pair); the scene projection is jittered with
  `Jitter.Apply` and `PostTaa.Offset` (the phase count follows render/display scale), the depth target is `DepthTarget
  | Sampled`. TAA resets on the first frame, a resize and a camera cut (>5000 units), and MetalFX resets on the same
  cut. The velocity pass's far-slice inputs are disabled; it reads the shared `SunMotionTracker`, accounts for
  Metal's 0..1 depth (`uClipDepthZeroToOne`) and flips the depth lookup for top-down targets (`uFlipDepthY`).
  Weather-particle coverage is written into TAA's motion alpha only (full- and low-resolution particles). MetalFX
  gets no particle coverage: full-size rain/dust in its reactive mask made distant objects shimmer. The grass and
  characters write their own motion through `IWorldPostChain.ObjectMotion`.
- `IGpuContext` facts: `FramesInFlight`, `VideoMemoryBudget`, `DeferDelete`, `Finish`, `CreateReadback` +
  `FrameCompleted`, `ConstantVertexAttributes`/`DummyVertexArray`, `EnsureFrame`, `HasRayQuery`,
  `RenderTargetsBottomUp`.
- `MTL_DEBUG_LAYER=1` catches the above immediately; keep it on for every new pass.

### Gates

Vulkan is the untouched reference: do not run the Vulkan path or the full test suite on Metal-only work. What
runs when:

**Every commit (cheap, Metal-only):**

```
dotnet build src/Meitou.Game -c Debug                                  # 0 warnings, 0 errors
DYLD_LIBRARY_PATH=/opt/homebrew/lib MTL_DEBUG_LAYER=1 dotnet run --project src/Meitou.Game --no-build -c Debug -- \
    --renderer metal --screenshot /tmp/x.png --no-population --time 8 --distance 2000 --pitch 45 \
    --faithful clouds,cloudshadows --no-gi
```

Live check: `--renderer metal --new-game --time 12 --distance 250 --pitch 20 --faithful
clouds,cloudshadows --no-gi` (WASD/arrows, Q/E, wheel, middle drag; F11 frame list, F12 fps). `--quit-after N`
prints the final camera and frame rate. Present/layer changes must also be seen in the live window: `--screenshot`
reads the backbuffer and never exercises the drawable present.

The host table (`--table`; BL red, centre `(127,0,128)`, TR blue, a `.swap.bmp` appears) when the change touches
the seam, the binding tables or the Metal host.

**When a step edits shared code** — a renderer widened to `IGpuContext`, a file Vulkan still compiles, the seam
itself — run the suite and a Vulkan comparison, because the change can alter the Vulkan path even when it is meant
to be neutral:

```
DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet test --filter "Category!=Slow&Category!=Bench"   # 476 passed / 2 skipped
```

Parity: render the same view on the Vulkan path before and after (two baseline runs, since the temporal path is
noisy), compare differing samples and the worst channel (the noise floor is roughly 350k–550k samples, max
~38–40), never bytes. The screenshot is taken at frame 40; `--shadow-range 0` isolates the non-shadow path.

**End of a plan step / before reporting:** the Metal shot at the step's view, plus a Vulkan comparison only if
that step's picture is meant to match the Vulkan one (water, characters, UI, post). Metal-only steps (indirect
plumbing, profiling, docs) need no Vulkan run.

## Constraints

- **GI**: the probe GI needs ray queries (`VK_KHR_ray_query`), which have no Metal
  translation; Metal's ray tracing is a different programming model. The engine already runs
  without GI where the device has no ray queries (macOS today), so a Metal path starts with GI
  off, like the Vulkan path on macOS.
- **Upscaler**: MetalFX is the Metal default (balanced, 0.50) and is implemented behind `IUpscaler`:
  `MetalFxUpscaler` is the temporal scaler at the 0.77/0.67/0.59/0.50/0.40 presets; `MetalFxSpatialUpscaler` covers
  the explicit 0.50/0.40 alternatives. 0.40 is the floor; 0.33 crashed before the first frame on this device and is
  clamped/rejected. Native is the fallback when MetalFX is unavailable. The FSR path stays for Vulkan.

## Open questions

- Metal's GPU timestamps (MTLCounterSampleBuffer), when the pass timings matter on Metal.

(Resolved in the plan: per-entry sampler state — sized sampler arrays in the argument buffer.
Resolved: the vertex-stage binding table — the kind-aware flatten, in the road table.)
