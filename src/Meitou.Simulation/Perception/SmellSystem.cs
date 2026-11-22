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
/// and falls at 1/240. A zone not loaded is dropped once its target is 0 (<c>FUN_1408f85f0</c>). An animal-led squad whose leader weighs goal 176
/// keeps the zone with the best weighted value (its own x1, a neighbour x0.8, any other x0.5) above the leader's ANIMAL_CHARACTER
/// <c>smell blood</c> while the value stays at the threshold; goal 176 is an engine goal of the chooser (<see cref="BloodHunt"/>, wanted while the
/// squad keeps a zone), whose act-out runs to the zone's position. Engine choices: every loaded zone is a candidate (the original's active zone
/// island); all zones step together, the original one zone per kick in a cycle.
/// </summary>
public sealed class SmellSystem(PopulationData data, PopulationSystem? population) : ITickSystem, IStateHashed, IDutyGoalOwner
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
        if (!world.Beat(EveryTicks, TickPhases.Smell)) return;

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
    /// The zone each hunting squad keeps (the squad data's <c>+0xf8</c>, <c>FUN_140507070</c>), by squad id.
    /// </summary>
    readonly SortedDictionary<int, int> _hunts = [];
    /// <summary>
    /// The <c>smell blood</c> threshold per record id, read once (the serial zone step fills it).
    /// </summary>
    readonly Dictionary<string, float> _thresholds = new(StringComparer.Ordinal);
    /// <summary>
    /// The characters goal 176 holds (its act-out, <see cref="BloodHunt"/>), by slot: added at the start, removed at the end.
    /// </summary>
    readonly SortedDictionary<int, CharacterId> _hunters = [];
    /// <summary>
    /// The engine goals of the world (the AI's), resolved at the first hunters' step.
    /// </summary>
    DutyGoals? _duties;

    /// <summary>
    /// Registers goal 176 HUNTING_BLOODSMELL with the chooser (<see cref="BloodHunt"/>): its row's own act-out, no other row.
    /// </summary>
    void IDutyGoalOwner.RegisterGoals(DutyGoals goals) => goals.Register(TaskType.HuntingBloodsmell, new BloodHunt(this));

    /// <summary>
    /// ANIMAL_CHARACTER <c>smell blood</c> of a character's record (<c>CharacterAnimal +0x6e8</c>, the AI's vfunc <c>0x430</c>); 0 when it has none.
    /// </summary>
    float Threshold(CharacterCold c)
    {
        if (_thresholds.TryGetValue(c.RecordId, out float t)) return t;

        t = data.Db.Find(c.RecordId)?.GetFloat("smell blood") ?? 0;
        _thresholds[c.RecordId] = t;
        return t;
    }

    /// <summary>
    /// The hunt's serial part: the zones the squads keep (<see cref="KeepZones"/>), then the walks of the hunters (<see cref="StepHunters"/>).
    /// </summary>
    void Hunt(World world)
    {
        KeepZones(world);
        if (_hunters.Count > 0) StepHunters(world);
    }

    /// <summary>
    /// The zone each animal-led squad keeps for goal 176 (the desire <c>FUN_140794b60</c> keeps the squad data's zone while its value is at least the
    /// asker's <c>smell blood</c>, else picks anew, <b>Verified</b>), for a squad whose leader weighs the goal now (the goal is in the Leader lists
    /// only, docs/game/perception.md "Blood smell"); the leader's threshold stands for the asker's. The original's desire writes the zone; here this
    /// serial step does, every <see cref="EveryTicks"/>, and the think's desire reads it (<see cref="BloodHunt.Desire"/>).
    /// </summary>
    void KeepZones(World world)
    {
        var table = world.Characters;
        foreach (var squad in world.Squads.All)
        {
            int zone = -1;
            if (squad.Ai is not null && table.TryResolveNext(squad.Leader, out int lead) && table.Cold(lead) is { Fighter.IsAnimal: true } leader
                && AiSystem.WeighsGoal(world, lead, TaskType.HuntingBloodsmell) && Threshold(leader) is > 0 and var threshold)
                zone = PickZone(world, squad, lead, threshold);

            if (zone < 0) _hunts.Remove(squad.Id);
            else _hunts[squad.Id] = zone;
        }

        foreach (int gone in _hunts.Keys.Where(id => world.Squads.Find(id) is null).ToList())
            _hunts.Remove(gone);
    }

    /// <summary>
    /// Each hunter the goal still holds walks on towards its squad's zone as the smell's position moves (<c>Task_HuntingSmell</c> runs to it, movement
    /// mode 2); one the goal no longer holds is left to the AI's sweep, which ends it.
    /// </summary>
    void StepHunters(World world)
    {
        _duties ??= world.SystemOf<AiSystem>()?.Duties;
        foreach (var (slot, _) in _hunters)
        {
            if (_duties?.Runs(world, slot, TaskType.HuntingBloodsmell) != true || world.Characters.Cold(slot)?.Ai is not { } mind) continue;
            if (_hunts.TryGetValue(mind.Squad.Id, out int zone) && _zones.TryGetValue(zone, out var z)) RunTo(world, slot, z.Position);
        }
    }

    /// <summary>
    /// Sends the hunter running to the smell's position (row 176, a goal's task), unless it is there already or going there.
    /// </summary>
    static void RunTo(World world, int slot, Vector2 to)
    {
        ref var n = ref world.Characters.Next[slot];
        bool far = Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), to) > HuntArrive;
        bool goingThere = n.Task == (byte)CharacterTask.GoTo && !(Vector2.Distance(n.Goal, to) > HuntArrive);
        if (far && !goingThere)
            TaskStart.Walk(world, slot, to, SpeedMode.Run, TaskType.HuntingBloodsmell, TaskSource.Goal);
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
    /// Goal 176 HUNTING_BLOODSMELL as the chooser weighs it (<see cref="IDutyGoal"/>; tier and weight from the package's AI_TASK record, no finder,
    /// no commitment): its desire, its start and its end.
    /// </summary>
    sealed class BloodHunt(SmellSystem smell) : IDutyGoal
    {
        /// <summary>
        /// The desire <c>FUN_140794b60</c> (<b>Verified</b>): 0 for a character that is not an animal (vfunc <c>0x248</c>), else 1
        /// (<c>DAT_14167c308</c>) while its squad keeps a zone (<see cref="KeepZones"/>), else 0. Parallel: reads the asker's own cold state and the
        /// zones only the serial step writes.
        /// </summary>
        public float Desire(World world, in DutyAsk ask) =>
            world.Characters.Cold(ask.Slot) is { Fighter.IsAnimal: true } && smell._hunts.ContainsKey(ask.Squad.Id) ? 1 : 0;

        /// <summary>
        /// The act-out's start (serial): the hunter runs to its squad's zone's smell (<see cref="RunTo"/>) and is kept until the goal ends. False
        /// when the squad no longer keeps a zone.
        /// </summary>
        public bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan)
        {
            if (!smell._hunts.TryGetValue(ask.Squad.Id, out int zone) || !smell._zones.TryGetValue(zone, out var z)) return false;

            smell._hunters[ask.Slot] = ask.Id;
            RunTo(world, ask.Slot, z.Position);
            return true;
        }

        /// <summary>
        /// The goal ended (displaced, an order, a replan, taken, gone): the hunter is no longer kept. <c>Task_HuntingSmell</c> has no end of its own
        /// and holds nothing; the squad's zone stays with the squad, as the squad data's does.
        /// </summary>
        public void End(World world, int slot, CharacterId id, DutyEnd why)
        {
            if (smell._hunters.TryGetValue(slot, out var hunter) && hunter == id) smell._hunters.Remove(slot);
        }
    }

    #endregion

    /// <summary>
    /// Adds the zones and the hunts to the world hash, only while there are any.
    /// </summary>
    public void Hash(ref StateHasher hasher)
    {
        if (_zones.Count == 0 && _hunts.Count == 0 && _hunters.Count == 0) return;

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

        if (_hunters.Count > 0)
        {
            hasher.Add(0x48756E74);   // "Hunt": only while a hunter runs
            foreach (var (slot, id) in _hunters)
            {
                hasher.Add(slot);
                hasher.Add(id);
            }
        }

        hasher.Add(_elapsed);
    }
}
