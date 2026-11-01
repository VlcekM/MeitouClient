using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The Meitou UI's character editor scene (docs/characters.md "Character editor", Meitou): a copy of The Hub's Storm House bar, its walls, furniture and lamps,
/// placed high above the edited character so nothing of the world around it reaches in, on a staged patch of ground, and drawn at night by the world's own
/// renderers. The edited character stands on its floor; the others being edited sit on its seats behind, among the bar's own people (its barman and guard, and
/// patrons of the town's bar squads).
/// </summary>
sealed class EditorBar
{
    /// <summary>The copy's placement key: its parts, interior volume and lamps go by it.</summary>
    public const string Key = "meitou:editor-bar";
    /// <summary>At most this many of the other edited characters sit in the bar.</summary>
    public const int MaxSeated = 5;
    const string TownName = "The Hub", BuildingName = "Storm House", LayoutName = "Bar";
    /// <summary>How far above the highest ground round the anchor the copy stands.</summary>
    const float Lift = 3000;
    /// <summary>
    /// Where the edited character stands and which way it faces, in the building's frame (its layout's: x across, z along, y up; the floor is 4.4 up): on the open floor
    /// between the counter and the tables, facing the counter, so the camera in front of it looks back across the room at the seated ones.
    /// </summary>
    static readonly Vector3 StandLocal = new(12, 4.4f, -14), FacingLocal = Vector3.UnitX;
    /// <summary>The seats and spots taken are those nearest this point of the building's frame: the far tables behind the edited character as the camera first sees it.</summary>
    static readonly Vector3 SeatFocusLocal = new(-53, 4.4f, -1);
    /// <summary>Nobody is put nearer the edited character than this (across): the orbit's eye (<see cref="EditorOrbit.BodyFar"/> out at most) never enters a figure.</summary>
    const float SeatClearance = EditorOrbit.BodyFar + 4;
    /// <summary>Patrons of the town's bar squads: this many seated after the editor's characters' seats, and this many standing at the room's nodes.</summary>
    const int SeatedPatrons = 3, StandingPatrons = 3;
    /// <summary>The idle stances the standing patrons hold (docs/characters.md "Idle stance").</summary>
    static readonly string[] Stances = ["idle_stand_relax", "idle_stand_normal", "idle_stand_confident", "idle_stand_timid", "idle_stand_stiff"];
    /// <summary>The staged ground: a flat-topped rock block under the bar, its top this far across at least, so the doorways and windows look out on ground.</summary>
    const string GroundMesh = "FLatTop_Pillar01.mesh";
    const float GroundHalfWidth = 1500;
    /// <summary>Shrubs scattered round the bar on the staged ground: count, and the ring they stand in (from the bar's origin).</summary>
    static readonly string[] Shrubs = ["bush01.mesh", "shrub.mesh", "shrubs&grass.mesh"];
    const int ShrubCount = 14;
    const float ShrubNear = 140, ShrubFar = 700;
    /// <summary>The room's neutral fill: a white point light under the roof, this share of the bar's own lamps' mean power, reaching this far.</summary>
    public const float FillShare = 0.5f, FillRadius = 220;
    const float FillHeight = 45;

    public BuildingPlacement Placement { get; }
    /// <summary>The copy's origin (absolute height) and turn, as the source's.</summary>
    public Vector3 Origin { get; }
    public Quaternion Rotation { get; }
    /// <summary>Where the edited character stands and its yaw (0 along +Z, turning towards +X).</summary>
    public Vector3 Stand { get; }
    public float StandYaw { get; }
    /// <summary>The seats for the editor's characters, nearest the focus first: their place, the yaw the sitter faces and the clip it sits with.</summary>
    public IReadOnlyList<(Vector3 Position, float Yaw, string Clip)> Seats { get; }
    /// <summary>The bar's own people: a CHARACTER record (with its template's faction, if any), where it is, its yaw, the clip it holds and the seed of its rolled look.</summary>
    public IReadOnlyList<(string Record, string? Faction, Vector3 Position, float Yaw, string Clip, int Seed)> Crowd { get; }
    /// <summary>The staged ground and shrubs round the bar (<see cref="Meitou.Rendering.WorldObjectRenderer.Staged"/>).</summary>
    public IReadOnlyList<PlacedMesh> Ground { get; }
    /// <summary>Where the room's neutral fill light hangs (<see cref="FillShare"/>).</summary>
    public Vector3 FillAt { get; }

    EditorBar(GameInstall install, GameDatabase db, TownSite site, TownBuilding source, ResolvedBuilding resolved, Vector3 origin)
    {
        var level = resolved.Placement;
        Origin = origin;
        Rotation = level.Rotation;
        Placement = level with { Zone = WorldLayout.ZoneOf(origin.X, origin.Z), Position = origin with { Y = 0 }, WorldY = origin.Y, StateZone = level.Zone, Key = Key, DestroyedOverride = null };
        Stand = origin + Vector3.Transform(StandLocal, Rotation);
        var facing = Vector3.Transform(FacingLocal, Rotation);
        StandYaw = MathF.Atan2(facing.X, facing.Z);
        FillAt = origin + Vector3.Transform(new Vector3(0, FillHeight, 0), Rotation);
        // The source's spots (world places; seats already turned to their tables: TownBuildings) moved with the building, the near ones left out, nearest the focus first.
        var focus = origin + Vector3.Transform(SeatFocusLocal, Rotation);
        IEnumerable<HomeSpot> Spots(Func<HomeSpot, bool> which) => source.Spots.Where(which)
            .Select(s => s with { Position = s.Position - resolved.Position + origin })
            .Where(s => new Vector2(s.Position.X - Stand.X, s.Position.Z - Stand.Z).Length() > SeatClearance && s.Position.Y < origin.Y + 20)   // the ground floor only
            .OrderBy(s => Vector3.DistanceSquared(s.Position, focus));
        var seats = Spots(s => s.Kind is HomeSpotKind.Chair or HomeSpotKind.ThroneSeat && s.SitClip is not null).ToList();
        Seats = [.. seats.Take(MaxSeated).Select(s => (s.Position, s.Yaw, s.SitClip!))];
        Crowd = MakeCrowd(db, site, source, seats.Skip(MaxSeated), Spots(s => s.Kind == HomeSpotKind.Shopkeeper), Spots(s => s.Kind == HomeSpotKind.Guard),
            Spots(s => s.Kind == HomeSpotKind.General));
        Ground = MakeGround(install, db, origin);
    }

    /// <summary>
    /// The bar's people: its residents' leader (the barman) at a shopkeeper node, the first of their squad (the guard) at a guard node, and patrons drawn from the town's
    /// bar squads, some on the seats left after the editor's characters' and some standing at the room's nodes. Rolled the same each time (fixed seeds).
    /// </summary>
    static List<(string, string?, Vector3, float, string, int)> MakeCrowd(GameDatabase db, TownSite site, TownBuilding source, IEnumerable<HomeSpot> seats,
        IEnumerable<HomeSpot> counter, IEnumerable<HomeSpot> posts, IEnumerable<HomeSpot> nodes)
    {
        var crowd = new List<(string, string?, Vector3, float, string, int)>();
        bool IsCharacter(string id) => db.Find(id)?.Type == Meitou.Data.Fcs.FcsRecordType.CHARACTER;
        if (db.Find(source.ResidentsTemplate) is { } residentRecord && SquadTemplate.From(residentRecord) is var residents)
        {
            if (residents.Leader is { } barman && IsCharacter(barman.Id) && counter.FirstOrDefault() is { Position: var at } spot && at != default)
                crowd.Add((barman.Id, residents.Faction, at, spot.Yaw, "idle_stand_confident", 11));
            if (residents.Squad.FirstOrDefault(m => IsCharacter(m.Id)) is { Id: { } guard } && posts.FirstOrDefault() is { Position: var post } postSpot && post != default)
                crowd.Add((guard, residents.Faction, post, postSpot.Yaw, "idle_stand_guard", 12));
        }
        var patrons = new List<(string Id, string? Faction)>();
        foreach (var link in site.Town.BarSquads)
            if (db.Find(link.Id) is { } record && SquadTemplate.From(record) is var squad)
                foreach (var m in (squad.Leader is { } leader ? [leader] : Array.Empty<RecordLink>()).Concat(squad.Squad).Concat(squad.Squad2).Concat(squad.ChooseFrom))
                    if (IsCharacter(m.Id) && !patrons.Exists(p => p.Id == m.Id)) patrons.Add((m.Id, squad.Faction));
        if (patrons.Count == 0) return crowd;
        var random = new Random(173);
        int next = 0;
        (string Id, string? Faction) Patron() => patrons[(next++ * 7 + random.Next(patrons.Count)) % patrons.Count];
        foreach (var seat in seats.Take(SeatedPatrons))
        {
            var (id, faction) = Patron();
            crowd.Add((id, faction, seat.Position, seat.Yaw, seat.SitClip!, 20 + next));
        }
        foreach (var node in nodes.Take(StandingPatrons))
        {
            var (id, faction) = Patron();
            crowd.Add((id, faction, node.Position, node.Yaw, Stances[random.Next(Stances.Length)], 40 + next));
        }
        return crowd;
    }

    /// <summary>
    /// The ground under the bar: <see cref="GroundMesh"/> drawn as its own map feature, scaled so its top spans <see cref="GroundHalfWidth"/> each way and raised so that top is
    /// the bar's ground level (the interior mask cuts it under the floor), and shrubs scattered on it. Empty when the data has no such feature.
    /// </summary>
    static List<PlacedMesh> MakeGround(GameInstall install, GameDatabase db, Vector3 origin)
    {
        var placed = new List<PlacedMesh>();
        if (TitleComposition.FeatureRecord(db, GroundMesh) is not { } look) return placed;
        string meshPath = look.GetPath("mesh");
        Vector3[] points;
        try
        {
            var mesh = Meitou.Data.Ogre.OgreMeshReader.ReadFile(Path.Combine(install.Root, meshPath.TrimStart('.', '\\', '/')));
            points = [.. (mesh.SharedVertexData?.ReadPositions() ?? []), .. mesh.SubMeshes.SelectMany(s => s.VertexData?.ReadPositions() ?? [])];
        }
        catch (IOException) { return placed; }
        if (points.Length == 0) return placed;
        float halfX = (points.Max(p => p.X) - points.Min(p => p.X)) / 2, halfZ = (points.Max(p => p.Z) - points.Min(p => p.Z)) / 2;
        var centre = new Vector2((points.Max(p => p.X) + points.Min(p => p.X)) / 2, (points.Max(p => p.Z) + points.Min(p => p.Z)) / 2);
        float scale = GroundHalfWidth / MathF.Max(MathF.Min(halfX, halfZ), 1);
        // The top: the highest vertices near the middle, so a rim or a spike does not lift the whole block.
        float top = points.Where(p => Vector2.Distance(new(p.X, p.Z), centre) < MathF.Min(halfX, halfZ) * 0.5f).Select(p => p.Y).DefaultIfEmpty(points.Max(p => p.Y)).Max();
        var at = new Vector3(origin.X - centre.X * scale, origin.Y - top * scale, origin.Z - centre.Y * scale);
        placed.Add(new PlacedMesh(meshPath, WorldObjectLayout.InstanceTransform(at, Quaternion.Identity, new Vector3(scale)), look, look, $"{Key}/ground", PlacedKind.MapFeature));
        var random = new Random(271);
        var shrubs = Shrubs.Select(name => TitleComposition.FeatureRecord(db, name)).OfType<GameRecord>().ToArray();
        for (int i = 0; i < ShrubCount && shrubs.Length > 0; i++)
        {
            var shrub = shrubs[random.Next(shrubs.Length)];
            float angle = random.NextSingle() * MathF.Tau, distance = ShrubNear + random.NextSingle() * (ShrubFar - ShrubNear);
            var spot = origin + new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle)) * distance;
            var transform = WorldObjectLayout.InstanceTransform(spot, Quaternion.CreateFromAxisAngle(Vector3.UnitY, random.NextSingle() * MathF.Tau), new Vector3(0.8f + random.NextSingle() * 0.8f));
            placed.Add(new PlacedMesh(shrub.GetPath("mesh"), transform, shrub, shrub, $"{Key}/shrub {i}", PlacedKind.MapFeature));
        }
        return placed;
    }

    /// <summary>The bar above <paramref name="anchor"/> (x, z; <paramref name="ground"/> gives the heights round it), or null when the data has no intact Storm House bar in The Hub.</summary>
    public static EditorBar? Above(GameInstall install, PopulationData data, Vector3 anchor, Func<float, float, float> ground)
    {
        foreach (var site in data.Sites.Where(s => s.Town.Name == TownName))
            if (site.Buildings.FirstOrDefault(b => b is { Destroyed: false, Resolved: not null } && b.Name == BuildingName && b.InteriorLayout == LayoutName) is { Resolved: { } resolved } source)
            {
                float top = float.MinValue;
                for (int x = -2; x <= 2; x++)
                    for (int z = -2; z <= 2; z++)
                        top = MathF.Max(top, ground(anchor.X + x * 100, anchor.Z + z * 100));
                return new EditorBar(install, data.Db, site, source, resolved, new Vector3(anchor.X, top + Lift, anchor.Z));
            }
        return null;
    }
}
