using System.Numerics;
using System.Runtime.InteropServices;
using Meitou.Data.Characters;
using Meitou.Data.Ogre;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Characters;

/// <summary>
/// The face and body poses (Ogre morph targets) of a mesh as sparse per-vertex slots (docs/character-renderer.md, "Face poses"): the vertices any
/// pose moves get a slot number 1..n (stored in the vertex's colour X, see <see cref="CharacterContent"/>), and each pose is a list of (slot,
/// offset). An appearance's morph is one offset per slot, the poses summed at their weights, kept in the <see cref="MorphArena"/>; the vertex program
/// adds it before skinning.
/// </summary>
internal sealed class MeshMorphs
{
    public required int SlotCount;
    /// <summary>
    /// The submesh the poses move (all of them target one).
    /// </summary>
    public required int SubMeshIndex;
    public required Dictionary<string, (int[] Slots, Vector3[] Offsets)> Poses;
    public bool HasCuttableHorns => Poses.ContainsKey("bone_horns_top_short");

    /// <summary>
    /// The poses of <paramref name="mesh"/> on <paramref name="model"/>'s parts; null when it has none, or they do not line up with the part's
    /// vertices.
    /// </summary>
    public static MeshMorphs? From(OgreMesh mesh, Model model, List<string> messages, string name)
    {
        if (mesh.Poses.Count == 0) return null;
        int target = mesh.Poses.Where(p => p.Target > 0).Select(p => p.Target - 1).DefaultIfEmpty(-1).First();
        if (target < 0 || target >= mesh.SubMeshes.Count) return null;
        var part = model.Parts.FirstOrDefault(p => p.SubMeshIndex == target);
        if (part is null || mesh.SubMeshes[target].VertexData is not { } data || part.Vertices.Length != data.VertexCount)
        {
            messages.Add($"{name}: poses do not line up with the part's vertices, not morphed");
            return null;
        }
        var slotOf = new Dictionary<uint, int>();
        foreach (var pose in mesh.Poses)
            if (pose.Target - 1 == target)
                foreach (var v in pose.Vertices)
                    if (v.Index < part.Vertices.Length && !slotOf.ContainsKey(v.Index))
                        slotOf[v.Index] = slotOf.Count + 1;
        var poses = new Dictionary<string, (int[], Vector3[])>(StringComparer.Ordinal);
        foreach (var pose in mesh.Poses)
        {
            if (pose.Target - 1 != target) continue;
            var valid = pose.Vertices.Where(v => v.Index < part.Vertices.Length).ToArray();
            poses[pose.Name] = ([.. valid.Select(v => slotOf[v.Index])], [.. valid.Select(v => v.Offset)]);
        }

        // The vertex's slot rides in the colour's X as raw bits (the characters use no vertex colours): 0 for a vertex no pose moves.
        foreach (var p in model.Parts)
            for (int i = 0; i < p.Vertices.Length; i++)
            {
                uint slot = p.SubMeshIndex == target && slotOf.TryGetValue((uint)i, out int s) ? (uint)s : 0;
                p.Vertices[i].Colour = new Vector4(BitConverter.UInt32BitsToSingle(slot), 0, 0, 1);
            }
        return new MeshMorphs { SlotCount = slotOf.Count, SubMeshIndex = target, Poses = poses! };
    }

    /// <summary>
    /// One offset per slot for the appearance's pose weights (the file's values baked as Kenshi does, <see cref="CharacterShape.BakeWeight"/>); null
    /// when none applies.
    /// </summary>
    public Vector4[]? Deltas(IReadOnlyDictionary<string, float> weights, bool cutHorns)
    {
        Vector4[]? result = null;
        foreach (var (name, stored) in weights)
        {
            if (!Poses.TryGetValue(name, out var pose) || CharacterShape.BakeWeight(name, stored, cutHorns) is not { } w)
                continue;
            result ??= new Vector4[SlotCount];
            for (int i = 0; i < pose.Slots.Length; i++) result[pose.Slots[i] - 1] += new Vector4(pose.Offsets[i] * w, 0);
        }
        return result;
    }
}

/// <summary>
/// The appearances' morph offsets, one growing storage buffer (binding 8 of the characters' frame): a set is <c>n</c> vec4s of slot offsets, found by
/// its first index minus one (<see cref="Add"/>), identical sets stored once (a town's squads share their faces). Written while the frame is being
/// prepared, before anything binds it; growing makes a new buffer and rewrites every set from the host copy.
/// </summary>
internal sealed class MorphArena : IDisposable
{
    readonly IGpuContext _gpu;
    DeviceBuffer _buffer;
    Vector4[] _host;
    int _used = 2;   // entries 0 and 1 are never a set's base, so a base of 0 can mean "no morph"
    readonly Dictionary<string, uint> _known = [];

    public MorphArena(IGpuContext gpu)
    {
        this._gpu = gpu;
        _host = new Vector4[4096];
        _buffer = DeviceBuffer.Create(gpu, (ulong)_host.Length * 16, BufferUse.Storage, "character morphs");
    }

    public int Entries => _used;
    public long Bytes => (long)_host.Length * 16;

    /// <summary>
    /// The base to give instances of the character: the vertex program reads entry <c>base + slot</c>.
    /// </summary>
    public uint Add(string key, Vector4[] deltas)
    {
        if (_known.TryGetValue(key, out uint existing)) return existing;
        int first = Reserve(deltas.Length);
        Write(first, deltas);
        uint baseIndex = (uint)(first - 1);   // slot 1 is entry `first`
        _known[key] = baseIndex;
        return baseIndex;
    }

    /// <summary>
    /// Three regions of <see cref="_scratchSlots"/> entries (the frames in flight and one more) that <see cref="Scratch"/> rewrites in turn.
    /// </summary>
    int _scratchFirst, _scratchSlots, _scratchTurn;

    /// <summary>
    /// The base for a morph that changes every frame (the character editor's blend between two looks): <paramref name="deltas"/> written into the
    /// next of three regions that are reused, so a frame in flight never reads the entries this one writes and the arena does not grow with every
    /// frame. Once per frame.
    /// </summary>
    public uint Scratch(Vector4[] deltas)
    {
        if (deltas.Length > _scratchSlots)
        {
            _scratchFirst = Reserve(3 * deltas.Length);
            _scratchSlots = deltas.Length;
        }
        int first = _scratchFirst + _scratchTurn++ % 3 * _scratchSlots;
        Write(first, deltas);
        return (uint)(first - 1);
    }

    /// <summary>
    /// Makes room for <paramref name="count"/> entries at the end and returns where they start; growing makes a new buffer and rewrites every set
    /// from the host copy.
    /// </summary>
    int Reserve(int count)
    {
        int first = _used;
        _used += count;
        if (_used > _host.Length)
        {
            Array.Resize(ref _host, Math.Max(_host.Length * 2, _used));
            _buffer.Dispose();
            _buffer = DeviceBuffer.Create(_gpu, (ulong)_host.Length * 16, BufferUse.Storage, "character morphs");
            Upload(0, MemoryMarshal.AsBytes(_host.AsSpan(0, first)));
        }
        return first;
    }

    void Write(int first, Vector4[] deltas)
    {
        deltas.CopyTo(_host, first);
        Upload((ulong)first * 16, MemoryMarshal.AsBytes(deltas.AsSpan()));
    }

    void Upload(ulong offset, ReadOnlySpan<byte> data)
    {
        _gpu.EnsureFrame();
        using var batch = _gpu.Uploads.Begin();
        batch.Write(_buffer, offset, data);
    }

    public GpuBufferBinding Binding(IGpuFrame frame) => _buffer.Binding(frame);

    public void Dispose() => _buffer.Dispose();
}