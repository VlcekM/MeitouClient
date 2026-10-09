using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Rendering.Gpu;
using Meitou.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Rendering.Gi;

/// <summary>One triangle geometry of a bottom-level build: positions (three floats at the start of each vertex) and 32-bit indices, both
/// reached by device address (buffers made with <see cref="BufferUse.RayInput"/>).</summary>
public readonly record struct TriangleGeometry(ulong VertexAddress, uint VertexStride, uint VertexCount, ulong IndexAddress, uint TriangleCount, bool Opaque = true);

/// <summary>A TLAS instance as Vulkan lays it out (<c>VkAccelerationStructureInstanceKHR</c>, 64 bytes): a 3 x 4 row-major transform, the
/// 24-bit custom index with the 8-bit mask, the 24-bit hit group offset with 8 bits of flags, and the BLAS's device address.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct InstanceRecord
{
    public fixed float Transform[12];
    public uint CustomIndexAndMask;
    public uint OffsetAndFlags;
    public ulong Reference;

    public const int Size = 64;

    /// <summary>An instance of <paramref name="blas"/> placed by <paramref name="world"/> (a System.Numerics row-vector matrix: the translation in
    /// row 4), visible to rays whose cull mask shares a bit with <paramref name="mask"/>.</summary>
    public static InstanceRecord Of(in Matrix4x4 world, uint customIndex, byte mask, ulong blas, GeometryInstanceFlagsKHR flags = GeometryInstanceFlagsKHR.TriangleFacingCullDisableBitKhr)
    {
        var r = new InstanceRecord { CustomIndexAndMask = (customIndex & 0xFFFFFF) | ((uint)mask << 24), OffsetAndFlags = (uint)flags << 24, Reference = blas };
        // Vulkan's rows are the transposed columns of the row-vector matrix.
        r.Transform[0] = world.M11; r.Transform[1] = world.M21; r.Transform[2] = world.M31; r.Transform[3] = world.M41;
        r.Transform[4] = world.M12; r.Transform[5] = world.M22; r.Transform[6] = world.M32; r.Transform[7] = world.M42;
        r.Transform[8] = world.M13; r.Transform[9] = world.M23; r.Transform[10] = world.M33; r.Transform[11] = world.M43;
        return r;
    }
}

/// <summary>
/// An acceleration structure (docs/render-gi.md "Acceleration structures"): its storage buffer, its handle and device address. Built into a
/// command list (<see cref="BuildBottom"/>, <see cref="BuildTop"/>) with a scratch buffer kept on it, freed at <see cref="Dispose"/> once the
/// frames that may still trace it have finished. Needs <see cref="VulkanDevice.HasRayQuery"/>. Render thread only.
/// </summary>
public sealed unsafe class AccelerationStructure : IDisposable
{
    readonly VulkanDevice device;
    readonly GpuBuffer storage;
    GpuBuffer? scratch;

    public AccelerationStructureKHR Handle { get; }
    public ulong Address { get; }
    public ulong Bytes => storage.Size + (scratch?.Size ?? 0);
    public AccelerationStructureTypeKHR Type { get; }

    AccelerationStructure(VulkanDevice device, AccelerationStructureTypeKHR type, ulong size, string name)
    {
        this.device = device;
        Type = type;
        var ext = device.AccelerationStructures ?? throw new InvalidOperationException("the device has no ray queries");
        storage = device.Allocator.CreateBuffer(size, BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit, MemoryKind.DeviceLocal, name);
        var info = new AccelerationStructureCreateInfoKHR
        {
            SType = StructureType.AccelerationStructureCreateInfoKhr, Buffer = storage.Buffer, Offset = 0, Size = size, Type = type,
        };
        AccelerationStructureKHR handle;
        VulkanException.Check(ext.CreateAccelerationStructure(device.Device, &info, null, &handle), "vkCreateAccelerationStructureKHR");
        Handle = handle;
        var addressInfo = new AccelerationStructureDeviceAddressInfoKHR { SType = StructureType.AccelerationStructureDeviceAddressInfoKhr, AccelerationStructure = handle };
        Address = ext.GetAccelerationStructureDeviceAddress(device.Device, &addressInfo);
    }

    /// <summary>A bottom-level structure over <paramref name="geometries"/>, built into <paramref name="cmd"/> (prefer fast tracing, compaction not used).</summary>
    public static AccelerationStructure BuildBottom(VulkanDevice device, CommandList cmd, ReadOnlySpan<TriangleGeometry> geometries, string name)
    {
        int n = geometries.Length;
        var geos = stackalloc AccelerationStructureGeometryKHR[Math.Max(n, 1)];
        var ranges = stackalloc AccelerationStructureBuildRangeInfoKHR[Math.Max(n, 1)];
        var counts = stackalloc uint[Math.Max(n, 1)];
        for (int i = 0; i < n; i++)
        {
            var g = geometries[i];
            geos[i] = new AccelerationStructureGeometryKHR
            {
                SType = StructureType.AccelerationStructureGeometryKhr,
                GeometryType = GeometryTypeKHR.TrianglesKhr,
                Flags = g.Opaque ? GeometryFlagsKHR.OpaqueBitKhr : 0,
                Geometry = new AccelerationStructureGeometryDataKHR
                {
                    Triangles = new AccelerationStructureGeometryTrianglesDataKHR
                    {
                        SType = StructureType.AccelerationStructureGeometryTrianglesDataKhr,
                        VertexFormat = Format.R32G32B32Sfloat,
                        VertexData = new DeviceOrHostAddressConstKHR { DeviceAddress = g.VertexAddress },
                        VertexStride = g.VertexStride,
                        MaxVertex = Math.Max(g.VertexCount, 1) - 1,
                        IndexType = IndexType.Uint32,
                        IndexData = new DeviceOrHostAddressConstKHR { DeviceAddress = g.IndexAddress },
                    },
                },
            };
            ranges[i] = new AccelerationStructureBuildRangeInfoKHR { PrimitiveCount = g.TriangleCount };
            counts[i] = g.TriangleCount;
        }
        return Build(device, cmd, AccelerationStructureTypeKHR.BottomLevelKhr, geos, (uint)n, ranges, counts,
            BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr, name);
    }

    /// <summary>The capacity of a top-level structure made by <see cref="CreateTop"/>.</summary>
    public uint MaxInstances { get; private set; }

    /// <summary>A top-level structure for up to <paramref name="maxInstances"/> instances with its scratch kept, rebuilt in place every frame
    /// (<see cref="RebuildTop"/>).</summary>
    public static AccelerationStructure CreateTop(VulkanDevice device, uint maxInstances, string name)
    {
        var geo = TopGeometry(0);
        var build = new AccelerationStructureBuildGeometryInfoKHR
        {
            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr, Type = AccelerationStructureTypeKHR.TopLevelKhr,
            Flags = BuildAccelerationStructureFlagsKHR.PreferFastBuildBitKhr, Mode = BuildAccelerationStructureModeKHR.BuildKhr, GeometryCount = 1, PGeometries = &geo,
        };
        var sizes = new AccelerationStructureBuildSizesInfoKHR { SType = StructureType.AccelerationStructureBuildSizesInfoKhr };
        uint max = maxInstances;
        device.AccelerationStructures!.GetAccelerationStructureBuildSizes(device.Device, AccelerationStructureBuildTypeKHR.DeviceKhr, &build, &max, &sizes);
        var top = new AccelerationStructure(device, AccelerationStructureTypeKHR.TopLevelKhr, Math.Max(sizes.AccelerationStructureSize, 256), name) { MaxInstances = maxInstances };
        top.scratch = device.Allocator.CreateBuffer(sizes.BuildScratchSize + device.ScratchAlignment, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit,
            MemoryKind.DeviceLocal, name + " scratch");
        return top;
    }

    /// <summary>Rebuilds a structure of <see cref="CreateTop"/> over <paramref name="count"/> (at most <see cref="MaxInstances"/>) <see cref="InstanceRecord"/>s
    /// at <paramref name="instancesAddress"/> into <paramref name="cmd"/>.</summary>
    public void RebuildTop(CommandList cmd, ulong instancesAddress, uint count)
    {
        if (count > MaxInstances) throw new ArgumentOutOfRangeException(nameof(count), $"{count} instances, room for {MaxInstances}");
        var geo = TopGeometry(instancesAddress);
        var build = new AccelerationStructureBuildGeometryInfoKHR
        {
            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr, Type = AccelerationStructureTypeKHR.TopLevelKhr,
            Flags = BuildAccelerationStructureFlagsKHR.PreferFastBuildBitKhr, Mode = BuildAccelerationStructureModeKHR.BuildKhr, GeometryCount = 1, PGeometries = &geo,
            DstAccelerationStructure = Handle, ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = ScratchAddress() },
        };
        var range = new AccelerationStructureBuildRangeInfoKHR { PrimitiveCount = count };
        var rangePointer = &range;
        device.AccelerationStructures!.CmdBuildAccelerationStructures(cmd.Handle, 1, &build, &rangePointer);
    }

    ulong ScratchAddress()
    {
        var info = new BufferDeviceAddressInfo { SType = StructureType.BufferDeviceAddressInfo, Buffer = scratch!.Buffer };
        ulong address = device.Vk.GetBufferDeviceAddress(device.Device, in info), align = device.ScratchAlignment;
        return (address + align - 1) / align * align;
    }

    static AccelerationStructureGeometryKHR TopGeometry(ulong instancesAddress) => new()
    {
        SType = StructureType.AccelerationStructureGeometryKhr,
        GeometryType = GeometryTypeKHR.InstancesKhr,
        Geometry = new AccelerationStructureGeometryDataKHR
        {
            Instances = new AccelerationStructureGeometryInstancesDataKHR
            {
                SType = StructureType.AccelerationStructureGeometryInstancesDataKhr,
                ArrayOfPointers = false,
                Data = new DeviceOrHostAddressConstKHR { DeviceAddress = instancesAddress },
            },
        },
    };

    static AccelerationStructure Build(VulkanDevice device, CommandList cmd, AccelerationStructureTypeKHR type, AccelerationStructureGeometryKHR* geos, uint geometryCount,
        AccelerationStructureBuildRangeInfoKHR* ranges, uint* maxPrimitives, BuildAccelerationStructureFlagsKHR flags, string name)
    {
        var ext = device.AccelerationStructures!;
        var build = new AccelerationStructureBuildGeometryInfoKHR
        {
            SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
            Type = type, Flags = flags, Mode = BuildAccelerationStructureModeKHR.BuildKhr,
            GeometryCount = geometryCount, PGeometries = geos,
        };
        var sizes = new AccelerationStructureBuildSizesInfoKHR { SType = StructureType.AccelerationStructureBuildSizesInfoKhr };
        ext.GetAccelerationStructureBuildSizes(device.Device, AccelerationStructureBuildTypeKHR.DeviceKhr, &build, maxPrimitives, &sizes);
        var structure = new AccelerationStructure(device, type, Math.Max(sizes.AccelerationStructureSize, 256), name);
        structure.scratch = device.Allocator.CreateBuffer(sizes.BuildScratchSize + device.ScratchAlignment, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.ShaderDeviceAddressBit,
            MemoryKind.DeviceLocal, name + " scratch");
        build.DstAccelerationStructure = structure.Handle;
        build.ScratchData = new DeviceOrHostAddressKHR { DeviceAddress = structure.ScratchAddress() };
        var rangePointer = ranges;
        ext.CmdBuildAccelerationStructures(cmd.Handle, 1, &build, &rangePointer);
        return structure;
    }

    /// <summary>The scratch buffer is only needed by the build: freed once the frame that built it has finished.</summary>
    public void ReleaseScratch()
    {
        if (scratch is null) return;
        device.DeferFree(scratch);
        scratch = null;
    }

    public void Dispose()
    {
        ReleaseScratch();
        var ext = device.AccelerationStructures!;
        var handle = Handle;
        var d = device;
        var buffer = storage;
        device.Frames.DeferDelete(() =>
        {
            ext.DestroyAccelerationStructure(d.Device, handle, null);
            d.Allocator.Free(buffer);
        });
    }

    /// <summary>A barrier from acceleration structure builds to the next builds and to shaders that trace.</summary>
    public static BarrierBatch BuildToTrace => new()
    {
        SrcStages = PipelineStageFlags2.AccelerationStructureBuildBitKhr,
        SrcAccess = AccessFlags2.AccelerationStructureWriteBitKhr,
        DstStages = PipelineStageFlags2.AccelerationStructureBuildBitKhr | PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
        DstAccess = AccessFlags2.AccelerationStructureReadBitKhr | AccessFlags2.ShaderReadBit,
    };
}
