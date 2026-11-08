using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data.Ogre;
using Meitou.Data.Physics;
using Meitou.Data.World;

namespace Meitou.Rendering;

public sealed partial class WorldObjectRenderer
{
    readonly AssetLocator assets;
    readonly ConcurrentDictionary<string, DoorFrame> doorFrames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The open amount (0..1) of each door that is not closed, by (placement id, index in the building's <c>doors</c> list); a door missing is drawn closed. Read on the
    /// render thread; the host replaces it as it likes (docs/render-objects.md "Doors").
    /// </summary>
    public IReadOnlyDictionary<(string Placement, int Door), float>? Doors { get; set; }

    /// <summary>Moves a door part to its open amount (<see cref="DoorLeaf.LocalOffset"/>, along its collision box's axes in world units), from its closed placement.</summary>
    void PoseDoor(ObjectStreamer.Instance inst, GpuObjectMesh gpu)
    {
        float doa = Doors is { } doors && doors.TryGetValue((inst.Placed.PlacementId, inst.Placed.DoorIndex), out float d) ? Math.Clamp(d, 0, 1) : 0;
        if (doa == inst.DoorOpen) return;
        inst.DoorOpen = doa;
        var m = inst.Placed.Transform;
        if (doa > 0)
        {
            var leaf = DoorLeaf.Of(inst.Placed.Owner);
            var local = leaf.LocalOffset(doa, doorFrames.GetOrAdd(inst.Placed.Source.GetPath("xml collision"), DoorFrameOf));
            var shift = Unit(m.M11, m.M12, m.M13) * local.X + Unit(m.M21, m.M22, m.M23) * local.Y + Unit(m.M31, m.M32, m.M33) * local.Z;
            m.M41 += shift.X; m.M42 += shift.Y; m.M43 += shift.Z;
        }
        inst.Transform = m;
        inst.Centre = Vector3.Transform(gpu.Centre, m);

        static Vector3 Unit(float x, float y, float z) => new Vector3(x, y, z) is var v && v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : Vector3.Zero;
    }

    /// <summary>A mesh's local bounds from its file's bounds chunk (empty when the file is not found).</summary>
    (Vector3, Vector3) MeshBounds(string name)
    {
        var path = assets.Find(name) ?? assets.Find(Path.GetFileName(name.Replace('\\', '/')));
        return path is not null && OgreMeshReader.TryReadBounds(path, out var b) && b.Max.X >= b.Min.X ? (b.Min, b.Max) : default;
    }

    /// <summary>A door part's collision box frame from its <c>xml collision</c> file (<see cref="DoorFrame.Default"/> when the file is missing or unreadable).</summary>
    DoorFrame DoorFrameOf(string recordPath)
    {
        if (string.IsNullOrWhiteSpace(recordPath) || FindRecordFile(recordPath) is not { } path) return DoorFrame.Default;
        try { return DoorFrame.Of(CollisionFile.ReadFile(path)); }
        catch (Exception e) when (e is InvalidDataException or IOException or System.Xml.XmlException or FormatException) { return DoorFrame.Default; }
    }
}
