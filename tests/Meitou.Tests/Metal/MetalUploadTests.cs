#if MEITOU_METAL
using Meitou.Game.Rendering.Metal;
using Meitou.Game.Rendering.Gpu;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;

namespace Meitou.Tests.MetalBackend;

/// <summary>
/// The Metal tests use one device at a time; the test GPU is real, but everything is offscreen.
/// </summary>
[CollectionDefinition("Metal GPU", DisableParallelization = true)]
public sealed class MetalGpuCollection;

/// <summary>
/// A windowless Metal context for the deterministic upload and lifetime tests.
/// </summary>
sealed class MetalTestGpu : IDisposable
{
    readonly MTLCommandQueue _queue;

    MetalTestGpu(MetalContext context, MTLCommandQueue queue)
    {
        Context = context;
        _queue = queue;
    }

    public MetalContext Context { get; }

    public static MetalTestGpu? Create()
    {
        ObjectiveC.LinkMetal();
        var device = MTLDevice.CreateSystemDefaultDevice();
        if (device.NativePtr == IntPtr.Zero) return null;
        var queue = device.NewCommandQueue();
        if (queue.NativePtr == IntPtr.Zero) return null;
        try { return new MetalTestGpu(new MetalContext(device, queue, MTLPixelFormat.BGRA8Unorm), queue); }
        catch
        {
            queue.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Context.Dispose();
        _queue.Dispose();
    }
}

[Collection("Metal GPU")]
public sealed class MetalUploadTests
{
    const int Width = 8;
    const int Height = 5;

    [Fact]
    public void SubRectangleCopiesFromTheStartOfEachSourceRow()
    {
        using var gpu = MetalTestGpu.Create();
        Assert.SkipWhen(gpu is null, "No Metal device");
        var ctx = gpu!.Context;
        using var texture = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba8, Width, Height, Name: "sub-rect upload"));
        var baseline = Rgba(Width, Height, 10, 20, 30, 255);
        Upload(ctx, texture, new PixelRect(0, 0, Width, Height), baseline);

        var tile = Rgba(3, 2, 100, 110, 120, 255);
        Upload(ctx, texture, new PixelRect(3, 2, 3, 2), tile);

        var expected = (byte[])baseline.Clone();
        for (int y = 0; y < 2; y++)
            Array.Copy(tile, y * 3 * 4, expected, ((2 + y) * Width + 3) * 4, 3 * 4);

        Assert.Equal(expected, ctx.Readback(texture));
    }

    [Fact]
    public void OddWidthR8RowsAreRepackedAndPadded()
    {
        using var gpu = MetalTestGpu.Create();
        Assert.SkipWhen(gpu is null, "No Metal device");
        var ctx = gpu!.Context;
        using var texture = ctx.CreateTexture(new TextureDesc(InternalFormat.R8, 7, 3, Name: "r8 upload"));
        var baseline = Enumerable.Repeat((byte)0x11, 7 * 3).ToArray();
        Upload(ctx, texture, new PixelRect(0, 0, 7, 3), baseline);

        // Three bytes in each of two rows: the source is tightly packed, while Metal's row needs a 4-byte stride.
        var tile = new byte[] { 1, 2, 3, 4, 5, 6 };
        Upload(ctx, texture, new PixelRect(2, 1, 3, 2), tile);

        var expected = (byte[])baseline.Clone();
        for (int y = 0; y < 2; y++)
            Array.Copy(tile, y * 3, expected, (1 + y) * 7 + 2, 3);
        Assert.Equal(expected, ctx.Readback(texture, 1));
    }

    [Fact]
    public void OddWidthR16RowsAreRepackedAndPadded()
    {
        using var gpu = MetalTestGpu.Create();
        Assert.SkipWhen(gpu is null, "No Metal device");
        var ctx = gpu!.Context;
        using var texture = ctx.CreateTexture(new TextureDesc(InternalFormat.R16, 5, 3, Name: "r16 upload"));
        var baseline = Enumerable.Repeat((byte)0x22, 5 * 3 * 2).ToArray();
        Upload(ctx, texture, new PixelRect(0, 0, 5, 3), baseline);

        var tile = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        Upload(ctx, texture, new PixelRect(2, 1, 3, 2), tile);

        var expected = (byte[])baseline.Clone();
        for (int y = 0; y < 2; y++)
            Array.Copy(tile, y * 3 * 2, expected, ((1 + y) * 5 + 2) * 2, 3 * 2);
        Assert.Equal(expected, ctx.Readback(texture, 2));
    }

    [Fact]
    public void OutOfBoundsTextureWritesAreRejectedWithoutChangingPixels()
    {
        using var gpu = MetalTestGpu.Create();
        Assert.SkipWhen(gpu is null, "No Metal device");
        var ctx = gpu!.Context;
        using var texture = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba8, 8, 5, Name: "bounds upload"));
        var baseline = Rgba(8, 5, 40, 50, 60, 255);
        Upload(ctx, texture, new PixelRect(0, 0, 8, 5), baseline);

        using (var batch = ctx.Uploads.Begin())
            batch.Write(texture, 0, 0, new PixelRect(7, 4, 2, 2), Rgba(2, 2, 255, 0, 0, 255));

        Assert.Equal(baseline, ctx.Readback(texture));
    }

    [Fact]
    public void ShortTextureUploadsAreRejected()
    {
        using var gpu = MetalTestGpu.Create();
        Assert.SkipWhen(gpu is null, "No Metal device");
        var ctx = gpu!.Context;
        using var texture = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba8, 4, 4, Name: "short upload"));

        Assert.Throws<ArgumentException>(() =>
        {
            using var batch = ctx.Uploads.Begin();
            batch.Write(texture, 0, 0, new PixelRect(0, 0, 2, 2), new byte[3]);
        });
    }

    [Fact]
    public void BufferWritesHonorOffsetsAndRejectThePastEnd()
    {
        using var gpu = MetalTestGpu.Create();
        Assert.SkipWhen(gpu is null, "No Metal device");
        var ctx = gpu!.Context;
        using var target = DeviceBuffer.Create(ctx, 16, BufferUse.TransferDst, "buffer bounds");

        using (var batch = ctx.Uploads.Begin())
            batch.Write(target, 12, new byte[] { 1, 2, 3, 4 });
        var expected = new byte[16];
        expected[12] = 1;
        expected[13] = 2;
        expected[14] = 3;
        expected[15] = 4;
        Assert.Equal(expected, ctx.ReadBuffer(target.Handle));

        using (var batch = ctx.Uploads.Begin())
            batch.Write(target, 13, new byte[] { 9, 9, 9, 9 });
        Assert.Equal(expected, ctx.ReadBuffer(target.Handle));
    }

    [Fact]
    public void ResourceTablesDoNotRetainReleasedTexturesViewsBuffersOrReadbacks()
    {
        using var gpu = MetalTestGpu.Create();
        Assert.SkipWhen(gpu is null, "No Metal device");
        var ctx = gpu!.Context;
        var before = ctx.LiveResourceCounts();

        for (int i = 0; i < 12; i++)
        {
            using var texture = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba8, 4, 4, Name: $"table leak {i}"));
            _ = texture.View();
            using var buffer = DeviceBuffer.Create(ctx, 64, BufferUse.TransferDst, $"table leak buffer {i}");
            using var readback = ctx.CreateReadback(32, $"table leak readback {i}");
        }

        ctx.WaitForGpu();
        Assert.Equal(before, ctx.LiveResourceCounts());
    }

    [Fact]
    public void RepeatedViewsAreCachedAndReleasedWithTheirTexture()
    {
        using var gpu = MetalTestGpu.Create();
        Assert.SkipWhen(gpu is null, "No Metal device");
        var ctx = gpu!.Context;
        var before = ctx.LiveResourceCounts();
        var texture = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba8, 4, 4, Name: "view cache"));
        try
        {
            var first = texture.View();
            var second = texture.View();
            Assert.Equal(first, second);
            Assert.Equal(before.Views + 1, ctx.LiveResourceCounts().Views);
        }
        finally
        {
            texture.Dispose();
        }
        Assert.Equal(before.Views, ctx.LiveResourceCounts().Views);
    }

    [Fact]
    public void DeferredReleaseRunsImmediatelyWhenNoFrameIsOpen()
    {
        using var gpu = MetalTestGpu.Create();
        Assert.SkipWhen(gpu is null, "No Metal device");
        int released = 0;
        gpu!.Context.DeferDelete(() => released++);
        Assert.Equal(1, released);
    }

    [Fact]
    public void MipLevelRoundTripsAndInvalidLevelsAreRejected()
    {
        using var gpu = MetalTestGpu.Create();
        Assert.SkipWhen(gpu is null, "No Metal device");
        var ctx = gpu!.Context;
        using var texture = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba8, 8, 8, Levels: 2, Name: "mip upload"));
        var levelOne = Rgba(4, 4, 70, 80, 90, 255);
        Upload(ctx, texture, new PixelRect(0, 0, 4, 4), levelOne, level: 1);
        Assert.Equal(levelOne, ctx.Readback(texture, 4, level: 1));

        Upload(ctx, texture, new PixelRect(0, 0, 4, 4), Rgba(4, 4, 255, 0, 0, 255), level: 2);
        Assert.Equal(levelOne, ctx.Readback(texture, 4, level: 1));
    }

    [Fact]
    public void ArrayLayerRoundTripsAndInvalidLayersAreRejected()
    {
        using var gpu = MetalTestGpu.Create();
        Assert.SkipWhen(gpu is null, "No Metal device");
        var ctx = gpu!.Context;
        using var texture = ctx.CreateTexture(new TextureDesc(InternalFormat.Rgba8, 4, 4, Layers: 2,
            Kind: TextureKind.Texture2DArray, Name: "array upload"));
        var layerZero = Rgba(4, 4, 20, 30, 40, 255);
        var layerOne = Rgba(4, 4, 50, 60, 70, 255);
        Upload(ctx, texture, new PixelRect(0, 0, 4, 4), layerZero, layer: 0);
        Upload(ctx, texture, new PixelRect(0, 0, 4, 4), layerOne, layer: 1);
        Assert.Equal(layerZero, ctx.Readback(texture, 4, layer: 0));
        Assert.Equal(layerOne, ctx.Readback(texture, 4, layer: 1));

        Upload(ctx, texture, new PixelRect(0, 0, 4, 4), Rgba(4, 4, 255, 0, 0, 255), layer: 2);
        Assert.Equal(layerZero, ctx.Readback(texture, 4, layer: 0));
    }

    static void Upload(MetalContext ctx, GpuTexture texture, PixelRect region, byte[] data, int level = 0, int layer = 0)
    {
        using var batch = ctx.Uploads.Begin();
        batch.Write(texture, level, layer, region, data);
    }

    static byte[] Rgba(int width, int height, byte r, byte g, byte b, byte a)
    {
        var data = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
            (data[i * 4], data[i * 4 + 1], data[i * 4 + 2], data[i * 4 + 3]) = (r, g, b, a);
        return data;
    }
}
#endif
