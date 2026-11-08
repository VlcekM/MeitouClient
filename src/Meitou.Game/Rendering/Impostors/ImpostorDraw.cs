using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Silk.NET.Vulkan;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;

namespace Meitou.Game.Rendering.Impostors;

/// <summary>
/// The impostor program in the native model (<see cref="ImpostorShaders.VertexNative"/>, <see cref="ImpostorShaders.FragmentNative"/>): six
/// vertices per instance through a shared six-entry index buffer (<see cref="Quad"/>: <c>gl_VertexIndex</c> is the index, 0 to 5, so the quad's
/// corners are the GL text's <c>gl_VertexID % 6</c>), the instance rows at locations 7 to 10 (the foliage's ABI, row 0 w the fade), the atlas and
/// sphere in <see cref="ImpostorPush"/>. Pipelines per (program, segment state, formats), kept for the last two states as the foliage
/// meshes keep theirs. Used by the foliage renderer and the preview.
/// </summary>
internal enum ImpostorProgram { Plain, Caster, Rock, Prepass, AfterPrepass }

internal sealed class ImpostorDraw : IDisposable
{
    readonly IGpuContext _gpu;
    readonly NativeProg _plain, _caster, _rock, _prepass, _afterPrepass;
    readonly DeviceBuffer _quad;
    readonly Dictionary<(ImpostorProgram, DrawStateKey, AttachmentFormats), GpuPipeline> _pipelines = [];

    /// <summary>What of a <see cref="DrawState"/> is in a pipeline (the dynamic parts are not).</summary>
    readonly record struct DrawStateKey(BlendState Blend, ColorMask Mask, PolygonKind Polygon, bool AlphaToCoverage, bool DepthClamp);

    public ImpostorDraw(IGpuContext gpu, NativeFrame frame)
    {
        this._gpu = gpu;
        _plain = new NativeProg(gpu, frame, ImpostorShaders.VertexNative(), ImpostorShaders.FragmentNative(), "impostors");
        _caster = new NativeProg(gpu, frame, ImpostorShaders.VertexNative(), ImpostorShaders.DepthFragmentNative(), "impostor casters");
        _rock = new NativeProg(gpu, frame, ImpostorShaders.RockVertexNative(), ImpostorShaders.RockFragmentNative(), "impostors of rocks");
        _prepass = new NativeProg(gpu, frame, ImpostorShaders.VertexNative(), ImpostorShaders.PrepassFragmentNative(), "impostor prepass");
        _afterPrepass = new NativeProg(gpu, frame, ImpostorShaders.VertexNative(), ImpostorShaders.FragmentAfterPrepassNative(), "impostors after prepass");
        _quad = DeviceBuffer.Create(gpu, 6 * sizeof(uint), BufferUse.Index, "impostor quad");
        using var batch = gpu.Uploads.Begin();
        batch.Write(_quad, 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes<uint>([0, 1, 2, 3, 4, 5]));
    }

    /// <summary>Every native program's layout is compatible (the shared push range and sets).</summary>
    public GpuProgram Program => _plain.P;

    /// <summary>The six-entry index buffer (0 to 5).</summary>
    public GpuBufferBinding Quad => new(_quad.Handle, 0, _quad.Size);

    /// <summary>The pipeline for <paramref name="state"/>'s fixed-function part into <paramref name="formats"/>. Render thread (Prepare).</summary>
    public GpuPipeline Pipeline(ImpostorProgram which, DrawState state, AttachmentFormats formats)
    {
        var key = (which, new DrawStateKey(state.Blend, state.ColourMask, state.Polygon, state.AlphaToCoverage, state.DepthClamp), formats);
        if (_pipelines.TryGetValue(key, out var p)) return p;
        var program = which switch { ImpostorProgram.Caster => _caster, ImpostorProgram.Rock => _rock, ImpostorProgram.Prepass => _prepass, ImpostorProgram.AfterPrepass => _afterPrepass, _ => _plain };
        Span<LegacyProgram.Attribute?> rows = new LegacyProgram.Attribute?[FoliageShaders.InstanceLocation + 4];
        for (int a = 0; a < 4; a++)
            rows[FoliageShaders.InstanceLocation + a] = new LegacyProgram.Attribute(default, InternalFormat.Rgba32F, 64, true);
        p = _gpu.Pipelines.Get(state.Pipeline(program.P, program.Layout(rows), PrimitiveType.Triangles, formats, program.P.Name));
        _pipelines[key] = p;
        return p;
    }

    public void Dispose()
    {
        _plain.Dispose();
        _caster.Dispose();
        _rock.Dispose();
        _prepass.Dispose();
        _afterPrepass.Dispose();
        _quad.Dispose();
    }
}