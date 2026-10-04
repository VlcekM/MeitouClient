using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data.World;

/// <summary>What placed a <see cref="PlacedMesh"/>.</summary>
public enum PlacedKind { BuildingPart, MapFeature }

/// <param name="MeshPath">The mesh file as stored in the record (an install-relative <c>.\data\...</c> path).</param>
/// <param name="Transform">Mesh space to world (System.Numerics row-vector convention: <c>world = local × Transform</c>).</param>
/// <param name="Source">The record naming the mesh: a BUILDING_PART or MAP_FEATURES record.</param>
/// <param name="Owner">The placed record: the BUILDING, or the MAP_FEATURES record again.</param>
public sealed record PlacedMesh(string MeshPath, Matrix4x4 Transform, GameRecord Source, GameRecord Owner, string PlacementId, PlacedKind Kind);

/// <summary>
/// Turns world placements into meshes with world transforms (docs/formats/zones.md, "From placements to meshes"):
/// a BUILDING is the BUILDING_PART records of its <c>parts</c> list (picked by group and chance, recursively),
/// each drawing its <c>phs or mesh</c> moved by <c>offset X/Y/Z</c>; the whole building is scaled by
/// <c>scale</c>, rotated by the placement's quaternion and moved to its position. A map feature draws its
/// MAP_FEATURES <c>mesh</c> scaled, rotated and moved by its <c>features.dat</c> entry.
/// </summary>
public static class WorldObjectLayout
{
    /// <summary>Local scale, then rotation, then translation.</summary>
    public static Matrix4x4 InstanceTransform(Vector3 position, Quaternion rotation, Vector3 scale) =>
        Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(Normalized(rotation)) * Matrix4x4.CreateTranslation(position);

    static Quaternion Normalized(Quaternion q) => q.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(q);

    /// <summary>
    /// Meshes of a placed building. <paramref name="worldPosition"/> is the absolute position (for zone placements:
    /// X/Z of the instance and the state's <c>world Y pos</c>, or terrain height + instance Y).
    /// </summary>
    public static List<PlacedMesh> Building(GameDatabase db, GameRecord building, string placementId, Vector3 worldPosition, Quaternion rotation)
    {
        var result = new List<PlacedMesh>();
        if (building.GetBool("is node")) return result;
        float scale = building.GetFloat("scale", 1);
        if (scale <= 0) scale = 1;
        var placement = InstanceTransform(worldPosition, rotation, new Vector3(scale));
        AddParts(db, building, building, placementId, Vector3.Zero, placement, result, Seed(placementId), 0);
        return result;
    }

    static void AddParts(GameDatabase db, GameRecord owner, GameRecord holder, string placementId, Vector3 parentOffset, Matrix4x4 placement,
        List<PlacedMesh> result, uint seed, int depth)
    {
        if (depth > 4) return;
        foreach (var r in ChooseParts(holder.GetReferences("parts"), seed + (uint)depth * 7919))
        {
            if (db.Find(r.TargetStringId) is not { Type: FcsRecordType.BUILDING_PART } part) continue;
            var offset = parentOffset;
            if (!part.GetBool("is for position marker"))
                offset += new Vector3(part.GetFloat("offset X"), part.GetFloat("offset Y"), part.GetFloat("offset Z"));
            var mesh = part.GetPath("phs or mesh");
            if (mesh.EndsWith(".mesh", StringComparison.OrdinalIgnoreCase))
                result.Add(new PlacedMesh(mesh, Matrix4x4.CreateTranslation(offset) * placement, part, owner, placementId, PlacedKind.BuildingPart));
            AddParts(db, owner, part, placementId, offset, placement, result, seed * 31 + 17, depth + 1);
        }
    }

    /// <summary>
    /// The <c>parts</c> entries a building uses (fcs.def): val0 is the group, val1 the chance. Group 0 entries are each
    /// kept with val1 as an absolute percentage; from every other group one entry is chosen, weighted by val1.
    /// The game rolls at random; here the roll comes from <paramref name="seed"/> so a placement always looks the same.
    /// </summary>
    public static List<GameReference> ChooseParts(IReadOnlyList<GameReference> parts, uint seed)
    {
        var result = new List<GameReference>();
        var rng = new Random((int)seed);
        foreach (var group in parts.GroupBy(p => p.Values.Value0).OrderBy(g => g.Key))
        {
            if (group.Key == 0)
            {
                foreach (var p in group)
                    if (p.Values.Value1 >= 100 || rng.Next(100) < p.Values.Value1) result.Add(p);
                continue;
            }
            var list = group.ToList();
            int total = list.Sum(p => Math.Max(p.Values.Value1, 0));
            if (total <= 0) { result.Add(list[0]); continue; }
            int roll = rng.Next(total);
            foreach (var p in list)
            {
                roll -= Math.Max(p.Values.Value1, 0);
                if (roll < 0) { result.Add(p); break; }
            }
        }
        return result;
    }

    /// <summary>The mesh of a <c>features.dat</c> entry, or null when its record is missing, hidden or meshless.</summary>
    public static PlacedMesh? Feature(GameDatabase db, MapFeature feature, string placementId)
    {
        if (db.Find(feature.StringId) is not { Type: FcsRecordType.MAP_FEATURES } record) return null;
        if (record.GetBool("hidden")) return null;
        var mesh = record.GetPath("mesh");
        if (!mesh.EndsWith(".mesh", StringComparison.OrdinalIgnoreCase)) return null;
        return new PlacedMesh(mesh, InstanceTransform(feature.Position, feature.Rotation, feature.Scale), record, record, placementId, PlacedKind.MapFeature);
    }

    /// <summary>A stable 32-bit hash (FNV-1a) of a placement id.</summary>
    public static uint Seed(string id)
    {
        uint h = 2166136261;
        foreach (char c in id) h = (h ^ c) * 16777619;
        return h;
    }
}
