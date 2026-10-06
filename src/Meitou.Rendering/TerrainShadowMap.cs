using System.Diagnostics;
using System.Numerics;
using Meitou.Data.World;

using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The terrain's shadow over the whole world for the Meitou shadows (beyond the cascades' range): for the sun's direction, per sample
/// of the world height grid (2049², 144 units apart) the height of the top of the shadow the land towards the sun casts there, and the
/// distance to the land that casts it. A point below that top is in shadow; the distance widens the penumbra. Built on the GPU by a
/// doubling sweep (each pass looks twice as far towards the sun: 12 passes cover the world) into two RG32F targets, again only when the
/// sun has moved. The receiver reads it with one fetch (<see cref="MeitouShadowShaders.Functions"/>, <c>msTerrain</c>).
/// <para>
/// Phase 8 (docs/renderer-native.md 8): the heights and the targets are native textures and the sweep renders into them in one native
/// segment of its own. The finished map still reaches its consumer (<c>ShadowPass.Meitou</c>, which binds it to a GL unit) as a GL name:
/// each target is imported into VkGl (<see cref="IGlInterop.Import"/>), and the import has GL's default sampler state, so the name is given
/// the linear, clamped state the GL texture had. Those few GL calls stay until the seam can import with a sampler state or the consumer takes
/// <see cref="Sampled"/> (reported in docs/renderer-native.md 8.1).
/// </para>
/// </summary>
sealed unsafe class TerrainShadowMap : IDisposable
{
    readonly IGl gl;
    /// <summary>The native GPU API the map is made and drawn with.</summary>
    public GpuContext Gpu { get; }
    readonly int size;
    readonly float spacing;
    readonly Texture heights;
    readonly SampledTexture heightsSampled;
    readonly LegacyProgram sweep;
    readonly SamplerSlot sourceSlot;
    readonly UniformHandle uSize, uFirst, uStep, uDrop, uDistance;
    readonly Texture[] targets = new Texture[2];
    readonly SampledTexture[] targetsSampled = new SampledTexture[2];
    readonly uint[] imported = new uint[2];

    Vector3 builtFor;
    int result = -1;

    /// <summary>Rebuilt when the sun's direction has turned by more than this (radians; about 0.1°).</summary>
    public const float RebuildAngle = 0.0017f;

    /// <param name="coarse">The whole-world raw heights, <paramref name="size"/>² samples (WorldScene.Coarse).</param>
    public TerrainShadowMap(IGl gl, GpuContext gpu, ushort[] coarse, int size)
    {
        this.gl = gl;
        Gpu = gpu;
        this.size = size;
        spacing = WorldLayout.WorldSize / (float)(size - 1);
        // GL's R16 and RG32F, one level each (VkGl sampled only level 0 of them: linear without mips), linear and clamped.
        var linear = gpu.Samplers.Get(SamplerDesc.FromGl(TextureMinFilter.Linear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge,
            TextureWrapMode.Repeat, false, DepthFunction.Lequal, false, 1, false, 0));
        using (var batch = gpu.Uploads.Begin())
        {
            heights = batch.Create(new TextureDesc(GlConventions.VkFormat(InternalFormat.R16), size, size, Name: "terrain shadow map heights"));
            batch.Write(heights, 0, 0, new Silk.NET.Vulkan.Rect2D(default, new((uint)size, (uint)size)),
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(coarse.AsSpan(0, size * size)));
            for (int i = 0; i < 2; i++)
                targets[i] = batch.Create(new TextureDesc(GlConventions.VkFormat(InternalFormat.RG32f), size, size,
                    Use: TextureUse.Sampled | TextureUse.ColourTarget, Name: "terrain shadow map"));
        }
        heightsSampled = new SampledTexture(linear, heights.View(), heights.Image);
        var interop = gpu.Interop!;
        for (int i = 0; i < 2; i++)
        {
            targetsSampled[i] = new SampledTexture(linear, targets[i].View(), targets[i].Image);
            // The consumer's GL name, with the sampler state the GL texture had (an import starts with GL's defaults).
            imported[i] = interop.Import(targets[i]);
            gl.BindTexture(TextureTarget.Texture2D, imported[i]);
            foreach (var (name, value) in GlSampling)
                gl.TexParameter(TextureTarget.Texture2D, name, value);
        }
        gl.BindTexture(TextureTarget.Texture2D, 0);
        // Native (docs/renderer-native.md 7.1, wave 3 agent B, step P): VkGl's SPIR-V and layout, made once, never inside a draw.
        sweep = LegacyProgram.Create(gpu, ShadowShaders.FullscreenVertex, MeitouShadowShaders.SweepFragment, "terrain shadow sweep");
        (sourceSlot, uSize, uFirst, uStep, uDrop, uDistance) = (sweep.Sampler("uSource"), sweep.Uniform("uSize"), sweep.Uniform("uFirst"),
            sweep.Uniform("uStep"), sweep.Uniform("uDrop"), sweep.Uniform("uDistance"));
        format = new AttachmentFormats(targets[0].Desc.Format, Silk.NET.Vulkan.Format.Undefined);
        // What VkGl made of the GL state the sweep set (no culling, depth or blending; all channels; filled).
        state = new DrawState(Silk.NET.Vulkan.CullModeFlags.None, GlConventions.FrontFace(FrontFaceDirection.Ccw), false, false, Silk.NET.Vulkan.CompareOp.LessOrEqual,
            false, 0, 0, BlendState.Off, Silk.NET.Vulkan.ColorComponentFlags.RBit | Silk.NET.Vulkan.ColorComponentFlags.GBit | Silk.NET.Vulkan.ColorComponentFlags.BBit |
            Silk.NET.Vulkan.ColorComponentFlags.ABit, Silk.NET.Vulkan.PolygonMode.Fill, false, false);
    }

    static readonly (TextureParameterName Name, int Value)[] GlSampling =
    [
        (TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear), (TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear),
        (TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge), (TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge),
    ];

    readonly AttachmentFormats format;
    readonly DrawState state;
    GraphicsPipeline? pipeline;

    /// <summary>The finished map as a GL name (0 before the first build or when the sun is too high for any terrain shadow).</summary>
    public uint Texture => result < 0 ? 0 : imported[result];
    /// <summary>The finished map as a native texture with its sampler (linear, clamped), or null as for <see cref="Texture"/>.</summary>
    public SampledTexture? Sampled => result < 0 ? null : targetsSampled[result];
    public int Size => size;
    public float Spacing => spacing;
    /// <summary>GPU work of the last rebuild is not timed separately; this counts rebuilds (for the log).</summary>
    public int Builds { get; private set; }
    public double LastBuildCpuMs { get; private set; }

    /// <summary>Whether the sun is low enough to leave the land's shadows anywhere (a sun straight up casts none).</summary>
    public static bool Applies(Vector3 toSun) => toSun.X * toSun.X + toSun.Z * toSun.Z > 1e-6f;

    /// <summary>
    /// Rebuilds the map when the sun has moved since the last build: one native segment of its own (outside any pass), each doubling pass
    /// its own rendering into one target reading the other, a full barrier between passes. Leaves GL's state as it was.
    /// </summary>
    public bool Update(Vector3 toSun)
    {
        toSun = Vector3.Normalize(toSun);
        if (!Applies(toSun)) { result = -1; return false; }
        if (result >= 0 && Vector3.Dot(toSun, builtFor) > MathF.Cos(RebuildAngle)) return false;
        var watch = Stopwatch.StartNew();
        long timing = StepTiming.Now();
        float horizontal = MathF.Sqrt(toSun.X * toSun.X + toSun.Z * toSun.Z);
        var direction = new Vector2(toSun.X, toSun.Z) / horizontal;   // samples (x, z) towards the sun
        float tangent = toSun.Y / horizontal;

        int passes = 1;
        while ((1 << (passes - 1)) < size) passes++;
        int write = 0;
        var interop = Gpu.Interop!;
        pipeline ??= Gpu.Pipelines.Get(state.Pipeline(sweep.Program, sweep.VertexLayout([]), Silk.NET.Vulkan.PrimitiveTopology.TriangleList, format, "terrain shadow sweep"));
        var viewport = new Silk.NET.Vulkan.Viewport(0, 0, size, size, 0, 1);
        var scissor = new Silk.NET.Vulkan.Rect2D(default, new((uint)size, (uint)size));
        sweep.Set(uSize, (float)size);
        var cmd = interop.BeginNative("terrain shadow sweep");
        for (int pass = 0; pass < passes; pass++)
        {
            float step = pass == 0 ? 1 : 1 << (pass - 1);   // the first pass reaches one sample, the k-th 2^(k-1) more
            sweep.Set(uFirst, pass == 0 ? 1 : 0);
            sweep.Set(uStep, direction.X * step, direction.Y * step);
            sweep.Set(uDrop, step * spacing * tangent);
            sweep.Set(uDistance, step * spacing);
            sweep.Bind(sourceSlot, pass == 0 ? heightsSampled : targetsSampled[1 - write]);
            var target = targets[write];
            cmd.BeginRendering(new RenderingDesc(new RenderTarget(target.Attachment(), Silk.NET.Vulkan.AttachmentLoadOp.Load, default, target.Image), default, size, size));
            cmd.SetViewport(viewport);
            cmd.SetScissor(scissor);
            cmd.SetRaster(state.Cull, state.Front);
            cmd.SetDepth(state.DepthTest, state.DepthWrite, state.Compare);
            cmd.SetDepthBias(state.BiasEnable, state.BiasConstant, state.BiasSlope);
            cmd.BindPipeline(pipeline);
            sweep.Flush(cmd);
            cmd.Draw(3);
            cmd.EndRendering();
            cmd.Barrier(BarrierBatch.Full);   // the next pass reads what this one wrote (VkGl: a full barrier before each pass)
            write = 1 - write;
        }
        interop.EndNative(cmd);
        result = 1 - write;
        builtFor = toSun;
        StepTiming.Add(StepTiming.Sweep, timing, passes);
        Builds++;
        LastBuildCpuMs = watch.Elapsed.TotalMilliseconds;
        return true;
    }

    public void Dispose()
    {
        for (int i = 0; i < 2; i++) { gl.DeleteTexture(imported[i]); targets[i].Dispose(); }
        heights.Dispose();
        sweep.Dispose();
    }
}
