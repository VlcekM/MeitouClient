using Meitou.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gpu;

/// <summary>
/// The bench's triangle size histogram (<c>--bench-tris</c>, docs/bench.md "Triangle size histogram"): while <see cref="Begin"/> is in effect the
/// colour programs of the main view (<see cref="TriangleBins.CategoryOf"/>) are made with a fragment shader that runs the original and then adds
/// one to a counter in a storage buffer, by category and by the area of the sample's triangle (<see cref="TriangleBins.Instrument"/>). The buffer
/// is reached by its device address, written into the shader text, so no renderer's layout or sets change. Normal programs and pipelines are
/// never touched: the instrumented ones are separate pipelines (<see cref="PipelineLibrary.GetWithFragment"/>, swapped in at <see cref="CommandList.BindPipeline(GraphicsPipeline)"/>), made only while it is on.
/// </summary>
public sealed unsafe class TriangleCounter : IDisposable
{
    static readonly ulong Bytes = (ulong)(TriangleBins.Categories.Length * TriangleBins.Fine * sizeof(uint));

    readonly GpuContext ctx;
    readonly Vk vk;
    readonly VulkanDevice device;
    readonly Silk.NET.Vulkan.Buffer buffer;
    readonly DeviceMemory memory;
    readonly ulong address;
    readonly ReadbackBuffer readback;
    readonly Dictionary<ShaderProgram, ShaderModule> modules = [];
    readonly List<string> skipped = [];
    readonly List<string> counted = [];

    /// <summary>Programs that were given a counting fragment shader.</summary>
    public IReadOnlyList<string> Counted => counted;

    public static bool Supported(GpuContext ctx) => ctx.Device.HasFragmentBarycentric && ctx.Device.HasBufferDeviceAddress;

    /// <summary>Programs of a counted category whose text could not be instrumented (their triangles are missing from the histogram).</summary>
    public IReadOnlyList<string> Skipped => skipped;

    public TriangleCounter(GpuContext ctx)
    {
        if (!Supported(ctx)) throw new NotSupportedException("the device has no fragment shader barycentrics or buffer device address");
        this.ctx = ctx;
        device = ctx.Device;
        vk = device.Vk;
        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo, Size = Bytes, SharingMode = SharingMode.Exclusive,
            Usage = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
        };
        VulkanException.Check(vk.CreateBuffer(device.Device, in info, null, out buffer), "vkCreateBuffer(triangle counters)");
        vk.GetBufferMemoryRequirements(device.Device, buffer, out var req);
        vk.GetPhysicalDeviceMemoryProperties(device.PhysicalDevice, out var props);
        uint type = uint.MaxValue;
        for (uint i = 0; i < props.MemoryTypeCount; i++)
            if ((req.MemoryTypeBits & (1u << (int)i)) != 0 && (props.MemoryTypes[(int)i].PropertyFlags & MemoryPropertyFlags.DeviceLocalBit) != 0) { type = i; break; }
        if (type == uint.MaxValue) throw new VulkanException("no device-local memory type for the triangle counters");
        var flags = new MemoryAllocateFlagsInfo { SType = StructureType.MemoryAllocateFlagsInfo, Flags = MemoryAllocateFlags.DeviceAddressBit };
        var alloc = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = req.Size, MemoryTypeIndex = type, PNext = &flags };
        VulkanException.Check(vk.AllocateMemory(device.Device, in alloc, null, out memory), "vkAllocateMemory(triangle counters)");
        VulkanException.Check(vk.BindBufferMemory(device.Device, buffer, memory, 0), "vkBindBufferMemory(triangle counters)");
        var addressInfo = new BufferDeviceAddressInfo { SType = StructureType.BufferDeviceAddressInfo, Buffer = buffer };
        address = vk.GetBufferDeviceAddress(device.Device, in addressInfo);
        readback = ReadbackBuffer.Create(ctx, Bytes, "triangle counters readback");
    }

    /// <summary>Zeroes the counters (in the frame being recorded, before its draws) and makes the pipelines created from now on count.</summary>
    public void Begin()
    {
        Clear();
        CommandList.PipelineSubstitute = Substitute;
    }

    /// <summary>Back to the normal pipelines.</summary>
    public void End() => CommandList.PipelineSubstitute = null;

    /// <summary>Zeroes the counters in the frame being recorded.</summary>
    public void Clear()
    {
        ctx.EnsureFrame();
        var list = ctx.BeginNative("triangle counters clear");
        list.FillBuffer(buffer, 0, Bytes, 0);
        ctx.EndNative(list);
    }

    GraphicsPipeline Substitute(GraphicsPipeline pipeline)
    {
        var module = ModuleFor(pipeline.Desc.Program);
        return module.Handle == 0 ? pipeline : ctx.Pipelines.GetWithFragment(pipeline.Desc, module);
    }

    // Called from the recording threads: the modules are made under a lock (once per program).
    ShaderModule ModuleFor(ShaderProgram program)
    {
        lock (modules) return ModuleForLocked(program);
    }

    ShaderModule ModuleForLocked(ShaderProgram program)
    {
        if (modules.TryGetValue(program, out var module)) return module;
        int category = TriangleBins.CategoryOf(program.Name);
        if (category >= 0 && program.FragmentSource is { } source)
        {
            if (TriangleBins.Instrument(source, category, address) is { } text)
            {
                module = program.CreateModule(ctx.Shaders.Compiler.CompileNative(text, fragment: true));
                counted.Add(TriangleBins.EarlyTests(source, category) ? program.Name : program.Name + " (late depth test)");
            }
            else skipped.Add(program.Name);
        }
        return modules[program] = module;
    }

    /// <summary>The samples counted since <see cref="Clear"/>, per category, as the histogram of their triangles (the frame in progress is submitted and waited for).</summary>
    public Dictionary<string, SizeHistogram> Read()
    {
        ctx.EnsureFrame();
        var list = ctx.BeginNative("triangle counters read");
        list.CopyBuffer(buffer, readback.Handle, new BufferCopy(0, 0, Bytes));
        ctx.EndNative(list);
        ctx.Finish();
        var bytes = readback.Read(0, Bytes);
        var counts = new uint[TriangleBins.Categories.Length * TriangleBins.Fine];
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bytes).CopyTo(counts);
        var result = new Dictionary<string, SizeHistogram>();
        for (int c = 0; c < TriangleBins.Categories.Length; c++)
        {
            var h = TriangleBins.Fold(counts.AsSpan(c * TriangleBins.Fine, TriangleBins.Fine));
            if (h.TotalPixels > 0) result[TriangleBins.Categories[c]] = h;
        }
        return result;
    }

    public void Dispose()
    {
        End();
        ctx.Finish();
        foreach (var m in modules.Values) if (m.Handle != 0) vk.DestroyShaderModule(device.Device, m, null);
        readback.Dispose();
        vk.DestroyBuffer(device.Device, buffer, null);
        vk.FreeMemory(device.Device, memory, null);
    }
}
