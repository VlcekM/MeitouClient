using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gpu.Core;

public enum MemoryKind
{
    /// <summary>Device-local memory (not host visible when a type without that exists).</summary>
    DeviceLocal,
    /// <summary>Host visible + coherent, persistently mapped. Small requests go to resizable-BAR memory when it exists.</summary>
    Upload,
    /// <summary>Host visible + cached (CPU reads are fast); flush/invalidate when not coherent.</summary>
    Readback,
}

/// <summary>A range of a memory block. Free it through <see cref="GpuAllocator.Free(Allocation)"/>.</summary>
public sealed class Allocation
{
    internal MemoryBlock Block = null!;
    internal bool Freed;

    public DeviceMemory Memory => Block.Memory;
    public ulong Offset { get; internal set; }
    public ulong Size { get; internal set; }
    /// <summary>The start of this range in host memory (non-zero for host-visible memory).</summary>
    public IntPtr MappedPointer { get; internal set; }
    public uint MemoryTypeIndex => Block.MemoryTypeIndex;
    public bool IsHostCoherent => Block.Coherent;
}

internal sealed class MemoryBlock
{
    public DeviceMemory Memory;
    public ulong Size;
    public IntPtr Mapped;
    public uint MemoryTypeIndex;
    public bool Optimal;
    public bool Dedicated;
    public bool Coherent;
    public ulong Used;
    public readonly List<(ulong Offset, ulong Size)> Free = new();
}

/// <summary>A memory block as seen by tests and diagnostics.</summary>
public readonly record struct BlockInfo(uint MemoryTypeIndex, bool Optimal, bool Dedicated, ulong Size, ulong Used, int FreeRanges);

public sealed class GpuBuffer
{
    internal bool Freed;
    public Silk.NET.Vulkan.Buffer Buffer { get; internal set; }
    public Allocation Allocation { get; internal set; } = null!;
    public ulong Size { get; internal set; }
    public BufferUsageFlags Usage { get; internal set; }
    /// <summary>The name given at creation (diagnostics: <see cref="GpuAllocator.Breakdown"/>).</summary>
    public string Name { get; internal set; } = "";
    public MemoryKind Kind { get; internal set; }
    /// <summary>Host pointer to the start of the buffer (null for device-local memory).</summary>
    public unsafe void* Mapped => (void*)Allocation.MappedPointer;
}

public sealed class GpuImage
{
    internal bool Freed;
    public Image Image { get; internal set; }
    public Allocation Allocation { get; internal set; } = null!;
    public Format Format { get; internal set; }
    public Extent3D Extent { get; internal set; }
    public uint MipLevels { get; internal set; }
    public uint ArrayLayers { get; internal set; }
    public SampleCountFlags Samples { get; internal set; }
    public ImageUsageFlags Usage { get; internal set; }
    public string Name { get; internal set; } = "";
    public MemoryKind Kind { get; internal set; }
}

/// <summary>
/// Suballocates device memory: per memory type, blocks (default 64 MB) with a sorted free list (best fit, alignment
/// respected, neighbours coalesced on free). Buffers/linear images and optimal images live in separate blocks, which
/// satisfies bufferImageGranularity. A request above half a block gets a dedicated block. Thread-safe.
/// </summary>
public sealed unsafe class GpuAllocator : IDisposable
{
    public const ulong DefaultBlockSize = 64UL * 1024 * 1024;
    const ulong RebarMinHeap = 1UL << 30;
    const ulong RebarMaxRequest = 16UL << 20;

    readonly VulkanDevice dev;
    readonly Vk vk;
    readonly ulong blockSize;
    readonly object gate = new();
    readonly Dictionary<(uint Type, bool Optimal), List<MemoryBlock>> pools = new();
    readonly HashSet<GpuBuffer> liveBuffers = new();
    readonly HashSet<GpuImage> liveImages = new();
    PhysicalDeviceMemoryProperties memProps;
    ulong totalAllocated;
    ulong totalUsed;
    int blockCount;
    int allocationCount;
    bool disposed;

    internal GpuAllocator(VulkanDevice dev, ulong blockSize = DefaultBlockSize)
    {
        this.dev = dev;
        vk = dev.Vk;
        this.blockSize = blockSize;
        vk.GetPhysicalDeviceMemoryProperties(dev.PhysicalDevice, out memProps);
    }

    public ulong BlockSize => blockSize;
    /// <summary>Bytes of device memory held in blocks.</summary>
    public ulong TotalAllocatedBytes { get { lock (gate) return totalAllocated; } }
    /// <summary>Bytes handed out to allocations.</summary>
    public ulong TotalUsedBytes { get { lock (gate) return totalUsed; } }
    public int BlockCount { get { lock (gate) return blockCount; } }
    public int AllocationCount { get { lock (gate) return allocationCount; } }
    /// <summary>Buffers and images still alive (destroyed on dispose).</summary>
    public int LiveResourceCount { get { lock (gate) return liveBuffers.Count + liveImages.Count; } }

    /// <summary>
    /// The live resources by owner, largest first: buffers and images grouped by their name with digits and what follows a digit removed
    /// ("gl texture 12" is "gl texture"), with the count, the bytes of their allocations and how many of those bytes are device-local.
    /// </summary>
    public List<(string Name, int Count, ulong Bytes, ulong DeviceLocal)> Breakdown()
    {
        var groups = new Dictionary<string, (int Count, ulong Bytes, ulong DeviceLocal)>();
        void Add(string name, ulong bytes, MemoryKind kind)
        {
            int digit = name.AsSpan().IndexOfAnyInRange('0', '9');
            string key = (digit >= 0 ? name[..digit] : name).TrimEnd(' ', '#', ':', '(');
            if (key.Length == 0) key = "(unnamed)";
            var g = groups.GetValueOrDefault(key);
            groups[key] = (g.Count + 1, g.Bytes + bytes, g.DeviceLocal + (kind == MemoryKind.DeviceLocal ? bytes : 0));
        }
        lock (gate)
        {
            foreach (var b in liveBuffers) Add(b.Name, b.Allocation.Size, b.Kind);
            foreach (var i in liveImages) Add(i.Name, i.Allocation.Size, i.Kind);
        }
        return groups.Select(g => (g.Key, g.Value.Count, g.Value.Bytes, g.Value.DeviceLocal)).OrderByDescending(g => g.Bytes).ToList();
    }

    public List<BlockInfo> GetBlocks()
    {
        lock (gate)
        {
            var list = new List<BlockInfo>();
            foreach (var (key, blocks) in pools)
            {
                foreach (var b in blocks)
                {
                    list.Add(new BlockInfo(key.Type, key.Optimal, b.Dedicated, b.Size, b.Used, b.Free.Count));
                }
            }
            return list;
        }
    }

    // ---- memory type choice --------------------------------------------------------------------------------

    bool IsRebarType(uint i)
    {
        var t = memProps.MemoryTypes[(int)i];
        const MemoryPropertyFlags f = MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
        return (t.PropertyFlags & f) == f && memProps.MemoryHeaps[(int)t.HeapIndex].Size >= RebarMinHeap;
    }

    List<uint> Candidates(uint typeBits, MemoryKind kind, ulong size)
    {
        var scored = new List<(int Score, uint Index)>();
        for (uint i = 0; i < memProps.MemoryTypeCount; i++)
        {
            if ((typeBits & (1u << (int)i)) == 0)
            {
                continue;
            }
            var f = memProps.MemoryTypes[(int)i].PropertyFlags;
            bool local = (f & MemoryPropertyFlags.DeviceLocalBit) != 0;
            bool visible = (f & MemoryPropertyFlags.HostVisibleBit) != 0;
            bool coherent = (f & MemoryPropertyFlags.HostCoherentBit) != 0;
            bool cached = (f & MemoryPropertyFlags.HostCachedBit) != 0;
            if ((f & (MemoryPropertyFlags.LazilyAllocatedBit | MemoryPropertyFlags.ProtectedBit)) != 0)
            {
                continue;
            }
            int score;
            switch (kind)
            {
                case MemoryKind.DeviceLocal:
                    if (!local) continue;
                    score = visible ? 1 : 10;
                    break;
                case MemoryKind.Upload:
                    if (!visible || !coherent) continue;
                    score = local ? 1 : 10;
                    if (local && size <= RebarMaxRequest && IsRebarType(i)) score = 20;
                    break;
                default:
                    if (!visible) continue;
                    score = cached ? 10 + (coherent ? 5 : 0) + (local ? 0 : 3) : 1;
                    break;
            }
            scored.Add((score, i));
        }
        scored.Sort((a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score) : a.Index.CompareTo(b.Index));
        return scored.ConvertAll(s => s.Index);
    }

    // ---- allocation ----------------------------------------------------------------------------------------

    /// <summary>Allocates memory for a resource. <paramref name="optimal"/>: optimal-tiling image (else buffer / linear image).</summary>
    public Allocation Allocate(in MemoryRequirements req, MemoryKind kind, bool optimal)
    {
        var candidates = Candidates(req.MemoryTypeBits, kind, req.Size);
        if (candidates.Count == 0)
        {
            throw new VulkanException($"No memory type for {kind} (bits {req.MemoryTypeBits:X})");
        }
        VulkanException? last = null;
        lock (gate)
        {
            foreach (var type in candidates)
            {
                try
                {
                    return AllocateFrom(type, req.Size, Math.Max(req.Alignment, 1), optimal);
                }
                catch (VulkanException e) when (e.Result is Result.ErrorOutOfDeviceMemory or Result.ErrorOutOfHostMemory)
                {
                    last = e;
                }
            }
        }
        throw last ?? new VulkanException("Allocation failed");
    }

    Allocation AllocateFrom(uint type, ulong size, ulong alignment, bool optimal)
    {
        if (!pools.TryGetValue((type, optimal), out var blocks))
        {
            pools[(type, optimal)] = blocks = new List<MemoryBlock>();
        }
        if (size > blockSize / 2)
        {
            var block = NewBlock(type, optimal, size, true);
            blocks.Add(block);
            return Carve(block, 0, 0, size);
        }
        MemoryBlock? bestBlock = null;
        int bestIndex = -1;
        ulong bestWaste = ulong.MaxValue;
        foreach (var b in blocks)
        {
            if (b.Dedicated)
            {
                continue;
            }
            for (int i = 0; i < b.Free.Count; i++)
            {
                var (off, len) = b.Free[i];
                ulong aligned = AlignUp(off, alignment);
                if (aligned + size <= off + len)
                {
                    ulong waste = len - size;
                    if (waste < bestWaste)
                    {
                        bestWaste = waste;
                        bestBlock = b;
                        bestIndex = i;
                    }
                }
            }
        }
        if (bestBlock == null)
        {
            bestBlock = NewBlock(type, optimal, blockSize, false);
            blocks.Add(bestBlock);
            bestIndex = 0;
        }
        return Carve(bestBlock, bestIndex, AlignUp(bestBlock.Dedicated ? 0 : bestBlock.Free[bestIndex].Offset, alignment), size);
    }

    Allocation Carve(MemoryBlock block, int freeIndex, ulong offset, ulong size)
    {
        if (block.Dedicated)
        {
            block.Free.Clear();
        }
        else
        {
            var (fo, fl) = block.Free[freeIndex];
            block.Free.RemoveAt(freeIndex);
            ulong end = offset + size;
            int at = freeIndex;
            if (offset > fo)
            {
                block.Free.Insert(at++, (fo, offset - fo));
            }
            if (end < fo + fl)
            {
                block.Free.Insert(at, (end, fo + fl - end));
            }
        }
        block.Used += size;
        totalUsed += size;
        allocationCount++;
        return new Allocation
        {
            Block = block,
            Offset = offset,
            Size = size,
            MappedPointer = block.Mapped == 0 ? 0 : block.Mapped + (nint)offset,
        };
    }

    MemoryBlock NewBlock(uint type, bool optimal, ulong size, bool dedicated)
    {
        var info = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = size, MemoryTypeIndex = type };
        var r = vk.AllocateMemory(dev.Device, in info, null, out var mem);
        if (r != Result.Success)
        {
            throw new VulkanException($"vkAllocateMemory({size} bytes, type {type}) failed: {r}", r);
        }
        var flags = memProps.MemoryTypes[(int)type].PropertyFlags;
        var block = new MemoryBlock
        {
            Memory = mem,
            Size = size,
            MemoryTypeIndex = type,
            Optimal = optimal,
            Dedicated = dedicated,
            Coherent = (flags & MemoryPropertyFlags.HostCoherentBit) != 0,
        };
        if ((flags & MemoryPropertyFlags.HostVisibleBit) != 0)
        {
            void* p;
            var mr = vk.MapMemory(dev.Device, mem, 0, Vk.WholeSize, 0, &p);
            if (mr != Result.Success)
            {
                vk.FreeMemory(dev.Device, mem, null);
                throw new VulkanException($"vkMapMemory failed: {mr}", mr);
            }
            block.Mapped = (IntPtr)p;
        }
        if (!dedicated)
        {
            block.Free.Add((0, size));
        }
        totalAllocated += size;
        blockCount++;
        return block;
    }

    /// <summary>Returns the range to its block; empty blocks beyond one per pool (and dedicated ones) go back to the driver.</summary>
    public void Free(Allocation? a)
    {
        if (a == null)
        {
            return;
        }
        lock (gate)
        {
            if (a.Freed)
            {
                return;
            }
            a.Freed = true;
            var b = a.Block;
            b.Used -= a.Size;
            totalUsed -= a.Size;
            allocationCount--;
            var blocks = pools[(b.MemoryTypeIndex, b.Optimal)];
            if (b.Dedicated)
            {
                ReleaseBlock(blocks, b);
                return;
            }
            InsertFree(b, a.Offset, a.Size);
            if (b.Used == 0)
            {
                bool otherEmpty = false;
                foreach (var o in blocks)
                {
                    if (o != b && !o.Dedicated && o.Used == 0)
                    {
                        otherEmpty = true;
                        break;
                    }
                }
                if (otherEmpty)
                {
                    ReleaseBlock(blocks, b);
                }
            }
        }
    }

    static void InsertFree(MemoryBlock b, ulong offset, ulong size)
    {
        var f = b.Free;
        int i = 0;
        while (i < f.Count && f[i].Offset < offset)
        {
            i++;
        }
        f.Insert(i, (offset, size));
        if (i + 1 < f.Count && f[i].Offset + f[i].Size == f[i + 1].Offset)
        {
            f[i] = (f[i].Offset, f[i].Size + f[i + 1].Size);
            f.RemoveAt(i + 1);
        }
        if (i > 0 && f[i - 1].Offset + f[i - 1].Size == f[i].Offset)
        {
            f[i - 1] = (f[i - 1].Offset, f[i - 1].Size + f[i].Size);
            f.RemoveAt(i);
        }
    }

    void ReleaseBlock(List<MemoryBlock> blocks, MemoryBlock b)
    {
        blocks.Remove(b);
        // Unmapping is implicit in vkFreeMemory.
        vk.FreeMemory(dev.Device, b.Memory, null);
        totalAllocated -= b.Size;
        blockCount--;
    }

    static ulong AlignUp(ulong v, ulong a) => (v + a - 1) / a * a;

    /// <summary>Makes host writes visible to the GPU (a no-op on coherent memory).</summary>
    public void Flush(Allocation a, ulong offset = 0, ulong size = Vk.WholeSize) => Range(a, offset, size, false);

    /// <summary>Makes GPU writes visible to the host (a no-op on coherent memory).</summary>
    public void Invalidate(Allocation a, ulong offset = 0, ulong size = Vk.WholeSize) => Range(a, offset, size, true);

    void Range(Allocation a, ulong offset, ulong size, bool invalidate)
    {
        if (a.Block.Coherent || a.Block.Mapped == 0)
        {
            return;
        }
        ulong atom = Math.Max(dev.Properties.Limits.NonCoherentAtomSize, 1);
        ulong start = a.Offset + offset;
        ulong len = size == Vk.WholeSize ? a.Size - offset : size;
        ulong alignedStart = start / atom * atom;
        ulong end = Math.Min(AlignUp(start + len, atom), a.Block.Size);
        var range = new MappedMemoryRange
        {
            SType = StructureType.MappedMemoryRange,
            Memory = a.Memory,
            Offset = alignedStart,
            Size = end - alignedStart,
        };
        if (invalidate)
        {
            vk.InvalidateMappedMemoryRanges(dev.Device, 1, in range);
        }
        else
        {
            vk.FlushMappedMemoryRanges(dev.Device, 1, in range);
        }
    }

    // ---- buffers and images --------------------------------------------------------------------------------

    public GpuBuffer CreateBuffer(ulong size, BufferUsageFlags usage, MemoryKind kind, string? name = null)
    {
        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        VulkanException.Check(vk.CreateBuffer(dev.Device, in info, null, out var buffer), "vkCreateBuffer");
        vk.GetBufferMemoryRequirements(dev.Device, buffer, out var req);
        Allocation alloc;
        try
        {
            alloc = Allocate(in req, kind, false);
        }
        catch
        {
            vk.DestroyBuffer(dev.Device, buffer, null);
            throw;
        }
        var r = vk.BindBufferMemory(dev.Device, buffer, alloc.Memory, alloc.Offset);
        if (r != Result.Success)
        {
            vk.DestroyBuffer(dev.Device, buffer, null);
            Free(alloc);
            throw new VulkanException($"vkBindBufferMemory failed: {r}", r);
        }
        var gb = new GpuBuffer { Buffer = buffer, Allocation = alloc, Size = size, Usage = usage, Name = name ?? "", Kind = kind };
        lock (gate)
        {
            liveBuffers.Add(gb);
        }
        if (name != null)
        {
            dev.SetName(ObjectType.Buffer, buffer.Handle, name);
        }
        return gb;
    }

    public GpuImage CreateImage(in ImageCreateInfo info, MemoryKind kind = MemoryKind.DeviceLocal, string? name = null)
    {
        VulkanException.Check(vk.CreateImage(dev.Device, in info, null, out var image), "vkCreateImage");
        vk.GetImageMemoryRequirements(dev.Device, image, out var req);
        Allocation alloc;
        try
        {
            alloc = Allocate(in req, kind, info.Tiling != ImageTiling.Linear);
        }
        catch
        {
            vk.DestroyImage(dev.Device, image, null);
            throw;
        }
        var r = vk.BindImageMemory(dev.Device, image, alloc.Memory, alloc.Offset);
        if (r != Result.Success)
        {
            vk.DestroyImage(dev.Device, image, null);
            Free(alloc);
            throw new VulkanException($"vkBindImageMemory failed: {r}", r);
        }
        var gi = new GpuImage
        {
            Image = image,
            Allocation = alloc,
            Format = info.Format,
            Extent = info.Extent,
            MipLevels = info.MipLevels,
            ArrayLayers = info.ArrayLayers,
            Samples = info.Samples,
            Usage = info.Usage,
            Name = name ?? "",
            Kind = kind,
        };
        lock (gate)
        {
            liveImages.Add(gi);
        }
        if (name != null)
        {
            dev.SetName(ObjectType.Image, image.Handle, name);
        }
        return gi;
    }

    /// <summary>Destroys the buffer and frees its memory now (use <see cref="VulkanDevice.DeferFree(GpuBuffer)"/> when the GPU may still use it).</summary>
    public void Free(GpuBuffer? b)
    {
        if (b == null)
        {
            return;
        }
        lock (gate)
        {
            if (b.Freed)
            {
                return;
            }
            b.Freed = true;
            liveBuffers.Remove(b);
        }
        vk.DestroyBuffer(dev.Device, b.Buffer, null);
        Free(b.Allocation);
    }

    public void Free(GpuImage? i)
    {
        if (i == null)
        {
            return;
        }
        lock (gate)
        {
            if (i.Freed)
            {
                return;
            }
            i.Freed = true;
            liveImages.Remove(i);
        }
        vk.DestroyImage(dev.Device, i.Image, null);
        Free(i.Allocation);
    }

    /// <summary>Destroys what is still alive and releases all blocks (the device must be idle).</summary>
    public void Dispose()
    {
        GpuBuffer[] bs;
        GpuImage[] ims;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            bs = liveBuffers.ToArray();
            ims = liveImages.ToArray();
        }
        foreach (var b in bs) Free(b);
        foreach (var i in ims) Free(i);
        lock (gate)
        {
            foreach (var blocks in pools.Values)
            {
                foreach (var b in blocks)
                {
                    vk.FreeMemory(dev.Device, b.Memory, null);
                }
            }
            pools.Clear();
            totalAllocated = 0;
            totalUsed = 0;
            blockCount = 0;
        }
    }
}
