using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.World;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation.Production;

/// <summary>
/// Seats that play a bed's clip (a BF_CHAIR such as Universal Wasteland Expansion's "Bed (job)", <c>sleeping (job)</c> with <c>sleepinbed</c>): with
/// <see cref="World.SeatBedFix"/> the sitter lies at the use node nearest it, turned as a bed's sleeper (<see cref="FixtureRegistry.LieSpot"/>,
/// docs/game/ai-tasks.md "Town goals"). The game seats every chair user on the building's origin facing its +Z, and <c>sleepinbed</c> holds
/// <c>Bip01</c> 11.8 to the body's side, so from the origin the body lay across the foot of the bed.
/// </summary>
sealed class SeatBeds(GameDatabase db)
{
    /// <summary>
    /// The clips of the BUILDING_FUNCTIONALITY records with <c>function</c> 6 BF_BED or 25 BF_SKELETON_BED, read on first use.
    /// </summary>
    HashSet<string>? _bedClips;

    /// <summary>
    /// Each BUILDING record's use nodes in its own frame (<see cref="FixtureRegistry.UseNodes"/>; empty for none). Filled lazily from any phase.
    /// </summary>
    readonly ConcurrentDictionary<string, UseNode[]> _nodes = new(StringComparer.Ordinal);

    /// <summary>
    /// Where a seat site playing <paramref name="clip"/> lies its sitter and the way it faces: the use node nearest <paramref name="from"/> (squared
    /// 3D distance), facing the site's yaw plus the node's (the node's own yaw - 90°). Null when the clip is no bed's or the building has no use node.
    /// </summary>
    public (Vector3 At, float Yaw)? LieSpot(BuildSite site, string clip, Vector3 from)
    {
        if (!IsBedClip(clip) || NodesOf(site.Record) is not { Length: > 0 } nodes) return null;

        float yaw = TownBuildings.YawOf(site.Rotation);
        var turn = Matrix4x4.CreateRotationY(yaw);
        (Vector3 At, float Yaw)? best = null;
        float bestDistance = float.MaxValue;
        foreach (var node in nodes)
        {
            var at = site.Position + Vector3.Transform(node.At, turn);
            float d = Vector3.DistanceSquared(at, from);
            if (d >= bestDistance) continue;
            best = (at, MathF.IEEERemainder(yaw + node.Yaw, MathF.Tau));
            bestDistance = d;
        }

        return best;
    }

    /// <summary>
    /// Whether a bed functionality plays <paramref name="clip"/>.
    /// </summary>
    bool IsBedClip(string clip)
    {
        var clips = Volatile.Read(ref _bedClips);
        if (clips is null)
        {
            clips = ReadBedClips();
            Volatile.Write(ref _bedClips, clips);
        }

        return clips.Contains(clip);
    }

    /// <summary>
    /// The animation names of every bed functionality.
    /// </summary>
    HashSet<string> ReadBedClips()
    {
        var clips = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in db.OfType(FcsRecordType.BuildingFunctionality))
        {
            if (f.GetInt("function") is not (TownBuildings.BedFunction or TownBuildings.SkeletonBedFunction)) continue;
            foreach (var a in f.GetReferences("animation"))
                if (db.Find(a.TargetStringId)?.Name is { } name) clips.Add(name);
        }

        return clips;
    }

    /// <summary>
    /// A record's use nodes, empty for none or an unknown record.
    /// </summary>
    UseNode[] NodesOf(string record) =>
        _nodes.GetOrAdd(record, id => db.Find(id) is { } building ? FixtureRegistry.UseNodes(db, building) ?? [] : []);
}
