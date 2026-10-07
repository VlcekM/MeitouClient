using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;
using PolygonMode = Silk.NET.Vulkan.PolygonMode;

namespace Meitou.Rendering.Impostors;

/// <summary>
/// The impostor program in the native model (<see cref="ImpostorShaders.VertexNative"/>, <see cref="ImpostorShaders.FragmentNative"/>): six
/// vertices per instance through a shared six-entry index buffer (<see cref="Quad"/>: <c>gl_VertexIndex</c> is the index, 0 to 5, so the quad's
/// corners are the GL text's <c>gl_VertexID % 6</c>), the instance rows at locations 7 to 10 (the foliage's ABI, row 0 w the fade), the atlas and
/// sphere in <see cref="ImpostorPush"/>. Pipelines per (program, segment state, formats), kept for the last two states as the foliage
/// meshes keep theirs. Used by the foliage renderer and the preview.
/// </summary>
internal enum ImpostorProgram { Plain, Caster }

internal sealed class ImpostorDraw : IDisposable
{
    readonly GpuContext gpu;
    readonly NativeProg plain, caster;
    readonly DeviceBuffer quad;
    readonly Dictionary<(ImpostorProgram, DrawStateKey, AttachmentFormats), GraphicsPipeline> pipelines = [];

    /// <summary>What of a <see cref="DrawState"/> is in a pipeline (the dynamic parts are not).</summary>
    readonly record struct DrawStateKey(BlendState Blend, ColorComponentFlags Mask, PolygonMode Polygon, bool AlphaToCoverage, bool DepthClamp);

    public ImpostorDraw(GpuContext gpu, NativeFrame frame)
    {
        this.gpu = gpu;
        plain = new NativeProg(gpu, frame, ImpostorShaders.VertexNative(), ImpostorShaders.FragmentNative(), "impostors");
        caster = new NativeProg(gpu, frame, ImpostorShaders.VertexNative(), ImpostorShaders.DepthFragmentNative(), "impostor casters");
        quad = DeviceBuffer.Create(gpu, 6 * sizeof(uint), BufferUse.Index, "impostor quad");
        using var batch = gpu.Uploads.Begin();
        batch.Write(quad, 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes<uint>([0, 1, 2, 3, 4, 5]));
    }

    /// <summary>Every native program's layout is compatible (the shared push range and sets).</summary>
    public PipelineLayout Layout => plain.P.Layout;

    /// <summary>The six-entry index buffer (0 to 5).</summary>
    public BufferBinding Quad => new(quad.Handle, 0, quad.Size);

    /// <summary>The pipeline for <paramref name="state"/>'s fixed-function part into <paramref name="formats"/>. Render thread (Prepare).</summary>
    public GraphicsPipeline Pipeline(ImpostorProgram which, DrawState state, AttachmentFormats formats)
    {
        var key = (which, new DrawStateKey(state.Blend, state.ColourMask, state.Polygon, state.AlphaToCoverage, state.DepthClamp), formats);
        if (pipelines.TryGetValue(key, out var p)) return p;
        var program = which == ImpostorProgram.Caster ? caster : plain;
        Span<LegacyProgram.Attribute?> rows = new LegacyProgram.Attribute?[FoliageShaders.InstanceLocation + 4];
        for (int a = 0; a < 4; a++)
            rows[FoliageShaders.InstanceLocation + a] = new LegacyProgram.Attribute(default, Format.R32G32B32A32Sfloat, 64, true);
        p = gpu.Pipelines.Get(state.Pipeline(program.P, program.Layout(rows), PrimitiveTopology.TriangleList, formats, program.P.Name));
        pipelines[key] = p;
        return p;
    }

    public void Dispose()
    {
        plain.Dispose();
        caster.Dispose();
        quad.Dispose();
    }
}
