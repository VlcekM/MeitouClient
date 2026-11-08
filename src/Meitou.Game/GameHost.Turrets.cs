using Meitou.Data.Gameplay.Combat;
using Meitou.Game.Rendering.Objects;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The turrets' turning parts from the snapshot to the object renderer (<see cref="WorldObjectRenderer.Turrets"/>, docs/simulation.md "Combat", turrets): a manned, simulated turret
/// (<see cref="MannedTurret.Angles"/>) is drawn with its parts turned by the angles of the last two snapshots, interpolated by the simulation's alpha. Display only. A turret keeps its last
/// turn when its gunner leaves (the building keeps the turn, as the simulation's turret does); a world change drops them all.
/// </summary>
sealed partial class GameHost
{
    readonly Dictionary<string, TurretPose> turretPoses = new(StringComparer.Ordinal);
    readonly Dictionary<string, float[]> previousTurretAngles = new(StringComparer.Ordinal);
    // The turrets whose records are missing (not retried).
    readonly HashSet<string> turretPoseMissing = new(StringComparer.Ordinal);
    World? turretPosesWorld;

    /// <summary>Once a frame, with the buildings.</summary>
    internal void UpdateTurretPoses(WorldSnapshot snapshot)
    {
        if (gpu?.Objects is not { } objects || scene.Database is not { } db) return;
        if (!ReferenceEquals(turretPosesWorld, session.World))
        {
            turretPosesWorld = session.World;
            turretPoses.Clear();
            turretPoseMissing.Clear();
            objects.Turrets = null;
        }
        previousTurretAngles.Clear();
        foreach (var c in session.PreviousSnapshot.Characters)
            if (c.Turret is { Simulated: true, Angles: { } before }) previousTurretAngles[c.Turret.Key] = before;
        float alpha = session.SimulationAlpha;
        bool added = false;
        foreach (var c in snapshot.Characters)
        {
            if (c.Turret is not { Simulated: true, Angles: { } angles } post || turretPoseMissing.Contains(post.Key)) continue;
            if (!turretPoses.TryGetValue(post.Key, out var pose))
            {
                if (db.Find(post.BuildingId) is not { } building || TurretGun.From(db, building) is not { } gun) { turretPoseMissing.Add(post.Key); continue; }
                turretPoses[post.Key] = pose = new TurretPose([.. gun.Parts.Select(p => p.Id)]);
                added = true;
            }
            previousTurretAngles.TryGetValue(post.Key, out var old);
            bool changed = false;
            for (int k = 0; k < pose.Angles.Length && k < angles.Length; k++)
            {
                float shown = old is not null && k < old.Length ? old[k] + (angles[k] - old[k]) * alpha : angles[k];
                if (shown == pose.Angles[k]) continue;
                pose.Angles[k] = shown;
                changed = true;
            }
            if (changed) pose.Version++;
        }
        if (added) objects.Turrets = new Dictionary<string, TurretPose>(turretPoses, StringComparer.Ordinal);
    }
}
