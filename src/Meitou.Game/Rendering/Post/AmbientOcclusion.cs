using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using InternalFormat = Meitou.Game.Rendering.Gpu.InternalFormat;
using Target2D = Meitou.Game.Rendering.Post.PostProcess.Target2D;

namespace Meitou.Game.Rendering.Post;

/// <summary>
/// The SSAO at half the render size (<see cref="PostProcessShaders.Ssao"/>): the occlusion from the scene depth, then a separable blur that also
/// takes it off the characters' pixels. <see cref="PostProcess"/> decides when it runs and where the result goes (the scene colour before the
/// particles, the merged layers or the composite).
/// </summary>
sealed class AmbientOcclusion(PostProcess post, AmbientOcclusion.SsaoPass ssao, AmbientOcclusion.BlurPass blur, AmbientOcclusion.SsaoMaskPass mask)
{
    #region Passes

    /// <summary>
    /// The characters' mask at the half size (<see cref="PostProcessShaders.SsaoMask"/>).
    /// </summary>
    internal sealed class SsaoMaskPass : PostProcess.FullscreenProgram
    {
        /// <summary>
        /// The scene colour (its alpha is the characters' mask).
        /// </summary>
        public readonly SamplerSlot Scene;

        /// <summary>
        /// Compiles the program and resolves its handles.
        /// </summary>
        public SsaoMaskPass(GpuContext gpu) : base(gpu, PostProcessShaders.SsaoMask, "post ssao mask") => Scene = P.Sampler("uScene");
    }

    /// <summary>
    /// One direction of the blur (<see cref="PostProcessShaders.SsaoBlur"/>).
    /// </summary>
    internal sealed class BlurPass : PostProcess.FullscreenProgram
    {
        /// <summary>
        /// The occlusion to blur and the characters' mask.
        /// </summary>
        public readonly SamplerSlot Ao, Mask;

        /// <summary>
        /// The texel step of this direction and how much occlusion the characters keep.
        /// </summary>
        public readonly UniformHandle Step, CharacterAo;

        /// <summary>
        /// Compiles the program and resolves its handles.
        /// </summary>
        public BlurPass(GpuContext gpu) : base(gpu, PostProcessShaders.SsaoBlur, "post ssao blur") =>
            (Ao, Mask, Step, CharacterAo) = (P.Sampler("uAo"), P.Sampler("uMask"), P.Uniform("uStep"), P.Uniform("uCharacterAo"));
    }

    /// <summary>
    /// The occlusion from the depth (<see cref="PostProcessShaders.Ssao"/>).
    /// </summary>
    internal sealed class SsaoPass : PostProcess.FullscreenProgram
    {
        /// <summary>
        /// The scene depth and the characters' mask.
        /// </summary>
        public readonly SamplerSlot Depth, Mask;

        /// <summary>
        /// The view's tangents, planes and size, the occlusion's radius, strength and fade, the view's axes and eye, the characters' weight and
        /// the water height, and the contact shadows' terms (none here: they are their own pass, PostProcess.ApplyContactShadows).
        /// </summary>
        public readonly UniformHandle Tan, NearFar, Size, Radius, Strength, FadeStart, FadeEnd, Right, Up, Back, Eye, CharacterOccluder, WaterY,
            Contact;

        /// <summary>
        /// Compiles the program and resolves its handles.
        /// </summary>
        public SsaoPass(GpuContext gpu) : base(gpu, PostProcessShaders.Ssao, "post ssao")
        {
            (Depth, Mask, CharacterOccluder, WaterY) = (P.Sampler("uDepth"), P.Sampler("uMask"), P.Uniform("uCharacterOccluder"),
                P.Uniform("uWaterY"));
            (Tan, NearFar, Size, Radius, Strength, FadeStart, FadeEnd) = (P.Uniform("uTan"), P.Uniform("uNearFar"), P.Uniform("uSize"),
                P.Uniform("uRadius"), P.Uniform("uStrength"), P.Uniform("uFadeStart"), P.Uniform("uFadeEnd"));
            (Right, Up, Back, Eye) = (P.Uniform("uRight"), P.Uniform("uUp"), P.Uniform("uBack"), P.Uniform("uSsaoEye"));
            Contact = P.Uniform("uContact");
        }
    }

    #endregion

    #region Occlusion

    /// <summary>
    /// The blur pass (GTAO's gather takes it too, PostProcess.Gtao).
    /// </summary>
    internal BlurPass Blur => blur;

    /// <summary>
    /// <c>MEITOU_SSAO_STAMPS=1</c>: GPU timestamps between the SSAO's passes (and the merged layers').
    /// </summary>
    internal static readonly bool Stamps = Environment.GetEnvironmentVariable("MEITOU_SSAO_STAMPS") == "1";

    /// <summary>
    /// The occlusion's two half-size targets (the blur ping-pongs between them; after <see cref="Run"/> the result is in <see cref="B"/>).
    /// </summary>
    public Target2D? A { get; internal set; }

    /// <summary>
    /// The finished occlusion after <see cref="Run"/> (the composite and the scene layers read it).
    /// </summary>
    public Target2D? B { get; internal set; }

    /// <summary>
    /// The characters' mask at the half size.
    /// </summary>
    public Target2D? Mask { get; private set; }

    /// <summary>
    /// Whether any character was drawn into the scene this frame (set by the world frame after the scene's characters): none means the scene colour's
    /// alpha, the SSAO's character mask, is 0 everywhere, so with <see cref="MaskSkip"/> the blur does not read it.
    /// </summary>
    public bool CharactersDrawn { get; set; } = true;

    /// <summary>
    /// Meitou: the SSAO blur's read of the character mask left out when no character was drawn (<c>--ab ssao-mask-skip</c>).
    /// </summary>
    public bool MaskSkip { get; set; } = true;

    /// <summary>
    /// The half-size targets, in the post targets' batch.
    /// </summary>
    public void Allocate(IGpuUploadBatch batch, int hw, int hh)
    {
        A = post.Make(batch, hw, hh, InternalFormat.Rg16F, TextureMinFilter.Linear, "post ssao");
        B = post.Make(batch, hw, hh, InternalFormat.Rg16F, TextureMinFilter.Linear, "post ssao");
        Mask = post.Make(batch, hw, hh, InternalFormat.R8, TextureMinFilter.Linear, "post ssao mask");
    }

    /// <summary>
    /// The targets are gone (the post targets were freed).
    /// </summary>
    public void Free() => A = B = Mask = null;

    /// <summary>
    /// The occlusion of this frame into <see cref="B"/>.
    /// </summary>
    public void Run()
    {
        var a = A!;
        var b = B!;
        Generate(a);
        BlurInto(a, b);

        // The composite reads B: swap so the finished result is there.
        (A, B) = (B, A);
    }

    /// <summary>
    /// The occlusion from the depth into <paramref name="a"/>.
    /// </summary>
    void Generate(Target2D a)
    {
        float tanY = MathF.Tan(post.FovY * 0.5f);
        var s = ssao;
        var scene = post.SceneColourTarget;
        post.Bind(s.P, s.Depth, post.SceneDepthTarget);

        // Characters count less as occluders (the mask, as the blur's below): a body lying on the ground darkened a halo round it as wide as itself.
        bool characters = scene is not null && (CharactersDrawn || !MaskSkip);
        if (characters)
        {
            // The characters' mask at this half size first (PostProcessShaders.SsaoMask): the taps below read it instead of the scene colour.
            post.Bind(mask.P, mask.Scene, scene);
            post.Draw(mask.P, Mask!);
        }
        post.Bind(s.P, s.Mask, characters ? Mask : null);
        s.P.Set(s.CharacterOccluder, characters ? PostOptions.SsaoCharacterOccluder : 1f);
        s.P.Set(s.Tan, ViewWindow.Tan(tanY * post.Aspect, tanY));
        s.P.Set(s.NearFar, post.NearPlane, post.FarPlane);
        s.P.Set(s.Size, (float)post.RenderWidth, (float)post.RenderHeight);
        s.P.Set(s.Radius, PostOptions.SsaoRadius);
        s.P.Set(s.Strength, PostOptions.SsaoStrength);

        // Fade out with distance: the occlusion is a detail effect, and far geometry is hazy and has little depth precision.
        s.P.Set(s.FadeStart, PostOptions.SsaoFadeStart);
        s.P.Set(s.FadeEnd, PostOptions.SsaoFadeEnd);

        // The air in front of each surface (haze, weather fog, fog volumes) hides its occlusion: the shader evaluates it at the world position
        // rebuilt from depth.
        var r = post.ViewRotation;
        var eye = post.EyeNow;
        s.P.Set(s.Right, r.M11, r.M21, r.M31);
        s.P.Set(s.Up, r.M12, r.M22, r.M32);
        s.P.Set(s.Back, ViewWindow.BackFor(r, post.FovY, post.Aspect));
        s.P.Set(s.Eye, eye.X, eye.Y, eye.Z);
        s.P.Set(s.WaterY, post.WaterHeight ?? float.MinValue);   // the floor's occlusion fades under the water, which writes no depth
        s.P.Set(s.Contact, default(System.Numerics.Vector4));   // the contact shadows are their own pass (ApplyContactShadows)
        s.P.ApplyGlobals();   // the atmosphere's and the fog volumes' uniforms, through the frame globals
        post.Draw(s.P, a);
        if (Stamps) post.Stamp("ssao gen");
    }

    /// <summary>
    /// The separable blur: across from <paramref name="a"/> into <paramref name="b"/>, then down back into <paramref name="a"/>.
    /// </summary>
    void BlurInto(Target2D a, Target2D b)
    {
        var bl = blur;
        post.Bind(bl.P, bl.Ao, a);
        post.Bind(bl.P, bl.Mask, null);
        bl.P.Set(bl.Step, 1f / a.Width, 0f);
        bl.P.Set(bl.CharacterAo, 1f);
        post.Draw(bl.P, b);
        if (Stamps) post.Stamp("ssao blur x");

        // The second pass also takes the occlusion off the characters' pixels (the scene colour's alpha; only they write it), by
        // SsaoCharacterStrength.
        var scene = post.SceneColourTarget;
        bool characters = scene is not null && (CharactersDrawn || !MaskSkip);
        post.Bind(bl.P, bl.Ao, b);
        post.Bind(bl.P, bl.Mask, characters ? scene : null);
        bl.P.Set(bl.Step, 0f, 1f / a.Height);
        bl.P.Set(bl.CharacterAo, characters ? PostOptions.SsaoCharacterStrength : 1f);
        post.Draw(bl.P, a);
        if (Stamps) post.Stamp("ssao blur y");
    }

    #endregion

    #region Lifetime

    /// <summary>
    /// The passes' programs, in the order the post chain disposed them.
    /// </summary>
    public void Dispose()
    {
        ssao.P.Dispose();
        mask.P.Dispose();
        blur.P.Dispose();
    }

    #endregion
}
