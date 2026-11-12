using System.Runtime.InteropServices;

using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Shaders;
using Meitou.Game.Rendering.Shadows;

namespace Meitou.Game.Rendering.Frame;

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
    /// <summary>
    /// The context the programs, the frame's constants and the bindless entries belong to.
    /// </summary>
    readonly IGpuContext _ctx;

    /// <summary>
    /// The alignment the constant slices are written at: the device's uniform buffer alignment, at least 16.
    /// </summary>
    readonly ulong _align;

    /// <summary>
    /// The frame constants as the last <see cref="Bind"/> built them.
    /// </summary>
    FrameConstants _constants;

    /// <summary>
    /// The frame constants as last written into the frame's constants (<see cref="_frameSlice"/>).
    /// </summary>
    FrameConstants _written;

    /// <summary>
    /// The frame <see cref="_written"/> was written in; -1: never.
    /// </summary>
    long _writtenFrame = -1;

    /// <summary>
    /// Where the frame block was last written in the frame's constants.
    /// </summary>
    Transient _frameSlice;

    /// <summary>
    /// The frame <see cref="_bones"/> was written in; -1: never.
    /// </summary>
    long _bonesFrame = -1;

    /// <summary>
    /// Where this frame's empty bone block is in the frame's constants.
    /// </summary>
    Transient _bones;

    /// <summary>
    /// The frame globals' version <see cref="_uniforms"/>, <see cref="_textures"/> and <see cref="_blocks"/> were resolved at; -1: never.
    /// </summary>
    int _resolvedVersion = -1;

    /// <summary>
    /// The frame globals' getter of each of <see cref="FrameConstants.Uniforms"/>; null where none is published.
    /// </summary>
    FrameGlobals.Uniform?[] _uniforms = [];

    /// <summary>
    /// The frame globals' getter of each of <see cref="FrameConstants.Textures"/>; null where none is published.
    /// </summary>
    Func<SampledTexture>?[] _textures = [];

    /// <summary>
    /// The frame globals' getter of each shadow block in <see cref="Blocks"/>; null where none is published.
    /// </summary>
    Func<GpuBufferBinding>?[] _blocks = [];

    /// <summary>
    /// Each frame texture's bindless entry: the texture it was made for, its index, and whether it is held.
    /// </summary>
    readonly (SampledTexture Texture, uint Index, bool Has)[] _entries = new (SampledTexture, uint, bool)[FrameConstants.Textures.Length];

    /// <summary>
    /// The kind of dummy (2D, cube, shadow, arrayed) each frame texture falls back to where the globals give none.
    /// </summary>
    readonly SamplerInfo[] _standIns;

    /// <summary>
    /// The shadow blocks of set 0: name, binding, std140 size.
    /// </summary>
    static readonly (string Name, uint Binding, int Size)[] Blocks =
    [
        (ShadowShaders.ReceiverBlock, NativeShaders.ReceiverBinding, ShadowPass.ReceiverBytes),
        (ShadowShaders.CasterBlock, NativeShaders.CasterBinding, 16),
        (MeitouShadowShaders.Block, NativeShaders.MeitouBinding, MeitouShadowShaders.BlockBytes),
    ];

    /// <summary>
    /// The skinning block's size: 128 matrices of 64 bytes.
    /// </summary>
    const int BonesBytes = 128 * 64;

    /// <summary>
    /// The empty bone block written once a frame (a new array each time was garbage).
    /// </summary>
    static readonly byte[] NoBones = new byte[BonesBytes];

    /// <summary>
    /// Storage buffers a consumer's own programs read at set 0, bindings 6 and up (<c>readonly buffer</c> in the shader), given to <see
    /// cref="Bind"/>.
    /// </summary>
    readonly int _extraStorage;

    /// <summary>
    /// Makes set 0's layout (six uniform buffers, then <paramref name="extraStorage"/> storage buffers) and the stand-ins of the frame textures.
    /// </summary>
    public NativeFrame(IGpuContext ctx, int extraStorage = 0)
    {
        this._ctx = ctx;
        this._extraStorage = extraStorage;
        _align = Math.Max(ctx.UniformBufferAlign, 16);
        SetLayout = GpuSetLayout.Written(
        [
            .. Enumerable.Range(0, 6).Select(b => new GpuSlot((uint)b, GpuSlotKind.UniformBuffer, GpuStage.All)),
            .. Enumerable.Range(6, extraStorage).Select(b => new GpuSlot((uint)b, GpuSlotKind.StorageBuffer, GpuStage.All)),
        ]);
        SetLayouts = [SetLayout, GpuSetLayout.Bindless];
        _standIns = [.. FrameConstants.Textures.Select(t => FrameGlobals.Sampler2D(t.Name, cube: t.Kind == BindlessKind.Cube,
            shadow: t.Kind is BindlessKind.Shadow2D or BindlessKind.ShadowArray,
            arrayed: t.Kind is BindlessKind.Texture2DArray or BindlessKind.ShadowArray))];
    }

    /// <summary>
    /// Set 0 of the native model (six uniform buffers; a push-descriptor set where the device has them).
    /// </summary>
    public GpuSetLayout SetLayout { get; }

    /// <summary>
    /// The native model's sets: the bindless table, then <see cref="SetLayout"/>.
    /// </summary>
    public GpuSetLayout[] SetLayouts { get; }

    /// <summary>
    /// A native program with the model's layout, then the consumer's <paramref name="own"/> sets (set 2 and up), and the shared push-constant range
    /// (<see cref="NativeShaders.PushBytes"/>).
    /// </summary>
    public GpuProgram Program(string vertexGlsl, string fragmentGlsl, string name, params GpuSetLayout[] own) =>
        _ctx.Shaders.Native(vertexGlsl, fragmentGlsl, name, [.. SetLayouts, .. own], NativeShaders.PushBytes);

    /// <summary>
    /// The frame constants as the last <see cref="Bind"/> wrote them (diagnostics and tests).
    /// </summary>
    public FrameConstants Constants => _constants;

    /// <summary>
    /// Looks the frame globals' getters up again (their version changed).
    /// </summary>
    void Resolve()
    {
        var g = _ctx.Globals;
        _resolvedVersion = g.Version;
        _uniforms = [.. FrameConstants.Uniforms.Select(u => g.UniformValue(u.Name))];
        _textures = [.. FrameConstants.Textures.Select(t => g.Texture(t.Name))];
        _blocks = [.. Blocks.Select(b => g.Block(b.Name))];
    }

    /// <summary>
    /// Once per native segment, before its draws: binds set 1 (the bindless table) and set 0 for <paramref name="program"/>'s layout (any native
    /// program's: they are all compatible) with this segment's frame constants, shadow blocks and <paramref name="view"/>.
    /// </summary>
    public void Bind(IGpuCommandList cmd, GpuProgram program, in ViewConstants view, ReadOnlySpan<GpuBufferBinding> extra = default)
    {
        var binding = Prepare(in view, extra);
        Record(cmd, program, in binding);
    }

    /// <summary>
    /// The render-thread half of <see cref="Bind"/> (docs/renderer-native.md 6.2, wave 4): evaluates the frame globals as they are now, registers
    /// the frame textures' bindless entries, writes the frame block (when it changed) and <paramref name="view"/> into the frame's constants, and
    /// returns what <see cref="Record"/> binds. Where the device has no push descriptors, the set is allocated and written here.
    /// </summary>
    public FrameBinding Prepare(in ViewConstants view, ReadOnlySpan<GpuBufferBinding> extra = default)
    {
        RenderJobs.AssertNotInJob();
        if (extra.Length != _extraStorage)
            throw new ArgumentException($"this frame's set has {_extraStorage} extra storage bindings, {extra.Length} given", nameof(extra));
        var frame = _ctx.Frame;
        var g = _ctx.Globals;
        if (g.Version != _resolvedVersion) Resolve();
        g.ApplyCount++;   // owners whose getters share one computation do it once for this segment (FrameGlobals.ApplyCount)
        var bytes = MemoryMarshal.AsBytes(new Span<FrameConstants>(ref _constants));
        for (int i = 0; i < _uniforms.Length; i++)
        {
            if (_uniforms[i] is { } u)
            {
                var (_, offset, size) = FrameConstants.Uniforms[i];
                u.TryRead(bytes.Slice(offset, size));
            }
        }

        for (int i = 0; i < _textures.Length; i++)
        {
            var t = _textures[i] is { } get ? get() : default;
            if (t.IsNull) t = _ctx.Dummy(_standIns[i]);
            var (_, kind, offset) = FrameConstants.Textures[i];
            MemoryMarshal.Write(bytes[offset..], Entry(i, kind, t));
        }

        // The frame block again only when it changed (the usual case within a frame: the same bytes, the same slice).
        if (_writtenFrame != frame.Number || !bytes.SequenceEqual(MemoryMarshal.AsBytes(new Span<FrameConstants>(ref _written))))
        {
            _frameSlice = frame.WriteConstants(MemoryMarshal.AsBytes(new ReadOnlySpan<FrameConstants>(in _constants)), _align);
            (_written, _writtenFrame) = (_constants, frame.Number);
            frame.Stats.ConstantBytes += sizeof(FrameConstants);
        }
        var viewSlice = frame.WriteConstants(MemoryMarshal.AsBytes(new ReadOnlySpan<ViewConstants>(in view)), _align);
        frame.Stats.ConstantBytes += sizeof(ViewConstants);

        // Skinning matrices: no consumer skins yet; the block is valid memory that is never read (uSkinned is 0).
        if (_bonesFrame != frame.Number)
        {
            _bones = frame.WriteConstants(NoBones, _align);
            _bonesFrame = frame.Number;
        }

        int count = 6 + _extraStorage;
        var result = new FrameBinding { Count = count };
        Span<GpuBinding> bindings = result.Bindings;
        var frameBuffer = new GpuBufferBinding(_frameSlice.Handle, _frameSlice.Offset, (ulong)sizeof(FrameConstants));
        var viewBuffer = new GpuBufferBinding(viewSlice.Handle, viewSlice.Offset, (ulong)sizeof(ViewConstants));
        var bonesBuffer = new GpuBufferBinding(_bones.Handle, _bones.Offset, BonesBytes);
        bindings[(int)NativeShaders.FrameBinding] = GpuBinding.Uniform(NativeShaders.FrameBinding, frameBuffer);
        bindings[(int)NativeShaders.ViewBinding] = GpuBinding.Uniform(NativeShaders.ViewBinding, viewBuffer);
        bindings[(int)NativeShaders.BonesBinding] = GpuBinding.Uniform(NativeShaders.BonesBinding, bonesBuffer);
        for (int i = 0; i < Blocks.Length; i++)
        {
            var (name, binding, size) = Blocks[i];
            var value = _blocks[i] is { } get ? get() : default;
            if (value.IsNull)
            {
                // Nothing published (no world program linked yet): zeros, as ShadowShaders.Bind's default blocks (shadows off).
                var zeros = frame.WriteConstants(new byte[size], _align);
                value = zeros.Binding;
            }
            ulong range = value.Size == GpuBufferBinding.WholeSize ? (ulong)size : Math.Min((ulong)size, value.Size);
            bindings[(int)binding] = GpuBinding.Uniform(binding, new GpuBufferBinding(value.Buffer, value.Offset, range));
        }
        for (int i = 0; i < _extraStorage; i++) bindings[6 + i] = GpuBinding.Storage((uint)(6 + i), extra[i]);
        return result;
    }

    /// <summary>
    /// The recording half of <see cref="Bind"/>: binds the bindless table (set 1) and pushes (or binds) set 0 as <paramref name="binding"/> says.
    /// Reads nothing else: safe on a recording job's thread.
    /// </summary>
    public static void Record(IGpuCommandList cmd, GpuProgram program, in FrameBinding binding)
    {
        cmd.BindBindlessTable(program, NativeShaders.BindlessSet);
        int count = binding.Count;
        var copy = binding.Bindings;   // on the stack: the writes point at it
        cmd.BindProgramResources(program, NativeShaders.FrameSet, new ReadOnlySpan<GpuBinding>(&copy, count));
    }

    /// <summary>
    /// A frame texture's bindless index: the same while its view and sampler are, a new one (the old freed) after a change.
    /// </summary>
    uint Entry(int i, BindlessKind kind, in SampledTexture t)
    {
        ref var e = ref _entries[i];
        if (e.Has && e.Texture == t) return e.Index;
        if (e.Has) _ctx.Bindless.Free(kind, e.Index);
        e = (t, _ctx.Bindless.Register(kind, t), true);
        return e.Index;
    }

    /// <summary>
    /// Frees the frame textures' bindless entries.
    /// </summary>
    public void Dispose()
    {
        for (int i = 0; i < _entries.Length; i++)
            if (_entries[i].Has) _ctx.Bindless.Free(FrameConstants.Textures[i].Kind, _entries[i].Index);
    }
}

/// <summary>
/// Set 0 and set 1 of one native segment as <see cref="NativeFrame.Prepare"/> resolved them (docs/renderer-native.md 6.2, wave 4): the buffer of
/// each binding of set 0, the bindless table, and either "push" or the set written on the render thread. A value: a recording job keeps its own copy.
/// </summary>
unsafe struct FrameBinding
{
    /// <summary>
    /// The most bindings set 0 has: the six uniform buffers and the storage buffers after them.
    /// </summary>
    public const int MaxBindings = 10;

    /// <summary>
    /// Room for <see cref="MaxBindings"/> bindings inline, so the value can be copied to a recording job.
    /// </summary>
    [System.Runtime.CompilerServices.InlineArray(MaxBindings)]
    public struct BindingArray
    {
        /// <summary>
        /// The first element: the runtime lays the others out after it.
        /// </summary>
        GpuBinding _first;
    }

    /// <summary>
    /// Set 0's bindings: the uniform buffers 0 to 5 and the storage buffers after them.
    /// </summary>
    public BindingArray Bindings;

    /// <summary>
    /// How many of <see cref="Bindings"/> are used.
    /// </summary>
    public int Count;
}