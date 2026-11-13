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
    /// The terrain's colour is resolved over the picture (default; <c>MEITOU_TERRAIN_RESOLVE=0</c>: the patches are drawn a second time for it, for
    /// comparing).
    /// </summary>
    public static bool TerrainResolve { get; set; } = Environment.GetEnvironmentVariable("MEITOU_TERRAIN_RESOLVE") != "0";

    /// <summary>
    /// This frame's targets for the resolved colour (after <see cref="BeginScene"/>): the prepass's (its own colour target, the scene's depth), the
    /// colour pass's (the scene's colour targets, no depth) and the bindless indices of what the colour pass reads. False: the terrain draws its
    /// colour over its patches.
    /// </summary>
    internal bool TerrainResolveTargets(out PassTargets prepass, out PassTargets colour, out (uint G, uint Depth) inputs)
    {
        (prepass, colour, inputs) = (null!, null!, default);
        if (!TerrainResolve || _sceneColour is not { } sceneColour || _sceneDepth is not { } sceneDepth) return false;

        if (_terrainResolve is null || _terrainResolve.Width != _width || _terrainResolve.Height != _height)
        {
            _terrainResolve?.Texture.Dispose();   // released after the frames in flight
            using var batch = Gpu.Uploads.Begin();
            _terrainResolve = Make(batch, _width, _height, InternalFormat.Rgba32F, TextureMinFilter.Nearest, "post terrain resolve");
        }

        var held = _terrainResolve;
        inputs = (Bound(ref _terrainResolveBound, held), Bound(ref _terrainDepthBound, sceneDepth));
        prepass = PassTargets.Of(held.Texture, sceneDepth.Texture);
        colour = PassTargets.Of(sceneColour.Texture, null, GiAlbedoTarget, TracedTermTarget);

        return true;
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
