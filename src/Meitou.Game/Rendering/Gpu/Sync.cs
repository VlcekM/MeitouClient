namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// How a pass uses a resource (docs/renderer-native.md 2.9). The backend turns it into its own masks; a combination says what one side of a
/// dependency reads or writes (the stages are merged).
/// </summary>
[Flags]
public enum Access
{
    /// <summary>
    /// No access.
    /// </summary>
    None = 0,
    /// <summary>
    /// Written as a colour attachment.
    /// </summary>
    ColourTarget = 1,
    /// <summary>
    /// Written (and tested) as a depth attachment.
    /// </summary>
    DepthTarget = 2,
    /// <summary>
    /// Read as a depth attachment (tested, not written).
    /// </summary>
    DepthRead = 4,
    /// <summary>
    /// Sampled by shaders.
    /// </summary>
    Sampled = 8,
    /// <summary>
    /// Read as storage by shaders.
    /// </summary>
    StorageRead = 16,
    /// <summary>
    /// Written as storage by shaders.
    /// </summary>
    StorageWrite = 32,
    /// <summary>
    /// Copied from.
    /// </summary>
    TransferSrc = 64,
    /// <summary>
    /// Copied into.
    /// </summary>
    TransferDst = 128,
    /// <summary>
    /// Read as indirect draw or dispatch arguments.
    /// </summary>
    IndirectRead = 256,
    /// <summary>
    /// Read as vertex attributes or indices.
    /// </summary>
    VertexRead = 512,
    /// <summary>
    /// Read as a uniform buffer.
    /// </summary>
    UniformRead = 1024,
    /// <summary>
    /// Presented.
    /// </summary>
    Present = 2048,

    /// <summary>
    /// Everything: the seam between two passes that know nothing of each other.
    /// </summary>
    Any = ~0,
}

/// <summary>
/// The part of a texture a use covers (the levels and the layers).
/// </summary>
public readonly record struct GpuRange(uint BaseLevel, uint Levels, uint BaseLayer, uint Layers);

/// <summary>
/// A resource a pass reads or writes: a <see cref="GpuTexture"/>, a <see cref="GpuBuffer"/> or any other object identifying it.
/// </summary>
public readonly record struct PassUse(object Resource, Access Access, GpuRange? Range = null);

/// <summary>
/// A dependency the backend turns into its own barrier: what happened before (<see cref="From"/>) and what happens now (<see cref="To"/>). The
/// backend decides whether it needs one at all (on Metal, Apple silicon tracks the hazards itself).
/// </summary>
public readonly record struct GpuDependency(Access From, Access To)
{
    /// <summary>
    /// Everything before against everything after (the seam).
    /// </summary>
    public static readonly GpuDependency Full = new(Access.Any, Access.Any);
}

/// <summary>
/// The last access of every resource a frame's native passes touched, and the dependency a new access needs (a read after a read needs none; anything
/// involving a write gets an execution and a memory dependency). A seam crossing orders everything (<see cref="AssumeFullBarrier"/>). Render thread
/// only.
/// </summary>
public sealed class ResourceStates
{
    /// <summary>
    /// The last access of each resource, by reference.
    /// </summary>
    readonly Dictionary<object, Access> _last = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The dependencies <paramref name="uses"/> needs after what was recorded, in <paramref name="dependencies"/> (the count returned); the new
    /// accesses become the last ones.
    /// </summary>
    public int Plan(ReadOnlySpan<PassUse> uses, Span<GpuDependency> dependencies)
    {
        int count = 0;
        foreach (var u in uses)
        {
            if (_last.TryGetValue(u.Resource, out var previous))
            {
                // Write then anything: make the write available and visible. Read then write: an execution dependency suffices.
                if (Writes(previous)) dependencies[count++] = new GpuDependency(previous, u.Access);
                else if (Writes(u.Access)) dependencies[count++] = new GpuDependency(previous, u.Access);
            }

            _last[u.Resource] = u.Access;
        }

        return count;
    }

    /// <summary>
    /// Whether an access writes (the backend's own table says what it needs).
    /// </summary>
    static bool Writes(Access a) => (a & (Access.ColourTarget | Access.DepthTarget | Access.StorageWrite | Access.TransferDst)) != 0;

    /// <summary>
    /// Plans and records the barrier for <paramref name="uses"/> (one call, nothing when no hazard).
    /// </summary>
    public void Use(IGpuCommandList cmd, ReadOnlySpan<PassUse> uses)
    {
        Span<GpuDependency> dependencies = stackalloc GpuDependency[uses.Length];
        int count = Plan(uses, dependencies);
        if (count > 0) cmd.Barrier(dependencies[..count]);
    }

    /// <summary>
    /// After a full barrier (a seam crossing, the start of a frame): nothing is pending.
    /// </summary>
    public void AssumeFullBarrier() => _last.Clear();

    /// <summary>
    /// The resources tracked now (since the last full barrier).
    /// </summary>
    public int Tracked => _last.Count;
}

/// <summary>
/// A labelled native pass: declares its uses (the dependencies from <see cref="ResourceStates"/>), opens a debug label and closes it on dispose.
/// <c>using var pass = new GpuPass(frame, cmd, "shadows c2", uses);</c>
/// </summary>
public readonly ref struct GpuPass
{
    /// <summary>
    /// The list the pass's label is on.
    /// </summary>
    readonly IGpuCommandList _cmd;

    /// <summary>
    /// Records the barrier for the pass's uses and opens its debug label.
    /// </summary>
    public GpuPass(GpuFrame frame, IGpuCommandList cmd, string label, ReadOnlySpan<PassUse> uses)
    {
        this._cmd = cmd;
        frame.States.Use(cmd, uses);
        cmd.BeginLabel(label);
    }

    /// <summary>
    /// Closes the pass's debug label.
    /// </summary>
    public void Dispose() => _cmd.EndLabel();
}
