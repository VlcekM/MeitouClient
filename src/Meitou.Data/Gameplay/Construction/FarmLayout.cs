using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.World;

namespace Meitou.Data.Gameplay.Construction;

/// <summary>
/// A FARM_PART of a farm's <c>plants</c> list (docs/game/buildings-production.md "Farm plants"): a crop mesh, its share of the plants
/// (<paramref name="Group"/> v0, <paramref name="Weight"/> v1) and how it grows: scale and height offset run from start to end over the plant's
/// growth after <paramref name="Delay"/>.
/// </summary>
public sealed record FarmPart(
    GameRecord Record, string Mesh, GameRecord? Material, int Group, int Weight, float ScaleStart, float ScaleEnd, float ScaleVariance,
    float OffsetStart, float OffsetEnd, float Delay)
{
    /// <summary>
    /// Neither scale nor offset changes: the part stays when harvested, does not sink or tint when dying and is darkened rather than lightened.
    /// </summary>
    public bool Static => ScaleStart == ScaleEnd && OffsetStart == OffsetEnd;

    /// <summary>
    /// A plant part from its FARM_PART record, its group (v0) and weight (v1), its material resolved through its MATERIAL_SPEC.
    /// </summary>
    public static FarmPart From(GameDatabase db, GameRecord part, int group, int weight) => new(
        part, part.Filenames.GetValueOrDefault("mesh") ?? "",
        part.GetReferences("material") is [var m, ..]
            ? WorldObjectLayout.ResolveCollection(
                db, db.Find(m.TargetStringId) is { Type: FcsRecordType.MaterialSpec } spec ? spec : null, part.StringId)
            : null,
        group, weight, part.GetFloat("scale start"), part.GetFloat("scale end"), part.GetFloat("scale variance"),
        part.GetFloat("offset start"), part.GetFloat("offset end"), part.GetFloat("delay"));
}

/// <summary>
/// One mesh of a plant: the plant's index, the <see cref="FarmData.Parts"/> index and the plant's roll U[0, 1] (its yaw and size variance).
/// </summary>
public readonly record struct FarmSubPlant(int Plant, int Part, float Random);

/// <summary>
/// A farm's plants: their world positions and meshes (one per <c>plants</c> group).
/// </summary>
public sealed record FarmPlants(Vector3[] Positions, FarmSubPlant[] Meshes);

/// <summary>
/// Where a farm's plants stand and how they look as they grow (docs/game/buildings-production.md "Farm plants"; <b>Observed</b>, <c>FarmBuilding</c>
/// layout <c>FUN_1400e6230</c>, part counts <c>FUN_1400e7a70</c>, growth <c>FUN_1400df360</c>, shader <c>farm_vs</c>). The game rolls unseeded; here
/// a seed keeps a farm's layout stable.
/// </summary>
public static class FarmLayout
{
    /// <summary>
    /// The number of plants: a grid (layout 0) has <c>across</c> = round(sqrt(amount) x aspect) (at least 1) columns of amount / across plants, which
    /// can be fewer than <c>amount</c>; the other layouts have <c>amount</c>.
    /// </summary>
    public static int Count(FarmData farm)
    {
        if (farm.Layout != 0) return farm.Plants;
        int across = Across(farm);
        return across * (farm.Plants / across);
    }

    /// <summary>
    /// A grid's column count: round(sqrt(amount) x aspect), at least 1.
    /// </summary>
    static int Across(FarmData farm) => Math.Max(1, (int)(MathF.Sqrt(farm.Plants) * farm.Aspect + 0.5));

    /// <summary>
    /// How many plants each part gets: amount x weight / (its group's total weight), rounded; what rounding leaves over goes to the group's last part
    /// with more than one.
    /// </summary>
    public static int[] PartCounts(FarmData farm)
    {
        var counts = new int[farm.Parts.Count];
        foreach (var group in farm.Parts.Select(p => p.Group).Distinct())
        {
            int total = farm.Parts.Where(p => p.Group == group).Sum(p => p.Weight);
            if (total == 0) continue;
            int left = farm.Plants, last = -1;
            for (int i = 0; i < counts.Length; i++)
            {
                if (farm.Parts[i].Group != group) continue;
                counts[i] = (int)MathF.Floor(farm.Plants * ((float)farm.Parts[i].Weight / total) + 0.5f);
                if (counts[i] > 1) last = i;
                left -= counts[i];
            }
            if (last >= 0) counts[last] += left;
        }
        return counts;
    }

    /// <summary>
    /// Lays out the plants of a farm at <paramref name="position"/> turned by <paramref name="rotation"/>; outdoors each stands on
    /// <paramref name="ground"/> (x, z). Every plant gets one mesh per group, drawn without replacement from the <see cref="PartCounts"/>.
    /// </summary>
    public static FarmPlants Plant(FarmData farm, Vector3 position, Quaternion rotation, Func<float, float, float> ground, uint seed)
    {
        var random = new BuildingRandom(seed);
        float U() => random.Next() / 32767f;
        var positions = new List<Vector3>();
        float s = farm.Spacing;
        int n = farm.Plants;
        if (farm.Layout == 0)
        {
            int across = Across(farm), along = n / across;
            var x = Vector3.Transform(Vector3.UnitX, rotation);
            var z = Vector3.Transform(Vector3.UnitZ, rotation);
            var corner = position - x * ((across - 1) * s * 0.5f) - z * ((along - 1) * s * 0.5f);
            for (int i = 0; i < across; i++)
                for (int k = 0; k < along; k++)
                {
                    float a = (U() - 0.5f) * farm.Jitter * s + i * s;
                    float b = (U() - 0.5f) * farm.Jitter * s + k * s;
                    positions.Add(corner + x * a + z * b);
                }
        }
        else if (farm.Layout == 1)
        {
            float aspect = farm.Aspect == 0 ? 0.001f : farm.Aspect;
            positions.Add(position);
            for (int i = 1; i < n; i++)
            {
                float angle = U() * MathF.Tau;
                float radius = U() * U() * MathF.Sqrt(n) * s + s;
                positions.Add(position + new Vector3(MathF.Sin(angle) * radius * aspect, 0, MathF.Cos(angle) * radius / aspect));
            }
        }
        else
        {
            float w = MathF.Sqrt(n) * farm.Aspect * s, d = MathF.Sqrt(n) / farm.Aspect * s;
            for (int i = 0; i < n; i++)
                positions.Add(position + new Vector3(U() * w - w * 0.5f, 0, U() * d - d * 0.5f));
        }
        if (!farm.Inside)
            for (int i = 0; i < positions.Count; i++)
                positions[i] = positions[i] with { Y = ground(positions[i].X, positions[i].Z) };

        // Each plant, per group: a roll in [0, amount) walks the parts (from the first, round twice at most) taking off their counts; the first part
        // of the group the roll has gone below 0 at that still has plants to give is chosen.
        int[] counts = PartCounts(farm), left = (int[])counts.Clone();
        var groups = farm.Parts.Select(p => p.Group).Distinct().Where(g => counts.Where((_, i) => farm.Parts[i].Group == g).Sum() != 0).ToArray();
        var meshes = new List<FarmSubPlant>();
        for (int plant = 0; plant < positions.Count; plant++)
            foreach (int group in groups)
            {
                int roll = random.Next() % n;
                for (int step = 0, j = 0; step < 2 * counts.Length - 1; step++, j = (j + 1) % counts.Length)
                {
                    if (farm.Parts[j].Group != group || (roll -= counts[j]) >= 0 || left[j] <= 0) continue;
                    left[j]--;
                    meshes.Add(new FarmSubPlant(plant, j, U()));
                    break;
                }
            }
        return new FarmPlants([.. positions], [.. meshes]);
    }

    /// <summary>
    /// A plant mesh's world transform at <paramref name="growth"/> (the plant's) and <paramref name="death"/> (the farm's <c>plant death</c>), or
    /// null when it is not drawn (harvested, growth below -1, unless the part is <see cref="FarmPart.Static"/>). f = (clamp01(growth) - delay) / (1 -
    /// delay); scale = lerp(scale start, scale end, f) x (1 + (U - 0.5) x scale variance); height offset = lerp(offset start, offset end, f), less
    /// death² x <paramref name="height"/> (the tallest part mesh's top) when it is not static. The yaw is the roll x 37 radians, the game's basis
    /// (sin, 0, cos).
    /// </summary>
    public static Matrix4x4? Transform(FarmPart part, Vector3 position, float random, float growth, float death, float height)
    {
        if (growth < -1 && !part.Static) return null;
        float t = Math.Clamp(growth, 0, 1);
        float f = part.Delay < 1 ? (t - part.Delay) / (1 - part.Delay) : 0;
        float scale = (part.ScaleStart + (part.ScaleEnd - part.ScaleStart) * f) * ((random - 0.5f) * part.ScaleVariance + 1);
        if (scale <= 0) return null;
        float offset = part.OffsetStart + (part.OffsetEnd - part.OffsetStart) * f;
        if (!part.Static) offset -= death * death * height;
        float sin = MathF.Sin(random * 37), cos = MathF.Cos(random * 37);
        // Mesh x goes to (sin, 0, -cos) and mesh z to (cos, 0, sin): farm_vs's rows dx = (sin, 0, cos), dz = dx x up.
        return new Matrix4x4(
            sin * scale, 0, -cos * scale, 0,
            0, scale, 0, 0,
            cos * scale, 0, sin * scale, 0,
            position.X, position.Y + offset, position.Z, 1);
    }

    /// <summary>
    /// A plant mesh's colour (<c>farm_vs</c>): lerp(white, <c>death colour</c>, saturate(d / <c>death threshold</c> x 0.8)) plus 0.1 x the roll, for
    /// a <see cref="FarmPart.Static"/> part white less 0.1 x the roll with no death tint.
    /// </summary>
    public static Vector3 Tint(FarmData farm, FarmPart part, float random, float death)
    {
        if (part.Static) return new Vector3(1 - 0.1f * random);
        uint c = farm.DeathColour;
        var dying = new Vector3((c >> 16) & 255, (c >> 8) & 255, c & 255) / 255f;
        float t = farm.DeathThreshold > 0 ? Math.Clamp(death / farm.DeathThreshold * 0.8f, 0, 1) : 0;
        return Vector3.Lerp(Vector3.One, dying, t) + new Vector3(0.1f * random);
    }
}