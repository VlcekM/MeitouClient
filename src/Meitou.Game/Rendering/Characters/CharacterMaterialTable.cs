using System.Runtime.InteropServices;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Characters;

/// <summary>
/// The parts' material records, one growing storage buffer (binding 7 of the characters' frame) that keeps each <see cref="PartMaterial"/>'s record
/// in a slot of its own, uploaded only when the record changed. A changed record goes to a new slot (a frame in flight may still read the old one); a
/// slot is used again once the frames that could read it are done: after its material changed, or was not drawn for <see cref="IdleUpdates"/>
/// updates. Render thread, in the characters' update, before anything binds it this frame.
/// </summary>
internal sealed class CharacterMaterialTable : IDisposable
{
    readonly IGpuContext _gpu;
    DeviceBuffer _buffer;
    CharacterMaterialRecord[] _host = new CharacterMaterialRecord[1024];
    PartMaterial?[] _owners = new PartMaterial?[1024];
    int _used;
    readonly Stack<int> _free = [];
    readonly Queue<(int Slot, long Frame)> _retired = [];
    readonly List<int> _dirty = [];
    CharacterMaterialRecord[] _dirtyRecords = new CharacterMaterialRecord[64];

    /// <summary>
    /// Updates without a draw after which a material gives its slot back (it takes a new one when it is drawn again).
    /// </summary>
    public const int IdleUpdates = 600;

    public CharacterMaterialTable(IGpuContext gpu)
    {
        this._gpu = gpu;
        _buffer = DeviceBuffer.Create(gpu, (ulong)_host.Length * CharacterMaterialRecord.Size, BufferUse.Storage, "character materials");
    }

    public int Slots => _used - _free.Count - _retired.Count;
    public long Bytes => (long)_host.Length * CharacterMaterialRecord.Size;

    /// <summary>
    /// Gives back the slots no frame in flight can read any more; first, each frame.
    /// </summary>
    public void BeginFrame()
    {
        long frame = _gpu.Frame.Number;
        int inFlight = _gpu.FramesInFlight;
        while (_retired.Count > 0 && frame - _retired.Peek().Frame > inFlight) _free.Push(_retired.Dequeue().Slot);
    }

    /// <summary>
    /// <paramref name="m"/>'s <see cref="PartMaterial.Record"/> into a new slot (its old one, if any, retired), uploaded at <see cref="Flush"/>.
    /// </summary>
    public void Place(PartMaterial m)
    {
        if (m.Slot >= 0) Retire(m.Slot);
        int slot = _free.Count > 0 ? _free.Pop() : Reserve();
        _host[slot] = m.Record;
        _owners[slot] = m;
        m.Slot = slot;
        _dirty.Add(slot);
    }

    void Retire(int slot)
    {
        _owners[slot] = null;
        _retired.Enqueue((slot, _gpu.Frame.Number));
    }

    /// <summary>
    /// Retires the slots of materials last drawn before <paramref name="before"/> (<see cref="PartMaterial.SlotUsed"/>).
    /// </summary>
    public void Sweep(long before)
    {
        for (int s = 0; s < _used; s++)
            if (_owners[s] is { } m && m.SlotUsed < before)
            {
                m.Slot = -1;
                Retire(s);
            }
    }

    int Reserve()
    {
        int slot = _used++;
        if (_used > _host.Length)
        {
            Array.Resize(ref _host, _host.Length * 2);
            Array.Resize(ref _owners, _owners.Length * 2);
            _buffer.Dispose();
            _buffer = DeviceBuffer.Create(_gpu, (ulong)_host.Length * CharacterMaterialRecord.Size, BufferUse.Storage, "character materials");
            _gpu.EnsureFrame();
            using var batch = _gpu.Uploads.Begin();
            batch.Write(_buffer, 0, MemoryMarshal.AsBytes(_host.AsSpan(0, slot)));
        }
        return slot;
    }

    /// <summary>
    /// Uploads the records placed since the last call, in one copy.
    /// </summary>
    public void Flush()
    {
        if (_dirty.Count == 0) return;
        if (_dirtyRecords.Length < _dirty.Count)
            _dirtyRecords = new CharacterMaterialRecord[Math.Max(_dirty.Count, _dirtyRecords.Length * 2)];
        for (int i = 0; i < _dirty.Count; i++) _dirtyRecords[i] = _host[_dirty[i]];
        _gpu.EnsureFrame();

        // The seam's uploads are batched (Vulkan would take one scattered copy; Metal records its own copy commands).
        using var batch = _gpu.Uploads.Begin();
        for (int i = 0; i < _dirty.Count; i++)
            batch.Write(_buffer, (ulong)_dirty[i] * CharacterMaterialRecord.Size, MemoryMarshal.AsBytes(_dirtyRecords.AsSpan(i, 1)));
        _dirty.Clear();
    }

    public GpuBufferBinding Binding(IGpuFrame frame) => _buffer.Binding(frame);

    public void Dispose() => _buffer.Dispose();
}