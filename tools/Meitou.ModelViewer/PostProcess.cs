using System.Text;
using Silk.NET.OpenGL;

namespace Meitou.ModelViewer;

/// <summary>
/// The world view's frame buffer and post-processing chain. The scene is drawn into a (multisampled) RGBA16F framebuffer
/// with depth, resolved, then: SSAO at half resolution from the depth, bloom (quarter-ish resolution mip chain), a
/// composite pass (exposure, occlusion, bloom, tone map, grade, vignette) and FXAA, ending in <see cref="Target"/>.
/// Usage per frame: <see cref="Begin"/>, draw the scene (calling <see cref="SetNearSlice"/> for the near depth slice), <see cref="End"/>.
/// Anything that draws into another framebuffer in between must rebind the one it found (<see cref="SceneFramebuffer"/>).
/// Facts about the game's own chain: docs/formats/post-processing.md.
/// </summary>
public sealed unsafe class PostProcess : IDisposable
{
    sealed class Target2D
    {
        public uint Texture, Framebuffer;
        public int Width, Height;
    }

    readonly GL gl;
    public PostOptions Options { get; }
    /// <summary>Framebuffer the final image goes to: 0 for the window, or an RGBA8 framebuffer of the same size.</summary>
    public uint Target;
    /// <summary>The framebuffer the scene is drawn into (valid after <see cref="Begin"/>).</summary>
    public uint SceneFramebuffer { get; private set; }

    int width, height, samples, requestedSamples;
    uint msFbo, msColour, msDepth;
    uint sceneFbo, sceneColour, sceneDepth;
    Target2D? aoA, aoB, ldr;
    Target2D[] bloom = [];
    readonly uint vao;
    readonly uint progSsao, progBlur, progPrefilter, progDown, progUp, progComposite, progFxaa;
    readonly Dictionary<(uint, string), int> uniforms = [];

    float nearPlane = 1, farPlane = 1000, fovY = 0.87f, aspect = 1;
    bool haveNearSlice;

    // GPU timestamps: one set per frame in flight, section k lasts from stamp k-1 to stamp k.
    const int Slots = 4, MaxStamps = 8;
    readonly uint[,] stamps = new uint[Slots, MaxStamps];
    readonly string[,] stampNames = new string[Slots, MaxStamps];
    readonly int[] stampCount = new int[Slots];
    readonly bool[] pending = new bool[Slots];
    int slot;
    readonly Dictionary<string, (double Sum, int Count)> costs = [];

    public PostProcess(GL gl, PostOptions options)
    {
        this.gl = gl;
        Options = options;
        vao = gl.GenVertexArray();
        progSsao = Program(PostProcessShaders.Ssao);
        progBlur = Program(PostProcessShaders.SsaoBlur);
        progPrefilter = Program(PostProcessShaders.BloomPrefilter);
        progDown = Program(PostProcessShaders.BloomDown);
        progUp = Program(PostProcessShaders.BloomUp);
        progComposite = Program(PostProcessShaders.Composite);
        progFxaa = Program(PostProcessShaders.Fxaa);
        for (int s = 0; s < Slots; s++)
            for (int i = 0; i < MaxStamps; i++) stamps[s, i] = gl.GenQuery();
    }

    uint Program(string fragment) => WorldGl.Program(gl, PostProcessShaders.Vertex, fragment);

    int U(uint p, string name)
    {
        if (!uniforms.TryGetValue((p, name), out int location)) uniforms[(p, name)] = location = gl.GetUniformLocation(p, name);
        return location;
    }

    /// <summary>The near depth slice's planes and the projection: what the depth buffer at the end of the frame holds (the far slice's depth is cleared).</summary>
    public void SetNearSlice(float near, float far, float fieldOfView, float aspectRatio)
    {
        nearPlane = near; farPlane = far; fovY = fieldOfView; aspect = aspectRatio;
        haveNearSlice = true;
    }

    // ---- targets ----

    void Free()
    {
        if (msFbo != 0) { gl.DeleteFramebuffer(msFbo); gl.DeleteRenderbuffer(msColour); gl.DeleteRenderbuffer(msDepth); msFbo = 0; }
        if (sceneFbo != 0) { gl.DeleteFramebuffer(sceneFbo); gl.DeleteTexture(sceneColour); gl.DeleteTexture(sceneDepth); sceneFbo = 0; }
        foreach (var t in new[] { aoA, aoB, ldr }.Concat(bloom)) if (t is not null) Release(t);
        aoA = aoB = ldr = null;
        bloom = [];
    }

    void Release(Target2D t) { gl.DeleteFramebuffer(t.Framebuffer); gl.DeleteTexture(t.Texture); }

    Target2D MakeTarget(int w, int h, InternalFormat format, PixelFormat pf, PixelType type)
    {
        var t = new Target2D { Width = w, Height = h };
        t.Texture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, t.Texture);
        gl.TexImage2D(TextureTarget.Texture2D, 0, format, (uint)w, (uint)h, 0, pf, type, null);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        t.Framebuffer = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, t.Framebuffer);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, t.Texture, 0);
        return t;
    }

    void Allocate(int w, int h, int msaa)
    {
        Free();
        width = w; height = h; samples = requestedSamples = msaa;
        gl.GetInteger((GLEnum)0x8D57, out int maxSamples); // GL_MAX_SAMPLES
        if (samples > maxSamples) samples = Math.Max(maxSamples, 1);

        // Resolved colour (RGBA16F) and depth (24 bit) textures; with one sample the scene is drawn straight into them.
        sceneColour = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, sceneColour);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba16f, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.HalfFloat, null);
        SamplerState(TextureMinFilter.Linear);
        sceneDepth = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, sceneDepth);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24, (uint)w, (uint)h, 0, PixelFormat.DepthComponent, PixelType.UnsignedInt, null);
        SamplerState(TextureMinFilter.Nearest);
        sceneFbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, sceneFbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, sceneColour, 0);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, sceneDepth, 0);
        Check("scene");

        if (samples > 1)
        {
            msColour = gl.GenRenderbuffer();
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, msColour);
            gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)samples, InternalFormat.Rgba16f, (uint)w, (uint)h);
            msDepth = gl.GenRenderbuffer();
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, msDepth);
            gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)samples, InternalFormat.DepthComponent24, (uint)w, (uint)h);
            msFbo = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, msFbo);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, msColour);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, msDepth);
            Check("multisampled scene");
        }

        int hw = Math.Max((w + 1) / 2, 1), hh = Math.Max((h + 1) / 2, 1);
        aoA = MakeTarget(hw, hh, InternalFormat.RG16f, PixelFormat.RG, PixelType.HalfFloat);
        aoB = MakeTarget(hw, hh, InternalFormat.RG16f, PixelFormat.RG, PixelType.HalfFloat);
        ldr = MakeTarget(w, h, InternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte);
        var levels = new List<Target2D>();
        for (int bw = hw, bh = hh; levels.Count < 6 && bw >= 4 && bh >= 4; bw = Math.Max(bw / 2, 1), bh = Math.Max(bh / 2, 1))
            levels.Add(MakeTarget(bw, bh, InternalFormat.R11fG11fB10f, PixelFormat.Rgb, PixelType.HalfFloat));
        bloom = [.. levels];
        gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    void SamplerState(TextureMinFilter filter)
    {
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)filter);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)(filter == TextureMinFilter.Nearest ? TextureMagFilter.Nearest : TextureMagFilter.Linear));
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
    }

    void Check(string what)
    {
        var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete) throw new InvalidOperationException($"Post-processing framebuffer '{what}' incomplete: {status}");
    }

    // ---- timing ----

    void Stamp(string name)
    {
        int n = stampCount[slot];
        if (n >= MaxStamps) return;
        gl.QueryCounter(stamps[slot, n], QueryCounterTarget.Timestamp);
        stampNames[slot, n] = name;
        stampCount[slot] = n + 1;
    }

    void Collect(int s, bool wait)
    {
        if (!pending[s]) return;
        int n = stampCount[s];
        if (!wait)
        {
            gl.GetQueryObject(stamps[s, n - 1], QueryObjectParameterName.ResultAvailable, out int ready);
            if (ready == 0) return;
        }
        gl.GetQueryObject(stamps[s, 0], QueryObjectParameterName.Result, out ulong previous);
        for (int i = 1; i < n; i++)
        {
            gl.GetQueryObject(stamps[s, i], QueryObjectParameterName.Result, out ulong now);
            string name = stampNames[s, i]!;
            costs.TryGetValue(name, out var c);
            costs[name] = (c.Sum + (now - previous) / 1e6, c.Count + 1);
            previous = now;
        }
        pending[s] = false;
    }

    /// <summary>Waits for every outstanding timing (after a <c>glFinish</c>); for the offscreen path.</summary>
    public void Flush() { for (int s = 0; s < Slots; s++) Collect(s, true); }

    /// <summary>Average GPU milliseconds per section since the last call ("scene" is the frame before post-processing), then resets.</summary>
    public IReadOnlyList<(string Name, double Ms)> TakeCosts()
    {
        var list = costs.Select(kv => (kv.Key, kv.Value.Sum / kv.Value.Count)).ToList();
        costs.Clear();
        return list;
    }

    public string DescribeCosts()
    {
        var sb = new StringBuilder();
        foreach (var (name, ms) in TakeCosts()) sb.Append(sb.Length > 0 ? ", " : "").Append(name).Append(' ').Append(ms.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    // ---- frame ----

    /// <summary>Binds the scene framebuffer (recreating it when the size or the sample count changed) and clears nothing: the caller does.</summary>
    public void Begin(int w, int h)
    {
        if (w != width || h != height || requestedSamples != Options.Msaa) Allocate(w, h, Options.Msaa);
        slot = (slot + 1) % Slots;
        Collect(slot, false);
        pending[slot] = false;
        stampCount[slot] = 0;
        haveNearSlice = false;
        Stamp("start");
        SceneFramebuffer = samples > 1 ? msFbo : sceneFbo;
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, SceneFramebuffer);
        gl.Viewport(0, 0, (uint)w, (uint)h);
    }

    /// <summary>Resolves the scene and runs the chain into <see cref="Target"/>.</summary>
    public void End()
    {
        var o = Options;
        Stamp("scene");
        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.CullFace);
        gl.Disable(EnableCap.Blend);
        gl.Disable(EnableCap.Multisample);
        gl.DepthMask(false);
        bool needDepth = o.Ssao && haveNearSlice;
        if (samples > 1)
        {
            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, msFbo);
            gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, sceneFbo);
            var mask = ClearBufferMask.ColorBufferBit | (needDepth ? ClearBufferMask.DepthBufferBit : 0);
            gl.BlitFramebuffer(0, 0, width, height, 0, 0, width, height, mask, BlitFramebufferFilter.Nearest);
            Stamp("resolve");
        }
        gl.BindVertexArray(vao);

        bool ao = needDepth, glow = o.Bloom && bloom.Length > 0;
        if (ao) RunSsao();
        if (ao) Stamp("ssao");
        if (glow) RunBloom();
        if (glow) Stamp("bloom");

        // Composite: into the LDR texture when FXAA follows, else straight to the target.
        bool fxaa = o.Fxaa;
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, fxaa ? ldr!.Framebuffer : Target);
        gl.Viewport(0, 0, (uint)width, (uint)height);
        gl.UseProgram(progComposite);
        Bind(0, sceneColour); gl.Uniform1(U(progComposite, "uScene"), 0);
        Bind(1, aoB?.Texture ?? 0); gl.Uniform1(U(progComposite, "uAo"), 1);
        Bind(2, bloom.Length > 0 ? bloom[0].Texture : 0); gl.Uniform1(U(progComposite, "uBloom"), 2);
        gl.Uniform1(U(progComposite, "uExposure"), o.Exposure);
        gl.Uniform1(U(progComposite, "uBloomIntensity"), o.BloomIntensity);
        gl.Uniform1(U(progComposite, "uSaturation"), o.Saturation);
        gl.Uniform1(U(progComposite, "uContrast"), o.Contrast);
        gl.Uniform1(U(progComposite, "uVignette"), o.Vignette ? o.VignetteStrength : 0f);
        gl.Uniform1(U(progComposite, "uUseAo"), ao ? 1 : 0);
        gl.Uniform1(U(progComposite, "uUseBloom"), glow ? 1 : 0);
        gl.Uniform1(U(progComposite, "uTone"), (int)o.ToneMap);
        gl.Uniform1(U(progComposite, "uGrade"), o.Grade ? 1 : 0);
        gl.Uniform1(U(progComposite, "uDither"), o.Dither && !fxaa ? 1 : 0);
        gl.Uniform1(U(progComposite, "uDebug"), o.Debug);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        Stamp("composite");

        if (fxaa)
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, Target);
            gl.Viewport(0, 0, (uint)width, (uint)height);
            gl.UseProgram(progFxaa);
            Bind(0, ldr!.Texture); gl.Uniform1(U(progFxaa, "uSrc"), 0);
            gl.Uniform2(U(progFxaa, "uTexel"), 1f / width, 1f / height);
            gl.Uniform1(U(progFxaa, "uSubpix"), o.FxaaSubpix);
            gl.Uniform1(U(progFxaa, "uDither"), o.Dither ? 1 : 0);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            Stamp("fxaa");
        }
        pending[slot] = true;

        gl.BindVertexArray(0);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.DepthMask(true);
        gl.Enable(EnableCap.DepthTest);
        gl.Enable(EnableCap.Multisample);
    }

    void Bind(int unit, uint texture)
    {
        gl.ActiveTexture(TextureUnit.Texture0 + unit);
        gl.BindTexture(TextureTarget.Texture2D, texture);
    }

    void Pass(Target2D target)
    {
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, target.Framebuffer);
        gl.Viewport(0, 0, (uint)target.Width, (uint)target.Height);
    }

    void RunSsao()
    {
        var a = aoA!; var b = aoB!;
        float tanY = MathF.Tan(fovY * 0.5f);
        Pass(a);
        gl.UseProgram(progSsao);
        Bind(0, sceneDepth); gl.Uniform1(U(progSsao, "uDepth"), 0);
        gl.Uniform2(U(progSsao, "uTan"), tanY * aspect, tanY);
        gl.Uniform2(U(progSsao, "uNearFar"), nearPlane, farPlane);
        gl.Uniform2(U(progSsao, "uSize"), (float)width, (float)height);
        gl.Uniform1(U(progSsao, "uRadius"), Options.SsaoRadius);
        gl.Uniform1(U(progSsao, "uStrength"), Options.SsaoStrength);
        // Fade out with distance: the occlusion is a detail effect, and far geometry is hazy and has little depth precision.
        gl.Uniform1(U(progSsao, "uFadeStart"), 3000f);
        gl.Uniform1(U(progSsao, "uFadeEnd"), 10000f);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

        gl.UseProgram(progBlur);
        Pass(b);
        Bind(0, a.Texture); gl.Uniform1(U(progBlur, "uAo"), 0);
        gl.Uniform2(U(progBlur, "uStep"), 1f / a.Width, 0f);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        Pass(a);
        Bind(0, b.Texture);
        gl.Uniform2(U(progBlur, "uStep"), 0f, 1f / a.Height);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        // The composite reads aoB: swap so the finished result is there.
        (aoA, aoB) = (aoB, aoA);
    }

    void RunBloom()
    {
        var first = bloom[0];
        Pass(first);
        gl.UseProgram(progPrefilter);
        Bind(0, sceneColour); gl.Uniform1(U(progPrefilter, "uSrc"), 0);
        gl.Uniform2(U(progPrefilter, "uTexel"), 1f / width, 1f / height);
        gl.Uniform1(U(progPrefilter, "uThreshold"), Math.Max(Options.BloomThreshold, 0.01f));
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        gl.UseProgram(progDown);
        for (int i = 1; i < bloom.Length; i++)
        {
            Pass(bloom[i]);
            Bind(0, bloom[i - 1].Texture); gl.Uniform1(U(progDown, "uSrc"), 0);
            gl.Uniform2(U(progDown, "uTexel"), 1f / bloom[i - 1].Width, 1f / bloom[i - 1].Height);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }
        gl.UseProgram(progUp);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
        for (int i = bloom.Length - 2; i >= 0; i--)
        {
            Pass(bloom[i]);
            Bind(0, bloom[i + 1].Texture); gl.Uniform1(U(progUp, "uSrc"), 0);
            gl.Uniform2(U(progUp, "uTexel"), 1f / bloom[i + 1].Width, 1f / bloom[i + 1].Height);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }
        gl.Disable(EnableCap.Blend);
    }

    public void Dispose()
    {
        Free();
        gl.DeleteVertexArray(vao);
        foreach (var p in new[] { progSsao, progBlur, progPrefilter, progDown, progUp, progComposite, progFxaa }) gl.DeleteProgram(p);
        for (int s = 0; s < Slots; s++)
            for (int i = 0; i < MaxStamps; i++) gl.DeleteQuery(stamps[s, i]);
    }
}
