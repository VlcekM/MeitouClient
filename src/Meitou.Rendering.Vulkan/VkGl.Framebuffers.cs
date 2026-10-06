using Meitou.Rendering.Vulkan.Core;
using Meitou.Rendering.Gpu;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Vulkan;

public sealed unsafe partial class VkGl
{
    internal readonly record struct Attachment(GlTextureObj Texture, int Level, int Layer);

    internal sealed class GlFramebufferObj
    {
        public Attachment? Colour, Depth;
        public bool DrawNone;
    }

    uint drawFramebuffer, readFramebuffer;

    // The render pass being recorded (dynamic rendering), if any.
    bool passActive;
    Attachment? passColour, passDepth;
    int passWidth, passHeight;

    public uint GenFramebuffer()
    {
        uint id = NewId();
        framebuffers[id] = new GlFramebufferObj();
        return id;
    }

    public void DeleteFramebuffer(uint framebuffer)
    {
        framebuffers.Remove(framebuffer);
        if (drawFramebuffer == framebuffer) { EndPass(); drawFramebuffer = 0; }
        if (readFramebuffer == framebuffer) readFramebuffer = 0;
    }

    public void BindFramebuffer(FramebufferTarget target, uint framebuffer)
    {
        Stats.BindCalls++;
        if (target is FramebufferTarget.Framebuffer or FramebufferTarget.DrawFramebuffer) drawFramebuffer = framebuffer;
        if (target is FramebufferTarget.Framebuffer or FramebufferTarget.ReadFramebuffer) readFramebuffer = framebuffer;
    }

    GlFramebufferObj Fb(FramebufferTarget target)
    {
        uint id = target == FramebufferTarget.ReadFramebuffer ? readFramebuffer : drawFramebuffer;
        return framebuffers.TryGetValue(id, out var f) ? f : throw new InvalidOperationException("framebuffer 0 has no attachments to change");
    }

    public void FramebufferTexture2D(FramebufferTarget target, FramebufferAttachment attachment, TextureTarget textarget, uint texture, int level)
    {
        var fb = Fb(target);
        Attachment? a = texture == 0 ? null : new Attachment(textures[texture],
            level, textarget is >= TextureTarget.TextureCubeMapPositiveX and <= TextureTarget.TextureCubeMapNegativeZ ? textarget - TextureTarget.TextureCubeMapPositiveX : 0);
        Attach(fb, attachment, a);
    }

    public void FramebufferRenderbuffer(FramebufferTarget target, FramebufferAttachment attachment, RenderbufferTarget renderbuffertarget, uint renderbuffer) =>
        Attach(Fb(target), attachment, renderbuffer == 0 ? null : new Attachment(renderbuffers[renderbuffer].Texture, 0, 0));

    void Attach(GlFramebufferObj fb, FramebufferAttachment attachment, Attachment? a)
    {
        if (passActive) EndPass();
        switch (attachment)
        {
            case FramebufferAttachment.ColorAttachment0: fb.Colour = a; break;
            case FramebufferAttachment.DepthAttachment or FramebufferAttachment.DepthStencilAttachment: fb.Depth = a; break;
            default: throw new NotSupportedException($"framebuffer attachment {attachment}");
        }
    }

    public GLEnum CheckFramebufferStatus(FramebufferTarget target) => GLEnum.FramebufferComplete;

    public void DrawBuffer(DrawBufferMode buf)
    {
        if (framebuffers.TryGetValue(drawFramebuffer, out var fb)) fb.DrawNone = buf == DrawBufferMode.None;
    }

    public void ReadBuffer(ReadBufferMode src) { }

    // ----- Renderbuffers -----

    internal sealed class GlRenderbufferObj
    {
        public GlTextureObj Texture = null!;
    }

    uint boundRenderbuffer;

    public uint GenRenderbuffer()
    {
        uint id = NewId();
        renderbuffers[id] = new GlRenderbufferObj();
        return id;
    }

    public void DeleteRenderbuffer(uint renderbuffer)
    {
        if (renderbuffers.Remove(renderbuffer, out var r) && r.Texture is not null) DestroyTexture(r.Texture);
    }

    public void BindRenderbuffer(RenderbufferTarget target, uint renderbuffer) => boundRenderbuffer = renderbuffer;

    public void RenderbufferStorage(RenderbufferTarget target, InternalFormat internalformat, uint width, uint height) =>
        RenderbufferStorageMultisample(target, 1, internalformat, width, height);

    public void RenderbufferStorageMultisample(RenderbufferTarget target, uint samples, InternalFormat internalformat, uint width, uint height)
    {
        var r = renderbuffers[boundRenderbuffer];
        if (r.Texture is not null) DestroyTexture(r.Texture);
        var format = VkFormat(internalformat);
        r.Texture = NewRenderTexture(format, (int)width, (int)height, Math.Max((int)samples, 1), $"renderbuffer {boundRenderbuffer}");
        r.Texture.GlFormat = internalformat;
    }

    // ----- Render passes -----

    (Attachment? Colour, Attachment? Depth) DrawTargets()
    {
        if (drawFramebuffer == 0) return (new Attachment(backbuffer!, 0, 0), new Attachment(backbufferDepth!, 0, 0));
        var fb = framebuffers[drawFramebuffer];
        return (fb.DrawNone ? null : fb.Colour, fb.Depth);
    }

    /// <summary>Starts dynamic rendering into the bound draw framebuffer if it is not already the pass being recorded.</summary>
    void EnsurePass(bool clearColour = false, bool clearDepth = false)
    {
        var (colour, depth) = DrawTargets();
        if (passActive && passColour == colour && passDepth == depth && !clearColour && !clearDepth) return;
        EndPass();
        var cb = Cmd;
        FullBarrier(cb);   // everything written before (attachments, transfers) is visible to this pass, and this pass waits for earlier reads
        var size = colour is { } c ? Size(c) : depth is { } d ? Size(d) : (1, 1);
        RenderingAttachmentInfo colourInfo = default, depthInfo = default;
        if (colour is { } ca)
        {
            colourInfo = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = AttachmentView(ca.Texture, ca.Level, ca.Layer),
                ImageLayout = ImageLayout.General,
                LoadOp = clearColour ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue(new ClearColorValue(clearColourValue.X, clearColourValue.Y, clearColourValue.Z, clearColourValue.W)),
            };
        }
        if (depth is { } da)
        {
            depthInfo = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = AttachmentView(da.Texture, da.Level, da.Layer),
                ImageLayout = ImageLayout.General,
                LoadOp = clearDepth ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue((float)clearDepthValue, 0)),
            };
        }
        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)size.Item1, (uint)size.Item2)),
            LayerCount = 1,
            ColorAttachmentCount = colour is null ? 0u : 1u,
            PColorAttachments = colour is null ? null : &colourInfo,
            PDepthAttachment = depth is null ? null : &depthInfo,
        };
        vk.CmdBeginRendering(cb, &rendering);
        passActive = true;
        passColour = colour;
        passDepth = depth;
        (passWidth, passHeight) = size;
        Stats.RenderPasses++;
        dynamicStateDirty = true;
    }

    static (int, int) Size(Attachment a) => (Math.Max(a.Texture.Width >> a.Level, 1), Math.Max(a.Texture.Height >> a.Level, 1));

    void EndPass()
    {
        if (!passActive) return;
        vk.CmdEndRendering(cmd);
        passActive = false;
        passColour = passDepth = null;
    }

    public void Clear(ClearBufferMask mask)
    {
        var (colour, depth) = DrawTargets();
        bool c = mask.HasFlag(ClearBufferMask.ColorBufferBit) && colour is not null && (colourMask.R || colourMask.G || colourMask.B || colourMask.A);
        bool d = mask.HasFlag(ClearBufferMask.DepthBufferBit) && depth is not null && depthWrite;
        if (!c && !d) return;
        var size = colour is { } ca ? Size(ca) : Size(depth!.Value);
        bool full = !scissorTest || (scissor.X <= 0 && scissor.Y <= 0 && scissor.X + scissor.W >= size.Item1 && scissor.Y + scissor.H >= size.Item2);
        bool partialMask = c && !(colourMask.R && colourMask.G && colourMask.B && colourMask.A);
        if (hostList is { } host && !hostGuestOpen)
        {
            // Inside a native host's rendering of the same targets (wave 4, the scene's host): the clear VkGl would record into its own open pass
            // (vkCmdClearAttachments over the target or the scissor), recorded in its place among the host's segments.
            if (colour != hostTargets.Colour || depth != hostTargets.Depth) throw new InvalidOperationException("Clear inside a native host of other targets");
            if (partialMask) throw new NotSupportedException("clearing with a partial colour mask");
            var area = full
                ? new Rect2D(new Offset2D(0, 0), new Extent2D((uint)size.Item1, (uint)size.Item2))
                : new Rect2D(new Offset2D(Math.Max(scissor.X, 0), Math.Max(scissor.Y, 0)), new Extent2D((uint)Math.Max(scissor.W, 0), (uint)Math.Max(scissor.H, 0)));
            Context.Clear(host, c, new ClearColorValue(clearColourValue.X, clearColourValue.Y, clearColourValue.Z, clearColourValue.W), d, (float)clearDepthValue, area);
            return;
        }
        if (full && !partialMask && !(passActive && passColour == colour && passDepth == depth))
        {
            EnsurePass(c, d);
            return;
        }
        if (partialMask) throw new NotSupportedException("clearing with a partial colour mask");
        EnsurePass();
        var attachments = stackalloc ClearAttachment[2];
        uint n = 0;
        if (c) attachments[n++] = new ClearAttachment(ImageAspectFlags.ColorBit, 0, new ClearValue(new ClearColorValue(clearColourValue.X, clearColourValue.Y, clearColourValue.Z, clearColourValue.W)));
        if (d) attachments[n++] = new ClearAttachment(ImageAspectFlags.DepthBit, 0, new ClearValue(depthStencil: new ClearDepthStencilValue((float)clearDepthValue, 0)));
        var rect = full
            ? new ClearRect(new Rect2D(new Offset2D(0, 0), new Extent2D((uint)size.Item1, (uint)size.Item2)), 0, 1)
            : new ClearRect(new Rect2D(new Offset2D(Math.Max(scissor.X, 0), Math.Max(scissor.Y, 0)), new Extent2D((uint)Math.Max(scissor.W, 0), (uint)Math.Max(scissor.H, 0))), 0, 1);
        vk.CmdClearAttachments(cmd, n, attachments, 1, &rect);
    }

    // ----- Blits and readback -----

    (Attachment? Colour, Attachment? Depth) ReadTargets()
    {
        if (readFramebuffer == 0) return (new Attachment(backbuffer!, 0, 0), new Attachment(backbufferDepth!, 0, 0));
        var fb = framebuffers[readFramebuffer];
        return (fb.Colour, fb.Depth);
    }

    public void BlitFramebuffer(int srcX0, int srcY0, int srcX1, int srcY1, int dstX0, int dstY0, int dstX1, int dstY1, ClearBufferMask mask, BlitFramebufferFilter filter)
    {
        EndPass();
        var cb = Cmd;
        FullBarrier(cb);
        var (srcColour, srcDepth) = ReadTargets();
        var (dstColour, dstDepth) = DrawTargets();
        if (mask.HasFlag(ClearBufferMask.ColorBufferBit) && srcColour is { } sc && dstColour is { } dc)
            Blit(cb, sc, dc, ImageAspectFlags.ColorBit, srcX0, srcY0, srcX1, srcY1, dstX0, dstY0, dstX1, dstY1, filter);
        if (mask.HasFlag(ClearBufferMask.DepthBufferBit) && srcDepth is { } sd && dstDepth is { } dd)
            Blit(cb, sd, dd, ImageAspectFlags.DepthBit, srcX0, srcY0, srcX1, srcY1, dstX0, dstY0, dstX1, dstY1, BlitFramebufferFilter.Nearest);
        FullBarrier(cb);
    }

    void Blit(CommandBuffer cb, Attachment src, Attachment dst, ImageAspectFlags aspect, int sx0, int sy0, int sx1, int sy1, int dx0, int dy0, int dx1, int dy1, BlitFramebufferFilter filter)
    {
        var s = src.Texture;
        var d = dst.Texture;
        bool sameRect = sx0 == dx0 && sy0 == dy0 && sx1 == dx1 && sy1 == dy1;
        if (s.Samples > 1 && d.Samples == 1)
        {
            if (!sameRect) throw new NotSupportedException("a multisample resolve to another rectangle");
            if (aspect == ImageAspectFlags.ColorBit)
            {
                var region = new ImageResolve
                {
                    SrcSubresource = new ImageSubresourceLayers(aspect, (uint)src.Level, (uint)src.Layer, 1),
                    DstSubresource = new ImageSubresourceLayers(aspect, (uint)dst.Level, (uint)dst.Layer, 1),
                    SrcOffset = new Offset3D(sx0, sy0, 0), DstOffset = new Offset3D(dx0, dy0, 0),
                    Extent = new Extent3D((uint)(sx1 - sx0), (uint)(sy1 - sy0), 1),
                };
                vk.CmdResolveImage(cb, s.Image!.Image, ImageLayout.General, d.Image!.Image, ImageLayout.General, 1, &region);
            }
            else ResolveDepth(cb, src, dst, sx1 - sx0, sy1 - sy0);
            return;
        }
        if (s.Samples != d.Samples) throw new NotSupportedException("a blit between different sample counts");
        if (sameRect && s.Format == d.Format)
        {
            var copy = new ImageCopy
            {
                SrcSubresource = new ImageSubresourceLayers(aspect, (uint)src.Level, (uint)src.Layer, 1),
                DstSubresource = new ImageSubresourceLayers(aspect, (uint)dst.Level, (uint)dst.Layer, 1),
                SrcOffset = new Offset3D(sx0, sy0, 0), DstOffset = new Offset3D(dx0, dy0, 0),
                Extent = new Extent3D((uint)(sx1 - sx0), (uint)(sy1 - sy0), 1),
            };
            vk.CmdCopyImage(cb, s.Image!.Image, ImageLayout.General, d.Image!.Image, ImageLayout.General, 1, &copy);
            return;
        }
        var blit = new ImageBlit
        {
            SrcSubresource = new ImageSubresourceLayers(aspect, (uint)src.Level, (uint)src.Layer, 1),
            DstSubresource = new ImageSubresourceLayers(aspect, (uint)dst.Level, (uint)dst.Layer, 1),
        };
        blit.SrcOffsets[0] = new Offset3D(sx0, sy0, 0);
        blit.SrcOffsets[1] = new Offset3D(sx1, sy1, 1);
        blit.DstOffsets[0] = new Offset3D(dx0, dy0, 0);
        blit.DstOffsets[1] = new Offset3D(dx1, dy1, 1);
        vk.CmdBlitImage(cb, s.Image!.Image, ImageLayout.General, d.Image!.Image, ImageLayout.General, 1, &blit, filter == BlitFramebufferFilter.Linear ? Filter.Linear : Filter.Nearest);
    }

    /// <summary>A multisampled depth buffer to a single-sample one (GL blits it; Vulkan resolves depth only in a render pass): sample 0.</summary>
    void ResolveDepth(CommandBuffer cb, Attachment src, Attachment dst, int width, int height)
    {
        var info = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = AttachmentView(src.Texture, src.Level, src.Layer), ImageLayout = ImageLayout.General,
            ResolveMode = ResolveModeFlags.SampleZeroBit,
            ResolveImageView = AttachmentView(dst.Texture, dst.Level, dst.Layer), ResolveImageLayout = ImageLayout.General,
            LoadOp = AttachmentLoadOp.Load, StoreOp = AttachmentStoreOp.Store,
        };
        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)width, (uint)height)),
            LayerCount = 1, PDepthAttachment = &info,
        };
        vk.CmdBeginRendering(cb, &rendering);
        vk.CmdEndRendering(cb);
    }

    public void ReadPixels(int x, int y, uint width, uint height, PixelFormat format, PixelType type, void* pixels)
    {
        EndPass();
        var (colour, _) = ReadTargets();
        var src = colour ?? throw new InvalidOperationException("ReadPixels without a colour attachment");
        var t = src.Texture;
        if (t.Samples > 1) throw new NotSupportedException("ReadPixels from a multisampled image");
        int texel = BytesPerTexel(t.Format);
        ulong bytes = (ulong)(width * height * texel);
        var readback = device.Allocator.CreateBuffer(bytes, BufferUsageFlags.TransferDstBit, MemoryKind.Readback, "readback");
        var cb = Cmd;
        FullBarrier(cb);
        var region = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)src.Level, (uint)src.Layer, 1),
            ImageOffset = new Offset3D(x, y, 0),
            ImageExtent = new Extent3D(width, height, 1),
        };
        vk.CmdCopyImageToBuffer(cb, t.Image!.Image, ImageLayout.General, readback.Buffer, 1, &region);
        Flush();
        Stats.Flushes++;
        byte* data = (byte*)readback.Mapped;
        int components = format switch { PixelFormat.Red => 1, PixelFormat.RG => 2, PixelFormat.Rgb => 3, _ => 4 };
        int outBytes = type == PixelType.Float ? 4 : 1;
        int rowOut = (int)width * components * outBytes;
        int pitch = (rowOut + packAlignment - 1) / packAlignment * packAlignment;
        for (int row = 0; row < height; row++)
            for (int i = 0; i < width; i++)
            {
                byte* s = data + ((long)row * width + i) * texel;
                byte* d = (byte*)pixels + (long)row * pitch + (long)i * components * outBytes;
                for (int c = 0; c < components; c++)
                {
                    float v = t.Format switch
                    {
                        Format.R8G8B8A8Unorm => s[c] / 255f,
                        Format.R16G16B16A16Sfloat => (float)((Half*)s)[c],
                        Format.R32G32B32A32Sfloat => ((float*)s)[c],
                        Format.R16G16Sfloat => c < 2 ? (float)((Half*)s)[c] : 0,
                        Format.R32G32Sfloat => c < 2 ? ((float*)s)[c] : 0,
                        Format.R32Sfloat => c == 0 ? ((float*)s)[0] : 0,
                        Format.R16Sfloat => c == 0 ? (float)((Half*)s)[0] : 0,
                        _ => throw new NotSupportedException($"ReadPixels from {t.Format}"),
                    };
                    if (type == PixelType.Float) ((float*)d)[c] = v;
                    else d[c] = t.Format == Format.R8G8B8A8Unorm ? s[c] : (byte)Math.Clamp(MathF.Round(v * 255), 0, 255);
                }
            }
        device.Allocator.Free(readback);
    }

    public void ReadPixels<T>(int x, int y, uint width, uint height, PixelFormat format, PixelType type, Span<T> pixels) where T : unmanaged
    {
        fixed (T* p = pixels) ReadPixels(x, y, width, height, format, type, p);
    }
}
