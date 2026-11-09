using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Characters;

/// <summary>
/// The Meitou <c>skin</c> switch's table (docs/character-renderer.md "Skin"): Penner's pre-integrated skin diffuse, the sun's N·L term of a sphere of
/// skin of radius r once light has spread under it by the six-Gaussian skin diffusion profile (d'Eon and Luebke, GPU Gems 3 ch. 14), per colour
/// channel. u: N·L from -1 to 1; v: the square root of the curvature 1/r over <see cref="MaxInverseRadiusMm"/> (0: flat, Lambert), so the gentle
/// curves of faces and limbs get most rows. RGBA16F, linear, sampled with clamped bilinear filtering.
/// </summary>
sealed class SkinScattering : IDisposable
{
    /// <summary>
    /// The table's size in texels: 64 steps of N·L by 64 of curvature.
    /// </summary>
    public const int Width = 64, Height = 64;
    /// <summary>
    /// The table's last row: a radius of 1 mm; anything curved more tightly reads that row.
    /// </summary>
    public const float MaxInverseRadiusMm = 1f;

    /// <summary>
    /// The profile: variance (mm²) and the weights of red, green and blue (GPU Gems 3, ch. 14, table 14-1).
    /// </summary>
    static readonly (float Variance, float R, float G, float B)[] Profile =
    [
        (0.0064f, 0.233f, 0.455f, 0.649f),
        (0.0484f, 0.100f, 0.336f, 0.344f),
        (0.187f, 0.118f, 0.198f, 0.0f),
        (0.567f, 0.113f, 0.007f, 0.007f),
        (1.99f, 0.358f, 0.004f, 0.0f),
        (7.41f, 0.078f, 0.0f, 0.0f),
    ];

    /// <summary>
    /// The device the table was uploaded to (for freeing its bindless slot).
    /// </summary>
    readonly IGpuContext _gpu;
    /// <summary>
    /// The baked table (RGBA16F, <see cref="Width"/> × <see cref="Height"/>).
    /// </summary>
    readonly GpuTexture _texture;

    /// <summary>
    /// The table's index in the bindless 2D array.
    /// </summary>
    public uint Index { get; }

    /// <summary>
    /// Bakes the table on the CPU, uploads it as half floats and registers it with a clamped bilinear sampler (<c>Index</c>).
    /// </summary>
    public SkinScattering(IGpuContext gpu)
    {
        this._gpu = gpu;
        var texels = Bake();
        var data = new Half[texels.Length];
        for (int i = 0; i < texels.Length; i++) data[i] = (Half)texels[i];
        using (var batch = gpu.Uploads.Begin())
        {
            _texture = batch.Create(new TextureDesc(InternalFormat.Rgba16F, Width, Height, Name: "skin scattering"));
            batch.Write(_texture, 0, 0, new PixelRect(0, 0, Width, Height), System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan()));
        }
        var sampler = gpu.Samplers.Get(SamplerDesc.Clamped(TextureMinFilter.Linear, TextureMagFilter.Linear));
        Index = gpu.Bindless.Register(BindlessKind.Texture2D, new SampledTexture(sampler, _texture.View()));
    }

    /// <summary>
    /// The table, RGBA by row (v) then column (u): for a ring of the sphere through the lit point, the light each point of the ring receives (clamped
    /// cos) weighted by the profile at the chord between them, over the profile's total along the ring. Row 0 is a flat surface: plain clamped N·L.
    /// </summary>
    public static float[] Bake(int steps = 1024)
    {
        var texels = new float[Width * Height * 4];
        for (int x = 0; x < Width; x++)
        {
            texels[4 * x] = texels[4 * x + 1] = texels[4 * x + 2] = MathF.Max(0, 2f * x / (Width - 1) - 1f);
            texels[4 * x + 3] = 1;
        }
        var weight = new float[steps * 3];
        var angles = new float[steps];
        for (int y = 1; y < Height; y++)
        {
            float v = (float)y / (Height - 1), inverseRadius = MaxInverseRadiusMm * v * v;

            // Only the arc whose chord is within 8 deviations of the widest Gaussian weighs (beyond, the profile is nothing), so a gentle curve still
            // gets fine steps.
            float span = MathF.Min(MathF.PI, 2f * MathF.Asin(MathF.Min(1f, 4f * MathF.Sqrt(Profile[^1].Variance) * inverseRadius)));
            float totalR = 0, totalG = 0, totalB = 0;
            for (int k = 0; k < steps; k++)
            {
                float a = span * (2f * (k + 0.5f) / steps - 1f);
                angles[k] = a;
                float d = 2f * MathF.Sin(MathF.Abs(a) * 0.5f) / inverseRadius;   // the chord, mm
                float r = 0, g = 0, b = 0;
                foreach (var p in Profile)
                {
                    // Normalised along the ring (1D), so each layer weighs by its share of the light: a 2D normalisation lets the narrowest layer
                    // swamp the ring.
                    float gauss = MathF.Exp(-d * d / (2f * p.Variance)) / MathF.Sqrt(2f * MathF.PI * p.Variance);
                    r += p.R * gauss;
                    g += p.G * gauss;
                    b += p.B * gauss;
                }
                weight[3 * k] = r;
                weight[3 * k + 1] = g;
                weight[3 * k + 2] = b;
                totalR += r;
                totalG += g;
                totalB += b;
            }
            for (int x = 0; x < Width; x++)
            {
                float theta = MathF.Acos(2f * x / (Width - 1) - 1f);
                float r = 0, g = 0, b = 0;
                for (int k = 0; k < steps; k++)
                {
                    float lit = MathF.Max(0, MathF.Cos(theta + angles[k]));
                    r += lit * weight[3 * k];
                    g += lit * weight[3 * k + 1];
                    b += lit * weight[3 * k + 2];
                }
                int o = 4 * (y * Width + x);
                (texels[o], texels[o + 1], texels[o + 2], texels[o + 3]) = (r / totalR, g / totalG, b / totalB, 1);
            }
        }
        return texels;
    }

    /// <summary>
    /// Frees the bindless slot and the texture.
    /// </summary>
    public void Dispose()
    {
        _gpu.Bindless.Free(BindlessKind.Texture2D, Index);
        _texture.Dispose();
    }
}