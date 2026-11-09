using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Shaders;

namespace Meitou.Game.Rendering.Terrain;

/// <summary>
/// A native program of the terrain with its vertex inputs from the reflection: the layout and buffers a GL vertex array feeds it, as VkGl feeds a
/// GL program (<see cref="LegacyProgram.VertexLayout"/>: a disabled attribute reads GL's constant through a stride-0 binding).
/// </summary>
internal sealed class TerrainProgram : IDisposable
{
    /// <summary>
    /// The context the program and its pipelines belong to.
    /// </summary>
    readonly IGpuContext _ctx;

    /// <summary>
    /// The native program.
    /// </summary>
    public readonly GpuProgram P;

    /// <summary>
    /// The vertex input locations the reflection lists, one per slot.
    /// </summary>
    readonly int[] _locations;

    /// <summary>
    /// The scalar kind of each location in <see cref="_locations"/>.
    /// </summary>
    readonly ScalarKind[] _kinds;

    /// <summary>
    /// The program's own input locations (below the placements' rows): 0 .. Own − 1.
    /// </summary>
    public readonly int Own;

    /// <summary>
    /// The last layout made (returned again for the same inputs).
    /// </summary>
    VertexLayout? _last;

    /// <summary>
    /// Compiles the program (tessellated when both <paramref name="control"/> and <paramref name="evaluation"/> are given) and reads its vertex
    /// inputs; at most four own inputs.
    /// </summary>
    public TerrainProgram(IGpuContext ctx, GpuSetLayout[] sets, string vertex, string fragment, string name,
        string? control = null, string? evaluation = null)
    {
        this._ctx = ctx;
        P = control is null || evaluation is null
            ? ctx.Shaders.Native(vertex, fragment, name, sets, NativeShaders.PushBytes)
            : ctx.Shaders.NativeTessellated(vertex, control, evaluation, fragment, name, sets, NativeShaders.PushBytes);
        var inputs = P.Info.VertexReflection!.Inputs;
        _locations = [.. inputs.SelectMany(i => Enumerable.Range(i.Location, i.Slots))];
        _kinds = [.. inputs.SelectMany(i => Enumerable.Repeat(i.Kind, i.Slots))];
        Own = _locations.Where(l => l < TerrainShaders.MeshInstanceLocation).DefaultIfEmpty(-1).Max() + 1;
        if (Own > 4) throw new NotSupportedException($"{name}: more than four own vertex inputs");
    }

    /// <summary>
    /// The vertex layout for the attributes by location: a missing one reads a constant through a stride-0 binding (or stride 16 without constant
    /// vertex attributes).
    /// </summary>
    public VertexLayout VertexLayout(ReadOnlySpan<LegacyProgram.Attribute?> byLocation)
    {
        Span<VertexInput> inputs = stackalloc VertexInput[_locations.Length];
        for (int i = 0; i < inputs.Length; i++)
        {
            int loc = _locations[i];
            inputs[i] = loc < byLocation.Length && byLocation[loc] is { } a
                ? new VertexInput((uint)loc, a.Format, a.Stride, a.PerInstance)
                : new VertexInput((uint)loc, GlConventions.DummyVertexFormat(_kinds[i]), _ctx.ConstantVertexAttributes ? 0u : 16u, false);
        }
        if (_last is { } l && inputs.SequenceEqual(l.Inputs)) return l;
        return _last = new VertexLayout(inputs.ToArray());
    }

    /// <summary>
    /// Locations 0 .. <see cref="Own"/> − 1 into <paramref name="result"/>: each attribute's buffer, GL's constant where none.
    /// </summary>
    public void Buffers(ReadOnlySpan<LegacyProgram.Attribute?> byLocation, Span<GpuBufferBinding> result)
    {
        for (int loc = 0; loc < Own; loc++)
        {
            int input = Array.IndexOf(_locations, loc);
            result[loc] = loc < byLocation.Length && byLocation[loc] is { } a
                ? a.Buffer
                : _ctx.ConstantVertexAttributes
                    ? new GpuBufferBinding(_ctx.DummyVertex, GlConventions.DummyVertexOffset(input >= 0 ? _kinds[input] : ScalarKind.Float))
                    : new GpuBufferBinding(_ctx.DummyVertexArray(input >= 0 ? _kinds[input] : ScalarKind.Float), 0);
        }
    }

    /// <summary>
    /// Forgets the program's pipelines and frees it.
    /// </summary>
    public void Dispose()
    {
        _ctx.Pipelines.Forget(P);
        _ctx.Shaders.Dispose(P);
    }
}
