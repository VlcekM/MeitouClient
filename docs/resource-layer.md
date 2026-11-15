# The resource layer

The layer between the renderers and the graphics API: the resources (the images, the buffers, the
samplers, the views, the pipelines) and the commands. It is Vulkan's today; a second backend
replaces it. The renderers state their draws in the engine's GL vocabulary
([renderer-native](renderer-native.md)); this is the same for the resources.

The vocabulary below is the shape the port needs. The descriptions are in the code: the draw
state, the formats, the load op, the clear colour, the index kind, the sampler's filters and
wrap modes, and the binding list (the renderers state the slots and what each holds; the
program carries the layout, the backend the image layout and the descriptor type). The sampler's
handle is in the code too: the renderers hold a `GpuSampler` (an index into the backend's
samplers, 0 being the null one) and the backend resolves it where it writes a descriptor. The
views are the same: a `GpuView` (an index into the backend's views), and the backend resolves it
where it makes a render pass. The textures and the buffers are the same: a `GpuTexture` (an index
into the backend's textures, with the description) and a `GpuBuffer` (an index into the backend's
buffers, with the size). The DLSS paths (Streamline on Windows, NGX on Linux) ask for the native image and view themselves:
that path is NVIDIA's and stays Vulkan's.

What is left: the seam (the context as the interface).

## What the renderers would hold

| Today (Vulkan's words) | The engine's word | Note |
|---|---|---|
| `GpuImage`, `GpuBuffer` (classes around a Vulkan object) | `GpuImage`, `GpuBuffer` (handles) | the description is the engine's, the object is the backend's |
| `Texture` (a class with a view cache) | `GpuTexture` (a handle) | the views are the backend's |
| `ImageView` (a Vulkan handle) | `GpuView` (a handle) | the index into the backend's views |
| `Sampler` | `GpuSampler` (a handle) | the index into the backend's samplers; 0 is the null one |
| `BufferBinding(Buffer, Offset, Size)` | `GpuBufferBinding(GpuBuffer, Offset, Size)` | |
| `PipelineLayout` | none | the reflection knows the sets, the slots and the stages |
| `WriteDescriptorSet`, `DescriptorImageInfo`, `DescriptorType` | `GpuBinding` | a flat list per set |
| `ImageLayout` | none | the backend's business |
| `DescriptorSet` | `GpuSet` | the index into the backend's sets |
| `DescriptorSetLayout` | `GpuSetLayout` | the slots of a set, in the engine's words |
| `PipelineLayout` | none | the program carries it |
| `CommandBuffer`, `CommandList` | `IGpuCommandList` | the commands the renderers state |
| `BufferCopy`, `PipelineBindPoint` | `GpuBufferCopy`, `GpuBindPoint` |
| `AttachmentLoadOp` | `LoadOp` | |
| `ClearValue` | `ClearColour`, a depth value | |
| `IndexType` | `IndexKind` | |
| `SamplerDesc`'s `Filter`, `SamplerAddressMode`, `SamplerMipmapMode` | `TextureMinFilter`, `TextureMagFilter`, `TextureWrapMode` | the engine's own already exist |
| `ComponentMapping` | `GpuSwizzle` | the default is the identity, as `default(ComponentMapping)` was |

The sampler's compare operation and the depth function were made neutral earlier
([renderer-native](renderer-native.md)).

A set is described in the engine's words: `GpuSetLayout` says the slots it has (the binding
number, what it holds, the stages that read it, how many entries, and whether its offset is given
when the set is bound) and whether the renderers write it for every draw. The backend turns that
into its own layout (Vulkan: a descriptor set layout; Metal: the program's argument table). The
bindless table is the one set the engine does not describe slot by slot: it is a named set
(`GpuSetLayout.Bindless`) whose seven runtime-sized arrays the backend owns.

## The binding list

One list per set, the slot in each entry, the program's own reflection for the rest:

```csharp
// Today (Vulkan's words):
var infos = stackalloc Vk.DescriptorImageInfo[4];
infos[0] = new Vk.DescriptorImageInfo(Point(), shaftInjected!.Texture.View(), Vk.ImageLayout.General);
var writes = stackalloc Vk.WriteDescriptorSet[5];
writes[0] = new Vk.WriteDescriptorSet { DstBinding = 0, DescriptorType = Vk.DescriptorType.CombinedImageSampler, PImageInfo = &infos[0] };
cmd.PushDescriptors(shaftScanProgram.Layout, 0, new ReadOnlySpan<Vk.WriteDescriptorSet>(writes, 5), Vk.PipelineBindPoint.Compute);

// The engine's words:
var bindings = stackalloc GpuBinding[5];
bindings[0] = GpuBinding.Sampled(0, new SampledTexture(Point(), shaftInjected!.Texture.View()));
bindings[1] = GpuBinding.StorageImage(1, new SampledTexture(default, shaftLight!.Texture.View()));
bindings[4] = GpuBinding.Uniform(4, new GpuBufferBinding(data.Handle, data.Offset, (ulong)sizeof(ShaftScanParams)));
cmd.BindProgramResources(shaftScanProgram, 0, new ReadOnlySpan<GpuBinding>(bindings, 5));
```

The kinds are a sampled texture (with its sampler), a storage image, a uniform buffer and a
storage buffer. The program's own reflection says which stage each slot wants and whether the
program is compute or graphics, so the call carries neither; the layout comes from the program
too. The view's range (the level, the layers) rides in the binding - the whole texture by
default - and the backend resolves it and keeps the cache, as the texture's own cache did.

Two things are still Vulkan's, on purpose: the acceleration structure (the GI's ray queries are
Vulkan-only; Metal has no equivalent) and the descriptor *sets* - the bindless table and the
constants block - which are the other half of the binding model.

## The seam

```
the renderers
   |  the neutral vocabulary: the descriptions, the handles, the bindings
   v
IGpuContext          the operations both backends have
   +-- the Vulkan implementation (today's GpuContext)
   +-- the Metal implementation (the port)
```

The interface is the union of what the renderers use. The Vulkan side implements all of it from
the start; the Metal side grows a pass at a time, and an operation it does not have yet throws.
A pass the backend cannot do yet is skipped, so the Metal picture grows from the sky to the whole
frame while the frame graph stays the engine's (one description, not two).

The interface carries the resources (the textures made, viewed and released, the buffers made and
released) and the services the renderers use (the frame, the pipelines, the programs, the samplers,
the uploads, the stand-ins, the frame globals, the bindless table and the upscaler's LOD bias). The
renderers' own fields change from the Vulkan class to the interface as the last of the Vulkan
escapes (the copies, the queries, the vendor upscalers) move behind it.

The renderers above `Gpu/` name no Vulkan object any more: the samplers, the views, the textures,
the buffers and the sets are the engine's handles; the programs carry their layout; a draw states
its resources as a flat `GpuBinding` list and records into an `IGpuCommandList` (the commands the
renderers state, in the engine's words). What is left in the renderers is the GI's acceleration
structure and the vendor upscalers' own command buffers, both Vulkan-only on purpose.

The renderer-side cost, measured outside `Gpu/`: about 294 references to the binding vocabulary
(`WriteDescriptorSet` 64, `DescriptorType` 53, `ImageLayout` 41, `DescriptorImageInfo` 38,
`AttachmentLoadOp` 25, `ClearValue` 18, `ShaderStageFlags` 17, `DescriptorSet` 15,
`PipelineLayout` 12, `IndexType` 11) and about 283 to the resource handles (`Buffer` 113,
`Image` 100, `Sampler` 70). The proof that a step changed nothing: the game's offscreen picture
is byte for byte the same ([metal-backend](metal-backend.md) "The road to a Metal frame").

Two things the neutral words must keep: a description's default is the *identity* (the swizzle
of a sampled view, as `default(ComponentMapping)` was), and a handle's index 0 is the null one.

## The two decisions

**1. The handle's shape: opaque handles.**

A dense index into the backend's table plus the description. The renderers cannot reach the
backend's objects, which is the point of the layer; the backend resolves the index (an array
lookup). Vulkan's tables are appended under a lock and read without one, the view cache too (a view is looked up per draw); a released entry is
cleared once no frame in flight reads it, and its index is never reused (caches compare views and
buffers by index). A pipeline handle is its description plus, where the backend has one, its native
handle: a bind needs no lookup, and `--bench-tris`'s variant of the same description stays apart. The alternative - backend interfaces (`IGpuImage`, `IGpuBuffer`, `IGpuSampler`) - would
cost a virtual call per use and a type change in every renderer signature, against a codebase that
is value-typed and allocation-free in the frame path.

**2. Where the seam sits: the context is the interface.**

The renderers say `IGpuContext`; the Vulkan class implements it as it is (a type rename in about
fifty files). The alternative - the context stays a class and forwards to a backend object - needs
no change in the renderers, but the forwarding layer grows with every operation and the
Vulkan-only parts (the allocator, the bindless journal, the swapchain) still leak: the second
engine the port exists to avoid.

The engine's own terms for the drawing (the state, the formats) are in
[renderer-native](renderer-native.md); the Metal side of the port is in
[metal-backend](metal-backend.md).
