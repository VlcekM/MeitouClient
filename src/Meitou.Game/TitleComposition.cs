using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.World;
using Meitou.Rendering;

namespace Meitou.Game;

/// <summary>
/// The Meitou title's view: a spot of the real world in the Okran's Pride region, drawn by the world's own renderers, with a wanderer
/// and a road fight staged in it, and the sun turned to the right of the frame (<see cref="SkyClock.SunTurn"/>).
/// </summary>
sealed class TitleComposition
{
    /// <summary>The view, as a viewer camera code (Ctrl+C in the viewer): target, yaw, pitch and distance.</summary>
    internal const string Spot = "01D8183CC7662DD3442850C8C60D2BA740BE448EBDEE49D345";
    /// <summary>The viewer's lens, which the spot was chosen with.</summary>
    internal const float FieldOfView = 50 * MathF.PI / 180;
    /// <summary>The near plane's cap: the eye is ~350 units over the ground, which would put the near plane past the wanderer.</summary>
    const float NearPlane = 20;
    /// <summary>How far right of the view's centre the sun stands.</summary>
    const float SunBearing = 26 * MathF.PI / 180;
    /// <summary>Where the wanderer looks: this far right of the view's heading (negative: left).</summary>
    const float WandererBearing = -10 * MathF.PI / 180;
    /// <summary>Where the wanderer stands: on the plateau under this point of a 16:9 frame (0..1 across, 0..1 down), else the ground under it.</summary>
    static readonly Vector2 WandererOnScreen = new(0.337f, 0.93f);
    /// <summary>
    /// The foreground. The ridge: rocks drawn with the terrain texturing of the local cliff feature (<see cref="RidgeLook"/>), side by side from the
    /// frame's bottom left to its bottom right, each one's top under its screen point at its distance ahead (along the ground). The plateau the
    /// wanderer stands on, close in front of the camera: a flat-topped block drawn as its own feature, its top under its screen point likewise.
    /// Then rocks and scree on the plateau, each drawn as its own feature where the ray through its screen point meets the plateau's top, sunk
    /// by its share of its height. A building (named by its record) stands on the plateau's top under its screen point, scaled about its foot.
    /// Every piece is turned by its yaw (degrees) from the view and scaled. The plateau comes before what lies on it.
    /// </summary>
    const string RidgeLook = "CLiff_Curved.mesh";
    /// <summary>How far left of its screen point the plateau block is moved (world units), so its right-hand cliff falls just right of the wanderer.</summary>
    const float PlateauLeft = 125;
    enum Part { Ridge, Plateau, OnPlateau, Building }
    static readonly (string Mesh, Part Part, Vector2 Screen, float Yaw, float Ahead, float Scale, float Sink)[] RidgePieces =
    [
        ("FLatTop_Pillar01.mesh", Part.Plateau, new(0.337f, 0.92f), 180, 45, 1, 0),
        ("Mesa_Zone_ScreeStones01.mesh", Part.OnPlateau, new(0.16f, 0.97f), 140, 0, 0.03f, 0.5f),
        ("Rock_MenhirStone_01.mesh", Part.OnPlateau, new(0.24f, 0.88f), 40, 0, 0.09f, 0.25f),
        // shrubs on the cliff top at the left, a tiny one by its edge on the right, and a few on the far edge at the left
        ("bush01.mesh", Part.OnPlateau, new(-0.04f, 0.8f), 40, 0, 1.1f, 0.05f), ("shrub.mesh", Part.OnPlateau, new(0.09f, 0.79f), 160, 0, 0.3f, 0.05f),
        ("shrubs&grass.mesh", Part.OnPlateau, new(0.15f, 0.86f), 90, 0, 1.2f, 0.1f), ("bush01.mesh", Part.OnPlateau, new(0.43f, 0.87f), 220, 0, 0.45f, 0.05f),
        ("bush01.mesh", Part.OnPlateau, new(0.03f, 0.715f), 0, 0, 1.5f, 0.05f), ("shrub.mesh", Part.OnPlateau, new(0.2f, 0.715f), 30, 0, 0.45f, 0.05f),
        ("shrubs&grass.mesh", Part.OnPlateau, new(0.12f, 0.72f), 60, 0, 1.6f, 0.1f),
        // a watchtower (the buildable one) at the far right, as on the original poster, on the valley floor this far ahead
        ("Watchtower", Part.Building, new(0.88f, 0.5f), 145, 2600, 1, 0),
    ];
    /// <summary>The ghost-town ruin under the logo (houses and scrap piles the foliage system places round this x, z), and how far left across the view it is moved.</summary>
    static readonly Vector3 RuinCentre = new(-52860, 0, -22715);
    const float RuinRadius = 350, RuinMove = 400;

    internal static WorldCameraCode.Pose Pose => WorldCameraCode.Decode(Spot) ?? throw new InvalidOperationException("TitleComposition.Spot is not a camera code");
    /// <summary>The view's target on the ground: the world is loaded round it when the game boots to the title.</summary>
    internal static (float X, float Z) Focus => (Pose.Target.X, Pose.Target.Z);

    public Vector3 Wanderer { get; }
    public float WandererYaw { get; }
    public CharacterAppearance Appearance { get; }

    /// <summary>The saved character's body from its stats and hunger (null: the staged wanderer's own shape).</summary>
    public BodyBuild? SavedBuild { get; init; }
    /// <summary>The road's three fight centres, projected onto the actual valley floor.</summary>
    public IReadOnlyList<Vector3> Fights { get; }
    /// <summary>The ruin moved left of the logo, for the title only (<see cref="FoliageRenderer.Shift"/>).</summary>
    public FoliageShift Ruins { get; }
    /// <summary>The staged ridge (<see cref="WorldObjectRenderer.Staged"/>), empty when the cliff feature or the outcrop mesh is missing.</summary>
    public IReadOnlyList<PlacedMesh> Staged { get; }

    public TitleComposition(GameInstall install, GameDatabase database, Func<float, float, float> ground, string character, CharacterAppearance? savedCharacter = null)
    {
        var view = new WorldCamera();
        Place(view);
        var (pieces, plateau, top) = Ridge(install, database, view, ground);
        Staged = pieces;
        Wanderer = OnPlateau(view, top, plateau, WandererOnScreen) ?? GroundUnder(view, WandererOnScreen, ground);
        Fights = [GroundUnder(view, new(0.70f, 0.85f), ground), GroundUnder(view, new(0.755f, 0.89f), ground), GroundUnder(view, new(0.81f, 0.93f), ground)];
        // His back to the camera, looking out over the valley.
        var facing = Vector3.Normalize(view.Forward with { Y = 0 }) * MathF.Cos(WandererBearing) + Vector3.Normalize(Vector3.Cross(view.Forward, Vector3.UnitY)) * MathF.Sin(WandererBearing);
        WandererYaw = MathF.Atan2(facing.X, facing.Z);   // the characters' yaw: 0 faces +Z (MovementSystem)
        var left = -Vector3.Normalize(Vector3.Cross(view.Forward, Vector3.UnitY) with { Y = 0 });
        Ruins = new(RuinCentre, RuinRadius, left * RuinMove, name => name.StartsWith("Ghost", StringComparison.Ordinal), ground);
        var gear = new[] { "Iron Hat", "Dustcoat", "Drifter's Leather Pants", "Drifter's Boots", "Katana", "Small Backpack" }
            .Where(n => database.Records.Values.Any(r => r.Name == n)).ToArray();
        Appearance = savedCharacter ?? CharacterAppearance.Build(database, install.Root, character, new CharacterOptions { Female = false, Naked = true, Equip = gear });
        // A strongish build that reads from behind: more muscle, broader shoulders and back, thicker arms and neck.
        if (savedCharacter is null)
        {
            var shape = CharacterShape.InputFor(database, Appearance);
            var sliders = new Dictionary<string, float>(shape.Sliders, StringComparer.Ordinal)
            {
                ["Shoulders"] = 111, ["Frame"] = 105, ["Chest"] = 106, ["Arm bulk"] = 110, ["Neck width"] = 106, ["Waist"] = 99,
            };
            Appearance.Shape = shape with { Muscle = 0.6f, Sliders = sliders };
        }
    }

    /// <summary>
    /// The foreground's pieces, the plateau's vertices in the world and the height of its top (the wanderer is stood on it). Nothing when the cliff
    /// feature is missing.
    /// </summary>
    static (IReadOnlyList<PlacedMesh> Staged, IReadOnlyList<Vector3> Plateau, float? Top) Ridge(GameInstall install, GameDatabase database, WorldCamera view,
        Func<float, float, float> ground)
    {
        var look = RidgeRecord(database);
        if (look is null) return ([], [], null);
        string folder = look.GetPath("mesh")[..^RidgeLook.Length];
        var placed = new List<PlacedMesh>();
        var surface = new List<Vector3>();
        float? plateauTop = null;
        foreach (var (name, part, screen, yaw, ahead, scale, sink) in RidgePieces)
        {
            if (part == Part.OnPlateau && plateauTop is null) continue;
            if (part == Part.Building)
            {
                // A building record by name, laid out as the game lays out a placed one: its origin on the plateau's top under its screen point,
                // or with a distance ahead, on the ground that far along the ray.
                if (database.Records.Values.FirstOrDefault(r => r.Type == Meitou.Data.Fcs.FcsRecordType.BUILDING && r.Name == name) is not { } building) continue;
                var toward = RayThrough(view, screen);
                var foot = view.Eye + toward * (ahead / MathF.Max(new Vector2(toward.X, toward.Z).Length(), 1e-3f));
                foot = ahead == 0 && plateauTop is { } level ? PlaneHit(view, level, screen) : foot with { Y = ground(foot.X, foot.Z) };
                // Scaled about its foot: a small one near the plateau's far edge reads as a big one far off, which the plateau would hide.
                var about = Matrix4x4.CreateTranslation(-foot) * Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(foot);
                placed.AddRange(WorldObjectLayout.Building(database, building, $"meitou title {name}", foot,
                    Quaternion.CreateFromAxisAngle(Vector3.UnitY, view.Yaw + yaw * MathF.PI / 180)).Select(p => p with { Transform = p.Transform * about }));
                continue;
            }
            // The ridge borrows the cliff's terrain texturing; the plateau and what lies on it are drawn as their own features.
            var pieceLook = part == Part.Ridge ? look : FeatureRecord(database, name) ?? look;
            string meshPath = pieceLook == look ? folder + name : pieceLook.GetPath("mesh");
            Vector3[] points;
            try
            {
                var mesh = Meitou.Data.Ogre.OgreMeshReader.ReadFile(Path.Combine(install.Root, meshPath.TrimStart('.', '\\', '/')));
                points = [.. (mesh.SharedVertexData?.ReadPositions() ?? []), .. mesh.SubMeshes.SelectMany(s => s.VertexData?.ReadPositions() ?? [])];
            }
            catch (IOException) { continue; }
            if (points.Length == 0) continue;
            float highest = points.Max(p => p.Y), lowest = points.Min(p => p.Y);
            var ray = RayThrough(view, screen);
            Vector3 at;
            if (part == Part.OnPlateau)
            {
                // Where the ray meets the plateau's top, sunk by its share of its height.
                var hit = OnPlateau(view, plateauTop, surface, screen)!.Value;
                at = hit with { Y = hit.Y - (lowest + (highest - lowest) * sink) * scale };
            }
            else
            {
                // Its distance ahead along the ground under the ray, its highest vertex at that ray's height there.
                var lip = view.Eye + ray * (ahead / MathF.Max(new Vector2(ray.X, ray.Z).Length(), 1e-3f));
                at = lip with { Y = lip.Y - highest * scale };
                if (part == Part.Plateau)
                {
                    // Its top under its screen point, the block itself moved left so its right edge (a sheer drop) is just right of the wanderer.
                    plateauTop = lip.Y;
                    at -= Vector3.Normalize(Vector3.Cross(view.Forward, Vector3.UnitY) with { Y = 0 }) * PlateauLeft;
                }
            }
            var transform = WorldObjectLayout.InstanceTransform(at, Quaternion.CreateFromAxisAngle(Vector3.UnitY, view.Yaw + yaw * MathF.PI / 180), new Vector3(scale));
            placed.Add(new PlacedMesh(meshPath, transform, pieceLook, pieceLook, $"meitou title ridge {placed.Count}", PlacedKind.MapFeature));
            if (part == Part.Plateau) surface.AddRange(points.Select(p => Vector3.Transform(p, transform)));
        }
        return (placed, surface, plateauTop);
    }


    /// <summary>The map feature whose terrain texturing the staged rocks borrow (<see cref="RidgeLook"/>), null when the data has none.</summary>
    static GameRecord? RidgeRecord(GameDatabase database) => FeatureRecord(database, RidgeLook);

    /// <summary>The map feature, else the foliage mesh, drawing the mesh file named <paramref name="mesh"/>; null when the data has none.</summary>
    internal static GameRecord? FeatureRecord(GameDatabase database, string mesh)
    {
        GameRecord? Of(Meitou.Data.Fcs.FcsRecordType type) => database.Records.Values.FirstOrDefault(r => r.Type == type &&
            string.Equals(Path.GetFileName(r.GetPath("mesh").Replace('\\', '/')), mesh, StringComparison.OrdinalIgnoreCase));
        return Of(Meitou.Data.Fcs.FcsRecordType.MAP_FEATURES) ?? Of(Meitou.Data.Fcs.FcsRecordType.FOLIAGE_MESH);
    }

    /// <summary>
    /// Where on the plateau the ray through <paramref name="screen"/> lands: on its top (<paramref name="top"/>), raised or lowered to the highest of its
    /// top-surface vertices (within 8 units of it) within 12 units across; null when there is no plateau.
    /// </summary>
    static Vector3? OnPlateau(WorldCamera view, float? top, IReadOnlyList<Vector3> plateau, Vector2 screen)
    {
        if (top is not { } height) return null;
        var hit = PlaneHit(view, height, screen);
        var near = plateau.Where(p => p.Y > height - 8 && Vector2.Distance(new(p.X, p.Z), new(hit.X, hit.Z)) < 12).ToList();
        return near.Count == 0 ? hit : hit with { Y = near.Max(p => p.Y) };
    }

    /// <summary>Where the ray through <paramref name="screen"/> meets the level <paramref name="height"/> (below the eye).</summary>
    static Vector3 PlaneHit(WorldCamera view, float height, Vector2 screen)
    {
        var ray = RayThrough(view, screen);
        return view.Eye + ray * ((height - view.Eye.Y) / MathF.Min(ray.Y, -1e-3f));
    }
    /// <summary>The view ray through <paramref name="screen"/> of a 16:9 frame (0..1 across, 0..1 down).</summary>
    static Vector3 RayThrough(WorldCamera camera, Vector2 screen)
    {
        var forward = camera.Forward;
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var up = Vector3.Cross(right, forward);
        float half = MathF.Tan(camera.FieldOfView / 2);
        return Vector3.Normalize(forward + right * ((screen.X * 2 - 1) * half * 16 / 9) + up * ((1 - screen.Y * 2) * half));
    }

    /// <summary>The title's camera: the spot exactly as the camera code gives it, with the viewer's lens.</summary>
    public static void Place(WorldCamera camera)
    {
        WorldCameraCode.Apply(Pose, camera);
        camera.FieldOfView = FieldOfView;
        camera.MaxNear = NearPlane;
    }

    /// <summary>Where the ray through <paramref name="screen"/> (16:9) first meets the ground, marching in 2-unit steps out to 20000 units.</summary>
    static Vector3 GroundUnder(WorldCamera camera, Vector2 screen, Func<float, float, float> ground)
    {
        var ray = RayThrough(camera, screen);
        var eye = camera.Eye;
        for (float t = 2; t < 20000; t += 2)
        {
            var p = eye + ray * t;
            float y = ground(p.X, p.Z);
            if (p.Y <= y) return p with { Y = y };
        }
        return eye + camera.Forward * 1000;
    }


    /// <summary>How far the moons are nudged from where the game puts them: left by about 15% of the big moon's size, up by a tenth.</summary>
    const float MoonsLeft = 2.4f * MathF.PI / 180, MoonsUp = 1.6f * MathF.PI / 180;
    /// <summary>The clouds' drift on the title (world units per second, right to left across the view): several times a windy day's.</summary>
    const float CloudSpeed = 125;

    /// <summary>The turn that moves the planets <see cref="MoonsLeft"/> and <see cref="MoonsUp"/> in <paramref name="camera"/>'s view (<see cref="SkyRenderer.PlanetTurn"/>).</summary>
    public static Quaternion MoonTurn(WorldCamera camera)
    {
        var right = Vector3.Normalize(Vector3.Cross(camera.Forward, Vector3.UnitY));
        return Quaternion.CreateFromAxisAngle(Vector3.UnitY, MoonsLeft) * Quaternion.CreateFromAxisAngle(right, MoonsUp);
    }

    /// <summary>The clouds' drift velocity (x, z) for <paramref name="camera"/>'s view: <see cref="CloudSpeed"/> towards the left.</summary>
    public static Vector2 CloudWind(WorldCamera camera)
    {
        var left = -Vector3.Normalize(Vector3.Cross(camera.Forward, Vector3.UnitY));
        return new Vector2(left.X, left.Z) * CloudSpeed;
    }
    /// <summary>The <see cref="SkyClock.SunTurn"/> that puts the sun of <paramref name="hour"/> <see cref="SunBearing"/> right of <paramref name="camera"/>'s heading.</summary>
    public static float SunTurn(SkyClock clock, float hour, WorldCamera camera)
    {
        var sun = (clock with { SunTurn = 0 }).SunDirection(hour);
        var ahead = camera.Forward;
        // Right of the heading is the positive sense of SunTurn (counter-clockwise in X/Z, which is clockwise on screen from above).
        return MathF.Atan2(ahead.Z, ahead.X) + SunBearing - MathF.Atan2(sun.Z, sun.X);
    }

    /// <summary>The wanderer, held in one frame of the relaxed idle: a still figure, as on the poster.</summary>
    public void Fill(CharacterDrawList list) =>
        list.Add(new CharacterInstance(long.MinValue + 1, Appearance, Wanderer, WandererYaw, [new("idle_stand_confident", 0.5f, 1)]) { Build = SavedBuild });
}
