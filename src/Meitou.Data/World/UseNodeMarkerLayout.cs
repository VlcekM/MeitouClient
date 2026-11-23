using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data.World;

/// <summary>
/// The markers a building's placement ghost shows at its usage nodes (docs/game/buildings-production.md "Usage node markers", <b>Verified</b>
/// by decompile, the preview's setup <c>FUN_1404dde30</c>): the building and every record its <c>parts</c> lists name, recursively and whatever
/// their chance, hold instances; each whose target has <c>node type</c> 4 (base data: <c>n-use node</c>) is drawn as the mesh of that node
/// record's first part (<c>nodemarker</c>, <c>nodemarker.mesh</c>) at the instance's position and rotation in the ghost's space, unscaled.
/// </summary>
public static class UseNodeMarkerLayout
{
    /// <summary>
    /// The <c>node type</c> of a usage node (the original's NODE_USE).
    /// </summary>
    public const int UseNodeType = 4;

    /// <summary>
    /// How deep the <c>parts</c> lists are followed (a guard against a list that names itself).
    /// </summary>
    const int MaxDepth = 8;

    /// <summary>
    /// The usage node markers of <paramref name="building"/> laid out at the origin with no rotation, in the order the original makes them (the
    /// building, then its parts' records in list order; each record's instances in id order). A node whose record has no part with a mesh has no
    /// marker (the original would look up an empty record id).
    /// </summary>
    public static List<PlacedMesh> Markers(GameDatabase db, GameRecord building)
    {
        var markers = new List<PlacedMesh>();
        foreach (var holder in Holders(db, building))
            AddMarkers(db, building, holder, markers);

        return markers;
    }

    /// <summary>
    /// The building and every BUILDING_PART its <c>parts</c> lists name, breadth first, each once.
    /// </summary>
    static List<GameRecord> Holders(GameDatabase db, GameRecord building)
    {
        var holders = new List<GameRecord> { building };
        var seen = new HashSet<string>(StringComparer.Ordinal) { building.StringId };
        int start = 0;
        for (int depth = 0; depth < MaxDepth && start < holders.Count; depth++)
        {
            int end = holders.Count;
            for (int i = start; i < end; i++)
            {
                foreach (var reference in holders[i].GetReferences("parts"))
                {
                    if (db.Find(reference.TargetStringId) is { } part && seen.Add(part.StringId)) holders.Add(part);
                }
            }
            start = end;
        }

        return holders;
    }

    /// <summary>
    /// Adds a marker for each of <paramref name="holder"/>'s usage node instances.
    /// </summary>
    static void AddMarkers(GameDatabase db, GameRecord building, GameRecord holder, List<PlacedMesh> markers)
    {
        foreach (var instance in holder.Instances.Values.Where(i => !i.IsCleared).OrderBy(i => i.Id, StringComparer.Ordinal))
        {
            if (db.Find(instance.Target) is not { } node || node.GetInt("node type") != UseNodeType) continue;
            if (MarkerPart(db, node) is not { } part) continue;

            var transform = Matrix4x4.CreateFromQuaternion(Normalized(instance.Rotation)) * Matrix4x4.CreateTranslation(instance.Position);
            markers.Add(new PlacedMesh(part.GetPath("phs or mesh"), transform, part, building, "usage node:" + instance.Id, PlacedKind.BuildingPart)
            {
                Material = WorldObjectLayout.ResolveCollection(db, db.Find(WorldObjectLayout.FallbackPartMaterial), part.StringId),
            });
        }
    }

    /// <summary>
    /// The node record's first part when it names a mesh; null otherwise.
    /// </summary>
    static GameRecord? MarkerPart(GameDatabase db, GameRecord node)
    {
        if (node.GetReferences("parts") is not { Count: > 0 } parts) return null;
        if (db.Find(parts[0].TargetStringId) is not { Type: FcsRecordType.BuildingPart } part) return null;

        string mesh = part.GetPath("phs or mesh");
        return mesh.EndsWith(".mesh", StringComparison.OrdinalIgnoreCase) ? part : null;
    }

    /// <summary>
    /// <paramref name="q"/> normalised; identity for a zero quaternion.
    /// </summary>
    static Quaternion Normalized(Quaternion q) => q.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(q);
}
