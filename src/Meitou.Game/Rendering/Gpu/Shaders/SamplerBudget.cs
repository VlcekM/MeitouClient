namespace Meitou.Rendering.Gpu.Shaders;

/// <summary>
/// The Metal fragment-stage sampler budget. Metal exposes 16 direct combined-image-sampler slots to a fragment stage; a native-model
/// program instead reads its textures through the bindless table, whose arrays live in one argument buffer. The 16 limit therefore
/// applies only to samplers outside the bindless set. Renderers that move a texture into the table (debris, and water in MEP-5) call
/// <see cref="Require"/> so an accidental direct sampler fails with a named shader rather than an opaque Metal pipeline error.
/// </summary>
public static class SamplerBudget
{
    public const int MetalFragmentLimit = 16;

    /// <summary>Samplers the stage reads directly (outside <paramref name="bindlessSet"/>); pass a negative set to count every sampler.</summary>
    public static int Direct(IEnumerable<SamplerInfo> samplers, int bindlessSet = -1) =>
        bindlessSet < 0 ? samplers.Count() : samplers.Count(s => s.Set != bindlessSet);

    /// <summary>Throws when <paramref name="samplers"/> has more direct fragment samplers than Metal allows; <paramref name="bindlessSet"/> is
    /// the native model's table set.</summary>
    public static void Require(string program, IEnumerable<SamplerInfo> samplers, int bindlessSet = -1, int limit = MetalFragmentLimit)
    {
        int direct = Direct(samplers, bindlessSet);
        if (direct > limit)
            throw new InvalidOperationException($"{program}: {direct} direct fragment samplers, Metal allows {limit}; move material or frame textures into the bindless set");
    }
}
