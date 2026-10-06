namespace Meitou.Rendering.Gpu;

/// <summary>
/// (Phase 8 stage 1, docs/renderer-native.md 8.) GL names over native resources, for code that still takes a GL vertex array from a renderer
/// whose resources are now native: <see cref="TerrainRenderer.DrawMeshes"/> / <see cref="TerrainRenderer.DrawMeshesIndirect"/> take the
/// TERRAIN-mode meshes as GL vertex arrays. The native object stays the owner (<see cref="IGlInterop.ImportBuffer"/>: borrowed names). The
/// GL texture names the impostor baker and preview needed went with their native port (stage 2). Goes with VkGl in stage 3, once those
/// consumers take native objects.
/// </summary>
public static unsafe class GlBridge
{
    static IGl Gl(GpuContext ctx) => ctx.Interop as IGl ?? throw new InvalidOperationException("GL names need VkGl (the interop)");

    /// <summary>Opens the frame where a GL upload would have (VkGl opens one lazily, before the host begins it: loading, offscreen tools), so a
    /// native upload (<see cref="Uploader"/>, <see cref="GpuFrame.PreFrame"/>) can record. Nothing when one is open.</summary>
    public static void EnsureFrame(GpuContext ctx)
    {
        if (!ctx.Frame.Open) ctx.Interop?.Interleave(static _ => { });
    }

    /// <summary>A float (or, with <see cref="Integer"/>, integer) vertex attribute of a <see cref="VertexArray"/>.</summary>
    public readonly record struct Attribute(uint Location, int Size, int Offset, bool Integer = false);

    /// <summary>A GL vertex array and the borrowed buffer names it holds.</summary>
    public readonly record struct VertexArrayNames(uint Vao, uint Vertices, uint Elements);

    /// <summary>A GL vertex array over native buffers: <paramref name="attributes"/> read <paramref name="vertices"/> with
    /// <paramref name="stride"/> (floats unnormalised, integers as unsigned bytes), and <paramref name="elements"/> is its element buffer.
    /// Leaves no vertex array bound.</summary>
    public static VertexArrayNames VertexArray(GpuContext ctx, DeviceBuffer vertices, uint stride, ReadOnlySpan<Attribute> attributes, DeviceBuffer elements)
    {
        var gl = Gl(ctx);
        var interop = ctx.Interop!;
        uint vbo = interop.ImportBuffer(vertices), ebo = interop.ImportBuffer(elements);
        uint vao = gl.GenVertexArray();
        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        foreach (var a in attributes)
        {
            gl.EnableVertexAttribArray(a.Location);
            if (a.Integer) gl.VertexAttribIPointer(a.Location, a.Size, VertexAttribIType.UnsignedByte, stride, (void*)a.Offset);
            else gl.VertexAttribPointer(a.Location, a.Size, VertexAttribPointerType.Float, false, stride, (void*)a.Offset);
        }
        gl.BindVertexArray(0);
        return new VertexArrayNames(vao, vbo, ebo);
    }

    /// <summary>Deletes what <see cref="VertexArray"/> made (the buffers stay the native owner's).</summary>
    public static void DeleteVertexArray(GpuContext ctx, VertexArrayNames names)
    {
        if (names.Vao == 0 || ctx.Interop is not IGl gl) return;
        gl.DeleteVertexArray(names.Vao);
        gl.DeleteBuffer(names.Vertices);
        gl.DeleteBuffer(names.Elements);
    }
}
