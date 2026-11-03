using System.Numerics;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Construction;

namespace Meitou.Simulation;

/// <summary>A building a nest stood up round itself (a TOWN's <c>debris building</c>): its id, BUILDING record and where it stands.</summary>
public sealed record NestPiece(string Id, string Record, Vector3 Position, Quaternion Rotation);

/// <summary>
/// A nest's <c>debris building</c> scatter (docs/game/factions-squads-towns.md "Nest furniture"; the original's <c>TownBase::_nestUpThisSpot</c> and its building placer
/// <c>FUN_140930120</c>): <c>num centrepoints</c> centres within <c>size radius</c> of the nest, each 190 from the earlier ones it came within 140 of, and round each
/// centre every entry's count of its BUILDING at v1 to v2 from it, pushed to 25 from a piece of the same entry within 17, dropped with no navmesh within 60 of the
/// centre's height (the original's nearest-point query, a box of half-size 60) or within 12 of one, stood on the ground tilted to it up to the BUILDING's <c>max slope</c>.
/// Pure; the rolls are keyed by the nest's place (the original seeds from its X/Z).
/// </summary>
public static class NestFurniture
{
    const float CentreSpacing = 140, CentrePush = 190, PieceSpacing = 17, PiecePush = 25, PieceGap = 12, NavReach = 60, CountSpread = 0.5f, NormalStep = 5;

    /// <summary>The pieces of <paramref name="nest"/> standing at <paramref name="at"/>; ids are <paramref name="idPrefix"/> and a number.</summary>
    public static List<NestPiece> Scatter(GameDatabase db, TownData nest, Vector2 at, IWalkability walk, ulong seed, string idPrefix)
    {
        var pieces = new List<NestPiece>();
        if (nest.NumCentrepoints <= 0 || nest.DebrisBuilding.Count == 0) return pieces;
        ulong key = Rng.Mix(SeedSalts.NestFurniture ^ (ulong)(uint)(int)at.X ^ (ulong)(uint)(int)at.Y << 32);
        var rolls = new RandomStream(seed, key, RngPurpose.Spawn);
        var centres = new List<Vector2>();
        float nestY = walk.GroundHeight(at.X, at.Y);
        for (int c = 0; c < nest.NumCentrepoints; c++)
        {
            var centre = Spaced(at + Direction(ref rolls) * Between(ref rolls, 0, nest.SizeRadius), centres, CentreSpacing, CentrePush);
            centres.Add(centre);
            float centreY = walk.GroundHeight(centre.X, centre.Y, nestY);
            foreach (var entry in nest.DebrisBuilding)
                if (db.Find(entry.Id) is { } record)
                    Place(record, new Vector3(centre.X, centreY, centre.Y), entry, Count(ref rolls, entry.V0), walk, ref rolls, idPrefix, pieces);
        }
        return pieces;
    }

    /// <summary>One entry's pieces round a centre; the spacing counts only this batch (the original clears its list on each call).</summary>
    static void Place(GameRecord record, Vector3 centre, RecordLink entry, int count, IWalkability walk, ref RandomStream rolls, string idPrefix, List<NestPiece> pieces)
    {
        var batch = new List<Vector2>();
        float maxSlope = record.GetFloat("max slope");
        for (int i = 0; i < count; i++)
        {
            var p = Spaced(new Vector2(centre.X, centre.Z) + Direction(ref rolls) * Between(ref rolls, entry.V1, entry.V2), batch, PieceSpacing, PiecePush);
            float yaw = rolls.NextFloat() * MathF.Tau;
            if (!walk.TryStand(p.X, p.Y, centre.Y, out float y) || MathF.Abs(y - centre.Y) > NavReach) continue;
            if (batch.Exists(b => Vector2.DistanceSquared(b, p) < PieceGap * PieceGap)) continue;
            batch.Add(p);
            var position = new Vector3(p.X, y, p.Y);
            pieces.Add(new NestPiece($"{idPrefix}{pieces.Count}", record.StringId, position, Stand(walk, position, yaw, maxSlope)));
        }
    }

    /// <summary>
    /// The original's count: v0 0 is one piece on a coin flip, 1 is one, more is v0 plus an int in [-v0/2, v0/2].
    /// </summary>
    static int Count(ref RandomStream rolls, int v0)
    {
        if (v0 <= 0) return rolls.NextFloat() < 0.5f ? 1 : 0;
        if (v0 == 1) return 1;
        int lo = (int)(-v0 * CountSpread), hi = (int)(v0 * CountSpread);
        return v0 + lo + rolls.NextInt(hi - lo + 1);
    }

    /// <summary>A point within <paramref name="spacing"/> of an earlier one is moved to <paramref name="push"/> from it, along the line between them, each in turn.</summary>
    static Vector2 Spaced(Vector2 p, List<Vector2> earlier, float spacing, float push)
    {
        foreach (var e in earlier)
        {
            var d = p - e;
            if (d.LengthSquared() >= spacing * spacing) continue;
            p = e + (d.LengthSquared() > 1e-6f ? Vector2.Normalize(d) : Vector2.UnitX) * push;
        }
        return p;
    }

    /// <summary>The original's direction: X and Z each uniform in [-1, 1], normalised.</summary>
    static Vector2 Direction(ref RandomStream rolls)
    {
        var d = new Vector2(rolls.NextFloat() * 2 - 1, rolls.NextFloat() * 2 - 1);
        return d.LengthSquared() > 1e-6f ? Vector2.Normalize(d) : Vector2.UnitX;
    }

    static float Between(ref RandomStream rolls, float min, float max) => min + rolls.NextFloat() * (max - min);

    /// <summary>A turn of <paramref name="yaw"/> about up, tilted to the ground's slope; a tilt past <paramref name="maxSlope"/> degrees is brought back to it (the original's slerp).</summary>
    static Quaternion Stand(IWalkability walk, Vector3 p, float yaw, float maxSlope)
    {
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        var normal = GroundNormal(walk, p);
        float tilt = MathF.Acos(Math.Clamp(normal.Y, -1, 1)) * (180 / MathF.PI);
        if (tilt < 1e-3f) return turn;
        var axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, normal));
        var tilted = Quaternion.CreateFromAxisAngle(axis, tilt * (MathF.PI / 180)) * turn;
        return tilt > maxSlope ? Quaternion.Slerp(turn, tilted, maxSlope / tilt) : tilted;
    }

    static Vector3 GroundNormal(IWalkability walk, Vector3 p)
    {
        float dx = walk.GroundHeight(p.X + NormalStep, p.Z, p.Y) - walk.GroundHeight(p.X - NormalStep, p.Z, p.Y);
        float dz = walk.GroundHeight(p.X, p.Z + NormalStep, p.Y) - walk.GroundHeight(p.X, p.Z - NormalStep, p.Y);
        return Vector3.Normalize(new Vector3(-dx, 2 * NormalStep, -dz));
    }
}

/// <summary>
/// The buildings the loaded nests stood up (<see cref="NestFurniture"/>), by wild site. Not saved: a nest stands them up again from its place each time it loads, as the
/// original (its nest buildings are never in a save). Serial phases only.
/// </summary>
public sealed class NestBuildings
{
    readonly SortedDictionary<int, (List<NestPiece> Pieces, int Owner, Vector2 Centre)> bySite = [];
    IReadOnlyList<BuildingSnapshot>? published;

    /// <summary>Bumped when a nest's buildings come or go: the host diffs <see cref="Published"/> by id when it changes.</summary>
    public long Revision { get; private set; }

    public IReadOnlyList<NestPiece> Of(int site) => bySite.TryGetValue(site, out var s) ? s.Pieces : [];

    /// <summary>The nests standing, by wild site, with where each stands.</summary>
    public IEnumerable<(int Site, Vector2 Centre)> Standing => bySite.Select(kv => (kv.Key, kv.Value.Centre));

    public void Set(int site, List<NestPiece> pieces, int owner, Vector2 centre)
    {
        bySite[site] = (pieces, owner, centre);
        published = null;
        Revision++;
    }

    public bool Remove(int site)
    {
        if (!bySite.Remove(site)) return false;
        published = null;
        Revision++;
        return true;
    }

    /// <summary>Every nest building as a complete building of its nest's faction, for the host's render and navmesh overlays.</summary>
    public IReadOnlyList<BuildingSnapshot> Published() => published ??= [.. bySite.Values.SelectMany(s => s.Pieces.Select(p => Snapshot(p, s.Owner)))];

    static BuildingSnapshot Snapshot(NestPiece p, int owner) => new(p.Id, p.Record, p.Position, p.Rotation, 0, null, owner, 0, 0, [], true, false);
}
