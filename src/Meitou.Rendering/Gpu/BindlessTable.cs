using Meitou.Rendering.Vulkan.Core;
using Meitou.Rendering.Vulkan.Shaders;
using Silk.NET.Vulkan;
using Sampler = Silk.NET.Vulkan.Sampler;

namespace Meitou.Rendering.Gpu;

/// <summary>The arrays of the bindless set (set 0 of the native model, docs/renderer-native.md 2.6), by binding number. The integer arrays
/// (<see cref="UTexture2D"/>, <see cref="ITexture2D"/>) take textures of UINT and SINT formats, which a float sampler may not read.</summary>
public enum BindlessKind { Texture2D = 0, Texture2DArray = 1, Cube = 2, Shadow2D = 3, UTexture2D = 4, ITexture2D = 5 }

/// <summary>An entry of the <see cref="BindlessTable"/>: the array and the index in it (what a shader reads through a push constant or a
/// material table).</summary>
public readonly record struct BindlessHandle(BindlessKind Kind, uint Index);

/// <summary>
/// The bindless texture table: combined image samplers in six partially bound, update-after-bind arrays (<c>sampler2D textures2D[]</c>,
/// <c>sampler2DArray textures2DArray[]</c>, <c>samplerCube texturesCube[]</c>, <c>sampler2DShadow shadowTextures[]</c>,
/// <c>usampler2D utextures2D[]</c>, <c>isampler2D itextures2D[]</c>; <see cref="GlslDeclarations"/>), one descriptor set per frame slot so a
/// slot is never written while a frame in flight reads it. Registrations and rewrites go into a journal that is replayed into a slot's set
/// when that slot's frame begins, and into the open frame's own set when that frame ends (before it is submitted): an index registered or
/// updated while a frame is open is valid in that frame. A freed index is reused once the frames that could read it have finished. The
/// sampler is part of the entry, as GL's per-texture sampler state is. Render thread only.
/// </summary>
/// <remarks>
/// Update-after-bind descriptors are read when the command buffer executes, so an <see cref="Update(BindlessKind, uint, in SampledTexture)"/>
/// made while a frame is open is seen by every draw of that frame that reads the index, including draws recorded before the call. To change
/// what a draw sees part-way through a frame (as GL does when a texture's view or sampler changes between two draws), register a new index
/// and free the old one.
/// </remarks>
public sealed unsafe class BindlessTable : IDisposable
{
    /// <summary>The number of arrays (bindings 0 to 5 of the set).</summary>
    public const int ArrayCount = 6;

    /// <summary>The GLSL names of the arrays, by binding (<see cref="BindlessKind"/> order).</summary>
    public static readonly string[] GlslArrays = ["textures2D", "textures2DArray", "texturesCube", "shadowTextures", "utextures2D", "itextures2D"];

    /// <summary>The GLSL types of the arrays' elements, by binding.</summary>
    public static readonly string[] GlslTypes = ["sampler2D", "sampler2DArray", "samplerCube", "sampler2DShadow", "usampler2D", "isampler2D"];

    /// <summary>The set's declarations for a native shader (set 0), with the extension <c>nonuniformEXT</c> needs. Index an array with
    /// <c>textures2D[nonuniformEXT(index)]</c> when the index may differ between invocations of a draw.</summary>
    public static readonly string GlslDeclarations = Declarations(0);

    /// <summary>As <see cref="GlslDeclarations"/> for the table bound at another set number.</summary>
    public static string Declarations(int set)
    {
        var sb = new System.Text.StringBuilder("#extension GL_EXT_nonuniform_qualifier : require\n");
        for (int i = 0; i < ArrayCount; i++) sb.Append($"layout(set = {set}, binding = {i}) uniform {GlslTypes[i]} {GlslArrays[i]}[];\n");
        return sb.ToString();
    }

    /// <summary>Checks a sampler a native shader declares in the table's set against the table (binding, element type, runtime-sized);
    /// throws naming the difference. <see cref="ShaderLibrary.Native"/> runs it for programs whose layout includes the table.</summary>
    public static void Check(SamplerInfo s, string program)
    {
        if (s.Binding < 0 || s.Binding >= ArrayCount)
            throw new InvalidOperationException($"{program}: sampler '{s.Name}' at binding {s.Binding} of the bindless set (bindings 0 to {ArrayCount - 1})");
        var (dim, arrayed, depth, scalar) = (BindlessKind)s.Binding switch
        {
            BindlessKind.Texture2DArray => (SamplerDimension.Dim2D, true, false, ScalarKind.Float),
            BindlessKind.Cube => (SamplerDimension.Cube, false, false, ScalarKind.Float),
            BindlessKind.Shadow2D => (SamplerDimension.Dim2D, false, true, ScalarKind.Float),
            BindlessKind.UTexture2D => (SamplerDimension.Dim2D, false, false, ScalarKind.UInt),
            BindlessKind.ITexture2D => (SamplerDimension.Dim2D, false, false, ScalarKind.Int),
            _ => (SamplerDimension.Dim2D, false, false, ScalarKind.Float),
        };
        if (s.Dimension != dim || s.Arrayed != arrayed || s.Depth != depth || s.SampledKind != scalar || s.Multisampled || s.ArrayLength >= 0)
            throw new InvalidOperationException($"{program}: sampler '{s.Name}' at binding {s.Binding} of the bindless set must be " +
                $"'{GlslTypes[s.Binding]} {GlslArrays[s.Binding]}[]' (got {s.SampledKind} {s.Dimension} arrayed={s.Arrayed} depth={s.Depth} length={s.ArrayLength})");
    }

    readonly VulkanDevice device;
    readonly DescriptorPool pool;
    readonly DescriptorSet[] sets;
    readonly long[] applied;   // per slot: journal entries replayed so far
    readonly List<(BindlessKind Kind, uint Index, SampledTexture Texture)> journal = [];
    long journalBase;          // number of entries dropped from the front of the journal
    readonly Stack<uint>[] free = new Stack<uint>[ArrayCount];
    readonly uint[] next = new uint[ArrayCount];
    bool open;                 // between BeginFrame and EndFrame of the frame in currentSlot

    public BindlessTable(VulkanDevice device)
    {
        this.device = device;
        var props12 = new PhysicalDeviceVulkan12Properties { SType = StructureType.PhysicalDeviceVulkan12Properties };
        var props = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &props12 };
        device.Vk.GetPhysicalDeviceProperties2(device.PhysicalDevice, &props);
        uint limit = Math.Min(props12.MaxPerStageDescriptorUpdateAfterBindSamplers, props12.MaxDescriptorSetUpdateAfterBindSampledImages);
        // 2D textures get most of the room; the rest are few (terrain arrays, the atmosphere's cubes, shadow maps, integer lookup textures
        // such as the terrain's blend cells). The shares add up to 15/16 of the limit.
        Capacity = [Math.Min(16384u, limit / 2), Math.Min(1024u, limit / 8), Math.Min(256u, limit / 16), Math.Min(256u, limit / 16),
            Math.Min(256u, limit / 16), Math.Min(256u, limit / 16)];
        for (int i = 0; i < ArrayCount; i++) free[i] = new Stack<uint>();

        var bindings = stackalloc DescriptorSetLayoutBinding[ArrayCount];
        var flags = stackalloc DescriptorBindingFlags[ArrayCount];
        for (int i = 0; i < ArrayCount; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding((uint)i, DescriptorType.CombinedImageSampler, Capacity[i], ShaderStageFlags.All);
            flags[i] = DescriptorBindingFlags.PartiallyBoundBit | DescriptorBindingFlags.UpdateAfterBindBit | DescriptorBindingFlags.UpdateUnusedWhilePendingBit;
        }
        var flagInfo = new DescriptorSetLayoutBindingFlagsCreateInfo { SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo, BindingCount = ArrayCount, PBindingFlags = flags };
        var li = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo, PNext = &flagInfo, Flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit,
            BindingCount = ArrayCount, PBindings = bindings,
        };
        VulkanException.Check(device.Vk.CreateDescriptorSetLayout(device.Device, &li, null, out var layout), "vkCreateDescriptorSetLayout");
        Layout = layout;

        int slots = device.Frames.Count;
        uint total = 0;
        foreach (var c in Capacity) total += c;
        var size = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, total * (uint)slots);
        var pi = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo, Flags = DescriptorPoolCreateFlags.UpdateAfterBindBit, MaxSets = (uint)slots, PoolSizeCount = 1, PPoolSizes = &size,
        };
        VulkanException.Check(device.Vk.CreateDescriptorPool(device.Device, &pi, null, out pool), "vkCreateDescriptorPool");
        sets = new DescriptorSet[slots];
        applied = new long[slots];
        for (int i = 0; i < slots; i++)
        {
            var ai = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = pool, DescriptorSetCount = 1, PSetLayouts = &layout };
            DescriptorSet set;
            VulkanException.Check(device.Vk.AllocateDescriptorSets(device.Device, &ai, &set), "vkAllocateDescriptorSets");
            sets[i] = set;
        }
    }

    /// <summary>Entries per array (<see cref="BindlessKind"/> order).</summary>
    public uint[] Capacity { get; }
    public DescriptorSetLayout Layout { get; }
    int currentSlot;
    /// <summary>The set of the frame being recorded (bind once per command buffer).</summary>
    public DescriptorSet Set => sets[currentSlot];
    public int Registered
    {
        get
        {
            int n = 0;
            for (int i = 0; i < ArrayCount; i++) n += (int)next[i] - free[i].Count;
            return n;
        }
    }

    /// <summary>The array a texture of <paramref name="format"/> and <paramref name="kind"/> belongs in: integer formats (UINT, SINT) go to
    /// <see cref="BindlessKind.UTexture2D"/> / <see cref="BindlessKind.ITexture2D"/>, a depth texture read with comparison
    /// (<paramref name="shadow"/>) to <see cref="BindlessKind.Shadow2D"/>, the rest by kind.</summary>
    public static BindlessKind KindFor(Format format, TextureKind kind = TextureKind.Texture2D, bool shadow = false)
    {
        if (shadow)
        {
            if (kind != TextureKind.Texture2D || !GlConventions.IsDepthFormat(format))
                throw new NotSupportedException($"bindless shadow textures are 2D depth textures (got {kind} {format})");
            return BindlessKind.Shadow2D;
        }
        var scalar = ScalarOf(format);
        if (scalar != ScalarKind.Float)
        {
            if (kind != TextureKind.Texture2D) throw new NotSupportedException($"bindless integer textures are 2D only (got {kind} {format})");
            return scalar == ScalarKind.UInt ? BindlessKind.UTexture2D : BindlessKind.ITexture2D;
        }
        return kind switch { TextureKind.Cube => BindlessKind.Cube, TextureKind.Texture2DArray => BindlessKind.Texture2DArray, _ => BindlessKind.Texture2D };
    }

    /// <summary>What a shader reads from a colour texture of <paramref name="format"/>: UInt for UINT formats, Int for SINT, Float otherwise
    /// (UNORM, SNORM, SFLOAT, SRGB, and depth: a depth-stencil format's stencil part is not sampled).</summary>
    public static ScalarKind ScalarOf(Format format) => Scalars.TryGetValue(format, out var s) ? s : ScalarByName(format);

    /// <summary><see cref="ScalarByName"/> of every named format, worked out once: <see cref="ScalarOf"/> runs for every
    /// <c>IGlInterop.Bindless</c> call, and the enum's name is a string allocation and three searches (~0.1 us).</summary>
    static readonly System.Collections.Frozen.FrozenDictionary<Format, ScalarKind> Scalars =
        System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(Enum.GetValues<Format>().Distinct(), f => f, ScalarByName);

    static ScalarKind ScalarByName(Format format)
    {
        var name = format.ToString();
        if (GlConventions.IsDepthFormat(format) || name.EndsWith("S8Uint", StringComparison.Ordinal)) return ScalarKind.Float;   // depth(-stencil)
        return name.Contains("Uint", StringComparison.Ordinal) ? ScalarKind.UInt
            : name.Contains("Sint", StringComparison.Ordinal) ? ScalarKind.Int
            : ScalarKind.Float;
    }

    /// <summary>Adds a texture. Its index is valid in the frame that is open (when one is) and in every frame that begins after the call.</summary>
    public uint Register(BindlessKind kind, in SampledTexture texture)
    {
        int k = (int)kind;
        uint index = free[k].Count > 0 ? free[k].Pop() : next[k] < Capacity[k] ? next[k]++ : throw new InvalidOperationException($"bindless {kind} array is full ({Capacity[k]})");
        journal.Add((kind, index, texture));
        return index;
    }

    /// <summary>Adds <paramref name="texture"/>'s full view with <paramref name="sampler"/> to the array its format and kind select
    /// (<see cref="KindFor"/>; <paramref name="shadow"/> for a depth texture read with comparison).</summary>
    public BindlessHandle Register(Texture texture, Sampler sampler, bool shadow = false)
    {
        var kind = KindFor(texture.Desc.Format, texture.Desc.Kind, shadow);
        return new BindlessHandle(kind, Register(kind, new SampledTexture(sampler, texture.View(), texture.Image)));
    }

    /// <summary>Replaces an entry's sampler or view (the upscaler's LOD bias changed, a texture was re-created). Takes effect in the frame
    /// that is open (for all of its draws: see the remarks) and in every frame that begins after the call.</summary>
    public void Update(BindlessKind kind, uint index, in SampledTexture texture) => journal.Add((kind, index, texture));

    /// <summary>As <see cref="Update(BindlessKind, uint, in SampledTexture)"/>.</summary>
    public void Update(BindlessHandle handle, in SampledTexture texture) => Update(handle.Kind, handle.Index, texture);

    /// <summary>Releases an index once the frames in flight are done with it. Draws recorded earlier in the open frame still read the
    /// entry; slots that have not seen it yet skip it (its view may be destroyed before they replay the journal).</summary>
    public void Free(BindlessKind kind, uint index)
    {
        journal.Add((kind, index, default));   // a tombstone (null view)
        device.Frames.DeferDelete(() => free[(int)kind].Push(index));
    }

    /// <summary>As <see cref="Free(BindlessKind, uint)"/>.</summary>
    public void Free(BindlessHandle handle) => Free(handle.Kind, handle.Index);

    /// <summary>At the start of a frame in <paramref name="slot"/> (the slot's previous frame has finished): the journal's new entries are
    /// written into the slot's set.</summary>
    internal void BeginFrame(int slot)
    {
        currentSlot = slot;
        Apply(slot, endOfFrame: false);
        open = true;
    }

    /// <summary>At the end of the open frame, before it is submitted: what was registered or updated during the frame is written into the
    /// frame's set. Update-after-bind allows this while the set is bound in the command buffer being recorded, which is not pending yet.</summary>
    internal void EndFrame()
    {
        if (!open) return;
        open = false;
        Apply(currentSlot, endOfFrame: true);
    }

    readonly HashSet<(BindlessKind, uint)> written = [];

    /// <summary>
    /// Writes the journal's entries the slot has not seen, newest first, each (array, index) once: an entry overwritten later is skipped (its
    /// view may be gone by now). At a frame's begin an index freed later is skipped too. At the end of the open frame it is still written:
    /// the frame's draws may have read it before the free, and its view lives until the frame has finished (deferred deletion).
    /// </summary>
    void Apply(int slot, bool endOfFrame)
    {
        long end = journalBase + journal.Count;
        long from = Math.Max(applied[slot], journalBase);
        int n = (int)(end - from);
        if (n > 0)
        {
            var writes = new WriteDescriptorSet[n];
            var infos = new DescriptorImageInfo[n];
            int count = 0;
            written.Clear();
            fixed (DescriptorImageInfo* pi = infos)
            fixed (WriteDescriptorSet* pw = writes)
            {
                for (int i = n - 1; i >= 0; i--)
                {
                    var (kind, index, t) = journal[(int)(from - journalBase) + i];
                    if (t.IsNull)
                    {
                        if (!endOfFrame) written.Add((kind, index));
                        continue;
                    }
                    if (!written.Add((kind, index))) continue;
                    pi[count] = new DescriptorImageInfo(t.Sampler, t.View, ImageLayout.General);
                    pw[count] = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet, DstSet = sets[slot], DstBinding = (uint)kind, DstArrayElement = index,
                        DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &pi[count],
                    };
                    count++;
                }
                if (count > 0) device.Vk.UpdateDescriptorSets(device.Device, (uint)count, pw, 0, null);
            }
        }
        applied[slot] = end;
        // Drop what every slot has seen.
        long seen = applied.Min();
        int drop = (int)(seen - journalBase);
        if (drop > 0) { journal.RemoveRange(0, drop); journalBase = seen; }
    }

    public void Dispose()
    {
        device.Vk.DestroyDescriptorPool(device.Device, pool, null);
        device.Vk.DestroyDescriptorSetLayout(device.Device, Layout, null);
    }
}
