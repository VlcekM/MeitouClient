using System.Numerics;
using Meitou.Data.Gameplay.Construction;

namespace Meitou.Game.Building;

/// <summary>
/// A building of a <c>snaps to</c> ghost's target records, by record, position and turn: what the ghost may take the place of.
/// </summary>
public readonly record struct SnapTarget(string Record, Vector3 Position, Quaternion Rotation);

/// <summary>
/// The snap of a <c>snaps to</c> ghost (docs/game/buildings-production.md "Snapping to a building"): the nearest building of one of its target records
/// round the cursor, whose position and turn the ghost takes. A ramp's mesh is laid out in its wall's own space, so in the wall's place it stands
/// against the wall (Wall Ramp beside Concrete Wall's -X face, Defensive Wall Ramp IV at the middle of its wall).
/// </summary>
public static class GhostSnap
{
    /// <summary>
    /// How far from the cursor (X/Z) the ghost's footprint centre may stand at a target for the ghost to snap there (engine choice; the original's
    /// reach is <b>Unknown</b>).
    /// </summary>
    public const float Reach = 150;

    /// <summary>
    /// The target of <paramref name="candidates"/> that the ghost of <paramref name="building"/>, its footprint centre at <paramref name="centre"/>
    /// in its own space (X, Z), takes for the cursor at <paramref name="cursor"/>: of those of its <c>snaps to</c> records, the one that puts that
    /// centre nearest the cursor within <see cref="Reach"/>; null when none does.
    /// </summary>
    public static SnapTarget? Find(BuildingBuildData building, Vector2 centre, Vector3 cursor, IEnumerable<SnapTarget> candidates)
    {
        SnapTarget? best = null;
        float nearest = Reach;
        foreach (var target in candidates)
        {
            if (!building.SnapsTo.Contains(target.Record)) continue;

            var at = target.Position + Vector3.Transform(new Vector3(centre.X, 0, centre.Y), target.Rotation);
            float distance = Vector2.Distance(new Vector2(at.X, at.Z), new Vector2(cursor.X, cursor.Z));
            if (distance < nearest) (best, nearest) = (target, distance);
        }

        return best;
    }
}
