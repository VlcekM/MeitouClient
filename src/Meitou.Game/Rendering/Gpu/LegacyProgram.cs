using System.Numerics;
using Meitou.Rendering.Gpu.Shaders;

namespace Meitou.Rendering.Gpu;

/// <summary>A loose uniform of a <see cref="LegacyProgram"/>, resolved at load (GL's location). Invalid when the program does not use it.</summary>
public readonly record struct UniformHandle(int Index)
{
    public bool IsValid => Index >= 0;
    public static readonly UniformHandle None = new(-1);
}

/// <summary>A sampler of a <see cref="LegacyProgram"/> by GL name (one or both stages).</summary>
public readonly record struct SamplerSlot(int Index)
{
    public bool IsValid => Index >= 0;
}

/// <summary>A named uniform block of a <see cref="LegacyProgram"/> (the shadow receiver and caster blocks).</summary>
public readonly record struct BlockSlot(int Index)
{
    public bool IsValid => Index >= 0;
}

/// <summary>A vertex input as a GL vertex array described it (the pipeline caches' key): per location the attribute (null when disabled)
/// and the element buffer (null binding when none).</summary>
public sealed record VertexArrayBindings(LegacyProgram.Attribute?[] Attributes, GpuBufferBinding Elements);

/// <summary>
/// The program VkGl would build for a GLSL source pair (docs/renderer-native.md 3.2, step P): the same SPIR-V and layout (set 0 the named blocks
/// and samplers at glslang's bindings, pushed; set 1 the default blocks as dynamic uniform buffers), driven without lookups while drawing.
/// It keeps VkGl's rules: the CPU copy of each default block persists for the program's lifetime (values set once stay set), conversions follow
/// <see cref="GlUniforms.Scatter"/>, inactive uniforms are ignored, a sampler with nothing bound reads the GL default (<see cref="GpuContext.Dummy"/>),
/// and set 0 is pushed whole when anything in it changed (or another program's layout was bound in between).
/// </summary>
public sealed unsafe class LegacyProgram : IDisposable
{
    readonly IGpuContext ctx;
    readonly ulong uniformAlign;              // the default blocks' slice alignment (the device limit, at least 16)
    readonly List<(string Name, UniformLookup? Vertex, UniformLookup? Fragment)> uniforms = [];
    readonly Dictionary<string, int> uniformIndex = [];
    // Set 0, in VkGl's order: every non-default uniform block of the vertex stage, then of the fragment stage; then every sampler of both.
    readonly (uint Binding, int Size, string Name)[] blocks;
    readonly (uint Binding, SamplerInfo Info, string Name)[] samplers;
    readonly string[] blockNames, samplerNames;
    readonly GpuBufferBinding[] blockValues;     // per block name
    readonly SampledTexture[] samplerValues;  // per sampler name
    readonly int[] blockIndex, samplerIndex;  // each set-0 entry's name index
    readonly ulong[] lastPush;
    long lastPushEpoch = -1;
    bool bindingsDirty = true;                // a Bind since the last Flush
    int pushedGlobals = -1;                   // the globals version set 0 was last evaluated with
    bool vertexDirty = true, fragmentDirty = true;
    long sliceFrame = -1;
    Transient vertexSlice, fragmentSlice;
    readonly Dictionary<(ulong, ulong), GpuSet> dynamicSets = [];

    LegacyProgram(IGpuContext ctx, GpuProgram program)
    {
        this.ctx = ctx;
        uniformAlign = Math.Max(ctx.UniformBufferAlign, 16);
        Program = program;
        var c = Program.Compiled!;
        VertexBlock = c.Vertex.DefaultBlock;
        FragmentBlock = c.Fragment.DefaultBlock;
        VertexDefault = new byte[VertexBlock?.Size ?? 0];
        FragmentDefault = new byte[FragmentBlock?.Size ?? 0];
        blocks = [
            .. c.Vertex.Blocks.Where(b => b.Kind == BlockKind.Uniform && !b.IsDefault).Select(b => ((uint)b.Binding, b.Size, b.Name)),
            .. c.Fragment.Blocks.Where(b => b.Kind == BlockKind.Uniform && !b.IsDefault).Select(b => ((uint)b.Binding, b.Size, b.Name))];
        samplers = [.. c.Vertex.Samplers.Concat(c.Fragment.Samplers).Select(s => ((uint)s.Binding, s, s.Name))];
        blockNames = [.. blocks.Select(b => b.Name).Distinct()];
        samplerNames = [.. samplers.Select(s => s.Name).Distinct()];
        blockValues = new GpuBufferBinding[blockNames.Length];
        samplerValues = new SampledTexture[samplerNames.Length];
        lastPush = new ulong[(blocks.Length + samplers.Length) * 3];
        blockIndex = [.. blocks.Select(b => Array.IndexOf(blockNames, b.Name))];
        samplerIndex = [.. samplers.Select(s => Array.IndexOf(samplerNames, s.Name))];
        InputLocations = [.. c.Vertex.Inputs.SelectMany(i => Enumerable.Range(i.Location, i.Slots))];
        InputKinds = [.. c.Vertex.Inputs.SelectMany(i => Enumerable.Repeat(i.Kind, i.Slots))];
    }

    /// <summary>Compiles (or takes from the caches) the pair as VkGl would and builds VkGl's layout.</summary>
    public static LegacyProgram Create(IGpuContext ctx, string vertexGlsl, string fragmentGlsl, string name) =>
        new(ctx, ctx.Shaders.Legacy(vertexGlsl, fragmentGlsl, name));

    public GpuProgram Program { get; }
    public string Name => Program.Name;
    public UniformBlockInfo? VertexBlock { get; }
    public UniformBlockInfo? FragmentBlock { get; }
    /// <summary>The CPU copies of the default blocks (persistent, as a GL program's uniform state).</summary>
    public byte[] VertexDefault { get; }
    public byte[] FragmentDefault { get; }
    /// <summary>The vertex input locations the program reads (a matrix input takes several), and their scalar kinds.</summary>
    public int[] InputLocations { get; }
    public ScalarKind[] InputKinds { get; }
    public IReadOnlyList<string> SamplerNames => samplerNames;
    public IReadOnlyList<string> BlockNames => blockNames;

    // ---- resolved at load ----

    /// <summary>A loose uniform by GL name (<c>uName</c>, <c>uName[3]</c>); <see cref="UniformHandle.None"/> when inactive (GL's −1).</summary>
    public UniformHandle Uniform(string glName)
    {
        if (uniformIndex.TryGetValue(glName, out int i)) return new UniformHandle(i);
        var c = Program.Compiled!;
        var v = Loose(c.Vertex.FindUniform(glName));
        var f = Loose(c.Fragment.FindUniform(glName));
        if (v is null && f is null) i = -1;
        else
        {
            i = uniforms.Count;
            uniforms.Add((glName, v, f));
        }
        uniformIndex[glName] = i;
        return new UniformHandle(i);
    }

    static UniformLookup? Loose(UniformLookup? l) => l is { } u && u.Block.IsDefault ? u : null;

    public SamplerSlot Sampler(string glName) => new(Array.IndexOf(samplerNames, glName));
    public BlockSlot Block(string glName) => new(Array.IndexOf(blockNames, glName));

    // ---- per draw ----

    public void Set(UniformHandle u, float v) => Write(u, (byte*)&v, 1, 1, 1, false);
    public void Set(UniformHandle u, int v) => Write(u, (byte*)&v, 1, 1, 1, true);
    public void Set(UniformHandle u, Vector2 v) => Write(u, (byte*)&v, 2, 1, 1, false);
    public void Set(UniformHandle u, Vector3 v) => Write(u, (byte*)&v, 3, 1, 1, false);
    public void Set(UniformHandle u, Vector4 v) => Write(u, (byte*)&v, 4, 1, 1, false);
    public void Set(UniformHandle u, float x, float y) { var v = new Vector2(x, y); Write(u, (byte*)&v, 2, 1, 1, false); }
    public void Set(UniformHandle u, float x, float y, float z) { var v = new Vector3(x, y, z); Write(u, (byte*)&v, 3, 1, 1, false); }
    public void Set(UniformHandle u, float x, float y, float z, float w) { var v = new Vector4(x, y, z, w); Write(u, (byte*)&v, 4, 1, 1, false); }
    /// <summary>A System.Numerics matrix as <c>WorldGl.Matrix</c> passes it (row-major memory, which GL reads as the column-vector form).</summary>
    public void Set(UniformHandle u, in Matrix4x4 m) { fixed (Matrix4x4* p = &m) Write(u, (byte*)p, 4, 4, 1, false); }
    /// <summary>Arrays of 4-byte components (glUniform*fv / glUniformMatrix4fv): <paramref name="rows"/> × <paramref name="columns"/> per element.</summary>
    public void Set(UniformHandle u, ReadOnlySpan<float> values, int rows, int columns = 1)
    {
        fixed (float* p = values) Write(u, (byte*)p, rows, columns, values.Length / (rows * columns), false);
    }

    void Write(UniformHandle u, byte* src, int rows, int columns, int count, bool isInt)
    {
        if (!u.IsValid) return;
        var (_, v, f) = uniforms[u.Index];
        if (v is { } vl) { GlUniforms.Scatter(VertexDefault, vl, src, rows, columns, count, isInt); vertexDirty = true; }
        if (f is { } fl) { GlUniforms.Scatter(FragmentDefault, fl, src, rows, columns, count, isInt); fragmentDirty = true; }
    }

    /// <summary>The texture a sampler reads (default: nothing, the GL stand-in).</summary>
    public void Bind(SamplerSlot s, in SampledTexture texture)
    {
        if (s.IsValid) { samplerValues[s.Index] = texture; bindingsDirty = true; }
    }

    public void Bind(BlockSlot b, in GpuBufferBinding buffer)
    {
        if (b.IsValid) { blockValues[b.Index] = buffer; bindingsDirty = true; }
    }

    /// <summary>The reflected sampler behind a slot (its kind decides the stand-in and whether it compares).</summary>
    public SamplerInfo SamplerInfo(SamplerSlot s) => samplers[Array.IndexOf(samplerIndex, s.Index)].Info;

    // ---- frame globals (docs/renderer-native.md 4.3) ----

    int globalsVersion = -1;
    Func<SampledTexture>?[] globalTextures = [];
    Func<GpuBufferBinding>?[] globalBlocks = [];
    (UniformHandle Handle, FrameGlobals.Uniform Value)[] globalUniforms = [];

    /// <summary>Re-reads the published names (call only when <see cref="FrameGlobals.Version"/> moved: its closure allocates).</summary>
    void ResolveGlobals()
    {
        var g = ctx.Globals;
        globalsVersion = g.Version;
        globalTextures = [.. samplerNames.Select(g.Texture)];
        globalBlocks = [.. blockNames.Select(g.Block)];
        globalUniforms = [.. g.UniformNames.Select(n => (Uniform(n), g.UniformValue(n)!)).Where(x => x.Item1.IsValid)];
    }

    /// <summary>
    /// Sets every published frame-global uniform this program uses to its current value (where the GL code set them: <c>SkyRenderer.Apply</c>,
    /// <c>TerrainRenderer.BindHeights</c>). Samplers and blocks need no call: one with nothing bound reads its global at <see cref="Flush"/>.
    /// </summary>
    public void ApplyGlobals()
    {
        if (ctx.Globals.Version != globalsVersion) ResolveGlobals();
        ctx.Globals.ApplyCount++;
        foreach (var (h, v) in globalUniforms) v.Write(this, h);
    }

    /// <summary>
    /// Before a draw: copies the default blocks that changed (or all, in a new frame) into the frame's constants and binds set 1 with their
    /// offsets, and pushes set 0 when it differs from what this program last pushed in this epoch of the command list.
    /// </summary>
    public void Flush(IGpuCommandList cmd)
    {
        var frame = ctx.Frame;
        if (ctx.Globals.Version != globalsVersion) ResolveGlobals();
        bool programChanged = !ReferenceEquals(cmd.BoundProgram, this) || cmd.Epoch != lastPushEpoch;
        if (VertexBlock is not null || FragmentBlock is not null)
        {
            if (sliceFrame != frame.Number) { vertexDirty = fragmentDirty = true; sliceFrame = frame.Number; }
            bool moved = false;
            ulong align = uniformAlign;
            if (vertexDirty && VertexDefault.Length > 0) { vertexSlice = frame.WriteConstants(VertexDefault, align); vertexDirty = false; moved = true; frame.Stats.ConstantBytes += VertexDefault.Length; }
            if (fragmentDirty && FragmentDefault.Length > 0) { fragmentSlice = frame.WriteConstants(FragmentDefault, align); fragmentDirty = false; moved = true; frame.Stats.ConstantBytes += FragmentDefault.Length; }
            if (moved || programChanged)
            {
                var set = DynamicSet();
                Span<uint> offsets = stackalloc uint[2];
                int n = 0;
                if (VertexBlock is not null) offsets[n++] = (uint)vertexSlice.Offset;
                if (FragmentBlock is not null) offsets[n++] = (uint)fragmentSlice.Offset;
                cmd.BindSets(Program, 1, new ReadOnlySpan<GpuSet>(in set), offsets[..n]);
            }
        }

        int count = blocks.Length + samplers.Length;
        // Set 0 as last pushed in this epoch, nothing bound since and the same globals: nothing to compare (the per-draw cost of a
        // batch of draws with one program). Globals are read at the program's first Flush of a native segment and after a Bind.
        bool unchanged = !programChanged && !bindingsDirty && pushedGlobals == globalsVersion;
        if (count > 0 && !unchanged)
        {
            var bindings = stackalloc GpuBinding[count];
            bool same = !programChanged;
            int w = 0;
            for (int i = 0; i < blocks.Length; i++)
            {
                var b = blocks[i];
                int bi = blockIndex[i];
                var value = blockValues[bi];
                if (value.IsNull && globalBlocks[bi] is { } gb) value = gb();
                if (value.IsNull) throw new InvalidOperationException($"{Name}: no buffer bound for uniform block {b.Name}");
                ulong range = value.Size == GpuBufferBinding.WholeSize ? (ulong)b.Size : Math.Min((ulong)b.Size, value.Size);
                same &= lastPush[w * 3] == (ulong)value.Buffer.Index && lastPush[w * 3 + 1] == value.Offset && lastPush[w * 3 + 2] == range;
                (lastPush[w * 3], lastPush[w * 3 + 1], lastPush[w * 3 + 2]) = ((ulong)value.Buffer.Index, value.Offset, range);
                bindings[w++] = GpuBinding.Uniform((uint)b.Binding, new GpuBufferBinding(value.Buffer, value.Offset, range));
            }
            for (int i = 0; i < samplers.Length; i++)
            {
                var s = samplers[i];
                int si = samplerIndex[i];
                var t = samplerValues[si];
                if (t.IsNull && globalTextures[si] is { } gt) t = gt();
                if (t.IsNull) t = ctx.Dummy(s.Info);
                same &= lastPush[w * 3] == (ulong)t.Sampler.Index && lastPush[w * 3 + 1] == (ulong)t.View.Index;
                (lastPush[w * 3], lastPush[w * 3 + 1]) = ((ulong)t.Sampler.Index, (ulong)t.View.Index);
                bindings[w++] = GpuBinding.Sampled((uint)s.Binding, new SampledTexture(t.Sampler, t.View));
            }
            if (!same)
            {
                lastPushEpoch = cmd.Epoch;
                cmd.BindProgramResources(Program, 0, new ReadOnlySpan<GpuBinding>(bindings, w));
            }
        }
        lastPushEpoch = cmd.Epoch;
        bindingsDirty = false;
        pushedGlobals = globalsVersion;
        cmd.BoundProgram = this;
    }

    GpuSet DynamicSet()
    {
        var key = (VertexBlock is null ? 0 : (ulong)vertexSlice.Handle.Index, FragmentBlock is null ? 0 : (ulong)fragmentSlice.Handle.Index);
        // The chunks of the last two frames (one 8 MB chunk per frame slot, the usual case): no dictionary.
        if (key == recentKeys[0] && !recentSets[0].IsNull) return recentSets[0];
        if (key == recentKeys[1] && !recentSets[1].IsNull) return recentSets[1];
        if (!dynamicSets.TryGetValue(key, out var set)) set = CreateDynamicSet(key);
        (recentKeys[1], recentSets[1]) = (recentKeys[0], recentSets[0]);
        (recentKeys[0], recentSets[0]) = (key, set);
        return set;
    }

    readonly (ulong, ulong)[] recentKeys = new (ulong, ulong)[2];
    readonly GpuSet[] recentSets = new GpuSet[2];

    GpuSet CreateDynamicSet((ulong, ulong) key)
    {
        Span<GpuBinding> bindings = stackalloc GpuBinding[2];
        int n = 0;
        if (VertexBlock is { } vb) bindings[n++] = GpuBinding.Uniform(0, new GpuBufferBinding(vertexSlice.Handle, 0, (ulong)vb.Size));
        if (FragmentBlock is { } fb) bindings[n++] = GpuBinding.Uniform(1, new GpuBufferBinding(fragmentSlice.Handle, 0, (ulong)fb.Size));
        var set = ctx.CreateSet(Program.NeutralSets[1], bindings[..n]);
        dynamicSets[key] = set;
        return set;
    }

    // ---- vertex input ----

    /// <summary>A GL vertex attribute as the legacy model feeds it: the buffer range, its format, stride (0 = tightly packed, as GL) and divisor.</summary>
    public readonly record struct Attribute(GpuBufferBinding Buffer, InternalFormat Format, uint Stride, bool PerInstance);

    /// <summary>
    /// The vertex layout VkGl packs for this program (<c>VkGl.PrepareDraw</c>): for each input location, the attribute's format, its stride
    /// and rate, or for a location with nothing (null) GL's disabled-attribute constant (stride 0, the dummy format of the input's kind).
    /// </summary>
    public VertexLayout VertexLayout(ReadOnlySpan<Attribute?> byLocation)
    {
        Span<VertexInput> inputs = stackalloc VertexInput[InputLocations.Length];
        for (int i = 0; i < inputs.Length; i++)
        {
            int loc = InputLocations[i];
            inputs[i] = loc < byLocation.Length && byLocation[loc] is { } a
                ? new VertexInput((uint)loc, a.Format, a.Stride, a.PerInstance)
                : new VertexInput((uint)loc, GlConventions.DummyVertexFormat(InputKinds[i]), 0, false);
        }
        // Draws in a row mostly share one layout: the same object again (no allocation, and the pipeline lookup compares by reference).
        if (lastLayout is { } last && inputs.SequenceEqual(last.Inputs)) return last;
        return lastLayout = new VertexLayout(inputs.ToArray());
    }

    VertexLayout? lastLayout;

    /// <summary>
    /// What <see cref="BindVertices"/> binds for the locations <paramref name="first"/> .. <paramref name="first"/> + <paramref name="count"/> − 1, as
    /// one array for one <see cref="CommandList.BindVertexBuffers"/> call: each attribute's buffer, GL's disabled-attribute constant where null
    /// (a location in the range the program does not read gets the float constant; binding it is harmless). Resolve it once per mesh and keep it
    /// while the mesh's <see cref="VertexArrayBindings"/> are the same object.
    /// </summary>
    public GpuBufferBinding[] VertexBuffers(ReadOnlySpan<Attribute?> byLocation, int first, int count)
    {
        var result = new GpuBufferBinding[count];
        for (int i = 0; i < count; i++)
        {
            int loc = first + i;
            int input = Array.IndexOf(InputLocations, loc);
            result[i] = loc < byLocation.Length && byLocation[loc] is { } a
                ? a.Buffer
                : new GpuBufferBinding(ctx.DummyVertex, GlConventions.DummyVertexOffset(input >= 0 ? InputKinds[input] : ScalarKind.Float));
        }
        return result;
    }

    /// <summary>Binds each input location's buffer (the dummy constant where null) in one call per run of consecutive locations.</summary>
    public void BindVertices(IGpuCommandList cmd, ReadOnlySpan<Attribute?> byLocation)
    {
        Span<GpuBufferBinding> run = stackalloc GpuBufferBinding[InputLocations.Length];
        int n = 0;
        uint first = 0;
        for (int i = 0; i < InputLocations.Length; i++)
        {
            int loc = InputLocations[i];
            if (n > 0 && loc != first + n) { cmd.BindVertexBuffers(first, run[..n]); n = 0; }
            if (n == 0) first = (uint)loc;
            run[n++] = loc < byLocation.Length && byLocation[loc] is { } a
                ? a.Buffer
                : new GpuBufferBinding(ctx.DummyVertex, GlConventions.DummyVertexOffset(InputKinds[i]));
        }
        if (n > 0) cmd.BindVertexBuffers(first, run[..n]);
    }

    public void Dispose()
    {
        ctx.Pipelines.Forget(Program);
        ctx.Shaders.Dispose(Program);
    }
}
