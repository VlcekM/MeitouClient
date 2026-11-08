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
    /// <summary>A completed player building's lights (placed as the renderer lights them; none by default).</summary>
    IReadOnlyList<WorldLight> SiteLights(Construction.BuildSite site) => [];
    /// <summary>BUILDING <c>enforce ceiling</c> (7 true): nobody on an upper floor of it is on top (<c>Character::threadedUpdatePeriodic</c>, <b>Verified</b>).</summary>
    bool EnforcesCeiling(TownBuilding building) => false;
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
/// Rooms are the registered towns' buildings only (ruins and the player's buildings are outdoors); the lamps of completed player buildings and the lanterns characters
/// carry (at their owner) light too.
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

    readonly record struct Room(string Key, InteriorVolume Volume, bool EnforcesCeiling);
    readonly record struct Lamp(WorldLight Light, int Room);

    readonly HashSet<string> added = new(StringComparer.Ordinal);
    // Each added building's room (-1: none) and lamps, and those a ruin took out of the cells (Remove).
    readonly Dictionary<string, (int Room, List<int> Lamps)> entries = new(StringComparer.Ordinal);
    readonly HashSet<string> hidden = new(StringComparer.Ordinal);
    readonly List<Room> rooms = [];
    readonly Dictionary<string, int> roomOf = new(StringComparer.Ordinal);
    readonly Dictionary<(int, int), List<int>> roomCells = [];
    readonly List<Lamp> lamps = [];
    readonly Dictionary<(int, int), List<int>> lampCells = [];
    // The completed player buildings' lamps, rebuilt when the building registry changes (outdoor lights, no room).
    readonly List<Lamp> siteLamps = [];
    readonly Dictionary<(int, int), List<int>> siteCells = [];
    long sitesRevision = -1;
    ISightSource? source;

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
        this.source ??= source;
        if (building.Destroyed || !added.Add(building.Key)) return;
        int room = -1;
        if (source.Room(building) is { } volume)
        {
            room = rooms.Count;
            rooms.Add(new Room(building.Key, volume, source.EnforcesCeiling(building)));
            roomOf[building.Key] = room;
            RoomCells(room, add: true);
        }
        var own = new List<int>();
        foreach (var l in source.Lights(building))
        {
            if (!(l.Intensity > 0) || !(l.Radius > 0)) continue;
            own.Add(lamps.Count);
            lamps.Add(new Lamp(l, l.Interior ? room : -1));
            LampCells(lamps.Count - 1, add: true);
        }
        entries[building.Key] = (room, own);
    }

    /// <summary>A registered building became a ruin (a town override's residents, docs/game/buildings-production.md "Ruined by residents"): its room and lamps go.</summary>
    public void Remove(string building)
    {
        if (!entries.TryGetValue(building, out var e) || !hidden.Add(building)) return;
        if (e.Room >= 0)
        {
            RoomCells(e.Room, add: false);
            roomOf.Remove(building);
        }
        foreach (int k in e.Lamps) LampCells(k, add: false);
    }

    /// <summary>
    /// A registered building laid out afresh (a town override's residents, <see cref="PopulationData.Relay"/>): its old room and lamps go and its new ones come, unless it
    /// is a ruin.
    /// </summary>
    public void Relay(ISightSource source, TownBuilding building)
    {
        if (!added.Contains(building.Key)) return;
        Remove(building.Key);
        added.Remove(building.Key);
        hidden.Remove(building.Key);
        entries.Remove(building.Key);
        Add(source, building);
    }

    /// <summary>A ruin stands again: the room and lamps it had come back, or are added now when it was a ruin when its town registered.</summary>
    public void Restore(ISightSource source, TownBuilding building)
    {
        if (!hidden.Remove(building.Key))
        {
            Add(source, building with { Destroyed = false, Resolved = building.Resolved is { } r ? r with { Destroyed = false } : null });
            return;
        }
        var e = entries[building.Key];
        if (e.Room >= 0)
        {
            RoomCells(e.Room, add: true);
            roomOf[building.Key] = e.Room;
        }
        foreach (int k in e.Lamps) LampCells(k, add: true);
    }

    void RoomCells(int room, bool add)
    {
        var footprint = rooms[room].Volume.Footprint;
        var min = new Vector2(footprint.Min(p => p.X), footprint.Min(p => p.Y));
        var max = new Vector2(footprint.Max(p => p.X), footprint.Max(p => p.Y));
        for (int x = CellOf(min.X); x <= CellOf(max.X); x++)
            for (int z = CellOf(min.Y); z <= CellOf(max.Y); z++)
                Toggle(roomCells, (x, z), room, add);
    }

    void LampCells(int lamp, bool add)
    {
        var l = lamps[lamp].Light;
        for (int x = CellOf(l.Position.X - l.Radius); x <= CellOf(l.Position.X + l.Radius); x++)
            for (int z = CellOf(l.Position.Z - l.Radius); z <= CellOf(l.Position.Z + l.Radius); z++)
                Toggle(lampCells, (x, z), lamp, add);
    }

    static void Toggle(Dictionary<(int, int), List<int>> cells, (int, int) key, int item, bool add)
    {
        if (add) Into(cells, key, item);
        else if (cells.TryGetValue(key, out var list)) list.Remove(item);
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
            int was = place.Length;
            Array.Resize(ref place, size);
            place.AsSpan(was).Fill(-1);
            Array.Resize(ref onTop, size);
            Array.Resize(ref light, size);
            Array.Resize(ref wanted, size);
            Array.Resize(ref floorOf, size);
            Array.Resize(ref probed, size);
            Array.Resize(ref low, size);
            Array.Resize(ref facing, size);
        }
        for (int s = 0; s < count; s++)
        {
            if (!world.Steps(s)) continue;   // the far cadence: a skipped character keeps its last place, posture and facing
            int was = place[s];
            place[s] = -1;
            low[s] = prev[s].Alive && table.Cold(s)?.Medical is { Prone: true };
            facing[s] = table.Cold(s)?.Animation is { HasDrawYaw: true } drawn ? drawn.DrawYaw : prev[s].Yaw;
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
                    // A building that enforces its ceiling has nobody on top above its ground floor; elsewhere the overhead query decides.
                    floorOf[s] = rooms[r].EnforcesCeiling ? rooms[r].Volume.FloorAt(at) : -1;
                    onTop[s] = !(rooms[r].EnforcesCeiling && floorOf[s] != 0) && !rooms[r].Volume.Covered(at);
                    probed[s] = key;
                }
            }
            else onTop[s] = false;
        }
        // A carried body is where its carrier is (the original's location of a carried character is its carrier's).
        for (int s = 0; s < count; s++)
            if (prev[s].Alive && table.Cold(s) is { CarriedBy.IsNone: false } c && table.TryResolveNext(c.CarriedBy, out int carrier) && carrier < count)
                (place[s], onTop[s]) = (place[carrier], onTop[carrier]);
        RefreshSites(world);
        // The lights characters carry (lanterns; docs/formats/lights.md "Carried lanterns"), at their owner, lit as their night flag allows.
        carried.Clear();
        for (int s = 0; s < count; s++)
            if (prev[s].Alive && table.Cold(s)?.Appearance is { Lights.Count: > 0 } look)
                foreach (var cl in look.Lights)
                    if (cl.Light.Intensity > 0 && cl.Light.Radius > 0 && WorldLights.IsLit(cl.Light, Daylight))
                        carried.Add((s, cl.Light, prev[s].Position + new Vector3(0, cl.Light.Position.Y, 0)));
        brightest = 0;
        for (int s = 0; s < count; s++)
        {
            light[s] = prev[s].Alive ? SampleLight(s, prev[s].Position, table.Cold(s) is { IsPlayer: true }) : 1;
            if (prev[s].Alive) brightest = MathF.Max(brightest, light[s]);
        }
    }

    // Each character's light request (Character +0xdc): raised by a sight test on its looker and target, and by the stat multiplier; the resample clears it (set from any worker, only ever to true).
    bool[] wanted = [];

    /// <summary>
    /// A character's light value for this tick (<c>FUN_1405ccd90</c>:53-60, <b>Verified</b>): every update first overwrites it with the bare daylight factor; only when its request was raised since
    /// (a sight test on it, as looker or target, or the stat multiplier) and it is player-controlled or the daylight is at or below the threshold is the request cleared and the light sampled in
    /// full (lamps, ray, indoor flag; <see cref="LightOf"/>). So a first test after a quiet period reads bare daylight.
    /// </summary>
    float SampleLight(int slot, Vector3 at, bool player)
    {
        if (!wanted[slot] || !player && Daylight >= NpcDaylight) return Daylight;
        wanted[slot] = false;
        return LightOf(slot, at, player);
    }

    void Request(int slot)
    {
        if ((uint)slot < (uint)wanted.Length) wanted[slot] = true;
    }

    float brightest = 1;

    /// <summary>Where a character's overhead and floor queries were last made (<see cref="floorOf"/> -1: not asked yet there).</summary>
    readonly record struct Probe(Vector3 At, int Room, int Generation);
    Probe[] probed = [];
    // Each character crawling or staying low at the last update (the sight test's ground posture).
    bool[] low = [];
    /// <summary>Each character's sight-cone forward: its drawn facing (<see cref="CharacterAnimation.DrawYaw"/>), else its facing.</summary>
    float[] facing = [];
    int[] floorOf = [];
    // The carried lights of this update: owner slot, light, position.
    readonly List<(int Owner, WorldLight Light, Vector3 At)> carried = [];

    /// <summary>The farthest the looker can see anyone this tick (<see cref="Range"/> with the brightest target and full ambient): a query radius.</summary>
    public float MaxRange(int looker, float outdoor)
    {
        bool top = Known(looker) && onTop[looker];
        float range = (Inside(looker) ? IndoorSight : outdoor) * (0.5f + 0.5f * Daylight) * (top ? 2 : 1);
        return range * Math.Clamp(MathF.Max(brightest, 1) - PeekLight(looker) + 1, 1, 2);
    }

    /// <summary>The room (<see cref="PlaceOf"/>'s numbering) a point lies in, -1 outdoors: an item's location as <c>FUN_14099b1d0</c> compares it with a character's.</summary>
    public int PlaceAt(Vector3 at) => RoomAt(at);

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
        var cell = (CellOf(at.X), CellOf(at.Z));
        bool lamped = lampCells.TryGetValue(cell, out var list), sited = siteCells.TryGetValue(cell, out var siteList);
        if (!lamped && !sited && carried.Count == 0) return sum;
        bool inside = Inside(slot);
        int floor = 0;
        if (inside && (floor = floorOf[slot]) < 0) floor = floorOf[slot] = rooms[place[slot]].Volume.FloorAt(at);
        foreach (int k in lamped ? list! : []) sum += Gain(lamps[k], slot, at, inside, floor);
        foreach (int k in sited ? siteList! : []) sum += Gain(siteLamps[k], slot, at, inside, floor);
        foreach (var (owner, l, p) in carried)
        {
            // Counted with the same indoor and floor rules as a building's light: the owner's place and floor stand in for the light's.
            if (Inside(owner) != inside) continue;
            if (inside && (place[owner] != place[slot] || FloorOf(owner, p) != floor)) continue;
            float d2 = Vector3.DistanceSquared(p, at), r2 = l.Radius * l.Radius;
            if (d2 <= r2) sum += LampGain * l.Intensity * (1 - d2 / r2);
        }
        return sum;
    }

    /// <summary>One lamp's share of a character's light (0 when it is off, of the other indoor state or floor, or out of reach).</summary>
    float Gain(Lamp lamp, int slot, Vector3 at, bool inside, int floor)
    {
        var (l, room) = lamp;
        if (!WorldLights.IsLit(l, l.Interior && InteriorLampsAllDay ? 0 : Daylight) || l.Interior != inside) return 0;
        // The original also matches the light's floor when both are inside; a lamp of another building's interior is taken as on another floor.
        if (inside && (room != place[slot] || l.Floor != floor)) return 0;
        float d2 = Vector3.DistanceSquared(l.Position, at), r2 = l.Radius * l.Radius;
        if (d2 > r2) return 0;
        float f = 1 - d2 / r2;
        if (l.Type == WorldLightType.Spot)
        {
            var to = at - l.Position;
            float len = to.Length();
            float cos = len > 1e-4f ? Vector3.Dot(to / len, l.Direction) : 1;
            float cosOuter = MathF.Cos(l.OuterAngle * 0.5f), cosInner = MathF.Cos(l.InnerAngle * 0.5f);
            f *= Math.Clamp(cosInner != cosOuter ? (cos - cosOuter) / (cosInner - cosOuter) : cos >= cosOuter ? 1 : 0, 0, 1);
        }
        return LampGain * l.Intensity * f;
    }

    /// <summary>Rebuilds the completed player buildings' lamps when the building registry changed (a level-placed building bought is a town building, already counted).</summary>
    void RefreshSites(World world)
    {
        if (source is null || world.Buildings.Revision == sitesRevision) return;
        sitesRevision = world.Buildings.Revision;
        siteLamps.Clear();
        siteCells.Clear();
        foreach (var site in world.Buildings.All)
        {
            if (!site.Complete || site.Destroyed || site.LevelPlacement is not null) continue;
            foreach (var l in source.SiteLights(site))
            {
                if (!(l.Intensity > 0) || !(l.Radius > 0)) continue;
                int lamp = siteLamps.Count;
                siteLamps.Add(new Lamp(l, -1));
                for (int x = CellOf(l.Position.X - l.Radius); x <= CellOf(l.Position.X + l.Radius); x++)
                    for (int z = CellOf(l.Position.Z - l.Radius); z <= CellOf(l.Position.Z + l.Radius); z++)
                        Into(siteCells, (x, z), lamp);
            }
        }
    }

    bool Known(int slot) => (uint)slot < (uint)count;

    /// <summary>In a building's indoor volume with something over it: the original's indoor sight and indoor lights.</summary>
    public bool Inside(int slot) => Known(slot) && place[slot] >= 0 && !onTop[slot];

    /// <summary>In no building at all, neither inside nor on top (the original's location type 0xb, outdoors).</summary>
    public bool Outdoors(int slot) => !Known(slot) || place[slot] < 0;

    /// <summary>
    /// The floor the character at <paramref name="at"/> (its last-tick position) stands on in its building (the original's <c>getFloor</c>); 0 outdoors or on top. Worked out on first
    /// ask after the character moved and kept; the parallel looks ask it too, and any worker writes the same value.
    /// </summary>
    public int FloorOf(int slot, Vector3 at)
    {
        if (!Inside(slot)) return 0;
        if (floorOf[slot] < 0) floorOf[slot] = rooms[place[slot]].Volume.FloorAt(at);
        return floorOf[slot];
    }

    /// <summary>The floor of <paramref name="building"/> at <paramref name="at"/> (an object's <c>getFloor</c>); 0 for a building the map does not hold.</summary>
    public int FloorAt(string building, Vector3 at) => roomOf.TryGetValue(building, out int r) ? rooms[r].Volume.FloorAt(at) : 0;

    /// <summary>Whether the map holds an indoor volume for <paramref name="building"/> (the key of a fixture's building): its <c>isIndoors</c> handle is then that building, else none.</summary>
    public bool HasRoom(string building) => roomOf.ContainsKey(building);

    /// <summary>The <see cref="TownBuilding.Key"/> of the building the character is <see cref="Inside"/>; null outdoors or on top.</summary>
    public string? BuildingOf(int slot) => Inside(slot) ? rooms[place[slot]].Key : null;

    /// <summary>The key of the building a place (<see cref="PlaceOf"/>) is in.</summary>
    public string PlaceKey(int place) => rooms[place].Key;

    /// <summary>The indoor volume (room index) the character stands in, -1 outdoors or on top of a building.</summary>
    public int PlaceOf(int slot) => Inside(slot) ? place[slot] : -1;

    /// <summary>
    /// The building the character is in, on top of it too (room index, -1 outdoors): the original's location (Character slot 59), which standing on top does not change
    /// (the on-top flag is slot 60, read by the sight test only). The medic finder compares it (docs/game/ai-tasks.md "Medic jobs").
    /// </summary>
    public int LocationOf(int slot) => Known(slot) ? place[slot] : -1;

    /// <summary>The light level of the character (1 in daylight; see the class remarks).</summary>
    public float Light(int slot)
    {
        Request(slot);
        return PeekLight(slot);
    }

    /// <summary>The light level without raising the character's light request (the HUD and the query bound read it, no sight test or stat multiplier does).</summary>
    public float PeekLight(int slot) => Known(slot) ? light[slot] : 1;

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
    /// The whole sight test of a looker at <paramref name="eye"/>, its cone along its drawn facing as the schedule phase found it (FUN_140852f70 reads the drawn node), on the character in <paramref name="target"/> at <paramref name="at"/>: the view
    /// factor (<see cref="AggroSystem.Visibility"/>, or <see cref="AggroSystem.SneakVisibility"/> with the target's <paramref name="stealth"/> when it sneaks) within <see cref="Range"/>.
    /// </summary>
    public float Sees(int looker, int target, Vector3 eye, Vector3 at, float outdoor, float stealth = -1)
    {
        float range = Range(looker, target, outdoor);
        if (!(range > 0)) return 0;
        // A target crawling or staying low is half as visible (FUN_140852f70: ground posture x0.5, before the sneaking factors).
        float posture = (uint)target < (uint)count && low[target] ? 0.5f : 1;
        float yaw = (uint)looker < (uint)count ? facing[looker] : 0;   // a looker made this tick has no facing captured yet: yaw 0 until the next schedule phase
        return stealth >= 0 ? AggroSystem.SneakVisibility(eye, yaw, at, range, stealth, posture) : AggroSystem.Visibility(eye, yaw, at, range, posture);
    }
}
