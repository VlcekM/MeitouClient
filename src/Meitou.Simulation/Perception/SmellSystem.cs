using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Perception;

/// <summary>
/// One zone's blood smell (<c>ZoneSmell</c> channel 0, docs/game/perception.md "Blood smell"): the current value and where its sources are.
/// </summary>
public readonly record struct ZoneBlood(float Value, Vector2 Position);

/// <summary>
/// The blood smell of the zones (<c>ZoneSmell</c>, <b>Verified</b> decompile) and the blood hunters that follow it (goal 176 HUNTING_BLOODSMELL,
/// <c>Task_HuntingSmell</c>). Serial, in <c>SlowWorld</c> every <see cref="EveryTicks"/> ticks, the zones stepping once per <see cref="StepSeconds"/>
/// with the elapsed time: each zone's target is its corpses with flies (1 each; the bleeders the original posts are skipped by its accumulation); its
/// position is the corpses' centroid, or (0, 0) without corpses (the original's zero vector); the value rises toward the target at 1/60 a game second
/// and falls at 1/240. A zone not loaded is dropped once its target is 0 (<c>FUN_1408f85f0</c>). An animal whose squad's package lists 176 hunts the
/// zone with the best weighted value (its own x1, a neighbour x0.8, any other x0.5) above its race's ANIMAL_CHARACTER <c>smell blood</c>, keeps it
/// while the value stays at the threshold, and runs to its position. Engine choices: every loaded zone is a candidate (the original's active zone
/// island); all zones step together, the original one zone per kick in a cycle.
/// </summary>
public sealed class SmellSystem(PopulationData data, PopulationSystem? population) : ITickSystem, IStateHashed
{
    #region Zone smell

    /// <summary>
    /// The ticks between the system's runs.
    /// </summary>
    public const int EveryTicks = 30;

    /// <summary>
    /// The blood channel's rates per game second (<b>Verified</b>: rise 1/60, decay 1/240).
    /// </summary>
    public const float Rise = 1f / 60, Decay = 1f / 240;

    /// <summary>
    /// The weights of a neighbouring and any other zone in the hunters' pick (<b>Verified</b> literals).
    /// </summary>
    public const float NeighbourWeight = 0.8f, FarWeight = 0.5f;

    /// <summary>
    /// Game seconds between the zone steps (Verified literal 10).
    /// </summary>
    public const float StepSeconds = 10;

    /// <summary>
    /// Per zone key: the blood smell's value and its sources' position.
    /// </summary>
    readonly SortedDictionary<int, (float Value, Vector2 Position)> _zones = [];
    /// <summary>
    /// The time gathered towards the next zone step, in game seconds. Not saved.
    /// </summary>
    float _elapsed;

    /// <summary>
    /// A zone's smell (none: 0).
    /// </summary>
    public ZoneBlood Blood(ZoneCoordinate zone) => _zones.TryGetValue(Key(zone), out var z) ? new ZoneBlood(z.Value, z.Position) : default;

    /// <summary>
    /// The zone's key: row x zone count + column.
    /// </summary>
    static int Key(ZoneCoordinate z) => z.Y * WorldLayout.ZoneCount + z.X;

    /// <summary>
    /// Every <see cref="EveryTicks"/>: the zone step once <see cref="StepSeconds"/> have gathered, then the hunters.
    /// </summary>
    public void SlowWorld(World world)
    {
        if (world.Tick % EveryTicks != 0) return;

        // The sources step once per 10 game seconds with the whole time since the last step as dt (FUN_1408f85f0: its timer runs to DAT_14167fad4 =
        // 10 and the step takes the frozen value).
        _elapsed += EveryTicks * world.TickSeconds;
        if (_elapsed >= StepSeconds)
        {
            Update(world, _elapsed);
            _elapsed = 0;
        }

        if (population is not null) Hunt(world);
    }

    /// <summary>
    /// One zone step over <paramref name="dt"/> game seconds: each zone's value toward its corpses' count, its position at their centroid; a zone
    /// with target 0 is dropped once its value is 0 or it is not loaded.
    /// </summary>
    void Update(World world, float dt)
    {
        var sums = CorpseSums(world);
        foreach (int key in _zones.Keys.Union(sums.Keys).ToList())
        {
            var (value, _) = _zones.GetValueOrDefault(key);
            var a = sums.GetValueOrDefault(key);
            float target = a.Corpses;
            var position = a.Corpses > 0 ? a.CorpseSum / a.Corpses : Vector2.Zero;
            value = value < target ? MathF.Min(target, value + dt * Rise) : MathF.Max(target, value - dt * Decay);
            bool unloaded = world.LoadedZones is { } loaded
                && !loaded.Contains(new ZoneCoordinate(key % WorldLayout.ZoneCount, key / WorldLayout.ZoneCount));
            if (target <= 0 && (value <= 0 || unloaded))
                _zones.Remove(key);
            else
                _zones[key] = (value, position);
        }
    }

    /// <summary>
    /// The dead characters with flies, counted and summed by the zone key they lie in.
    /// </summary>
    static Dictionary<int, (int Corpses, Vector2 CorpseSum)> CorpseSums(World world)
    {
        var table = world.Characters;
        var sums = new Dictionary<int, (int Corpses, Vector2 CorpseSum)>();
        for (int s = 0; s < table.HighWater; s++)
        {
            if (!table.Next[s].Alive || table.Cold(s) is not { Medical: { Dead: true } } c || !c.HasFlies) continue;

            var p = table.Next[s].Position;
            var zone = WorldLayout.ZoneOf(p.X, p.Z);
            if (zone.X is < 0 or >= WorldLayout.ZoneCount || zone.Y is < 0 or >= WorldLayout.ZoneCount) continue;

            int key = Key(zone);
            var a = sums.GetValueOrDefault(key);
            var at = new Vector2(p.X, p.Z);
            sums[key] = (a.Corpses + 1, a.CorpseSum + at);
        }

        return sums;
    }

    #endregion

    #region Hunters

    /// <summary>
    /// Within this of the smell's position a hunter stops walking (engine choice; the original keeps walking at the moving centroid).
    /// </summary>
    const float HuntArrive = 50;

    /// <summary>
    /// The zone each hunting squad keeps (Blackboard +0xf8), by squad id.
    /// </summary>
    readonly SortedDictionary<int, int> _hunts = [];
    /// <summary>
    /// The <c>smell blood</c> threshold per record id, read once.
    /// </summary>
    readonly Dictionary<string, float> _thresholds = new(StringComparer.Ordinal);

    /// <summary>
    /// ANIMAL_CHARACTER <c>smell blood</c> of a character's record (<c>CharacterAnimal +0x6e8</c>); 0 when it has none.
    /// </summary>
    float Threshold(CharacterCold c)
    {
        if (_thresholds.TryGetValue(c.RecordId, out float t)) return t;

        t = data.Db.Find(c.RecordId)?.GetFloat("smell blood") ?? 0;
        _thresholds[c.RecordId] = t;
        return t;
    }

    /// <summary>
    /// The blood hunters: each animal-led squad whose package lists goal 176 keeps or picks its zone and runs to its smell.
    /// </summary>
    void Hunt(World world)
    {
        var table = world.Characters;
        foreach (var squad in world.Squads.All)
        {
            if (squad.Ai is null || squad.Members.Count == 0 || !table.TryResolveNext(squad.Leader, out int lead)
                || table.Cold(lead) is not { Fighter.IsAnimal: true } leader)
                continue;

            int role = (int)AiSystem.RoleOf(squad, squad.Leader, leader);
            bool listed = population!.GoalDuty(world, squad, (AiRole)role, TaskType.HuntingBloodsmell);
            float threshold = Threshold(leader);
            int zone = listed && threshold > 0 ? PickZone(world, squad, lead, threshold) : -1;
            if (zone < 0)
            {
                if (_hunts.Remove(squad.Id)) Release(world, squad);

                continue;
            }

            _hunts[squad.Id] = zone;
            var to = _zones[zone].Position;
            foreach (var member in squad.Members)
                HuntWith(world, member, to);
        }
    }

    /// <summary>
    /// The zone a squad hunts: the one it keeps while its smell holds, else the best weighted one above the threshold; -1 for none.
    /// </summary>
    int PickZone(World world, Squad squad, int lead, float threshold)
    {
        if (_hunts.TryGetValue(squad.Id, out int kept) && _zones.TryGetValue(kept, out var k) && k.Value >= threshold)
            return kept;

        var here = WorldLayout.ZoneOf(world.Characters.Next[lead].Position.X, world.Characters.Next[lead].Position.Z);
        int zone = -1;
        float best = threshold;
        foreach (var (key, z) in _zones)
        {
            int dx = Math.Abs(key % WorldLayout.ZoneCount - here.X);
            int dy = Math.Abs(key / WorldLayout.ZoneCount - here.Y);
            float w = z.Value * (dx == 0 && dy == 0 ? 1 : dx <= 1 && dy <= 1 ? NeighbourWeight : FarWeight);
            if (w > best) (best, zone) = (w, key);
        }

        return zone;
    }

    /// <summary>
    /// Sends one squad member that is free to hunt to the smell's position, when it is not there yet.
    /// </summary>
    static void HuntWith(World world, CharacterId member, Vector2 to)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(member, out int s) || table.Cold(s) is not { } c || c.InCombat || c.Fleeing || c.Medical is { Incapacitated: true }
            || !c.CarriedBy.IsNone)
            return;

        if (!c.Engaged && !AiSystem.Holds(table.Next[s], c)) return;

        c.Engaged = true;
        ref var n = ref table.Next[s];
        bool far = Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), to) > HuntArrive;
        bool goingThere = n.Task == (byte)CharacterTask.GoTo && !(Vector2.Distance(n.Goal, to) > HuntArrive);
        if (far && !goingThere)
            TaskStart.Walk(world, s, to, SpeedMode.Run, TaskType.HuntingBloodsmell, TaskSource.Goal);
    }

    /// <summary>
    /// Lets the squad's members' AI take them again.
    /// </summary>
    static void Release(World world, Squad squad)
    {
        foreach (var member in squad.Members)
        {
            if (world.Characters.TryResolveNext(member, out int s) && world.Characters.Cold(s) is { Engaged: true } c)
                c.Engaged = false;
        }
    }

    #endregion

    /// <summary>
    /// Adds the zones and the hunts to the world hash, only while there are any.
    /// </summary>
    public void Hash(ref StateHasher hasher)
    {
        if (_zones.Count == 0 && _hunts.Count == 0) return;

        hasher.Add(0x536D656C);
        foreach (var (k, z) in _zones)
        {
            hasher.Add(k);
            hasher.Add(z.Value);
            hasher.Add(z.Position);
        }

        foreach (var (s, z) in _hunts)
        {
            hasher.Add(s);
            hasher.Add(z);
        }

        hasher.Add(_elapsed);
    }
}
