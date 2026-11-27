using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;

using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// The targets of the terrain's resolved colour (docs/render-terrain.md "Resolved colour"; <c>TerrainRenderer.Resolve</c>): the patches' prepass
/// draws into the scene's depth and a target of its own that keeps, per pixel, what the colour needs besides the depth; the colour is then one pass
/// over the picture into the scene's colour targets.
/// </summary>
public sealed partial class PostProcess
{
    /// <summary>
    /// The prepass's own colour target at the render size.
    /// </summary>
    Target2D? _terrainResolve;

    /// <summary>
    /// The bindless registrations of <see cref="_terrainResolve"/> and of the scene's depth, and the textures they were made for.
    /// </summary>
    (GpuTexture? Texture, uint Index) _terrainResolveBound, _terrainDepthBound;

    /// <summary>
    /// The terrain's relief pass's two targets at the render size (TerrainRenderer.Resolve, docs/render-terrain.md "Relief pass"): RGBA16F, the moves
    /// in world units, the shade and the puddles' trough; null while the pass is not wanted.
    /// </summary>
    Target2D? _terrainReliefA, _terrainReliefB;

    /// <summary>
    /// The bindless registrations of <see cref="_terrainReliefA"/> and <see cref="_terrainReliefB"/>.
    /// </summary>
    (GpuTexture? Texture, uint Index) _terrainReliefABound, _terrainReliefBBound;

    /// <summary>
    /// The terrain's colour is resolved over the picture (default; <c>MEITOU_TERRAIN_RESOLVE=0</c>: the patches are drawn a second time for it, for
    /// comparing).
    /// </summary>
    public static bool TerrainResolve { get; set; } = Environment.GetEnvironmentVariable("MEITOU_TERRAIN_RESOLVE") != "0";

    /// <summary>
    /// This frame's targets for the resolved colour (after <see cref="BeginScene"/>): the prepass's (its own colour target, the scene's depth), the
    /// colour pass's (the scene's colour targets, no depth), the relief pass's when <paramref name="relief"/> (else null) and the bindless indices of
    /// what the colour pass reads (the relief's 0 without it). False: the terrain draws its colour over its patches.
    /// </summary>
    bool IWorldPostChain.TerrainResolveTargets(bool relief, out PassTargets prepass, out PassTargets colour, out PassTargets? reliefTargets,
        out TerrainResolveIndices inputs)
    {
        (prepass, colour, reliefTargets, inputs) = (null!, null!, null, default);
        if (!TerrainResolve || _sceneColour is not { } sceneColour || _sceneDepth is not { } sceneDepth) return false;

        var held = Sized(ref _terrainResolve, InternalFormat.Rgba32F, "post terrain resolve");
        inputs = new TerrainResolveIndices(Bound(ref _terrainResolveBound, held), Bound(ref _terrainDepthBound, sceneDepth), 0, 0);
        prepass = PassTargets.Of(held.Texture, sceneDepth.Texture);
        colour = PassTargets.Of(sceneColour.Texture, null, GiAlbedoTarget, TracedTermTarget);
        if (relief) reliefTargets = ReliefTargets(ref inputs);
        else ReleaseReliefTargets();

        return true;
    }

    /// <summary>
    /// The relief pass's targets (made at the render size when missing), their bindless indices put in <paramref name="inputs"/>.
    /// </summary>
    PassTargets ReliefTargets(ref TerrainResolveIndices inputs)
    {
        var a = Sized(ref _terrainReliefA, InternalFormat.Rgba16F, "post terrain relief a");
        var b = Sized(ref _terrainReliefB, InternalFormat.Rgba16F, "post terrain relief b");
        inputs = inputs with { ReliefA = Bound(ref _terrainReliefABound, a), ReliefB = Bound(ref _terrainReliefBBound, b) };

        return PassTargets.Of(a.Texture, null, b.Texture);
    }

    /// <summary>
    /// Frees the relief pass's targets and their registrations while the pass is not wanted (the parallax off, or the pass switched off).
    /// </summary>
    void ReleaseReliefTargets()
    {
        if (_terrainReliefA is null) return;

        foreach (var bound in new[] { _terrainReliefABound, _terrainReliefBBound })
            if (bound.Texture is not null) Gpu.Bindless.Free(BindlessKind.Texture2D, bound.Index);
        (_terrainReliefABound, _terrainReliefBBound) = (default, default);
        _terrainReliefA.Texture.Dispose();   // released after the frames in flight
        _terrainReliefB?.Texture.Dispose();
        (_terrainReliefA, _terrainReliefB) = (null, null);
    }

    /// <summary>
    /// <paramref name="target"/>, made anew in <paramref name="format"/> at the render size when missing or of another size; one of the opaque scene's
    /// transients.
    /// </summary>
    Target2D Sized(ref Target2D? target, InternalFormat format, string name)
    {
        if (target is not null && target.Width == _width && target.Height == _height) return target;

        target?.Texture.Dispose();   // released after the frames in flight
        using var batch = Gpu.Uploads.Begin();
        target = Make(batch, _width, _height, format, TextureMinFilter.Nearest, name, transient: TransientLifetime.Single(TransientPhase.OpaqueScene));

        return target;
    }

    /// <summary>
    /// The bindless index of a target, registered again when the target was made anew.
    /// </summary>
    uint Bound(ref (GpuTexture? Texture, uint Index) bound, Target2D target)
    {
        if (bound.Texture == target.Texture) return bound.Index;
        if (bound.Texture is not null) Gpu.Bindless.Free(BindlessKind.Texture2D, bound.Index);

        var sampled = Sampled(target.Texture, TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToEdge, 1);
        bound = (target.Texture, Gpu.Bindless.Register(BindlessKind.Texture2D, sampled));

        return bound.Index;
    }
}

/// <summary>
/// The bindless indices the terrain's resolve reads (<see cref="IWorldPostChain.TerrainResolveTargets"/>): the prepass's target, the scene's depth and the
/// relief pass's two targets (0 without the pass).
/// </summary>
internal readonly record struct TerrainResolveIndices(uint G, uint Depth, uint ReliefA, uint ReliefB);
