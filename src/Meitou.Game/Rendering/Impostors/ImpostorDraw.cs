using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Silk.NET.Vulkan;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;

namespace Meitou.Game.Rendering.Impostors;

/// <summary>
/// Which of <see cref="ImpostorDraw"/>'s programs a draw uses.
/// </summary>
internal enum ImpostorProgram
{
    /// <summary>
    /// The billboards' colour program (<see cref="ImpostorShaders.FragmentNative"/>).
    /// </summary>
    Plain,
    /// <summary>
    /// The shadow casters' depth program (<see cref="ImpostorShaders.DepthFragmentNative"/>).
    /// </summary>
    Caster,
    /// <summary>
    /// The TERRAIN-mode rocks' colour program (<see cref="ImpostorShaders.RockVertexNative"/>, <see cref="ImpostorShaders.RockFragmentNative"/>).
    /// </summary>
    Rock,
    /// <summary>
    /// The billboards' depth prepass (<see cref="ImpostorShaders.PrepassFragmentNative"/>, <see cref="ImpostorPrepass"/>).
    /// </summary>
    Prepass,
    /// <summary>
    /// The lighting after the prepass, with no discard (<see cref="ImpostorShaders.FragmentAfterPrepassNative"/>).
    /// </summary>
    AfterPrepass
}

/// <summary>
/// The impostor program in the native model (<see cref="ImpostorShaders.VertexNative"/>, <see cref="ImpostorShaders.FragmentNative"/>): six vertices
/// per instance through a shared six-entry index buffer (<see cref="Quad"/>: <c>gl_VertexIndex</c> is the index, 0 to 5, so the quad's corners are
/// the GL text's <c>gl_VertexID % 6</c>), the instance rows at locations 7 to 10 (the foliage's ABI, row 0 w the fade), the atlas and sphere in
/// <see cref="ImpostorPush"/>. Pipelines per (program, segment state, formats), kept for the last two states as the foliage meshes keep theirs. Used
/// by the foliage renderer and the preview.
/// </summary>
internal sealed class ImpostorDraw : IDisposable
{
    /// <summary>
    /// The device the programs and pipelines are made on.
    /// </summary>
    readonly IGpuContext _gpu;
    /// <summary>
    /// The five programs (<see cref="ImpostorProgram"/>).
    /// </summary>
    readonly NativeProg _plain, _caster, _rock, _prepass, _afterPrepass;
    /// <summary>
    /// The shared six-entry index buffer (<see cref="Quad"/>).
    /// </summary>
    readonly DeviceBuffer _quad;
    /// <summary>
    /// The pipelines made so far, by program, the fixed-function part of the state and the target formats.
    /// </summary>
    readonly Dictionary<(ImpostorProgram, DrawStateKey, AttachmentFormats), GpuPipeline> _pipelines = [];

    /// <summary>
    /// What of a <see cref="DrawState"/> is in a pipeline (the dynamic parts are not).
    /// </summary>
    readonly record struct DrawStateKey(BlendState Blend, ColorMask Mask, PolygonKind Polygon, bool AlphaToCoverage, bool DepthClamp);

    /// <summary>
    /// Compiles the five programs against <paramref name="frame"/>'s sets and uploads the quad's indices.
    /// </summary>
    public ImpostorDraw(IGpuContext gpu, NativeFrame frame)
    {
        this._gpu = gpu;
        _plain = new NativeProg(gpu, frame, ImpostorShaders.VertexNative(), ImpostorShaders.FragmentNative(), "impostors");
        _caster = new NativeProg(gpu, frame, ImpostorShaders.VertexNative(), ImpostorShaders.DepthFragmentNative(), "impostor casters");
        _rock = new NativeProg(gpu, frame, ImpostorShaders.RockVertexNative(), ImpostorShaders.RockFragmentNative(), "impostors of rocks");
        _prepass = new NativeProg(gpu, frame, ImpostorShaders.VertexNative(), ImpostorShaders.PrepassFragmentNative(), "impostor prepass");
        _afterPrepass = new NativeProg(gpu, frame, ImpostorShaders.VertexNative(), ImpostorShaders.FragmentAfterPrepassNative(),
            "impostors after prepass");
        _quad = DeviceBuffer.Create(gpu, 6 * sizeof(uint), BufferUse.Index, "impostor quad");
        using var batch = gpu.Uploads.Begin();
        batch.Write(_quad, 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes<uint>([0, 1, 2, 3, 4, 5]));
    }

    /// <summary>
    /// Every native program's layout is compatible (the shared push range and sets).
    /// </summary>
    public GpuProgram Program => _plain.P;

    /// <summary>
    /// The six-entry index buffer (0 to 5).
    /// </summary>
    public GpuBufferBinding Quad => new(_quad.Handle, 0, _quad.Size);

    /// <summary>
    /// The pipeline for <paramref name="state"/>'s fixed-function part into <paramref name="formats"/>. Render thread (Prepare).
    /// </summary>
    public GpuPipeline Pipeline(ImpostorProgram which, DrawState state, AttachmentFormats formats)
    {
        var key = (which, new DrawStateKey(state.Blend, state.ColourMask, state.Polygon, state.AlphaToCoverage, state.DepthClamp), formats);
        if (_pipelines.TryGetValue(key, out var p)) return p;

        var program = ProgramOf(which);
        Span<LegacyProgram.Attribute?> rows = new LegacyProgram.Attribute?[FoliageShaders.InstanceLocation + 4];
        for (int a = 0; a < 4; a++)
            rows[FoliageShaders.InstanceLocation + a] = new LegacyProgram.Attribute(default, InternalFormat.Rgba32F, 64, true);

        p = _gpu.Pipelines.Get(state.Pipeline(program.P, program.Layout(rows), PrimitiveType.Triangles, formats, program.P.Name));
        _pipelines[key] = p;
        return p;
    }

    /// <summary>
    /// The program of <paramref name="which"/>.
    /// </summary>
    NativeProg ProgramOf(ImpostorProgram which) => which switch
    {
        ImpostorProgram.Caster => _caster,
        ImpostorProgram.Rock => _rock,
        ImpostorProgram.Prepass => _prepass,
        ImpostorProgram.AfterPrepass => _afterPrepass,
        _ => _plain,
    };

    /// <summary>
    /// Frees the programs and the index buffer.
    /// </summary>
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
