using System.Numerics;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Shaders;

namespace Meitou.Rendering;

/// <summary>
/// A native program with its vertex inputs from the reflection: the layout and buffers a GL vertex array feeds it, as VkGl feeds a GL
/// program (<see cref="LegacyProgram.VertexLayout"/>: a disabled attribute reads GL's constant through a stride-0 binding). Used by the
/// character renderer; <c>WorldObjectRenderer</c> still has its own copy (<c>ObjProg</c>, same code), to be switched after the merge.
/// </summary>
sealed class ReflectedProgram : IDisposable
{
    readonly GpuContext ctx;
    public readonly ShaderProgram P;
    readonly int[] locations;
    readonly ScalarKind[] kinds;
    /// <summary>The program's own input locations (below the instance rows): 0 .. Own − 1.</summary>
    public readonly int Own;
    VertexLayout? last;

    /// <param name="rowLocation">The first per-instance input location; the inputs below it are the mesh's own.</param>
    public ReflectedProgram(GpuContext ctx, NativeFrame frame, string vertex, string fragment, string name, int rowLocation)
    {
        this.ctx = ctx;
        P = frame.Program(vertex, fragment, name);
        var inputs = P.VertexReflection!.Inputs;
        locations = [.. inputs.SelectMany(i => Enumerable.Range(i.Location, i.Slots))];
        kinds = [.. inputs.SelectMany(i => Enumerable.Repeat(i.Kind, i.Slots))];
        Own = locations.Where(l => l < rowLocation).DefaultIfEmpty(-1).Max() + 1;
    }

    public VertexLayout Layout(ReadOnlySpan<LegacyProgram.Attribute?> byLocation)
    {
        Span<VertexInput> inputs = stackalloc VertexInput[locations.Length];
        for (int i = 0; i < inputs.Length; i++)
        {
            int loc = locations[i];
            inputs[i] = loc < byLocation.Length && byLocation[loc] is { } a
                ? new VertexInput((uint)loc, a.Format, a.Stride, a.PerInstance)
                : new VertexInput((uint)loc, GlConventions.DummyVertexFormat(kinds[i]), 0, false);
        }
        if (last is { } l && inputs.SequenceEqual(l.Inputs)) return l;
        return last = new VertexLayout(inputs.ToArray());
    }

    /// <summary>Locations 0 .. <paramref name="count"/> − 1 as one array for one bind: each attribute's buffer, GL's constant where none.</summary>
    public BufferBinding[] Buffers(ReadOnlySpan<LegacyProgram.Attribute?> byLocation, int count)
    {
        var result = new BufferBinding[count];
        for (int loc = 0; loc < count; loc++)
        {
            int input = Array.IndexOf(locations, loc);
            result[loc] = loc < byLocation.Length && byLocation[loc] is { } a
                ? a.Buffer
                : new BufferBinding(ctx.Defaults.DummyVertex.Buffer, GlConventions.DummyVertexOffset(input >= 0 ? kinds[input] : ScalarKind.Float));
        }
        return result;
    }

    public void Dispose()
    {
        ctx.Pipelines.Forget(P);
        P.Dispose();
    }
}

/// <summary>Frustum tests shared by the renderers.</summary>
static class FrustumTests
{
    /// <summary>Whether a sphere is not wholly outside any of the (not necessarily normalised) <paramref name="planes"/>.</summary>
    public static bool SphereVisible(Vector4[] planes, Vector3 centre, float radius)
    {
        foreach (var p in planes)
            if (p.X * centre.X + p.Y * centre.Y + p.Z * centre.Z + p.W < -radius * MathF.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z)) return false;
        return true;
    }
}
