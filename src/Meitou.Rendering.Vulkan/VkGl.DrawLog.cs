using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using GlPolygonMode = Meitou.Rendering.Gpu.PolygonMode;

namespace Meitou.Rendering.Vulkan;

/// <summary>
/// The translated draws' side of the draw log (docs/renderer-native.md 7.6): one <see cref="DrawRecord"/> per draw, in the form
/// <see cref="CommandList"/> writes for native draws, so a ported renderer's log can be compared line by line with the GL one.
/// Only while <see cref="GpuContext.Log"/> is open (one frame of a <c>MEITOU_DRAW_LOG</c> run).
/// </summary>
public sealed unsafe partial class VkGl
{
    readonly DrawRecord logRecord = new();
    readonly List<(uint Loc, VkBuffer Buffer, ulong Offset, uint Stride, bool PerInstance)> logVertex = [];

    /// <summary>At the end of <see cref="PrepareDraw"/>: the pipeline state of the key and the dynamic state as recorded.</summary>
    void LogPrepare(GlProgramObj p, in PipelineKey key)
    {
        var r = logRecord;
        r.Label = "";
        r.ColourImage = passColour?.Texture.Image?.Image.Handle ?? 0;
        r.DepthImage = passDepth?.Texture.Image?.Image.Handle ?? 0;
        r.Program = DrawLog.ProgramHash(p.VertexCode, p.FragmentCode);
        r.Topology = key.Topology;
        (r.ColourFormat, r.DepthFormat, r.Samples) = (key.Colour, key.Depth, key.Samples);
        r.Blend = key.Blend;
        (r.Src, r.Dst) = key.Blend ? (Factor(key.Src), Factor(key.Dst)) : (BlendFactor.One, BlendFactor.Zero);
        r.Mask = (ColorComponentFlags)key.Mask;
        r.PolygonLine = key.Polygon == GlPolygonMode.Line;
        r.AlphaToCoverage = key.AlphaToCoverage;
        r.DepthClamp = key.DepthClamp;
        var inputs = KeyInputs(p, in key);
        r.Vertex = new VertexLayout(inputs).ToString();
        r.Viewport = (sentViewport.X, sentViewport.Y, sentViewport.W, sentViewport.H, 0, 1);
        r.Scissor = sentScissor;
        (r.Cull, r.Front) = (sentCull, sentFront);
        (r.DepthTest, r.DepthWrite, r.Compare) = (sentDepthTest, sentDepthWrite, sentCompare);
        (r.BiasEnable, r.BiasConstant, r.BiasSlope) = (sentBiasEnable, sentBias.Item1, sentBias.Item2);
        r.Set1 = $"v={DrawLog.Hash(p.VertexDefault)} f={DrawLog.Hash(p.FragmentDefault)}";
        r.PushConstants = "";
        logVertex.Clear();
        foreach (var i in inputs) logVertex.Add((i.Location, default, 0, i.Stride, i.PerInstance));
    }

    /// <summary>The whole of set 0 as the draw has it (also when the push was skipped as unchanged).</summary>
    void LogSet0(DrawLog log, ReadOnlySpan<WriteDescriptorSet> writes)
    {
        logRecord.Set0.Clear();
        foreach (var w in writes) logRecord.Set0[w.DstBinding] = log.DescribeWrite(in w);
    }

    void LogVertexBuffer(uint loc, VkBuffer buffer, ulong offset)
    {
        for (int i = 0; i < logVertex.Count; i++)
            if (logVertex[i].Loc == loc) logVertex[i] = logVertex[i] with { Buffer = buffer, Offset = offset };
    }

    void LogDraw(DrawLog log, bool indexed, uint count, uint instances, uint first, VkBuffer index = default, ulong indexOffset = 0, IndexType type = IndexType.Uint32)
    {
        var r = logRecord;
        r.VertexBuffers.Clear();
        foreach (var (loc, buffer, offset, stride, perInstance) in logVertex)
            r.VertexBuffers[loc] = log.DescribeVertex(buffer, offset, stride, perInstance, instances, 0);
        r.Index = indexed ? log.DescribeIndex(index, indexOffset, type, 0, count) : "-";
        r.Call = indexed ? $"indexed {count} x{instances}" : $"draw {count} x{instances} first={first}";
        log.Write(r);
    }
}
