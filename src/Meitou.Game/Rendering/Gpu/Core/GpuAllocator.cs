using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gpu.Core;

/// <summary>
/// What memory an allocation needs: device-local, CPU-written uploads or CPU-read readbacks (<see cref="GpuAllocator.Allocate"/> picks the memory
/// type).
/// </summary>
public enum MemoryKind
{
    /// <summary>
    /// Device-local memory (not host visible when a type without that exists).
    /// </summary>
    DeviceLocal,
    /// <summary>
    /// Host visible + coherent, persistently mapped. Small requests go to resizable-BAR memory when it exists.
    /// </summary>
    Upload,
    /// <summary>
    /// Host visible + cached (CPU reads are fast); flush/invalidate when not coherent.
    /// </summary>
    Readback,
}

/// <summary>
/// A range of a memory block. Free it through <see cref="GpuAllocator.Free(Allocation)"/>.
/// </summary>
public sealed class Allocation
{
    /// <summary>
    /// The block the range is carved from.
    /// </summary>
    internal MemoryBlock Block = null!;
    /// <summary>
    /// Set by the first free; a second free does nothing.
    /// </summary>
    internal bool Freed;

    /// <summary>
    /// The block's device memory.
    /// </summary>
    public DeviceMemory Memory => Block.Memory;
    /// <summary>
    /// The range's offset in the block's memory, in bytes.
    /// </summary>
    public ulong Offset { get; internal set; }
    /// <summary>
    /// The range's size in bytes.
    /// </summary>
    public ulong Size { get; internal set; }
    /// <summary>
    /// The start of this range in host memory (non-zero for host-visible memory).
    /// </summary>
    public IntPtr MappedPointer { get; internal set; }
    /// <summary>
    /// The Vulkan memory type of the block.
    /// </summary>
    public uint MemoryTypeIndex => Block.MemoryTypeIndex;
    /// <summary>
    /// Whether the block's memory is host coherent (no flush or invalidate needed).
    /// </summary>
    public bool IsHostCoherent => Block.Coherent;
}

/// <summary>
/// A device memory allocation the allocator carves ranges from: a pooled block or a dedicated one holding one allocation.
/// </summary>
internal sealed class MemoryBlock
{
    /// <summary>
    /// The device memory.
    /// </summary>
    public DeviceMemory Memory;
    /// <summary>
    /// The block's size in bytes.
    /// </summary>
    public ulong Size;
    /// <summary>
    /// The persistently mapped host address; 0 for memory that is not host visible.
    /// </summary>
    public IntPtr Mapped;
    /// <summary>
    /// The Vulkan memory type.
    /// </summary>
    public uint MemoryTypeIndex;
    /// <summary>
    /// Holds optimal-tiling images (else buffers and linear images), kept apart for bufferImageGranularity.
    /// </summary>
    public bool Optimal;
    /// <summary>
    /// The size of every allocation in it, for a block of streamed textures (<see cref="PoolKey.Slot"/>); 0 for the others.
    /// </summary>
    public ulong Slot;
    /// <summary>
    /// Holds one allocation of its own (above half a block, or a streamed texture with memory of its own).
    /// </summary>
    public bool Dedicated;
    /// <summary>
    /// Whether the memory is host coherent.
    /// </summary>
    public bool Coherent;
    /// <summary>
    /// Bytes carved out of the block.
    /// </summary>
    public ulong Used;
    /// <summary>
    /// The free ranges, sorted by offset, neighbours coalesced (<see cref="GpuAllocator.InsertFree"/>).
    /// </summary>
    public readonly List<(ulong Offset, ulong Size)> Free = new();
}

/// <summary>
/// A pool of blocks: the memory type, images (optimal tiling) or buffers, and for streamed textures (which come and go as the view moves) the one
/// size every allocation in it has. A freed slot fits the next texture of that size exactly, so streamed textures leave no odd holes; and they are
/// kept apart from the long-lived images (render targets), which would pin their blocks.
/// </summary>
readonly record struct PoolKey(uint Type, bool Optimal, ulong Slot);

/// <summary>
/// A memory block as seen by tests and diagnostics.
/// </summary>
public readonly record struct BlockInfo(uint MemoryTypeIndex, bool Optimal, bool Dedicated, ulong Size, ulong Used, int FreeRanges);

/// <summary>
/// A buffer bound to memory from the allocator. Free it through <see cref="GpuAllocator.Free(BufferAllocation)"/>.
/// </summary>
public sealed class BufferAllocation
{
    /// <summary>
    /// Set by the first free; a second free does nothing.
    /// </summary>
    internal bool Freed;
    /// <summary>
    /// The Vulkan buffer.
    /// </summary>
    public Silk.NET.Vulkan.Buffer Buffer { get; internal set; }
    /// <summary>
    /// The memory range the buffer is bound to.
    /// </summary>
    public Allocation Allocation { get; internal set; } = null!;
    /// <summary>
    /// The buffer's size in bytes as asked for.
    /// </summary>
    public ulong Size { get; internal set; }
    /// <summary>
    /// The usage flags the buffer was made with.
    /// </summary>
    public BufferUsageFlags Usage { get; internal set; }
    /// <summary>
    /// The name given at creation (diagnostics: <see cref="GpuAllocator.Breakdown"/>).
    /// </summary>
    public string Name { get; internal set; } = "";
    /// <summary>
    /// The kind of memory the buffer was asked with.
    /// </summary>
    public MemoryKind Kind { get; internal set; }
    /// <summary>
    /// Host pointer to the start of the buffer (null for device-local memory).
    /// </summary>
    public unsafe void* Mapped => (void*)Allocation.MappedPointer;
    /// <summary>
    /// The index into the device's buffers: the handle the renderers hold.
    /// </summary>
    public int Index { get; internal set; }
    /// <summary>
    /// The handle the renderers hold (the backend's identity and the size).
    /// </summary>
    public GpuBuffer Handle => new(Index, Size);
}

/// <summary>
/// An image bound to memory from the allocator. Free it through <see cref="GpuAllocator.Free(ImageAllocation)"/>.
/// </summary>
public sealed class ImageAllocation
{
    /// <summary>
    /// Set by the first free; a second free does nothing.
    /// </summary>
    internal bool Freed;
    /// <summary>
    /// The Vulkan image.
    /// </summary>
    public Image Image { get; internal set; }
    /// <summary>
    /// The memory range the image is bound to.
    /// </summary>
    public Allocation Allocation { get; internal set; } = null!;
    /// <summary>
    /// The image's format.
    /// </summary>
    public Format Format { get; internal set; }
    /// <summary>
    /// The image's size in texels.
    /// </summary>
    public Extent3D Extent { get; internal set; }
    /// <summary>
    /// The image's mip levels.
    /// </summary>
    public uint MipLevels { get; internal set; }
    /// <summary>
    /// The image's array layers.
    /// </summary>
    public uint ArrayLayers { get; internal set; }
    /// <summary>
    /// The image's sample count.
    /// </summary>
    public SampleCountFlags Samples { get; internal set; }
    /// <summary>
    /// The usage flags the image was made with.
    /// </summary>
    public ImageUsageFlags Usage { get; internal set; }
    /// <summary>
    /// The name given at creation (diagnostics: <see cref="GpuAllocator.Breakdown"/>).
    /// </summary>
    public string Name { get; internal set; } = "";
    /// <summary>
    /// The kind of memory the image was asked with.
    /// </summary>
    public MemoryKind Kind { get; internal set; }
}

/// <summary>
/// Suballocates device memory: per memory type, blocks (default 64 MB) with a sorted free list (best fit, alignment
/// respected, neighbours coalesced on free). Buffers/linear images and optimal images live in separate blocks, which
/// satisfies bufferImageGranularity. A request above half a block gets a dedicated block. Thread-safe.
/// </summary>
public sealed unsafe class GpuAllocator : IDisposable
{
    /// <summary>
    /// The default block size: 64 MB.
    /// </summary>
    public const ulong DefaultBlockSize = 64UL * 1024 * 1024;
    /// <summary>
    /// The smallest device-local host-visible heap counted as resizable BAR (1 GB).
    /// </summary>
    const ulong RebarMinHeap = 1UL << 30;
    /// <summary>
    /// The largest upload request put in resizable-BAR memory (16 MB).
    /// </summary>
    const ulong RebarMaxRequest = 16UL << 20;

    /// <summary>
    /// The device the memory is allocated on.
    /// </summary>
    readonly VulkanDevice _dev;
    /// <summary>
    /// The Vulkan API entry points.
    /// </summary>
    readonly Vk _vk;
    /// <summary>
    /// The size of a pooled block in bytes.
    /// </summary>
    readonly ulong _blockSize;
    /// <summary>
    /// Guards the pools, the totals and the live sets.
    /// </summary>
    readonly object _gate = new();
    /// <summary>
    /// The blocks of each pool.
    /// </summary>
    readonly Dictionary<PoolKey, List<MemoryBlock>> _pools = new();
    /// <summary>
    /// The buffers not yet freed (diagnostics and teardown).
    /// </summary>
    readonly HashSet<BufferAllocation> _liveBuffers = new();
    /// <summary>
    /// The images not yet freed (diagnostics and teardown).
    /// </summary>
    readonly HashSet<ImageAllocation> _liveImages = new();
    /// <summary>
    /// The physical device's memory types and heaps.
    /// </summary>
    PhysicalDeviceMemoryProperties _memProps;
    /// <summary>
    /// Bytes of device memory held in blocks.
    /// </summary>
    ulong _totalAllocated;
    /// <summary>
    /// Bytes carved out of blocks.
    /// </summary>
    ulong _totalUsed;
    /// <summary>
    /// Blocks held.
    /// </summary>
    int _blockCount;
    /// <summary>
    /// Allocations not yet freed.
    /// </summary>
    int _allocationCount;
    /// <summary>
    /// Set by the first <see cref="Dispose"/>.
    /// </summary>
    bool _disposed;
    /// <summary>
    /// Backs <see cref="Lean"/>.
    /// </summary>
    volatile bool _lean;

    /// <summary>
    /// The spare block made ahead for each pool. A pool that keeps needing new blocks gets one made ahead on a worker (a spare), and blocks that
    /// empty out are given back to the driver on a worker: vkAllocateMemory of 64 MB takes 0.3 to 1 ms and sometimes 10 to 50, vkFreeMemory 0.5 to 5
    /// and sometimes 30, and neither belongs on the render thread (2026-10-10, docs/renderer-native.md "Upload steps").
    /// </summary>
    readonly Dictionary<PoolKey, MemoryBlock> _spares = new();
    /// <summary>
    /// The pools whose spare is being made on a worker now.
    /// </summary>
    readonly HashSet<PoolKey> _sparing = new();
    /// <summary>
    /// How many new blocks each pool has needed (spares start at <see cref="SpareAfterMisses"/>).
    /// </summary>
    readonly Dictionary<PoolKey, int> _misses = new();
    /// <summary>
    /// Spares being made and blocks being given back on workers.
    /// </summary>
    int _workersBusy;
    /// <summary>
    /// A pool makes spares once it has needed this many new blocks (a pool that never grows keeps none).
    /// </summary>
    const int SpareAfterMisses = 2;

    /// <summary>
    /// An allocator on the device's physical memory properties, with blocks of <paramref name="blockSize"/> bytes.
    /// </summary>
    internal GpuAllocator(VulkanDevice dev, ulong blockSize = DefaultBlockSize)
    {
        this._dev = dev;
        _vk = dev.Vk;
        this._blockSize = blockSize;
        _vk.GetPhysicalDeviceMemoryProperties(dev.PhysicalDevice, out _memProps);
    }

    /// <summary>
    /// The size of a pooled block in bytes.
    /// </summary>
    public ulong BlockSize => _blockSize;
    /// <summary>
    /// Bytes of device memory held in blocks.
    /// </summary>
    public ulong TotalAllocatedBytes { get { lock (_gate) return _totalAllocated; } }
    /// <summary>
    /// Bytes handed out to allocations.
    /// </summary>
    public ulong TotalUsedBytes { get { lock (_gate) return _totalUsed; } }
    /// <summary>
    /// Blocks held.
    /// </summary>
    public int BlockCount { get { lock (_gate) return _blockCount; } }
    /// <summary>
    /// Allocations not yet freed.
    /// </summary>
    public int AllocationCount { get { lock (_gate) return _allocationCount; } }
    /// <summary>
    /// Blocks made ahead on a worker for pools that keep growing (counted in <see cref="BlockCount"/> and <see cref="TotalAllocatedBytes"/>, not in
    /// <see cref="GetBlocks"/>).
    /// </summary>
    public int SpareBlocks { get { lock (_gate) return _spares.Count; } }
    /// <summary>
    /// Buffers and images still alive (destroyed on dispose).
    /// </summary>
    public int LiveResourceCount { get { lock (_gate) return _liveBuffers.Count + _liveImages.Count; } }

    /// <summary>
    /// The live resources by owner, largest first: buffers and images grouped by <see cref="Owner"/>, with the count, the bytes of their
    /// allocations and how many of those bytes are device-local.
    /// </summary>
    public List<(string Name, int Count, ulong Bytes, ulong DeviceLocal)> Breakdown()
    {
        var groups = new Dictionary<string, (int Count, ulong Bytes, ulong DeviceLocal)>();

        void Add(string name, ulong bytes, MemoryKind kind)
        {
            string key = Owner(name);
            var g = groups.GetValueOrDefault(key);
            groups[key] = (g.Count + 1, g.Bytes + bytes, g.DeviceLocal + (kind == MemoryKind.DeviceLocal ? bytes : 0));
        }

        lock (_gate)
        {
            foreach (var b in _liveBuffers) Add(b.Name, b.Allocation.Size, b.Kind);
            foreach (var i in _liveImages) Add(i.Name, i.Allocation.Size, i.Kind);
        }

        return groups.Select(g => (g.Key, g.Value.Count, g.Value.Bytes, g.Value.DeviceLocal)).OrderByDescending(g => g.Bytes).ToList();
    }

    /// <summary>
    /// A resource's owner, from its name: what precedes " | " ("object textures | rock.dds" is "object textures"), else the name with its first
    /// digit and what follows removed ("gl texture 12" is "gl texture").
    /// </summary>
    public static string Owner(string name)
    {
        int bar = name.IndexOf(" | ", StringComparison.Ordinal);
        int cut = bar >= 0 ? bar : name.AsSpan().IndexOfAnyInRange('0', '9');
        string key = (cut >= 0 ? name[..cut] : name).TrimEnd(' ', '#', ':', '(');
        return key.Length == 0 ? "(unnamed)" : key;
    }

    /// <summary>
    /// Writes every live resource (largest first: bytes, memory kind, name, and an image's format, extent, levels and layers or a buffer's usage)
    /// and every block (memory type and its flags, size, used, free ranges) to <paramref name="w"/>, for the VRAM dump (Ctrl+Shift+F12 in the game).
    /// </summary>
    public void WriteReport(TextWriter w)
    {
        var rows = new List<(ulong Bytes, string Line)>();
        var blocks = new List<(uint Type, ulong Size, string Line)>();

        lock (_gate)
        {
            foreach (var b in _liveBuffers)
                rows.Add((b.Allocation.Size, $"{Mb(b.Allocation.Size),10:0.000}  {b.Kind,-11}  buffer  {b.Name}  [{b.Size} bytes, {b.Usage}]"));

            foreach (var i in _liveImages)
            {
                rows.Add((i.Allocation.Size, $"{Mb(i.Allocation.Size),10:0.000}  {i.Kind,-11}  image   {i.Name}  "
                    + $"[{i.Format} {i.Extent.Width}x{i.Extent.Height}" + (i.Extent.Depth > 1 ? $"x{i.Extent.Depth}" : "")
                    + $", {i.MipLevels} levels, {i.ArrayLayers} layers" + (i.Samples != SampleCountFlags.Count1Bit ? $", {i.Samples}" : "") + "]"));
            }

            void Block(uint type, MemoryBlock b, bool spare) =>
                blocks.Add((type, b.Size, $"{Mb(b.Size),10:0.000}  {Mb(b.Used),10:0.000}  {b.Free.Count,5}  type {type} "
                    + $"{_memProps.MemoryTypes[(int)type].PropertyFlags}" + (b.Optimal ? " images" : " buffers")
                    + (b.Slot > 0 ? $" streamed, slots of {Mb(b.Slot):0.000} MB" : "") + (b.Dedicated ? " dedicated" : "")
                    + (spare ? " spare" : "")));

            foreach (var (key, list) in _pools) foreach (var b in list) Block(key.Type, b, false);
            foreach (var (key, b) in _spares) Block(key.Type, b, true);
        }

        w.WriteLine($"resources ({rows.Count}, MB  memory  what  name  [details]):");
        foreach (var (_, line) in rows.OrderByDescending(r => r.Bytes)) w.WriteLine(line);

        w.WriteLine();
        w.WriteLine($"blocks ({blocks.Count}, MB size  MB used  free ranges  memory type):");
        foreach (var (_, _, line) in blocks.OrderBy(b => b.Type).ThenByDescending(b => b.Size)) w.WriteLine(line);
    }

    /// <summary>
    /// Bytes to MB (2^20).
    /// </summary>
    static double Mb(ulong bytes) => bytes / 1048576.0;

    /// <summary>
    /// Where the room in our blocks is (device-local blocks only): empty blocks kept to grow into, spares made ahead, and free ranges inside
    /// blocks that also hold allocations (fragmentation: only moving what is in them would give those back), with the count of such blocks.
    /// </summary>
    public (ulong Empty, ulong Spare, ulong Scattered, int PartBlocks) Slack()
    {
        ulong empty = 0, spare = 0, scattered = 0;
        int parts = 0;

        lock (_gate)
        {
            foreach (var ((type, _, _), blocks) in _pools)
            {
                if ((_memProps.MemoryTypes[(int)type].PropertyFlags & MemoryPropertyFlags.DeviceLocalBit) == 0)
                    continue;

                foreach (var b in blocks)
                {
                    if (b.Dedicated) continue;

                    if (b.Used == 0) empty += b.Size;
                    else if (b.Used < b.Size)
                    {
                        scattered += b.Size - b.Used;
                        parts++;
                    }
                }
            }

            foreach (var ((type, _, _), b) in _spares)
            {
                if ((_memProps.MemoryTypes[(int)type].PropertyFlags & MemoryPropertyFlags.DeviceLocalBit) != 0)
                    spare += b.Size;
            }
        }

        return (empty, spare, scattered, parts);
    }

    /// <summary>
    /// Gives every empty block and every spare back to the driver (the VRAM guard under pressure: the room kept to grow into is the first
    /// thing to shed, before anything drawn is evicted). The bytes given back.
    /// </summary>
    public ulong Trim()
    {
        var release = new List<MemoryBlock>();

        lock (_gate)
        {
            if (_disposed) return 0;

            foreach (var blocks in _pools.Values)
            {
                for (int i = blocks.Count - 1; i >= 0; i--)
                {
                    if (!blocks[i].Dedicated && blocks[i].Used == 0)
                    {
                        release.Add(blocks[i]);
                        ReleaseBlock(blocks, blocks[i]);
                    }
                }
            }

            foreach (var b in _spares.Values)
            {
                release.Add(b);
                _totalAllocated -= b.Size;
                _blockCount--;
            }

            _spares.Clear();
        }

        ulong bytes = 0;

        foreach (var b in release)
        {
            bytes += b.Size;
            GiveBack(b);
        }

        return bytes;
    }

    /// <summary>
    /// Lean (the VRAM guard under pressure): no spares are made, a block that empties goes back at once instead of being kept to grow into, and
    /// turning it on gives back what is kept now (<see cref="Trim"/>). Allocation is slower while it lasts (a new block for every growth).
    /// </summary>
    public bool Lean
    {
        get => _lean;
        set
        {
            if (_lean == value) return;

            _lean = value;
            if (value) Trim();
        }
    }

    /// <summary>
    /// The blocks of every pool (spares not included), for tests and diagnostics.
    /// </summary>
    public List<BlockInfo> GetBlocks()
    {
        lock (_gate)
        {
            var list = new List<BlockInfo>();

            foreach (var (key, blocks) in _pools)
            {
                foreach (var b in blocks)
                {
                    list.Add(new BlockInfo(key.Type, key.Optimal, b.Dedicated, b.Size, b.Used, b.Free.Count));
                }
            }

            return list;
        }
    }

    #region Memory type choice

    /// <summary>
    /// A memory type counts as resizable BAR: device-local, host visible and coherent, on a heap of at least <see cref="RebarMinHeap"/>.
    /// </summary>
    bool IsRebarType(uint i)
    {
        var t = _memProps.MemoryTypes[(int)i];
        const MemoryPropertyFlags f = MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
        return (t.PropertyFlags & f) == f && _memProps.MemoryHeaps[(int)t.HeapIndex].Size >= RebarMinHeap;
    }

    /// <summary>
    /// The memory types of <paramref name="typeBits"/> that suit the kind, best first: device-local prefers types not host visible; uploads prefer
    /// device-local coherent memory (resizable BAR for small requests); readbacks prefer cached, then coherent, then system memory.
    /// </summary>
    List<uint> Candidates(uint typeBits, MemoryKind kind, ulong size)
    {
        var scored = new List<(int Score, uint Index)>();

        for (uint i = 0; i < _memProps.MemoryTypeCount; i++)
        {
            if ((typeBits & (1u << (int)i)) == 0)
            {
                continue;
            }

            var f = _memProps.MemoryTypes[(int)i].PropertyFlags;
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

    #endregion

    #region Allocation

    /// <summary>
    /// A streamed texture from this size up gets memory of its own when made on a worker (<see cref="Allocate"/>).
    /// </summary>
    public const ulong OwnMemoryBytes = 4UL << 20;
    /// <summary>
    /// A streamed slot's size is rounded up to this; a block of slots holds about <see cref="SlotBlockBytes"/> of them (one at least).
    /// </summary>
    const ulong SlotGranule = 64UL << 10, SlotBlockBytes = 16UL << 20;
    /// <summary>
    /// Backs <see cref="Failures"/>.
    /// </summary>
    int _failures;

    /// <summary>
    /// Allocates memory for a resource. <paramref name="optimal"/>: optimal-tiling image (else buffer / linear image). <paramref name="streamed"/>: a
    /// streamed texture: from <see cref="OwnMemoryBytes"/> up, when <paramref name="ownMemory"/> (the caller is a worker, where a slow
    /// vkAllocateMemory costs no frame), memory of its own, given back the moment it is freed; else a slot in a pool of its size
    /// (<see cref="PoolKey"/>).
    /// </summary>
    public Allocation Allocate(in MemoryRequirements req, MemoryKind kind, bool optimal, bool streamed = false, bool ownMemory = false)
    {
        var candidates = Candidates(req.MemoryTypeBits, kind, req.Size);

        if (candidates.Count == 0)
        {
            throw new VulkanException($"No memory type for {kind} (bits {req.MemoryTypeBits:X})");
        }

        VulkanException? last = null;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            foreach (var type in candidates)
            {
                try
                {
                    return AllocateFrom(type, req.Size, Math.Max(req.Alignment, 1), optimal, streamed, ownMemory);
                }
                catch (VulkanException e) when (e.Result is Result.ErrorOutOfDeviceMemory or Result.ErrorOutOfHostMemory)
                {
                    last = e;
                }
            }

            // Out of memory: the spares are only a convenience, give them up and try again; then what the finished frames released and the blocks
            // that emptied (on the render thread, Reclaim), once.
            if (!ReleaseSpares() && (attempt == 2 || Reclaim?.Invoke() != true)) break;
        }

        Interlocked.Increment(ref _failures);
        throw last ?? new VulkanException("Allocation failed");
    }

    /// <summary>
    /// Out of memory with no spares left: frees what the submitted frames released and gives back the empty blocks; true when that may have made room
    /// (the allocation is tried again). Set by the device; does nothing off the render thread.
    /// </summary>
    public Func<bool>? Reclaim { get; set; }

    /// <summary>
    /// Allocations the driver refused after every retry (the VRAM guard enters pressure when it sees one).
    /// </summary>
    public int Failures => Volatile.Read(ref _failures);

    /// <summary>
    /// Whether a buffer of <paramref name="size"/> bytes in device-local memory would be carved from free space in blocks the driver already
    /// holds (so it takes nothing from the video memory budget): it is not a dedicated allocation and some device-local buffer block has
    /// a free range of that size plus 64 KB for alignment.
    /// </summary>
    public bool FitsInFreeSpace(ulong size)
    {
        if (size > _blockSize / 2) return false;

        lock (_gate)
        {
            foreach (var ((type, optimal, _), blocks) in _pools)
            {
                if (optimal || (_memProps.MemoryTypes[(int)type].PropertyFlags & MemoryPropertyFlags.DeviceLocalBit) == 0)
                    continue;

                foreach (var b in blocks)
                {
                    if (b.Dedicated) continue;
                    foreach (var (_, len) in b.Free) if (len >= size + 65536) return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Carves an allocation from a pool of the memory type: the best fit in its blocks (the fullest block for streamed slots), else its spare, else a
    /// new block made outside the lock; a dedicated block for a large or own-memory request.
    /// </summary>
    Allocation AllocateFrom(uint type, ulong size, ulong alignment, bool optimal, bool streamed, bool ownMemory)
    {
        bool dedicated = size > _blockSize / 2 || streamed && ownMemory && size >= OwnMemoryBytes;
        ulong slot = streamed && !dedicated ? AlignUp(size, Math.Max(alignment, SlotGranule)) : 0;
        if (slot > 0)
            size = slot;   // every allocation of the pool is a whole slot: a freed one is exactly the room the next needs

        var key = new PoolKey(type, optimal, slot);
        MemoryBlock? fresh = null;

        while (true)
        {
            lock (_gate)
            {
                if (!_pools.TryGetValue(key, out var blocks))
                {
                    _pools[key] = blocks = new List<MemoryBlock>();
                }

                if (fresh is not null)
                {
                    // The block made outside the lock (a dedicated one holds just this allocation).
                    blocks.Add(fresh);
                    _totalAllocated += fresh.Size;
                    _blockCount++;
                    var made = Carve(fresh, 0, 0, size);
                    if (!dedicated && slot == 0 && _misses.GetValueOrDefault(key) >= SpareAfterMisses)
                        MaybeStartSpare(key);

                    return made;
                }

                if (!dedicated)
                {
                    if ((slot > 0 ? Fullest(blocks, size, alignment) : Fit(blocks, size, alignment)) is { } f)
                    {
                        return Carve(f.Block, f.Index, AlignUp(f.Block.Free[f.Index].Offset, alignment), size);
                    }

                    // A spare made ahead is the next block (its bytes were counted when it was made).
                    if (_spares.Remove(key, out var spare))
                    {
                        blocks.Add(spare);
                        var made = Carve(spare, 0, 0, size);
                        MaybeStartSpare(key);
                        return made;
                    }

                    if (slot == 0) _misses[key] = _misses.GetValueOrDefault(key) + 1;
                }
            }

            // No room: a new block, made outside the lock so a slow vkAllocateMemory does not hold up the other threads' allocations.
            fresh = CreateBlock(type, optimal, dedicated ? size : slot > 0 ? slot * Math.Max(SlotBlockBytes / slot, 1) : _blockSize, dedicated, slot);
        }
    }

    /// <summary>
    /// The best fit (smallest waste) among the pool's regular blocks; null when none has room.
    /// </summary>
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

    /// <summary>
    /// The slot pools' placement: the fullest block with a free slot, so the streamed textures gather in as few blocks as they can and the others
    /// empty as the view moves on; null when none has room.
    /// </summary>
    static (MemoryBlock Block, int Index)? Fullest(List<MemoryBlock> blocks, ulong size, ulong alignment)
    {
        MemoryBlock? bestBlock = null;
        int bestIndex = -1;
        ulong bestWaste = ulong.MaxValue;

        foreach (var b in blocks)
        {
            if (b.Dedicated || bestBlock is not null && b.Used < bestBlock.Used) continue;

            for (int i = 0; i < b.Free.Count; i++)
            {
                var (off, len) = b.Free[i];
                if (AlignUp(off, alignment) + size > off + len) continue;

                ulong waste = len - size;
                if (bestBlock is null || b.Used > bestBlock.Used || waste < bestWaste)
                    (bestBlock, bestIndex, bestWaste) = (b, i, waste);
            }
        }

        return bestBlock is null ? null : (bestBlock, bestIndex);
    }

    /// <summary>
    /// Under the lock: makes the pool's next block on a worker unless there is one or it is being made.
    /// </summary>
    void MaybeStartSpare(PoolKey key)
    {
        if (_disposed || _lean || _spares.ContainsKey(key) || !_sparing.Add(key)) return;

        Interlocked.Increment(ref _workersBusy);
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            MemoryBlock? block = null;

            try
            {
                block = CreateBlock(key.Type, key.Optimal, _blockSize, false, key.Slot);
            }
            catch (VulkanException)
            {
                // out of memory: no spare, the pool asks again at its next miss
            }

            bool keep = false;

            lock (_gate)
            {
                _sparing.Remove(key);

                if (block is not null && !_disposed)
                {
                    _spares[key] = block;
                    _totalAllocated += block.Size;
                    _blockCount++;
                    keep = true;
                }
            }

            if (!keep && block is not null) _vk.FreeMemory(_dev.Device, block.Memory, null);

            Interlocked.Decrement(ref _workersBusy);
        }, null);
    }

    /// <summary>
    /// Gives the spares back (out of memory). False when there were none.
    /// </summary>
    bool ReleaseSpares()
    {
        List<MemoryBlock> mine;

        lock (_gate)
        {
            if (_spares.Count == 0) return false;

            mine = [.. _spares.Values];
            _spares.Clear();

            foreach (var b in mine)
            {
                _totalAllocated -= b.Size;
                _blockCount--;
            }
        }

        foreach (var b in mine) _vk.FreeMemory(_dev.Device, b.Memory, null);

        return true;
    }

    /// <summary>
    /// Takes a range out of a block's free list (the whole block when dedicated) and counts it.
    /// </summary>
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
        _totalUsed += size;
        _allocationCount++;
        return new Allocation
        {
            Block = block,
            Offset = offset,
            Size = size,
            MappedPointer = block.Mapped == 0 ? 0 : block.Mapped + (nint)offset,
        };
    }

    /// <summary>
    /// Allocates and maps a block (no bookkeeping, no lock: the caller counts it once it is in a pool). Any thread.
    /// </summary>
    MemoryBlock CreateBlock(uint type, bool optimal, ulong size, bool dedicated, ulong slot)
    {
        long profileStart = UploadProfile.Begin();

        // With ray queries every block may hold acceleration structures, their build inputs or scratch, which are reached by device address.
        var addressFlags = new MemoryAllocateFlagsInfo { SType = StructureType.MemoryAllocateFlagsInfo, Flags = MemoryAllocateFlags.DeviceAddressBit };
        var info = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo, AllocationSize = size, MemoryTypeIndex = type,
            PNext = _dev.HasRayQuery && !optimal ? &addressFlags : null,
        };
        var r = _vk.AllocateMemory(_dev.Device, in info, null, out var mem);

        if (r != Result.Success)
        {
            throw new VulkanException($"vkAllocateMemory({size} bytes, type {type}) failed: {r}", r);
        }

        var flags = _memProps.MemoryTypes[(int)type].PropertyFlags;
        var block = new MemoryBlock
        {
            Memory = mem,
            Size = size,
            MemoryTypeIndex = type,
            Optimal = optimal,
            Slot = slot,
            Dedicated = dedicated,
            Coherent = (flags & MemoryPropertyFlags.HostCoherentBit) != 0,
        };

        if ((flags & MemoryPropertyFlags.HostVisibleBit) != 0)
        {
            void* p;
            var mr = _vk.MapMemory(_dev.Device, mem, 0, Vk.WholeSize, 0, &p);

            if (mr != Result.Success)
            {
                _vk.FreeMemory(_dev.Device, mem, null);
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

    /// <summary>
    /// Returns the range to its block; empty blocks beyond one per pool (and dedicated ones) go back to the driver.
    /// </summary>
    public void Free(Allocation? a)
    {
        if (a == null)
        {
            return;
        }

        MemoryBlock? release = null;

        lock (_gate)
        {
            if (a.Freed)
            {
                return;
            }

            a.Freed = true;
            var b = a.Block;
            b.Used -= a.Size;
            _totalUsed -= a.Size;
            _allocationCount--;
            var blocks = _pools[Key(b)];

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
                    // Another empty block in the pool, or a spare made ahead, is the room to grow into: this one goes back. A block of slots goes
                    // back at once (a pool per size: kept empties would add up).
                    bool otherEmpty = _spares.ContainsKey(Key(b));

                    foreach (var o in blocks)
                    {
                        if (o != b && !o.Dedicated && o.Used == 0)
                        {
                            otherEmpty = true;
                            break;
                        }
                    }

                    if (otherEmpty || _lean || b.Slot > 0)
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
        if (_disposed)
        {
            _vk.FreeMemory(_dev.Device, b.Memory, null);
            return;
        }

        Interlocked.Increment(ref _workersBusy);
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            long start = UploadProfile.Begin();
            _vk.FreeMemory(_dev.Device, b.Memory, null);   // unmapping is implicit
            UploadProfile.EndBlock(false, start, b.Size, b.MemoryTypeIndex, b.Optimal, b.Dedicated);
            Interlocked.Decrement(ref _workersBusy);
        }, null);
    }

    /// <summary>
    /// Puts a range back in a block's sorted free list, coalescing it with its neighbours.
    /// </summary>
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

    /// <summary>
    /// Under the lock: takes the block out of its pool and the totals; the caller gives it back to the driver (<see cref="GiveBack"/>) outside the
    /// lock.
    /// </summary>
    void ReleaseBlock(List<MemoryBlock> blocks, MemoryBlock b)
    {
        blocks.Remove(b);
        _totalAllocated -= b.Size;
        _blockCount--;
    }

    /// <summary>
    /// The pool a block belongs to.
    /// </summary>
    static PoolKey Key(MemoryBlock b) => new(b.MemoryTypeIndex, b.Optimal, b.Slot);

    /// <summary>
    /// <paramref name="v"/> rounded up to a multiple of <paramref name="a"/>.
    /// </summary>
    static ulong AlignUp(ulong v, ulong a) => (v + a - 1) / a * a;

    /// <summary>
    /// Makes host writes visible to the GPU (a no-op on coherent memory).
    /// </summary>
    public void Flush(Allocation a, ulong offset = 0, ulong size = Vk.WholeSize) => Range(a, offset, size, false);

    /// <summary>
    /// Makes GPU writes visible to the host (a no-op on coherent memory).
    /// </summary>
    public void Invalidate(Allocation a, ulong offset = 0, ulong size = Vk.WholeSize) => Range(a, offset, size, true);

    /// <summary>
    /// Flushes or invalidates a range of non-coherent mapped memory, widened to the device's non-coherent atom size.
    /// </summary>
    void Range(Allocation a, ulong offset, ulong size, bool invalidate)
    {
        if (a.Block.Coherent || a.Block.Mapped == 0)
        {
            return;
        }

        ulong atom = Math.Max(_dev.Properties.Limits.NonCoherentAtomSize, 1);
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
            _vk.InvalidateMappedMemoryRanges(_dev.Device, 1, in range);
        }
        else
        {
            _vk.FlushMappedMemoryRanges(_dev.Device, 1, in range);
        }
    }

    #endregion

    #region Buffers and images

    /// <summary>
    /// A new buffer bound to new memory (mapped when host visible) and registered with the device. Any thread.
    /// </summary>
    public BufferAllocation CreateBuffer(ulong size, BufferUsageFlags usage, MemoryKind kind, string? name = null)
    {
        long profileStart = UploadProfile.Begin();

        try
        {
            return CreateBufferCore(size, usage, kind, name);
        }
        finally
        {
            UploadProfile.End(UploadProfile.Part.CreateBuffer, profileStart);
        }
    }

    /// <summary>
    /// Creates the buffer, allocates and binds its memory, registers it with the device and names it.
    /// </summary>
    BufferAllocation CreateBufferCore(ulong size, BufferUsageFlags usage, MemoryKind kind, string? name)
    {
        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        VulkanException.Check(_vk.CreateBuffer(_dev.Device, in info, null, out var buffer), "vkCreateBuffer");
        _vk.GetBufferMemoryRequirements(_dev.Device, buffer, out var req);
        Allocation alloc;

        try
        {
            alloc = Allocate(in req, kind, false);
        }
        catch
        {
            _vk.DestroyBuffer(_dev.Device, buffer, null);
            throw;
        }

        var r = _vk.BindBufferMemory(_dev.Device, buffer, alloc.Memory, alloc.Offset);

        if (r != Result.Success)
        {
            _vk.DestroyBuffer(_dev.Device, buffer, null);
            Free(alloc);
            throw new VulkanException($"vkBindBufferMemory failed: {r}", r);
        }

        var gb = new BufferAllocation { Buffer = buffer, Allocation = alloc, Size = size, Usage = usage, Name = name ?? "", Kind = kind };
        gb.Index = _dev.AddBuffer(new BufferEntry(buffer, gb));

        lock (_gate)
        {
            _liveBuffers.Add(gb);
        }

        if (name != null)
        {
            _dev.SetName(ObjectType.Buffer, buffer.Handle, name);
        }

        _dev.AddressLog?.Placed(ObjectType.Buffer, buffer.Handle, gb.Name, alloc.Memory.Handle, alloc.Offset, alloc.Size);
        return gb;
    }

    /// <summary>
    /// A new image bound to new memory (<see cref="Allocate"/> for <paramref name="streamed"/> and <paramref name="ownMemory"/>). Any thread.
    /// </summary>
    public ImageAllocation CreateImage(in ImageCreateInfo info, MemoryKind kind = MemoryKind.DeviceLocal, string? name = null, bool streamed = false,
        bool ownMemory = false)
    {
        long profileStart = UploadProfile.Begin();

        try
        {
            return CreateImageCore(in info, kind, name, streamed, ownMemory);
        }
        finally
        {
            UploadProfile.End(UploadProfile.Part.CreateImage, profileStart);
        }
    }

    /// <summary>
    /// Creates the image, allocates and binds its memory and tracks it as live.
    /// </summary>
    ImageAllocation CreateImageCore(in ImageCreateInfo info, MemoryKind kind, string? name, bool streamed, bool ownMemory)
    {
        VulkanException.Check(_vk.CreateImage(_dev.Device, in info, null, out var image), "vkCreateImage");
        _vk.GetImageMemoryRequirements(_dev.Device, image, out var req);
        Allocation alloc;

        try
        {
            alloc = Allocate(in req, kind, info.Tiling != ImageTiling.Linear, streamed, ownMemory);
        }
        catch
        {
            _vk.DestroyImage(_dev.Device, image, null);
            throw;
        }

        var r = _vk.BindImageMemory(_dev.Device, image, alloc.Memory, alloc.Offset);

        if (r != Result.Success)
        {
            _vk.DestroyImage(_dev.Device, image, null);
            Free(alloc);
            throw new VulkanException($"vkBindImageMemory failed: {r}", r);
        }

        var gi = new ImageAllocation
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

        lock (_gate)
        {
            _liveImages.Add(gi);
        }

        if (name != null)
        {
            _dev.SetName(ObjectType.Image, image.Handle, name);
        }

        _dev.AddressLog?.Placed(ObjectType.Image, image.Handle, gi.Name, alloc.Memory.Handle, alloc.Offset, alloc.Size);
        return gi;
    }

    /// <summary>
    /// Destroys the buffer and frees its memory now (use <see cref="VulkanDevice.DeferFree(BufferAllocation)"/> when the GPU may still use it).
    /// </summary>
    public void Free(BufferAllocation? b)
    {
        if (b == null)
        {
            return;
        }

        lock (_gate)
        {
            if (b.Freed)
            {
                return;
            }

            b.Freed = true;
            _liveBuffers.Remove(b);
        }

        long profileStart = UploadProfile.Begin();
        _vk.DestroyBuffer(_dev.Device, b.Buffer, null);
        _dev.AddressLog?.Forget(ObjectType.Buffer, b.Buffer.Handle);
        _dev.RemoveBuffer(b.Index);
        Free(b.Allocation);
        UploadProfile.End(UploadProfile.Part.FreeBuffer, profileStart);
    }

    /// <summary>
    /// Destroys an image and frees its memory; null or a second free does nothing.
    /// </summary>
    public void Free(ImageAllocation? i)
    {
        if (i == null)
        {
            return;
        }

        lock (_gate)
        {
            if (i.Freed)
            {
                return;
            }

            i.Freed = true;
            _liveImages.Remove(i);
        }

        long profileStart = UploadProfile.Begin();
        _vk.DestroyImage(_dev.Device, i.Image, null);
        _dev.AddressLog?.Forget(ObjectType.Image, i.Image.Handle);
        Free(i.Allocation);
        UploadProfile.End(UploadProfile.Part.FreeImage, profileStart);
    }

    /// <summary>
    /// Destroys what is still alive and releases all blocks (the device must be idle).
    /// </summary>
    public void Dispose()
    {
        BufferAllocation[] bs;
        ImageAllocation[] ims;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            bs = _liveBuffers.ToArray();
            ims = _liveImages.ToArray();
        }

        foreach (var b in bs) Free(b);
        foreach (var i in ims) Free(i);

        // Workers making spares or giving blocks back finish before the rest goes (a spare made after disposed was set frees itself).
        var wait = new SpinWait();
        while (Volatile.Read(ref _workersBusy) > 0) wait.SpinOnce();

        lock (_gate)
        {
            foreach (var blocks in _pools.Values)
            {
                foreach (var b in blocks)
                {
                    _vk.FreeMemory(_dev.Device, b.Memory, null);
                }
            }

            foreach (var b in _spares.Values) _vk.FreeMemory(_dev.Device, b.Memory, null);

            _spares.Clear();
            _pools.Clear();
            _totalAllocated = 0;
            _totalUsed = 0;
            _blockCount = 0;
        }
    }

    #endregion
}