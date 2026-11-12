using System.Numerics;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu.Shaders;

namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// A loose uniform of a <see cref="LegacyProgram"/>, resolved at load (GL's location). Invalid when the program does not use it.
/// </summary>
public readonly record struct UniformHandle(int Index)
{
    /// <summary>
    /// Whether the program uses the uniform.
    /// </summary>
    public bool IsValid => Index >= 0;
    /// <summary>
    /// The handle of an inactive uniform (GL's location −1).
    /// </summary>
    public static readonly UniformHandle None = new(-1);
}

/// <summary>
/// A sampler of a <see cref="LegacyProgram"/> by GL name (one or both stages).
/// </summary>
public readonly record struct SamplerSlot(int Index)
{
    /// <summary>
    /// Whether the program has a sampler of that name.
    /// </summary>
    public bool IsValid => Index >= 0;
}

/// <summary>
/// A named uniform block of a <see cref="LegacyProgram"/> (the shadow receiver and caster blocks).
/// </summary>
public readonly record struct BlockSlot(int Index)
{
    /// <summary>
    /// Whether the program has a block of that name.
    /// </summary>
    public bool IsValid => Index >= 0;
}

/// <summary>
/// A vertex input as a GL vertex array described it (the pipeline caches' key): per location the attribute (null when disabled)
/// and the element buffer (null binding when none).
/// </summary>
public sealed record VertexArrayBindings(LegacyProgram.Attribute?[] Attributes, GpuBufferBinding Elements);

/// <summary>
/// The program VkGl would build for a GLSL source pair (docs/renderer-native.md 3.2, step P): the same SPIR-V and layout (set 0 the named blocks
/// and samplers at glslang's bindings, pushed; set 1 the default blocks as dynamic uniform buffers), driven without lookups while drawing.
/// It keeps VkGl's rules: the CPU copy of each default block persists for the program's lifetime (values set once stay set), conversions follow
/// <see cref="GlUniforms.Scatter"/>, inactive uniforms are ignored, a sampler with nothing bound reads the GL default
/// (<see cref="GpuContext.Dummy"/>), and set 0 is pushed whole when anything in it changed (or another program's layout was bound in between).
/// </summary>
public sealed unsafe class LegacyProgram : IDisposable
{
    #region State

    /// <summary>
    /// The backend the program's sets and pipelines are made on.
    /// </summary>
    readonly IGpuContext _ctx;
    /// <summary>
    /// The default blocks' slice alignment (the device limit, at least 16).
    /// </summary>
    readonly ulong _uniformAlign;
    /// <summary>
    /// The loose uniforms resolved so far, by handle index: the GL name and where it is in each stage's default block (null when that stage lacks
    /// it).
    /// </summary>
    readonly List<(string Name, UniformLookup? Vertex, UniformLookup? Fragment)> _uniforms = [];
    /// <summary>
    /// The handle index of each GL name asked for (-1 for an inactive one).
    /// </summary>
    readonly Dictionary<string, int> _uniformIndex = [];

    /// <summary>
    /// Set 0's uniform blocks, in VkGl's order: every non-default uniform block of the vertex stage, then of the fragment stage; the samplers follow
    /// them.
    /// </summary>
    readonly (uint Binding, int Size, string Name)[] _blocks;
    /// <summary>
    /// Set 0's samplers after the blocks: the vertex stage's, then the fragment stage's.
    /// </summary>
    readonly (uint Binding, SamplerInfo Info, string Name)[] _samplers;
    /// <summary>
    /// The distinct block and sampler names (a name used in both stages is one slot).
    /// </summary>
    readonly string[] _blockNames, _samplerNames;
    /// <summary>
    /// The buffer bound to each block name.
    /// </summary>
    readonly GpuBufferBinding[] _blockValues;
    /// <summary>
    /// The texture bound to each sampler name.
    /// </summary>
    readonly SampledTexture[] _samplerValues;
    /// <summary>
    /// Each set-0 block's and sampler's name index.
    /// </summary>
    readonly int[] _blockIndex, _samplerIndex;
    /// <summary>
    /// The set-0 descriptor values last pushed (three words per entry): an unchanged set is not pushed again.
    /// </summary>
    readonly ulong[] _lastPush;
    /// <summary>
    /// The command list epoch set 0 was last pushed in (a new one forces a push).
    /// </summary>
    long _lastPushEpoch = -1;
    /// <summary>
    /// A Bind since the last Flush.
    /// </summary>
    bool _bindingsDirty = true;
    /// <summary>
    /// The globals version set 0 was last evaluated with.
    /// </summary>
    int _pushedGlobals = -1;
    /// <summary>
    /// The vertex and fragment default blocks changed since they were last copied to a slice.
    /// </summary>
    bool _vertexDirty = true, _fragmentDirty = true;
    /// <summary>
    /// The frame the default blocks' slices were taken in (a new frame copies both again).
    /// </summary>
    long _sliceFrame = -1;
    /// <summary>
    /// The default blocks' slices of this frame's constants (<see cref="IGpuFrame.WriteConstants"/>).
    /// </summary>
    Transient _vertexSlice, _fragmentSlice;
    /// <summary>
    /// The set-1 descriptor sets by the vertex and fragment slices' buffer handles.
    /// </summary>
    readonly Dictionary<(ulong, ulong), GpuSet> _dynamicSets = [];

    #endregion

    #region Construction

    /// <summary>
    /// Reflects the compiled program: its default blocks, set-0 blocks and samplers, and vertex inputs.
    /// </summary>
    LegacyProgram(IGpuContext ctx, GpuProgram program)
    {
        this._ctx = ctx;
        _uniformAlign = Math.Max(ctx.UniformBufferAlign, 16);
        Program = program;
        var c = Program.Compiled!;
        VertexBlock = c.Vertex.DefaultBlock;
        FragmentBlock = c.Fragment.DefaultBlock;
        VertexDefault = new byte[VertexBlock?.Size ?? 0];
        FragmentDefault = new byte[FragmentBlock?.Size ?? 0];
        _blocks = [
            .. c.Vertex.Blocks.Where(b => b.Kind == BlockKind.Uniform && !b.IsDefault).Select(b => ((uint)b.Binding, b.Size, b.Name)),
            .. c.Fragment.Blocks.Where(b => b.Kind == BlockKind.Uniform && !b.IsDefault).Select(b => ((uint)b.Binding, b.Size, b.Name))];
        _samplers = [.. c.Vertex.Samplers.Concat(c.Fragment.Samplers).Select(s => ((uint)s.Binding, s, s.Name))];
        _blockNames = [.. _blocks.Select(b => b.Name).Distinct()];
        _samplerNames = [.. _samplers.Select(s => s.Name).Distinct()];
        _blockValues = new GpuBufferBinding[_blockNames.Length];
        _samplerValues = new SampledTexture[_samplerNames.Length];
        _lastPush = new ulong[(_blocks.Length + _samplers.Length) * 3];
        _blockIndex = [.. _blocks.Select(b => Array.IndexOf(_blockNames, b.Name))];
        _samplerIndex = [.. _samplers.Select(s => Array.IndexOf(_samplerNames, s.Name))];
        InputLocations = [.. c.Vertex.Inputs.SelectMany(i => Enumerable.Range(i.Location, i.Slots))];
        InputKinds = [.. c.Vertex.Inputs.SelectMany(i => Enumerable.Repeat(i.Kind, i.Slots))];
    }

    /// <summary>
    /// Compiles (or takes from the caches) the pair as VkGl would and builds VkGl's layout.
    /// </summary>
    public static LegacyProgram Create(IGpuContext ctx, string vertexGlsl, string fragmentGlsl, string name) =>
        new(ctx, ctx.Shaders.Legacy(vertexGlsl, fragmentGlsl, name));

    /// <summary>
    /// The compiled program.
    /// </summary>
    public GpuProgram Program { get; }
    /// <summary>
    /// The program's name.
    /// </summary>
    public string Name => Program.Name;
    /// <summary>
    /// The vertex stage's default uniform block; null when it has none.
    /// </summary>
    public UniformBlockInfo? VertexBlock { get; }
    /// <summary>
    /// The fragment stage's default uniform block; null when it has none.
    /// </summary>
    public UniformBlockInfo? FragmentBlock { get; }

    /// <summary>
    /// The CPU copies of the default blocks (persistent, as a GL program's uniform state).
    /// </summary>
    public byte[] VertexDefault { get; }
    /// <summary>
    /// The CPU copy of the fragment default block.
    /// </summary>
    public byte[] FragmentDefault { get; }

    /// <summary>
    /// The vertex input locations the program reads (a matrix input takes several), and their scalar kinds.
    /// </summary>
    public int[] InputLocations { get; }
    /// <summary>
    /// The scalar kind of each of <see cref="InputLocations"/>.
    /// </summary>
    public ScalarKind[] InputKinds { get; }
    /// <summary>
    /// The distinct sampler names of both stages.
    /// </summary>
    public IReadOnlyList<string> SamplerNames => _samplerNames;
    /// <summary>
    /// The distinct uniform block names of both stages.
    /// </summary>
    public IReadOnlyList<string> BlockNames => _blockNames;

    #endregion

    #region Resolved at load

    /// <summary>
    /// A loose uniform by GL name (<c>uName</c>, <c>uName[3]</c>); <see cref="UniformHandle.None"/> when inactive (GL's −1).
    /// </summary>
    public UniformHandle Uniform(string glName)
    {
        if (_uniformIndex.TryGetValue(glName, out int i)) return new UniformHandle(i);
        var c = Program.Compiled!;
        var v = Loose(c.Vertex.FindUniform(glName));
        var f = Loose(c.Fragment.FindUniform(glName));
        if (v is null && f is null) i = -1;
        else
        {
            i = _uniforms.Count;
            _uniforms.Add((glName, v, f));
        }
        _uniformIndex[glName] = i;
        return new UniformHandle(i);
    }

    /// <summary>
    /// The lookup when it is in the default block (a loose uniform); null otherwise.
    /// </summary>
    static UniformLookup? Loose(UniformLookup? l) => l is { } u && u.Block.IsDefault ? u : null;

    /// <summary>
    /// A sampler by GL name; invalid when the program has none of that name.
    /// </summary>
    public SamplerSlot Sampler(string glName) => new(Array.IndexOf(_samplerNames, glName));
    /// <summary>
    /// A uniform block by GL name; invalid when the program has none of that name.
    /// </summary>
    public BlockSlot Block(string glName) => new(Array.IndexOf(_blockNames, glName));

    #endregion

    #region Per draw

    /// <summary>
    /// <c>glUniform1f</c>.
    /// </summary>
    public void Set(UniformHandle u, float v) => Write(u, (byte*)&v, 1, 1, 1, false);
    /// <summary>
    /// <c>glUniform1i</c> (a sampler unit or an int/bool uniform).
    /// </summary>
    public void Set(UniformHandle u, int v) => Write(u, (byte*)&v, 1, 1, 1, true);
    /// <summary>
    /// <c>glUniform2fv</c>.
    /// </summary>
    public void Set(UniformHandle u, Vector2 v) => Write(u, (byte*)&v, 2, 1, 1, false);
    /// <summary>
    /// <c>glUniform3fv</c>.
    /// </summary>
    public void Set(UniformHandle u, Vector3 v) => Write(u, (byte*)&v, 3, 1, 1, false);
    /// <summary>
    /// <c>glUniform4fv</c>.
    /// </summary>
    public void Set(UniformHandle u, Vector4 v) => Write(u, (byte*)&v, 4, 1, 1, false);

    /// <summary>
    /// <c>glUniform2f</c>.
    /// </summary>
    public void Set(UniformHandle u, float x, float y)
    {
        var v = new Vector2(x, y);
        Write(u, (byte*)&v, 2, 1, 1, false);
    }

    /// <summary>
    /// <c>glUniform3f</c>.
    /// </summary>
    public void Set(UniformHandle u, float x, float y, float z)
    {
        var v = new Vector3(x, y, z);
        Write(u, (byte*)&v, 3, 1, 1, false);
    }

    /// <summary>
    /// <c>glUniform4f</c>.
    /// </summary>
    public void Set(UniformHandle u, float x, float y, float z, float w)
    {
        var v = new Vector4(x, y, z, w);
        Write(u, (byte*)&v, 4, 1, 1, false);
    }

    /// <summary>
    /// A System.Numerics matrix as <c>WorldGl.Matrix</c> passes it (row-major memory, which GL reads as the column-vector form).
    /// </summary>
    public void Set(UniformHandle u, in Matrix4x4 m)
    {
        fixed (Matrix4x4* p = &m) Write(u, (byte*)p, 4, 4, 1, false);
    }

    /// <summary>
    /// Arrays of 4-byte components (glUniform*fv / glUniformMatrix4fv): <paramref name="rows"/> × <paramref name="columns"/> per element.
    /// </summary>
    public void Set(UniformHandle u, ReadOnlySpan<float> values, int rows, int columns = 1)
    {
        fixed (float* p = values) Write(u, (byte*)p, rows, columns, values.Length / (rows * columns), false);
    }

    /// <summary>
    /// Scatters GL values into the default block of each stage that has the uniform and marks that block changed; an invalid handle does nothing.
    /// </summary>
    void Write(UniformHandle u, byte* src, int rows, int columns, int count, bool isInt)
    {
        if (!u.IsValid) return;
        var (_, v, f) = _uniforms[u.Index];
        if (v is { } vl)
        {
            GlUniforms.Scatter(VertexDefault, vl, src, rows, columns, count, isInt);
            _vertexDirty = true;
        }

        if (f is { } fl)
        {
            GlUniforms.Scatter(FragmentDefault, fl, src, rows, columns, count, isInt);
            _fragmentDirty = true;
        }
    }

    /// <summary>
    /// The texture a sampler reads (default: nothing, the GL stand-in).
    /// </summary>
    public void Bind(SamplerSlot s, in SampledTexture texture)
    {
        if (!s.IsValid) return;
        _samplerValues[s.Index] = texture;
        _bindingsDirty = true;
    }

    /// <summary>
    /// The buffer a uniform block reads.
    /// </summary>
    public void Bind(BlockSlot b, in GpuBufferBinding buffer)
    {
        if (!b.IsValid) return;
        _blockValues[b.Index] = buffer;
        _bindingsDirty = true;
    }

    /// <summary>
    /// The reflected sampler behind a slot (its kind decides the stand-in and whether it compares).
    /// </summary>
    public SamplerInfo SamplerInfo(SamplerSlot s) => _samplers[Array.IndexOf(_samplerIndex, s.Index)].Info;

    #endregion

    #region Frame globals (docs/renderer-native.md 4.3)

    /// <summary>
    /// The <see cref="FrameGlobals.Version"/> the global lookups below were resolved at.
    /// </summary>
    int _globalsVersion = -1;
    /// <summary>
    /// The published texture of each sampler name (null where none is published).
    /// </summary>
    Func<SampledTexture>?[] _globalTextures = [];
    /// <summary>
    /// The published buffer of each block name (null where none is published).
    /// </summary>
    Func<GpuBufferBinding>?[] _globalBlocks = [];
    /// <summary>
    /// The published uniforms this program uses, with their handles.
    /// </summary>
    (UniformHandle Handle, FrameGlobals.Uniform Value)[] _globalUniforms = [];

    /// <summary>
    /// Re-reads the published names (call only when <see cref="FrameGlobals.Version"/> moved: its closure allocates).
    /// </summary>
    void ResolveGlobals()
    {
        var g = _ctx.Globals;
        _globalsVersion = g.Version;
        _globalTextures = [.. _samplerNames.Select(g.Texture)];
        _globalBlocks = [.. _blockNames.Select(g.Block)];
        _globalUniforms = [.. g.UniformNames.Select(n => (Uniform(n), g.UniformValue(n)!)).Where(x => x.Item1.IsValid)];
    }

    /// <summary>
    /// Sets every published frame-global uniform this program uses to its current value (where the GL code set them: <c>SkyRenderer.Apply</c>,
    /// <c>TerrainRenderer.BindHeights</c>). Samplers and blocks need no call: one with nothing bound reads its global at <see cref="Flush"/>.
    /// </summary>
    public void ApplyGlobals()
    {
        if (_ctx.Globals.Version != _globalsVersion) ResolveGlobals();
        _ctx.Globals.ApplyCount++;
        foreach (var (h, v) in _globalUniforms) v.Write(this, h);
    }

    #endregion

    #region Flush

    /// <summary>
    /// Before a draw: copies the default blocks that changed (or all, in a new frame) into the frame's constants and binds set 1 with their
    /// offsets, and pushes set 0 when it differs from what this program last pushed in this epoch of the command list.
    /// </summary>
    public void Flush(IGpuCommandList cmd)
    {
        IGpuFrame frame = _ctx.Frame;
        if (_ctx.Globals.Version != _globalsVersion) ResolveGlobals();
        bool programChanged = !ReferenceEquals(cmd.BoundProgram, this) || cmd.Epoch != _lastPushEpoch;
        if (VertexBlock is not null || FragmentBlock is not null) FlushDefaultBlocks(cmd, frame, programChanged);
        int count = _blocks.Length + _samplers.Length;

        // Set 0 as last pushed in this epoch, nothing bound since and the same globals: nothing to compare (the per-draw cost of a
        // batch of draws with one program). Globals are read at the program's first Flush of a native segment and after a Bind.
        bool unchanged = !programChanged && !_bindingsDirty && _pushedGlobals == _globalsVersion;
        if (count > 0 && !unchanged) PushSet0(cmd, count, programChanged);
        _lastPushEpoch = cmd.Epoch;
        _bindingsDirty = false;
        _pushedGlobals = _globalsVersion;
        cmd.BoundProgram = this;
    }

    /// <summary>
    /// Copies the default blocks that changed (or all, in a new frame) into the frame's constants, then binds set 1 with their offsets.
    /// </summary>
    void FlushDefaultBlocks(IGpuCommandList cmd, IGpuFrame frame, bool programChanged)
    {
        if (_sliceFrame != frame.Number)
        {
            _vertexDirty = _fragmentDirty = true;
            _sliceFrame = frame.Number;
        }

        bool moved = false;
        ulong align = _uniformAlign;
        if (_vertexDirty && VertexDefault.Length > 0)
        {
            _vertexSlice = frame.WriteConstants(VertexDefault, align);
            _vertexDirty = false;
            moved = true;
            frame.Stats.ConstantBytes += VertexDefault.Length;
        }

        if (_fragmentDirty && FragmentDefault.Length > 0)
        {
            _fragmentSlice = frame.WriteConstants(FragmentDefault, align);
            _fragmentDirty = false;
            moved = true;
            frame.Stats.ConstantBytes += FragmentDefault.Length;
        }

        if (moved || programChanged)
        {
            var set = DynamicSet();
            Span<uint> offsets = stackalloc uint[2];
            int n = 0;
            if (VertexBlock is not null) offsets[n++] = (uint)_vertexSlice.Offset;
            if (FragmentBlock is not null) offsets[n++] = (uint)_fragmentSlice.Offset;
            cmd.BindSets(Program, 1, new ReadOnlySpan<GpuSet>(in set), offsets[..n]);
        }
    }

    /// <summary>
    /// Pushes set 0 (every named block and sampler, in set-0 order) unless it equals what this program last pushed in this epoch.
    /// </summary>
    void PushSet0(IGpuCommandList cmd, int count, bool programChanged)
    {
        var bindings = stackalloc GpuBinding[count];
        bool same = !programChanged;
        int w = 0;
        for (int i = 0; i < _blocks.Length; i++)
        {
            var b = _blocks[i];
            int bi = _blockIndex[i];
            var value = _blockValues[bi];
            if (value.IsNull && _globalBlocks[bi] is { } gb) value = gb();
            if (value.IsNull) throw new InvalidOperationException($"{Name}: no buffer bound for uniform block {b.Name}");
            ulong range = value.Size == GpuBufferBinding.WholeSize ? (ulong)b.Size : Math.Min((ulong)b.Size, value.Size);
            same &= _lastPush[w * 3] == (ulong)value.Buffer.Index && _lastPush[w * 3 + 1] == value.Offset
                && _lastPush[w * 3 + 2] == range;
            (_lastPush[w * 3], _lastPush[w * 3 + 1], _lastPush[w * 3 + 2]) = ((ulong)value.Buffer.Index, value.Offset, range);
            bindings[w++] = GpuBinding.Uniform((uint)b.Binding, new GpuBufferBinding(value.Buffer, value.Offset, range));
        }

        for (int i = 0; i < _samplers.Length; i++)
        {
            var s = _samplers[i];
            int si = _samplerIndex[i];
            var t = _samplerValues[si];
            if (t.IsNull && _globalTextures[si] is { } gt) t = gt();
            if (t.IsNull) t = _ctx.Dummy(s.Info);
            same &= _lastPush[w * 3] == (ulong)t.Sampler.Index && _lastPush[w * 3 + 1] == (ulong)t.View.Index;
            (_lastPush[w * 3], _lastPush[w * 3 + 1]) = ((ulong)t.Sampler.Index, (ulong)t.View.Index);
            bindings[w++] = GpuBinding.Sampled((uint)s.Binding, new SampledTexture(t.Sampler, t.View));
        }

        if (!same)
        {
            _lastPushEpoch = cmd.Epoch;
            cmd.BindProgramResources(Program, 0, new ReadOnlySpan<GpuBinding>(bindings, w));
        }
    }

    #endregion

    #region Dynamic sets

    /// <summary>
    /// The keys of the last two dynamic sets asked for (the chunks of the last two frames: one 8 MB chunk per frame slot, the usual case), looked up
    /// without the dictionary.
    /// </summary>
    readonly (ulong, ulong)[] _recentKeys = new (ulong, ulong)[2];
    /// <summary>
    /// The sets of <see cref="_recentKeys"/>.
    /// </summary>
    readonly GpuSet[] _recentSets = new GpuSet[2];

    /// <summary>
    /// The set-1 descriptor set over this frame's default-block slices: one of the last two, else from <see cref="_dynamicSets"/>, else a new one.
    /// </summary>
    GpuSet DynamicSet()
    {
        var key = (VertexBlock is null ? 0 : (ulong)_vertexSlice.Handle.Index, FragmentBlock is null ? 0 : (ulong)_fragmentSlice.Handle.Index);
        if (key == _recentKeys[0] && !_recentSets[0].IsNull) return _recentSets[0];
        if (key == _recentKeys[1] && !_recentSets[1].IsNull) return _recentSets[1];
        if (!_dynamicSets.TryGetValue(key, out var set)) set = CreateDynamicSet(key);
        (_recentKeys[1], _recentSets[1]) = (_recentKeys[0], _recentSets[0]);
        (_recentKeys[0], _recentSets[0]) = (key, set);
        return set;
    }

    /// <summary>
    /// A set-1 descriptor set binding the vertex slice at 0 and the fragment slice at 1, cached in <see cref="_dynamicSets"/>.
    /// </summary>
    GpuSet CreateDynamicSet((ulong, ulong) key)
    {
        Span<GpuBinding> bindings = stackalloc GpuBinding[2];
        int n = 0;
        if (VertexBlock is { } vb)
            bindings[n++] = GpuBinding.Uniform(0, new GpuBufferBinding(_vertexSlice.Handle, 0, (ulong)vb.Size));
        if (FragmentBlock is { } fb)
            bindings[n++] = GpuBinding.Uniform(1, new GpuBufferBinding(_fragmentSlice.Handle, 0, (ulong)fb.Size));
        var set = _ctx.CreateSet(Program.NeutralSets[1], bindings[..n]);
        _dynamicSets[key] = set;
        return set;
    }

    #endregion

    #region Vertex input

    /// <summary>
    /// The vertex layout last returned: a run of draws reuses the same object.
    /// </summary>
    VertexLayout? _lastLayout;

    /// <summary>
    /// A GL vertex attribute as the legacy model feeds it: the buffer range, its format, stride (0 = tightly packed, as GL) and divisor.
    /// </summary>
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
        if (_lastLayout is { } last && inputs.SequenceEqual(last.Inputs)) return last;
        return _lastLayout = new VertexLayout(inputs.ToArray());
    }

    /// <summary>
    /// What <see cref="BindVertices"/> binds for the locations <paramref name="first"/> .. <paramref name="first"/> + <paramref name="count"/> − 1,
    /// as one array for one <see cref="CommandList.BindVertexBuffers"/> call: each attribute's buffer, GL's disabled-attribute constant where
    /// null (a location in the range the program does not read gets the float constant; binding it is harmless). Resolve it once per mesh and
    /// keep it while the mesh's <see cref="VertexArrayBindings"/> are the same object.
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
                : new GpuBufferBinding(_ctx.DummyVertex, GlConventions.DummyVertexOffset(input >= 0 ? InputKinds[input] : ScalarKind.Float));
        }
        return result;
    }

    /// <summary>
    /// Binds each input location's buffer (the dummy constant where null) in one call per run of consecutive locations.
    /// </summary>
    public void BindVertices(IGpuCommandList cmd, ReadOnlySpan<Attribute?> byLocation)
    {
        Span<GpuBufferBinding> run = stackalloc GpuBufferBinding[InputLocations.Length];
        int n = 0;
        uint first = 0;
        for (int i = 0; i < InputLocations.Length; i++)
        {
            int loc = InputLocations[i];
            if (n > 0 && loc != first + n)
            {
                cmd.BindVertexBuffers(first, run[..n]);
                n = 0;
            }

            if (n == 0) first = (uint)loc;
            run[n++] = loc < byLocation.Length && byLocation[loc] is { } a
                ? a.Buffer
                : new GpuBufferBinding(_ctx.DummyVertex, GlConventions.DummyVertexOffset(InputKinds[i]));
        }

        if (n > 0) cmd.BindVertexBuffers(first, run[..n]);
    }

    #endregion

    /// <summary>
    /// Forgets the program's pipelines and releases its shaders.
    /// </summary>
    public void Dispose()
    {
        _ctx.Pipelines.Forget(Program);
        _ctx.Shaders.Dispose(Program);
    }
}