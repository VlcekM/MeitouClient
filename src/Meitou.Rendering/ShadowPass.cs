using System.Diagnostics;
using System.Numerics;
using Meitou.Data.World;
using Silk.NET.OpenGL;

using Meitou.Rendering.Gpu;

namespace Meitou.Rendering;

/// <summary>
/// The sun's shadow map as the game's CSM mode draws it (docs/formats/shadows.md): four cascades fitted by <see cref="ShadowCascades"/>
/// (Meitou.Data, backend-independent) in the tiles of one square depth atlas, the casters drawn by the renderers' depth-only entry points
/// through <see cref="CasterDraw"/>. This class is the thin GL part: the atlas, the caster state, the uniform blocks the receivers read
/// (<see cref="ShadowShaders.Functions"/>), timing and the debug views.
/// </summary>
public sealed unsafe class ShadowPass : IDisposable
{
    /// <summary>Draws the casters of one cascade: the matrix maps absolute world positions to the tile's clip space (depth 0..1), the planes cull (no near plane), the eye picks levels of detail.</summary>
    public delegate void CasterDraw(ShadowCascade cascade, Matrix4x4 worldToClip, Vector4[] planes, Vector3 lodEye);

    readonly IGl gl;
    readonly uint receiverUbo, casterUbo;
    readonly uint[] queries = new uint[4];
    readonly bool[] pending = new bool[2];
    readonly List<double> gpuSamples = [], cpuSamples = [];
    uint atlas, fbo;
    int atlasSize, slot;
    uint debugProgram, atlasProgram, emptyVao;
    uint sceneDepth, sceneFbo;
    int sceneWidth, sceneHeight;
    readonly Dictionary<(uint, string), int> uniforms = [];

    public ShadowPass(IGl gl)
    {
        this.gl = gl;
        receiverUbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.UniformBuffer, receiverUbo);
        gl.BufferData(BufferTargetARB.UniformBuffer, ReceiverBytes, null, BufferUsageARB.DynamicDraw);
        casterUbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.UniformBuffer, casterUbo);
        gl.BufferData(BufferTargetARB.UniformBuffer, 16, null, BufferUsageARB.DynamicDraw);
        gl.BindBuffer(BufferTargetARB.UniformBuffer, 0);
        for (int i = 0; i < queries.Length; i++) queries[i] = gl.GenQuery();
        Disable();   // a valid block (shadows off) before the first frame
    }

    /// <summary>The atlas side, the range and the number of cascades (the game's <c>shadow quality</c> and <c>Shadow Range</c>).</summary>
    public ShadowSettings Settings { get; set; } = new();
    public bool Enabled { get; set; } = true;
    /// <summary>Below this sun height there is no shadow map: the lighting direction lies on the horizon there and the sun is black anyway.</summary>
    public float MinSunHeight { get; set; } = 0.02f;
    /// <summary>The cascades of the last frame (null when nothing was drawn).</summary>
    public ShadowCascade[]? Cascades { get; private set; }
    public uint Atlas => atlas;
    public double CpuMs { get; private set; }
    public double GpuMs { get; private set; }
    /// <summary>What the caster callback drew in the last frame (for the log).</summary>
    public string CasterStats { get; set; } = "";
    /// <summary>CPU time of the callback's parts (terrain, objects, foliage) summed over the cascades of the last frame.</summary>
    public readonly double[] PhaseMs = new double[3];

    /// <summary>Size of the receiver block (std140) the shaders declare in <see cref="ShadowShaders.Functions"/>.</summary>
    public const int ReceiverBytes = 560;

    /// <summary>Draws the cascades for this camera and sun, then publishes them to the receivers. Restores the framebuffer and viewport given.</summary>
    public void Render(ShadowView view, Vector3 toSun, uint restoreFramebuffer, int restoreWidth, int restoreHeight, CasterDraw draw)
    {
        Poll();
        Cascades = null;
        if (!Enabled || toSun.Y < MinSunHeight || toSun.LengthSquared() < 1e-8f) { Disable(); return; }
        var watch = Stopwatch.StartNew();
        Array.Clear(PhaseMs);
        Resize(Settings.MapSize);
        var cascades = ShadowCascades.Fit(view, Vector3.Normalize(toSun), Settings);
        int timed = -1;
        if (!pending[slot]) { timed = slot; gl.QueryCounter(queries[timed * 2], QueryCounterTarget.Timestamp); }

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        gl.Viewport(0, 0, (uint)atlasSize, (uint)atlasSize);
        gl.Disable(EnableCap.ScissorTest);
        gl.DepthMask(true);
        gl.ColorMask(false, false, false, false);
        gl.ClearDepth(1.0);
        gl.Clear(ClearBufferMask.DepthBufferBit);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Less);
        gl.Enable(EnableCap.DepthClamp);   // casters between the box and the sun are flattened onto its near side, not clipped
        gl.Disable(EnableCap.Blend);
        gl.Enable(EnableCap.ScissorTest);
        gl.BindBufferBase(BufferTargetARB.UniformBuffer, ShadowShaders.CasterBinding, casterUbo);
        foreach (var c in cascades)
        {
            if (c.Unused) continue;   // in front of the camera's near plane: nothing on screen reads it (a viewer saving)
            int x = (int)MathF.Round(c.Tile.X * atlasSize), y = (int)MathF.Round(c.Tile.Y * atlasSize), s = Settings.TileSize;
            gl.Viewport(x, y, (uint)s, (uint)s);
            gl.Scissor(x, y, (uint)s, (uint)s);
            var bias = new Vector4(c.FixedBias, KenshiShadows.SlopeBias, KenshiShadows.MaxSlopeBias, 0);
            gl.BindBuffer(BufferTargetARB.UniformBuffer, casterUbo);
            gl.BufferSubData(BufferTargetARB.UniformBuffer, 0, 16, &bias);
            draw(c, c.WorldToClip(), c.CullPlanes(), view.Eye);
        }
        gl.BindBuffer(BufferTargetARB.UniformBuffer, 0);
        gl.Disable(EnableCap.ScissorTest);
        gl.Disable(EnableCap.DepthClamp);
        gl.ColorMask(true, true, true, true);
        gl.DepthFunc(DepthFunction.Lequal);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, restoreFramebuffer);
        gl.Viewport(0, 0, (uint)restoreWidth, (uint)restoreHeight);
        if (timed >= 0)
        {
            gl.QueryCounter(queries[timed * 2 + 1], QueryCounterTarget.Timestamp);
            pending[timed] = true;
            slot = (slot + 1) % 2;
        }
        Cascades = cascades;
        Publish(view, cascades);
        CpuMs = watch.Elapsed.TotalMilliseconds;
        cpuSamples.Add(CpuMs);
    }

    /// <summary>Marks the shadows off for the receivers (the term is 1 everywhere).</summary>
    public void Disable()
    {
        var data = new float[ReceiverBytes / 4];
        Upload(data);
    }

    void Publish(ShadowView view, ShadowCascade[] cascades)
    {
        var data = new float[ReceiverBytes / 4];
        var origin = view.Eye;
        for (int i = 0; i < cascades.Length && i < 4; i++)
        {
            var c = cascades[i];
            Put(data, i * 16, c.OriginToTile(origin));
            Put(data, 64 + i * 4, c.Tile);
            Put(data, 80 + i * 4, new Vector4(c.FarDepth, c.FilterRadius, c.Size.X, c.Size.Y));
            Put(data, 96 + i * 4, new Vector4(c.Size.Z, 1f / Settings.TileSize, 0, 0));
        }
        Put(data, 112, cascades[0].Rotation);
        Put(data, 128, new Vector4(origin, 1));
        Put(data, 132, new Vector4(Vector3.Normalize(view.Forward), Math.Min(cascades.Length, 4)));
        Put(data, 136, new Vector4(atlasSize, 0, 0, 0));
        Upload(data);
    }

    void Upload(float[] data)
    {
        gl.BindBuffer(BufferTargetARB.UniformBuffer, receiverUbo);
        fixed (float* p = data) gl.BufferSubData(BufferTargetARB.UniformBuffer, 0, (nuint)(data.Length * 4), p);
        gl.BindBuffer(BufferTargetARB.UniformBuffer, 0);
        gl.BindBufferBase(BufferTargetARB.UniformBuffer, ShadowShaders.ReceiverBinding, receiverUbo);
        if (ShadowShaders.MapUnit >= 0 && atlas != 0)
        {
            gl.ActiveTexture(TextureUnit.Texture0 + ShadowShaders.MapUnit);
            gl.BindTexture(TextureTarget.Texture2D, atlas);
            gl.ActiveTexture(TextureUnit.Texture0);
        }
    }

    static void Put(float[] d, int at, Matrix4x4 m)
    {
        // System.Numerics row-major memory is what GL reads as the column-major column-vector form (as WorldGl.Matrix).
        d[at] = m.M11; d[at + 1] = m.M12; d[at + 2] = m.M13; d[at + 3] = m.M14;
        d[at + 4] = m.M21; d[at + 5] = m.M22; d[at + 6] = m.M23; d[at + 7] = m.M24;
        d[at + 8] = m.M31; d[at + 9] = m.M32; d[at + 10] = m.M33; d[at + 11] = m.M34;
        d[at + 12] = m.M41; d[at + 13] = m.M42; d[at + 14] = m.M43; d[at + 15] = m.M44;
    }

    static void Put(float[] d, int at, Vector4 v) { d[at] = v.X; d[at + 1] = v.Y; d[at + 2] = v.Z; d[at + 3] = v.W; }

    void Resize(int size)
    {
        if (size == atlasSize && fbo != 0) return;
        FreeAtlas();
        atlasSize = size;
        atlas = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, atlas);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent32f, (uint)size, (uint)size, 0, PixelFormat.DepthComponent, PixelType.Float, null);
        // Hardware comparison with bilinear weights: each of the receiver's taps is a 2 × 2 PCF (the game point-samples; a viewer choice).
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc, (int)DepthFunction.Lequal);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, atlas, 0);
        gl.DrawBuffer(DrawBufferMode.None);
        gl.ReadBuffer(ReadBufferMode.None);
        if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
            throw new InvalidOperationException("Shadow atlas framebuffer incomplete.");
    }

    void FreeAtlas()
    {
        if (fbo != 0) { gl.DeleteFramebuffer(fbo); gl.DeleteTexture(atlas); fbo = 0; atlas = 0; }
    }

    // ------------------------------------------------------------------ cost

    /// <summary>Collects finished GPU timings (<paramref name="wait"/>: block for them).</summary>
    public void Poll(bool wait = false)
    {
        for (int s = 0; s < 2; s++)
        {
            if (!pending[s]) continue;
            int available = 1;
            if (!wait) gl.GetQueryObject(queries[s * 2 + 1], QueryObjectParameterName.ResultAvailable, out available);
            if (available == 0) continue;
            gl.GetQueryObject(queries[s * 2], QueryObjectParameterName.Result, out ulong start);
            gl.GetQueryObject(queries[s * 2 + 1], QueryObjectParameterName.Result, out ulong end);
            GpuMs = (end - start) / 1e6;
            gpuSamples.Add(GpuMs);
            pending[s] = false;
        }
    }

    /// <summary>Mean and 95th percentile of the pass's GPU and CPU time over the frames drawn (the first left out: shaders warm up).</summary>
    public string DescribeStats()
    {
        Poll(wait: true);
        static string One(List<double> values)
        {
            var s = values.Skip(1).OrderBy(x => x).ToList();
            if (s.Count == 0) return "none";
            return $"mean {s.Average():0.00}, p95 {s[Math.Min((int)(s.Count * 0.95), s.Count - 1)]:0.00}, max {s[^1]:0.00}";
        }
        return $"{cpuSamples.Count} frames: gpu {One(gpuSamples)} ms, cpu {One(cpuSamples)} ms";
    }

    // ------------------------------------------------------------------ debug views

    /// <summary>Copies the scene's depth (the near slice, what the depth buffer holds at the end of the frame) for <see cref="DrawDebug"/>. Call before the post-processing resolves.</summary>
    public void CaptureDepth(uint sceneFramebuffer, int width, int height)
    {
        if (sceneFbo == 0 || width != sceneWidth || height != sceneHeight)
        {
            if (sceneFbo != 0) { gl.DeleteFramebuffer(sceneFbo); gl.DeleteTexture(sceneDepth); }
            (sceneWidth, sceneHeight) = (width, height);
            sceneDepth = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, sceneDepth);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24, (uint)width, (uint)height, 0, PixelFormat.DepthComponent, PixelType.UnsignedInt, null);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            gl.BindTexture(TextureTarget.Texture2D, 0);
            sceneFbo = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, sceneFbo);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, sceneDepth, 0);
            gl.DrawBuffer(DrawBufferMode.None);
            gl.ReadBuffer(ReadBufferMode.None);
        }
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, sceneFramebuffer);
        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, sceneFbo);
        gl.BlitFramebuffer(0, 0, width, height, 0, 0, width, height, ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, sceneFramebuffer);
    }

    /// <summary>
    /// Draws a debug view into <paramref name="target"/> after the frame: 1 the cascade maps in a corner, 2 the shadow term of the
    /// scene's surfaces tinted by cascade, 3 the term multiplied over the picture. <paramref name="nearProjection"/>: the near slice's
    /// projection (the captured depth's).
    /// </summary>
    public void DrawDebug(int mode, uint target, int width, int height, Matrix4x4 view, Matrix4x4 nearProjection, Vector3 eye)
    {
        if (mode <= 0) return;
        if (debugProgram == 0)
        {
            debugProgram = WorldGl.Program(gl, ShadowShaders.FullscreenVertex, ShadowShaders.DebugFragment);
            atlasProgram = WorldGl.Program(gl, ShadowShaders.FullscreenVertex, ShadowShaders.AtlasFragment);
            emptyVao = gl.GenVertexArray();
        }
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, target);
        gl.Disable(EnableCap.DepthTest);
        gl.DepthMask(false);
        gl.Disable(EnableCap.CullFace);
        gl.BindVertexArray(emptyVao);
        if (mode >= 2 && sceneDepth != 0)
        {
            gl.Viewport(0, 0, (uint)width, (uint)height);
            gl.UseProgram(debugProgram);
            if (mode == 3)
            {
                gl.Enable(EnableCap.Blend);
                gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.Zero);
            }
            var rotation = view with { M41 = 0, M42 = 0, M43 = 0 };
            Matrix4x4.Invert(rotation * nearProjection, out var inverse);
            WorldGl.Matrix(gl, U(debugProgram, "uInverse"), inverse);
            gl.Uniform3(U(debugProgram, "uEye"), eye.X, eye.Y, eye.Z);
            gl.Uniform1(U(debugProgram, "uMode"), mode);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, sceneDepth);
            gl.Uniform1(U(debugProgram, "uSceneDepth"), 0);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            gl.Disable(EnableCap.Blend);
        }
        if ((mode == 1 || mode == 2) && atlas != 0)
        {
            // The atlas in the lower right corner, plain depth values (the comparison off for the read).
            int side = Math.Min(width, height) / (mode == 1 ? 2 : 3);
            gl.Viewport(width - side - 8, 8, (uint)side, (uint)side);
            gl.UseProgram(atlasProgram);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, atlas);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.None);
            gl.Uniform1(U(atlasProgram, "uAtlas"), 0);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
        }
        gl.BindTexture(TextureTarget.Texture2D, 0);
        gl.BindVertexArray(0);
        gl.Viewport(0, 0, (uint)width, (uint)height);
        gl.DepthMask(true);
        gl.Enable(EnableCap.DepthTest);
    }

    int U(uint program, string name)
    {
        if (!uniforms.TryGetValue((program, name), out int location)) uniforms[(program, name)] = location = gl.GetUniformLocation(program, name);
        return location;
    }

    public void Dispose()
    {
        FreeAtlas();
        if (sceneFbo != 0) { gl.DeleteFramebuffer(sceneFbo); gl.DeleteTexture(sceneDepth); }
        if (debugProgram != 0) { gl.DeleteProgram(debugProgram); gl.DeleteProgram(atlasProgram); gl.DeleteVertexArray(emptyVao); }
        gl.DeleteBuffer(receiverUbo);
        gl.DeleteBuffer(casterUbo);
        foreach (var q in queries) gl.DeleteQuery(q);
    }
}
