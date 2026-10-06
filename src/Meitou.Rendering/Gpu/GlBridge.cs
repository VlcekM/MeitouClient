namespace Meitou.Rendering.Gpu;

/// <summary>
/// (Phase 8 stage 1, docs/renderer-native.md 8.) GL names over native resources, for code that still takes a GL texture or vertex array from
/// a renderer whose resources are now native (the impostor baker's texture names went with its native port, stage 2):
/// <see cref="TerrainRenderer.DrawMeshes"/> / <see cref="TerrainRenderer.DrawMeshesIndirect"/> take the TERRAIN-mode meshes as GL vertex
/// arrays. The native object stays the owner (<see cref="IGlInterop.Import"/>, <see cref="IGlInterop.ImportBuffer"/>: borrowed names).
/// Stage 2 added the GL mirror of <see cref="PostProcess"/>'s targets (framebuffers over imported names, their binding, the fixed-function state
/// its guests read; docs/renderer-native.md 8.6). Goes with VkGl in stage 3, once those consumers take native objects.
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

    /// <summary>Forgets a name made by <see cref="Texture"/> (the image stays the native texture's).</summary>
    public static void DeleteTexture(GpuContext ctx, uint id)
    {
        if (id != 0 && ctx.Interop is IGl gl) gl.DeleteTexture(id);
    }
}
