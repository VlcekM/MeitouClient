using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Simulation;

/// <summary>The indoor volume and the lights of a town's building, from the level data (the host's; null on <see cref="PopulationData"/>: everyone stays outdoors and unlit).</summary>
public interface ISightSource
{
    /// <summary>The building's indoor volume (<see cref="InteriorVolume.For"/>), or null when it has none.</summary>
    InteriorVolume? Room(TownBuilding building);
    /// <summary>The building's lights, its interior and exterior layouts' included (<see cref="WorldLights.ForBuilding"/>).</summary>
    IReadOnlyList<WorldLight> Lights(TownBuilding building);
}

/// <summary>
/// Where each character is for sight and how lit it is (docs/game/perception.md "Sight"): the original keeps both on the character (its location handle and light level <c>+0xd8</c>);
/// here they are worked out in the schedule phase of every tick from the last tick's positions and read by every sight test (<see cref="Sees"/>). Derived, not state.
/// <list type="bullet">
/// <item><b>Place</b>: the building whose indoor volume holds the character (a carried one takes its carrier's, a caged or bedded one its fixture's building), else outdoors. Standing in a
/// building with nothing over it (a roof, a roofless part: the original's overhead query <c>0x8079e00</c>) is <b>on top</b>.</item>
/// <item><b>Light</b> (<c>FUN_140a0af10</c>): 1 while the daylight factor is above 0.5, else 2 x daylight plus the lit lamps within their radius: <c>1.5 x power x (1 - d^2 / r^2)</c>
/// (x the spot cone) for a lamp of the character's own indoor state, and on its floor when both are inside; interior lamps lit at any daylight with <see cref="InteriorLampsAllDay"/>.
/// An NPC keeps the bare daylight factor while it is 0.75 or more.</item>
/// </list>
/// Only the registered towns' buildings count (ruins and the player's buildings are outdoors); a character's carried lights are not counted.
/// </summary>
public sealed class SightMap
{
    /// <summary>The original's sight inside a building (<c>FUN_140851bc0</c>, Verified literal; no race multiplier).</summary>
    public const float IndoorSight = 500;
    /// <summary>The light sampler's daylight cut: above it everyone is fully lit (Verified literal).</summary>
    public const float LitDaylight = 0.5f;
    /// <summary>An NPC's light is the bare daylight factor from this up (the update's gate, Verified literal).</summary>
    public const float NpcDaylight = 0.75f;
    /// <summary>A lamp's contribution factor (Verified literal).</summary>
    public const float LampGain = 1.5f;
    /// <summary>The ambient light's floor in the sight test: a looker inside a building, outside one (Verified literals).</summary>
    public const float InsideFloor = 0.2f, OutsideFloor = 0.4f;

    const float Cell = 500;

    readonly record struct Room(string Key, InteriorVolume Volume);
    readonly record struct Lamp(WorldLight Light, int Room);

    readonly HashSet<string> added = new(StringComparer.Ordinal);
    readonly List<Room> rooms = [];
    readonly Dictionary<string, int> roomOf = new(StringComparer.Ordinal);
    readonly Dictionary<(int, int), List<int>> roomCells = [];
    readonly List<Lamp> lamps = [];
    readonly Dictionary<(int, int), List<int>> lampCells = [];

    int[] place = [];
    bool[] onTop = [];
    float[] light = [];
    int count;

    /// <summary>
    /// The <c>interiorlamps</c> switch's Meitou side, as the renderer's <c>WorldLamps.InteriorAllDay</c>: interior lamps count at every daylight (the game: night-only ones at
    /// daylight 0.1 or less), so a lamp that is drawn on also lights. Set by the host.
    /// </summary>
    public bool InteriorLampsAllDay { get; set; } = true;

    /// <summary>The daylight factor of this tick (<see cref="WorldLights.DaylightFactor"/>).</summary>
    public float Daylight { get; private set; } = 1;

    /// <summary>Adds a town building's room and lamps once (when its town registers, <see cref="Locks.FixtureRegistry.RegisterSite"/>).</summary>
    public void Add(ISightSource source, TownBuilding building)
    {
        if (building.Destroyed || !added.Add(building.Key)) return;
        int room = -1;
        if (!building.Destroyed && source.Room(building) is { } volume)
        {
            room = rooms.Count;
            rooms.Add(new Room(building.Key, volume));
            roomOf[building.Key] = room;
            var min = new Vector2(volume.Footprint.Min(p => p.X), volume.Footprint.Min(p => p.Y));
            var max = new Vector2(volume.Footprint.Max(p => p.X), volume.Footprint.Max(p => p.Y));
            for (int x = CellOf(min.X); x <= CellOf(max.X); x++)
                for (int z = CellOf(min.Y); z <= CellOf(max.Y); z++)
                    Into(roomCells, (x, z), room);
        }
        foreach (var l in source.Lights(building))
        {
            if (!(l.Intensity > 0) || !(l.Radius > 0)) continue;
            int lamp = lamps.Count;
            lamps.Add(new Lamp(l, l.Interior ? room : -1));
            for (int x = CellOf(l.Position.X - l.Radius); x <= CellOf(l.Position.X + l.Radius); x++)
                for (int z = CellOf(l.Position.Z - l.Radius); z <= CellOf(l.Position.Z + l.Radius); z++)
                    Into(lampCells, (x, z), lamp);
        }
    }

    static int CellOf(float v) => (int)MathF.Floor(v / Cell);

    static void Into(Dictionary<(int, int), List<int>> cells, (int, int) key, int item)
    {
        if (!cells.TryGetValue(key, out var list)) cells[key] = list = [];
        list.Add(item);
    }

    /// <summary>Works out every character's place and light from the last tick's positions (the schedule phase, serial).</summary>
    public void Update(World world)
    {
        Daylight = WorldLights.DaylightFactor((float)(world.GameHours % 24), world.Settings.Sunrise, world.Settings.Sunset);
        var table = world.Characters;
        var prev = table.Previous;
        count = table.HighWater;
        if (place.Length < count)
        {
            int size = Math.Max(count, place.Length * 2);
            Array.Resize(ref place, size);
            Array.Resize(ref onTop, size);
            Array.Resize(ref light, size);
            Array.Resize(ref floorOf, size);
            Array.Resize(ref probed, size);
        }
        for (int s = 0; s < count; s++)
        {
            int was = place[s];
            place[s] = -1;
            if (!prev[s].Alive || rooms.Count == 0) { onTop[s] = false; probed[s] = default; continue; }
            var c = table.Cold(s);
            var at = prev[s].Position;
            if (c?.InWhat is { } held && world.Fixtures.Find(held) is { } fixture)
            {
                place[s] = roomOf.GetValueOrDefault(fixture.Building, -1);
                onTop[s] = false;
                if (place[s] != was) { floorOf[s] = -1; probed[s] = default; }
            }
            else if (RoomAt(at) is var r and >= 0)
            {
                place[s] = r;
                // The overhead and floor queries are triangle searches: kept while the character stays put in the same room.
                var key = new Probe(at, r, prev[s].Generation);
                if (r != was || probed[s] != key)
                {
                    onTop[s] = !rooms[r].Volume.Covered(at);
                    floorOf[s] = -1;
                    probed[s] = key;
                }
            }
            else onTop[s] = false;
        }
        // A carried body is where its carrier is (the original's location of a carried character is its carrier's).
        for (int s = 0; s < count; s++)
            if (prev[s].Alive && table.Cold(s) is { CarriedBy.IsNone: false } c && table.TryResolveNext(c.CarriedBy, out int carrier) && carrier < count)
                (place[s], onTop[s]) = (place[carrier], onTop[carrier]);
        brightest = 0;
        for (int s = 0; s < count; s++)
        {
            light[s] = prev[s].Alive ? LightOf(s, prev[s].Position, table.Cold(s) is { IsPlayer: true }) : 1;
            if (prev[s].Alive) brightest = MathF.Max(brightest, light[s]);
        }
    }

    float brightest = 1;

    /// <summary>Where a character's overhead and floor queries were last made (<see cref="floorOf"/> -1: not asked yet there).</summary>
    readonly record struct Probe(Vector3 At, int Room, int Generation);
    Probe[] probed = [];
    int[] floorOf = [];

    /// <summary>The farthest the looker can see anyone this tick (<see cref="Range"/> with the brightest target and full ambient): a query radius.</summary>
    public float MaxRange(int looker, float outdoor)
    {
        bool top = Known(looker) && onTop[looker];
        float range = (Inside(looker) ? IndoorSight : outdoor) * (0.5f + 0.5f * Daylight) * (top ? 2 : 1);
        return range * Math.Clamp(MathF.Max(brightest, 1) - Light(looker) + 1, 1, 2);
    }

    int RoomAt(Vector3 at)
    {
        if (!roomCells.TryGetValue((CellOf(at.X), CellOf(at.Z)), out var list)) return -1;
        foreach (int r in list)
            if (rooms[r].Volume.Contains(at)) return r;
        return -1;
    }

    float LightOf(int slot, Vector3 at, bool player)
    {
        if (!player && Daylight >= NpcDaylight) return Daylight;
        if (Daylight > LitDaylight) return 1;
        float sum = 2 * Daylight;
        if (!lampCells.TryGetValue((CellOf(at.X), CellOf(at.Z)), out var list)) return sum;
        bool inside = Inside(slot);
        int floor = 0;
        if (inside && (floor = floorOf[slot]) < 0) floor = floorOf[slot] = rooms[place[slot]].Volume.FloorAt(at);
        foreach (int k in list)
        {
            var (l, room) = lamps[k];
            if (!WorldLights.IsLit(l, l.Interior && InteriorLampsAllDay ? 0 : Daylight) || l.Interior != inside) continue;
            // The original also matches the light's floor when both are inside; a lamp of another building's interior is taken as on another floor.
            if (inside && (room != place[slot] || l.Floor != floor)) continue;
            float d2 = Vector3.DistanceSquared(l.Position, at), r2 = l.Radius * l.Radius;
            if (d2 > r2) continue;
            float f = 1 - d2 / r2;
            if (l.Type == WorldLightType.Spot)
            {
                var to = at - l.Position;
                float len = to.Length();
                float cos = len > 1e-4f ? Vector3.Dot(to / len, l.Direction) : 1;
                float cosOuter = MathF.Cos(l.OuterAngle * 0.5f), cosInner = MathF.Cos(l.InnerAngle * 0.5f);
                f *= Math.Clamp(cosInner != cosOuter ? (cos - cosOuter) / (cosInner - cosOuter) : cos >= cosOuter ? 1 : 0, 0, 1);
            }
            sum += LampGain * l.Intensity * f;
        }
        return sum;
    }

    bool Known(int slot) => (uint)slot < (uint)count;

    /// <summary>In a building's indoor volume with something over it: the original's indoor sight and indoor lights.</summary>
    public bool Inside(int slot) => Known(slot) && place[slot] >= 0 && !onTop[slot];

    /// <summary>In no building at all, neither inside nor on top (the original's location type 0xb, outdoors).</summary>
    public bool Outdoors(int slot) => !Known(slot) || place[slot] < 0;

    /// <summary>The <see cref="TownBuilding.Key"/> of the building the character is <see cref="Inside"/>; null outdoors or on top.</summary>
    public string? BuildingOf(int slot) => Inside(slot) ? rooms[place[slot]].Key : null;

    /// <summary>The light level of the character (1 in daylight; see the class remarks).</summary>
    public float Light(int slot) => Known(slot) ? light[slot] : 1;

    /// <summary>
    /// The range a looker sees <paramref name="target"/> to before the view cone (<c>FUN_140857b10</c>, <c>FUN_140852f70</c>, <c>FUN_140851bc0</c>): 0 when they are in different places
    /// (outdoors and inside a building, or two buildings) and neither stands on top; else 500 inside a building or <paramref name="outdoor"/> (the looker's outdoor sight, race multiplier
    /// applied), twice that on top, times lerp(daylight, 0.5, 1); times lerp(ambient, 0.2 inside or 0.4 outside, 1) and the contrast clamp(target light - looker light + 1, 0, 2),
    /// ambient = clamp(mean of both lights, 0, 1).
    /// </summary>
    public float Range(int looker, int target, float outdoor)
    {
        int lp = Known(looker) ? place[looker] : -1, tp = Known(target) ? place[target] : -1;
        bool lTop = Known(looker) && onTop[looker], tTop = Known(target) && onTop[target];
        if (lp != tp && !lTop && !tTop) return 0;
        float range = Inside(looker) ? IndoorSight : outdoor;
        range *= 0.5f + 0.5f * Daylight;
        if (lTop) range *= 2;
        float ll = Light(looker), tl = Light(target);
        float contrast = Math.Clamp(tl - ll + 1, 0, 2);
        float ambient = Math.Clamp((tl + ll) * 0.5f, 0, 1);
        float floor = lp >= 0 ? InsideFloor : OutsideFloor;
        return range * (floor + (1 - floor) * ambient) * contrast;
    }

    /// <summary>
    /// The whole sight test of a looker at <paramref name="eye"/> facing <paramref name="yaw"/> on the character in <paramref name="target"/> at <paramref name="at"/>: the view
    /// factor (<see cref="AggroSystem.Visibility"/>, or <see cref="AggroSystem.SneakVisibility"/> with the target's <paramref name="stealth"/> when it sneaks) within <see cref="Range"/>.
    /// </summary>
    public float Sees(int looker, int target, Vector3 eye, float yaw, Vector3 at, float outdoor, float stealth = -1)
    {
        float range = Range(looker, target, outdoor);
        if (!(range > 0)) return 0;
        return stealth >= 0 ? AggroSystem.SneakVisibility(eye, yaw, at, range, stealth) : AggroSystem.Visibility(eye, yaw, at, range);
    }
}
