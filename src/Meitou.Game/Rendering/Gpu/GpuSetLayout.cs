namespace Meitou.Rendering.Gpu;

/// <summary>The stages a program's slot is read in (the engine's words; the backend maps them).</summary>
[Flags]
public enum GpuStage
{
    None = 0,
    Vertex = 1,
    Fragment = 2,
    Compute = 4,
    TessControl = 8,
    TessEvaluation = 16,
    /// <summary>The stages of a graphics program, including tessellation where the program uses it.</summary>
    Graphics = Vertex | Fragment | TessControl | TessEvaluation,
    All = Vertex | Fragment | Compute | TessControl | TessEvaluation,
}

/// <summary>What a program's slot holds (the engine's words; the backend maps it to its own).</summary>
public enum GpuSlotKind
{
    /// <summary>A uniform buffer (a block of constants).</summary>
    UniformBuffer = 0,
    /// <summary>A storage buffer (a shader's own buffer).</summary>
    StorageBuffer = 1,
    /// <summary>A texture with its sampler (GL's combined image sampler).</summary>
    CombinedSampler = 2,
    /// <summary>A storage image (GL's image).</summary>
    StorageImage = 3,
}

/// <summary>
/// One slot of a program's set: the binding number, what it holds, the stages that read it, how many
/// entries it has (0 = a runtime-sized array) and whether its offset is given when the set is bound.
/// </summary>
public readonly record struct GpuSlot(uint Binding, GpuSlotKind Kind, GpuStage Stages, uint Count = 1, bool OffsetAtBind = false);

/// <summary>
/// A set of a program's resources, in the engine's words (docs/resource-layer.md): the slots it has and
/// whether the renderers write it for every draw. The backend turns it into whatever its own model needs
/// (Vulkan: a descriptor set layout; Metal: the program's argument table). Two sets with the same slots
/// are the same set.
/// </summary>
public sealed class GpuSetLayout : IEquatable<GpuSetLayout>
{
    /// <summary>The set's slots, by binding number.</summary>
    public GpuSlot[] Slots { get; }

    /// <summary>The renderers write this set's contents for every draw (the backend pushes them when it can).</summary>
    public bool PerDraw { get; }

    /// <summary>This set is the bindless table: the seven runtime-sized arrays of textures the shaders index by number.</summary>
    public bool IsBindless { get; }

    GpuSetLayout(GpuSlot[] slots, bool perDraw, bool bindless)
    {
        Slots = slots;
        PerDraw = perDraw;
        IsBindless = bindless;
    }

    /// <summary>A set the renderers write for every draw.</summary>
    public static GpuSetLayout Written(GpuSlot[] slots) => new(slots, true, false);

    /// <summary>A set the renderers bind once and leave alone.</summary>
    public static GpuSetLayout Fixed(GpuSlot[] slots) => new(slots, false, false);

    /// <summary>The bindless table's set.</summary>
    public static GpuSetLayout Bindless { get; } = new([], false, true);

    public bool Equals(GpuSetLayout? other) =>
        other is not null && PerDraw == other.PerDraw && IsBindless == other.IsBindless && Slots.AsSpan().SequenceEqual(other.Slots);

    public override bool Equals(object? obj) => Equals(obj as GpuSetLayout);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(PerDraw);
        h.Add(IsBindless);
        foreach (var s in Slots) h.Add(s);
        return h.ToHashCode();
    }
}
