using System.Numerics;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Shaders;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// A native program with its vertex inputs from the reflection: the layout and buffers a GL vertex array feeds it, as VkGl feeds a GL
/// program (<see cref="LegacyProgram.VertexLayout"/>: a disabled attribute reads GL's constant through a stride-0 binding). Used by the
/// character renderer; <c>WorldObjectRenderer</c> still has its own copy (<c>ObjProg</c>, same code), to be switched after the merge.
/// </summary>
sealed class ReflectedProgram : IDisposable
{
    /// <summary>
    /// The context the program and its pipelines live in.
    /// </summary>
    readonly IGpuContext _ctx;

    /// <summary>
    /// The native program, built through the frame's program cache.
    /// </summary>
    public readonly GpuProgram P;

    /// <summary>
    /// The vertex input locations the reflection lists, one per slot (a matrix input takes several).
    /// </summary>
    readonly int[] _locations;

    /// <summary>
    /// The scalar kind of each of <see cref="_locations"/>' inputs, for the dummy format a missing attribute reads.
    /// </summary>
    readonly ScalarKind[] _kinds;

    /// <summary>
    /// The program's own input locations (below the instance rows): 0 .. Own − 1.
    /// </summary>
    public readonly int Own;

    /// <summary>
    /// The layout <see cref="Layout"/> made last, returned again while the inputs stay the same.
    /// </summary>
    VertexLayout? _last;

    /// <summary>
    /// Builds the program from <paramref name="vertex"/> and <paramref name="fragment"/> through the frame's cache and reads its vertex inputs'
    /// locations and kinds.
    /// </summary>
    /// <param name="rowLocation">The first per-instance input location; the inputs below it are the mesh's own.</param>
    public ReflectedProgram(IGpuContext ctx, NativeFrame frame, string vertex, string fragment, string name, int rowLocation)
    {
        this._ctx = ctx;
        P = frame.Program(vertex, fragment, name);
        var inputs = P.Info.VertexReflection!.Inputs;
        _locations = [.. inputs.SelectMany(i => Enumerable.Range(i.Location, i.Slots))];
        _kinds = [.. inputs.SelectMany(i => Enumerable.Repeat(i.Kind, i.Slots))];
        Own = _locations.Where(l => l < rowLocation).DefaultIfEmpty(-1).Max() + 1;
    }

    /// <summary>
    /// The vertex layout for the GL attributes by location (<paramref name="byLocation"/>): each input the attribute's format and stride, a missing
    /// one GL's constant (stride 0 where the device has constant vertex attributes, else 16). Reuses the last layout while nothing changed.
    /// </summary>
    public VertexLayout Layout(ReadOnlySpan<LegacyProgram.Attribute?> byLocation)
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
    /// Locations 0 .. <paramref name="count"/> − 1 as one array for one bind: each attribute's buffer, GL's constant where none.
    /// </summary>
    public GpuBufferBinding[] Buffers(ReadOnlySpan<LegacyProgram.Attribute?> byLocation, int count)
    {
        var result = new GpuBufferBinding[count];
        for (int loc = 0; loc < count; loc++)
        {
            int input = Array.IndexOf(_locations, loc);
            result[loc] = loc < byLocation.Length && byLocation[loc] is { } a
                ? a.Buffer
                : _ctx.ConstantVertexAttributes
                ? new GpuBufferBinding(_ctx.DummyVertex, GlConventions.DummyVertexOffset(input >= 0 ? _kinds[input] : ScalarKind.Float))
                : new GpuBufferBinding(_ctx.DummyVertexArray(input >= 0 ? _kinds[input] : ScalarKind.Float), 0);
        }
        return result;
    }

    /// <summary>
    /// Drops the program's pipelines and the program.
    /// </summary>
    public void Dispose()
    {
        _ctx.Pipelines.Forget(P);
        _ctx.Shaders.Dispose(P);
    }
}

/// <summary>
/// Frustum tests shared by the renderers.
/// </summary>
static class FrustumTests
{
    /// <summary>
    /// Whether a sphere is not wholly outside any of the (not necessarily normalised) <paramref name="planes"/>.
    /// </summary>
    public static bool SphereVisible(Vector4[] planes, Vector3 centre, float radius)
    {
        foreach (var p in planes)
        {
            if (p.X * centre.X + p.Y * centre.Y + p.Z * centre.Z + p.W < -radius * MathF.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z))
                return false;
        }

        return true;
    }
}