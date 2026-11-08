using Meitou.Rendering.Gpu;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using SharpMetal.QuartzCore;
using System.Runtime.InteropServices;

namespace Meitou.Metal;

/// <summary>
/// The frame being recorded, on Metal (docs/resource-layer.md): a slot ring over the display's drawables —
/// one drawable, one command buffer and one constants buffer per slot. The constants ring is a shared buffer
/// the CPU writes (unified memory); the frame's commands are a <see cref="MetalCommandList"/>. Begun and ended
/// by the context's frame loop (<see cref="MetalContext.BeginFrame"/>, <see cref="MetalContext.EndFrame"/>).
/// </summary>
public sealed unsafe class MetalFrame : IGpuFrame
{
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_retain")]
    static extern IntPtr Retain_objc_retain(IntPtr obj);

    readonly MetalContext ctx;

    /// <summary>
    /// One constants chunk. A slice never moves when another allocation does not fit: the next chunk is used
    /// instead (a frame's characters allocate the bone palettes, the materials and the instances at different
    /// times, and writing through a slice's pointer must keep reaching the slice the GPU is bound to).
    /// </summary>
    sealed class Chunk
    {
        public MTLBuffer Buffer = default;
        public GpuBuffer Handle;
        public byte* Pointer;
        public ulong Size, Used;
    }

    readonly List<Chunk> chunks = [];
    Chunk current;

    internal MetalFrame(MetalContext ctx, int slot)
    {
        this.ctx = ctx;
        Slot = slot;
        current = NewChunk(1 << 20);
        Commands = new MetalCommandList(ctx, $"frame {slot}", default);
        preFrame = new MetalCommandList(ctx, $"preframe {slot}", default);
        States = new ResourceStates();
        Stats = new GpuStats();
    }

    public long Number { get; internal set; }
    public int Slot { get; }
    public bool Open { get; internal set; }
    public MetalCommandList Commands { get; }
    IGpuCommandList IGpuFrame.Commands => Commands;

    readonly MetalCommandList preFrame;
    MTLCommandBuffer preCommand;
    // The separate pre-frame command buffer is part of the opt-in Metal GPU-cull path. With the CPU cull, the
    // upstream pre-frame work (uploads, the ocean's FFT, impostor bakes) stays in the frame's own command list
    // as before the 2026-10-23 cull change: the extra command buffer is not needed and is one more ordering
    // point on a path that has produced GPU progress timeouts.
    static readonly bool SeparatePreFrame = Environment.GetEnvironmentVariable("MEITOU_METAL_GPU_CULL") is "1" or "true";

    /// <summary>
    /// The commands recorded ahead of the frame's passes (the GPU cull's dispatches, the ocean's FFT, the impostor
    /// bakes). With the Metal GPU cull they go into a command buffer of their own, committed before the frame's;
    /// otherwise the frame's own list is used.
    /// </summary>
    public IGpuCommandList PreFrame => SeparatePreFrame ? SeparatePreFrameList() : Commands;

    IGpuCommandList IGpuFrame.PreFrame => PreFrame;

    IGpuCommandList SeparatePreFrameList()
    {
        if (preCommand.NativePtr == IntPtr.Zero)
        {
            preCommand = ctx.Queue.CommandBuffer();
            Retain_objc_retain(preCommand.NativePtr);
            preFrame.Rebind(preCommand);
            preFrame.Invalidate();
        }
        return preFrame;
    }

    /// <summary>Commits the pre-frame before the frame's own command buffer.</summary>
    internal void CommitPreFrame(bool wait = false)
    {
        if (preCommand.NativePtr == IntPtr.Zero) return;
        preFrame.Finish();
        preCommand.Commit();
        if (wait) preCommand.WaitUntilCompleted();
    }

    /// <summary>The slot's previous pre-frame has completed; release its wrapper.</summary>
    internal void ReleasePreFrame()
    {
        if (preCommand.NativePtr != IntPtr.Zero) preCommand.Dispose();
        preCommand = default;
    }

    public ResourceStates States { get; }
    public GpuStats Stats { get; }
    public IGpuTimestamps Timestamps => ctx.Timestamps;

    /// <summary>The command buffer this frame's commands are recorded into (the present pass continues it at <see cref="MetalContext.EndFrame"/>).</summary>
    internal MTLCommandBuffer Command { get; private set; }

    /// <summary>The drawable this frame draws into (the context's <see cref="MetalDisplay.TryBeginFrame"/> gave it).</summary>
    public CAMetalDrawable Drawable { get; internal set; }

    /// <summary>Writes bytes into the frame's constant ring (the default blocks' slices), aligned; the slice lives for the frame.</summary>
    public unsafe Transient WriteConstants(ReadOnlySpan<byte> data, ulong alignment = 16)
    {
        var slice = AllocateConstants((ulong)data.Length, alignment);
        if (data.Length > 0) data.CopyTo(slice.Bytes);
        return slice;
    }

    /// <inheritdoc cref="IGpuFrame.AllocateConstants"/>
    public unsafe Transient AllocateConstants(ulong size, ulong alignment = 16)
    {
        alignment = Math.Max(alignment, 1);
        if (size == 0) return new Transient(current.Handle, current.Used, current.Pointer + current.Used, 0);
        if (current.Size - current.Used < size + alignment)
            current = NextChunk(size + alignment);
        ulong offset = (current.Used + alignment - 1) / alignment * alignment;
        if (current.Size - offset < size) current = NextChunk(size + alignment);
        offset = (current.Used + alignment - 1) / alignment * alignment;
        var pointer = current.Pointer + (nint)offset;
        current.Used = offset + size;
        // Shared mode needs no flush (Managed's DidModifyRange); the GPU reads the memory as it is.
        return new Transient(current.Handle, offset, pointer, size);
    }

    Chunk NewChunk(ulong size)
    {
        var shared = ctx.NewSharedBuffer(Math.Max(size, 1 << 20), $"frame constants {Slot}");
        var chunk = new Chunk
        {
            Buffer = shared.Buffer,
            Handle = ctx.RegisterBuffer(shared.Buffer, $"frame constants {Slot}"),
            Pointer = shared.Pointer,
            Size = shared.Size,
        };
        chunks.Add(chunk);
        return chunk;
    }

    Chunk NextChunk(ulong needed)
    {
        // Reuse a chunk with room left before making a new buffer; all chunks are reset at the frame's begin.
        foreach (var chunk in chunks)
            if (chunk.Size - chunk.Used >= needed) return chunk;
        return NewChunk(Math.Max(needed, 1 << 20));
    }

    internal void Begin(long number, MTLCommandBuffer command, CAMetalDrawable drawable)
    {
        // Drop the chunks this slot did not use in its previous frame. A busy frame can grow the ring to
        // hundreds of megabytes; after the camera leaves a town those chunks would otherwise stay for the
        // process lifetime even though no pass wants them.
        for (int i = chunks.Count - 1; i >= 1; i--)
        {
            if (chunks[i].Used != 0) continue;
            var stale = chunks[i];
            chunks.RemoveAt(i);
            ctx.DeferDelete(() => ctx.ReleaseBuffer(stale.Handle));
        }
        // The context retains this command for its slot (the queue's autorelease would otherwise end it after
        // one frame); the slot is reused only after the three-frame ring has advanced. A failed command is the
        // first thing to look for when the present stops advancing.
        ReleasePreFrame();
        if (Command.NativePtr != IntPtr.Zero)
        {
            if (Command.Status == MTLCommandBufferStatus.Error)
                Console.WriteLine($"metal     frame slot {Slot} command buffer failed: {Command.Error.LocalizedDescription}");
            Command.Dispose();
        }
        Number = number;
        Command = command;
        command.Label = (NSString)$"Meitou frame {number}";
        Commands.Rebind(command);
        Drawable = drawable;
        foreach (var chunk in chunks) chunk.Used = 0;
        current = chunks[0];
        Commands.Invalidate();
        States.AssumeFullBarrier();
        Stats.Reset();
        Open = true;
    }

    internal void End() => Open = false;

    /// <summary>Waits for this slot's command buffer (the Vulkan fence diagnostic; the command is retained by the slot).</summary>
    internal void WaitForCompletion()
    {
        if (Command.NativePtr == IntPtr.Zero) return;
        Command.WaitUntilCompleted();
        if (Command.Status == MTLCommandBufferStatus.Error)
            Console.WriteLine($"metal     frame slot {Slot} command buffer failed: {Command.Error.LocalizedDescription}");
        ReleasePreFrame();
    }

    /// <summary>The command buffer completed and the slot will be reused: drop the pointer so teardown releases do not retain
    /// a command object the queue may already have freed.</summary>
    internal void ForgetCommand()
    {
        if (Command.NativePtr != IntPtr.Zero) Command.Dispose();
        Command = default;
        ReleasePreFrame();
    }

    internal int ChunkCount => chunks.Count;
    internal long ChunkBytes
    {
        get { long bytes = 0; foreach (var c in chunks) bytes += (long)c.Size; return bytes; }
    }
    internal long ChunkUsedBytes
    {
        get { long bytes = 0; foreach (var c in chunks) bytes += (long)c.Used; return bytes; }
    }
}
