using Meitou.Game.Rendering.Bench;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Game.Rendering.Gpu;

/// <summary>
/// The bench's triangle size histogram (<c>--bench-tris</c>, docs/bench.md "Triangle size histogram"): while <see cref="Begin"/> is in effect the
/// colour programs of the main view (<see cref="TriangleBins.CategoryOf"/>) are made with a fragment shader that runs the original and then adds one
/// to a counter in a storage buffer, by category and by the area of the sample's triangle (<see cref="TriangleBins.Instrument"/>). The buffer is
/// reached by its device address, written into the shader text, so no renderer's layout or sets change. Normal programs and pipelines are never
/// touched: the instrumented ones are separate pipelines (<see cref="PipelineLibrary.GetWithFragment"/>, swapped in at
/// <see cref="CommandList.BindPipeline(GpuPipeline)"/>), made only while it is on.
/// </summary>
public sealed unsafe class TriangleCounter : IDisposable
{
    /// <summary>
    /// The counter buffer's size: one uint per category and fine size bin.
    /// </summary>
    static readonly ulong Bytes = (ulong)(TriangleBins.Categories.Length * TriangleBins.Fine * sizeof(uint));

    /// <summary>
    /// The context the counters are recorded and read through.
    /// </summary>
    readonly GpuContext _ctx;
    /// <summary>
    /// The Vulkan API entry points.
    /// </summary>
    readonly Vk _vk;
    /// <summary>
    /// The device the buffer and modules are made on.
    /// </summary>
    readonly VulkanDevice _device;
    /// <summary>
    /// The counter buffer (storage, reached by device address).
    /// </summary>
    readonly Silk.NET.Vulkan.Buffer _buffer;
    /// <summary>
    /// The counter buffer's handle in the device's buffer table.
    /// </summary>
    readonly GpuBuffer _handle;
    /// <summary>
    /// The counter buffer's own device-local memory.
    /// </summary>
    readonly DeviceMemory _memory;
    /// <summary>
    /// The counter buffer's device address, written into the instrumented shaders.
    /// </summary>
    readonly ulong _address;
    /// <summary>
    /// Where the counters are copied for reading.
    /// </summary>
    readonly ReadbackBuffer _readback;
    /// <summary>
    /// The counting fragment module of each program asked for (0 when the program is not counted).
    /// </summary>
    readonly Dictionary<GpuProgram, ShaderModule> _modules = [];
    /// <summary>
    /// Backs <see cref="Skipped"/>.
    /// </summary>
    readonly List<string> _skipped = [];
    /// <summary>
    /// Backs <see cref="Counted"/>.
    /// </summary>
    readonly List<string> _counted = [];

    /// <summary>
    /// Programs that were given a counting fragment shader.
    /// </summary>
    public IReadOnlyList<string> Counted => _counted;

    /// <summary>
    /// Programs of a counted category whose text could not be instrumented (their triangles are missing from the histogram).
    /// </summary>
    public IReadOnlyList<string> Skipped => _skipped;

    /// <summary>
    /// Whether the device has fragment shader barycentrics and buffer device address.
    /// </summary>
    public static bool Supported(GpuContext ctx) => ctx.Device.HasFragmentBarycentric && ctx.Device.HasBufferDeviceAddress;

    /// <summary>
    /// Makes the counter buffer in its own memory and the readback; throws when the device lacks what the counting needs.
    /// </summary>
    public TriangleCounter(GpuContext ctx)
    {
        if (!Supported(ctx)) throw new NotSupportedException("the device has no fragment shader barycentrics or buffer device address");

        this._ctx = ctx;
        _device = ctx.Device;
        _vk = _device.Vk;
        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = Bytes,
            SharingMode = SharingMode.Exclusive,
            Usage = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit
                | BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
        };
        VulkanException.Check(_vk.CreateBuffer(_device.Device, in info, null, out _buffer), "vkCreateBuffer(triangle counters)");
        _vk.GetBufferMemoryRequirements(_device.Device, _buffer, out var req);
        _vk.GetPhysicalDeviceMemoryProperties(_device.PhysicalDevice, out var props);

        uint type = uint.MaxValue;
        for (uint i = 0; i < props.MemoryTypeCount; i++)
        {
            if ((req.MemoryTypeBits & (1u << (int)i)) != 0 && (props.MemoryTypes[(int)i].PropertyFlags & MemoryPropertyFlags.DeviceLocalBit) != 0)
            {
                type = i;
                break;
            }
        }

        if (type == uint.MaxValue) throw new VulkanException("no device-local memory type for the triangle counters");

        var flags = new MemoryAllocateFlagsInfo { SType = StructureType.MemoryAllocateFlagsInfo, Flags = MemoryAllocateFlags.DeviceAddressBit };
        var alloc = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo, AllocationSize = req.Size, MemoryTypeIndex = type, PNext = &flags,
        };
        VulkanException.Check(_vk.AllocateMemory(_device.Device, in alloc, null, out _memory), "vkAllocateMemory(triangle counters)");
        VulkanException.Check(_vk.BindBufferMemory(_device.Device, _buffer, _memory, 0), "vkBindBufferMemory(triangle counters)");
        _handle = new GpuBuffer(_device.AddBuffer(new BufferEntry(_buffer, null)), Bytes);
        var addressInfo = new BufferDeviceAddressInfo { SType = StructureType.BufferDeviceAddressInfo, Buffer = _buffer };
        _address = _vk.GetBufferDeviceAddress(_device.Device, in addressInfo);
        _readback = ReadbackBuffer.Create(ctx, Bytes, "triangle counters readback");
    }

    /// <summary>
    /// Zeroes the counters (in the frame being recorded, before its draws) and makes the pipelines created from now on count.
    /// </summary>
    public void Begin()
    {
        Clear();
        CommandList.PipelineSubstitute = Substitute;
    }

    /// <summary>
    /// Back to the normal pipelines.
    /// </summary>
    public void End() => CommandList.PipelineSubstitute = null;

    /// <summary>
    /// Zeroes the counters in the frame being recorded.
    /// </summary>
    public void Clear()
    {
        _ctx.EnsureFrame();
        var list = _ctx.BeginNative("triangle counters clear");
        list.FillBuffer(_handle, 0, Bytes, 0);
        _ctx.EndNative(list);
    }

    /// <summary>
    /// The pipeline with the program's counting fragment shader, or the pipeline itself when the program is not counted
    /// (<see cref="CommandList.PipelineSubstitute"/>).
    /// </summary>
    GpuPipeline Substitute(GpuPipeline pipeline)
    {
        var module = ModuleFor(pipeline.Desc.Program);
        return module.Handle == 0 ? pipeline : _ctx.Pipelines.GetWithFragment(pipeline.Desc, module);
    }

    /// <summary>
    /// The program's counting fragment module, made under a lock once per program (called from the recording threads).
    /// </summary>
    ShaderModule ModuleFor(GpuProgram program)
    {
        lock (_modules)
            return ModuleForLocked(program);
    }

    /// <summary>
    /// The program's counting fragment module: instrumented from its fragment source when its name is of a counted category; 0 otherwise or when the
    /// text could not be instrumented.
    /// </summary>
    ShaderModule ModuleForLocked(GpuProgram program)
    {
        if (_modules.TryGetValue(program, out var module)) return module;

        int category = TriangleBins.CategoryOf(program.Name);
        if (category >= 0 && _ctx.Shaders.Backend(program).FragmentSource is { } source)
        {
            if (TriangleBins.Instrument(source, category, _address) is { } text)
            {
                module = _ctx.Shaders.Backend(program).CreateModule(_ctx.Shaders.Compiler.CompileNative(text, fragment: true));
                _counted.Add(TriangleBins.EarlyTests(source, category) ? program.Name : program.Name + " (late depth test)");
            }
            else _skipped.Add(program.Name);
        }

        return _modules[program] = module;
    }

    /// <summary>
    /// The samples counted since <see cref="Clear"/>, per category, as the histogram of their triangles (the frame in progress is submitted and
    /// waited for).
    /// </summary>
    public Dictionary<string, SizeHistogram> Read()
    {
        _ctx.EnsureFrame();
        var list = _ctx.BeginNative("triangle counters read");
        list.CopyBuffer(_handle, _readback.Handle, new GpuBufferCopy(0, 0, Bytes));
        _ctx.EndNative(list);
        _ctx.Finish();

        var bytes = _readback.Read(0, Bytes);
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

    /// <summary>
    /// Ends the counting, waits for the device, and releases the modules, the readback and the buffer.
    /// </summary>
    public void Dispose()
    {
        End();
        _ctx.Finish();
        foreach (var m in _modules.Values)
            if (m.Handle != 0) _vk.DestroyShaderModule(_device.Device, m, null);

        _readback.Dispose();
        _vk.DestroyBuffer(_device.Device, _buffer, null);
        _vk.FreeMemory(_device.Device, _memory, null);
    }
}
