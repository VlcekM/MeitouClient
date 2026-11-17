using Meitou.Game.Rendering.Frame;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// The characters' traced ambient occlusion (docs/render-post.md "Traced occlusion on characters"): <see cref="Gi.CharacterOcclusionPass"/> over the
/// scene, then the ambient light it found blocked taken off the characters' pixels.
/// </summary>
public sealed unsafe partial class PostProcess
{
    /// <summary>
    /// The blocked ambient light off the characters' pixels (<see cref="Gi.CharacterOcclusionPass.Apply"/>).
    /// </summary>
    sealed class CharacterOcclusionApplyPass : GiPass
    {
        /// <summary>
        /// The traced visibility.
        /// </summary>
        public readonly SamplerSlot Traced;

        /// <summary>
        /// How much of the blocked light is taken off.
        /// </summary>
        public readonly UniformHandle Strength;

        /// <summary>
        /// The sun's term target (its alpha: what the fragment took of the sky beside its diffuse light).
        /// </summary>
        public readonly SamplerSlot Term;

        /// <summary>
        /// 1 while <see cref="Term"/> is bound.
        /// </summary>
        public readonly UniformHandle TermOn;

        /// <summary>
        /// Compiles the apply and looks up its slots.
        /// </summary>
        public CharacterOcclusionApplyPass(GpuContext gpu) : base(gpu, Gi.CharacterOcclusionPass.Apply, "post character occlusion") =>
            (Traced, Strength, Term, TermOn) = (P.Sampler("uTraced"), P.Uniform("uTracedStrength"), P.Sampler("uTerm"), P.Uniform("uTermOn"));
    }

    /// <summary>
    /// The apply, made when first used.
    /// </summary>
    CharacterOcclusionApplyPass? _characterOcclusionApply;


    /// <summary>
    /// The traced visibility at the render size.
    /// </summary>
    Target2D? _characterVisibility;

    /// <summary>
    /// The last frame's gather (the two swap each frame): a ray's hit on a character reads the light gathered there.
    /// </summary>
    Target2D? _characterGatherLast;

    /// <summary>
    /// The last frame's gather pooled over a block of pixels (<see cref="Gi.GiShaders.GatherPool"/>), for the rays' hits on characters.
    /// </summary>
    Target2D? _characterGatherPooled;

    /// <summary>
    /// The frame <see cref="_characterVisibility"/> was traced in.
    /// </summary>
    long _characterVisibilityFrame = -1;

    /// <summary>
    /// Whether this frame's characters should mark themselves as receivers for <see cref="RunCharacterOcclusion"/> (the surface target's bit 19).
    /// </summary>
    internal bool CharacterOcclusionWanted => Options.CharacterOcclusion && GtaoOn && GiResolve > 0 && SupportsStorage(InternalFormat.Rgba16F);

    /// <summary>
    /// Traces the characters' occlusion. <paramref name="scene"/> must hold this frame's traced scene with its characters.
    /// </summary>
    internal void RunCharacterOcclusion(Gi.GiProbes? rays, Gi.GiScene? scene)
    {
        var (colour, depth, surface) = (_sceneColour, _sceneDepth, _giAlbedo);
        if (!CharacterOcclusionWanted || !_haveScene || colour is null || depth is null || surface is null) return;
        if (scene?.Top is null || scene.CharactersShown == 0) return;

        MarkScene();
        bool hadLast = _characterVisibility is not null && _characterVisibilityFrame == _frameIndex - 1;
        (_characterVisibility, _characterGatherLast) = (_characterGatherLast, _characterVisibility);
        if (_characterVisibility is null)
        {
            using var batch = Gpu.Uploads.Begin();
            _characterVisibility = Make(batch, _width, _height, InternalFormat.Rgba16F, TextureMinFilter.Nearest, "post character gather");
        }
        if (_characterGatherPooled is null)
        {
            using var batch = Gpu.Uploads.Begin();
            _characterGatherPooled = Make(batch, _width, _height, InternalFormat.Rgba16F, TextureMinFilter.Nearest, "post character gather pooled");
        }
        CloseSegment();

        // The characters' indirect light, gathered by rays (GiShaders.CharacterGather), a texel a pixel.
        float tanY = MathF.Tan(_fovNow * 0.5f);
        var rot = _viewRotation;
        var frame = new Gi.GiProbes.ReflectionFrame(_characterVisibility.Texture, surface.Texture, depth.Texture, _eyeNow,
            new(rot.M11, rot.M21, rot.M31), new(rot.M12, rot.M22, rot.M32), ViewWindow.BackFor(rot, _fovNow, _aspectNow),
            ViewWindow.Tan(tanY * _aspectNow, tanY), _planes, _width, _height, 1);
        if (rays is null) return;

        var last = hadLast ? _characterGatherLast?.Texture : null;
        if (!rays.TraceCharacterGather((CommandList)Segment(), scene, frame, last, _characterGatherPooled.Texture)) return;
        CloseSegment();
        _characterVisibilityFrame = _frameIndex;

        // The ambient light the rays found blocked, off the scene; then the floor, as the lamps' shadows (the stored albedo's rounding).
        var a = _characterOcclusionApply ??= new CharacterOcclusionApplyPass(Gpu);
        SetGiView(a);
        Bind(a.P, a.Traced, _characterVisibility);
        a.P.Set(a.Strength, PostOptions.CharacterOcclusionStrength);
        var term = TracedTermTarget is not null ? _tracedTerm : null;
        Bind(a.P, a.Term, term);
        a.P.Set(a.TermOn, term is not null ? 1f : 0f);
        a.P.ApplyGlobals();   // the probes, the sky's ambient and the haze
        Draw(a.P, colour.Attachment, colour.Format, _width, _height, _width, _height, LampShadowState);

        var floor = _lampShadowFloor ??= new LampShadowFloorPass(Gpu);
        Draw(floor.P, colour.Attachment, colour.Format, _width, _height, _width, _height, LampShadowFloorState);
        Stamp("character occlusion");
        CloseSegment();
    }
}
