using System.Globalization;
using System.Text;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Meitou.Rendering.Gpu;

/// <summary>A 64-bit FNV-1a hash: stable across processes (draw logs from two runs must compare), unlike <see cref="HashCode"/>.</summary>
public static class StableHash
{
    public static ulong Of(ReadOnlySpan<byte> data, ulong seed = 14695981039346656037UL)
    {
        ulong h = seed;
        foreach (byte b in data) { h ^= b; h *= 1099511628211UL; }
        return h;
    }
}

/// <summary>
/// Everything one draw is made of, in the form both sides log (docs/renderer-native.md 7.6): the pipeline state, the dynamic state, the
/// attachments, the descriptors (textures as sampler and view handles, buffers as handle:offset:range, or a hash of the bytes when the
/// memory is per-frame host-visible memory whose address differs between runs and paths), the vertex and index buffers and the counts.
/// </summary>
public sealed class DrawRecord
{
    public string Label = "";
    public ulong ColourImage, DepthImage;
    public ulong Program;
    public PrimitiveTopology Topology;
    public Format ColourFormat, DepthFormat;
    public int Samples = 1;
    public bool Blend;
    public BlendFactor Src = BlendFactor.One, Dst = BlendFactor.Zero;
    public ColorComponentFlags Mask;
    public bool PolygonLine, AlphaToCoverage, DepthClamp;
    public string Vertex = "";
    public (float X, float Y, float W, float H, float Min, float Max) Viewport;
    public (int X, int Y, int W, int H) Scissor;
    public CullModeFlags Cull;
    public FrontFace Front;
    public bool DepthTest, DepthWrite, BiasEnable;
    public CompareOp Compare;
    public float BiasConstant, BiasSlope;
    public readonly SortedDictionary<uint, string> Set0 = [];
    public string Set1 = "";
    public string PushConstants = "";
    public readonly SortedDictionary<uint, string> VertexBuffers = [];
    public string Index = "";
    public string Call = "";

    public string Format(int number)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(512);
        sb.Append('#').Append(number).Append(" [").Append(Label).Append("] ");
        sb.Append("tgt c=").Append(ColourImage.ToString("x", c)).Append(" d=").Append(DepthImage.ToString("x", c));
        sb.Append(" | prog=").Append(Program.ToString("x16", c)).Append(" topo=").Append(Topology).Append(" fmt=").Append(ColourFormat).Append(',').Append(DepthFormat).Append('x').Append(Samples);
        sb.Append(" blend=").Append(Blend ? $"{Src}:{Dst}" : "off").Append(" mask=").Append((int)Mask).Append(" line=").Append(PolygonLine ? 1 : 0)
          .Append(" a2c=").Append(AlphaToCoverage ? 1 : 0).Append(" clamp=").Append(DepthClamp ? 1 : 0).Append(" vtx=").Append(Vertex);
        sb.Append(" | vp=").Append(string.Create(c, $"{Viewport.X:R},{Viewport.Y:R},{Viewport.W:R},{Viewport.H:R},{Viewport.Min:R},{Viewport.Max:R}"))
          .Append(" sc=").Append(Scissor.X).Append(',').Append(Scissor.Y).Append(',').Append(Scissor.W).Append(',').Append(Scissor.H)
          .Append(" cull=").Append((int)Cull).Append(" front=").Append((int)Front)
          .Append(" depth=").Append(DepthTest ? 1 : 0).Append(DepthWrite ? 1 : 0).Append(':').Append(Compare)
          .Append(" bias=").Append(BiasEnable ? 1 : 0).Append(string.Create(c, $":{BiasConstant:R}:{BiasSlope:R}"));
        sb.Append(" | s0:");
        foreach (var (b, d) in Set0) sb.Append(' ').Append(b).Append('=').Append(d);
        sb.Append(" | s1: ").Append(Set1).Append(" | pc: ").Append(PushConstants).Append(" | vb:");
        foreach (var (b, d) in VertexBuffers) sb.Append(' ').Append(b).Append('=').Append(d);
        sb.Append(" | ib=").Append(Index).Append(" | ").Append(Call);
        return sb.ToString();
    }
}

/// <summary>
/// The per-draw log of one frame (docs/renderer-native.md 7.6), written by VkGl (at each translated draw) and by <see cref="CommandList"/>
/// (at each native draw) into one file in recording order. <c>MEITOU_DRAW_LOG=&lt;file&gt;</c> turns it on for frame
/// <c>MEITOU_DRAW_LOG_FRAME</c> (default 3, after the first frames' uploads) of a run; <c>meitou-tools draw-log-diff a b</c> compares two.
/// The native side keeps the state the command list set, since a draw's line needs all of it.
/// </summary>
public sealed unsafe class DrawLog : IDisposable
{
    readonly TextWriter writer;
    readonly HostMemory host;
    int number;
    // Native state as recorded.
    readonly DrawRecord state = new();
    readonly Stack<string> labels = new();
    // Bound vertex buffers by location; their stride and rate come from the pipeline bound at the draw (as in Vulkan), not at the bind.
    readonly Dictionary<uint, BufferBinding> vertex = [];
    BufferBinding index;
    IndexType indexType;

    public DrawLog(TextWriter writer, HostMemory host)
    {
        this.writer = writer;
        this.host = host;
    }

    /// <summary>The log the environment asks for, when <paramref name="frame"/> is the one to record; else null.</summary>
    public static string? RequestedPath(long frame)
    {
        string? path = Environment.GetEnvironmentVariable("MEITOU_DRAW_LOG");
        if (string.IsNullOrEmpty(path)) return null;
        long want = long.TryParse(Environment.GetEnvironmentVariable("MEITOU_DRAW_LOG_FRAME"), out long f) ? f : 3;
        return frame == want ? path : null;
    }

    public int Draws => number;

    public void Write(DrawRecord r) => writer.WriteLine(r.Format(++number));

    /// <summary>A comment line (frame boundaries, seam crossings); the diff tool ignores lines starting with ';'.</summary>
    public void Note(string text) => writer.WriteLine("; " + text);

    /// <summary>Describes a buffer range a draw reads: its bytes' hash when it is per-frame host memory, else handle:offset:range.</summary>
    public string Describe(Buffer buffer, ulong offset, ulong size)
    {
        if (buffer.Handle == 0) return "null";
        if (host.Contains(buffer))
        {
            var bytes = host.Read(buffer, offset, size == Vk.WholeSize ? 0 : size);
            return $"h{StableHash.Of(bytes):x16}/{size}";
        }
        return $"{buffer.Handle:x}:{offset}:{(size == Vk.WholeSize ? "all" : size.ToString(CultureInfo.InvariantCulture))}";
    }

    public static string Hash(ReadOnlySpan<byte> bytes) => bytes.IsEmpty ? "-" : StableHash.Of(bytes).ToString("x16", CultureInfo.InvariantCulture);

    // ---- the native side (CommandList) ----

    internal void Label(string? name)
    {
        if (name is null) { if (labels.Count > 0) labels.Pop(); }
        else labels.Push(name);
        state.Label = labels.Count > 0 ? labels.Peek() : "";
    }

    internal void BeginRendering(in RenderingDesc d)
    {
        state.ColourImage = d.Colour.IsNull ? 0 : d.Colour.Image.Handle;
        state.DepthImage = d.Depth.IsNull ? 0 : d.Depth.Image.Handle;
    }

    internal void Pipeline(GraphicsPipeline p)
    {
        var d = p.Desc;
        state.Program = ProgramHash(d.Program);
        state.Topology = d.Topology;
        (state.ColourFormat, state.DepthFormat, state.Samples) = (d.Targets.Colour, d.Targets.Depth, d.Targets.Samples);
        state.Blend = d.Blend.Enable;
        (state.Src, state.Dst) = d.Blend.Enable ? (d.Blend.Src, d.Blend.Dst) : (BlendFactor.One, BlendFactor.Zero);
        state.Mask = d.ColourMask;
        state.PolygonLine = d.Polygon == Silk.NET.Vulkan.PolygonMode.Line;
        state.AlphaToCoverage = d.AlphaToCoverage;
        state.DepthClamp = d.DepthClamp;
        state.Vertex = d.Vertex.ToString();
        vertexLayout = d.Vertex;
    }

    VertexLayout? vertexLayout;

    /// <summary>The hash a program is logged by: of the module code given to the driver (the same for VkGl and a legacy program).</summary>
    public static ulong ProgramHash(ShaderProgram p) => ProgramHash(p.VertexCode ?? p.ComputeCode ?? [], p.FragmentCode ?? []);

    public static ulong ProgramHash(byte[] vertex, byte[] fragment) => StableHash.Of(fragment, StableHash.Of(vertex));

    internal void Viewport(in Viewport v) => state.Viewport = (v.X, v.Y, v.Width, v.Height, v.MinDepth, v.MaxDepth);
    internal void Scissor(in Rect2D r) => state.Scissor = (r.Offset.X, r.Offset.Y, (int)r.Extent.Width, (int)r.Extent.Height);
    internal void Raster(CullModeFlags cull, FrontFace front) => (state.Cull, state.Front) = (cull, front);
    internal void Front(FrontFace front) => state.Front = front;
    internal void Depth(bool test, bool write, CompareOp op) => (state.DepthTest, state.DepthWrite, state.Compare) = (test, write, op);
    internal void Bias(bool enable, float constant, float slope) => (state.BiasEnable, state.BiasConstant, state.BiasSlope) = (enable, constant, slope);

    internal void VertexBuffers(uint first, ReadOnlySpan<BufferBinding> bindings)
    {
        for (int i = 0; i < bindings.Length; i++) vertex[first + (uint)i] = bindings[i];
    }

    internal void IndexBuffer(BufferBinding b, IndexType type) => (index, indexType) = (b, type);

    internal void Sets(uint first, ReadOnlySpan<DescriptorSet> sets, ReadOnlySpan<uint> offsets) { }

    /// <summary>The default blocks a legacy program's set 1 holds at the next draw (their CPU bytes).</summary>
    public void DefaultBlocks(ReadOnlySpan<byte> vertexBlock, ReadOnlySpan<byte> fragmentBlock) => state.Set1 = $"v={Hash(vertexBlock)} f={Hash(fragmentBlock)}";

    /// <summary>The whole of set 0 as a legacy program has it bound at the next draw (logged even when the push was skipped as unchanged).</summary>
    public void Descriptors(ReadOnlySpan<WriteDescriptorSet> writes) => Push(0, writes);

    internal void Push(uint set, ReadOnlySpan<WriteDescriptorSet> writes)
    {
        if (set != 0) return;
        state.Set0.Clear();
        foreach (var w in writes) state.Set0[w.DstBinding] = DescribeWrite(in w);
    }

    /// <summary>A descriptor write as logged: a sampler and view, or a buffer range.</summary>
    public string DescribeWrite(in WriteDescriptorSet w) => w.DescriptorType switch
    {
        DescriptorType.CombinedImageSampler => $"{w.PImageInfo->Sampler.Handle:x}:{w.PImageInfo->ImageView.Handle:x}",
        DescriptorType.UniformBuffer or DescriptorType.StorageBuffer => Describe(w.PBufferInfo->Buffer, w.PBufferInfo->Offset, w.PBufferInfo->Range),
        _ => w.DescriptorType.ToString(),
    };

    internal void PushConstants(uint offset, ReadOnlySpan<byte> bytes) => state.PushConstants = $"{offset}:{Hash(bytes)}";

    internal void Draw(bool indexed, uint count, uint instances, uint first, int vertexOffset, uint firstInstance)
    {
        Fill(state, indexed, count, instances, first, vertexOffset, firstInstance);
        Write(state);
    }

    internal void Indirect(Buffer args, ulong offset, uint count, uint stride)
    {
        state.Call = $"indirect {Describe(args, offset, (ulong)count * stride)} n={count}";
        Write(state);
    }

    void Fill(DrawRecord r, bool indexed, uint count, uint instances, uint first, int vertexOffset, uint firstInstance)
    {
        r.VertexBuffers.Clear();
        foreach (var (loc, b) in vertex)
            if (vertexLayout is { } layout && layout.Inputs.Any(x => x.Location == loc))
            {
                var input = layout.Inputs.First(x => x.Location == loc);
                r.VertexBuffers[loc] = DescribeVertex(b.Buffer, b.Offset, input.Stride, input.PerInstance, instances, firstInstance, vertexOffset);
            }
        r.Index = indexed ? DescribeIndex(index.Buffer, index.Offset, indexType, first, count) : "-";
        r.Call = indexed ? $"indexed {count} x{instances}" : $"draw {count} x{instances} first={first}";
    }

    /// <summary>A vertex buffer as logged: per-instance data in host memory by the hash of the instances drawn; other host memory by its
    /// stride only (the range a per-vertex draw reads is not known here); device memory by handle and offset.</summary>
    public string DescribeVertex(Buffer buffer, ulong offset, uint stride, bool perInstance, uint instances, uint firstInstance, int vertexOffset = 0)
    {
        if (stride == 0) return "const";
        if (host.Contains(buffer))
            return perInstance ? Describe(buffer, offset + (ulong)firstInstance * stride, (ulong)instances * stride) : $"host/{stride}";
        // The draw's first instance and vertex offset are folded into the offset, so binding at an offset and drawing from an index log alike.
        return $"{buffer.Handle:x}:{(long)offset + (perInstance ? (long)firstInstance * stride : (long)vertexOffset * stride)}/{stride}";
    }

    /// <summary>An index buffer as logged: the hash of the indices drawn when in host memory, else handle and offset.</summary>
    public string DescribeIndex(Buffer buffer, ulong offset, IndexType type, uint firstIndex, uint count)
    {
        ulong size = type == IndexType.Uint16 ? 2u : 4u;
        if (host.Contains(buffer)) return Describe(buffer, offset + firstIndex * size, count * size);
        return $"{buffer.Handle:x}:{offset + firstIndex * size}:{type}";
    }

    public void Dispose() => writer.Dispose();
}
