using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// The characters' traced ambient occlusion (docs/render-post.md "Traced occlusion on characters"): <see cref="Gi.CharacterOcclusionPass"/> over the scene, read
/// by GTAO's apply on the characters' pixels.
/// </summary>
public sealed unsafe partial class PostProcess
{
    Gi.CharacterOcclusionPass? _characterOcclusion;
    Target2D? _characterVisibility;
    /// <summary>The frame <see cref="_characterVisibility"/> was traced in.</summary>
    long _characterVisibilityFrame = -1;

    /// <summary>Whether this frame's characters should mark themselves as receivers for <see cref="RunCharacterOcclusion"/> (the surface target's bit 19).</summary>
    internal bool CharacterOcclusionWanted => GtaoOn && GiResolve > 0 && SupportsStorage(InternalFormat.Rgba16F);

    /// <summary>The traced visibility of this frame's characters, for GTAO's apply; null when it was not traced.</summary>
    Target2D? CharacterVisibilityNow => _characterVisibilityFrame == _frameIndex ? _characterVisibility : null;

    /// <summary>Traces the characters' occlusion. <paramref name="scene"/> must hold this frame's traced scene with its characters.</summary>
    internal void RunCharacterOcclusion(Gi.GiScene? scene)
    {
        var (depth, surface) = (_sceneDepth, _giAlbedo);
        if (!CharacterOcclusionWanted || !_haveScene || depth is null || surface is null || scene?.Top is null || scene.CharactersShown == 0) return;

        MarkScene();
        if (_characterVisibility is null)
        {
            using var batch = Gpu.Uploads.Begin();
            _characterVisibility = Make(batch, _width, _height, InternalFormat.Rgba16F, TextureMinFilter.Nearest, "post character occlusion");
        }
        CloseSegment();
        var pass = _characterOcclusion ??= new Gi.CharacterOcclusionPass(Gpu);
        pass.Run(scene, depth.Texture, surface.Texture, _characterVisibility.Texture, _width, _height, SceneToEye, _eyeNow, 2 * MathF.Tan(_fovNow * 0.5f) / _height,
            PostOptions.CharacterOcclusionRadius);
        _characterVisibilityFrame = _frameIndex;
        Stamp("character occlusion");
    }
}
