using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// A native program of the foliage with its vertex inputs from the reflection: the layout and buffers a GL vertex array feeds it, as VkGl
/// feeds a GL program (<see cref="LegacyProgram.VertexLayout"/>: a disabled attribute reads GL's constant through a stride-0 binding).
/// </summary>
internal sealed class NativeProg : IDisposable
{
    readonly GpuContext ctx;
    public readonly ShaderProgram P;
    readonly int[] locations;
    readonly Meitou.Rendering.Gpu.Shaders.ScalarKind[] kinds;
    /// <summary>The program's own input locations (below the instance rows): 0 .. Own − 1.</summary>
    public readonly int Own;
    VertexLayout? last;

    public NativeProg(GpuContext ctx, NativeFrame frame, string vertex, string fragment, string name)
    {
        this.ctx = ctx;
        P = frame.Program(vertex, fragment, name);
        var inputs = P.VertexReflection!.Inputs;
        locations = [.. inputs.SelectMany(i => Enumerable.Range(i.Location, i.Slots))];
        kinds = [.. inputs.SelectMany(i => Enumerable.Repeat(i.Kind, i.Slots))];
        Own = locations.Where(l => l < FoliageShaders.InstanceLocation).DefaultIfEmpty(-1).Max() + 1;
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
                : new BufferBinding(ctx.Defaults.DummyVertex.Buffer, GlConventions.DummyVertexOffset(input >= 0 ? kinds[input] : Meitou.Rendering.Gpu.Shaders.ScalarKind.Float));
        }
        return result;
    }

    public void Dispose()
    {
        ctx.Pipelines.Forget(P);
        P.Dispose();
    }
}
