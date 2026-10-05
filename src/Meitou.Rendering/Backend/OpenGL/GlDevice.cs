using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace Meitou.Rendering.Backend.OpenGL;

/// <summary>
/// The OpenGL 3.3 core backend of <see cref="IGpuDevice"/>. Commands run as they are recorded, on the thread that owns the
/// context; resources are freed at once (the driver tracks GPU use itself). Shaders are GLSL 3.30 with
/// <c>GL_ARB_shading_language_420pack</c> for <c>layout(binding = n)</c> on uniform blocks and samplers, so the binding numbers
/// are the same as in the Vulkan shaders (set 0). <see cref="Gl"/> is exposed for the renderers that still draw with GL calls
/// of their own (all of them in Phase 1, see docs/engine.md).
/// </summary>
public sealed unsafe class GlDevice : IGpuDevice
{
    readonly GlCommandList commands;

    public GlDevice(GL gl)
    {
        Gl = gl;
        commands = new GlCommandList(this);
        gl.GetInteger(GetPName.MaxTextureSize, out int maxTexture);
        gl.GetInteger(GetPName.MaxArrayTextureLayers, out int maxLayers);
        gl.GetInteger((GetPName)0x8D57, out int maxSamples);   // GL_MAX_SAMPLES
        string renderer = gl.GetStringS(StringName.Renderer) ?? "OpenGL";
        string version = gl.GetStringS(StringName.Version) ?? "3.3";
        Capabilities = new GpuCapabilities(GpuBackendKind.OpenGL, renderer, version, maxTexture, maxLayers, maxSamples,
            Bindless: false, TimestampQueries: true, DedicatedTransferQueue: false, ValidationEnabled: false);
    }

    /// <summary>The context, for renderers that draw with their own GL calls.</summary>
    public GL Gl { get; }
    public GpuCapabilities Capabilities { get; }
    public int FramesInFlight => 1;
    public long FrameNumber { get; private set; }
    public IGpuCommandList Commands => commands;

    public IGpuBuffer CreateBuffer(in GpuBufferDesc desc) => new GlBuffer(Gl, desc);
    public IGpuTexture CreateTexture(in GpuTextureDesc desc) => new GlTexture(Gl, desc);
    public IGpuSampler CreateSampler(in GpuSamplerDesc desc) => new GlSampler(Gl, desc);
    public IGpuPipeline CreatePipeline(GpuPipelineDesc desc) => new GlPipeline(Gl, desc);
    public IGpuTimer CreateTimer() => new GlTimer(Gl);

    public void Release(IGpuResource resource) => resource.Dispose();

    public void BeginFrame(int width, int height) => commands.FrameSize = (width, height);
    public void EndFrame() => FrameNumber++;
    public void WaitIdle() => Gl.Finish();

    public byte[] ReadPixels(IGpuTexture? texture, int width, int height)
    {
        uint fbo = 0;
        if (texture is GlTexture t)
        {
            fbo = Gl.GenFramebuffer();
            Gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fbo);
            Gl.FramebufferTexture2D(FramebufferTarget.ReadFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, t.Id, 0);
        }
        else Gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        var pixels = new byte[width * height * 4];
        Gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        Gl.ReadPixels<byte>(0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
        if (fbo != 0) Gl.DeleteFramebuffer(fbo);
        // GL rows start at the bottom.
        var flipped = new byte[pixels.Length];
        for (int y = 0; y < height; y++) pixels.AsSpan((height - 1 - y) * width * 4, width * 4).CopyTo(flipped.AsSpan(y * width * 4));
        return flipped;
    }

    public void Dispose() => commands.Dispose();

    internal static (InternalFormat Internal, PixelFormat Format, PixelType Type) Format(GpuFormat f) => f switch
    {
        GpuFormat.R8 => (InternalFormat.R8, PixelFormat.Red, PixelType.UnsignedByte),
        GpuFormat.RG8 => (InternalFormat.RG8, PixelFormat.RG, PixelType.UnsignedByte),
        GpuFormat.Rgba8 => (InternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte),
        GpuFormat.Bgra8 => (InternalFormat.Rgba8, PixelFormat.Bgra, PixelType.UnsignedByte),
        GpuFormat.R16 => (InternalFormat.R16, PixelFormat.Red, PixelType.UnsignedShort),
        GpuFormat.R16F => (InternalFormat.R16f, PixelFormat.Red, PixelType.HalfFloat),
        GpuFormat.Rg16F => (InternalFormat.RG16f, PixelFormat.RG, PixelType.HalfFloat),
        GpuFormat.Rgba16F => (InternalFormat.Rgba16f, PixelFormat.Rgba, PixelType.HalfFloat),
        GpuFormat.R32F => (InternalFormat.R32f, PixelFormat.Red, PixelType.Float),
        GpuFormat.Rg32F => (InternalFormat.RG32f, PixelFormat.RG, PixelType.Float),
        GpuFormat.Rgba32F => (InternalFormat.Rgba32f, PixelFormat.Rgba, PixelType.Float),
        GpuFormat.Depth24 => (InternalFormat.DepthComponent24, PixelFormat.DepthComponent, PixelType.UnsignedInt),
        GpuFormat.Depth32F => (InternalFormat.DepthComponent32f, PixelFormat.DepthComponent, PixelType.Float),
        GpuFormat.Depth24Stencil8 => (InternalFormat.Depth24Stencil8, PixelFormat.DepthStencil, PixelType.UnsignedInt248),
        GpuFormat.Bc1 => (InternalFormat.CompressedRgbaS3TCDxt1Ext, PixelFormat.Rgba, PixelType.UnsignedByte),
        GpuFormat.Bc2 => (InternalFormat.CompressedRgbaS3TCDxt3Ext, PixelFormat.Rgba, PixelType.UnsignedByte),
        GpuFormat.Bc3 => (InternalFormat.CompressedRgbaS3TCDxt5Ext, PixelFormat.Rgba, PixelType.UnsignedByte),
        GpuFormat.Bc4 => (InternalFormat.CompressedRedRgtc1, PixelFormat.Red, PixelType.UnsignedByte),
        GpuFormat.Bc5 => (InternalFormat.CompressedRGRgtc2, PixelFormat.RG, PixelType.UnsignedByte),
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };

    internal static DepthFunction Compare(GpuCompare c) => c switch
    {
        GpuCompare.Never => DepthFunction.Never,
        GpuCompare.Less => DepthFunction.Less,
        GpuCompare.LessOrEqual => DepthFunction.Lequal,
        GpuCompare.Equal => DepthFunction.Equal,
        GpuCompare.GreaterOrEqual => DepthFunction.Gequal,
        GpuCompare.Greater => DepthFunction.Greater,
        _ => DepthFunction.Always,
    };
}

sealed class GlBuffer : IGpuBuffer
{
    readonly GL gl;
    public GlBuffer(GL gl, GpuBufferDesc desc)
    {
        this.gl = gl;
        Desc = desc;
        Id = gl.GenBuffer();
        Target = desc.Usage.HasFlag(GpuBufferUsage.Index) ? BufferTargetARB.ElementArrayBuffer
            : desc.Usage.HasFlag(GpuBufferUsage.Uniform) ? BufferTargetARB.UniformBuffer : BufferTargetARB.ArrayBuffer;
        // Index buffers are bound through a vertex array in GL; filling one outside a VAO would change the bound VAO's state.
        var fillTarget = Target == BufferTargetARB.ElementArrayBuffer ? BufferTargetARB.CopyWriteBuffer : Target;
        gl.BindBuffer(fillTarget, Id);
        unsafe { gl.BufferData(fillTarget, (nuint)desc.Size, null, desc.Dynamic ? BufferUsageARB.DynamicDraw : BufferUsageARB.StaticDraw); }
        gl.BindBuffer(fillTarget, 0);
    }
    public uint Id { get; private set; }
    public BufferTargetARB Target { get; }
    public GpuBufferDesc Desc { get; }
    public string? Name => Desc.Name;
    public void Dispose() { if (Id != 0) gl.DeleteBuffer(Id); Id = 0; }
}

sealed class GlTexture : IGpuTexture
{
    readonly GL gl;
    public GlTexture(GL gl, GpuTextureDesc desc)
    {
        this.gl = gl;
        Desc = desc;
        Id = gl.GenTexture();
        Target = desc.Kind switch
        {
            GpuTextureKind.Texture2DArray => TextureTarget.Texture2DArray,
            GpuTextureKind.TextureCube => TextureTarget.TextureCubeMap,
            _ => desc.Samples > 1 ? TextureTarget.Texture2DMultisample : TextureTarget.Texture2D,
        };
        gl.BindTexture(Target, Id);
        var (internalFormat, format, type) = GlDevice.Format(desc.Format);
        unsafe
        {
            if (Target == TextureTarget.Texture2DMultisample)
                gl.TexImage2DMultisample(Target, (uint)desc.Samples, internalFormat, (uint)desc.Width, (uint)desc.Height, true);
            else
            {
                for (int mip = 0; mip < desc.MipLevels; mip++)
                {
                    uint w = (uint)Math.Max(desc.Width >> mip, 1), h = (uint)Math.Max(desc.Height >> mip, 1);
                    if (GpuFormats.IsCompressed(desc.Format))
                    {
                        uint bytes = (uint)GpuFormats.LevelBytes(desc.Format, (int)w, (int)h);
                        if (Target == TextureTarget.Texture2DArray)
                            gl.CompressedTexImage3D(Target, mip, internalFormat, w, h, (uint)desc.Layers, 0, bytes * (uint)desc.Layers, null);
                        else if (Target == TextureTarget.TextureCubeMap)
                            for (int face = 0; face < 6; face++) gl.CompressedTexImage2D(TextureTarget.TextureCubeMapPositiveX + face, mip, internalFormat, w, h, 0, bytes, null);
                        else gl.CompressedTexImage2D(Target, mip, internalFormat, w, h, 0, bytes, null);
                    }
                    else if (Target == TextureTarget.Texture2DArray)
                        gl.TexImage3D(Target, mip, internalFormat, w, h, (uint)desc.Layers, 0, format, type, null);
                    else if (Target == TextureTarget.TextureCubeMap)
                        for (int face = 0; face < 6; face++) gl.TexImage2D(TextureTarget.TextureCubeMapPositiveX + face, mip, internalFormat, w, h, 0, format, type, null);
                    else gl.TexImage2D(Target, mip, internalFormat, w, h, 0, format, type, null);
                }
                gl.TexParameter(Target, TextureParameterName.TextureBaseLevel, 0);
                gl.TexParameter(Target, TextureParameterName.TextureMaxLevel, desc.MipLevels - 1);
            }
        }
        gl.BindTexture(Target, 0);
    }
    public uint Id { get; private set; }
    public TextureTarget Target { get; }
    public GpuTextureDesc Desc { get; }
    public string? Name => Desc.Name;
    public void Dispose() { if (Id != 0) gl.DeleteTexture(Id); Id = 0; }
}

sealed class GlSampler : IGpuSampler
{
    readonly GL gl;
    public GlSampler(GL gl, GpuSamplerDesc desc)
    {
        this.gl = gl;
        Desc = desc;
        Id = gl.GenSampler();
        int min = (desc.Min, desc.Mip, desc.MaxLod > 0) switch
        {
            (GpuFilter.Nearest, _, false) => (int)TextureMinFilter.Nearest,
            (GpuFilter.Linear, _, false) => (int)TextureMinFilter.Linear,
            (GpuFilter.Nearest, GpuFilter.Nearest, true) => (int)TextureMinFilter.NearestMipmapNearest,
            (GpuFilter.Nearest, GpuFilter.Linear, true) => (int)TextureMinFilter.NearestMipmapLinear,
            (GpuFilter.Linear, GpuFilter.Nearest, true) => (int)TextureMinFilter.LinearMipmapNearest,
            _ => (int)TextureMinFilter.LinearMipmapLinear,
        };
        gl.SamplerParameter(Id, SamplerParameterI.MinFilter, min);
        gl.SamplerParameter(Id, SamplerParameterI.MagFilter, (int)(desc.Mag == GpuFilter.Nearest ? TextureMagFilter.Nearest : TextureMagFilter.Linear));
        gl.SamplerParameter(Id, SamplerParameterI.WrapS, Wrap(desc.U));
        gl.SamplerParameter(Id, SamplerParameterI.WrapT, Wrap(desc.V));
        gl.SamplerParameter(Id, SamplerParameterI.WrapR, Wrap(desc.W));
        gl.SamplerParameter(Id, SamplerParameterF.MinLod, desc.MinLod);
        gl.SamplerParameter(Id, SamplerParameterF.MaxLod, desc.MaxLod);
        if (desc.MaxAnisotropy > 1) gl.SamplerParameter(Id, (SamplerParameterF)0x84FE, desc.MaxAnisotropy); // GL_TEXTURE_MAX_ANISOTROPY
        if (desc.Compare is { } compare)
        {
            gl.SamplerParameter(Id, SamplerParameterI.CompareMode, (int)TextureCompareMode.CompareRefToTexture);
            gl.SamplerParameter(Id, SamplerParameterI.CompareFunc, (int)GlDevice.Compare(compare));
        }
    }
    static int Wrap(GpuAddress a) => (int)(a switch
    {
        GpuAddress.ClampToEdge => TextureWrapMode.ClampToEdge,
        GpuAddress.ClampToBorder => TextureWrapMode.ClampToBorder,
        GpuAddress.MirroredRepeat => TextureWrapMode.MirroredRepeat,
        _ => TextureWrapMode.Repeat,
    });
    public uint Id { get; private set; }
    public GpuSamplerDesc Desc { get; }
    public string? Name => null;
    public void Dispose() { if (Id != 0) gl.DeleteSampler(Id); Id = 0; }
}

sealed class GlPipeline : IGpuPipeline
{
    readonly GL gl;
    public GlPipeline(GL gl, GpuPipelineDesc desc)
    {
        this.gl = gl;
        Desc = desc;
        if (desc.Vertex.Glsl is null || desc.Fragment.Glsl is null)
            throw new ArgumentException("The OpenGL backend needs GLSL sources.", nameof(desc));
        Program = WorldGl.Program(gl, desc.Vertex.Glsl, desc.Fragment.Glsl);
        Vao = gl.GenVertexArray();
    }
    public uint Program { get; private set; }
    /// <summary>The vertex array whose attribute layout follows the pipeline (buffers are attached when bound).</summary>
    public uint Vao { get; private set; }
    public GpuPipelineDesc Desc { get; }
    public string? Name => Desc.Name;
    public void Dispose()
    {
        if (Program != 0) gl.DeleteProgram(Program);
        if (Vao != 0) gl.DeleteVertexArray(Vao);
        Program = Vao = 0;
    }
}

sealed class GlTimer : IGpuTimer
{
    readonly GL gl;
    readonly uint[] queries = new uint[4];
    readonly bool[] pending = new bool[4];
    int next, active = -1;
    double? last;
    public GlTimer(GL gl)
    {
        this.gl = gl;
        for (int i = 0; i < queries.Length; i++) queries[i] = gl.GenQuery();
    }
    public string? Name => null;
    internal void Begin()
    {
        if (pending[next]) { active = -1; return; }   // all slots still waiting for the GPU: skip this measurement
        active = next;
        gl.BeginQuery(QueryTarget.TimeElapsed, queries[active]);
    }
    internal void End()
    {
        if (active < 0) return;
        gl.EndQuery(QueryTarget.TimeElapsed);
        pending[active] = true;
        next = (next + 1) % queries.Length;
        active = -1;
    }
    public double? Read(bool wait = false)
    {
        for (int k = 0; k < queries.Length; k++)
        {
            int i = (next + k) % queries.Length;   // oldest first
            if (!pending[i]) continue;
            if (!wait)
            {
                gl.GetQueryObject(queries[i], QueryObjectParameterName.ResultAvailable, out int available);
                if (available == 0) continue;
            }
            gl.GetQueryObject(queries[i], QueryObjectParameterName.Result, out ulong ns);
            pending[i] = false;
            last = ns / 1e6;
        }
        return last;
    }
    public void Dispose() { foreach (var q in queries) gl.DeleteQuery(q); }
}

/// <summary>Immediate-mode command list: each call is the GL calls it stands for.</summary>
sealed unsafe class GlCommandList(GlDevice device) : IGpuCommandList, IDisposable
{
    readonly GL gl = device.Gl;
    uint framebuffer, uniformRing;
    int ringOffset, ringSize, uniformAlign;
    GlPipeline? pipeline;
    GpuIndexType indexType;
    long indexOffset;
    public (int Width, int Height) FrameSize;

    public void BeginRenderPass(GpuRenderPassDesc pass)
    {
        if (pass.Colour.Length == 0 && pass.Depth is null)
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.Viewport(0, 0, (uint)FrameSize.Width, (uint)FrameSize.Height);
            return;
        }
        if (framebuffer == 0) framebuffer = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        var buffers = stackalloc GLEnum[Math.Max(pass.Colour.Length, 1)];
        int width = 0, height = 0;
        for (int i = 0; i < pass.Colour.Length; i++)
        {
            var a = pass.Colour[i];
            Attach(FramebufferAttachment.ColorAttachment0 + i, (GlTexture)a.Texture, a.Layer, a.Mip);
            buffers[i] = GLEnum.ColorAttachment0 + i;
            (width, height) = (Math.Max(a.Texture.Desc.Width >> a.Mip, 1), Math.Max(a.Texture.Desc.Height >> a.Mip, 1));
        }
        for (int i = pass.Colour.Length; i < 4; i++)
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0 + i, TextureTarget.Texture2D, 0, 0);
        if (pass.Colour.Length == 0) gl.DrawBuffer(DrawBufferMode.None);
        else gl.DrawBuffers((uint)pass.Colour.Length, buffers);
        if (pass.Depth is { } d)
        {
            Attach(GpuFormatsHasStencil(d.Texture.Desc.Format) ? FramebufferAttachment.DepthStencilAttachment : FramebufferAttachment.DepthAttachment, (GlTexture)d.Texture, d.Layer, d.Mip);
            (width, height) = (Math.Max(d.Texture.Desc.Width >> d.Mip, 1), Math.Max(d.Texture.Desc.Height >> d.Mip, 1));
        }
        else gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, 0, 0);
        gl.Viewport(0, 0, (uint)width, (uint)height);
        for (int i = 0; i < pass.Colour.Length; i++)
            if (pass.Colour[i].Load == GpuLoad.Clear)
            {
                var c = pass.Colour[i].Clear;
                gl.ClearBuffer(BufferKind.Color, i, &c.X);
            }
        if (pass.Depth is { Load: GpuLoad.Clear } depth)
        {
            gl.DepthMask(true);
            float clear = depth.ClearDepth;
            gl.ClearBuffer(BufferKind.Depth, 0, &clear);
        }
        currentPass = pass;
    }

    GpuRenderPassDesc? currentPass;

    static bool GpuFormatsHasStencil(GpuFormat f) => f == GpuFormat.Depth24Stencil8;

    void Attach(FramebufferAttachment attachment, GlTexture t, int layer, int mip)
    {
        if (t.Target is TextureTarget.Texture2DArray or TextureTarget.TextureCubeMap)
            gl.FramebufferTextureLayer(FramebufferTarget.Framebuffer, attachment, t.Id, mip, layer);
        else gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, attachment, t.Target, t.Id, mip);
    }

    public void EndRenderPass()
    {
        if (currentPass is { } pass)
            for (int i = 0; i < pass.Colour.Length; i++)
                if (pass.Colour[i].Resolve is GlTexture target)
                {
                    var source = pass.Colour[i].Texture;
                    uint fbo = gl.GenFramebuffer();
                    gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, framebuffer);
                    gl.ReadBuffer(ReadBufferMode.ColorAttachment0 + i);
                    gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, fbo);
                    gl.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, target.Id, 0);
                    gl.BlitFramebuffer(0, 0, source.Desc.Width, source.Desc.Height, 0, 0, target.Desc.Width, target.Desc.Height, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
                    gl.DeleteFramebuffer(fbo);
                }
        currentPass = null;
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void SetViewport(int x, int y, int width, int height) => gl.Viewport(x, y, (uint)width, (uint)height);

    public void SetScissor(int x, int y, int width, int height)
    {
        gl.Enable(EnableCap.ScissorTest);
        gl.Scissor(x, y, (uint)width, (uint)height);
    }

    public void SetPipeline(IGpuPipeline p)
    {
        pipeline = (GlPipeline)p;
        var s = pipeline.Desc.State;
        gl.UseProgram(pipeline.Program);
        gl.BindVertexArray(pipeline.Vao);
        if (s.Cull == GpuCull.None) gl.Disable(EnableCap.CullFace);
        else { gl.Enable(EnableCap.CullFace); gl.CullFace(s.Cull == GpuCull.Back ? TriangleFace.Back : TriangleFace.Front); }
        gl.PolygonMode(TriangleFace.FrontAndBack, s.Wireframe ? PolygonMode.Line : PolygonMode.Fill);
        if (s.DepthTest) { gl.Enable(EnableCap.DepthTest); gl.DepthFunc(GlDevice.Compare(s.DepthCompare)); }
        else gl.Disable(EnableCap.DepthTest);
        gl.DepthMask(s.DepthWrite);
        gl.ColorMask(s.ColourWrite, s.ColourWrite, s.ColourWrite, s.ColourWrite);
        switch (s.Blend)
        {
            case GpuBlend.Opaque: gl.Disable(EnableCap.Blend); break;
            case GpuBlend.Alpha: gl.Enable(EnableCap.Blend); gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); break;
            case GpuBlend.PremultipliedAlpha: gl.Enable(EnableCap.Blend); gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha); break;
            case GpuBlend.Additive: gl.Enable(EnableCap.Blend); gl.BlendFunc(BlendingFactor.One, BlendingFactor.One); break;
        }
        if (s.AlphaToCoverage) gl.Enable(EnableCap.SampleAlphaToCoverage); else gl.Disable(EnableCap.SampleAlphaToCoverage);
        if (s.DepthClamp) gl.Enable(EnableCap.DepthClamp); else gl.Disable(EnableCap.DepthClamp);
        if (s.DepthBiasConstant != 0 || s.DepthBiasSlope != 0) { gl.Enable(EnableCap.PolygonOffsetFill); gl.PolygonOffset(s.DepthBiasSlope, s.DepthBiasConstant); }
        else gl.Disable(EnableCap.PolygonOffsetFill);
    }

    public void SetVertexBuffer(int slot, IGpuBuffer buffer, long offset = 0)
    {
        var p = pipeline ?? throw new InvalidOperationException("SetPipeline first.");
        var b = (GlBuffer)buffer;
        var layout = p.Desc.Slots[slot];
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, b.Id);
        foreach (var a in p.Desc.Attributes)
        {
            if (a.Slot != slot) continue;
            uint location = (uint)a.Location;
            gl.EnableVertexAttribArray(location);
            if (a.NormalizedBytes) gl.VertexAttribPointer(location, a.Components, VertexAttribPointerType.UnsignedByte, true, (uint)layout.Stride, (void*)(offset + a.Offset));
            else gl.VertexAttribPointer(location, a.Components, VertexAttribPointerType.Float, false, (uint)layout.Stride, (void*)(offset + a.Offset));
            gl.VertexAttribDivisor(location, layout.PerInstance ? 1u : 0u);
        }
    }

    public void SetIndexBuffer(IGpuBuffer buffer, GpuIndexType type, long offset = 0)
    {
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ((GlBuffer)buffer).Id);
        indexType = type;
        indexOffset = offset;
    }

    public void SetUniforms<T>(int binding, in T data) where T : unmanaged
    {
        if (uniformRing == 0)
        {
            gl.GetInteger(GetPName.UniformBufferOffsetAlignment, out uniformAlign);
            ringSize = 4 << 20;
            uniformRing = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.UniformBuffer, uniformRing);
            gl.BufferData(BufferTargetARB.UniformBuffer, (nuint)ringSize, null, BufferUsageARB.StreamDraw);
        }
        int size = sizeof(T);
        if (ringOffset + size > ringSize) ringOffset = 0;   // GL keeps earlier contents alive for draws already issued (driver renames on write)
        gl.BindBuffer(BufferTargetARB.UniformBuffer, uniformRing);
        fixed (T* p = &data) gl.BufferSubData(BufferTargetARB.UniformBuffer, ringOffset, (nuint)size, p);
        gl.BindBufferRange(BufferTargetARB.UniformBuffer, (uint)binding, uniformRing, ringOffset, (nuint)size);
        ringOffset = (ringOffset + size + uniformAlign - 1) / uniformAlign * uniformAlign;
    }

    public void SetUniformBuffer(int binding, IGpuBuffer buffer, long offset, long size) =>
        gl.BindBufferRange(BufferTargetARB.UniformBuffer, (uint)binding, ((GlBuffer)buffer).Id, (nint)offset, (nuint)size);

    public void SetTexture(int binding, IGpuTexture texture, IGpuSampler sampler)
    {
        var t = (GlTexture)texture;
        gl.ActiveTexture(TextureUnit.Texture0 + binding);
        gl.BindTexture(t.Target, t.Id);
        gl.BindSampler((uint)binding, ((GlSampler)sampler).Id);
    }

    PrimitiveType Topology => pipeline?.Desc.Topology switch
    {
        GpuTopology.TriangleStrip => PrimitiveType.TriangleStrip,
        GpuTopology.Lines => PrimitiveType.Lines,
        _ => PrimitiveType.Triangles,
    };

    public void Draw(int vertexCount, int instanceCount = 1, int firstVertex = 0, int firstInstance = 0)
    {
        if (firstInstance != 0) throw new NotSupportedException("OpenGL 3.3 has no base instance.");
        if (instanceCount == 1) gl.DrawArrays(Topology, firstVertex, (uint)vertexCount);
        else gl.DrawArraysInstanced(Topology, firstVertex, (uint)vertexCount, (uint)instanceCount);
    }

    public void DrawIndexed(int indexCount, int instanceCount = 1, int firstIndex = 0, int vertexOffset = 0, int firstInstance = 0)
    {
        if (firstInstance != 0) throw new NotSupportedException("OpenGL 3.3 has no base instance.");
        var type = indexType == GpuIndexType.UInt16 ? DrawElementsType.UnsignedShort : DrawElementsType.UnsignedInt;
        void* offset = (void*)(indexOffset + firstIndex * (indexType == GpuIndexType.UInt16 ? 2 : 4));
        if (instanceCount == 1 && vertexOffset == 0) gl.DrawElements(Topology, (uint)indexCount, type, offset);
        else if (vertexOffset == 0) gl.DrawElementsInstanced(Topology, (uint)indexCount, type, offset, (uint)instanceCount);
        else gl.DrawElementsInstancedBaseVertex(Topology, (uint)indexCount, type, offset, (uint)instanceCount, vertexOffset);
    }

    public void UpdateBuffer<T>(IGpuBuffer buffer, long offset, ReadOnlySpan<T> data) where T : unmanaged
    {
        var b = (GlBuffer)buffer;
        gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, b.Id);
        fixed (T* p = data) gl.BufferSubData(BufferTargetARB.CopyWriteBuffer, (nint)offset, (nuint)(data.Length * sizeof(T)), p);
        gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, 0);
    }

    public void UpdateTexture(IGpuTexture texture, int mip, int layer, int x, int y, int width, int height, ReadOnlySpan<byte> data)
    {
        var t = (GlTexture)texture;
        var (internalFormat, format, type) = GlDevice.Format(t.Desc.Format);
        gl.BindTexture(t.Target, t.Id);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        fixed (byte* p = data)
        {
            bool compressed = GpuFormats.IsCompressed(t.Desc.Format);
            if (t.Target == TextureTarget.Texture2DArray)
            {
                if (compressed) gl.CompressedTexSubImage3D(t.Target, mip, x, y, layer, (uint)width, (uint)height, 1, internalFormat, (uint)data.Length, p);
                else gl.TexSubImage3D(t.Target, mip, x, y, layer, (uint)width, (uint)height, 1, format, type, p);
            }
            else
            {
                var target = t.Target == TextureTarget.TextureCubeMap ? TextureTarget.TextureCubeMapPositiveX + layer : t.Target;
                if (compressed) gl.CompressedTexSubImage2D(target, mip, x, y, (uint)width, (uint)height, internalFormat, (uint)data.Length, p);
                else gl.TexSubImage2D(target, mip, x, y, (uint)width, (uint)height, format, type, p);
            }
        }
        gl.BindTexture(t.Target, 0);
    }

    public void GenerateMips(IGpuTexture texture)
    {
        var t = (GlTexture)texture;
        gl.BindTexture(t.Target, t.Id);
        gl.GenerateMipmap(t.Target);
        gl.BindTexture(t.Target, 0);
    }

    public void BeginTimer(IGpuTimer timer) => ((GlTimer)timer).Begin();
    public void EndTimer(IGpuTimer timer) => ((GlTimer)timer).End();

    public void Dispose()
    {
        if (framebuffer != 0) gl.DeleteFramebuffer(framebuffer);
        if (uniformRing != 0) gl.DeleteBuffer(uniformRing);
        framebuffer = uniformRing = 0;
    }
}
