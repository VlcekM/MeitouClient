using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.World;
using Meitou.Rendering;
using Meitou.Rendering.Characters;

namespace Meitou.Game;

/// <summary>
/// The character editor's bar set (the <c>editorbar</c> switch, docs/characters.md "Character editor", Meitou): the base data's Storm House with its <c>Bar</c> interior, its lamps and
/// people, on a staged patch of ground, placed round the edited character in the stage picture where the platform otherwise stands. It is built from records alone, so the world (a save,
/// a world mod's towns) has no say in it. The edited character stands on its floor; the others being edited sit on its seats behind, among the bar's own people.
/// </summary>
sealed class EditorBar
{
    /// <summary>The base data's Storm House, a generic bar's residents and The Hub (whose <c>bar squads</c> give the patrons).</summary>
    const string BuildingId = "3384-TwoStorey.mod", ResidentsId = "44856-rebirth.mod", TownId = "18919-Newwworld.mod";
    const string PlacementKey = "meitou:editor-bar", LayoutName = "Bar";
    /// <summary>At most this many of the other edited characters sit in the bar.</summary>
    public const int MaxSeated = 5;
    /// <summary>Where the edited character stands in the building's frame (x across, z along, y up; the floor is 4.4 up): on the open floor between the counter and the tables.</summary>
    static readonly Vector3 StandLocal = new(12, 4.4f, -14);
    /// <summary>The building's +x, the way the character faces, turned onto the stage frame's +z (the character's front).</summary>
    static readonly Matrix4x4 FacingToStage = Matrix4x4.CreateRotationY(-MathF.PI / 2);
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
    const float FillShare = 0.5f, FillRadius = 220, FillHeight = 45;

    /// <summary>The building, its furniture, the ground and the shrubs, in the stage's frame.</summary>
    public IReadOnlyList<PlacedMesh> Meshes { get; }
    /// <summary>The lamps in the stage's frame, those that light the edited character most first, the room's fill last.</summary>
    public IReadOnlyList<StageLamp> Lamps { get; }
    /// <summary>The seats for the editor's characters, nearest the focus first: their place, the yaw the sitter faces and the clip it sits with.</summary>
    public IReadOnlyList<(Vector3 Position, float Yaw, string Clip)> Seats { get; }
    /// <summary>The bar's own people: a CHARACTER record (with its template's faction, if any), where it is, its yaw, the clip it holds and the seed of its rolled look.</summary>
    public IReadOnlyList<(string Record, string? Faction, Vector3 Position, float Yaw, string Clip, int Seed)> Crowd { get; }

    EditorBar(WorldObjects world, GameRecord record, Matrix4x4 feet)
    {
        var db = world.Database;
        // Any place but the zero one (which means unplaced); everything is moved off it below, and it seeds the choice among a building's part variants.
        var at = new Vector3(1, 0, 0);
        var placement = new BuildingPlacement(WorldLayout.ZoneOf(at.X, at.Z), PlacementKey, BuildingId, at, Quaternion.Identity, null, at.Y)
        {
            Key = PlacementKey, InteriorLayout = LayoutName, ExteriorLayout = "",
        };
        var resolved = new ResolvedBuilding(placement, record, at, false);
        var stand = at + StandLocal;
        var toStage = Matrix4x4.CreateTranslation(-stand) * FacingToStage * feet;
        Vector3 Place(Vector3 p) => Vector3.Transform(p, toStage);
        float Turn(float yaw)
        {
            var forward = Vector3.TransformNormal(new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw)), toStage);
            return MathF.Atan2(forward.X, forward.Z);
        }

        Meshes = [.. world.BuildRuntime(placement).Items.Concat(MakeGround(world.Install, db, at)).Select(p => p with { Transform = p.Transform * toStage })];

        var lamps = WorldLights.ForBuilding(db, world.Levels, resolved, world.Layouts);
        float power = lamps.Count > 0 ? lamps.Average(l => l.Intensity) * FillShare : 1;
        Lamps =
        [
            .. lamps.OrderByDescending(l => l.Intensity * MathF.Max(1 - Vector3.Distance(l.Position, stand) / MathF.Max(l.Radius, 1), 0))
                .Select(l => new StageLamp(Place(l.Position), l.Radius, l.Colour * l.Intensity)),
            new StageLamp(Place(at + new Vector3(0, FillHeight, 0)), FillRadius, Vector3.One * power),
        ];

        var focus = at + SeatFocusLocal;
        var spots = TownBuildings.InteriorSpots(db, world.Layouts, resolved);
        IEnumerable<HomeSpot> Spots(Func<HomeSpot, bool> which) => spots.Where(which)
            .Where(s => new Vector2(s.Position.X - stand.X, s.Position.Z - stand.Z).Length() > SeatClearance && s.Position.Y < at.Y + 20)   // the ground floor only
            .OrderBy(s => Vector3.DistanceSquared(s.Position, focus));
        var seats = Spots(s => s.Kind is HomeSpotKind.Chair or HomeSpotKind.ThroneSeat && s.SitClip is not null).ToList();
        Seats = [.. seats.Take(MaxSeated).Select(s => (Place(s.Position), Turn(s.Yaw), s.SitClip!))];
        Crowd = [.. MakeCrowd(db, seats.Skip(MaxSeated), Spots(s => s.Kind == HomeSpotKind.Shopkeeper), Spots(s => s.Kind == HomeSpotKind.Guard), Spots(s => s.Kind == HomeSpotKind.General))
            .Select(c => (c.Record, c.Faction, Place(c.Position), Turn(c.Yaw), c.Clip, c.Seed))];
    }

    /// <summary>The set round <paramref name="feet"/> (the stage's transform of the character); null when the data has no such building or layout.</summary>
    public static EditorBar? At(WorldObjects world, Matrix4x4 feet) =>
        world.Database.Find(BuildingId) is { Type: FcsRecordType.BUILDING } record && world.Layouts.Find(record, LayoutName, exterior: false) is not null ? new EditorBar(world, record, feet) : null;

    /// <summary>
    /// The bar's people: its residents' leader (the barman) at a shopkeeper node, the first of their squad (the guard) at a guard node, and patrons drawn from the town's
    /// bar squads, some on the seats left after the editor's characters' and some standing at the room's nodes. Rolled the same each time (fixed seeds).
    /// </summary>
    static List<(string Record, string? Faction, Vector3 Position, float Yaw, string Clip, int Seed)> MakeCrowd(GameDatabase db, IEnumerable<HomeSpot> seats,
        IEnumerable<HomeSpot> counter, IEnumerable<HomeSpot> posts, IEnumerable<HomeSpot> nodes)
    {
        var crowd = new List<(string, string?, Vector3, float, string, int)>();
        bool IsCharacter(string id) => db.Find(id)?.Type == FcsRecordType.CHARACTER;
        if (db.Find(ResidentsId) is { } residentRecord && SquadTemplate.From(residentRecord) is var residents)
        {
            if (residents.Leader is { } barman && IsCharacter(barman.Id) && counter.FirstOrDefault() is { Position: var at } spot && at != default)
                crowd.Add((barman.Id, residents.Faction, at, spot.Yaw, "idle_stand_confident", 11));
            if (residents.Squad.FirstOrDefault(m => IsCharacter(m.Id)) is { Id: { } guard } && posts.FirstOrDefault() is { Position: var post } postSpot && post != default)
                crowd.Add((guard, residents.Faction, post, postSpot.Yaw, "idle_stand_guard", 12));
        }
        var patrons = new List<(string Id, string? Faction)>();
        if (db.Find(TownId) is { } town)
            foreach (var link in town.GetReferences("bar squads"))
                if (db.Find(link.TargetStringId) is { } record && SquadTemplate.From(record) is var squad)
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
    /// The ground under the bar: <see cref="GroundMesh"/> as a map feature, scaled so its top spans <see cref="GroundHalfWidth"/> each way and raised so that top is the bar's
    /// ground level, and shrubs scattered on it. Empty when the data has no such feature.
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
        placed.Add(new PlacedMesh(meshPath, WorldObjectLayout.InstanceTransform(at, Quaternion.Identity, new Vector3(scale)), look, look, $"{PlacementKey}/ground", PlacedKind.MapFeature));
        var random = new Random(271);
        var shrubs = Shrubs.Select(name => TitleComposition.FeatureRecord(db, name)).OfType<GameRecord>().ToArray();
        for (int i = 0; i < ShrubCount && shrubs.Length > 0; i++)
        {
            var shrub = shrubs[random.Next(shrubs.Length)];
            float angle = random.NextSingle() * MathF.Tau, distance = ShrubNear + random.NextSingle() * (ShrubFar - ShrubNear);
            var spot = origin + new Vector3(MathF.Sin(angle), 0, MathF.Cos(angle)) * distance;
            var transform = WorldObjectLayout.InstanceTransform(spot, Quaternion.CreateFromAxisAngle(Vector3.UnitY, random.NextSingle() * MathF.Tau), new Vector3(0.8f + random.NextSingle() * 0.8f));
            placed.Add(new PlacedMesh(shrub.GetPath("mesh"), transform, shrub, shrub, $"{PlacementKey}/shrub {i}", PlacedKind.MapFeature));
        }
        return placed;
    }
}
