using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Sky;

/// <summary>
/// The pipelines of one native program's draws (docs/renderer-native.md 7.5): remembers the last two segment states (the scene's pass and the
/// reflection's multisampled one alternate) and the vertex export they were made for, so a draw compares a few fields instead of building a pipeline
/// description.
/// </summary>
internal sealed class NativeSegment(IGpuContext gpu, LegacyProgram program, PrimitiveType topology, string label)
{
    /// <summary>
    /// What a pipeline is made for besides the program: the formats and the state's blend, mask, polygon mode, alpha to coverage and depth clamp.
    /// </summary>
    readonly record struct Key(AttachmentFormats Formats, BlendState Blend, ColorMask Mask, PolygonKind Polygon,
        bool AlphaToCoverage, bool DepthClamp);

    /// <summary>
    /// The last two segment states (0 the newest).
    /// </summary>
    Key _key0, _key1;

    /// <summary>
    /// The pipelines made for them.
    /// </summary>
    GpuPipeline? _pipeline0, _pipeline1;

    /// <summary>
    /// The vertex exports they were made for (compared by reference).
    /// </summary>
    VertexArrayBindings? _source0, _source1;

    /// <summary>
    /// Every state's pipeline and the vertex export it was made for, behind the last two: a renderer whose draws cycle through more than two
    /// states (the particles' blends) finds it here instead of building a pipeline description.
    /// </summary>
    readonly Dictionary<Key, (VertexArrayBindings? Source, GpuPipeline Pipeline)> _made = [];

    /// <summary>
    /// The pipeline for <paramref name="state"/> into <paramref name="formats"/>, with the vertex inputs of <paramref name="vertices"/> (null: none).
    /// </summary>
    public GpuPipeline Get(DrawState state, AttachmentFormats formats, VertexArrayBindings? vertices)
    {
        var key = new Key(formats, state.Blend, state.ColourMask, state.Polygon, state.AlphaToCoverage, state.DepthClamp);
        if (_pipeline0 is not null && key == _key0 && ReferenceEquals(vertices, _source0)) return _pipeline0!.Value;
        if (_pipeline1 is not null && key == _key1 && ReferenceEquals(vertices, _source1)) return _pipeline1!.Value;
        bool madeBefore = _made.TryGetValue(key, out var known) && ReferenceEquals(known.Source, vertices);
        var made = madeBefore ? known.Pipeline : Make(state, formats, vertices);
        _made[key] = (vertices, made);
        (_key1, _pipeline1, _source1) = (_key0, _pipeline0, _source0);
        (_key0, _pipeline0, _source0) = (key, made, vertices);
        return made;
    }

    /// <summary>
    /// Builds the pipeline description of <paramref name="state"/> and takes its pipeline from the context's cache (made there when new).
    /// </summary>
    GpuPipeline Make(DrawState state, AttachmentFormats formats, VertexArrayBindings? vertices)
    {
        var layout = program.VertexLayout(vertices is null ? default : vertices.Attributes);
        return gpu.Pipelines.Get(state.Pipeline(program.Program, layout, topology, formats, label));
    }
}
