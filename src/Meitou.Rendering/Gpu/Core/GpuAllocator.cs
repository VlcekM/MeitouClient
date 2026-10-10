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

    // A pool that keeps needing new blocks gets one made ahead on a worker (a spare), and blocks that empty out are given back to the driver
    // on a worker: vkAllocateMemory of 64 MB takes 0.3 to 1 ms and sometimes 10 to 50, vkFreeMemory 0.5 to 5 and sometimes 30, and neither
    // belongs on the render thread (2026-10-10, docs/renderer-native.md "Upload steps").
    readonly Dictionary<(uint Type, bool Optimal), MemoryBlock> spares = new();
    readonly HashSet<(uint Type, bool Optimal)> sparing = new();
    readonly Dictionary<(uint Type, bool Optimal), int> misses = new();
    /// <summary>Spares being made and blocks being given back on workers.</summary>
    int workersBusy;
    /// <summary>A pool makes spares once it has needed this many new blocks (a pool that never grows keeps none).</summary>
    const int SpareAfterMisses = 2;

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
    /// <summary>Blocks made ahead on a worker for pools that keep growing (counted in <see cref="BlockCount"/> and <see cref="TotalAllocatedBytes"/>, not in <see cref="GetBlocks"/>).</summary>
    public int SpareBlocks { get { lock (gate) return spares.Count; } }
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
        for (int attempt = 0; attempt < 2; attempt++)
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
            // Out of memory: the spares are only a convenience, give them up and try once more.
            if (!ReleaseSpares()) break;
        }
        throw last ?? new VulkanException("Allocation failed");
    }

    /// <summary>
    /// Whether a buffer of <paramref name="size"/> bytes in device-local memory would be carved from free space in blocks the driver already
    /// holds (so it takes nothing from the video memory budget): it is not a dedicated allocation and some device-local buffer block has
    /// a free range of that size plus 64 KB for alignment.
    /// </summary>
    public bool FitsInFreeSpace(ulong size)
    {
        if (size > blockSize / 2) return false;
        lock (gate)
        {
            foreach (var ((type, optimal), blocks) in pools)
            {
                if (optimal || (memProps.MemoryTypes[(int)type].PropertyFlags & MemoryPropertyFlags.DeviceLocalBit) == 0) continue;
                foreach (var b in blocks)
                {
                    if (b.Dedicated) continue;
                    foreach (var (_, len) in b.Free) if (len >= size + 65536) return true;
                }
            }
        }
        return false;
    }

    Allocation AllocateFrom(uint type, ulong size, ulong alignment, bool optimal)
    {
        var key = (type, optimal);
        bool dedicated = size > blockSize / 2;
        MemoryBlock? fresh = null;
        while (true)
        {
            lock (gate)
            {
                if (!pools.TryGetValue(key, out var blocks))
                {
                    pools[key] = blocks = new List<MemoryBlock>();
                }
                if (fresh is not null)
                {
                    // The block made outside the lock (a dedicated one holds just this allocation).
                    blocks.Add(fresh);
                    totalAllocated += fresh.Size;
                    blockCount++;
                    var made = Carve(fresh, 0, 0, size);
                    if (!dedicated && misses.GetValueOrDefault(key) >= SpareAfterMisses) MaybeStartSpare(key);
                    return made;
                }
                if (!dedicated)
                {
                    if (Fit(blocks, size, alignment) is { } f)
                    {
                        return Carve(f.Block, f.Index, AlignUp(f.Block.Free[f.Index].Offset, alignment), size);
                    }
                    // A spare made ahead is the next block (its bytes were counted when it was made).
                    if (spares.Remove(key, out var spare))
                    {
                        blocks.Add(spare);
                        var made = Carve(spare, 0, 0, size);
                        MaybeStartSpare(key);
                        return made;
                    }
                    misses[key] = misses.GetValueOrDefault(key) + 1;
                }
            }
            // No room: a new block, made outside the lock so a slow vkAllocateMemory does not hold up the other threads' allocations.
            fresh = CreateBlock(type, optimal, dedicated ? size : blockSize, dedicated);
        }
    }

    /// <summary>The best fit (smallest waste) among the pool's regular blocks; null when none has room.</summary>
    static (MemoryBlock Block, int Index)? Fit(List<MemoryBlock> blocks, ulong size, ulong alignment)
    {
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
        return bestBlock is null ? null : (bestBlock, bestIndex);
    }

    /// <summary>Under the lock: makes the pool's next block on a worker unless there is one or it is being made.</summary>
    void MaybeStartSpare((uint Type, bool Optimal) key)
    {
        if (disposed || spares.ContainsKey(key) || !sparing.Add(key)) return;
        Interlocked.Increment(ref workersBusy);
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            MemoryBlock? block = null;
            try { block = CreateBlock(key.Type, key.Optimal, blockSize, false); }
            catch (VulkanException) { }   // out of memory: no spare, the pool asks again at its next miss
            bool keep = false;
            lock (gate)
            {
                sparing.Remove(key);
                if (block is not null && !disposed)
                {
                    spares[key] = block;
                    totalAllocated += block.Size;
                    blockCount++;
                    keep = true;
                }
            }
            if (!keep && block is not null) vk.FreeMemory(dev.Device, block.Memory, null);
            Interlocked.Decrement(ref workersBusy);
        }, null);
    }

    /// <summary>Gives the spares back (out of memory). False when there were none.</summary>
    bool ReleaseSpares()
    {
        List<MemoryBlock> mine;
        lock (gate)
        {
            if (spares.Count == 0) return false;
            mine = [.. spares.Values];
            spares.Clear();
            foreach (var b in mine) { totalAllocated -= b.Size; blockCount--; }
        }
        foreach (var b in mine) vk.FreeMemory(dev.Device, b.Memory, null);
        return true;
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

    /// <summary>Allocates and maps a block (no bookkeeping, no lock: the caller counts it once it is in a pool). Any thread.</summary>
    MemoryBlock CreateBlock(uint type, bool optimal, ulong size, bool dedicated)
    {
        long profileStart = UploadProfile.Begin();
        // With ray queries every block may hold acceleration structures, their build inputs or scratch, which are reached by device address.
        var addressFlags = new MemoryAllocateFlagsInfo { SType = StructureType.MemoryAllocateFlagsInfo, Flags = MemoryAllocateFlags.DeviceAddressBit };
        var info = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = size, MemoryTypeIndex = type, PNext = dev.HasRayQuery && !optimal ? &addressFlags : null };
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
        UploadProfile.EndBlock(true, profileStart, size, type, optimal, dedicated);
        return block;
    }

    /// <summary>Returns the range to its block; empty blocks beyond one per pool (and dedicated ones) go back to the driver.</summary>
    public void Free(Allocation? a)
    {
        if (a == null)
        {
            return;
        }
        MemoryBlock? release = null;
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
                release = b;
            }
            else
            {
                InsertFree(b, a.Offset, a.Size);
                if (b.Used == 0)
                {
                    // Another empty block in the pool, or a spare made ahead, is the room to grow into: this one goes back.
                    bool otherEmpty = spares.ContainsKey((b.MemoryTypeIndex, b.Optimal));
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
                        release = b;
                    }
                }
            }
        }
        if (release is not null) GiveBack(release);
    }

    /// <summary>
    /// A block that left its pool goes back to the driver on a worker (vkFreeMemory takes 0.5 to 5 ms and sometimes 30; the render thread
    /// frees most blocks, when a frame's deletions run). Inline once the allocator is being disposed.
    /// </summary>
    void GiveBack(MemoryBlock b)
    {
        if (disposed)
        {
            vk.FreeMemory(dev.Device, b.Memory, null);
            return;
        }
        Interlocked.Increment(ref workersBusy);
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            long start = UploadProfile.Begin();
            vk.FreeMemory(dev.Device, b.Memory, null);   // unmapping is implicit
            UploadProfile.EndBlock(false, start, b.Size, b.MemoryTypeIndex, b.Optimal, b.Dedicated);
            Interlocked.Decrement(ref workersBusy);
        }, null);
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

    /// <summary>Under the lock: takes the block out of its pool and the totals; the caller gives it back to the driver (<see cref="GiveBack"/>) outside the lock.</summary>
    void ReleaseBlock(List<MemoryBlock> blocks, MemoryBlock b)
    {
        blocks.Remove(b);
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
        long profileStart = UploadProfile.Begin();
        try { return CreateBufferCore(size, usage, kind, name); }
        finally { UploadProfile.End(UploadProfile.Part.CreateBuffer, profileStart); }
    }

    GpuBuffer CreateBufferCore(ulong size, BufferUsageFlags usage, MemoryKind kind, string? name)
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
        long profileStart = UploadProfile.Begin();
        try { return CreateImageCore(in info, kind, name); }
        finally { UploadProfile.End(UploadProfile.Part.CreateImage, profileStart); }
    }

    GpuImage CreateImageCore(in ImageCreateInfo info, MemoryKind kind, string? name)
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
        long profileStart = UploadProfile.Begin();
        vk.DestroyBuffer(dev.Device, b.Buffer, null);
        Free(b.Allocation);
        UploadProfile.End(UploadProfile.Part.FreeBuffer, profileStart);
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
        long profileStart = UploadProfile.Begin();
        vk.DestroyImage(dev.Device, i.Image, null);
        Free(i.Allocation);
        UploadProfile.End(UploadProfile.Part.FreeImage, profileStart);
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
        // Workers making spares or giving blocks back finish before the rest goes (a spare made after disposed was set frees itself).
        var wait = new SpinWait();
        while (Volatile.Read(ref workersBusy) > 0) wait.SpinOnce();
        lock (gate)
        {
            foreach (var blocks in pools.Values)
            {
                foreach (var b in blocks)
                {
                    vk.FreeMemory(dev.Device, b.Memory, null);
                }
            }
            foreach (var b in spares.Values) vk.FreeMemory(dev.Device, b.Memory, null);
            spares.Clear();
            pools.Clear();
            totalAllocated = 0;
            totalUsed = 0;
            blockCount = 0;
        }
    }
}
