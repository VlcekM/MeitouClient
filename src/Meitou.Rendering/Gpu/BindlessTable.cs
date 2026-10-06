using Meitou.Rendering.Vulkan.Core;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gpu;

/// <summary>The arrays of the bindless set (set 0 of the native model, docs/renderer-native.md 2.6), by binding number.</summary>
public enum BindlessKind { Texture2D = 0, Texture2DArray = 1, Cube = 2, Shadow2D = 3 }

/// <summary>
/// The bindless texture table: combined image samplers in four partially bound, update-after-bind arrays (<c>sampler2D textures2D[]</c>,
/// <c>sampler2DArray textures2DArray[]</c>, <c>samplerCube texturesCube[]</c>, <c>sampler2DShadow shadowTextures[]</c>), one descriptor set per
/// frame slot so a slot is never written while a frame in flight reads it. Registrations and rewrites go into a journal that is replayed
/// into a slot's set when that slot's frame begins; a freed index is reused once the frames that could read it have finished. The sampler
/// is part of the entry, as GL's per-texture sampler state is. Render thread only.
/// </summary>
public sealed unsafe class BindlessTable : IDisposable
{
    public static readonly string[] GlslArrays = ["textures2D", "textures2DArray", "texturesCube", "shadowTextures"];

    readonly VulkanDevice device;
    readonly DescriptorPool pool;
    readonly DescriptorSet[] sets;
    readonly long[] applied;   // per slot: journal entries replayed so far
    readonly List<(BindlessKind Kind, uint Index, SampledTexture Texture)> journal = [];
    long journalBase;          // number of entries dropped from the front of the journal
    readonly Stack<uint>[] free = new Stack<uint>[4];
    readonly uint[] next = new uint[4];

    public BindlessTable(VulkanDevice device)
    {
        this.device = device;
        var props12 = new PhysicalDeviceVulkan12Properties { SType = StructureType.PhysicalDeviceVulkan12Properties };
        var props = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &props12 };
        device.Vk.GetPhysicalDeviceProperties2(device.PhysicalDevice, &props);
        uint limit = Math.Min(props12.MaxPerStageDescriptorUpdateAfterBindSamplers, props12.MaxDescriptorSetUpdateAfterBindSampledImages);
        // 2D textures get most of the room; the rest are few (terrain arrays, the atmosphere's cubes, shadow maps).
        Capacity = [Math.Min(16384u, limit / 2), Math.Min(1024u, limit / 8), Math.Min(256u, limit / 16), Math.Min(256u, limit / 16)];
        for (int i = 0; i < 4; i++) free[i] = new Stack<uint>();

        var bindings = stackalloc DescriptorSetLayoutBinding[4];
        var flags = stackalloc DescriptorBindingFlags[4];
        for (int i = 0; i < 4; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding((uint)i, DescriptorType.CombinedImageSampler, Capacity[i], ShaderStageFlags.All);
            flags[i] = DescriptorBindingFlags.PartiallyBoundBit | DescriptorBindingFlags.UpdateAfterBindBit | DescriptorBindingFlags.UpdateUnusedWhilePendingBit;
        }
        var flagInfo = new DescriptorSetLayoutBindingFlagsCreateInfo { SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo, BindingCount = 4, PBindingFlags = flags };
        var li = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo, PNext = &flagInfo, Flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit,
            BindingCount = 4, PBindings = bindings,
        };
        VulkanException.Check(device.Vk.CreateDescriptorSetLayout(device.Device, &li, null, out var layout), "vkCreateDescriptorSetLayout");
        Layout = layout;

        int slots = device.Frames.Count;
        var size = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, (Capacity[0] + Capacity[1] + Capacity[2] + Capacity[3]) * (uint)slots);
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
    public int Registered => (int)(next[0] + next[1] + next[2] + next[3]) - free.Sum(f => f.Count);

    /// <summary>Adds a texture; its index is valid from the next frame that begins (and in the current one when it began after this call).</summary>
    public uint Register(BindlessKind kind, in SampledTexture texture)
    {
        int k = (int)kind;
        uint index = free[k].Count > 0 ? free[k].Pop() : next[k] < Capacity[k] ? next[k]++ : throw new InvalidOperationException($"bindless {kind} array is full ({Capacity[k]})");
        journal.Add((kind, index, texture));
        return index;
    }

    /// <summary>Replaces an entry's sampler or view (the upscaler's LOD bias changed, a texture was re-created).</summary>
    public void Update(BindlessKind kind, uint index, in SampledTexture texture) => journal.Add((kind, index, texture));

    /// <summary>Releases an index once the frames in flight are done with it.</summary>
    public void Free(BindlessKind kind, uint index) => device.Frames.DeferDelete(() => free[(int)kind].Push(index));

    /// <summary>At the start of a frame in <paramref name="slot"/>: the journal's new entries are written into the slot's set.</summary>
    internal void BeginFrame(int slot)
    {
        currentSlot = slot;
        long end = journalBase + journal.Count;
        long from = Math.Max(applied[slot], journalBase);
        int n = (int)(end - from);
        if (n > 0)
        {
            var writes = new WriteDescriptorSet[n];
            var infos = new DescriptorImageInfo[n];
            fixed (DescriptorImageInfo* pi = infos)
            fixed (WriteDescriptorSet* pw = writes)
            {
                for (int i = 0; i < n; i++)
                {
                    var (kind, index, t) = journal[(int)(from - journalBase) + i];
                    pi[i] = new DescriptorImageInfo(t.Sampler, t.View, ImageLayout.General);
                    pw[i] = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet, DstSet = sets[slot], DstBinding = (uint)kind, DstArrayElement = index,
                        DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &pi[i],
                    };
                }
                device.Vk.UpdateDescriptorSets(device.Device, (uint)n, pw, 0, null);
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
