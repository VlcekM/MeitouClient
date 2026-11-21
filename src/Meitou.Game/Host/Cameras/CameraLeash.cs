using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Game.Host.Cameras;

/// <summary>
/// The game's camera leash (docs/formats/camera.md "Leash"): each tick a camera farther than <see cref="KenshiCamera.LeashRadius"/> from the
/// nearest anchor (the player's characters) is put back on that sphere, along the line from the anchor. The strategy camera's pivot is pulled;
/// engine choice: the free camera's eye is pulled the same way.
/// </summary>
public sealed class CameraLeash
{
    /// <summary>
    /// Whether the leash holds this tick (set by the host from the setting and the scenes that move the camera themselves).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The points the camera is kept near; none (or no list) leaves it free.
    /// </summary>
    public Func<IReadOnlyList<Vector3>>? Anchors { get; set; }

    /// <summary>
    /// Pulls the active camera of <paramref name="rig"/> back within the radius of the nearest anchor.
    /// </summary>
    internal void Apply(CameraRig rig)
    {
        if (!Enabled || Anchors?.Invoke() is not { Count: > 0 } anchors) return;

        var point = rig.IsFree ? rig.Free.Position : rig.Strategy.Pivot;
        if (Pulled(point, anchors) is not { } pulled) return;

        var strategy = rig.Strategy;
        if (rig.IsFree)
            rig.Free.Leash(pulled);
        else
            strategy.Place(new Vector2(pulled.X, pulled.Z), strategy.Yaw, strategy.Pitch, strategy.Distance, strategy.Pivot.Y);
    }

    /// <summary>
    /// <paramref name="point"/> put on the leash sphere round the nearest of <paramref name="anchors"/>, or null when it is within the radius.
    /// </summary>
    static Vector3? Pulled(Vector3 point, IReadOnlyList<Vector3> anchors)
    {
        var nearest = anchors[0];
        float best = Vector3.DistanceSquared(point, nearest);
        for (int i = 1; i < anchors.Count; i++)
        {
            float d = Vector3.DistanceSquared(point, anchors[i]);
            if (d >= best) continue;

            (nearest, best) = (anchors[i], d);
        }

        if (best <= KenshiCamera.LeashRadius * KenshiCamera.LeashRadius) return null;

        return nearest + Vector3.Normalize(point - nearest) * KenshiCamera.LeashRadius;
    }
}
