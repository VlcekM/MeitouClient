using System.Runtime.InteropServices;

using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Shaders;
using Silk.NET.Vulkan;

namespace Meitou.Rendering;

/// <summary>
/// The CPU side of the native model's sets (docs/renderer-native.md 2.6 and 3.3, <see cref="NativeShaders"/>): makes native programs with the
/// model's layout and, once per native segment, pushes set 0 and binds the bindless table (set 1): <see cref="FrameConstants"/> built from the
/// frame globals as they are now (the atmosphere's values and the bindless indices of its and the shadows' textures), the three shadow blocks
/// the GL code has bound, the segment's <see cref="ViewConstants"/>, and the skinning block. One per consumer (it owns its frame textures'
/// bindless entries and its set layout); render thread only.
/// </summary>
/// <remarks>
/// What a draw reads is what the GL program read at the same point: the frame globals are evaluated per segment (as a legacy program reads them
/// at its first flush in a segment), a uniform the owner would not set (its condition false) keeps its last value (as a default block does),
/// and a frame texture gets a new bindless index whenever its view or sampler changed (the old one freed after the frames in flight), so a
/// segment recorded earlier keeps what it was given.
/// </remarks>
sealed unsafe class NativeFrame : IDisposable
{
    readonly GpuContext ctx;
    readonly bool push;
    readonly ulong align;
    FrameConstants constants;
    FrameConstants written;
    long writtenFrame = -1;
    Transient frameSlice;
    long bonesFrame = -1;
    Transient bones;
    int resolvedVersion = -1;
    FrameGlobals.Uniform?[] uniforms = [];
    Func<SampledTexture>?[] textures = [];
    Func<BufferBinding>?[] blocks = [];
    readonly (SampledTexture Texture, uint Index, bool Has)[] entries = new (SampledTexture, uint, bool)[FrameConstants.Textures.Length];
    readonly SamplerInfo[] standIns;

    /// <summary>The shadow blocks of set 0: name, binding, std140 size.</summary>
    static readonly (string Name, uint Binding, int Size)[] Blocks =
    [
        (ShadowShaders.ReceiverBlock, NativeShaders.ReceiverBinding, ShadowPass.ReceiverBytes),
        (ShadowShaders.CasterBlock, NativeShaders.CasterBinding, 16),
        (MeitouShadowShaders.Block, NativeShaders.MeitouBinding, MeitouShadowShaders.BlockBytes),
    ];
    const int BonesBytes = 128 * 64;

    /// <summary>Storage buffers a consumer's own programs read at set 0, bindings 6 and up (<c>readonly buffer</c> in the shader), given to <see cref="Bind"/>.</summary>
    readonly int extraStorage;

    public NativeFrame(GpuContext ctx, int extraStorage = 0)
    {
        this.ctx = ctx;
        this.extraStorage = extraStorage;
        push = ctx.Device.HasPushDescriptor;
        align = Math.Max(ctx.Device.Limits.MinUniformBufferOffsetAlignment, 16);
        var stages = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit;
        DescriptorSetLayoutBinding[] bindings =
        [
            .. Enumerable.Range(0, 6).Select(b => new DescriptorSetLayoutBinding((uint)b, DescriptorType.UniformBuffer, 1, stages)),
            .. Enumerable.Range(6, extraStorage).Select(b => new DescriptorSetLayoutBinding((uint)b, DescriptorType.StorageBuffer, 1, stages)),
        ];
        SetLayout = ctx.Shaders.CreateSetLayout(bindings, push ? DescriptorSetLayoutCreateFlags.PushDescriptorBitKhr : 0);
        SetLayouts = [SetLayout, ctx.Bindless.Layout];
        standIns = [.. FrameConstants.Textures.Select(t => FrameGlobals.Sampler2D(t.Name, cube: t.Kind == BindlessKind.Cube, shadow: t.Kind == BindlessKind.Shadow2D))];
    }

    /// <summary>Set 0 of the native model (six uniform buffers; a push-descriptor set where the device has them).</summary>
    public DescriptorSetLayout SetLayout { get; }
    /// <summary>The native model's sets: the bindless table, then <see cref="SetLayout"/>.</summary>
    public DescriptorSetLayout[] SetLayouts { get; }

    /// <summary>A native program with the model's layout and the shared push-constant range (<see cref="NativeShaders.PushBytes"/>).</summary>
    public ShaderProgram Program(string vertexGlsl, string fragmentGlsl, string name) =>
        ctx.Shaders.Native(vertexGlsl, fragmentGlsl, name, SetLayouts, NativeShaders.PushBytes);

    /// <summary>The frame constants as the last <see cref="Bind"/> wrote them (diagnostics and tests).</summary>
    public FrameConstants Constants => constants;

    void Resolve()
    {
        var g = ctx.Globals;
        resolvedVersion = g.Version;
        uniforms = [.. FrameConstants.Uniforms.Select(u => g.UniformValue(u.Name))];
        textures = [.. FrameConstants.Textures.Select(t => g.Texture(t.Name))];
        blocks = [.. Blocks.Select(b => g.Block(b.Name))];
    }

    /// <summary>
    /// Once per native segment, before its draws: binds set 1 (the bindless table) and set 0 for <paramref name="layout"/> (any native
    /// program's: they are all compatible) with this segment's frame constants, shadow blocks and <paramref name="view"/>.
    /// </summary>
    public void Bind(CommandList cmd, PipelineLayout layout, in ViewConstants view, ReadOnlySpan<BufferBinding> extra = default)
    {
        var binding = Prepare(in view, extra);
        Record(cmd, layout, in binding);
    }

    /// <summary>
    /// The render-thread half of <see cref="Bind"/> (docs/renderer-native.md 6.2, wave 4): evaluates the frame globals as they are now, registers
    /// the frame textures' bindless entries, writes the frame block (when it changed) and <paramref name="view"/> into the frame's constants, and
    /// returns what <see cref="Record"/> binds. Where the device has no push descriptors, the set is allocated and written here.
    /// </summary>
    public FrameBinding Prepare(in ViewConstants view, ReadOnlySpan<BufferBinding> extra = default)
    {
        RenderJobs.AssertNotInJob();
        if (extra.Length != extraStorage) throw new ArgumentException($"this frame's set has {extraStorage} extra storage bindings, {extra.Length} given", nameof(extra));
        var frame = ctx.Frame;
        var g = ctx.Globals;
        if (g.Version != resolvedVersion) Resolve();
        g.ApplyCount++;   // owners whose getters share one computation do it once for this segment (FrameGlobals.ApplyCount)
        var bytes = MemoryMarshal.AsBytes(new Span<FrameConstants>(ref constants));
        for (int i = 0; i < uniforms.Length; i++)
            if (uniforms[i] is { } u)
            {
                var (_, offset, size) = FrameConstants.Uniforms[i];
                u.TryRead(bytes.Slice(offset, size));
            }
        for (int i = 0; i < textures.Length; i++)
        {
            var t = textures[i] is { } get ? get() : default;
            if (t.IsNull) t = ctx.Dummy(standIns[i]);
            var (_, kind, offset) = FrameConstants.Textures[i];
            MemoryMarshal.Write(bytes[offset..], Entry(i, kind, t));
        }
        // The frame block again only when it changed (the usual case within a frame: the same bytes, the same slice).
        if (writtenFrame != frame.Number || !bytes.SequenceEqual(MemoryMarshal.AsBytes(new Span<FrameConstants>(ref written))))
        {
            frameSlice = frame.Constants.Write<FrameConstants>(new ReadOnlySpan<FrameConstants>(in constants), align);
            (written, writtenFrame) = (constants, frame.Number);
            frame.Stats.ConstantBytes += sizeof(FrameConstants);
        }
        var viewSlice = frame.Constants.Write<ViewConstants>(new ReadOnlySpan<ViewConstants>(in view), align);
        frame.Stats.ConstantBytes += sizeof(ViewConstants);
        // Skinning matrices: no consumer skins yet; the block is valid memory that is never read (uSkinned is 0).
        if (bonesFrame != frame.Number) { bones = frame.Constants.Allocate(BonesBytes, align); bonesFrame = frame.Number; }

        int count = 6 + extraStorage;
        var result = new FrameBinding { Count = count, Table = ctx.Bindless.Set, Push = push };
        Span<DescriptorBufferInfo> infos = result.Infos;
        infos[(int)NativeShaders.FrameBinding] = new DescriptorBufferInfo(frameSlice.Handle, frameSlice.Offset, (ulong)sizeof(FrameConstants));
        infos[(int)NativeShaders.ViewBinding] = new DescriptorBufferInfo(viewSlice.Handle, viewSlice.Offset, (ulong)sizeof(ViewConstants));
        infos[(int)NativeShaders.BonesBinding] = new DescriptorBufferInfo(bones.Handle, bones.Offset, BonesBytes);
        for (int i = 0; i < Blocks.Length; i++)
        {
            var (name, binding, size) = Blocks[i];
            var value = blocks[i] is { } get ? get() : default;
            if (value.IsNull)
            {
                // Nothing published (no world program linked yet): zeros, as ShadowShaders.Bind's default blocks (shadows off).
                var zeros = frame.Constants.Allocate((ulong)size, align);
                zeros.Bytes.Clear();
                value = zeros.Binding;
            }
            ulong range = value.Size == Vk.WholeSize ? (ulong)size : Math.Min((ulong)size, value.Size);
            infos[(int)binding] = new DescriptorBufferInfo(value.Buffer, value.Offset, range);
        }
        for (int i = 0; i < extraStorage; i++) infos[6 + i] = new DescriptorBufferInfo(extra[i].Buffer, extra[i].Offset, extra[i].Size);
        if (!push)
        {
            var set = frame.AllocateSet(SetLayout);
            var writes = stackalloc WriteDescriptorSet[count];
            fixed (DescriptorBufferInfo* pi = infos) FrameBinding.Writes(writes, pi, count, set);
            ctx.Device.Vk.UpdateDescriptorSets(ctx.Device.Device, (uint)count, writes, 0, null);
            result.Set = set;
        }
        return result;
    }

    /// <summary>
    /// The recording half of <see cref="Bind"/>: binds the bindless table (set 1) and pushes (or binds) set 0 as <paramref name="binding"/> says.
    /// Reads nothing else: safe on a recording job's thread.
    /// </summary>
    public static void Record(CommandList cmd, PipelineLayout layout, in FrameBinding binding)
    {
        var table = binding.Table;
        cmd.BindSets(layout, NativeShaders.BindlessSet, new ReadOnlySpan<DescriptorSet>(in table), []);
        if (binding.Push)
        {
            int count = binding.Count;
            var copy = binding.Infos;   // on the stack: the writes point at it
            var writes = stackalloc WriteDescriptorSet[count];
            FrameBinding.Writes(writes, (DescriptorBufferInfo*)&copy, count, default);
            cmd.PushDescriptors(layout, NativeShaders.FrameSet, new ReadOnlySpan<WriteDescriptorSet>(writes, count));
        }
        else
        {
            var set = binding.Set;
            cmd.BindSets(layout, NativeShaders.FrameSet, new ReadOnlySpan<DescriptorSet>(in set), []);
        }
    }

    /// <summary>A frame texture's bindless index: the same while its view and sampler are, a new one (the old freed) after a change.</summary>
    uint Entry(int i, BindlessKind kind, in SampledTexture t)
    {
        ref var e = ref entries[i];
        if (e.Has && e.Texture == t) return e.Index;
        if (e.Has) ctx.Bindless.Free(kind, e.Index);
        e = (t, ctx.Bindless.Register(kind, t), true);
        return e.Index;
    }

    public void Dispose()
    {
        for (int i = 0; i < entries.Length; i++)
            if (entries[i].Has) ctx.Bindless.Free(FrameConstants.Textures[i].Kind, entries[i].Index);
        var (vk, dev, layout) = (ctx.Device.Vk, ctx.Device.Device, SetLayout);
        ctx.Device.Frames.DeferDelete(() => vk.DestroyDescriptorSetLayout(dev, layout, null));
    }
}

/// <summary>
/// Set 0 and set 1 of one native segment as <see cref="NativeFrame.Prepare"/> resolved them (docs/renderer-native.md 6.2, wave 4): the buffer of
/// each binding of set 0, the bindless table, and either "push" or the set written on the render thread. A value: a recording job keeps its own copy.
/// </summary>
unsafe struct FrameBinding
{
    public const int MaxBindings = 9;
    [System.Runtime.CompilerServices.InlineArray(MaxBindings)]
    public struct InfoArray { DescriptorBufferInfo first; }
    public InfoArray Infos;
    public int Count;
    public DescriptorSet Table;
    public DescriptorSet Set;
    public bool Push;

    /// <summary>The writes of set 0 (uniform buffers 0 to 5, storage buffers after them) pointing at <paramref name="infos"/>.</summary>
    public static void Writes(WriteDescriptorSet* writes, DescriptorBufferInfo* infos, int count, DescriptorSet set)
    {
        for (int b = 0; b < count; b++)
            writes[b] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = (uint)b, DescriptorCount = 1,
                DescriptorType = b < 6 ? DescriptorType.UniformBuffer : DescriptorType.StorageBuffer, PBufferInfo = &infos[b],
            };
    }
}
