using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// The characters' traced ambient occlusion (docs/render-post.md "Traced occlusion on characters"): <see cref="Gi.CharacterOcclusionPass"/> over the scene, then
/// the ambient light it found blocked taken off the characters' pixels.
/// </summary>
public sealed unsafe partial class PostProcess
{
    sealed class CharacterOcclusionApplyPass : GiPass
    {
        public readonly SamplerSlot Traced;
        public readonly UniformHandle Strength;
        public CharacterOcclusionApplyPass(GpuContext gpu) : base(gpu, Gi.CharacterOcclusionPass.Apply, "post character occlusion") =>
            (Traced, Strength) = (P.Sampler("uTraced"), P.Uniform("uTracedStrength"));
    }

    CharacterOcclusionApplyPass? _characterOcclusionApply;
    Gi.CharacterOcclusionPass? _characterOcclusion;
    Target2D? _characterVisibility;
    /// <summary>The frame <see cref="_characterVisibility"/> was traced in.</summary>
    long _characterVisibilityFrame = -1;

    /// <summary>Whether this frame's characters should mark themselves as receivers for <see cref="RunCharacterOcclusion"/> (the surface target's bit 19).</summary>
    internal bool CharacterOcclusionWanted => Options.CharacterOcclusion && GtaoOn && GiResolve > 0 && SupportsStorage(InternalFormat.Rgba16F);

    /// <summary>Traces the characters' occlusion. <paramref name="scene"/> must hold this frame's traced scene with its characters.</summary>
    internal void RunCharacterOcclusion(Gi.GiScene? scene)
    {
        var (colour, depth, surface) = (_sceneColour, _sceneDepth, _giAlbedo);
        if (!CharacterOcclusionWanted || !_haveScene || colour is null || depth is null || surface is null || scene?.Top is null || scene.CharactersShown == 0) return;

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

        // The ambient light the rays found blocked, off the scene; then the floor, as the lamps' shadows (the stored albedo's rounding).
        var a = _characterOcclusionApply ??= new CharacterOcclusionApplyPass(Gpu);
        SetGiView(a);
        Bind(a.P, a.Traced, _characterVisibility);
        a.P.Set(a.Strength, PostOptions.CharacterOcclusionStrength);
        a.P.ApplyGlobals();   // the probes, the sky's ambient and the haze
        Draw(a.P, colour.Attachment, colour.Format, _width, _height, _width, _height, LampShadowState);
        var floor = _lampShadowFloor ??= new LampShadowFloorPass(Gpu);
        Draw(floor.P, colour.Attachment, colour.Format, _width, _height, _width, _height, LampShadowFloorState);
        Stamp("character occlusion");
        CloseSegment();
    }
}
