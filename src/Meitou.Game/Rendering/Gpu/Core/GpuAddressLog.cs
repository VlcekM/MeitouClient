using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;

namespace Meitou.Game.Rendering.Gpu.Core;

/// <summary>
/// Diagnostic (issue #319, <c>MEITOU_DIAG_FAULT=1</c>): what is and recently was at each GPU virtual address, so a lost device's fault address
/// (<see cref="DeviceFault"/>) names a texture or buffer and tells whether it was already freed. Two sources: the ranges the driver reports as it
/// binds and unbinds them (VK_EXT_device_address_binding_report, through a debug messenger of its own), and where the allocator placed each image
/// and buffer in its memory blocks (<see cref="Placed"/>, <see cref="Forget"/>), which names an address inside a block when the driver reports
/// only the blocks. Under the validation layer the ranges stay unnamed: it wraps the handles, so the driver's differ from the allocator's
/// (RTX 4070, 2026-11-10). Any thread.
/// </summary>
public sealed unsafe class GpuAddressLog : IDisposable
{
    /// <summary>
    /// An address range the driver bound to an object: the frames it was bound and unbound in (<c>Gone</c> -1 while bound) and the object's name
    /// when the allocator gave one.
    /// </summary>
    readonly record struct Range(ObjectType Type, ulong Handle, ulong Base, ulong Size, long Bound, long Gone, string? Name);

    /// <summary>
    /// Where the allocator put an image or buffer: the memory block, the offset and size in it, and the frames it was made and freed in
    /// (<c>Freed</c> -1 while alive).
    /// </summary>
    readonly record struct Placement(ObjectType Type, ulong Handle, string Name, ulong Memory, ulong Offset, ulong Size, long Made, long Freed);

    /// <summary>
    /// How many unbound ranges and freed placements are kept (the newest).
    /// </summary>
    const int Kept = 16384;
    /// <summary>
    /// How many past objects one address lists at most.
    /// </summary>
    const int Listed = 4;

    #region State

    /// <summary>
    /// The device whose addresses are logged.
    /// </summary>
    readonly VulkanDevice _dev;
    /// <summary>
    /// VK_EXT_debug_utils functions (the messenger's).
    /// </summary>
    readonly ExtDebugUtils _debugUtils;
    /// <summary>
    /// Guards the tables below (the driver calls back on the thread that binds).
    /// </summary>
    readonly object _gate = new();
    /// <summary>
    /// The bound ranges by object.
    /// </summary>
    readonly Dictionary<(ObjectType, ulong), List<Range>> _bound = [];
    /// <summary>
    /// The ranges unbound last, a ring written at <see cref="_unboundCount"/>.
    /// </summary>
    readonly Range[] _unbound = new Range[Kept];
    /// <summary>
    /// Ranges unbound so far.
    /// </summary>
    long _unboundCount;
    /// <summary>
    /// The live images and buffers by object.
    /// </summary>
    readonly Dictionary<(ObjectType, ulong), Placement> _placed = [];
    /// <summary>
    /// The placements freed last, a ring written at <see cref="_freedCount"/>.
    /// </summary>
    readonly Placement[] _freed = new Placement[Kept];
    /// <summary>
    /// Placements freed so far.
    /// </summary>
    long _freedCount;
    /// <summary>
    /// The handle to this log the messenger's callback receives as its user data.
    /// </summary>
    GCHandle _self;
    /// <summary>
    /// The callback delegate, kept alive as long as the messenger.
    /// </summary>
    DebugUtilsMessengerCallbackFunctionEXT? _callback;
    /// <summary>
    /// The messenger the driver reports bindings to.
    /// </summary>
    DebugUtilsMessengerEXT _messenger;

    /// <summary>
    /// A log for <paramref name="dev"/>, whose device has address binding reports enabled; null (with a log line) when the messenger cannot be made.
    /// </summary>
    internal static GpuAddressLog? Create(VulkanDevice dev, ExtDebugUtils debugUtils)
    {
        var log = new GpuAddressLog(dev, debugUtils);
        var result = log.CreateMessenger();
        if (result == Result.Success) return log;

        Console.WriteLine($"diagnostic address tracking off: vkCreateDebugUtilsMessengerEXT failed ({result})");
        log.Dispose();
        return null;
    }

    /// <summary>
    /// An empty log; <see cref="CreateMessenger"/> starts it.
    /// </summary>
    GpuAddressLog(VulkanDevice dev, ExtDebugUtils debugUtils)
    {
        _dev = dev;
        _debugUtils = debugUtils;
    }

    /// <summary>
    /// The messenger that receives only address binding reports.
    /// </summary>
    Result CreateMessenger()
    {
        _self = GCHandle.Alloc(this);
        _callback = Callback;
        var info = new DebugUtilsMessengerCreateInfoEXT
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt | DebugUtilsMessageSeverityFlagsEXT.InfoBitExt
                | DebugUtilsMessageSeverityFlagsEXT.WarningBitExt | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType = DebugUtilsMessageTypeFlagsEXT.DeviceAddressBindingBitExt,
            PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(_callback),
            PUserData = (void*)GCHandle.ToIntPtr(_self),
        };
        return _debugUtils.CreateDebugUtilsMessenger(_dev.Instance, in info, null, out _messenger);
    }

    /// <summary>
    /// Destroys the messenger (the instance must still exist).
    /// </summary>
    public void Dispose()
    {
        if (_messenger.Handle != 0)
        {
            _debugUtils.DestroyDebugUtilsMessenger(_dev.Instance, _messenger, null);
            _messenger = default;
        }

        if (_self.IsAllocated) _self.Free();
    }

    #endregion

    #region Recording

    /// <summary>
    /// Ranges the driver has reported bound so far, for the report.
    /// </summary>
    public long Reported { get; private set; }

    /// <summary>
    /// The allocator made the image or buffer <paramref name="handle"/> named <paramref name="name"/> at <paramref name="offset"/> of the block
    /// <paramref name="memory"/>.
    /// </summary>
    public void Placed(ObjectType type, ulong handle, string name, ulong memory, ulong offset, ulong size)
    {
        lock (_gate) _placed[(type, handle)] = new Placement(type, handle, name, memory, offset, size, Frame, -1);
    }

    /// <summary>
    /// The allocator destroyed the object: its placement and any range the driver did not report unbound join the past ones.
    /// </summary>
    public void Forget(ObjectType type, ulong handle)
    {
        lock (_gate)
        {
            long frame = Frame;
            if (_bound.Remove((type, handle), out var ranges))
            {
                foreach (var r in ranges) Unbound(r, frame);
            }

            if (_placed.Remove((type, handle), out var p)) _freed[_freedCount++ % Kept] = p with { Freed = frame };
        }
    }

    /// <summary>
    /// The messenger's callback: an address binding report's range is bound to or unbound from its object.
    /// </summary>
    static uint Callback(DebugUtilsMessageSeverityFlagsEXT severity, DebugUtilsMessageTypeFlagsEXT type, DebugUtilsMessengerCallbackDataEXT* data,
        void* user)
    {
        if ((type & DebugUtilsMessageTypeFlagsEXT.DeviceAddressBindingBitExt) == 0 || data->ObjectCount == 0) return 0;
        if (GCHandle.FromIntPtr((IntPtr)user).Target is not GpuAddressLog self) return 0;

        for (var next = (BaseInStructure*)data->PNext; next != null; next = next->PNext)
        {
            if (next->SType != StructureType.DeviceAddressBindingCallbackDataExt) continue;

            var binding = (DeviceAddressBindingCallbackDataEXT*)next;
            var o = data->PObjects[0];
            self.Record(o.ObjectType, o.ObjectHandle, binding->BaseAddress, binding->Size,
                binding->BindingType == DeviceAddressBindingTypeEXT.UnbindExt);
            break;
        }

        return 0;
    }

    /// <summary>
    /// The driver bound the range to the object, or unbound it (a range not listed, as one bound before the log began, is ignored).
    /// </summary>
    void Record(ObjectType type, ulong handle, ulong address, ulong size, bool unbind)
    {
        lock (_gate)
        {
            long frame = Frame;
            if (!unbind)
            {
                if (!_bound.TryGetValue((type, handle), out var list)) _bound[(type, handle)] = list = [];

                // A driver may report a range twice (NVIDIA 2026-11: a memory block at its allocation and again): listed once.
                if (!list.Exists(r => r.Base == address && r.Size == size))
                    list.Add(new Range(type, handle, address, size, frame, -1, null));
                Reported++;
                return;
            }

            if (!_bound.TryGetValue((type, handle), out var ranges)) return;

            int at = ranges.FindIndex(r => r.Base == address);
            if (at < 0) return;

            Unbound(ranges[at], frame);
            ranges.RemoveAt(at);
            if (ranges.Count == 0) _bound.Remove((type, handle));
        }
    }

    /// <summary>
    /// Under the lock: <paramref name="r"/> joins the past ranges, with the name its object has now.
    /// </summary>
    void Unbound(Range r, long frame) =>
        _unbound[_unboundCount++ % Kept] = r with { Gone = frame, Name = _placed.TryGetValue((r.Type, r.Handle), out var p) ? p.Name : null };

    /// <summary>
    /// The frame being recorded (0 before the frame ring exists).
    /// </summary>
    long Frame => _dev.Frames?.FrameNumber ?? 0;

    #endregion

    #region Report

    /// <summary>
    /// What is and was at <paramref name="address"/> (known to <paramref name="precision"/>, a power of two): the objects bound there now, the last
    /// few unbound from there, and for a memory block the images and buffers placed at that offset of it.
    /// </summary>
    internal void Describe(ulong address, ulong precision, List<string> lines)
    {
        ulong low = precision > 1 ? address & ~(precision - 1) : address, high = precision > 1 ? address | (precision - 1) : address;
        lock (_gate)
        {
            int before = lines.Count;
            foreach (var list in _bound.Values)
            {
                foreach (var r in list)
                    if (Covers(r.Base, r.Size, low, high)) DescribeRange(r, low, high, lines);
            }

            int listed = 0;
            for (long i = _unboundCount - 1; i >= Math.Max(_unboundCount - Kept, 0) && listed < Listed; i--)
            {
                var r = _unbound[i % Kept];
                if (!Covers(r.Base, r.Size, low, high)) continue;

                DescribeRange(r, low, high, lines);
                listed++;
            }

            if (lines.Count == before)
            {
                lines.Add($"  no object is or recently was bound there ({_bound.Count} objects bound, {Reported} ranges reported, "
                    + $"{Math.Min(_unboundCount, Kept)} unbound kept)");
            }
        }
    }

    /// <summary>
    /// Whether the range of <paramref name="size"/> bytes at <paramref name="start"/> reaches into <paramref name="low"/>..<paramref name="high"/>.
    /// </summary>
    static bool Covers(ulong start, ulong size, ulong low, ulong high) => start <= high && start + size > low;

    /// <summary>
    /// Under the lock: one line for a range at the address, and for a memory block the placements at that offset of it.
    /// </summary>
    void DescribeRange(Range r, ulong low, ulong high, List<string> lines)
    {
        string? known = r.Name ?? (_placed.TryGetValue((r.Type, r.Handle), out var p) ? p.Name : null);
        string name = known is null ? "" : $" '{known}'";
        string life = r.Gone < 0 ? $"bound since frame {r.Bound}" : $"UNBOUND in frame {r.Gone} (bound in frame {r.Bound})";
        lines.Add($"  {r.Type} 0x{r.Handle:x}{name}, 0x{r.Base:x} + {r.Size / 1024.0:0.#} KB, {life}");
        if (r.Type != ObjectType.DeviceMemory) return;

        // The address as an offset range of the block (the block's start when the precision reaches below it).
        ulong from = low > r.Base ? low - r.Base : 0, to = high - r.Base;
        foreach (var live in _placed.Values)
            if (live.Memory == r.Handle && Covers(live.Offset, live.Size, from, to) && Within(live, r)) DescribePlacement(live, lines);

        int listed = 0;
        for (long i = _freedCount - 1; i >= Math.Max(_freedCount - Kept, 0) && listed < Listed; i--)
        {
            var gone = _freed[i % Kept];
            if (gone.Memory != r.Handle || !Covers(gone.Offset, gone.Size, from, to) || !Within(gone, r)) continue;

            DescribePlacement(gone, lines);
            listed++;
        }
    }

    /// <summary>
    /// Whether the placement was made while the block's range <paramref name="r"/> was bound (a later block may reuse the handle).
    /// </summary>
    static bool Within(Placement p, Range r) => p.Made >= r.Bound && (r.Gone < 0 || p.Made <= r.Gone);

    /// <summary>
    /// One line for an image or buffer placed in a memory block.
    /// </summary>
    static void DescribePlacement(Placement p, List<string> lines)
    {
        string life = p.Freed < 0 ? $"alive since frame {p.Made}" : $"FREED in frame {p.Freed} (made in frame {p.Made})";
        lines.Add($"    {p.Type} 0x{p.Handle:x} '{p.Name}' at +0x{p.Offset:x}, {p.Size / 1024.0:0.#} KB, {life}");
    }

    #endregion
}
