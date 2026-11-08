using System.Globalization;
using System.Numerics;

namespace Meitou.Game.Rendering.Post;

/// <summary>How the scene gets from its render size to the display: drawn at full size, edges smoothed by FXAA (off), or temporally reconstructed from
/// jittered frames at a render scale (the built-in TAA, AMD FSR, NVIDIA DLSS). docs/engine.md "Upscaling".</summary>
public enum UpscalerKind { Off, Taa, Fsr, Dlss, MetalFx }

/// <summary>The Frame generation setting: off, AMD's FSR 3 frame-interpolation swapchain, or NVIDIA's DLSS frame generation through Streamline (docs/render-post.md).</summary>
public enum FrameGenerationMethod { Off, Fsr, Dlss }

/// <summary>The upscaler options of <see cref="PostOptions"/>: which one, the render scale (per axis) and the sharpening.</summary>
public sealed class UpscaleOptions
{
    /// <summary>DLSS by default (TAA runs instead where it is not available): the Meitou anti-aliasing (Off is the game's FXAA).</summary>
    public UpscalerKind Kind = UpscalerKind.Dlss;
    /// <summary>The temporal method the Meitou anti-aliasing switch turns on (the last one chosen; DLSS until then).</summary>
    public UpscalerKind Preferred = UpscalerKind.Dlss;
    /// <summary>Render size over display size per axis; null selects <see cref="DefaultScale"/> for temporal Meitou rendering, native when off.</summary>
    public float? Scale;
    /// <summary>The Meitou default with an upscaler: Quality (the High preset's).</summary>
    public const float DefaultScale = 1 / 1.5f;
    public const float UltraQualityScale = 0.83f;
    /// <summary>0..1: FSR's own sharpening (RCAS); after TAA and DLSS the composite's (PostProcessShaders.Composite).</summary>
    public float Sharpness = 0.3f;
    /// <summary>Set on the command line: the saved settings don't override it.</summary>
    public bool Explicit;
    /// <summary>DLSS's render preset for every mode, Streamline's and NGX's number (0 their default, 10 = J .. 13 = M; <see cref="DlssPresets"/>).</summary>
    public uint DlssPreset;

    /// <summary>The DLSS presets the settings offer: the defaults (K at Quality, Balanced and DLAA, M at Performance, L at Ultra Performance) and the transformer ones.</summary>
    public static readonly (string Name, uint Number)[] DlssPresets = [("Default", 0), ("J", 10), ("K", 11), ("L", 12), ("M", 13)];

    /// <summary>The quality modes' scales, FSR's and DLSS's shared ratios (1.5, 1.7, 2, 3 per axis).</summary>
    public static readonly (string Name, float Scale)[] Modes =
        [("native", 1f), ("ultra-quality", UltraQualityScale), ("quality", 1 / 1.5f), ("balanced", 1 / 1.7f), ("performance", 0.5f), ("ultra", 1 / 3f)];

    public bool Temporal => Kind != UpscalerKind.Off;
    /// <summary>The render scale; with Off too (FXAA then runs on the plainly scaled-up picture: no jitter, no history).</summary>
    public float EffectiveScale => Scale ?? (Temporal ? DefaultScale : 1);

    public static float ParseScale(string s)
    {
        foreach (var (name, scale) in Modes) if (string.Equals(name, s, StringComparison.OrdinalIgnoreCase)) return scale;
        float v = float.Parse(s, CultureInfo.InvariantCulture);
        if (v is < 0.25f or > 1) throw new ArgumentException("--render-scale must be 0.25..1 or native, ultra-quality, quality, balanced, performance, ultra");
        return v;
    }

    /// <summary>The render size for a display size: each axis scaled and rounded, at least 1.</summary>
    public (int Width, int Height) RenderSize(int width, int height)
    {
        float s = EffectiveScale;
        return (Math.Max((int)MathF.Round(width * s), 1), Math.Max((int)MathF.Round(height * s), 1));
    }

    public void CopyFrom(UpscaleOptions other)
    {
        (Kind, Preferred, Scale, Sharpness, DlssPreset) = (other.Kind, other.Preferred, other.Scale, other.Sharpness, other.DlssPreset);
    }

    public string Describe() => Kind == UpscalerKind.Off && EffectiveScale >= 1 ? "off" : string.Create(CultureInfo.InvariantCulture, $"{Kind.ToString().ToLowerInvariant()} at {EffectiveScale:0.###}");
}

/// <summary>
/// Sub-pixel camera jitter for temporal upscaling: Halton(2, 3) offsets in render pixels (−0.5..0.5), the sequence length
/// growing with the square of the upscale ratio so every display pixel gets samples (FSR's rule: 8 × ratio²).
/// The offset moves the projected picture by that many render pixels (<see cref="Apply"/>).
/// </summary>
public static class Jitter
{
    public static float Halton(int index, int radix)
    {
        float result = 0, f = 1;
        for (int i = index; i > 0; i /= radix)
        {
            f /= radix;
            result += f * (i % radix);
        }
        return result;
    }

    public static int PhaseCount(int renderWidth, int displayWidth)
    {
        float ratio = displayWidth / (float)Math.Max(renderWidth, 1);
        return Math.Max((int)MathF.Ceiling(8 * ratio * ratio), 8);
    }

    /// <summary>The offset of frame <paramref name="frame"/> in render pixels (Halton indices start at 1: index 0 would be the centre).</summary>
    public static Vector2 Offset(long frame, int phases)
    {
        int i = (int)(frame % phases) + 1;
        return new Vector2(Halton(i, 2) - 0.5f, Halton(i, 3) - 0.5f);
    }

    /// <summary>The projection moved by <paramref name="pixels"/> render pixels: a clip-space translation scaled by w (row vectors).</summary>
    public static Matrix4x4 Apply(Matrix4x4 projection, Vector2 pixels, int renderWidth, int renderHeight) =>
        pixels == Vector2.Zero ? projection : projection * Matrix4x4.CreateTranslation(2 * pixels.X / renderWidth, 2 * pixels.Y / renderHeight, 0);
}

/// <summary>
/// One frame's inputs to an external upscaler (FSR, DLSS), as native textures (each kept in GENERAL). Conventions: textures are bottom-up
/// (GL row 0 is the first row of the image), so "up" in them is +row; <see cref="Motion"/> holds, per render pixel, the screen-space
/// motion in UV units from the previous frame to this one without the jitter (previous UV = UV − motion; RG of an RGBA16F);
/// <see cref="Depth"/> is an R32F 0..1 depth of one D3D-style projection with <see cref="Near"/> and <see cref="Far"/> (1 = far, not inverted);
/// <see cref="JitterPixels"/> is the offset the projection was moved by, in render pixels along +column and +row; <see cref="Exposure"/>, when given,
/// is a 1 x 1 R32F holding what the composite multiplies the colour by before its tone map (null: the upscaler measures its own).
/// </summary>
public sealed record UpscaleInputs
{
    public required Gpu.GpuTexture Colour, Depth, Motion, Output;
    public Gpu.GpuTexture? Reactive, Exposure;
    /// <summary>DLSS's render preset (<see cref="UpscaleOptions.DlssPreset"/>).</summary>
    public uint Preset;
    public required int RenderWidth, RenderHeight, DisplayWidth, DisplayHeight;
    public required Vector2 JitterPixels;
    public required float Near, Far, FieldOfView, DeltaSeconds, Sharpness;
    public required bool Reset;
    /// <summary>The camera, for upscalers that want matrices (DLSS): the unjittered projection of <see cref="Depth"/> (eye-relative view to clip),
    /// this frame's clip space to the previous frame's for that projection (<see cref="Reprojection.ClipToPrevious"/>), the eye and its axes.</summary>
    public Matrix4x4 ViewToClip, ClipToPreviousClip;
    public Vector3 Eye, Right, Up, Forward;
    public float Aspect;
}

/// <summary>An upscaler outside the post-processing chain's own TAA, backed by a vendor library (Meitou.Game.Rendering.Upscalers).</summary>
public interface IUpscaler : IDisposable
{
    UpscalerKind Kind { get; }
    string Name { get; }
    /// <summary>Upscales <see cref="UpscaleInputs.Colour"/> into <see cref="UpscaleInputs.Output"/> (RGBA16F, display size), recorded in the frame; false on failure (the chain falls back to TAA).</summary>
    bool Dispatch(UpscaleInputs inputs);
}

/// <summary>Camera reprojection for motion vectors.</summary>
public static class Reprojection
{
    /// <summary>
    /// The matrix (row vectors) from this frame's clip space to the previous frame's, for points of a static world: both views taken
    /// relative to this frame's eye (the previous view shifted by the eye's step), so no large world translations meet in float.
    /// <paramref name="rotation"/> / <paramref name="previousRotation"/> are the view matrices without translation; the projections
    /// are what each frame drew with (this frame's jittered, the previous one's not, so the motion carries no jitter).
    /// </summary>
    public static Matrix4x4 ClipToPrevious(Matrix4x4 rotation, Vector3 eye, Matrix4x4 projection, Matrix4x4 previousRotation, Vector3 previousEye, Matrix4x4 previousProjection)
    {
        if (!Matrix4x4.Invert(rotation * projection, out var inverse)) return Matrix4x4.Identity;
        var previousView = previousRotation;
        var shift = Vector3.TransformNormal(eye - previousEye, previousRotation);
        previousView.M41 = shift.X; previousView.M42 = shift.Y; previousView.M43 = shift.Z;
        return inverse * previousView * previousProjection;
    }
}