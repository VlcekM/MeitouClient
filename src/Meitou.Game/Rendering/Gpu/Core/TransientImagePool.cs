using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gpu.Core;

/// <summary>
/// A device-local optimal-image memory block shared by non-overlapping intra-frame transient render targets.
/// </summary>
internal sealed class TransientBlock
{
    /// <summary>
    /// The backing Vulkan device memory.
    /// </summary>
    public DeviceMemory Memory;

    /// <summary>
    /// The block's size in bytes.
    /// </summary>
    public ulong Size;

    /// <summary>
    /// The Vulkan memory type index.
    /// </summary>
    public uint MemoryTypeIndex;

    /// <summary>
    /// Live aliased image slots currently bound inside this block.
    /// </summary>
    public readonly List<TransientSlot> Slots = [];

    /// <summary>
    /// Highest byte offset + size occupied by any live slot in this block.
    /// </summary>
    public ulong HighWaterBytes
    {
        get
        {
            ulong max = 0;
            foreach (var s in Slots)
            {
                ulong end = s.Offset + s.Size;
                if (end > max) max = end;
            }

            return max;
        }
    }
}

/// <summary>
/// One aliased image's placement and phase lifetime inside a <see cref="TransientBlock"/>.
/// </summary>
internal sealed class TransientSlot
{
    /// <summary>
    /// The block this slot lives in.
    /// </summary>
    public TransientBlock Block = null!;

    /// <summary>
    /// The aliased image allocation.
    /// </summary>
    public ImageAllocation Image = null!;

    /// <summary>
    /// Byte offset inside <see cref="Block"/>.
    /// </summary>
    public ulong Offset;

    /// <summary>
    /// Required byte size from <c>vkGetImageMemoryRequirements</c>.
    /// </summary>
    public ulong Size;

    /// <summary>
    /// Intra-frame phase range during which the image holds live data.
    /// </summary>
    public TransientLifetime Lifetime;

    /// <summary>
    /// Vulkan image aspect flags used when emitting aliasing layout transitions.
    /// </summary>
    public ImageAspectFlags Aspect;

    /// <summary>
    /// Whether this slot's byte range physically overlaps at least one other live slot in <see cref="Block"/>.
    /// </summary>
    public bool AliasedWithOther;

    /// <summary>
    /// Set when <see cref="AliasedWithOther"/> drops to false so the slot still receives one final
    /// <c>UNDEFINED -> GENERAL</c> transition the next time its phase runs.
    /// </summary>
    public bool NeedsAcquireBarrier;
}

/// <summary>
/// Packs strictly intra-frame transient render targets with disjoint <see cref="TransientLifetime"/> intervals into
/// shared device-local optimal-image <see cref="DeviceMemory"/> blocks on Vulkan, and emits batched
/// <c>UNDEFINED -> GENERAL</c> aliasing barriers at each phase start. Disabled when <c>MEITOU_NO_ALIAS=1</c>.
/// </summary>
internal sealed unsafe class TransientImagePool : IDisposable
{
    /// <summary>
    /// Number of phases in <see cref="TransientPhase"/>.
    /// </summary>
    const int PhaseCount = 12;

    /// <summary>
    /// Minimum size of a transient aliasing block (64 MB, matching <see cref="GpuAllocator.DefaultBlockSize"/>).
    /// </summary>
    const ulong MinBlockBytes = GpuAllocator.DefaultBlockSize;

    /// <summary>
    /// Granularity used when rounding up oversized blocks (1 MB).
    /// </summary>
    const ulong OversizeGranule = 1UL << 20;

    /// <summary>
    /// Owning device.
    /// </summary>
    readonly VulkanDevice _dev;

    /// <summary>
    /// Vulkan entry points.
    /// </summary>
    readonly Vk _vk;

    /// <summary>
    /// Owning allocator (used for memory type selection and priority settings).
    /// </summary>
    readonly GpuAllocator _allocator;

    /// <summary>
    /// Synchronises block placement, freeing and diagnostics.
    /// </summary>
    readonly object _gate = new();

    /// <summary>
    /// Active transient memory blocks.
    /// </summary>
    readonly List<TransientBlock> _blocks = [];

    /// <summary>
    /// Live slots indexed by <see cref="TransientLifetime.First"/> for fast phase-start barrier emission.
    /// </summary>
    readonly List<TransientSlot>[] _byFirstPhase = [.. Enumerable.Range(0, PhaseCount).Select(_ => new List<TransientSlot>())];

    /// <summary>
    /// Reusable scratch list for <see cref="BeginPhase"/> to avoid per-phase heap allocations (render thread only).
    /// </summary>
    readonly List<(Image Image, ImageAspectFlags Aspect)> _phaseBarriers = [];

    /// <summary>
    /// Whether aliasing is enabled (false when <c>MEITOU_NO_ALIAS=1</c>).
    /// </summary>
    readonly bool _enabled = Environment.GetEnvironmentVariable("MEITOU_NO_ALIAS") != "1";

    /// <summary>
    /// Frame number tracked by <see cref="BeginPhase"/> (render thread only).
    /// </summary>
    long _lastFrame = -1;

    /// <summary>
    /// Bitmask of phases already begun on <see cref="_lastFrame"/> (render thread only).
    /// </summary>
    uint _phasesBegun;

    /// <summary>
    /// Set by <see cref="Dispose"/>.
    /// </summary>
    bool _disposed;

    /// <summary>
    /// Creates a transient aliasing pool backed by <paramref name="allocator"/>.
    /// </summary>
    public TransientImagePool(VulkanDevice dev, GpuAllocator allocator)
    {
        _dev = dev;
        _vk = dev.Vk;
        _allocator = allocator;
    }

    /// <summary>
    /// Whether transient aliasing is active.
    /// </summary>
    public bool Enabled => _enabled;

    /// <summary>
    /// Total physical bytes allocated in transient blocks.
    /// </summary>
    public ulong AllocatedBytes
    {
        get
        {
            lock (_gate)
            {
                ulong sum = 0;
                foreach (var b in _blocks) sum += b.Size;
                return sum;
            }
        }
    }

    /// <summary>
    /// Total high-water physical bytes used across transient blocks.
    /// </summary>
    public ulong UsedBytes
    {
        get
        {
            lock (_gate)
            {
                ulong sum = 0;
                foreach (var b in _blocks) sum += b.HighWaterBytes;
                return sum;
            }
        }
    }

    /// <summary>
    /// Number of active transient blocks.
    /// </summary>
    public int BlockCount { get { lock (_gate) return _blocks.Count; } }

    /// <summary>
    /// Number of live aliased images currently bound in the pool.
    /// </summary>
    public int ImageCount
    {
        get
        {
            lock (_gate)
            {
                int count = 0;
                foreach (var b in _blocks) count += b.Slots.Count;
                return count;
            }
        }
    }

    /// <summary>
    /// Creates an optimal-tiling image with <see cref="ImageCreateFlags.CreateAliasBit"/> and binds it to a
    /// non-overlapping offset in a shared device-local transient block.
    /// </summary>
    public ImageAllocation CreateImage(in ImageCreateInfo sourceInfo, TransientLifetime lifetime, ImageAspectFlags aspect, string? name)
    {
        var info = sourceInfo;
        info.Flags |= ImageCreateFlags.CreateAliasBit;
        VulkanException.Check(_vk.CreateImage(_dev.Device, in info, null, out var image), "vkCreateImage(alias)");
        _vk.GetImageMemoryRequirements(_dev.Device, image, out var req);

        var alloc = new Allocation { Size = req.Size };
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
            Kind = MemoryKind.DeviceLocal,
            Aliased = true,
        };
        var slot = new TransientSlot
        {
            Image = gi,
            Size = req.Size,
            Lifetime = lifetime,
            Aspect = aspect,
        };
        gi.TransientSlot = slot;

        var (block, offset) = Place(req, slot, image);
        alloc.Offset = offset;
        alloc.TransientMemory = block.Memory;
        alloc.TransientMemoryTypeIndex = block.MemoryTypeIndex;

        var r = _vk.BindImageMemory(_dev.Device, image, block.Memory, offset);
        if (r != Result.Success)
        {
            lock (_gate)
            {
                block.Slots.Remove(slot);
                UpdateAliasingFlags(block);
            }

            _vk.DestroyImage(_dev.Device, image, null);
            throw new VulkanException($"vkBindImageMemory(alias) failed: {r}", r);
        }

        lock (_gate) _byFirstPhase[(int)lifetime.First].Add(slot);

        if (name != null) _dev.SetName(ObjectType.Image, image.Handle, name);

        _dev.AddressLog?.Placed(ObjectType.Image, image.Handle, gi.Name, block.Memory.Handle, offset, req.Size);
        return gi;
    }

    /// <summary>
    /// Destroys an aliased image, removes its slot and frees its block back to the driver on a worker when empty
    /// (keeping up to one empty block per memory type unless <see cref="GpuAllocator.Lean"/> is set).
    /// </summary>
    public void Free(ImageAllocation img)
    {
        TransientBlock? emptyBlock = null;
        bool disposed;

        lock (_gate)
        {
            if (img.Freed) return;

            img.Freed = true;
            disposed = _disposed;
            var slot = img.TransientSlot;

            if (slot is not null)
            {
                var block = slot.Block;
                block.Slots.Remove(slot);
                _byFirstPhase[(int)slot.Lifetime.First].Remove(slot);

                if (block.Slots.Count == 0)
                {
                    if (_allocator.Lean || _disposed || HasOtherEmptyBlock(block))
                    {
                        _blocks.Remove(block);
                        emptyBlock = block;
                    }
                }
                else
                {
                    UpdateAliasingFlags(block);
                }
            }
        }

        _vk.DestroyImage(_dev.Device, img.Image, null);
        _dev.AddressLog?.Forget(ObjectType.Image, img.Image.Handle);

        if (emptyBlock is not null)
        {
            if (disposed) _vk.FreeMemory(_dev.Device, emptyBlock.Memory, null);
            else _allocator.GiveBackMemory(emptyBlock.Memory, emptyBlock.Size, emptyBlock.MemoryTypeIndex, true, false);
        }
    }

    /// <summary>
    /// Gives every kept empty transient block back to the driver on a worker. Returns the bytes released.
    /// </summary>
    public ulong Trim()
    {
        List<TransientBlock>? release = null;

        lock (_gate)
        {
            if (_disposed) return 0;

            for (int i = _blocks.Count - 1; i >= 0; i--)
            {
                if (_blocks[i].Slots.Count == 0)
                {
                    release ??= [];
                    release.Add(_blocks[i]);
                    _blocks.RemoveAt(i);
                }
            }
        }

        if (release is null) return 0;

        ulong bytes = 0;

        foreach (var b in release)
        {
            bytes += b.Size;
            _allocator.GiveBackMemory(b.Memory, b.Size, b.MemoryTypeIndex, true, false);
        }

        return bytes;
    }

    /// <summary>
    /// Emits a batched <c>UNDEFINED -> GENERAL</c> aliasing barrier for every live aliased image whose
    /// <see cref="TransientLifetime.First"/> equals <paramref name="phase"/> and whose memory physically overlaps
    /// another slot, at most once per frame per phase.
    /// </summary>
    public void BeginPhase(CommandBuffer cb, TransientPhase phase, long frameNumber)
    {
        if (!_enabled) return;

        if (_lastFrame != frameNumber)
        {
            _lastFrame = frameNumber;
            _phasesBegun = 0;
        }

        uint bit = 1u << (int)phase;
        if ((_phasesBegun & bit) != 0) return;

        _phasesBegun |= bit;
        var list = _byFirstPhase[(int)phase];

        lock (_gate)
        {
            _phaseBarriers.Clear();

            foreach (var s in list)
            {
                if (!s.AliasedWithOther && !s.NeedsAcquireBarrier) continue;

                s.NeedsAcquireBarrier = false;
                _phaseBarriers.Add((s.Image.Image, s.Aspect));
            }
        }

        if (_phaseBarriers.Count == 0) return;

        EmitAliasingBarriers(cb, _phaseBarriers);
    }

    /// <summary>
    /// Emits a single <c>UNDEFINED -> GENERAL</c> aliasing barrier for a newly created aliased image on the frame's
    /// command buffer so prior writes in the same frame to the same physical bytes are ordered before it.
    /// </summary>
    public void RecordAcquireBarrier(CommandBuffer cb, Image image, ImageAspectFlags aspect)
    {
        var barrier = MakeBarrier(image, aspect);
        var dep = new DependencyInfo { SType = StructureType.DependencyInfo, ImageMemoryBarrierCount = 1, PImageMemoryBarriers = &barrier };
        _vk.CmdPipelineBarrier2(cb, &dep);
    }

    /// <summary>
    /// Writes diagnostics for every transient block and aliased image to <paramref name="rows"/> and
    /// <paramref name="blocks"/> (called by <see cref="GpuAllocator.WriteReport"/>).
    /// </summary>
    public void AppendReport(List<(ulong Bytes, string Line)> rows, List<(uint Type, ulong Size, string Line)> blocks,
        Func<ulong, double> mb, Func<uint, MemoryPropertyFlags> typeFlags)
    {
        lock (_gate)
        {
            for (int bi = 0; bi < _blocks.Count; bi++)
            {
                var b = _blocks[bi];
                blocks.Add((b.MemoryTypeIndex, b.Size,
                    $"{mb(b.Size),10:0.000}  {mb(b.HighWaterBytes),10:0.000}  {b.Slots.Count,5}  type {b.MemoryTypeIndex} "
                    + $"{typeFlags(b.MemoryTypeIndex)} transient-aliased ({b.Slots.Count} images)"));

                foreach (var s in b.Slots)
                {
                    var i = s.Image;
                    rows.Add((0,
                        $"   (alias)  DeviceLocal  image   {i.Name}  "
                        + $"[{i.Format} {i.Extent.Width}x{i.Extent.Height}, {mb(s.Size):0.000} MB @ blk#{bi}+{mb(s.Offset):0.00} MB, "
                        + $"{s.Lifetime.First}..{s.Lifetime.Last}]"));
                }
            }

            if (_blocks.Count > 0)
            {
                ulong used = 0;
                int count = 0;
                foreach (var b in _blocks)
                {
                    used += b.HighWaterBytes;
                    count += b.Slots.Count;
                }

                rows.Add((used, $"{mb(used),10:0.000}  DeviceLocal  alias   transient render targets  [{count} aliased images in {_blocks.Count} blocks]"));
            }
        }
    }

    /// <summary>
    /// Destroys all remaining aliased images and frees all transient blocks.
    /// </summary>
    public void Dispose()
    {
        ImageAllocation[] imgs;
        TransientBlock[] leftover;

        lock (_gate)
        {
            if (_disposed) return;

            _disposed = true;
            var all = new List<ImageAllocation>();
            foreach (var b in _blocks)
                foreach (var s in b.Slots) all.Add(s.Image);

            imgs = all.ToArray();
            leftover = _blocks.ToArray();
            _blocks.Clear();
            foreach (var list in _byFirstPhase) list.Clear();
        }

        foreach (var img in imgs)
        {
            if (!img.Freed)
            {
                img.Freed = true;
                _vk.DestroyImage(_dev.Device, img.Image, null);
                _dev.AddressLog?.Forget(ObjectType.Image, img.Image.Handle);
            }
        }

        foreach (var b in leftover) _vk.FreeMemory(_dev.Device, b.Memory, null);
    }

    #region Placement and barriers

    /// <summary>
    /// Finds a non-overlapping aligned offset in an existing transient block of a compatible device-local memory type,
    /// or allocates a new transient block when none fits, and reserves <paramref name="slot"/> inside the chosen block.
    /// Destroys <paramref name="image"/> on failure.
    /// </summary>
    (TransientBlock Block, ulong Offset) Place(in MemoryRequirements req, TransientSlot slot, Image image)
    {
        var types = _allocator.SelectMemoryTypes(req.MemoryTypeBits, MemoryKind.DeviceLocal, req.Size);
        if (types.Count == 0)
        {
            _vk.DestroyImage(_dev.Device, image, null);
            throw new VulkanException($"no device-local memory type for aliased image ({req.Size} bytes)");
        }

        lock (_gate)
        {
            foreach (uint type in types)
            {
                TransientBlock? emptyFit = null;

                foreach (var b in _blocks)
                {
                    if (b.MemoryTypeIndex != type) continue;

                    if (b.Slots.Count == 0)
                    {
                        if (emptyFit is null && req.Size <= b.Size) emptyFit = b;
                        continue;
                    }

                    if (TryFindOffset(b, req.Size, req.Alignment, slot.Lifetime, out ulong offset))
                    {
                        AttachSlot(b, slot, offset);
                        return (b, offset);
                    }
                }

                if (emptyFit is not null)
                {
                    AttachSlot(emptyFit, slot, 0);
                    return (emptyFit, 0);
                }
            }
        }

        ulong blockSize = Math.Max(MinBlockBytes, AlignUp(req.Size, OversizeGranule));
        TransientBlock? fresh = null;
        VulkanException? last = null;

        foreach (uint type in types)
        {
            try
            {
                fresh = AllocateBlock(type, blockSize);
                break;
            }
            catch (VulkanException e) when (e.OutOfMemory)
            {
                last = e;
            }
        }

        if (fresh is null)
        {
            _vk.DestroyImage(_dev.Device, image, null);
            throw last ?? new VulkanException("transient block allocation failed");
        }

        lock (_gate)
        {
            _blocks.Add(fresh);
            AttachSlot(fresh, slot, 0);
        }

        return (fresh, 0);
    }

    /// <summary>
    /// Places <paramref name="slot"/> at <paramref name="offset"/> inside <paramref name="block"/> and refreshes
    /// aliasing overlap flags (caller holds <see cref="_gate"/>).
    /// </summary>
    static void AttachSlot(TransientBlock block, TransientSlot slot, ulong offset)
    {
        slot.Block = block;
        slot.Offset = offset;
        block.Slots.Add(slot);
        UpdateAliasingFlags(block);
    }

    /// <summary>
    /// Whether another empty transient block of the same memory type is already kept (caller holds <see cref="_gate"/>).
    /// </summary>
    bool HasOtherEmptyBlock(TransientBlock block)
    {
        foreach (var o in _blocks)
            if (o != block && o.MemoryTypeIndex == block.MemoryTypeIndex && o.Slots.Count == 0) return true;

        return false;
    }

    /// <summary>
    /// Recomputes <see cref="TransientSlot.AliasedWithOther"/> for every live slot in <paramref name="block"/>.
    /// </summary>
    static void UpdateAliasingFlags(TransientBlock block)
    {
        var slots = block.Slots;
        int n = slots.Count;

        for (int i = 0; i < n; i++)
        {
            var a = slots[i];
            ulong aEnd = a.Offset + a.Size;
            bool overlaps = false;

            for (int j = 0; j < n; j++)
            {
                if (i == j) continue;

                var b = slots[j];
                if (a.Offset < b.Offset + b.Size && b.Offset < aEnd)
                {
                    overlaps = true;
                    break;
                }
            }

            if (a.AliasedWithOther && !overlaps) a.NeedsAcquireBarrier = true;

            a.AliasedWithOther = overlaps;
        }
    }

    /// <summary>
    /// Finds the lowest aligned offset in <paramref name="block"/> that fits <paramref name="size"/> bytes without
    /// overlapping any live slot whose lifetime overlaps <paramref name="lifetime"/>.
    /// </summary>
    static bool TryFindOffset(TransientBlock block, ulong size, ulong alignment, TransientLifetime lifetime, out ulong offset)
    {
        offset = 0;
        if (size > block.Size) return false;

        var conflicts = new List<(ulong Start, ulong End)>();
        foreach (var s in block.Slots)
            if (s.Lifetime.Overlaps(lifetime)) conflicts.Add((s.Offset, s.Offset + s.Size));

        if (conflicts.Count == 0) return true;

        conflicts.Sort((a, b) => a.Start.CompareTo(b.Start));
        ulong candidate = 0;

        foreach (var (start, end) in conflicts)
        {
            if (candidate + size <= start)
            {
                offset = candidate;
                return true;
            }

            ulong next = AlignUp(end, alignment);
            if (next > candidate) candidate = next;
        }

        if (candidate + size <= block.Size)
        {
            offset = candidate;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Allocates a new device-local optimal-image transient block of <paramref name="size"/> bytes.
    /// </summary>
    TransientBlock AllocateBlock(uint type, ulong size)
    {
        var priority = new MemoryPriorityAllocateInfoEXT
        {
            SType = StructureType.MemoryPriorityAllocateInfoExt,
            Priority = 0.7f,
        };
        var info = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = size,
            MemoryTypeIndex = type,
            PNext = _dev.HasMemoryPriority ? &priority : null,
        };
        var r = _vk.AllocateMemory(_dev.Device, in info, null, out var mem);
        if (r != Result.Success) throw new VulkanException($"vkAllocateMemory(transient {size} bytes, type {type}) failed: {r}", r);

        return new TransientBlock { Memory = mem, Size = size, MemoryTypeIndex = type };
    }

    /// <summary>
    /// Emits a single batched <c>vkCmdPipelineBarrier2</c> transitioning all <paramref name="targets"/> from
    /// <c>UNDEFINED</c> to <c>GENERAL</c> with a full memory dependency.
    /// </summary>
    void EmitAliasingBarriers(CommandBuffer cb, List<(Image Image, ImageAspectFlags Aspect)> targets)
    {
        int n = targets.Count;
        var barriers = n <= 32 ? stackalloc ImageMemoryBarrier2[n] : new ImageMemoryBarrier2[n];
        for (int i = 0; i < n; i++) barriers[i] = MakeBarrier(targets[i].Image, targets[i].Aspect);

        fixed (ImageMemoryBarrier2* p = barriers)
        {
            var dep = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                ImageMemoryBarrierCount = (uint)n,
                PImageMemoryBarriers = p,
            };
            _vk.CmdPipelineBarrier2(cb, &dep);
        }
    }

    /// <summary>
    /// Builds an <see cref="ImageMemoryBarrier2"/> transitioning <paramref name="image"/> from <c>UNDEFINED</c> to
    /// <c>GENERAL</c> after all prior writes in the command stream.
    /// </summary>
    static ImageMemoryBarrier2 MakeBarrier(Image image, ImageAspectFlags aspect) => new()
    {
        SType = StructureType.ImageMemoryBarrier2,
        SrcStageMask = PipelineStageFlags2.AllCommandsBit,
        SrcAccessMask = AccessFlags2.MemoryWriteBit,
        DstStageMask = PipelineStageFlags2.AllCommandsBit,
        DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
        OldLayout = ImageLayout.Undefined,
        NewLayout = ImageLayout.General,
        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
        Image = image,
        SubresourceRange = new ImageSubresourceRange(aspect, 0, Vk.RemainingMipLevels, 0, Vk.RemainingArrayLayers),
    };

    /// <summary>
    /// Rounds <paramref name="value"/> up to a multiple of power-of-two <paramref name="alignment"/>.
    /// </summary>
    static ulong AlignUp(ulong value, ulong alignment) => alignment <= 1 ? value : (value + alignment - 1) & ~(alignment - 1);

    #endregion
}
