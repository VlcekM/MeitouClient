namespace Meitou.Rendering.Gpu;

/// <summary>How a pass uses a resource (docs/renderer-native.md 2.9). The backend turns it into its own masks; a
/// combination says what one side of a dependency reads or writes (the stages are merged).</summary>
[Flags]
public enum Access
{
    None = 0,
    ColourTarget = 1, DepthTarget = 2, DepthRead = 4, Sampled = 8, StorageRead = 16, StorageWrite = 32,
    TransferSrc = 64, TransferDst = 128, IndirectRead = 256, VertexRead = 512, UniformRead = 1024, Present = 2048,
    /// <summary>Everything: the seam between two passes that know nothing of each other.</summary>
    Any = ~0,
}

/// <summary>The part of a texture a use covers (the levels and the layers).</summary>
public readonly record struct GpuRange(uint BaseLevel, uint Levels, uint BaseLayer, uint Layers);

/// <summary>A resource a pass reads or writes: a <see cref="GpuTexture"/>, a <see cref="GpuBuffer"/> or any other object identifying it.</summary>
public readonly record struct PassUse(object Resource, Access Access, GpuRange? Range = null);

/// <summary>
/// A dependency the backend turns into its own barrier: what happened before (<see cref="From"/>) and
/// what happens now (<see cref="To"/>). The backend decides whether it needs one at all (on Metal,
/// Apple silicon tracks the hazards itself).
/// </summary>
public readonly record struct GpuDependency(Access From, Access To)
{
    /// <summary>Everything before against everything after (the seam).</summary>
    public static readonly GpuDependency Full = new(Access.Any, Access.Any);
}

/// <summary>
/// The last access of every resource a frame's native passes touched, and the dependency a new access
/// needs (a read after a read needs none; anything involving a write gets an execution and a memory
/// dependency). A seam crossing orders everything (<see cref="AssumeFullBarrier"/>). Render thread only.
/// </summary>
public sealed class ResourceStates
{
    readonly Dictionary<object, Access> last = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The dependencies <paramref name="uses"/> needs after what was recorded, in
    /// <paramref name="dependencies"/> (the count returned); the new accesses become the last ones.
    /// </summary>
    public int Plan(ReadOnlySpan<PassUse> uses, Span<GpuDependency> dependencies)
    {
        int count = 0;
        foreach (var u in uses)
        {
            if (last.TryGetValue(u.Resource, out var previous))
            {
                // Write then anything: make the write available and visible. Read then write: an execution dependency suffices.
                if (Writes(previous)) dependencies[count++] = new GpuDependency(previous, u.Access);
                else if (Writes(u.Access)) dependencies[count++] = new GpuDependency(previous, u.Access);
            }
            last[u.Resource] = u.Access;
        }
        return count;
    }

    /// <summary>Whether an access writes (the backend's own table says what it needs).</summary>
    static bool Writes(Access a) => (a & (Access.ColourTarget | Access.DepthTarget | Access.StorageWrite | Access.TransferDst)) != 0;

    /// <summary>Plans and records the barrier for <paramref name="uses"/> (one call, nothing when no hazard).</summary>
    public void Use(IGpuCommandList cmd, ReadOnlySpan<PassUse> uses)
    {
        Span<GpuDependency> dependencies = stackalloc GpuDependency[uses.Length];
        int count = Plan(uses, dependencies);
        if (count > 0) cmd.Barrier(dependencies[..count]);
    }

    /// <summary>After a full barrier (a seam crossing, the start of a frame): nothing is pending.</summary>
    public void AssumeFullBarrier() => last.Clear();

    public int Tracked => last.Count;
}

/// <summary>
/// A labelled native pass: declares its uses (the dependencies from <see cref="ResourceStates"/>), opens a
/// debug label and closes it on dispose.
/// <c>using var pass = new GpuPass(frame, cmd, "shadows c2", uses);</c>
/// </summary>
public readonly ref struct GpuPass
{
    readonly IGpuCommandList cmd;

    public GpuPass(GpuFrame frame, IGpuCommandList cmd, string label, ReadOnlySpan<PassUse> uses)
    {
        this.cmd = cmd;
        frame.States.Use(cmd, uses);
        cmd.BeginLabel(label);
    }

    public void Dispose() => cmd.EndLabel();
}
