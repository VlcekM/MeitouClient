using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu;
using SharpMetal.Foundation;
using SharpMetal.Metal;

namespace Meitou.Metal;

/// <summary>
/// The uploads: each <see cref="IGpuUploads.Begin"/> batch records its copies into its own command buffer (a
/// blit encoder over a shared staging ring), which is committed when the batch is disposed. The queue executes
/// its command buffers in commit order, so a batch disposed before the frame's own commands are committed is
/// ordered ahead of them. The staging buffers are shared mode — on Apple silicon the whole memory is unified,
/// and the copy is the GPU's own from it (no separate staging allocation).
/// </summary>
public sealed class MetalUploads : IGpuUploads
{
    readonly MetalContext ctx;

    public MetalUploads(MetalContext ctx) => this.ctx = ctx;

    public IGpuUploadBatch Begin() => new MetalUploadBatch(ctx);

    internal sealed unsafe class MetalUploadBatch : IGpuUploadBatch
    {
        readonly MetalContext ctx;
        readonly MTLCommandBuffer command;
        readonly MetalCommandList list;
        readonly List<string> written = [];
        MTLBuffer staging;
        ulong stagingSize, stagingUsed;

        public MetalUploadBatch(MetalContext ctx)
        {
            this.ctx = ctx;
            command = ctx.Queue.CommandBuffer();
            list = new MetalCommandList(ctx, "uploads", command);
        }

        public IGpuCommandList Commands => list;

        public GpuTexture Create(TextureDesc desc) => ctx.CreateTexture(desc);

        public void Write(GpuTexture target, int level, int layer, in PixelRect region, ReadOnlySpan<byte> data)
        {
            var desc = target.Desc;
            if (level < 0 || level >= desc.Levels)
            {
                Console.WriteLine($"warning   upload outside {desc.Name} L{level} layer {layer}: level is not in 0..{desc.Levels - 1}");
                return;
            }
            uint maxWidth = (uint)Math.Max(desc.Width >> level, 1), maxHeight = (uint)Math.Max(desc.Height >> level, 1);
            // A cube's six faces are its type's slices, not array layers; the descriptor's Layers stays 1.
            uint layers = desc.Kind == TextureKind.Cube ? 6u * (uint)Math.Max(desc.Layers, 1) : (uint)desc.Layers;
            if (region.X < 0 || region.Y < 0 || (uint)region.X + region.Width > maxWidth ||
                (uint)region.Y + region.Height > maxHeight || (uint)layer >= layers)
            {
                Console.WriteLine($"warning   upload outside {desc.Name} L{level} layer {layer}: " +
                    $"{region.X},{region.Y} {region.Width}x{region.Height} (texture {maxWidth}x{maxHeight}, {layers} layers)");
                return;
            }
            written.Add($"{desc.Name} L{level}");
            int block = MetalConventions.BlockWidth(desc.Format);
            int texel = MetalConventions.TexelBytes(desc.Format);
            // A block-compressed row is whole blocks (a tail mip's 1x1 level still strides one block).
            ulong bytesPerRow = (ulong)(((region.Width + block - 1) / block) * texel);
            ulong blockRows = (ulong)((region.Height + block - 1) / block);
            ulong bytesPerImage = bytesPerRow * blockRows;
            if ((ulong)data.Length < bytesPerImage) throw new ArgumentException($"the upload has {data.Length} bytes, the region needs {bytesPerImage}");
            // Metal's blit reads a source row from its start, then writes it at the destination origin. Repack a sub-rectangle
            // into full-width rows with the tile at offset zero; adding region.X to the source as well would shift it twice.
            // Metal's buffer-to-texture copy needs the source row stride 4-byte aligned. R16/R8 textures
            // (the terrain heights and masks) can have odd widths, so pad the repacked rows.
            ulong levelWidth = (ulong)Math.Max(desc.Width >> level, 1);
            ulong neededRow = (((levelWidth + (ulong)block - 1) / (ulong)block) * (ulong)texel + 3) & ~3UL;
            var blit = list.OpenBlit();
            if (bytesPerRow != neededRow || region.X != 0)
            {
                ulong neededImage = neededRow * blockRows;
                var staged = Reserve(checked((int)neededImage), out ulong stagedOffset);
                var dst = (byte*)staged.Contents + stagedOffset;
                if (staged.Contents == IntPtr.Zero || dst is null) throw new InvalidOperationException($"no staging for {desc.Name} L{level}: {neededImage} bytes");
                new Span<byte>(dst, (int)neededImage).Clear();
                fixed (byte* src = data)
                {
                    if (src is null) throw new InvalidOperationException($"no source for {desc.Name} L{level}: {data.Length} bytes");
                    for (ulong r = 0; r < blockRows; r++)
                        Buffer.MemoryCopy(src + r * bytesPerRow, dst + (nint)(r * neededRow), (long)neededRow, (long)bytesPerRow);
                }
                blit.CopyFromBuffer(staged, stagedOffset, neededRow, neededImage,
                    new MTLSize { width = (ulong)region.Width, height = (ulong)region.Height, depth = 1 },
                    ctx.TextureAt(target), (ulong)layer, (ulong)level,
                    new MTLOrigin { x = (ulong)region.X, y = (ulong)region.Y, z = 0 });
            }
            else
            {
                var buffer = Reserve(data.Length, out ulong offset);
                data.CopyTo(new Span<byte>((void*)(buffer.Contents + (nint)offset), data.Length));
                blit.CopyFromBuffer(buffer, offset, bytesPerRow, bytesPerImage,
                    new MTLSize { width = (ulong)region.Width, height = (ulong)region.Height, depth = 1 },
                    ctx.TextureAt(target), (ulong)layer, (ulong)level,
                    new MTLOrigin { x = (ulong)region.X, y = (ulong)region.Y, z = 0 });
            }
            ctx.CountUpload(data.Length);
        }

        public void Write(DeviceBuffer target, ulong offset, ReadOnlySpan<byte> data)
        {
            written.Add($"{target.Name} buffer +{offset} ({data.Length} B)");
            if (offset > target.Size || (ulong)data.Length > target.Size - offset)
            {
                Console.WriteLine($"warning   upload outside {target.Name} buffer: " +
                    $"{offset} + {data.Length} bytes (buffer {target.Size})");
                return;
            }
            var buffer = Reserve(data.Length, out ulong stagingOffset);
            data.CopyTo(new Span<byte>((void*)(buffer.Contents + (nint)stagingOffset), data.Length));
            var blit = list.OpenBlit();
            blit.CopyFromBuffer(buffer, stagingOffset, ctx.BufferAt(target.Handle), offset, (ulong)data.Length);
            ctx.CountUpload(data.Length);
        }

        /// <summary>The staging room for <paramref name="bytes"/> more (a grown buffer when the current is full).</summary>
        MTLBuffer Reserve(int bytes, out ulong offset)
        {
            // Buffer-to-texture copies require a 4-byte-aligned source offset, and the staging ring mixes
            // texture rows (R16 height slabs) with buffer writes.
            ulong aligned = (stagingUsed + 3) & ~3UL;
            if (aligned > stagingSize || stagingSize - aligned < (ulong)bytes)
            {
                // The blits already recorded in this batch still name the old buffer: keep it until the command completes.
                if (staging.NativePtr != IntPtr.Zero) ctx.RetireUpload(command, staging, string.Join(", ", written));
                // Many small batches (foliage meshes, textures) live until their command completes; a 1 MB floor each
                // made the first foliage frame allocate several GB. Grow only to what this batch actually writes.
                stagingSize = Math.Max((ulong)bytes + 3, stagingSize == 0 ? 64 << 10 : stagingSize * 2);
                staging = ctx.Device.NewBuffer(stagingSize, MTLResourceOptions.ResourceStorageModeShared);
                stagingUsed = 0;
                aligned = 0;
            }
            offset = aligned;
            stagingUsed = aligned + (ulong)bytes;
            return staging;
        }

        public void Dispose()
        {
            list.EndBlit();
            command.Commit();
            // The GPU reads the staging buffer after commit; the context releases it once this command has completed.
            if (staging.NativePtr != IntPtr.Zero) ctx.RetireUpload(command, staging, string.Join(", ", written));
        }
    }
}
