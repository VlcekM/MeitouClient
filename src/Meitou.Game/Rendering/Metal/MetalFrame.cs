using Meitou.Game.Rendering.Gpu;
using SharpMetal.Foundation;
using SharpMetal.Metal;
using SharpMetal.ObjectiveCCore;
using SharpMetal.QuartzCore;
using System.Runtime.InteropServices;

namespace Meitou.Game.Rendering.Metal;

/// <summary>
/// The frame being recorded, on Metal (docs/resource-layer.md): a slot ring over the display's drawables —
/// one drawable, one command buffer and one constants buffer per slot. The constants ring is a shared buffer
/// the CPU writes (unified memory); the frame's commands are a <see cref="MetalCommandList"/>. Begun and ended
/// by the context's frame loop (<see cref="MetalContext.BeginFrame"/>, <see cref="MetalContext.EndFrame"/>).
/// </summary>
public sealed unsafe class MetalFrame : IGpuFrame
{
    #region Frame

    /// <summary>
    /// The backend: its queue, shared buffers and handle registry.
    /// </summary>
    readonly MetalContext _ctx;

    /// <summary>
    /// The frame of ring slot <paramref name="slot"/>, with a 1 MB constants chunk and its command lists.
    /// </summary>
    internal MetalFrame(MetalContext ctx, int slot)
    {
        this._ctx = ctx;
        Slot = slot;
        _current = NewChunk(1 << 20);
        Commands = new MetalCommandList(ctx, $"frame {slot}", default);
        _preFrame = new MetalCommandList(ctx, $"preframe {slot}", default);
        States = new ResourceStates();
        Stats = new GpuStats();
    }

    /// <summary>
    /// The number of the frame the slot holds (set at <see cref="Begin"/>).
    /// </summary>
    public long Number { get; internal set; }
    /// <summary>
    /// The frame ring slot.
    /// </summary>
    public int Slot { get; }
    /// <summary>
    /// Between <see cref="Begin"/> and <see cref="End"/>.
    /// </summary>
    public bool Open { get; internal set; }
    /// <summary>
    /// The frame's command list.
    /// </summary>
    public MetalCommandList Commands { get; }
    /// <inheritdoc/>
    IGpuCommandList IGpuFrame.Commands => Commands;

    /// <summary>
    /// The frame's resource state tracking (reset to a full barrier at <see cref="Begin"/>).
    /// </summary>
    public ResourceStates States { get; }
    /// <summary>
    /// The frame's draw and upload counts (reset at <see cref="Begin"/>).
    /// </summary>
    public GpuStats Stats { get; }
    /// <summary>
    /// The context's timestamps (Metal records none yet).
    /// </summary>
    public IGpuTimestamps Timestamps => _ctx.Timestamps;

    /// <summary>
    /// The command buffer this frame's commands are recorded into (the present pass continues it at <see cref="MetalContext.EndFrame"/>).
    /// </summary>
    internal MTLCommandBuffer Command { get; private set; }

    /// <summary>
    /// The drawable this frame draws into (the context's <see cref="MetalDisplay.TryBeginFrame"/> gave it).
    /// </summary>
    public CAMetalDrawable Drawable { get; internal set; }

    /// <summary>
    /// Begins the slot's frame: drops the unused chunks, releases the previous command buffer (logging a failed one), binds the new command buffer
    /// and drawable, and resets the chunks, the state tracking and the counts.
    /// </summary>
    internal void Begin(long number, MTLCommandBuffer command, CAMetalDrawable drawable)
    {
        // Drop the chunks this slot did not use in its previous frame. A busy frame can grow the ring to
        // hundreds of megabytes; after the camera leaves a town those chunks would otherwise stay for the
        // process lifetime even though no pass wants them.
        for (int i = _chunks.Count - 1; i >= 1; i--)
        {
            if (_chunks[i].Used != 0) continue;
            var stale = _chunks[i];
            _chunks.RemoveAt(i);
            _ctx.DeferDelete(() => _ctx.ReleaseBuffer(stale.Handle));
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
        foreach (var chunk in _chunks) chunk.Used = 0;
        _current = _chunks[0];
        Commands.Invalidate();
        States.AssumeFullBarrier();
        Stats.Reset();
        Open = true;
    }

    /// <summary>
    /// Ends the slot's frame.
    /// </summary>
    internal void End() => Open = false;

    /// <summary>
    /// Waits for this slot's command buffer (the Vulkan fence diagnostic; the command is retained by the slot).
    /// </summary>
    internal void WaitForCompletion()
    {
        if (Command.NativePtr == IntPtr.Zero) return;
        Command.WaitUntilCompleted();
        if (Command.Status == MTLCommandBufferStatus.Error)
            Console.WriteLine($"metal     frame slot {Slot} command buffer failed: {Command.Error.LocalizedDescription}");
        ReleasePreFrame();
    }

    /// <summary>
    /// The command buffer completed and the slot will be reused: drop the pointer so teardown releases do not retain
    /// a command object the queue may already have freed.
    /// </summary>
    internal void ForgetCommand()
    {
        if (Command.NativePtr != IntPtr.Zero) Command.Dispose();
        Command = default;
        ReleasePreFrame();
    }

    #endregion

    #region Pre-frame

    /// <summary>
    /// The list the separate pre-frame command buffer is recorded with (the GPU cull path only).
    /// </summary>
    readonly MetalCommandList _preFrame;
    /// <summary>
    /// The separate pre-frame command buffer; default until the first <see cref="PreFrame"/> of the frame asks for it.
    /// </summary>
    MTLCommandBuffer _preCommand;

    /// <summary>
    /// Whether the pre-frame gets a command buffer of its own (<c>MEITOU_METAL_GPU_CULL</c>): part of the opt-in Metal GPU-cull path. With the CPU
    /// cull, the upstream pre-frame work (uploads, the ocean's FFT, impostor bakes) stays in the frame's own command list as before the 2026-10-23
    /// cull change: the extra command buffer is not needed and is one more ordering point on a path that has produced GPU progress timeouts.
    /// </summary>
    static readonly bool SeparatePreFrame = Environment.GetEnvironmentVariable("MEITOU_METAL_GPU_CULL") is "1" or "true";

    /// <summary>
    /// The commands recorded ahead of the frame's passes (the GPU cull's dispatches, the ocean's FFT, the impostor
    /// bakes). With the Metal GPU cull they go into a command buffer of their own, committed before the frame's;
    /// otherwise the frame's own list is used.
    /// </summary>
    public IGpuCommandList PreFrame => SeparatePreFrame ? SeparatePreFrameList() : Commands;

    /// <inheritdoc/>
    IGpuCommandList IGpuFrame.PreFrame => PreFrame;

    /// <summary>
    /// Commits the pre-frame before the frame's own command buffer.
    /// </summary>
    internal void CommitPreFrame(bool wait = false)
    {
        if (_preCommand.NativePtr == IntPtr.Zero) return;
        _preFrame.Finish();
        _preCommand.Commit();
        if (wait) _preCommand.WaitUntilCompleted();
    }

    /// <summary>
    /// The slot's previous pre-frame has completed; release its wrapper.
    /// </summary>
    internal void ReleasePreFrame()
    {
        if (_preCommand.NativePtr != IntPtr.Zero) _preCommand.Dispose();
        _preCommand = default;
    }

    /// <summary>
    /// The pre-frame list on its own command buffer, made from the queue (and retained) on the frame's first ask.
    /// </summary>
    IGpuCommandList SeparatePreFrameList()
    {
        if (_preCommand.NativePtr == IntPtr.Zero)
        {
            _preCommand = _ctx.Queue.CommandBuffer();
            Retain_objc_retain(_preCommand.NativePtr);
            _preFrame.Rebind(_preCommand);
            _preFrame.Invalidate();
        }
        return _preFrame;
    }

    /// <summary>
    /// <c>objc_retain</c>: keeps the pre-frame command buffer alive past the queue's autorelease.
    /// </summary>
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_retain")]
    static extern IntPtr Retain_objc_retain(IntPtr obj);

    #endregion

    #region Constants ring

    /// <summary>
    /// The slot's constants chunks; the first is kept across frames and the unused others are dropped at <see cref="Begin"/>.
    /// </summary>
    readonly List<Chunk> _chunks = [];
    /// <summary>
    /// The chunk allocations come from now.
    /// </summary>
    Chunk _current;

    /// <summary>
    /// Writes bytes into the frame's constant ring (the default blocks' slices), aligned; the slice lives for the frame.
    /// </summary>
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
        if (size == 0) return new Transient(_current.Handle, _current.Used, _current.Pointer + _current.Used, 0);
        if (_current.Size - _current.Used < size + alignment)
            _current = NextChunk(size + alignment);
        ulong offset = (_current.Used + alignment - 1) / alignment * alignment;
        if (_current.Size - offset < size) _current = NextChunk(size + alignment);
        offset = (_current.Used + alignment - 1) / alignment * alignment;
        var pointer = _current.Pointer + (nint)offset;
        _current.Used = offset + size;

        // Shared mode needs no flush (Managed's DidModifyRange); the GPU reads the memory as it is.
        return new Transient(_current.Handle, offset, pointer, size);
    }

    /// <summary>
    /// A new shared chunk of at least 1 MB, registered as a buffer handle.
    /// </summary>
    Chunk NewChunk(ulong size)
    {
        var shared = _ctx.NewSharedBuffer(Math.Max(size, 1 << 20), $"frame constants {Slot}");
        var chunk = new Chunk
        {
            Buffer = shared.Buffer,
            Handle = _ctx.RegisterBuffer(shared.Buffer, $"frame constants {Slot}"),
            Pointer = shared.Pointer,
            Size = shared.Size,
        };
        _chunks.Add(chunk);
        return chunk;
    }

    /// <summary>
    /// A chunk with <paramref name="needed"/> bytes free, else a new one of at least 1 MB.
    /// </summary>
    Chunk NextChunk(ulong needed)
    {
        // Reuse a chunk with room left before making a new buffer; all chunks are reset at the frame's begin.
        foreach (var chunk in _chunks)
            if (chunk.Size - chunk.Used >= needed) return chunk;
        return NewChunk(Math.Max(needed, 1 << 20));
    }

    /// <summary>
    /// The constants chunks the slot holds (debug trace).
    /// </summary>
    internal int ChunkCount => _chunks.Count;
    /// <summary>
    /// The constants chunks' total size in bytes (debug trace).
    /// </summary>
    internal long ChunkBytes
    {
        get { long bytes = 0; foreach (var c in _chunks) bytes += (long)c.Size; return bytes; }
    }
    /// <summary>
    /// The bytes handed out of the chunks this frame (debug trace).
    /// </summary>
    internal long ChunkUsedBytes
    {
        get { long bytes = 0; foreach (var c in _chunks) bytes += (long)c.Used; return bytes; }
    }

    /// <summary>
    /// One constants chunk. A slice never moves when another allocation does not fit: the next chunk is used
    /// instead (a frame's characters allocate the bone palettes, the materials and the instances at different
    /// times, and writing through a slice's pointer must keep reaching the slice the GPU is bound to).
    /// </summary>
    sealed class Chunk
    {
        /// <summary>
        /// The shared buffer.
        /// </summary>
        public MTLBuffer Buffer = default;
        /// <summary>
        /// The buffer's handle in the backend's registry (what bindings name).
        /// </summary>
        public GpuBuffer Handle;
        /// <summary>
        /// The buffer's contents' CPU address.
        /// </summary>
        public byte* Pointer;
        /// <summary>
        /// The chunk's size and the bytes handed out of it this frame, in bytes.
        /// </summary>
        public ulong Size, Used;
    }

    #endregion
}
