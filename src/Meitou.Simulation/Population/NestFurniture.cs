using System.Numerics;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation.Construction;

namespace Meitou.Simulation.Population;

/// <summary>
/// A building a nest stood up round itself (a TOWN's <c>debris building</c>): its id, BUILDING record and where it stands.
/// </summary>
public sealed record NestPiece(string Id, string Record, Vector3 Position, Quaternion Rotation)
{
    /// <summary>
    /// The key of the town building whose residents' nest stood it up (null: a wild nest's or a town centre's).
    /// </summary>
    public string? Home { get; init; }

    /// <summary>
    /// Whether it stands on <see cref="Home"/>: the ray the original casts straight down onto its spot meets the home first, which makes it one of the
    /// home's interior objects (<c>FUN_140930120</c>, <c>FUN_14054f3d0</c>); buying the home destroys it.
    /// </summary>
    public bool OnHome { get; init; }
}

/// <summary>
/// The building surface the original's placement ray meets first over a resident nest's piece, and whether it is the nest's home.
/// </summary>
public readonly record struct PlacementHit(GroundHit Hit, bool OnHome);

/// <summary>
/// What the original's placement ray (<c>FUN_140930120</c>: straight down from 10000 over a piece's spot, mask <c>0x204</c>; a debris mesh's from
/// <paramref name="from"/>) meets first over <paramref name="spot"/> (X, Z) round a resident nest's home, given the pieces laid before it: a
/// building's surface (the home's or another's), else null for the terrain.
/// </summary>
public delegate PlacementHit? PlacementRay(Vector2 spot, IReadOnlyList<NestPiece> earlier, float from = float.PositiveInfinity);

/// <summary>
/// What a nest scatters at its place: its buildings, its <c>debris</c> meshes (drawn only) and the items it lays down in a fresh zone (<c>debris</c>
/// with physics, <c>loot spawn</c>).
/// </summary>
public sealed record NestLayout(List<NestPiece> Pieces, List<NestMesh> Meshes, List<NestDrop> Drops)
{
    /// <summary>
    /// Whether the nest scatters nothing.
    /// </summary>
    public bool IsEmpty => Pieces.Count == 0 && Meshes.Count == 0 && Drops.Count == 0;
}

/// <summary>
/// A nest's <c>debris building</c> scatter (docs/game/factions-squads-towns.md "Nest furniture"; the original's <c>TownBase::_nestUpThisSpot</c> and
/// its building placer <c>FUN_140930120</c>): <c>num centrepoints</c> centres within <c>size radius</c> of the nest, each 190 from the earlier ones
/// it came within 140 of, and round each centre every entry's count of its BUILDING at v1 to v2 from it, pushed to 25 from a piece of the same entry
/// within 17, dropped with no navmesh within 60 of the centre's height (the original's nearest-point query, a box of half-size 60) or within 12 of
/// one, stood on the ground tilted to it up to the BUILDING's <c>max slope</c>. Pure; the rolls are keyed by the nest's place (the original seeds
/// from its X/Z). The <c>debris</c> and <c>loot spawn</c> lists are in <c>NestDebris.cs</c>.
/// </summary>
public static partial class NestFurniture
{
    /// <summary>
    /// A centre within this of an earlier one is pushed away from it.
    /// </summary>
    const float CentreSpacing = 140;
    /// <summary>
    /// How far a centre is pushed from an earlier one it came near.
    /// </summary>
    const float CentrePush = 190;
    /// <summary>
    /// A piece within this of a piece of the same entry is pushed away from it.
    /// </summary>
    const float PieceSpacing = 17;
    /// <summary>
    /// How far a piece is pushed from one of the same entry it came near.
    /// </summary>
    const float PiecePush = 25;
    /// <summary>
    /// A piece still within this of a piece of the same entry after the push is dropped.
    /// </summary>
    const float PieceGap = 12;
    /// <summary>
    /// A piece (or a loot item) with no navmesh within this of its centre's height is dropped (the original's nearest-point query, a box of
    /// half-size 60).
    /// </summary>
    const float NavReach = 60;
    /// <summary>
    /// The spread of an entry's count: v0 ± v0 x 0.5.
    /// </summary>
    const float CountSpread = 0.5f;
    /// <summary>
    /// The step of the ground-normal samples, in units.
    /// </summary>
    const float NormalStep = 5;

    /// <summary>
    /// What <paramref name="nest"/> scatters at <paramref name="at"/>, in the original's order: <c>debris</c>, the buildings (ids are
    /// <paramref name="idPrefix"/> and a number), <c>loot spawn</c>. <paramref name="ray"/>: a resident nest's placement ray, whose first hit a
    /// piece stands on (null: a nest of no home, whose buildings stand on the navmesh and debris on the terrain); <paramref name="atY"/>: the
    /// height of its spot, the home's position, over which its debris rays start.
    /// </summary>
    public static NestLayout Scatter(GameDatabase db, TownData nest, Vector2 at, IWalkability walk, ulong seed, string idPrefix,
        PlacementRay? ray = null, float atY = 0)
    {
        ulong key = Rng.Mix(SeedSalts.NestFurniture ^ (ulong)(uint)(int)at.X ^ (ulong)(uint)(int)at.Y << 32);
        var rolls = new RandomStream(seed, key, RngPurpose.Spawn);
        var layout = new NestLayout([], [], []);
        Debris(db, nest, new Vector3(at.X, atY, at.Y), new DebrisGround(walk, ray), ref rolls, layout);
        Buildings(db, nest, at, walk, ref rolls, idPrefix, layout.Pieces, ray);
        Loot(nest, at, walk, ref rolls, layout.Drops);
        return layout;
    }

    /// <summary>
    /// Lays out the <c>debris building</c> pieces: <c>num centrepoints</c> centres, and each entry's buildings round each centre.
    /// </summary>
    static void Buildings(GameDatabase db, TownData nest, Vector2 at, IWalkability walk, ref RandomStream rolls, string idPrefix,
        List<NestPiece> pieces, PlacementRay? ray)
    {
        if (nest.NumCentrepoints <= 0 || nest.DebrisBuilding.Count == 0) return;

        var centres = new List<Vector2>();
        float nestY = walk.GroundHeight(at.X, at.Y);
        for (int c = 0; c < nest.NumCentrepoints; c++)
        {
            var centre = Snapped(walk, at + Direction(ref rolls) * Between(ref rolls, 0, nest.SizeRadius));
            centre = Spaced(centre, centres, CentreSpacing, CentrePush);
            centres.Add(centre);
            float centreY = walk.GroundHeight(centre.X, centre.Y, nestY);
            foreach (var entry in nest.DebrisBuilding)
            {
                if (db.Find(entry.Id) is { } record)
                {
                    var start = new Vector3(centre.X, centreY, centre.Y);
                    Place(record, start, entry, Count(ref rolls, entry.V0), walk, ref rolls, idPrefix, pieces, ray);
                }
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
        List<NestPiece> pieces,
        PlacementRay? ray)
    {
        var batch = new List<Vector2>();
        float maxSlope = record.GetFloat("max slope");
        for (int i = 0; i < count; i++)
        {
            var p = Spaced(new Vector2(centre.X, centre.Z) + Direction(ref rolls) * Between(ref rolls, entry.V1, entry.V2), batch, PieceSpacing,
                PiecePush);
            float yaw = rolls.NextFloat() * MathF.Tau;
            if (!walk.TryStand(p.X, p.Y, centre.Y, out float y) || MathF.Abs(y - centre.Y) > NavReach) continue;

            if (batch.Exists(b => Vector2.DistanceSquared(b, p) < PieceGap * PieceGap)) continue;

            batch.Add(p);
            pieces.Add(Piece($"{idPrefix}{pieces.Count}", record, new Vector3(p.X, y, p.Y), yaw, maxSlope, walk, pieces, ray));
        }
    }

    /// <summary>
    /// A piece at <paramref name="position"/>. With a placement <paramref name="ray"/> (given the <paramref name="earlier"/> pieces) it stands where
    /// the ray meets first, tilted to that surface: a building (on the home when it is the home) or the terrain. The navmesh only keeps or drops
    /// it (<see cref="Place"/>): its stand height may be a surface beside the spot (#1005: a Hub ruin's plinth 16 over the ground at its foot).
    /// With none it stands on the navmesh, tilted to it.
    /// </summary>
    static NestPiece Piece(string id, GameRecord record, Vector3 position, float yaw, float maxSlope, IWalkability walk,
        IReadOnlyList<NestPiece> earlier, PlacementRay? ray)
    {
        if (ray is null) return new NestPiece(id, record.StringId, position, Tilt(GroundNormal(walk, position), yaw, maxSlope));

        if (ray(new Vector2(position.X, position.Z), earlier) is not { } first)
        {
            var onTerrain = position with { Y = walk.TerrainHeight(position.X, position.Z) };
            return new NestPiece(id, record.StringId, onTerrain, Tilt(TerrainNormal(walk, onTerrain), yaw, maxSlope));
        }

        var onBuilding = position with { Y = first.Hit.Y };
        return new NestPiece(id, record.StringId, onBuilding, Tilt(first.Hit.Normal, yaw, maxSlope)) { OnHome = first.OnHome };
    }

    /// <summary>
    /// A centre snapped to the navmesh (<c>FUN_140927600</c>): the closest walkable point within <see cref="NavReach"/> (engine choice: the reach of
    /// the original's snap is Unknown), else the point as it is.
    /// </summary>
    static Vector2 Snapped(IWalkability walk, Vector2 p) => WalkableSpots.Closest(walk, p, NavReach, out var spot) ? spot : p;

    /// <summary>
    /// The original's count: v0 0 is one piece on a coin flip, 1 is one, more is v0 plus an int in [-v0/2, v0/2].
    /// </summary>
    static int Count(ref RandomStream rolls, int v0)
    {
        if (v0 <= 0) return rolls.NextFloat() < 0.5f ? 1 : 0;

        if (v0 == 1) return 1;

        int lo = (int)(-v0 * CountSpread);
        int hi = (int)(v0 * CountSpread);
        return v0 + lo + rolls.NextInt(hi - lo + 1);
    }

    /// <summary>
    /// A point within <paramref name="spacing"/> of an earlier one is moved to <paramref name="push"/> from it, along the line between them, each
    /// in turn.
    /// </summary>
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

    /// <summary>
    /// The original's direction: X and Z each uniform in [-1, 1], normalised.
    /// </summary>
    static Vector2 Direction(ref RandomStream rolls)
    {
        var d = new Vector2(rolls.NextFloat() * 2 - 1, rolls.NextFloat() * 2 - 1);
        return d.LengthSquared() > 1e-6f ? Vector2.Normalize(d) : Vector2.UnitX;
    }

    /// <summary>
    /// A uniform roll in [<paramref name="min"/>, <paramref name="max"/>).
    /// </summary>
    static float Between(ref RandomStream rolls, float min, float max) => min + rolls.NextFloat() * (max - min);

    /// <summary>
    /// A turn of <paramref name="yaw"/> about up, tilted to a surface of <paramref name="normal"/>; a tilt past <paramref name="maxSlope"/> degrees
    /// is brought back to it (the original's slerp).
    /// </summary>
    static Quaternion Tilt(Vector3 normal, float yaw, float maxSlope)
    {
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        float tilt = MathF.Acos(Math.Clamp(normal.Y, -1, 1)) * (180 / MathF.PI);
        if (tilt < 1e-3f) return turn;

        var axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, normal));
        var tilted = Quaternion.CreateFromAxisAngle(axis, tilt * (MathF.PI / 180)) * turn;
        return tilt > maxSlope ? Quaternion.Slerp(turn, tilted, maxSlope / tilt) : tilted;
    }

    /// <summary>
    /// The ground's normal at <paramref name="p"/>, from samples <see cref="NormalStep"/> either side on X and Z.
    /// </summary>
    static Vector3 GroundNormal(IWalkability walk, Vector3 p) => Normal((x, z) => walk.GroundHeight(x, z, p.Y), p);

    /// <summary>
    /// The terrain's normal at <paramref name="p"/>, sampled as <see cref="GroundNormal"/>.
    /// </summary>
    static Vector3 TerrainNormal(IWalkability walk, Vector3 p) => Normal(walk.TerrainHeight, p);

    /// <summary>
    /// The normal of the surface <paramref name="height"/> gives at <paramref name="p"/>, from samples <see cref="NormalStep"/> either side on X and Z.
    /// </summary>
    static Vector3 Normal(Func<float, float, float> height, Vector3 p)
    {
        float dx = height(p.X + NormalStep, p.Z) - height(p.X - NormalStep, p.Z);
        float dz = height(p.X, p.Z + NormalStep) - height(p.X, p.Z - NormalStep);
        return Vector3.Normalize(new Vector3(-dx, 2 * NormalStep, -dz));
    }
}

/// <summary>
/// The buildings and debris meshes the loaded nests (and the towns with <c>spawn in town centre</c>) stood up (<see cref="NestFurniture"/>), by camp:
/// a wild site, or a town site (<see cref="TownCamp"/>). Not saved: a nest stands them up again from its place each time it loads, as the original
/// (its nest buildings are never in a save); the items it lays down are ground items, saved as such, laid once per camp in a fresh zone
/// (<see cref="Stock"/>). Serial phases only.
/// </summary>
public sealed class NestBuildings
{
    /// <summary>
    /// A camp standing: its layout, owner faction, centre, the TOWN record it was stood up from, and the furniture its pieces give the homes they
    /// stand round (by home building key).
    /// </summary>
    sealed record Camp(NestLayout Layout, int Owner, Vector2 Centre, string Record, IReadOnlyDictionary<string, List<HomeSpot>> Furniture);

    /// <summary>
    /// The camps standing, by camp key.
    /// </summary>
    readonly SortedDictionary<int, Camp> _camps = [];
    /// <summary>
    /// The camps that laid their items (see <see cref="Stock"/>).
    /// </summary>
    readonly HashSet<int> _stocked = [];
    /// <summary>
    /// The furniture the standing camps' pieces give homes, by home building key (see <see cref="FurnitureOf"/>).
    /// </summary>
    readonly Dictionary<string, List<HomeSpot>> _furniture = new(StringComparer.Ordinal);
    /// <summary>
    /// The cache of <see cref="Published"/> (null: made again on the next call).
    /// </summary>
    IReadOnlyList<BuildingSnapshot>? _published;
    /// <summary>
    /// The cache of <see cref="Meshes"/> (null: made again on the next call).
    /// </summary>
    IReadOnlyList<NestMesh>? _meshes;

    /// <summary>
    /// Bumped when a camp's buildings or meshes come or go: the host diffs <see cref="Published"/> and <see cref="Meshes"/> when it changes.
    /// </summary>
    public long Revision { get; private set; }

    /// <summary>
    /// The camp key of town site <paramref name="site"/> (a wild site's key is its index).
    /// </summary>
    public static int TownCamp(int site) => -1 - site;

    /// <summary>
    /// The buildings camp <paramref name="camp"/> stood up (none when it stands nothing).
    /// </summary>
    public IReadOnlyList<NestPiece> Of(int camp) => _camps.TryGetValue(camp, out var c) ? c.Layout.Pieces : [];

    /// <summary>
    /// The TOWN record a camp was stood up from (null: none standing).
    /// </summary>
    public string? RecordOf(int camp) => _camps.TryGetValue(camp, out var c) ? c.Record : null;

    /// <summary>
    /// The nests standing (not the towns), by wild site, with where each stands.
    /// </summary>
    public IEnumerable<(int Site, Vector2 Centre)> Standing => _camps.Where(kv => kv.Key >= 0).Select(kv => (kv.Key, kv.Value.Centre));

    /// <summary>
    /// The pieces of furniture the standing camps stood round home <paramref name="homeKey"/> (a town's residents' nest, docs/formats/zones.md "Nest
    /// debris and loot"): what staying home takes besides the home's own interior; none when nothing stands there.
    /// </summary>
    public IReadOnlyList<HomeSpot> FurnitureOf(string homeKey) => _furniture.TryGetValue(homeKey, out var spots) ? spots : [];

    /// <summary>
    /// Stands up a camp's layout for its owner at its centre, replacing what it stood before; <paramref name="furniture"/> is what its pieces give
    /// the homes they stand round (by home building key).
    /// </summary>
    public void Set(int camp, NestLayout layout, int owner, Vector2 centre, string record,
        IReadOnlyDictionary<string, List<HomeSpot>>? furniture = null)
    {
        Remove(camp);
        _camps[camp] = new Camp(layout, owner, centre, record, furniture ?? new Dictionary<string, List<HomeSpot>>());
        foreach (var (home, spots) in _camps[camp].Furniture) _furniture[home] = spots;
        Changed();
    }

    /// <summary>
    /// Takes a camp down; false when it stood nothing.
    /// </summary>
    public bool Remove(int camp)
    {
        if (!_camps.Remove(camp, out var gone)) return false;

        foreach (string home in gone.Furniture.Keys) _furniture.Remove(home);
        Changed();
        return true;
    }

    /// <summary>
    /// The player bought <paramref name="home"/> (<c>FUN_1407ad6c0</c>, docs/formats/zones.md "Nest debris and loot"): in each camp that stood a
    /// nest round it, the pieces that nest stood on the home go (<see cref="NestPiece.OnHome"/>; those on the ground beside it stay), and so does
    /// every debris mesh within <paramref name="radius"/> (X/Z) of <paramref name="centre"/>; the furniture the nest gave the home goes too. Returns
    /// the pieces taken, whose fixtures the caller unregisters.
    /// </summary>
    public List<NestPiece> ClearHome(string home, Vector2 centre, float radius)
    {
        var gone = new List<NestPiece>();
        var camps = _camps.Values.Where(c => c.Furniture.ContainsKey(home)).ToList();
        if (camps.Count == 0) return gone;

        foreach (var camp in camps)
        {
            gone.AddRange(camp.Layout.Pieces.Where(p => p.OnHome && p.Home == home));
            camp.Layout.Pieces.RemoveAll(p => p.OnHome && p.Home == home);
            camp.Layout.Meshes.RemoveAll(m => Vector2.DistanceSquared(new Vector2(m.Position.X, m.Position.Z), centre) < radius * radius);
        }

        _furniture.Remove(home);
        Changed();
        return gone;
    }

    /// <summary>
    /// The pieces standing in every camp but <paramref name="camp"/> (what a new layout of that camp's placement ray may meet besides its own).
    /// </summary>
    public IEnumerable<NestPiece> PiecesBesides(int camp) => _camps
        .Where(c => c.Key != camp)
        .SelectMany(c => c.Value.Layout.Pieces);

    /// <summary>
    /// The town of camp <paramref name="camp"/> unloads: the pieces and meshes its nests laid round <paramref name="homes"/> (the houses the player
    /// bought) go, as the original's town takes them down with it and lays no nest round such a house again. Returns the pieces taken, whose
    /// fixtures the caller unregisters.
    /// </summary>
    public List<NestPiece> DropHomes(int camp, IReadOnlySet<string> homes)
    {
        if (!_camps.TryGetValue(camp, out var c)) return [];

        var gone = c.Layout.Pieces.Where(p => Bought(p.Home, homes)).ToList();
        int meshes = c.Layout.Meshes.RemoveAll(m => Bought(m.Home, homes));
        c.Layout.Pieces.RemoveAll(p => Bought(p.Home, homes));
        if (gone.Count > 0 || meshes > 0) Changed();
        return gone;
    }

    /// <summary>
    /// Whether <paramref name="home"/> is one of <paramref name="homes"/>.
    /// </summary>
    static bool Bought(string? home, IReadOnlySet<string> homes) => home is not null && homes.Contains(home);

    /// <summary>
    /// True the first time a camp asks in this world (it lays its items now; the caller asks only for a zone the loaded save has no file of).
    /// </summary>
    public bool Stock(int camp) => _stocked.Add(camp);

    /// <summary>
    /// Drops the caches and bumps <see cref="Revision"/>.
    /// </summary>
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
        _published ??= [.. _camps.SelectMany(kv => kv.Value.Layout.Pieces.Select(p => Snapshot(p, kv.Value.Owner, kv.Key)))];
        return _published;
    }

    /// <summary>
    /// Every camp's debris meshes, for the host's render overlay.
    /// </summary>
    public IReadOnlyList<NestMesh> Meshes() => _meshes ??= [.. _camps.Values.SelectMany(c => c.Layout.Meshes)];

    /// <summary>
    /// A camp piece as a complete building snapshot of its owner, in the town of its camp (<paramref name="camp"/>: a wild site, else
    /// <see cref="TownCamp"/>).
    /// </summary>
    static BuildingSnapshot Snapshot(NestPiece p, int owner, int camp) => new(p.Id, p.Record, p.Position, p.Rotation, 0, null, owner, 0, 0, [], true,
        false)
    {
        CampTown = camp >= 0 ? new Locks.FixtureTown(-1, camp) : new Locks.FixtureTown(-1 - camp, -1),
    };
}
