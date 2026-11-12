using System.Text.RegularExpressions;
using Meitou.Game.Rendering.Gpu;
using SharpMetal.Metal;

namespace Meitou.Game.Rendering.Metal;

/// <summary>
/// One stage's argument buffer layout, read from the MSL: the members' offsets in entries (8 bytes each) and the arrays each kind got. Metal packs
/// the members sequentially in declaration order — the MSL's <c>[[id]]</c> decorations do not move them (verified with the pipeline reflection) — and
/// a compiler drops an array a stage never reads, so the offsets differ per program: a program's buffer is filled to its own layout.
/// </summary>
internal sealed class MetalTableLayout
{
    #region State

    /// <summary>
    /// Each kind's texture array offset in entries; -1 when the stage's layout lacks it.
    /// </summary>
    readonly int[] _textureAt = Filled();
    /// <summary>
    /// Each kind's sampler array offset in entries; -1 when the stage's layout lacks it.
    /// </summary>
    readonly int[] _samplerAt = Filled();

    /// <summary>
    /// The empty layout (a program that does not read the table).
    /// </summary>
    public static readonly MetalTableLayout Empty = new();

    #endregion

    #region Properties

    /// <summary>
    /// The buffer's total entries.
    /// </summary>
    public int Entries { get; private set; }

    /// <summary>
    /// Whether the stage's layout has no table members (it never reads the table).
    /// </summary>
    public bool IsEmpty => Entries == 0;

    /// <summary>
    /// A kind's texture array offset in entries; -1 when the layout lacks it.
    /// </summary>
    public int TextureAt(int kind) => _textureAt[kind];
    /// <summary>
    /// A kind's sampler array offset in entries; -1 when the layout lacks it.
    /// </summary>
    public int SamplerAt(int kind) => _samplerAt[kind];

    #endregion

    #region Parsing

    /// <summary>
    /// An offset array with every kind missing (-1).
    /// </summary>
    static int[] Filled()
    {
        var at = new int[BindlessTable.ArrayCount];
        Array.Fill(at, -1);
        return at;
    }

    /// <summary>
    /// The layout of the MSL's argument buffer struct (the first <c>spvDescriptorSetBuffer</c> struct's members, in order).
    /// </summary>
    public static MetalTableLayout Parse(string msl)
    {
        var layout = new MetalTableLayout();

        // The table's set is the parameter moved to [[buffer(TableBinding)]]; programs with other sets carry their structs too
        // (the terrain has sets 0 and 2), and the first struct in the file is not the table's.
        var parameter = Regex.Match(
            msl, @"constant\s+spvDescriptorSetBuffer(?<set>\d+)&[^,\n]*\[\[buffer\(" + MslTranslator.TableBinding + @"\)\]\]");
        if (!parameter.Success) return layout;
        var buffer = Regex.Match(msl, @"struct\s+spvDescriptorSetBuffer" + parameter.Groups["set"].Value + @"\s*\{(?<body>[^}]*)\}");
        if (!buffer.Success) return layout;
        int at = 0;
        const string memberPattern = @"array<[^,]+(?:<[^>]*>)?,\s*(?<count>\d+)>\s+\w+\s+\[\[id\((?<id>\d+)\)\]\]";
        foreach (Match member in Regex.Matches(buffer.Groups["body"].Value, memberPattern))
        {
            int count = int.Parse(member.Groups["count"].Value);
            if (MetalShaders.TableKindOf(uint.Parse(member.Groups["id"].Value)) is var (kind, sampler))
            {
                if (sampler) layout._samplerAt[kind] = at;
                else layout._textureAt[kind] = at;
            }

            at += count;
        }

        layout.Entries = at;
        return layout;
    }

    #endregion
}

/// <summary>
/// The engine's bindless table on Metal (docs/metal-backend.md, "The bindless table"): the bindless set is one Metal 3
/// tier-2 argument buffer per frame slot and per program layout (a stage's unused arrays are not in its layout, so the offsets
/// move). The entries are the textures' and samplers' <c>gpuResourceID</c>s; the float and shadow kinds carry a sampler per
/// entry, the integer kinds' fetches never read one. The journal contract is the Vulkan table's (<see cref="BindlessTable"/>):
/// a registration or update is visible in the frame that is open and in every frame that begins after it, and a freed index is
/// handed out again once the frames that could read it have finished. Render thread only.
/// </summary>
internal sealed unsafe class MetalBindlessTable : IGpuBindlessTable
{
    #region State

    /// <summary>
    /// The backend: frame numbers and the shared buffers the slots are made of.
    /// </summary>
    readonly MetalContext _ctx;
    /// <summary>
    /// Per kind: the entries' texture ids.
    /// </summary>
    readonly ulong[][] _textures = new ulong[BindlessTable.ArrayCount][];
    /// <summary>
    /// Per kind: the entries' sampler ids (where the layout has them).
    /// </summary>
    readonly ulong[][] _samplers = new ulong[BindlessTable.ArrayCount][];
    /// <summary>
    /// Per kind: the entries' textures, told to the encoder as resident (<see cref="UseResources"/>).
    /// </summary>
    readonly MTLTexture?[][] _live = new MTLTexture?[BindlessTable.ArrayCount][];
    /// <summary>
    /// Per kind: the indices freed and past the frames in flight, handed out again first.
    /// </summary>
    readonly Stack<uint>[] _free = new Stack<uint>[BindlessTable.ArrayCount];
    /// <summary>
    /// Per kind: the next index never handed out.
    /// </summary>
    readonly uint[] _next = new uint[BindlessTable.ArrayCount];
    /// <summary>
    /// Freed indices with the frame they were freed in, waiting for the ring to pass.
    /// </summary>
    readonly List<(long Frame, BindlessKind Kind, uint Index)> _pendingFree = [];
    /// <summary>
    /// The argument buffers by program layout and frame slot.
    /// </summary>
    readonly Dictionary<(MetalTableLayout Layout, int Slot), Slot> _slots = [];

    /// <summary>
    /// Bumped when an entry changed (the slots refill from it).
    /// </summary>
    long _version;

    #endregion

    #region Construction

    /// <summary>
    /// One argument buffer: a shared buffer, its contents' address and size, and the entry version it was last filled at.
    /// </summary>
    sealed class Slot
    {
        /// <summary>
        /// The shared buffer.
        /// </summary>
        public MTLBuffer Buffer;
        /// <summary>
        /// Its contents' CPU address.
        /// </summary>
        public byte* Pointer;
        /// <summary>
        /// Its length in bytes.
        /// </summary>
        public ulong Size;
        /// <summary>
        /// The <see cref="_version"/> it was last filled at; -1 before the first fill.
        /// </summary>
        public long Version = -1;
    }

    /// <summary>
    /// An empty table sized to <see cref="MetalShaders.TableCapacity"/>.
    /// </summary>
    public MetalBindlessTable(MetalContext ctx)
    {
        this._ctx = ctx;
        for (int k = 0; k < _free.Length; k++)
        {
            _free[k] = new Stack<uint>();
            _textures[k] = new ulong[MetalShaders.TableCapacity[k]];
            _samplers[k] = new ulong[MetalShaders.TableCapacity[k]];
            _live[k] = new MTLTexture?[MetalShaders.TableCapacity[k]];
        }
    }

    #endregion

    #region Properties

    /// <inheritdoc/>
    public uint[] Capacity => MetalShaders.TableCapacity;

    /// <inheritdoc/>
    public int Registered
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _next.Length; i++) n += (int)_next[i] - _free[i].Count;
            return n;
        }
    }

    /// <summary>
    /// The table is the argument buffer, not a set: the renderers bind it through <c>BindBindlessTable</c>.
    /// </summary>
    public int SetIndex => 0;

    #endregion

    #region Registration

    /// <inheritdoc/>
    public uint Register(BindlessKind kind, in SampledTexture texture)
    {
        int k = (int)kind;
        uint index = _free[k].Count > 0 ? _free[k].Pop()
            : _next[k] < Capacity[k] ? _next[k]++
            : throw new InvalidOperationException($"bindless {kind} array is full ({Capacity[k]})");
        Set(kind, index, texture);
        return index;
    }

    /// <inheritdoc/>
    public BindlessHandle Register(GpuTexture texture, GpuSampler sampler, bool shadow = false)
    {
        var kind = BindlessTable.KindFor(texture.Desc.Format, texture.Desc.Kind, shadow);
        return new BindlessHandle(kind, Register(kind, new SampledTexture(sampler, texture.View())));
    }

    #endregion

    #region Entries

    /// <inheritdoc/>
    public void Update(BindlessKind kind, uint index, in SampledTexture texture) => Set(kind, index, texture);

    /// <inheritdoc/>
    public void Update(BindlessHandle handle, in SampledTexture texture) => Update(handle.Kind, handle.Index, texture);

    /// <summary>
    /// Writes an entry: its texture's and sampler's resource ids (zero for a null texture), and bumps <see cref="_version"/>.
    /// </summary>
    void Set(BindlessKind kind, uint index, in SampledTexture texture)
    {
        int k = (int)kind;
        if (texture.IsNull)
        {
            _textures[k][index] = 0;
            _live[k][index] = null;
            if (MetalShaders.HasSamplers(k)) _samplers[k][index] = 0;
        }
        else
        {
            var image = _ctx.ViewTexture(texture.View);
            _textures[k][index] = image.GpuResourceID._impl;
            _live[k][index] = image;
            if (MetalShaders.HasSamplers(k)) _samplers[k][index] = _ctx.Samplers.Backend(texture.Sampler).GpuResourceID._impl;
        }

        _version++;
    }

    /// <inheritdoc/>
    public void Free(BindlessKind kind, uint index) => _pendingFree.Add((_ctx.FrameNumber, kind, index));

    /// <inheritdoc/>
    public void Free(BindlessHandle handle) => Free(handle.Kind, handle.Index);

    #endregion

    #region Frames

    /// <inheritdoc/>
    public void BeginFrame(int slot)
    {
        // The frames that could have read an index freed when frame f was open are done once the ring wraps past it.
        for (int i = 0; i < _pendingFree.Count;)
        {
            var (frame, kind, index) = _pendingFree[i];
            if (frame + MetalContext.FrameRingDepth > _ctx.FrameNumber)
            {
                i++;
                continue;
            }

            _free[(int)kind].Push(index);
            Set(kind, index, default);
            _pendingFree.RemoveAt(i);
        }
    }

    /// <summary>
    /// Nothing to do: a slot's argument buffer refills when it is next asked for (<see cref="BufferFor"/>).
    /// </summary>
    public void EndFrame() { }

    #endregion

    #region Buffers

    /// <summary>
    /// The stage's argument buffer for this frame slot, refilled from the entries when one changed since the last fill; null
    /// when the stage's layout has no members (it never reads the table).
    /// </summary>
    public MTLBuffer? BufferFor(MetalTableLayout layout, int slot)
    {
        if (layout.IsEmpty) return null;
        if (!_slots.TryGetValue((layout, slot), out var made))
        {
            var shared = _ctx.NewSharedBuffer((ulong)layout.Entries * 8, $"bindless table {slot}");
            made = _slots[(layout, slot)] = new Slot { Buffer = shared.Buffer, Pointer = shared.Pointer, Size = shared.Size };
        }

        if (made.Version == _version) return made.Buffer;
        for (int k = 0; k < BindlessTable.ArrayCount; k++)
        {
            int at = layout.TextureAt(k);
            if (at >= 0) Copy(_textures[k], made.Pointer + at * 8);
            at = layout.SamplerAt(k);
            if (at >= 0) Copy(_samplers[k], made.Pointer + at * 8);
        }

        made.Version = _version;
        return made.Buffer;

        static void Copy(ulong[] entries, byte* to)
        {
            fixed (ulong* from = entries) Buffer.MemoryCopy(from, to, entries.Length * 8, entries.Length * 8);
        }
    }

    /// <summary>
    /// Tells the encoder which textures the argument buffer's entries hold: a resource the encoder does not know about is not made resident
    /// and reads as nothing (the manual argument buffer's price).
    /// </summary>
    public void UseResources(MetalCommandList list)
    {
        foreach (var kind in _live)
        {
            foreach (var texture in kind)
            {
                if (texture is { NativePtr: not 0 } image) list.UseResource(image);
            }
        }
    }

    #endregion

    #region Lifetime

    /// <summary>
    /// Releases the argument buffers.
    /// </summary>
    public void Dispose()
    {
        foreach (var (_, made) in _slots) made.Buffer.Dispose();
        _slots.Clear();
    }

    #endregion
}
