using System.Text.RegularExpressions;
using Meitou.Rendering.Gpu;
using SharpMetal.Metal;

namespace Meitou.Metal;

/// <summary>
/// One stage's argument buffer layout, read from the MSL: the members' offsets in entries (8 bytes each) and the arrays each
/// kind got. Metal packs the members sequentially in declaration order — the MSL's <c>[[id]]</c> decorations do not move them
/// (verified with the pipeline reflection) — and a compiler drops an array a stage never reads, so the offsets differ per
/// program: a program's buffer is filled to its own layout.
/// </summary>
internal sealed class MetalTableLayout
{
    readonly int[] textureAt = Filled();
    readonly int[] samplerAt = Filled();

    static int[] Filled()
    {
        var at = new int[BindlessTable.ArrayCount];
        Array.Fill(at, -1);
        return at;
    }

    /// <summary>The empty layout (a program that does not read the table).</summary>
    public static readonly MetalTableLayout Empty = new();

    /// <summary>The buffer's total entries.</summary>
    public int Entries { get; private set; }

    public bool IsEmpty => Entries == 0;

    public int TextureAt(int kind) => textureAt[kind];
    public int SamplerAt(int kind) => samplerAt[kind];

    /// <summary>The layout of the MSL's argument buffer struct (the first <c>spvDescriptorSetBuffer</c> struct's members, in order).</summary>
    public static MetalTableLayout Parse(string msl)
    {
        var layout = new MetalTableLayout();
        // The table's set is the parameter moved to [[buffer(TableBinding)]]; programs with other sets carry their structs too
        // (the terrain has sets 0 and 2), and the first struct in the file is not the table's.
        var parameter = Regex.Match(msl, @"constant\s+spvDescriptorSetBuffer(?<set>\d+)&[^,\n]*\[\[buffer\(" + MslTranslator.TableBinding + @"\)\]\]");
        if (!parameter.Success) return layout;
        var buffer = Regex.Match(msl, @"struct\s+spvDescriptorSetBuffer" + parameter.Groups["set"].Value + @"\s*\{(?<body>[^}]*)\}");
        if (!buffer.Success) return layout;
        int at = 0;
        foreach (Match member in Regex.Matches(buffer.Groups["body"].Value, @"array<[^,]+(?:<[^>]*>)?,\s*(?<count>\d+)>\s+\w+\s+\[\[id\((?<id>\d+)\)\]\]"))
        {
            int count = int.Parse(member.Groups["count"].Value);
            if (MetalShaders.TableKindOf(uint.Parse(member.Groups["id"].Value)) is var (kind, sampler))
            {
                if (sampler) layout.samplerAt[kind] = at;
                else layout.textureAt[kind] = at;
            }
            at += count;
        }
        layout.Entries = at;
        return layout;
    }
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
    readonly MetalContext ctx;
    readonly ulong[][] textures = new ulong[BindlessTable.ArrayCount][];   // per kind: the entries' texture ids
    readonly ulong[][] samplers = new ulong[BindlessTable.ArrayCount][];   // per kind: the entries' sampler ids (where the layout has them)
    readonly MTLTexture?[][] live = new MTLTexture?[BindlessTable.ArrayCount][];
    readonly Stack<uint>[] free = new Stack<uint>[BindlessTable.ArrayCount];
    readonly uint[] next = new uint[BindlessTable.ArrayCount];
    readonly List<(long Frame, BindlessKind Kind, uint Index)> pendingFree = [];
    readonly Dictionary<(MetalTableLayout Layout, int Slot), Slot> slots = [];

    sealed class Slot
    {
        public MTLBuffer Buffer;
        public byte* Pointer;
        public ulong Size;
        public long Version = -1;
    }

    public MetalBindlessTable(MetalContext ctx)
    {
        this.ctx = ctx;
        for (int k = 0; k < free.Length; k++)
        {
            free[k] = new Stack<uint>();
            textures[k] = new ulong[MetalShaders.TableCapacity[k]];
            samplers[k] = new ulong[MetalShaders.TableCapacity[k]];
            live[k] = new MTLTexture?[MetalShaders.TableCapacity[k]];
        }
    }

    public uint[] Capacity => MetalShaders.TableCapacity;

    public int Registered
    {
        get
        {
            int n = 0;
            for (int i = 0; i < next.Length; i++) n += (int)next[i] - free[i].Count;
            return n;
        }
    }

    /// <summary>The table is the argument buffer, not a set: the renderers bind it through <c>BindBindlessTable</c>.</summary>
    public int SetIndex => 0;

    /// <summary>Bumped when an entry changed (the slots refill from it).</summary>
    long version;

    public uint Register(BindlessKind kind, in SampledTexture texture)
    {
        int k = (int)kind;
        uint index = free[k].Count > 0 ? free[k].Pop() : next[k] < Capacity[k] ? next[k]++ : throw new InvalidOperationException($"bindless {kind} array is full ({Capacity[k]})");
        Set(kind, index, texture);
        return index;
    }

    public BindlessHandle Register(GpuTexture texture, GpuSampler sampler, bool shadow = false)
    {
        var kind = BindlessTable.KindFor(texture.Desc.Format, texture.Desc.Kind, shadow);
        return new BindlessHandle(kind, Register(kind, new SampledTexture(sampler, texture.View())));
    }

    public void Update(BindlessKind kind, uint index, in SampledTexture texture) => Set(kind, index, texture);

    public void Update(BindlessHandle handle, in SampledTexture texture) => Update(handle.Kind, handle.Index, texture);

    void Set(BindlessKind kind, uint index, in SampledTexture texture)
    {
        int k = (int)kind;
        if (texture.IsNull)
        {
            textures[k][index] = 0;
            live[k][index] = null;
            if (MetalShaders.HasSamplers(k)) samplers[k][index] = 0;
        }
        else
        {
            var image = ctx.ViewTexture(texture.View);
            textures[k][index] = image.GpuResourceID._impl;
            live[k][index] = image;
            if (MetalShaders.HasSamplers(k)) samplers[k][index] = ctx.Samplers.Backend(texture.Sampler).GpuResourceID._impl;
        }
        version++;
    }

    public void Free(BindlessKind kind, uint index) => pendingFree.Add((ctx.FrameNumber, kind, index));

    public void Free(BindlessHandle handle) => Free(handle.Kind, handle.Index);

    public void BeginFrame(int slot)
    {
        // The frames that could have read an index freed when frame f was open are done once the ring wraps past it.
        for (int i = 0; i < pendingFree.Count;)
        {
            var (frame, kind, index) = pendingFree[i];
            if (frame + MetalContext.FrameRingDepth > ctx.FrameNumber) { i++; continue; }
            free[(int)kind].Push(index);
            Set(kind, index, default);
            pendingFree.RemoveAt(i);
        }
    }

    public void EndFrame() { }

    /// <summary>
    /// The stage's argument buffer for this frame slot, refilled from the entries when one changed since the last fill; null
    /// when the stage's layout has no members (it never reads the table).
    /// </summary>
    public MTLBuffer? BufferFor(MetalTableLayout layout, int slot)
    {
        if (layout.IsEmpty) return null;
        if (!slots.TryGetValue((layout, slot), out var made))
        {
            var shared = ctx.NewSharedBuffer((ulong)layout.Entries * 8, $"bindless table {slot}");
            made = slots[(layout, slot)] = new Slot { Buffer = shared.Buffer, Pointer = shared.Pointer, Size = shared.Size };
        }
        if (made.Version == version) return made.Buffer;
        for (int k = 0; k < BindlessTable.ArrayCount; k++)
        {
            int at = layout.TextureAt(k);
            if (at >= 0) Copy(textures[k], made.Pointer + at * 8);
            at = layout.SamplerAt(k);
            if (at >= 0) Copy(samplers[k], made.Pointer + at * 8);
        }
        made.Version = version;
        return made.Buffer;

        static void Copy(ulong[] entries, byte* to)
        {
            fixed (ulong* from = entries) Buffer.MemoryCopy(from, to, entries.Length * 8, entries.Length * 8);
        }
    }

    /// <summary>Tells the encoder which textures the argument buffer's entries hold: a resource the encoder does not know about
    /// is not made resident and reads as nothing (the manual argument buffer's price).</summary>
    public void UseResources(MetalCommandList list)
    {
        foreach (var kind in live)
            foreach (var texture in kind)
                if (texture is { NativePtr: not 0 } image) list.UseResource(image);
    }

    public void Dispose()
    {
        foreach (var (_, made) in slots) made.Buffer.Dispose();
        slots.Clear();
    }
}
