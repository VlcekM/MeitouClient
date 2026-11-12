using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gi;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Rendering.Gpu.Core;
using Silk.NET.Vulkan;

namespace Meitou.Tests.Vulkan;

/// <summary>
/// The global illumination's acceleration structures and ray queries (docs/render-gi.md): a triangle built into a bottom level from
/// <see cref="BufferUse.RayInput"/> buffers, placed by a top-level instance, traced from a compute shader.
/// </summary>
public class RayQueryTests
{
    /// <summary>
    /// The rays per side of the grid.
    /// </summary>
    const int N = 16;

    /// <summary>
    /// Rays straight down (or up) from a 16 x 16 grid over [-1, 1]²; per ray the hit distance (-1: none) and the hit's custom index.
    /// </summary>
    const string Trace = """
        #version 460
        #extension GL_EXT_ray_query : require
        layout(local_size_x = 16, local_size_y = 16) in;
        layout(set = 0, binding = 0) uniform accelerationStructureEXT uScene;
        layout(set = 0, binding = 1, std430) buffer Results { vec2 results[]; };
        layout(push_constant) uniform Push { float startY; float dirY; uint flags; } p;
        void main()
        {
            uvec2 id = gl_GlobalInvocationID.xy;
            vec3 origin = vec3((vec2(id) + 0.5) / 16.0 * 2.0 - 1.0, 0.0).xzy + vec3(0.0, p.startY, 0.0);
            rayQueryEXT rq;
            rayQueryInitializeEXT(rq, uScene, gl_RayFlagsOpaqueEXT | p.flags, 0xFF, origin, 0.0, vec3(0.0, p.dirY, 0.0), 100.0);
            while (rayQueryProceedEXT(rq)) { }
            bool hit = rayQueryGetIntersectionTypeEXT(rq, true) == gl_RayQueryCommittedIntersectionTriangleEXT;
            results[id.y * 16u + id.x] = hit ? vec2(rayQueryGetIntersectionTEXT(rq, true), float(rayQueryGetIntersectionInstanceCustomIndexEXT(rq, true))) : vec2(-1.0);
        }
        """;

    /// <summary>
    /// A shader's push constants: where the rays start, which way they go, and the ray flags.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct Push
    {
        /// <summary>
        /// The rays' start height.
        /// </summary>
        public float StartY;

        /// <summary>
        /// The rays' direction along y (1 up, -1 down).
        /// </summary>
        public float DirY;

        /// <summary>
        /// The ray flags added to opaque.
        /// </summary>
        public uint Flags;
    }

    /// <summary>
    /// A triangle placed 2 up by its instance: rays down from above hit its front where it lies, with the instance's custom index; rays up from
    /// below hit only when back faces are not culled.
    /// </summary>
    [Fact]
    [Slow]
    public unsafe void A_placed_triangle_is_hit_where_it_lies_and_counter_clockwise_from_above_is_its_front()
    {
        using var device = TryCreate();

        Assert.SkipWhen(device is null || !device.HasRayQuery, "No Vulkan device with ray queries");

        using var ctx = new GpuContext(device!);

        // The triangle (0,0,0) (0,0,1) (1,0,0): counter-clockwise seen from above (+y), as the terrain grid's triangles; placed 2 up.
        Vector3[] positions = [new(0, 0, 0), new(0, 0, 1), new(1, 0, 0)];
        uint[] indices = [0, 1, 2];
        using var vertices = DeviceBuffer.Create(ctx, 36, BufferUse.Vertex | BufferUse.RayInput, "test triangle");
        using var index = DeviceBuffer.Create(ctx, 12, BufferUse.Index | BufferUse.RayInput, "test triangle");
        ctx.EnsureFrame();
        ctx.Uploads.Write(vertices, 0, MemoryMarshal.AsBytes(positions.AsSpan()));
        ctx.Uploads.Write(index, 0, MemoryMarshal.AsBytes(indices.AsSpan()));
        var instances = device!.Allocator.CreateBuffer(InstanceRecord.Size,
                BufferUsageFlags.ShaderDeviceAddressBit | BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr,
            MemoryKind.Upload, "test instances");
        *(InstanceRecord*)instances.Mapped = InstanceRecord.Of(Matrix4x4.CreateTranslation(0, 2, 0), 7, 0xFF, 0, 0);
        var results = DeviceBuffer.Create(ctx, N * N * 8, BufferUse.Storage | BufferUse.TransferSrc, "test results");
        using var readback = ReadbackBuffer.Create(ctx, N * N * 8, "test results");
        var program = ctx.Shaders.Compute(Trace, "ray query test", [(0, DescriptorType.AccelerationStructureKhr), (1, DescriptorType.StorageBuffer)]);
        var pipeline = ctx.Pipelines.Get(new ComputePipelineDesc(program, "ray query test"));

        var cmd = ctx.BeginNative("ray query test");
        cmd.Order();
        var tlas = BuildTheScene(device, cmd, vertices, index, instances, out var blas);

        var got = new List<Vector2[]>();
        var writes = stackalloc WriteDescriptorSet[2];
        // From above with back faces culled, from below with and without: down rays must hit the front, up rays only when nothing is culled.
        (float StartY, float DirY, uint Flags)[] cases = [(10, -1, 0x10), (-10, 1, 0x10),
                (-10, 1, 0)];   // 0x10: gl_RayFlagsCullBackFacingTrianglesEXT
        foreach (var c in cases)
            cmd = TraceOneCase(ctx, cmd, tlas, results, readback, program, pipeline, writes, c, got);
        ctx.EndNative(cmd);
        ctx.Finish();

        int inside = AssertTheHits(got);

        Assert.True(inside > 20, $"only {inside} rays inside the triangle");
        Assert.True(device.ValidationErrors == 0, "Validation errors:\n" + string.Join("\n", device.ValidationLog));

        tlas.Dispose();
        blas.Dispose();
        results.Dispose();
        device.DeferFree(instances);
    }

    /// <summary>
    /// A validated Vulkan device with ray tracing, or null where there is none.
    /// </summary>
    static VulkanDevice? TryCreate()
    {
        VulkanDevice? d;
        try
        {
            d = VulkanDevice.Create(new VulkanDeviceOptions { Validation = true, RayTracing = true });
        }
        catch (Exception e) when (e is VulkanException or DllNotFoundException or EntryPointNotFoundException
            or Silk.NET.Core.Loader.SymbolLoadingException)
        {
            d = null;
        }
        return d;
    }

    /// <summary>
    /// Builds the triangle's bottom level and a top level of the one instance placing it; returns the top level.
    /// </summary>
    static unsafe AccelerationStructure BuildTheScene(VulkanDevice device, IGpuCommandList cmd, DeviceBuffer vertices, DeviceBuffer index,
            BufferAllocation instances, out AccelerationStructure blas)
    {
        blas = AccelerationStructure.BuildBottom(device, (CommandList)cmd,
                [new TriangleGeometry(device.Address(vertices.Handle), 12, 3, device.Address(index.Handle), 1)], "test bottom");
        ((InstanceRecord*)instances.Mapped)->Reference = blas.Address;
        ((CommandList)cmd).Barrier(AccelerationStructure.BuildToTrace);
        var tlas = AccelerationStructure.CreateTop(device, 4, "test top");
        var addressInfo = new BufferDeviceAddressInfo { SType = StructureType.BufferDeviceAddressInfo, Buffer = instances.Buffer };
        tlas.RebuildTop((CommandList)cmd, device.Vk.GetBufferDeviceAddress(device.Device, in addressInfo), 1);
        ((CommandList)cmd).Barrier(AccelerationStructure.BuildToTrace);
        return tlas;
    }

    /// <summary>
    /// Traces one case's rays, reads their results back into <paramref name="got"/> and returns the next command list.
    /// </summary>
    static unsafe IGpuCommandList TraceOneCase(GpuContext ctx, IGpuCommandList cmd, AccelerationStructure tlas, DeviceBuffer results,
            ReadbackBuffer readback, GpuProgram program, GpuComputePipeline pipeline, WriteDescriptorSet* writes,
            (float StartY, float DirY, uint Flags) c, List<Vector2[]> got)
    {
        var handle = tlas.Handle;
        var asWrite = new WriteDescriptorSetAccelerationStructureKHR { SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
                AccelerationStructureCount = 1, PAccelerationStructures = &handle };
        var bufferInfo = new DescriptorBufferInfo(ctx.Device.BufferAt(results.Handle.Index), 0, results.Size);

        writes[0] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, PNext = &asWrite, DstBinding = 0, DescriptorCount = 1,
                DescriptorType = DescriptorType.AccelerationStructureKhr };
        writes[1] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstBinding = 1, DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &bufferInfo };
        cmd.BindPipeline(pipeline);
        ((CommandList)cmd).PushDescriptors(program, 0, new ReadOnlySpan<WriteDescriptorSet>(writes, 2), GpuBindPoint.Compute);
        var push = new Push { StartY = c.StartY, DirY = c.DirY, Flags = c.Flags };
        cmd.PushConstants(program, GpuStage.Compute, in push);
        cmd.Dispatch(1, 1);
        cmd.Order();
        cmd.CopyBuffer(results.Handle, readback.Handle, new GpuBufferCopy(0, 0, results.Size));
        ctx.EndNative(cmd);
        ctx.Finish();
        got.Add(MemoryMarshal.Cast<byte, Vector2>(readback.Read(0, results.Size)).ToArray());
        ctx.EnsureFrame();
        return ctx.BeginNative("ray query test");
    }

    /// <summary>
    /// Every ray clearly inside the triangle hits it as the case says, every one clearly outside misses from above; returns how many are inside.
    /// </summary>
    static int AssertTheHits(List<Vector2[]> got)
    {
        int inside = 0;
        for (int j = 0; j < N; j++)
            for (int i = 0; i < N; i++)
            {
                float x = (i + 0.5f) / N * 2 - 1, z = (j + 0.5f) / N * 2 - 1;
                bool within = x > 0.05f && z > 0.05f && x + z < 0.95f, without = x < -0.05f || z < -0.05f || x + z > 1.05f;
                var down = got[0][j * N + i];
                if (within)
                {
                    inside++;

                    Assert.Equal(8f, down.X, 3);   // from y 10 to the triangle at y 2
                    Assert.Equal(7f, down.Y);      // the instance's custom index
                    Assert.Equal(-1f, got[1][j * N + i].X);   // from below, culled: the back face
                    Assert.Equal(12f, got[2][j * N + i].X, 3);   // from y -10, not culled
                }
                else if (without) Assert.Equal(-1f, down.X);
            }
        return inside;
    }
}
