using System.Numerics;
using Meitou.Data;
using Meitou.Data.Gameplay;

namespace Meitou.Simulation.Population;

/// <summary>A <c>debris</c> mesh a nest lays round itself (drawn only, no item): its NEST_ITEM, the mesh, where it lies, its turn and scale.</summary>
public sealed record NestMesh(string Record, string Mesh, Vector3 Position, float Yaw, float Scale);

/// <summary>An item a nest lays down in a fresh zone: a <c>debris</c> item with a <c>physics file</c> (<paramref name="List"/> false), or a roll of a <c>loot spawn</c> VENDOR_LIST.</summary>
public sealed record NestDrop(string Record, bool List, Vector3 Position, float Yaw);

/// <summary>
/// A nest's <c>debris</c> and <c>loot spawn</c> (docs/game/factions-squads-towns.md "Nest furniture"; <c>TownBase::spawnDebris</c> <c>FUN_140937510</c> and the loot loop of
/// <c>FUN_140938440</c>). Each <c>debris</c> entry: v0 to v1 clusters (the original's clamped int range) at scale v2 / 100 (0: 1), each at a random point within <c>size radius</c>.
/// A NEST_ITEM without a <c>physics file</c> is <c>cluster min</c> to <c>cluster max</c> meshes, each within <c>cluster range</c> x scale of the one before (a walk), its
/// <c>ground mesh</c> (else <c>mesh</c>) or, with <c>number of meshes</c> above 1, a numbered one of them, scaled by the scale ± 0.1; one with a physics file is that many items within
/// <c>cluster range</c> of the cluster's point. Its own <c>cluster</c> list follows from the last one placed (v0 to v1 clusters within v2). <c>loot spawn</c>: v0 to v1 rolls of the
/// list, each at a point within <c>size radius</c> on the navmesh, none below height 1.
/// </summary>
public static partial class NestFurniture
{
    const float MeshScaleSpread = 0.1f, LowestLoot = 1;
    const int MaxDebrisDepth = 4;

    static void Debris(GameDatabase db, TownData nest, Vector2 at, IWalkability walk, ref RandomStream rolls, NestLayout layout)
    {
        foreach (var entry in nest.Debris)
        {
            if (db.Find(entry.Id) is not { } item) continue;
            float scale = entry.V2 == 0 ? 1 : entry.V2 * 0.01f;
            var centre = new Vector3(at.X, walk.GroundHeight(at.X, at.Y), at.Y);
            Clusters(db, item, centre, nest.SizeRadius, IntIn(ref rolls, entry.V0, entry.V1), scale, walk, ref rolls, layout, 0);
        }
    }

    /// <summary><paramref name="count"/> clusters of <paramref name="item"/> round <paramref name="at"/>, each followed by the item's own <c>cluster</c> list.</summary>
    static void Clusters(GameDatabase db, GameRecord item, Vector3 at, float radius, int count, float scale, IWalkability walk, ref RandomStream rolls, NestLayout layout, int depth)
    {
        bool physical = item.GetPath("physics file") is { Length: > 0 };
        int min = item.GetInt("cluster min"), max = item.GetInt("cluster max");
        float range = item.GetFloat("cluster range", item.GetInt("cluster range"));
        for (int k = 0; k < count; k++)
        {
            var last = at;
            if (physical)
            {
                for (int n = IntIn(ref rolls, min, max); n > 0; n--)
                {
                    last = Ground(walk, at, Direction(ref rolls) * Between(ref rolls, 0, range));
                    layout.Drops.Add(new NestDrop(item.StringId, false, last, rolls.NextFloat() * MathF.Tau));
                }
            }
            else
            {
                last = Ground(walk, at, Direction(ref rolls) * Between(ref rolls, 0, radius));
                for (int n = IntIn(ref rolls, min, max); n > 0; n--)
                {
                    float yaw = rolls.NextFloat() * MathF.Tau;
                    string mesh = MeshOf(item, ref rolls);
                    float size = scale + Between(ref rolls, -MeshScaleSpread, MeshScaleSpread);
                    last = Ground(walk, last, Direction(ref rolls) * Between(ref rolls, 0, range * scale));
                    if (mesh.Length > 0) layout.Meshes.Add(new NestMesh(item.StringId, mesh, last, yaw, size));
                }
            }
            if (depth >= MaxDebrisDepth) continue;
            foreach (var sub in item.GetReferences("cluster"))
                if (db.Find(sub.TargetStringId) is { } subItem)
                    Clusters(db, subItem, last, sub.Values.Value2, IntIn(ref rolls, sub.Values.Value0, sub.Values.Value1), scale, walk, ref rolls, layout, depth + 1);
        }
    }

    /// <summary>
    /// The mesh of one debris piece: <c>ground mesh</c>, else <c>mesh</c>; with <c>number of meshes</c> n above 1, its bare name less its last character and a number in [1, n]
    /// (<c>FUN_14092bdc0</c>: <c>bone1.mesh</c> with 6 is one of <c>bone1</c> to <c>bone6</c>).
    /// </summary>
    static string MeshOf(GameRecord item, ref RandomStream rolls)
    {
        string mesh = item.GetPath("ground mesh") is { Length: > 0 } ground ? ground : item.GetPath("mesh") ?? "";
        int variants = item.GetInt("number of meshes");
        if (variants < 2 || mesh.Length == 0) return mesh;
        return Data.World.WorldObjectLayout.MeshVariant(mesh, IntIn(ref rolls, 1, variants));
    }

    static void Loot(TownData nest, Vector2 at, IWalkability walk, ref RandomStream rolls, List<NestDrop> drops)
    {
        float nestY = walk.GroundHeight(at.X, at.Y);
        foreach (var entry in nest.LootSpawn)
            for (int n = IntIn(ref rolls, entry.V0, entry.V1); n > 0; n--)
            {
                var p = at + Direction(ref rolls) * Between(ref rolls, 0, nest.SizeRadius);
                if (!walk.TryStand(p.X, p.Y, nestY, out float y) || MathF.Abs(y - nestY) > NavReach || y < LowestLoot) continue;
                drops.Add(new NestDrop(entry.Id, true, new Vector3(p.X, y, p.Y), 0));
            }
    }

    /// <summary>A point <paramref name="offset"/> from <paramref name="from"/> on the ground under it.</summary>
    static Vector3 Ground(IWalkability walk, Vector3 from, Vector2 offset)
    {
        float x = from.X + offset.X, z = from.Z + offset.Y;
        return new Vector3(x, walk.GroundHeight(x, z, from.Y), z);
    }

    /// <summary>The original's int range (<c>Rand_IntInRange</c>): <paramref name="lo"/> + a uniform step over [lo, hi], clamped to it, and <paramref name="lo"/> when it is above <paramref name="hi"/>.</summary>
    static int IntIn(ref RandomStream rolls, int lo, int hi)
    {
        int v = lo + (int)(rolls.NextFloat() * (hi - lo + 1));
        return Math.Max(lo, Math.Min(v, hi));
    }
}