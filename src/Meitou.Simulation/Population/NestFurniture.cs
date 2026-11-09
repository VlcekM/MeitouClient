using System.Numerics;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Simulation.Construction;

namespace Meitou.Simulation.Population;

/// <summary>
/// A building a nest stood up round itself (a TOWN's <c>debris building</c>): its id, BUILDING record and where it stands.
/// </summary>
public sealed record NestPiece(string Id, string Record, Vector3 Position, Quaternion Rotation);

/// <summary>
/// What a nest scatters at its place: its buildings, its <c>debris</c> meshes (drawn only) and the items it lays down in a fresh zone (<c>debris</c> with
/// physics, <c>loot spawn</c>).
/// </summary>
public sealed record NestLayout(List<NestPiece> Pieces, List<NestMesh> Meshes, List<NestDrop> Drops)
{
    public bool IsEmpty => Pieces.Count == 0 && Meshes.Count == 0 && Drops.Count == 0;
}

/// <summary>
/// A nest's <c>debris building</c> scatter (docs/game/factions-squads-towns.md "Nest furniture"; the original's <c>TownBase::_nestUpThisSpot</c> and its
/// building placer <c>FUN_140930120</c>): <c>num centrepoints</c> centres within <c>size radius</c> of the nest, each 190 from the earlier ones it came within 140
/// of, and round each centre every entry's count of its BUILDING at v1 to v2 from it, pushed to 25 from a piece of the same entry within 17, dropped with no
/// navmesh within 60 of the centre's height (the original's nearest-point query, a box of half-size 60) or within 12 of one, stood on the ground tilted to it up
/// to the BUILDING's <c>max slope</c>. Pure; the rolls are keyed by the nest's place (the original seeds from its X/Z). The <c>debris</c> and <c>loot spawn</c>
/// lists are in <c>NestDebris.cs</c>.
/// </summary>
public static partial class NestFurniture
{
    const float CentreSpacing = 140;
    const float CentrePush = 190;
    const float PieceSpacing = 17;
    const float PiecePush = 25;
    const float PieceGap = 12;
    const float NavReach = 60;
    const float CountSpread = 0.5f;
    const float NormalStep = 5;

    /// <summary>
    /// What <paramref name="nest"/> scatters at <paramref name="at"/>, in the original's order: <c>debris</c>, the buildings (ids are <paramref name="idPrefix"/>
    /// and a number), <c>loot spawn</c>.
    /// </summary>
    public static NestLayout Scatter(GameDatabase db, TownData nest, Vector2 at, IWalkability walk, ulong seed, string idPrefix)
    {
        ulong key = Rng.Mix(SeedSalts.NestFurniture ^ (ulong)(uint)(int)at.X ^ (ulong)(uint)(int)at.Y << 32);
        var rolls = new RandomStream(seed, key, RngPurpose.Spawn);
        var layout = new NestLayout([], [], []);
        Debris(db, nest, at, walk, ref rolls, layout);
        Buildings(db, nest, at, walk, ref rolls, idPrefix, layout.Pieces);
        Loot(nest, at, walk, ref rolls, layout.Drops);
        return layout;
    }

    static void Buildings(GameDatabase db, TownData nest, Vector2 at, IWalkability walk, ref RandomStream rolls, string idPrefix, List<NestPiece> pieces)
    {
        if (nest.NumCentrepoints <= 0 || nest.DebrisBuilding.Count == 0)
            return;

        var centres = new List<Vector2>();
        float nestY = walk.GroundHeight(at.X, at.Y);
        for (int c = 0; c < nest.NumCentrepoints; c++)
        {
            var centre = Spaced(at + Direction(ref rolls) * Between(ref rolls, 0, nest.SizeRadius), centres, CentreSpacing, CentrePush);
            centres.Add(centre);
            float centreY = walk.GroundHeight(centre.X, centre.Y, nestY);
            foreach (var entry in nest.DebrisBuilding)
            {
                if (db.Find(entry.Id) is { } record)
                    Place(record, new Vector3(centre.X, centreY, centre.Y), entry, Count(ref rolls, entry.V0), walk, ref rolls, idPrefix, pieces);
            }
        }
    }

    /// <summary>
    /// One entry's pieces round a centre; the spacing counts only this batch (the original clears its list on each call).
    /// </summary>
    static void Place(
        GameRecord record,
        Vector3 centre,
        RecordLink entry,
        int count,
        IWalkability walk,
        ref RandomStream rolls,
        string idPrefix,
        List<NestPiece> pieces)
    {
        var batch = new List<Vector2>();
        float maxSlope = record.GetFloat("max slope");
        for (int i = 0; i < count; i++)
        {
            var p = Spaced(new Vector2(centre.X, centre.Z) + Direction(ref rolls) * Between(ref rolls, entry.V1, entry.V2), batch, PieceSpacing, PiecePush);
            float yaw = rolls.NextFloat() * MathF.Tau;
            if (!walk.TryStand(p.X, p.Y, centre.Y, out float y) || MathF.Abs(y - centre.Y) > NavReach)
                continue;

            if (batch.Exists(b => Vector2.DistanceSquared(b, p) < PieceGap * PieceGap))
                continue;

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
        if (v0 <= 0)
            return rolls.NextFloat() < 0.5f ? 1 : 0;

        if (v0 == 1)
            return 1;

        int lo = (int)(-v0 * CountSpread);
        int hi = (int)(v0 * CountSpread);
        return v0 + lo + rolls.NextInt(hi - lo + 1);
    }

    /// <summary>
    /// A point within <paramref name="spacing"/> of an earlier one is moved to <paramref name="push"/> from it, along the line between them, each in turn.
    /// </summary>
    static Vector2 Spaced(Vector2 p, List<Vector2> earlier, float spacing, float push)
    {
        foreach (var e in earlier)
        {
            var d = p - e;
            if (d.LengthSquared() >= spacing * spacing)
                continue;

            p = e + (d.LengthSquared() > 1e-6f ? Vector2.Normalize(d) : Vector2.UnitX) * push;
        }

        return p;
    }

    /// <summary>
    /// The original's direction: X and Z each uniform in [-1, 1], normalised.
    /// </summary>
    static Vector2 Direction(ref RandomStream rolls)
    {
        var d = new Vector2(rolls.NextFloat() * 2 - 1, rolls.NextFloat() * 2 - 1);
        return d.LengthSquared() > 1e-6f ? Vector2.Normalize(d) : Vector2.UnitX;
    }

    static float Between(ref RandomStream rolls, float min, float max) => min + rolls.NextFloat() * (max - min);

    /// <summary>
    /// A turn of <paramref name="yaw"/> about up, tilted to the ground's slope; a tilt past <paramref name="maxSlope"/> degrees is brought back to it (the
    /// original's slerp).
    /// </summary>
    static Quaternion Stand(IWalkability walk, Vector3 p, float yaw, float maxSlope)
    {
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        var normal = GroundNormal(walk, p);
        float tilt = MathF.Acos(Math.Clamp(normal.Y, -1, 1)) * (180 / MathF.PI);
        if (tilt < 1e-3f)
            return turn;

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
/// The buildings and debris meshes the loaded nests (and the towns with <c>spawn in town centre</c>) stood up (<see cref="NestFurniture"/>), by camp: a wild
/// site, or a town site (<see cref="TownCamp"/>). Not saved: a nest stands them up again from its place each time it loads, as the original (its nest buildings
/// are never in a save); the items it lays down are ground items, saved as such, laid once per camp in a fresh zone (<see cref="Stock"/>). Serial phases only.
/// </summary>
public sealed class NestBuildings
{
    sealed record Camp(NestLayout Layout, int Owner, Vector2 Centre, string Record);

    readonly SortedDictionary<int, Camp> _camps = [];
    readonly HashSet<int> _stocked = [];
    IReadOnlyList<BuildingSnapshot>? _published;
    IReadOnlyList<NestMesh>? _meshes;

    /// <summary>
    /// Bumped when a camp's buildings or meshes come or go: the host diffs <see cref="Published"/> and <see cref="Meshes"/> when it changes.
    /// </summary>
    public long Revision { get; private set; }

    /// <summary>
    /// The camp key of town site <paramref name="site"/> (a wild site's key is its index).
    /// </summary>
    public static int TownCamp(int site) => -1 - site;

    public IReadOnlyList<NestPiece> Of(int camp) => _camps.TryGetValue(camp, out var c) ? c.Layout.Pieces : [];

    /// <summary>
    /// The TOWN record a camp was stood up from (null: none standing).
    /// </summary>
    public string? RecordOf(int camp) => _camps.TryGetValue(camp, out var c) ? c.Record : null;

    /// <summary>
    /// The nests standing (not the towns), by wild site, with where each stands.
    /// </summary>
    public IEnumerable<(int Site, Vector2 Centre)> Standing => _camps.Where(kv => kv.Key >= 0).Select(kv => (kv.Key, kv.Value.Centre));

    public void Set(int camp, NestLayout layout, int owner, Vector2 centre, string record)
    {
        _camps[camp] = new Camp(layout, owner, centre, record);
        Changed();
    }

    public bool Remove(int camp)
    {
        if (!_camps.Remove(camp))
            return false;

        Changed();
        return true;
    }

    /// <summary>
    /// True the first time a camp asks in this world (it lays its items now; the caller asks only for a zone the loaded save has no file of).
    /// </summary>
    public bool Stock(int camp) => _stocked.Add(camp);

    void Changed()
    {
        _published = null;
        _meshes = null;
        Revision++;
    }

    /// <summary>
    /// Every camp building as a complete building of its camp's faction, for the host's render and navmesh overlays.
    /// </summary>
    public IReadOnlyList<BuildingSnapshot> Published()
    {
        _published ??= [.. _camps.Values.SelectMany(c => c.Layout.Pieces.Select(p => Snapshot(p, c.Owner)))];
        return _published;
    }

    /// <summary>
    /// Every camp's debris meshes, for the host's render overlay.
    /// </summary>
    public IReadOnlyList<NestMesh> Meshes() => _meshes ??= [.. _camps.Values.SelectMany(c => c.Layout.Meshes)];

    static BuildingSnapshot Snapshot(NestPiece p, int owner) => new(p.Id, p.Record, p.Position, p.Rotation, 0, null, owner, 0, 0, [], true, false);
}
