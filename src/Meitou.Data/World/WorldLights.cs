using System.Numerics;
using Meitou.Data.Fcs;

namespace Meitou.Data.World;

/// <summary>LIGHT <c>type</c> (fcs_enums.def <c>LightType</c>): the game makes an Ogre point light or spot light of it.</summary>
public enum WorldLightType
{
    Point = 0,
    Spot = 1,
}

/// <summary>LIGHT <c>effect</c> (fcs_enums.def <c>LightEffect</c>); see <see cref="WorldLights.EffectFactor"/>.</summary>
public enum WorldLightEffect
{
    None = 0,
    Pulse = 1,
    Flicker = 2,
    Shimmer = 3,
}

/// <summary>When the game shows a building's lights (docs/formats/lights.md, "When a light is on").</summary>
public enum WorldLightRule
{
    /// <summary>A light building (<c>function</c> BF_LIGHT, lamp posts and wall lights): drawn only while the daylight factor is at most 0.1.</summary>
    Night,
    /// <summary>Any other building: shown and hidden with the building's on state (day and night alike); Unknown what that state is for town buildings.</summary>
    WhenBuildingOn,
}

/// <summary>
/// One placed light of the world (docs/formats/lights.md). Lit like the sun: a surface receives
/// <c>π · max(N·L, 0) · Colour · Intensity · Attenuation(d, Radius) [· SpotFactor]</c>, in the same units as the sun term.
/// </summary>
/// <param name="Position">World position.</param>
/// <param name="Colour">The LIGHT <c>diffuse</c>, each channel byte / 255, used as is (the game does no sRGB decode).</param>
/// <param name="Intensity">The light's power: <c>brightness</c> plus the building's roll in ±<c>variance</c>/2; ≤ 0 means the game never draws it.</param>
/// <param name="Radius">The LIGHT <c>radius</c>: the light reaches exactly this far (<see cref="WorldLights.Attenuation"/>).</param>
/// <param name="Direction">Unit direction a spot light points in (also set for point lights).</param>
/// <param name="InnerAngle">Spot inner cone, full angle in radians (LIGHT <c>inner</c>, degrees in the data).</param>
/// <param name="OuterAngle">Spot outer cone, full angle in radians (LIGHT <c>outer</c>); may be below <see cref="InnerAngle"/> in the data.</param>
/// <param name="Falloff">Spot falloff exponent between the cones (LIGHT <c>falloff</c>).</param>
/// <param name="Interior">Placed by a building's interior layout (furniture inside); the rest are on the building or its exterior layout.</param>
/// <param name="Floor">The holder part's <c>building floor</c> (the game hands it to the light pass with the light; use Unknown).</param>
/// <param name="LightId">The LIGHT record's string id.</param>
/// <param name="BuildingId">The BUILDING record that owns the light.</param>
/// <param name="PlacementId">The placement (zone instance id, plus "/layout item id" for layout objects).</param>
/// <param name="PowerOn">The placed building's GAMESTATE_BUILDING <c>power on</c> when its state has one (null for layout objects and states without it); what it does to the lights is Unknown.</param>
public sealed record WorldLight(
    Vector3 Position,
    Vector3 Colour,
    float Intensity,
    float Radius,
    WorldLightType Type,
    Vector3 Direction,
    float InnerAngle,
    float OuterAngle,
    float Falloff,
    WorldLightEffect Effect,
    WorldLightRule Rule,
    bool Interior,
    int Floor,
    string LightId,
    string BuildingId,
    string PlacementId,
    bool? PowerOn = null);

/// <summary>
/// The world's light list (docs/formats/lights.md): the LIGHT instances of every placed building, of the buildings' doors and of their
/// exterior (and optionally interior) layout objects, placed by <see cref="WorldObjectLayout.Building(GameDatabase, GameRecord, string, Vector3, Quaternion, BuildingState?, List{PlacedLight}?)"/>,
/// plus the game's shading formulas for them.
/// </summary>
public static class WorldLights
{
    /// <summary>BuildingFunction.BF_LIGHT: the game makes such a building a light building (class BCTYPE_LIGHT), whose lights are night-only.</summary>
    public const int LightFunction = 15;

    /// <summary>The light pass draws a night-only light while the daylight factor is at most this.</summary>
    public const float NightDaylightLimit = 0.1f;

    /// <summary>Hours over which the daylight factor ramps at sunrise and at sunset (1/12 hour).</summary>
    public const float DaylightRampHours = 1f / 12;

    /// <summary>The lights of every placed building of the world. Destroyed buildings give none (Unknown in the game).</summary>
    /// <param name="layouts">Building layouts (from <c>interiors.level</c>) for the lights of exterior/interior layout objects; null skips them.</param>
    /// <param name="includeInteriors">Also the interior layouts' lights (furniture inside buildings), flagged <see cref="WorldLight.Interior"/>.</param>
    public static IReadOnlyList<WorldLight> ForWorld(GameDatabase db, WorldLevelData levels, Func<double, double, float> terrainHeight,
        BuildingLayouts? layouts = null, bool includeInteriors = true)
    {
        var result = new List<WorldLight>();
        foreach (var b in levels.Buildings())
            if (BuildingPlacements.Resolve(b, db, levels, terrainHeight) is { } resolved)
                result.AddRange(ForBuilding(db, levels, resolved, layouts, includeInteriors));
        return result;
    }

    /// <summary>The lights of one resolved placement (see <see cref="BuildingPlacements.Resolve"/>), for per-zone use.</summary>
    public static List<WorldLight> ForBuilding(GameDatabase db, WorldLevelData levels, ResolvedBuilding building, BuildingLayouts? layouts = null,
        bool includeInteriors = true)
    {
        var result = new List<WorldLight>();
        if (building.Destroyed) return result;
        var b = building.Placement;
        var state = b.StateId is not null && levels.Zones.TryGetValue(b.Zone, out var zoneDb) ? zoneDb.Find(b.StateId) : null;
        bool? powerOn = state is not null && state.Bools.TryGetValue("power on", out var on) ? on : null;
        var placed = new List<PlacedLight>();
        WorldObjectLayout.Building(db, building.Record, b.InstanceId, building.Position, b.Rotation, new BuildingState(), placed);
        foreach (var p in placed) result.Add(FromPlaced(p, interior: false, powerOn));
        if (layouts is null || state is null) return result;
        AddLayout(db, layouts, building, state.GetString("exterior layout name"), exterior: true, result);
        if (includeInteriors) AddLayout(db, layouts, building, state.GetString("interior layout name"), exterior: false, result);
        return result;
    }

    static void AddLayout(GameDatabase db, BuildingLayouts layouts, ResolvedBuilding building, string name, bool exterior, List<WorldLight> result)
    {
        if (name.Length == 0 || layouts.Find(building.Record, name, exterior) is not { } layout) return;
        foreach (var item in layouts.Items(layout))
        {
            var (at, rotation) = BuildingLayouts.Place(building.Position, building.Placement.Rotation, item);
            var placed = new List<PlacedLight>();
            WorldObjectLayout.Building(db, item.Building, $"{building.Placement.InstanceId}/{item.InstanceId}", at, rotation, new BuildingState(), placed);
            foreach (var p in placed) result.Add(FromPlaced(p, interior: !exterior, powerOn: null));
        }
    }

    /// <summary>A placed LIGHT instance as a <see cref="WorldLight"/>; <paramref name="powerOn"/> is the owner's state <c>power on</c>, if any.</summary>
    public static WorldLight FromPlaced(PlacedLight p, bool interior, bool? powerOn = null)
    {
        var light = p.Light;
        return new WorldLight(
            p.Position,
            Colour(light.GetInt("diffuse", 0xFFFFFF)),
            p.Power,
            light.GetFloat("radius", 100),
            light.GetInt("type") == 1 ? WorldLightType.Spot : WorldLightType.Point,
            p.Direction,
            light.GetFloat("inner", 45) * (MathF.PI / 180),
            light.GetFloat("outer", 40) * (MathF.PI / 180),
            light.GetFloat("falloff", 1),
            (WorldLightEffect)Math.Clamp(light.GetInt("effect"), 0, 3),
            p.Owner.GetInt("function") == LightFunction ? WorldLightRule.Night : WorldLightRule.WhenBuildingOn,
            interior,
            p.Holder.Type == FcsRecordType.BUILDING_PART ? p.Holder.GetInt("building floor") : 0,
            light.StringId,
            p.Owner.StringId,
            p.PlacementId,
            powerOn);
    }
    /// <summary>A packed 0xRRGGBB int as (r, g, b) / 255, as Ogre's <c>ColourValue::setAsARGB</c> unpacks it.</summary>
    public static Vector3 Colour(int rgb) => new Vector3((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF) / 255f;

    /// <summary>
    /// The game's distance attenuation (<c>deferred.hlsl</c> light pass), of x = distance / radius: 0.2 + 0.8 (1 − x)³ below x = 0.649,
    /// 0.242 − 3 (x − 0.6)² up to 0.8, then 3 (1 − x)², and nothing beyond the radius. Not inverse-square: 1 at the light, 0.30 at half the radius,
    /// 0.235 at 0.649 R, 0.12 at 0.8 R, 0 at R.
    /// </summary>
    public static float Attenuation(float distance, float radius)
    {
        if (radius <= 0 || distance >= radius) return 0;
        float x = Math.Clamp(distance / radius, 0, 1);
        if (x < 0.649f) return 0.2f + 0.8f * (1 - x) * (1 - x) * (1 - x);
        if (x < 0.8f) return 0.242f - 3 * (x - 0.6f) * (x - 0.6f);
        return 3 * (x - 1) * (x - 1);
    }

    /// <summary>
    /// Spot cone factor for <paramref name="cosAngle"/> = cos of the angle between the spot direction and the light-to-surface direction:
    /// <c>saturate(((cosAngle − cos(outer/2)) / (cos(inner/2) − cos(outer/2)))^falloff)</c> (Ogre's convention for the game's <c>spot</c>
    /// constant; Observed). The cones are put in order first (in the data <c>inner</c> can exceed <c>outer</c>; Meitou choice).
    /// </summary>
    public static float SpotFactor(float cosAngle, float innerAngle, float outerAngle, float falloff)
    {
        float inner = MathF.Min(innerAngle, outerAngle), outer = MathF.Max(innerAngle, outerAngle);
        float cosInner = MathF.Cos(inner * 0.5f), cosOuter = MathF.Cos(outer * 0.5f);
        if (cosAngle <= cosOuter) return 0;
        if (cosInner - cosOuter < 1e-5f) return 1;
        float t = Math.Clamp((cosAngle - cosOuter) / (cosInner - cosOuter), 0, 1);
        return Math.Clamp(MathF.Pow(t, falloff > 0 ? falloff : 1), 0, 1);
    }

    /// <summary>
    /// The light pass's per-frame factor on the power for <paramref name="effect"/> at time <paramref name="t"/> (the game's clock value × 60;
    /// its unit is Unknown): PULSE 0.5 + 0.5 sin 4t, SHIMMER 0.95 + 0.05 sin 30t · sin 17t, NONE and FLICKER 1 (FLICKER has no case there).
    /// </summary>
    public static float EffectFactor(WorldLightEffect effect, float t) => effect switch
    {
        WorldLightEffect.Pulse => 0.5f + 0.5f * MathF.Sin(4 * t),
        WorldLightEffect.Shimmer => 0.95f + 0.05f * MathF.Sin(30 * t) * MathF.Sin(17 * t),
        _ => 1,
    };

    /// <summary>
    /// The game's daylight factor at <paramref name="hour"/>: 0 outside [sunrise, sunset], 1 inside, ramping linearly over
    /// <see cref="DaylightRampHours"/> after sunrise and before sunset (Observed that the bounds are the CONSTANTS <c>sunrise</c>/<c>sunset</c>).
    /// </summary>
    public static float DaylightFactor(float hour, float sunrise, float sunset)
    {
        if (hour < sunrise || hour > sunset) return 0;
        if (hour < sunrise + DaylightRampHours) return (hour - sunrise) / DaylightRampHours;
        if (hour > sunset - DaylightRampHours) return (sunset - hour) / DaylightRampHours;
        return 1;
    }

    /// <summary>Whether the game draws <paramref name="light"/> at <paramref name="daylight"/> (<see cref="DaylightFactor"/>) with its building on or off.</summary>
    public static bool IsLit(WorldLight light, float daylight, bool buildingOn = true) =>
        light.Intensity > 0 && light.Rule switch
        {
            WorldLightRule.Night => daylight <= NightDaylightLimit,
            _ => buildingOn,
        };
}
