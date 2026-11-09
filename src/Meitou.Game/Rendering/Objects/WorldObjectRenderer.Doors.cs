using Meitou.Game.Rendering.Post;
using Meitou.Game.Rendering.Frame;
using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data.Ogre;
using Meitou.Data.Physics;
using Meitou.Data.World;
using Meitou.Game.Rendering.Gpu;

namespace Meitou.Game.Rendering.Objects;

public sealed partial class WorldObjectRenderer
{
    // ---- motion vectors for the temporal upscalers: the animated instances (doors, turret joints, rotors) are drawn again into the motion target with this and last frame's transform ----

    /// <summary>
    /// Set each frame by the world frame when a temporal upscaler runs (<see cref="BeginMotion"/>).
    /// </summary>
    public bool Motion { get; private set; }

    readonly record struct MotionDraw(MeshBindings Mesh, int IndexCount, Matrix4x4 Model, Matrix4x4 Previous);

    readonly List<MotionDraw> _motionDraws = [];
    MotionMeshes? _motionMeshes;
    Vector3 _motionEye;
    long _motionFrame;

    /// <summary>
    /// Starts a frame for the scene camera at <paramref name="eye"/> (the only view that writes motion).
    /// </summary>
    public void BeginMotion(bool on, Vector3 eye)
    {
        _motionDraws.Clear();
        Motion = on;
        _motionEye = eye;
        _motionFrame++;
    }

    /// <summary>
    /// An animated instance drawn in the scene view: with a transform from the last frame that differs from this frame's, its meshes are noted with both, relative to the eye. One that
    /// was not drawn last frame (new, or back from out of range) has no motion from before.
    /// </summary>
    void NoteMotion(ObjectStreamer.Instance inst, GpuObjectMesh gpu, float lodValue)
    {
        var now = inst.Transform;
        var before = inst.LastDrawnFrame == _motionFrame - 1 ? inst.LastDrawn : now;
        if (inst.LastDrawnFrame != _motionFrame) (inst.LastDrawn, inst.LastDrawnFrame) = (now, _motionFrame);
        if (before == now) return;

        var toEye = Matrix4x4.CreateTranslation(-_motionEye);
        int level = gpu.Curve.Select(lodValue * LodBias);
        var g = gpu.Manual[level] ?? gpu;
        int lv = gpu.Manual[level] is null ? level : 0;
        foreach (var gp in g.Parts)
        {
            if (gp.Count[lv] > 0) _motionDraws.Add(new MotionDraw(_meshes.PlainMesh(gp, lv), gp.Count[lv], now * toEye, before * toEye));
        }
    }

    /// <summary>
    /// Draws the noted instances into the motion target (the post chain's object-motion pass).
    /// </summary>
    internal void DrawMotion(MotionTargets targets, MotionCamera camera)
    {
        if (!Motion || _motionDraws.Count == 0) return;

        var m = _motionMeshes ??= new MotionMeshes(Gpu, _nativeFrame, GpuVertex.ObjectSize);
        m.Begin(targets, camera);
        foreach (var d in _motionDraws)
        {
            if (d.Mesh.Attributes[0] is not { } position || d.Mesh.Attributes[2] is not { } uv) continue;
            m.Mesh(position.Buffer, uv.Buffer, d.Mesh.Elements);
            m.Draw((uint)d.IndexCount, 0, 0, d.Model, d.Previous, Gpu.StandIn2D, 0);
        }

        m.End();
        _motionDraws.Clear();
    }

    readonly AssetLocator _assets;
    readonly ConcurrentDictionary<string, DoorFrame> _doorFrames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The open amount (0..1) of each door that is not closed, by (placement id, index in the building's <c>doors</c> list); a door missing is drawn
    /// closed. Read on the render thread; the host replaces it as it likes (docs/render-objects.md "Doors").
    /// </summary>
    public IReadOnlyDictionary<(string Placement, int Door), float>? Doors { get; set; }

    /// <summary>
    /// Moves a door part to its open amount (<see cref="DoorLeaf.LocalOffset"/>, along its collision box's axes in world units), from its closed
    /// placement.
    /// </summary>
    void PoseDoor(ObjectStreamer.Instance inst, GpuObjectMesh gpu)
    {
        float doa = Doors is { } doors && doors.TryGetValue((inst.Placed.PlacementId, inst.Placed.DoorIndex), out float d) ? Math.Clamp(d, 0, 1) : 0;
        if (doa == inst.DoorOpen) return;
        inst.DoorOpen = doa;
        var m = inst.Placed.Transform;
        if (doa > 0)
        {
            var leaf = DoorLeaf.Of(inst.Placed.Owner);
            var local = leaf.LocalOffset(doa, _doorFrames.GetOrAdd(inst.Placed.Source.GetPath("xml collision"), DoorFrameOf));
            var shift = Unit(m.M11, m.M12, m.M13) * local.X + Unit(m.M21, m.M22, m.M23) * local.Y + Unit(m.M31, m.M32, m.M33) * local.Z;
            m.M41 += shift.X; m.M42 += shift.Y; m.M43 += shift.Z;
        }
        inst.Transform = m;
        inst.Centre = Vector3.Transform(gpu.Centre, m);

        static Vector3 Unit(float x, float y, float z) => new Vector3(x, y,
            z) is var v && v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : Vector3.Zero;
    }

    /// <summary>
    /// A mesh's local bounds from its file's bounds chunk (empty when the file is not found).
    /// </summary>
    (Vector3, Vector3) MeshBounds(string name)
    {
        var path = _assets.Find(name) ?? _assets.Find(Path.GetFileName(name.Replace('\\', '/')));
        return path is not null && OgreMeshReader.TryReadBounds(path, out var b) && b.Max.X >= b.Min.X ? (b.Min, b.Max) : default;
    }

    /// <summary>
    /// A door part's collision box frame from its <c>xml collision</c> file (<see cref="DoorFrame.Default"/> when the file is missing or unreadable).
    /// </summary>
    DoorFrame DoorFrameOf(string recordPath)
    {
        if (string.IsNullOrWhiteSpace(recordPath) || FindRecordFile(recordPath) is not { } path) return DoorFrame.Default;
        try { return DoorFrame.Of(CollisionFile.ReadFile(path)); }
        catch (Exception e) when (e is InvalidDataException or IOException or System.Xml.XmlException or FormatException) { return DoorFrame.Default; }
    }
}