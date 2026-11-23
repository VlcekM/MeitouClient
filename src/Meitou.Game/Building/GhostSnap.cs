using System.Numerics;
using Meitou.Data.Gameplay.Construction;

namespace Meitou.Game.Building;

/// <summary>
/// The building that carries a snap node (<c>is node</c> instance such as <c>Node Light Mount</c>): its placement id, the parent instance id a
/// placed attachment records, whether it is a level building, and the ownership flags <see cref="PlacementRules.MayBuildIn"/> reads.
/// </summary>
public readonly record struct SnapHost(string Id, string Parent, bool IsLevel, bool Owned, bool Destroyed, bool OwnerNotReal);

/// <summary>
/// A building or building snap node of a <c>snaps to</c> ghost's target records, by record, position and turn: what the ghost may take the place of.
/// <see cref="Host"/> is set for an <c>is node</c> attachment point on another building, null for a standalone building target.
/// </summary>
public readonly record struct SnapTarget(string Record, Vector3 Position, Quaternion Rotation, SnapHost? Host = null);

/// <summary>
/// The snap of a <c>snaps to</c> ghost (docs/game/buildings-production.md "Snapping to a building"): the nearest building or building snap node of
/// one of its target records round the cursor, whose position and turn the ghost takes. A ramp's mesh is laid out in its wall's own space, so in the
/// wall's place it stands against the wall (Wall Ramp beside Concrete Wall's -X face, Defensive Wall Ramp IV at the middle of its wall); a wall
/// light sits on a <c>Node Light Mount</c> instance carried by its host building, a turret on a <c>Node Turret</c> one.
/// </summary>
public static class GhostSnap
{
    /// <summary>
    /// How far from the cursor (X/Z) a standalone target's ghost footprint centre may stand for the ghost to snap there (engine choice).
    /// </summary>
    public const float Reach = 150;

    /// <summary>
    /// How far from the cursor (3D) a snap node may stand to snap there (<b>Verified</b>, <c>0x1404dd7e8</c>: the node search of
    /// <c>PreviewBuilding_Snapping</c> takes radius 300).
    /// </summary>
    public const float NodeReach = 300;

    /// <summary>
    /// <see cref="NodeReach"/> for a building with a <c>gun turret</c> (<b>Verified</b>, <c>0x1404dd86d</c>: 60).
    /// </summary>
    public const float TurretNodeReach = 60;

    /// <summary>
    /// Perpendicular distance from the pointer ray within which hovering a blue snap node snaps directly to it, even when the terrain behind the
    /// building wall lies far away (engine choice; blue node spheres have radius 5).
    /// </summary>
    const float NodeRayPickRadius = 14;

    /// <summary>
    /// The target of <paramref name="candidates"/> that the ghost of <paramref name="building"/>, its footprint centre at <paramref name="centre"/>
    /// in its own space (X, Z), takes for the cursor at <paramref name="cursor"/>: of the buildings of its <c>snaps to</c> records, the one that puts
    /// that centre nearest the cursor within <see cref="Reach"/>; of snap nodes (collected for its node type), the nearest within
    /// <see cref="NodeReach"/> (<see cref="TurretNodeReach"/> for a turret) in 3D, direct ray hover preferred when <paramref name="ray"/> is given;
    /// null when none does.
    /// </summary>
    public static SnapTarget? Find(BuildingBuildData building, Vector2 centre, Vector3 cursor, IEnumerable<SnapTarget> candidates,
        (Vector3 Origin, Vector3 Direction)? ray = null)
    {
        SnapTarget? rayBest = null;
        float rayBestPerp = NodeRayPickRadius;
        float rayBestT = float.PositiveInfinity;

        SnapTarget? best = null;
        float nearest = float.PositiveInfinity;

        float nodeReach = building.HasGunTurret ? TurretNodeReach : NodeReach;
        foreach (var target in candidates)
        {
            if (target.Host is null && !building.SnapsTo.Contains(target.Record)) continue;

            if (target.Host is not null)
            {
                if (ray is { } r)
                {
                    var to = target.Position - r.Origin;
                    float t = Vector3.Dot(to, r.Direction);
                    if (t > 0)
                    {
                        float perp = Vector3.Distance(target.Position, r.Origin + r.Direction * t);
                        if (perp < rayBestPerp - 0.5f || MathF.Abs(perp - rayBestPerp) <= 0.5f && t < rayBestT)
                            (rayBest, rayBestPerp, rayBestT) = (target, perp, t);
                    }
                }

                float distance = Vector3.Distance(target.Position, cursor);
                if (distance < nodeReach && distance < nearest) (best, nearest) = (target, distance);
            }
            else
            {
                var at = target.Position + Vector3.Transform(new Vector3(centre.X, 0, centre.Y), target.Rotation);
                float distance = Vector2.Distance(new Vector2(at.X, at.Z), new Vector2(cursor.X, cursor.Z));
                if (distance < Reach && distance < nearest) (best, nearest) = (target, distance);
            }
        }

        return rayBest ?? best;
    }
}
