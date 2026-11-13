using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu.Core;
using Meitou.Game.Rendering.Gpu.Shaders;
using Silk.NET.Vulkan;
using Sampler = Silk.NET.Vulkan.Sampler;

namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// The arrays of the bindless set (set 0 of the native model, docs/renderer-native.md 2.6), by binding number. The integer arrays
/// (<see cref="UintTexture2D"/>, <see cref="IntTexture2D"/>) take textures of UINT and SINT formats, which a float sampler may not read.
/// </summary>
public enum BindlessKind
{
    /// <summary>
    /// Binding 0, <c>sampler2D textures2D[]</c>: 2D colour textures.
    /// </summary>
    Texture2D = 0,
    /// <summary>
    /// Binding 1, <c>sampler2DArray textures2DArray[]</c>: 2D array textures.
    /// </summary>
    Texture2DArray = 1,
    /// <summary>
    /// Binding 2, <c>samplerCube texturesCube[]</c>: cube maps.
    /// </summary>
    Cube = 2,
    /// <summary>
    /// Binding 3, <c>sampler2DShadow shadowTextures[]</c>: depth textures read with a compare.
    /// </summary>
    Shadow2D = 3,
    /// <summary>
    /// Binding 4, <c>usampler2D utextures2D[]</c>: 2D textures of UINT formats.
    /// </summary>
    UintTexture2D = 4,
    /// <summary>
    /// Binding 5, <c>isampler2D itextures2D[]</c>: 2D textures of SINT formats.
    /// </summary>
    IntTexture2D = 5,
    /// <summary>
    /// Binding 6, <c>sampler2DArrayShadow shadowArrays[]</c>: depth array textures read with a compare.
    /// </summary>
    ShadowArray = 6
}

/// <summary>
/// An entry of the <see cref="BindlessTable"/>: the array and the index in it (what a shader reads through a push constant or a material table).
/// </summary>
public readonly record struct BindlessHandle(BindlessKind Kind, uint Index);

/// <summary>
/// The bindless texture table: combined image samplers in seven partially bound, update-after-bind arrays (<c>sampler2D textures2D[]</c>,
/// <c>sampler2DArray textures2DArray[]</c>, <c>samplerCube texturesCube[]</c>, <c>sampler2DShadow shadowTextures[]</c>,
/// <c>usampler2D utextures2D[]</c>, <c>isampler2D itextures2D[]</c>, <c>sampler2DArrayShadow shadowArrays[]</c>; <see cref="GlslDeclarations"/>), one
/// descriptor set per frame slot so a slot is never written while a frame in flight reads it. Registrations and rewrites go into a journal that is
/// replayed into a slot's set when that slot's frame begins, and into the open frame's own set when that frame ends (before it is submitted): an
/// index registered or updated while a frame is open is valid in that frame. A freed index is reused once the frames that could read it have
/// finished. The sampler is part of the entry, as GL's per-texture sampler state is. Render thread only.
/// </summary>
/// <remarks>
/// Update-after-bind descriptors are read when the command buffer executes, so an <see cref="Update(BindlessKind, uint, in SampledTexture)"/>
/// made while a frame is open is seen by every draw of that frame that reads the index, including draws recorded before the call. To change
/// what a draw sees part-way through a frame (as GL does when a texture's view or sampler changes between two draws), register a new index
/// and free the old one.
/// </remarks>
public sealed unsafe class BindlessTable : IDisposable, IGpuBindlessTable
{
    #region GLSL declarations

    /// <summary>
    /// The number of arrays (bindings 0 to 6 of the set).
    /// </summary>
    public const int ArrayCount = 7;

    /// <summary>
    /// The GLSL names of the arrays, by binding (<see cref="BindlessKind"/> order).
    /// </summary>
    public static readonly string[] GlslArrays = ["textures2D", "textures2DArray", "texturesCube", "shadowTextures", "utextures2D", "itextures2D",
        "shadowArrays"];

    /// <summary>
    /// The GLSL types of the arrays' elements, by binding.
    /// </summary>
    public static readonly string[] GlslTypes = ["sampler2D", "sampler2DArray", "samplerCube", "sampler2DShadow", "usampler2D", "isampler2D",
        "sampler2DArrayShadow"];

    /// <summary>
    /// The set's declarations for a native shader (set 0), with the extension <c>nonuniformEXT</c> needs. Index an array with
    /// <c>textures2D[nonuniformEXT(index)]</c> when the index may differ between invocations of a draw.
    /// </summary>
    public static readonly string GlslDeclarations = Declarations(0);

    /// <summary>
    /// As <see cref="GlslDeclarations"/> for the table bound at another set number.
    /// </summary>
    public static string Declarations(int set)
    {
        var sb = new System.Text.StringBuilder("#extension GL_EXT_nonuniform_qualifier : require\n");
        for (int i = 0; i < ArrayCount; i++) sb.Append($"layout(set = {set}, binding = {i}) uniform {GlslTypes[i]} {GlslArrays[i]}[];\n");
        return sb.ToString();
    }

    /// <summary>
    /// Checks a sampler a native shader declares in the table's set against the table (binding, element type, runtime-sized); throws naming the
    /// difference. <see cref="ShaderLibrary.Native"/> runs it for programs whose layout includes the table.
    /// </summary>
    public static void Check(SamplerInfo s, string program)
    {
        if (s.Binding < 0 || s.Binding >= ArrayCount)
        {
            throw new InvalidOperationException($"{program}: sampler '{s.Name}' at binding {s.Binding} "
                + $"of the bindless set (bindings 0 to {ArrayCount - 1})");
        }

        var (dim, arrayed, depth, scalar) = (BindlessKind)s.Binding switch
        {
            BindlessKind.Texture2DArray => (SamplerDimension.Dim2D, true, false, ScalarKind.Float),
            BindlessKind.Cube => (SamplerDimension.Cube, false, false, ScalarKind.Float),
            BindlessKind.Shadow2D => (SamplerDimension.Dim2D, false, true, ScalarKind.Float),
            BindlessKind.ShadowArray => (SamplerDimension.Dim2D, true, true, ScalarKind.Float),
            BindlessKind.UintTexture2D => (SamplerDimension.Dim2D, false, false, ScalarKind.UInt),
            BindlessKind.IntTexture2D => (SamplerDimension.Dim2D, false, false, ScalarKind.Int),
            _ => (SamplerDimension.Dim2D, false, false, ScalarKind.Float),
        };
        if (s.Dimension != dim || s.Arrayed != arrayed || s.Depth != depth || s.SampledKind != scalar || s.Multisampled || s.ArrayLength >= 0)
        {
            throw new InvalidOperationException($"{program}: sampler '{s.Name}' at binding {s.Binding} of the bindless set must be " +
                $"'{GlslTypes[s.Binding]} {GlslArrays[s.Binding]}[]' (got {s.SampledKind} {s.Dimension} "
                + $"arrayed={s.Arrayed} depth={s.Depth} length={s.ArrayLength})");
        }
    }

    #endregion

    #region Table state

    /// <summary>
    /// The device the set, pool and layout are made on.
    /// </summary>
    readonly VulkanDevice _device;
    /// <summary>
    /// The update-after-bind pool the slots' sets come from.
    /// </summary>
    readonly DescriptorPool _pool;
    /// <summary>
    /// One set per frame slot.
    /// </summary>
    readonly DescriptorSet[] _sets;
    /// <summary>
    /// Each slot's set's index in the device's set table.
    /// </summary>
    readonly int[] _setIndices;
    /// <summary>
    /// Per slot: the journal entries replayed so far.
    /// </summary>
    readonly long[] _applied;
    /// <summary>
    /// The registrations and rewrites in order, replayed into each slot's set (entries every slot has seen are dropped).
    /// </summary>
    readonly List<(BindlessKind Kind, uint Index, SampledTexture GpuTexture)> _journal = [];
    /// <summary>
    /// The number of entries dropped from the front of the journal.
    /// </summary>
    long _journalBase;
    /// <summary>
    /// Per array: the indices freed and past the frames in flight, handed out again first.
    /// </summary>
    readonly Stack<uint>[] _free = new Stack<uint>[ArrayCount];
    /// <summary>
    /// Per array: the next index never handed out.
    /// </summary>
    readonly uint[] _next = new uint[ArrayCount];
    /// <summary>
    /// Between BeginFrame and EndFrame of the frame in <see cref="_currentSlot"/>.
    /// </summary>
    bool _open;

    /// <summary>
    /// The table for the device: the arrays sized from its update-after-bind limits (raised to 8192 on macOS), the layout, the pool and one set per
    /// frame slot.
    /// </summary>
    public BindlessTable(VulkanDevice device)
    {
        this._device = device;
        var props12 = new PhysicalDeviceVulkan12Properties { SType = StructureType.PhysicalDeviceVulkan12Properties };
        var props = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &props12 };
        device.Vk.GetPhysicalDeviceProperties2(device.PhysicalDevice, &props);
        uint limit = Math.Min(props12.MaxPerStageDescriptorUpdateAfterBindSamplers, props12.MaxDescriptorSetUpdateAfterBindSampledImages);

        // MoltenVK advertises only 1024 update-after-bind sampled images ("Metal caps the legacy path"), but its Metal
        // argument buffers on Tier 2 (Apple silicon) hold far more. The 512-slot 2D array overflows when 512+ unique
        // textures are resident (The Hub's foliage alone); Windows GPUs advertise 16k+ and never see this.
        if (OperatingSystem.IsMacOS() && limit < 8192) limit = 8192;

        // 2D textures get most of the room; the rest are few (terrain arrays, the atmosphere's cubes, shadow maps, integer lookup textures
        // such as the terrain's blend cells, the sun's cascade array). The shares add up to 31/32 of the limit.
        Capacity = [Math.Min(16384u, limit / 2), Math.Min(1024u, limit / 8), Math.Min(256u, limit / 16), Math.Min(256u, limit / 16),
            Math.Min(256u, limit / 16), Math.Min(256u, limit / 16), Math.Min(64u, limit / 32)];
        for (int i = 0; i < ArrayCount; i++) _free[i] = new Stack<uint>();

        var bindings = stackalloc DescriptorSetLayoutBinding[ArrayCount];
        var flags = stackalloc DescriptorBindingFlags[ArrayCount];
        for (int i = 0; i < ArrayCount; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding((uint)i, DescriptorType.CombinedImageSampler, Capacity[i], ShaderStageFlags.All);
            flags[i] = DescriptorBindingFlags.PartiallyBoundBit | DescriptorBindingFlags.UpdateAfterBindBit
                | DescriptorBindingFlags.UpdateUnusedWhilePendingBit;
        }
        var flagInfo = new DescriptorSetLayoutBindingFlagsCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo, BindingCount = ArrayCount, PBindingFlags = flags,
        };
        var li = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            PNext = &flagInfo,
            Flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit,
            BindingCount = ArrayCount,
            PBindings = bindings,
        };
        VulkanException.Check(device.Vk.CreateDescriptorSetLayout(device.Device, &li, null, out var layout), "vkCreateDescriptorSetLayout");
        Layout = layout;

        int slots = device.Frames.Count;
        uint total = 0;
        foreach (var c in Capacity) total += c;
        var size = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, total * (uint)slots);
        var pi = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            Flags = DescriptorPoolCreateFlags.UpdateAfterBindBit,
            MaxSets = (uint)slots,
            PoolSizeCount = 1,
            PPoolSizes = &size,
        };
        VulkanException.Check(device.Vk.CreateDescriptorPool(device.Device, &pi, null, out _pool), "vkCreateDescriptorPool");
        _sets = new DescriptorSet[slots];
        _setIndices = new int[slots];
        _applied = new long[slots];
        _vacated = new List<(BindlessKind, uint)>[slots];
        for (int i = 0; i < slots; i++)
        {
            var ai = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _pool, DescriptorSetCount = 1,
                PSetLayouts = &layout,
            };
            DescriptorSet set;
            VulkanException.Check(device.Vk.AllocateDescriptorSets(device.Device, &ai, &set), "vkAllocateDescriptorSets");
            _sets[i] = set;
            _setIndices[i] = device.AddSet(set);
            _vacated[i] = [];
        }
    }

    #endregion

    #region Registration

    /// <summary>
    /// The slot of the frame begun last.
    /// </summary>
    int _currentSlot;

    /// <summary>
    /// The indices freed and not registered again (<c>MEITOU_DIAG_FAULT=1</c>, <see cref="DeviceFault.Tracking"/>, issue #319); null otherwise.
    /// </summary>
    public FreedIndices? Freed { get; } = DeviceFault.Tracking ? new() : null;

    /// <summary>
    /// Entries per array (<see cref="BindlessKind"/> order).
    /// </summary>
    public uint[] Capacity { get; }
    /// <summary>
    /// The table's descriptor set layout (set 0 of the native model).
    /// </summary>
    public DescriptorSetLayout Layout { get; }

    /// <summary>
    /// The set of the frame being recorded (bind once per command buffer).
    /// </summary>
    public DescriptorSet Set => _sets[_currentSlot];

    /// <summary>
    /// The set of the frame being recorded, as the renderers' handle (the index into the backend's sets).
    /// </summary>
    public int SetIndex => _setIndices[_currentSlot];
    /// <inheritdoc/>
    public int Registered
    {
        get
        {
            int n = 0;
            for (int i = 0; i < ArrayCount; i++) n += (int)_next[i] - _free[i].Count;
            return n;
        }
    }

    /// <summary>
    /// The array a texture of <paramref name="format"/> and <paramref name="kind"/> belongs in: integer formats (UINT, SINT) go to
    /// <see cref="BindlessKind.UintTexture2D"/> / <see cref="BindlessKind.IntTexture2D"/>, a depth texture read with comparison
    /// (<paramref name="shadow"/>) to <see cref="BindlessKind.Shadow2D"/> (a 2D array one to <see cref="BindlessKind.ShadowArray"/>), the rest by
    /// kind.
    /// </summary>
    public static BindlessKind KindFor(InternalFormat format, TextureKind kind = TextureKind.Texture2D, bool shadow = false)
    {
        if (shadow)
        {
            if (kind == TextureKind.Cube || !GlConventions.IsDepthFormat(format))
                throw new NotSupportedException($"bindless shadow textures are 2D or 2D array depth textures (got {kind} {format})");
            return kind == TextureKind.Texture2DArray ? BindlessKind.ShadowArray : BindlessKind.Shadow2D;
        }
        var scalar = ScalarOf(format);
        if (scalar != ScalarKind.Float)
        {
            if (kind != TextureKind.Texture2D)
                throw new NotSupportedException($"bindless integer textures are 2D only (got {kind} {format})");
            return scalar == ScalarKind.UInt ? BindlessKind.UintTexture2D : BindlessKind.IntTexture2D;
        }
        return kind switch
        {
            TextureKind.Cube => BindlessKind.Cube,
            TextureKind.Texture2DArray => BindlessKind.Texture2DArray,
            _ => BindlessKind.Texture2D,
        };
    }

    /// <summary>
    /// What a shader reads from a colour texture of <paramref name="format"/>: UInt for UINT formats, Int for SINT, Float otherwise (UNORM, SNORM,
    /// SFLOAT, SRGB, and depth: a depth-stencil format's stencil part is not sampled).
    /// </summary>
    public static ScalarKind ScalarOf(InternalFormat format) => format switch
    {
        InternalFormat.R8Ui or InternalFormat.R16Ui or InternalFormat.R32Ui
            or InternalFormat.Rgba8Ui or InternalFormat.Rgba16Ui or InternalFormat.Rgba32Ui => ScalarKind.UInt,
        InternalFormat.R16I or InternalFormat.R32I or InternalFormat.Rgba32I => ScalarKind.Int,
        _ => ScalarKind.Float,
    };

    /// <summary>
    /// Adds a texture. Its index is valid in the frame that is open (when one is) and in every frame that begins after the call.
    /// </summary>
    public uint Register(BindlessKind kind, in SampledTexture texture)
    {
        RenderJobs.AssertNotInJob();   // the table and its journal are the render thread's (docs/renderer-native.md 6.5)
        int k = (int)kind;
        uint index = _free[k].Count > 0 ? _free[k].Pop()
            : _next[k] < Capacity[k] ? _next[k]++
            : throw new InvalidOperationException($"bindless {kind} array is full ({Capacity[k]})");
        _journal.Add((kind, index, texture));
        Freed?.Taken(kind, index);
        return index;
    }

    /// <summary>
    /// Adds <paramref name="texture"/>'s full view with <paramref name="sampler"/> to the array its format and kind select (<see cref="KindFor"/>;
    /// <paramref name="shadow"/> for a depth texture read with comparison).
    /// </summary>
    public BindlessHandle Register(GpuTexture texture, GpuSampler sampler, bool shadow = false)
    {
        var kind = KindFor(texture.Desc.Format, texture.Desc.Kind, shadow);
        return new BindlessHandle(kind, Register(kind, new SampledTexture(sampler, texture.View())));
    }

    /// <summary>
    /// Replaces an entry's sampler or view (the upscaler's LOD bias changed, a texture was re-created). Takes effect in the frame that is open (for
    /// all of its draws: see the remarks) and in every frame that begins after the call.
    /// </summary>
    public void Update(BindlessKind kind, uint index, in SampledTexture texture)
    {
        RenderJobs.AssertNotInJob();
        _journal.Add((kind, index, texture));
    }

    /// <summary>
    /// As <see cref="Update(BindlessKind, uint, in SampledTexture)"/>.
    /// </summary>
    public void Update(BindlessHandle handle, in SampledTexture texture) => Update(handle.Kind, handle.Index, texture);

    /// <summary>
    /// Releases an index once the frames in flight are done with it. Draws recorded earlier in the open frame still read the entry; the slots'
    /// sets get the array's stand-in at their next begin (its view may be destroyed by then), or keep the old descriptor in an array without one.
    /// </summary>
    public void Free(BindlessKind kind, uint index)
    {
        RenderJobs.AssertNotInJob();
        _journal.Add((kind, index, default));   // a tombstone (null view)
        Freed?.Freed(kind, index, _device.Frames.FrameNumber);
        _device.Frames.DeferDelete(() => _free[(int)kind].Push(index));
    }

    /// <summary>
    /// As <see cref="Free(BindlessKind, uint)"/>.
    /// </summary>
    public void Free(BindlessHandle handle) => Free(handle.Kind, handle.Index);

    #endregion

    #region Frame updates

    /// <summary>
    /// The (array, index) pairs already written in the current <see cref="Apply"/> (each is written once, the newest entry).
    /// </summary>
    readonly HashSet<(BindlessKind, uint)> _written = [];

    /// <summary>
    /// The descriptors the current <see cref="Apply"/> writes.
    /// </summary>
    readonly List<(BindlessKind Kind, uint Index, SampledTexture Texture)> _writes = [];

    /// <summary>
    /// Per array: what a freed index reads from the next begin of each slot on (<see cref="SetStandIn"/>; none: the old descriptor stays).
    /// </summary>
    readonly SampledTexture[] _standIns = new SampledTexture[ArrayCount];

    /// <summary>
    /// Per slot: the indices freed while its frame was open, given the stand-in at its next begin.
    /// </summary>
    readonly List<(BindlessKind Kind, uint Index)>[] _vacated;

    /// <summary>
    /// Sets what a freed index of <paramref name="kind"/> reads once the frames that could read its texture have finished, in place of a
    /// descriptor whose view is destroyed: a draw that still holds the index samples the stand-in.
    /// </summary>
    public void SetStandIn(BindlessKind kind, in SampledTexture standIn) => _standIns[(int)kind] = standIn;

    /// <summary>
    /// At the start of a frame in <paramref name="slot"/> (the slot's previous frame has finished): the journal's new entries are written into the
    /// slot's set.
    /// </summary>
    public void BeginFrame(int slot)
    {
        _currentSlot = slot;
        Apply(slot, endOfFrame: false);
        _open = true;
    }

    /// <summary>
    /// At the end of the open frame, before it is submitted: what was registered or updated during the frame is written into the frame's set.
    /// Update-after-bind allows this while the set is bound in the command buffer being recorded, which is not pending yet.
    /// </summary>
    public void EndFrame()
    {
        if (!_open) return;
        _open = false;
        Apply(_currentSlot, endOfFrame: true);
    }

    /// <summary>
    /// Writes the journal's entries the slot has not seen, newest first, each (array, index) once: an entry overwritten later is skipped (its
    /// view may be gone by now). At a frame's begin an index freed since gets the array's stand-in (<see cref="SetStandIn"/>), so a draw that
    /// still reads it samples that rather than a destroyed view (issue #319). At the end of the open frame a freed index is still written: the
    /// frame's draws may have read it before the free, and its view lives until the frame has finished (deferred deletion); the stand-in follows
    /// at the slot's next begin.
    /// </summary>
    void Apply(int slot, bool endOfFrame)
    {
        long end = _journalBase + _journal.Count;
        long from = Math.Max(_applied[slot], _journalBase);
        _written.Clear();
        _writes.Clear();
        Collect(slot, (int)(from - _journalBase), (int)(end - from), endOfFrame);
        if (!endOfFrame) CollectVacated(slot);
        Write(slot);
        _applied[slot] = end;

        // Drop what every slot has seen.
        long seen = _applied.Min();
        int drop = (int)(seen - _journalBase);
        if (drop > 0)
        {
            _journal.RemoveRange(0, drop);
            _journalBase = seen;
        }
    }

    /// <summary>
    /// Gathers the entries <paramref name="n"/> from <paramref name="first"/> of the journal, newest first, into <see cref="_writes"/>.
    /// </summary>
    void Collect(int slot, int first, int n, bool endOfFrame)
    {
        for (int i = n - 1; i >= 0; i--)
        {
            var (kind, index, t) = _journal[first + i];
            if (t.IsNull)
            {
                if (endOfFrame) _vacated[slot].Add((kind, index));
                else if (_written.Add((kind, index))) StandInAt(kind, index);
                continue;
            }

            if (_written.Add((kind, index))) _writes.Add((kind, index, t));
        }
    }

    /// <summary>
    /// The stand-in for the indices freed while the slot's last frame was open, unless a newer entry took them.
    /// </summary>
    void CollectVacated(int slot)
    {
        foreach (var (kind, index) in _vacated[slot])
            if (_written.Add((kind, index))) StandInAt(kind, index);
        _vacated[slot].Clear();
    }

    /// <summary>
    /// Queues the array's stand-in at a freed index (nothing for an array without one).
    /// </summary>
    void StandInAt(BindlessKind kind, uint index)
    {
        var standIn = _standIns[(int)kind];
        if (!standIn.IsNull) _writes.Add((kind, index, standIn));
    }

    /// <summary>
    /// Writes <see cref="_writes"/> into the slot's set.
    /// </summary>
    void Write(int slot)
    {
        int count = _writes.Count;
        if (count == 0) return;
        var writes = new WriteDescriptorSet[count];
        var infos = new DescriptorImageInfo[count];
        fixed (DescriptorImageInfo* pi = infos)
        fixed (WriteDescriptorSet* pw = writes)
        {
            for (int i = 0; i < count; i++)
            {
                var (kind, index, t) = _writes[i];
                pi[i] = new DescriptorImageInfo(_device.SamplerAt(t.Sampler.Index), _device.ViewAt(t.View.Index), ImageLayout.General);
                pw[i] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _sets[slot],
                    DstBinding = (uint)kind,
                    DstArrayElement = index,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.CombinedImageSampler,
                    PImageInfo = &pi[i],
                };
            }
            _device.Vk.UpdateDescriptorSets(_device.Device, (uint)count, pw, 0, null);
        }
    }

    #endregion

    #region Disposal

    /// <summary>
    /// Destroys the pool (its sets go with it) and the layout.
    /// </summary>
    public void Dispose()
    {
        _device.Vk.DestroyDescriptorPool(_device.Device, _pool, null);
        _device.Vk.DestroyDescriptorSetLayout(_device.Device, Layout, null);
    }

    #endregion
}
