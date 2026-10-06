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
    readonly uint heights, program, vao;
    readonly uint[] targets = new uint[2], fbos = new uint[2];
    readonly Dictionary<string, int> uniforms = [];
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
        program = WorldGl.Program(gl, ShadowShaders.FullscreenVertex, MeitouShadowShaders.SweepFragment);
        vao = gl.GenVertexArray();
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
        gl.UseProgram(program);
        gl.BindVertexArray(vao);
        gl.Uniform1(U("uSource"), 0);
        gl.Uniform1(U("uSize"), (float)size);
        gl.Viewport(0, 0, (uint)size, (uint)size);
        gl.ActiveTexture(TextureUnit.Texture0);
        int passes = 1;
        while ((1 << (passes - 1)) < size) passes++;
        int write = 0;
        for (int pass = 0; pass < passes; pass++)
        {
            float step = pass == 0 ? 1 : 1 << (pass - 1);   // the first pass reaches one sample, the k-th 2^(k-1) more
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbos[write]);
            gl.BindTexture(TextureTarget.Texture2D, pass == 0 ? heights : targets[1 - write]);
            gl.Uniform1(U("uFirst"), pass == 0 ? 1 : 0);
            gl.Uniform2(U("uStep"), direction.X * step, direction.Y * step);
            gl.Uniform1(U("uDrop"), step * spacing * tangent);
            gl.Uniform1(U("uDistance"), step * spacing);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            write = 1 - write;
        }
        result = 1 - write;
        gl.BindTexture(TextureTarget.Texture2D, 0);
        gl.BindVertexArray(0);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.DepthMask(true);
        gl.Enable(EnableCap.DepthTest);
        builtFor = toSun;
        StepTiming.Add(StepTiming.Sweep, timing, passes);
        Builds++;
        LastBuildCpuMs = watch.Elapsed.TotalMilliseconds;
        return true;
    }

    int U(string name)
    {
        if (!uniforms.TryGetValue(name, out int location)) uniforms[name] = location = gl.GetUniformLocation(program, name);
        return location;
    }

    public void Dispose()
    {
        gl.DeleteTexture(heights);
        for (int i = 0; i < 2; i++) { gl.DeleteFramebuffer(fbos[i]); gl.DeleteTexture(targets[i]); }
        gl.DeleteProgram(program);
        gl.DeleteVertexArray(vao);
    }
}
