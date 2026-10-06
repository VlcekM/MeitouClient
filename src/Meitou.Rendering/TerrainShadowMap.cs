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
/// </summary>
sealed unsafe class TerrainShadowMap : IDisposable
{
    readonly IGl gl;
    /// <summary>The native GPU API next to <c>gl</c> (docs/renderer-native.md 7.1 step 8); ports use it instead of looking it up.</summary>
    public GpuContext Gpu { get; }
    readonly int size;
    readonly float spacing;
    readonly uint heights;
    readonly LegacyProgram sweep;
    readonly SamplerSlot sourceSlot;
    readonly UniformHandle uSize, uFirst, uStep, uDrop, uDistance;
    readonly uint[] targets = new uint[2], fbos = new uint[2];

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
        heights = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, heights);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 2);
        gl.TexImage2D<ushort>(TextureTarget.Texture2D, 0, InternalFormat.R16, (uint)size, (uint)size, 0, PixelFormat.Red, PixelType.UnsignedShort, coarse.AsSpan());
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        Sampling();
        for (int i = 0; i < 2; i++)
        {
            targets[i] = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, targets[i]);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.RG32f, (uint)size, (uint)size, 0, PixelFormat.RG, PixelType.Float, null);
            Sampling();
            fbos[i] = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbos[i]);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, targets[i], 0);
            if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
                throw new InvalidOperationException("Terrain shadow framebuffer incomplete.");
        }
        gl.BindTexture(TextureTarget.Texture2D, 0);
        // Native (docs/renderer-native.md 7.1, wave 3 agent B, step P): VkGl's SPIR-V and layout, made once, never inside a draw.
        sweep = LegacyProgram.Create(gpu, ShadowShaders.FullscreenVertex, MeitouShadowShaders.SweepFragment, "terrain shadow sweep");
        (sourceSlot, uSize, uFirst, uStep, uDrop, uDistance) = (sweep.Sampler("uSource"), sweep.Uniform("uSize"), sweep.Uniform("uFirst"),
            sweep.Uniform("uStep"), sweep.Uniform("uDrop"), sweep.Uniform("uDistance"));
    }

    void Sampling()
    {
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
    }

    /// <summary>The finished map (0 before the first build or when the sun is too high for any terrain shadow).</summary>
    public uint Texture => result < 0 ? 0 : targets[result];
    public int Size => size;
    public float Spacing => spacing;
    /// <summary>GPU work of the last rebuild is not timed separately; this counts rebuilds (for the log).</summary>
    public int Builds { get; private set; }
    public double LastBuildCpuMs { get; private set; }

    /// <summary>Whether the sun is low enough to leave the land's shadows anywhere (a sun straight up casts none).</summary>
    public static bool Applies(Vector3 toSun) => toSun.X * toSun.X + toSun.Z * toSun.Z > 1e-6f;

    /// <summary>Rebuilds the map when the sun has moved since the last build. Leaves framebuffer 0 bound; the caller restores its own.</summary>
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

        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.Blend);
        gl.Disable(EnableCap.CullFace);
        gl.Disable(EnableCap.ScissorTest);
        gl.DepthMask(false);
        gl.ColorMask(true, true, true, true);
        gl.Viewport(0, 0, (uint)size, (uint)size);
        gl.ActiveTexture(TextureUnit.Texture0);
        int passes = 1;
        while ((1 << (passes - 1)) < size) passes++;
        int write = 0;
        var interop = Gpu.Interop!;
        sweep.Set(uSize, (float)size);
        for (int pass = 0; pass < passes; pass++)
        {
            float step = pass == 0 ? 1 : 1 << (pass - 1);   // the first pass reaches one sample, the k-th 2^(k-1) more
            // GL side: this pass's target and source (a new pass for VkGl each time, which orders the ping-pong); then one native segment.
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbos[write]);
            gl.BindTexture(TextureTarget.Texture2D, pass == 0 ? heights : targets[1 - write]);
            sweep.Set(uFirst, pass == 0 ? 1 : 0);
            sweep.Set(uStep, direction.X * step, direction.Y * step);
            sweep.Set(uDrop, step * spacing * tangent);
            sweep.Set(uDistance, step * spacing);
            sweep.Bind(sourceSlot, interop.SampledUnit(0, sweep.SamplerInfo(sourceSlot)));
            var cmd = interop.BeginNativeInPass("terrain shadow sweep");
            var t = interop.CurrentTargets();
            var state = interop.CurrentState();
            state.Record(cmd, t);
            cmd.BindPipeline(Gpu.Pipelines.Get(state.Pipeline(sweep.Program, sweep.VertexLayout([]), Silk.NET.Vulkan.PrimitiveTopology.TriangleList, t.Formats, "terrain shadow sweep")));
            sweep.Flush(cmd);
            cmd.Draw(3);
            interop.EndNative(cmd);
            write = 1 - write;
        }
        result = 1 - write;
        gl.BindTexture(TextureTarget.Texture2D, 0);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.DepthMask(true);
        gl.Enable(EnableCap.DepthTest);
        builtFor = toSun;
        StepTiming.Add(StepTiming.Sweep, timing, passes);
        Builds++;
        LastBuildCpuMs = watch.Elapsed.TotalMilliseconds;
        return true;
    }

    public void Dispose()
    {
        gl.DeleteTexture(heights);
        for (int i = 0; i < 2; i++) { gl.DeleteFramebuffer(fbos[i]); gl.DeleteTexture(targets[i]); }
        sweep.Dispose();
    }
}
