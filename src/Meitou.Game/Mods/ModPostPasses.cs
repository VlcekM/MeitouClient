namespace Meitou.Modding;

/// <summary>
/// The code mods' post passes (docs/modding.md "Post passes"), in the order they run: by <see cref="PostPass.Order"/>, then by registration.
/// Committed on the boot worker when a mod's <see cref="IMod.Initialize"/> returns; the post chain reads <see cref="All"/> on the render thread.
/// </summary>
internal sealed class ModPostPasses
{
    /// <summary>
    /// The passes in running order; replaced whole when a mod's are committed, so a reader keeps a consistent list.
    /// </summary>
    volatile PostPass[] _passes = [];

    /// <summary>
    /// Every mod's passes, in running order.
    /// </summary>
    public IReadOnlyList<PostPass> All => _passes;

    /// <summary>
    /// Adds one mod's passes after those of the same order already there.
    /// </summary>
    public void Add(IReadOnlyList<PostPass> passes)
    {
        if (passes.Count == 0) return;

        // OrderBy is stable: equal orders keep registration order across mods.
        _passes = [.. _passes.Concat(passes).OrderBy(p => p.Order)];
    }
}
