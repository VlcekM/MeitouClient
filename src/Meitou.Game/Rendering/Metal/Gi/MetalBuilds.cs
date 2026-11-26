using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using Meitou.Game.Rendering.Gi;

namespace Meitou.Game.Rendering.Metal.Gi;

/// <summary>
/// A bottom level of the Metal traced scene: its structure, a geometry record per geometry (in its geometry order), which part of the mesh each
/// is, what a trace reaches by address (the structure and the records' buffers, made resident on the dispatch) and the buffers it owns.
/// </summary>
sealed class MetalBottomLevel(MTLAccelerationStructure structure, GeometryRecord[] records, int[] parts, nint[] resident, MTLBuffer[] owned)
    : IDisposable
{
    /// <summary>
    /// The structure.
    /// </summary>
    public MTLAccelerationStructure Structure => structure;

    /// <summary>
    /// One record per geometry.
    /// </summary>
    public GeometryRecord[] Records => records;

    /// <summary>
    /// Per record, the mesh part it is.
    /// </summary>
    public int[] Parts => parts;

    /// <summary>
    /// The structure's and the records' buffers' native pointers.
    /// </summary>
    public nint[] Resident => resident;

    /// <summary>
    /// What the scene and the eviction keep about it: the sources it was built from and the update it was last in.
    /// </summary>
    public object[] Sources { get; init; } = [];

    /// <summary>
    /// The update it was last in the scene.
    /// </summary>
    public long LastUsed { get; set; }

    /// <summary>
    /// Releases the structure and the buffers it owns.
    /// </summary>
    public void Dispose()
    {
        structure.Dispose();
        foreach (var b in owned) b.Dispose();
    }
}

/// <summary>
/// One frame's acceleration-structure builds: the bottom levels in one encoder (they do not depend on each other), each top level in an encoder
/// after it. The frame's own commands are closed first; scratch buffers go after the frames in flight.
/// </summary>
sealed class MetalBuilds(MetalContext ctx)
{
    /// <summary>
    /// The open bottom-level encoder (null: none this frame yet).
    /// </summary>
    MTLAccelerationStructureCommandEncoder _encoder;

    /// <summary>
    /// Whether <see cref="_encoder"/> is open.
    /// </summary>
    bool _open;

    /// <summary>
    /// The bottom levels built this frame.
    /// </summary>
    public int Count { get; private set; }

    /// <summary>
    /// The bytes of the bottom levels built so far, in total.
    /// </summary>
    public long Bytes { get; private set; }

    /// <summary>
    /// Starts a frame's builds.
    /// </summary>
    public void Begin() => Count = 0;

    /// <summary>
    /// A new bottom level of <paramref name="geometries"/>, recorded into the frame's encoder. The geometries stay the caller's to release: a
    /// descriptor holds its buffers strongly, so one never released keeps them allocated after they are freed (docs/metal-performance-memory.md
    /// "Acceleration-structure descriptors").
    /// </summary>
    public MTLAccelerationStructure Bottom(string name, IReadOnlyList<MTLAccelerationStructureTriangleGeometryDescriptor> geometries)
    {
        var descriptor = new MTLPrimitiveAccelerationStructureDescriptor
        {
            GeometryDescriptors = ArrayOf(geometries.Select(g => g.NativePtr)),
            Usage = MTLAccelerationStructureUsage.PreferFastIntersection,
        };
        var sizes = ctx.Device.AccelerationStructureSizes(descriptor);
        var structure = ctx.Device.NewAccelerationStructure(sizes.accelerationStructureSize);
        structure.Label = (NSString)name;
        var scratch = ctx.Device.NewBuffer(Math.Max(sizes.buildScratchBufferSize, 1), MTLResourceOptions.ResourceStorageModePrivate);
        Encoder().BuildAccelerationStructure(structure, descriptor, scratch, 0);
        ctx.DeferDelete(scratch.Dispose);
        descriptor.Dispose();
        Count++;
        Bytes += (long)sizes.accelerationStructureSize;
        return structure;
    }

    /// <summary>
    /// A new bottom level of <paramref name="geometries"/> that can be refitted (<see cref="Refit"/>; built fast, as it is rebuilt now and then),
    /// with its descriptor (the caller's to release with the structure) and refit scratch.
    /// </summary>
    public (MTLAccelerationStructure Structure, MTLPrimitiveAccelerationStructureDescriptor Descriptor, MTLBuffer RefitScratch) Refittable(string name,
        IReadOnlyList<MTLAccelerationStructureTriangleGeometryDescriptor> geometries)
    {
        var descriptor = new MTLPrimitiveAccelerationStructureDescriptor
        {
            GeometryDescriptors = ArrayOf(geometries.Select(g => g.NativePtr)),
            Usage = MTLAccelerationStructureUsage.Refit | MTLAccelerationStructureUsage.PreferFastBuild,
        };
        var sizes = ctx.Device.AccelerationStructureSizes(descriptor);
        var structure = ctx.Device.NewAccelerationStructure(sizes.accelerationStructureSize);
        structure.Label = (NSString)name;
        var scratch = ctx.Device.NewBuffer(Math.Max(sizes.buildScratchBufferSize, 1), MTLResourceOptions.ResourceStorageModePrivate);
        var refitScratch = ctx.Device.NewBuffer(Math.Max(sizes.refitScratchBufferSize, 1), MTLResourceOptions.ResourceStorageModePrivate);
        Encoder().BuildAccelerationStructure(structure, descriptor, scratch, 0);
        ctx.DeferDelete(scratch.Dispose);
        Count++;
        Bytes += (long)sizes.accelerationStructureSize;
        return (structure, descriptor, refitScratch);
    }

    /// <summary>
    /// <paramref name="structure"/> refitted in place over its geometries' new positions.
    /// </summary>
    public void Refit(MTLAccelerationStructure structure, MTLPrimitiveAccelerationStructureDescriptor descriptor, MTLBuffer scratch) =>
        Encoder().RefitAccelerationStructure(structure, descriptor, structure, scratch, 0);

    /// <summary>
    /// Builds <paramref name="descriptor"/> into <paramref name="top"/> with <paramref name="scratch"/>, in an encoder after the bottom levels'.
    /// </summary>
    public void Top(MTLAccelerationStructure top, MTLInstanceAccelerationStructureDescriptor descriptor, MTLBuffer scratch)
    {
        End();
        var encoder = Command().AccelerationStructureCommandEncoder();
        encoder.BuildAccelerationStructure(top, descriptor, scratch, 0);
        encoder.EndEncoding();
    }

    /// <summary>
    /// Ends the bottom levels' encoder, if open.
    /// </summary>
    public void End()
    {
        if (!_open) return;

        _encoder.EndEncoding();
        _open = false;
    }

    /// <summary>
    /// The open bottom-level encoder, opened after the frame's own commands.
    /// </summary>
    MTLAccelerationStructureCommandEncoder Encoder()
    {
        if (_open) return _encoder;

        _encoder = Command().AccelerationStructureCommandEncoder();
        _open = true;
        return _encoder;
    }

    /// <summary>
    /// The frame's command buffer, its open encoders closed.
    /// </summary>
    MTLCommandBuffer Command()
    {
        ((MetalCommandList)ctx.Frame.Commands).Finish();
        return ctx.Frame.Command;
    }

    /// <summary>
    /// An NSArray of Objective-C objects.
    /// </summary>
    internal static NSArray ArrayOf(IEnumerable<IntPtr> objects)
    {
        var mutable = ObjectiveC.IntPtr_objc_msgSend(ObjectiveC.objc_getClass("NSMutableArray"), "array");
        foreach (var p in objects) ObjectiveC.objc_msgSend(mutable, "addObject:", p);

        return new NSArray(mutable);
    }
}
