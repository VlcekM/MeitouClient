using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Game.Rendering.Objects;

/// <summary>
/// The turned poses of the parts with joints (<see cref="PlacedMesh.Joints"/>): a turret's parts by its <see cref="TurretPose"/>, a rotating part
/// by <see cref="RotatingParts"/> (docs/render-objects.md "Turrets" and "Rotating parts").
/// </summary>
sealed class JointPoses(RotatingParts rotors)
{
    /// <summary>
    /// The turn of each turret that has turned, by placement id (a town building's key, a player site's id): the parts of its meshes with
    /// <see cref="PlacedMesh.Joints"/> are drawn turned about their joints (docs/render-objects.md "Turrets"). A turret missing is drawn as placed.
    /// Read on the render thread; the host replaces or edits it.
    /// </summary>
    public IReadOnlyDictionary<string, TurretPose>? Turrets { get; set; }

    /// <summary>
    /// Turns a turret part or a rotating part (<see cref="RotatingParts.Advance"/>) to its pose: its placement, then a turn about each joint above it
    /// and its own, the innermost first (the joints are stored outermost first). A mesh under a rotor is posed again each frame, a turret's only when
    /// its pose changes.
    /// </summary>
    public void Pose(ObjectStreamer.Instance inst, GpuObjectMesh gpu)
    {
        var pose = Turrets is { } turrets && turrets.TryGetValue(inst.Placed.PlacementId, out var found) ? found : null;
        int version = pose?.Version ?? 0;
        var joints = inst.Placed.Joints!;
        bool rotor = false;
        foreach (var j in joints) rotor |= j.Rotor is not null;
        if (version == inst.TurretStamp && (!rotor || inst.RotorFrame == rotors.Frame)) return;

        inst.TurretStamp = version;
        inst.RotorFrame = rotors.Frame;
        var m = inst.Placed.Transform;
        if (pose is not null || rotor)
        {
            for (int k = joints.Count - 1; k >= 0; k--)
            {
                float angle = joints[k].Rotor is not null ? rotors.Turn(inst.Placed, joints[k]) : pose?.AngleOf(joints[k].PartId) ?? 0;
                if (angle == 0) continue;

                m *= Matrix4x4.CreateTranslation(-joints[k].Pivot) * Matrix4x4.CreateFromAxisAngle(joints[k].Axis,
                    angle) * Matrix4x4.CreateTranslation(joints[k].Pivot);
            }
        }
        inst.Transform = m;
        inst.Centre = Vector3.Transform(gpu.Centre, m);
    }
}
