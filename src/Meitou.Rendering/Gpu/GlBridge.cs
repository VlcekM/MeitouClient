namespace Meitou.Rendering.Gpu;

/// <summary>
/// (Phase 8 stage 1, docs/renderer-native.md 8.) GL names over native resources, for code that still takes a GL texture or vertex array from
/// a renderer whose resources are now native: the impostor baker and preview bind <see cref="WorldTexture.Id"/> through IGl, and
/// <see cref="TerrainRenderer.DrawMeshes"/> / <see cref="TerrainRenderer.DrawMeshesIndirect"/> take the TERRAIN-mode meshes as GL vertex
/// arrays. The native object stays the owner (<see cref="IGlInterop.Import"/>, <see cref="IGlInterop.ImportBuffer"/>: borrowed names).
/// Goes with VkGl in stage 3, once those consumers take native objects.
/// </summary>
public static unsafe class GlBridge
{
    static IGl Gl(GpuContext ctx) => ctx.Interop as IGl ?? throw new InvalidOperationException("GL names need VkGl (the interop)");

    /// <summary>Opens the frame where a GL upload would have (VkGl opens one lazily, before the host begins it: loading, offscreen tools), so a
    /// native upload (<see cref="Uploader"/>, <see cref="GpuFrame.PreFrame"/>) can record. Nothing when one is open.</summary>
    public static void EnsureFrame(GpuContext ctx)
    {
        if (!ctx.Frame.Open) ctx.Interop?.Interleave(static _ => { });
    }

    /// <summary>A GL texture name for <paramref name="texture"/> with GL sampler state of its own (what the GL texture it replaces was given):
    /// trilinear, <paramref name="wrap"/> on S and T, a border colour of 0 when <paramref name="transparentBorder"/>, the anisotropy, and the
    /// swizzle (GL enum values, 0 for none). Leaves no texture bound on the active unit.</summary>
    public static uint Texture(GpuContext ctx, Texture texture, TextureWrapMode wrap, bool transparentBorder, float anisotropy, ReadOnlySpan<int> swizzle)
    {
        var gl = Gl(ctx);
        uint id = ctx.Interop!.Import(texture);
        gl.BindTexture(TextureTarget.Texture2D, id);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)wrap);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)wrap);
        if (transparentBorder) gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBorderColor, [0f, 0f, 0f, 0f]);
        gl.TexParameter(TextureTarget.Texture2D, (TextureParameterName)0x84FE, anisotropy);   // TEXTURE_MAX_ANISOTROPY
        for (int i = 0; i < swizzle.Length && i < 4; i++)
            if (swizzle[i] != 0) gl.TexParameter(TextureTarget.Texture2D, (TextureParameterName)(0x8E42 + i), swizzle[i]);   // TEXTURE_SWIZZLE_R..A
        gl.BindTexture(TextureTarget.Texture2D, 0);
        return id;
    }

    /// <summary>(Phase 8 stage 2, post.) A GL texture name for <paramref name="texture"/> with the plain sampler state the GL texture it replaces
    /// had (no mips: <paramref name="filter"/> is <c>NEAREST</c> or <c>LINEAR</c> for both min and mag, <paramref name="wrap"/> on S and T), for a
    /// still-GL user that samples it by name. Leaves no texture bound on the active unit.</summary>
    public static uint Texture(GpuContext ctx, Texture texture, TextureMinFilter filter, TextureWrapMode wrap)
    {
        var gl = Gl(ctx);
        uint id = ctx.Interop!.Import(texture);
        gl.BindTexture(TextureTarget.Texture2D, id);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)filter);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)(filter == TextureMinFilter.Nearest ? TextureMagFilter.Nearest : TextureMagFilter.Linear));
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)wrap);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)wrap);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        return id;
    }

    /// <summary>(Phase 8 stage 2, post.) A GL framebuffer over GL texture names (<see cref="Texture(GpuContext, Gpu.Texture, TextureMinFilter, TextureWrapMode)"/>;
    /// 0 for no attachment), for guests that draw into "the bound framebuffer" (<see cref="IGlInterop.CurrentTargets"/>) and for hosts that
    /// take a framebuffer name to restore. Leaves it bound.</summary>
    public static uint Framebuffer(GpuContext ctx, uint colour, uint depth)
    {
        var gl = Gl(ctx);
        uint fbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        if (colour != 0) gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, colour, 0);
        if (depth != 0) gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, depth, 0);
        return fbo;
    }

    /// <summary>Deletes a framebuffer made by <see cref="Framebuffer"/> (not its attachments).</summary>
    public static void DeleteFramebuffer(GpuContext ctx, uint fbo)
    {
        if (fbo != 0 && ctx.Interop is IGl gl) gl.DeleteFramebuffer(fbo);
    }

    /// <summary>(Phase 8 stage 2, post.) Binds a GL framebuffer (0: the window) with a viewport of <paramref name="width"/> × <paramref name="height"/>:
    /// what <see cref="IGlInterop.CurrentTargets"/> then reports, for the guests drawing into it and for code that draws into "the bound one".</summary>
    public static void Bind(GpuContext ctx, uint fbo, int width, int height)
    {
        var gl = Gl(ctx);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        gl.Viewport(0, 0, (uint)width, (uint)height);
    }

    /// <summary>(Phase 8 stage 2, post.) The GL fixed-function state guests and later GL users read (<see cref="IGlInterop.CurrentState"/>):
    /// depth test, depth write, face culling, blending, and the colour mask.</summary>
    public static void State(GpuContext ctx, bool depthTest, bool depthWrite, bool cullFace, bool blend, bool red = true, bool green = true, bool blue = true, bool alpha = true)
    {
        var gl = Gl(ctx);
        if (depthTest) gl.Enable(EnableCap.DepthTest); else gl.Disable(EnableCap.DepthTest);
        gl.DepthMask(depthWrite);
        if (cullFace) gl.Enable(EnableCap.CullFace); else gl.Disable(EnableCap.CullFace);
        if (blend) gl.Enable(EnableCap.Blend); else gl.Disable(EnableCap.Blend);
        gl.ColorMask(red, green, blue, alpha);
    }

    /// <summary>Forgets a name made by <see cref="Texture"/> (the image stays the native texture's).</summary>
    public static void DeleteTexture(GpuContext ctx, uint id)
    {
        if (id != 0 && ctx.Interop is IGl gl) gl.DeleteTexture(id);
    }

    /// <summary>A float (or, with <see cref="Integer"/>, integer) vertex attribute of a <see cref="VertexArray"/>.</summary>
    public readonly record struct Attribute(uint Location, int Size, int Offset, bool Integer = false);

    /// <summary>A GL vertex array and the borrowed buffer names it holds.</summary>
    public readonly record struct VertexArrayNames(uint Vao, uint Vertices, uint Elements);

    /// <summary>A GL vertex array over native buffers: <paramref name="attributes"/> read <paramref name="vertices"/> with
    /// <paramref name="stride"/> (floats unnormalised, integers as unsigned bytes), and <paramref name="elements"/> is its element buffer.
    /// Leaves no vertex array bound.</summary>
    public static VertexArrayNames VertexArray(GpuContext ctx, DeviceBuffer vertices, uint stride, ReadOnlySpan<Attribute> attributes, DeviceBuffer elements)
    {
        var gl = Gl(ctx);
        var interop = ctx.Interop!;
        uint vbo = interop.ImportBuffer(vertices), ebo = interop.ImportBuffer(elements);
        uint vao = gl.GenVertexArray();
        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        foreach (var a in attributes)
        {
            gl.EnableVertexAttribArray(a.Location);
            if (a.Integer) gl.VertexAttribIPointer(a.Location, a.Size, VertexAttribIType.UnsignedByte, stride, (void*)a.Offset);
            else gl.VertexAttribPointer(a.Location, a.Size, VertexAttribPointerType.Float, false, stride, (void*)a.Offset);
        }
        gl.BindVertexArray(0);
        return new VertexArrayNames(vao, vbo, ebo);
    }

    /// <summary>Deletes what <see cref="VertexArray"/> made (the buffers stay the native owner's).</summary>
    public static void DeleteVertexArray(GpuContext ctx, VertexArrayNames names)
    {
        if (names.Vao == 0 || ctx.Interop is not IGl gl) return;
        gl.DeleteVertexArray(names.Vao);
        gl.DeleteBuffer(names.Vertices);
        gl.DeleteBuffer(names.Elements);
    }
}
