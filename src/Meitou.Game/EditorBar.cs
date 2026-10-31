using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The Meitou UI's character editor scene (docs/characters.md "Character editor", Meitou): a copy of The Hub's Storm House bar, its walls, furniture and lamps,
/// placed high above the edited character so nothing of the world around it reaches in, and drawn at night by the world's own renderers. The edited character
/// stands on its floor; the others being edited sit on its seats behind.
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
    /// <summary>The seats taken are those nearest this point of the building's frame: the tables behind the edited character as the camera first sees it.</summary>
    static readonly Vector3 SeatFocusLocal = new(-12, 4.4f, -8);

    public BuildingPlacement Placement { get; }
    /// <summary>The copy's origin (absolute height) and turn, as the source's.</summary>
    public Vector3 Origin { get; }
    public Quaternion Rotation { get; }
    /// <summary>Where the edited character stands and its yaw (0 along +Z, turning towards +X).</summary>
    public Vector3 Stand { get; }
    public float StandYaw { get; }
    /// <summary>The seats, nearest the focus first: their place, the yaw the sitter faces and the clip it sits with.</summary>
    public IReadOnlyList<(Vector3 Position, float Yaw, string Clip)> Seats { get; }

    EditorBar(TownBuilding source, ResolvedBuilding resolved, Vector3 origin)
    {
        var level = resolved.Placement;
        Origin = origin;
        Rotation = level.Rotation;
        Placement = level with { Zone = WorldLayout.ZoneOf(origin.X, origin.Z), Position = origin with { Y = 0 }, WorldY = origin.Y, StateZone = level.Zone, Key = Key, DestroyedOverride = false };
        Stand = origin + Vector3.Transform(StandLocal, Rotation);
        var facing = Vector3.Transform(FacingLocal, Rotation);
        StandYaw = MathF.Atan2(facing.X, facing.Z);
        // The source's seats (world places, already turned to their tables: TownBuildings) moved with the building.
        var focus = origin + Vector3.Transform(SeatFocusLocal, Rotation);
        Seats = [.. source.Spots.Where(s => s.Kind is HomeSpotKind.Chair or HomeSpotKind.ThroneSeat && s.SitClip is not null)
            .Select(s => (Position: s.Position - resolved.Position + origin, s.Yaw, Clip: s.SitClip!))
            .OrderBy(s => Vector3.DistanceSquared(s.Position, focus))];
    }

    /// <summary>The bar above <paramref name="anchor"/> (x, z; <paramref name="ground"/> gives the heights round it), or null when the data has no intact Storm House bar in The Hub.</summary>
    public static EditorBar? Above(PopulationData data, Vector3 anchor, Func<float, float, float> ground)
    {
        var source = data.Sites.Where(s => s.Town.Name == TownName).SelectMany(s => s.Buildings)
            .FirstOrDefault(b => b is { Destroyed: false, Resolved: not null } && b.Name == BuildingName && b.InteriorLayout == LayoutName);
        if (source?.Resolved is not { } resolved) return null;
        float top = float.MinValue;
        for (int x = -2; x <= 2; x++)
            for (int z = -2; z <= 2; z++)
                top = MathF.Max(top, ground(anchor.X + x * 100, anchor.Z + z * 100));
        return new EditorBar(source, resolved, new Vector3(anchor.X, top + Lift, anchor.Z));
    }
}
