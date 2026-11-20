using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Kidnap;

/// <summary>
/// The captive and slave goals as goals of the one chooser (docs/simulation.md "Captives", "Engine goals"): the prisoner's fate (135, 139, 282), the
/// eaters and worshippers (230, 232, 233, 237), the dogs' limb play (277, 278, 279), the slavers' processing, capture, carry and shouting (167, 168,
/// 183, 188, 260), their logistics (172, 173, 192, 194, 217, 218, 245) and the liberators (276). A serial look keeps what each member's finder
/// finds and the desires that read other characters or the town timers; the think's desire, finder and states read the picks; a chosen goal is
/// acted out as a <see cref="CaptiveJob"/> of its worker (<see cref="CaptiveSystem"/>), done at the original's own end.
/// </summary>
internal sealed class CaptiveGoals(CaptiveSystem captives, PopulationData data)
{
    #region Registration

    /// <summary>
    /// The captive goal types, in the order their picks are kept.
    /// </summary>
    static readonly TaskType[] Types =
    [
        TaskType.KillARandomCageOccupant, TaskType.EatARandomCageOccupantMeasuredRate, TaskType.EatARandomCageOccupant, TaskType.EatARandomDeadBody,
        TaskType.EatARandomKoBody, TaskType.FindCropsToEat, TaskType.FogmanWorshipVictim, TaskType.AnimalFetchALimb,
        TaskType.PlayBecauseIHaveALimbInMouth, TaskType.ChaseAllyDogsWithMouthLimbs, TaskType.NewSlaveProcessing, TaskType.SlaveWatching,
        TaskType.CaptureNewSlaves, TaskType.CarryWoundedSlaves, TaskType.GatherSlavesFromCages, TaskType.GatherSlavesFromCagesIfFemaleOrBeast,
        TaskType.GatherSlavesFromCagesIfItsAnExportTown, TaskType.WorkTheSlaves, TaskType.GoToSomewhereForDeliveringSlaves,
        TaskType.GiveAllMySlavesTo, TaskType.GiveAllMySlavesToIfItsAnImportTown, TaskType.SlaveGetInMyMastersCage, TaskType.LiberateAllThePrisoners,
    ];

    /// <summary>
    /// Each type's index in <see cref="Types"/> by type number (-1: no captive goal).
    /// </summary>
    static readonly int[] IndexOfType = BuildIndex();

    /// <summary>
    /// Builds <see cref="IndexOfType"/>.
    /// </summary>
    static int[] BuildIndex()
    {
        var index = Enumerable.Repeat(-1, TaskBehaviours.TypeCount).ToArray();
        for (int k = 0; k < Types.Length; k++) index[(int)Types[k]] = k;
        return index;
    }

    /// <summary>
    /// Registers each goal with the rows of its plan its job carries out (<see cref="DutyGoals.Register"/>; leaf rows from the TaskData providers:
    /// 134 Kill prisoner, 220 Eat prisoner, 231 Eating crops, 236 Worshiping, 3 the limb's pick-up, 40 the chase, 182 / 166 / 181 the processing,
    /// cuff and strip, 170 / 25 the lift and its first aid, 174 Ordering slaves, 159 Talking, 53 the trip, 107 Imprisoned, 110 the release).
    /// </summary>
    internal void Register(DutyGoals goals)
    {
        goals.Register(TaskType.KillARandomCageOccupant, GoalOf(TaskType.KillARandomCageOccupant), TaskType.KillCageOccupant);
        foreach (var eat in new[] { TaskType.EatARandomCageOccupantMeasuredRate, TaskType.EatARandomCageOccupant, TaskType.EatARandomDeadBody,
                     TaskType.EatARandomKoBody })
            goals.Register(eat, GoalOf(eat), TaskType.EatTargetAlive);

        goals.Register(TaskType.FindCropsToEat, GoalOf(TaskType.FindCropsToEat), TaskType.EatCrops);
        goals.Register(TaskType.FogmanWorshipVictim, GoalOf(TaskType.FogmanWorshipVictim), TaskType.WorshipTarget);
        goals.Register(TaskType.AnimalFetchALimb, GoalOf(TaskType.AnimalFetchALimb), TaskType.Pickup);
        goals.Register(TaskType.PlayBecauseIHaveALimbInMouth, GoalOf(TaskType.PlayBecauseIHaveALimbInMouth));
        goals.Register(TaskType.ChaseAllyDogsWithMouthLimbs, GoalOf(TaskType.ChaseAllyDogsWithMouthLimbs), TaskType.GetNearTo);
        foreach (var watch in new[] { TaskType.NewSlaveProcessing, TaskType.SlaveWatching })
            goals.Register(watch, GoalOf(watch), TaskType.ProcessAndStripNewSlave, TaskType.ChainTarget, TaskType.StripTargetsWeapons);

        goals.Register(TaskType.CaptureNewSlaves, GoalOf(TaskType.CaptureNewSlaves), TaskType.ChainTarget, TaskType.StripTargetsWeapons);
        goals.Register(TaskType.CarryWoundedSlaves, GoalOf(TaskType.CarryWoundedSlaves), TaskType.LiftObjectButHealFirst, TaskType.FirstAidOrder);
        foreach (var gather in new[] { TaskType.GatherSlavesFromCages, TaskType.GatherSlavesFromCagesIfFemaleOrBeast,
                     TaskType.GatherSlavesFromCagesIfItsAnExportTown })
            goals.Register(gather, GoalOf(gather), TaskType.GetSlave);

        foreach (var talk in new[] { TaskType.WorkTheSlaves, TaskType.GiveAllMySlavesTo, TaskType.GiveAllMySlavesToIfItsAnImportTown })
            goals.Register(talk, GoalOf(talk), TaskType.SendDialogueSignal);

        goals.Register(TaskType.GoToSomewhereForDeliveringSlaves, GoalOf(TaskType.GoToSomewhereForDeliveringSlaves), TaskType.TravelToTargetTown);
        goals.Register(TaskType.SlaveGetInMyMastersCage, GoalOf(TaskType.SlaveGetInMyMastersCage), TaskType.UseCage);
        goals.Register(TaskType.LiberateAllThePrisoners, GoalOf(TaskType.LiberateAllThePrisoners), TaskType.ReleasePrisoner);
    }

    /// <summary>
    /// The chooser's view of goal <paramref name="type"/>.
    /// </summary>
    Goal GoalOf(TaskType type) => new(this, type);

    #endregion

    #region Picks

    /// <summary>
    /// What a finder found: the target as the plan holds it, the cage or farm it is in or at, the ground item, and the town.
    /// </summary>
    readonly record struct Found(PlanTarget Target, string Place, int Item, int Site);

    /// <summary>
    /// A member's last look for one goal: who looked, the desire the look read and what the finder found.
    /// </summary>
    readonly record struct Pick(CharacterId Id, float Desire, Found Found);

    /// <summary>
    /// The picks of each goal (<see cref="Types"/> order) by slot (hashed).
    /// </summary>
    readonly SortedDictionary<int, Pick>[] _picks = Types.Select(_ => new SortedDictionary<int, Pick>()).ToArray();

    /// <summary>
    /// Scratch of <see cref="LookAt"/>: which goals the member's lists hold now (serial only).
    /// </summary>
    readonly bool[] _listed = new bool[Types.Length];

    /// <summary>
    /// The finders' look (serial, the captive step), for each character on its slot's turn every <paramref name="every"/> ticks.
    /// </summary>
    internal void Look(World world, int every)
    {
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
            if (world.Turn(s, every)) LookAt(world, s);

        foreach (var picks in _picks) Prune(table, picks);
    }

    /// <summary>
    /// The picks of the character at <paramref name="s"/>: for a member with a mind of its own, each captive goal its role, slave or race list holds
    /// in the hour (as <see cref="AiSystem.WeighsGoal"/> reads them) keeps its desire and target; for a player-faction mind those of its master's
    /// Slave list while it obeys (<see cref="AiSystem.ObeyedRoles"/>) and those of its own lists on the chooser's rungs (<see cref="Ladder.OnRungs"/>:
    /// an animal's race goals), looked for under the engine's empty board; else none.
    /// </summary>
    internal void LookAt(World world, int s)
    {
        if (s < 0) return;
        var table = world.Characters;
        var cold = table.Next[s].Alive ? table.Cold(s) : null;
        if (cold is null || ListsOf(world, s, cold) is not var (squad, board, roles, own))
        {
            foreach (var picks in _picks) picks.Remove(s);
            return;
        }

        MarkListed(world, roles, own, rungsOnly: cold.Ai!.PlayerSide);
        for (int k = 0; k < Types.Length; k++)
        {
            if (_listed[k]) LookFor(world, k, s, cold, squad, board);
            else _picks[k].Remove(s);
        }
    }

    /// <summary>
    /// The squad, board and lists whose captive goals the character's look weighs: a player-faction mind its master's Slave list while it obeys and
    /// its own lists, under the engine's board; an NPC with a mind of its own and a squad board its role list (an obeying slave's: its master's
    /// Slave list) and race list; null for any other character.
    /// </summary>
    static (Squad Squad, Blackboard Board, IReadOnlyList<AiGoal> Roles, IReadOnlyList<AiGoal> Own)? ListsOf(World world, int s, CharacterCold cold)
    {
        if (cold.Ai is { PlayerSide: true, Squad: var side } player)
            return (side, AiSystem.EngineBoard, AiSystem.ObeyedRoles(world, side, cold) ?? [], player.RaceGoals);
        if (cold is not { IsPlayer: false, Ai: { Engine: false, Player: false, Squad: { Ai: { } board } squad } mind }) return null;

        var roles = AiSystem.ObeyedRoles(world, squad, cold) ?? board.Roles[(int)AiSystem.RoleOf(squad, world.Characters.IdOf(s), cold)];
        return (squad, board, roles, mind.RaceGoals);
    }

    /// <summary>
    /// Marks in <see cref="_listed"/> the captive goals of the member's role and own lists (<see cref="ListsOf"/>) that hold in the hour; with
    /// <paramref name="rungsOnly"/> (a player-faction mind) only the own lists' types on the chooser's rungs (<see cref="Ladder.OnRungs"/>).
    /// </summary>
    void MarkListed(World world, IReadOnlyList<AiGoal> roles, IReadOnlyList<AiGoal> own, bool rungsOnly)
    {
        Array.Clear(_listed);
        Mark(roles, world.Hour, rungsOnly: false);
        Mark(own, world.Hour, rungsOnly);
    }

    /// <summary>
    /// Marks the captive goals of one list that hold in <paramref name="hour"/> (<see cref="MarkListed"/>).
    /// </summary>
    void Mark(IReadOnlyList<AiGoal> list, int hour, bool rungsOnly)
    {
        for (int g = 0; g < list.Count; g++)
        {
            var type = list[g].Task.Type;
            if ((uint)type >= (uint)IndexOfType.Length || IndexOfType[(int)type] is not (>= 0 and var k) || !list[g].InWindow(hour)) continue;
            if (!rungsOnly || Ladder.OnRungs.Contains(type)) _listed[k] = true;
        }
    }

    /// <summary>
    /// One goal's look for the member: its desire first (the chooser asks the finder only for a wanted goal), then its finder, which keeps the target
    /// its job works while the goal holds the member. 278 keeps no pick: its desire is the member's own and it has no finder.
    /// </summary>
    void LookFor(World world, int k, int s, CharacterCold cold, Squad squad, Blackboard board)
    {
        var type = Types[k];
        var id = world.Characters.IdOf(s);
        float desire = DesireOf(world, s, cold, squad, board, type, out int site);
        if (!(desire > 0) || type == TaskType.PlayBecauseIHaveALimbInMouth)
        {
            _picks[k].Remove(s);
            return;
        }

        var found = captives.JobOf(id, type) is { } job
            ? new Found(job.Target, job.Cage, job.Item, job.Site)
            : FindFor(world, s, cold, squad, board, type, site);
        if (found is { } f) _picks[k][s] = new Pick(id, desire, f);
        else _picks[k].Remove(s);
    }

    /// <summary>
    /// Drops the picks of slots that no longer hold the character that looked.
    /// </summary>
    static void Prune(CharacterTable table, SortedDictionary<int, Pick> picks)
    {
        List<int>? stale = null;
        foreach (var (slot, pick) in picks)
            if (!table.TryResolveNext(pick.Id, out int at) || at != slot) (stale ??= []).Add(slot);

        if (stale is not null) foreach (int slot in stale) picks.Remove(slot);
    }

    /// <summary>
    /// The asker's pick for its goal; null for none or another character's.
    /// </summary>
    Pick? PickOf(in DutyAsk ask) =>
        IndexOfType[(int)ask.Type] is >= 0 and var k && _picks[k].TryGetValue(ask.Slot, out var pick) && pick.Id == ask.Id ? pick : null;

    /// <summary>
    /// Adds the picks to the state hash, only while there are any.
    /// </summary>
    internal void Hash(ref StateHasher hasher)
    {
        if (_picks.All(p => p.Count == 0)) return;

        hasher.Add(0x43617047);   // "CapG"
        foreach (var picks in _picks)
        {
            hasher.Add(picks.Count);
            foreach (var (slot, pick) in picks)
            {
                hasher.Add(slot);
                hasher.Add(pick.Id);
                hasher.Add(pick.Desire);
                hasher.Add(pick.Found.Target.Character ?? CharacterId.None);
                hasher.Add(Rng.StableHash(pick.Found.Target.Fixture ?? ""));
                hasher.Add(pick.Found.Target.Position);
                hasher.Add(Rng.StableHash(pick.Found.Place));
                hasher.Add(pick.Found.Item);
                hasher.Add(pick.Found.Site);
            }
        }
    }

    #endregion

    #region Desires

    /// <summary>
    /// The goal's own desire as the look reads it (serial: the kill rate's timer, the slave lists), and the town it read (-1: none).
    /// </summary>
    float DesireOf(World world, int s, CharacterCold cold, Squad squad, Blackboard board, TaskType type, out int site)
    {
        site = -1;
        switch (type)
        {
            case TaskType.KillARandomCageOccupant or TaskType.EatARandomCageOccupantMeasuredRate:
                // AI slot 20 (FUN_14032e9b0): the town's timer is read (and made) before any cage test.
                site = captives.FateTown(world, s, board);
                return site >= 0 && captives.KillDue(world, site, cold) ? 1 : 0;
            case TaskType.EatARandomCageOccupant:
                site = captives.FateTown(world, s, board);
                return 1;
            case TaskType.ChaseAllyDogsWithMouthLimbs:
                return CaptiveSystem.ChaseDesire(world, cold);
            case TaskType.GatherSlavesFromCages or TaskType.GatherSlavesFromCagesIfFemaleOrBeast:
                return SlavesUp(world, squad) <= 3 ? 1 : 0;
            case TaskType.GatherSlavesFromCagesIfItsAnExportTown:
                return captives.ExportTown(world, cold, CaptiveSystem.TownAt(data, world.Characters.Next[s].Position)) ? 1 : 0;
            case TaskType.WorkTheSlaves or TaskType.GoToSomewhereForDeliveringSlaves or TaskType.GiveAllMySlavesTo:
                return captives.DeliverDesire(world, squad);
            case TaskType.GiveAllMySlavesToIfItsAnImportTown:
                return InPrisonOrCamp(world, s) ? captives.DeliverDesire(world, squad) : 0;
            default:
                return 1;
        }
    }

    /// <summary>
    /// AI slot 63's count (<c>FUN_14059b940</c>, <b>Verified</b>): the platoon's slaves that are not down and held in nothing.
    /// </summary>
    static int SlavesUp(World world, Squad squad)
    {
        var table = world.Characters;
        return CaptiveSystem.SlavesOf(world, squad).Count(s => table.Cold(s) is { } c && !CharacterFacts.Down(c) && c.InSomething == HeldState.None);
    }

    /// <summary>
    /// AI slot 66's gate (<c>FUN_140597c70</c>, <b>Verified</b>): the town the character stands in (its status town) is a prison or a slave camp.
    /// </summary>
    bool InPrisonOrCamp(World world, int s) =>
        CaptiveSystem.TownAt(data, world.Characters.Next[s].Position) is >= 0 and var site
        && data.Sites[site].Town.Type is TownType.Prison or TownType.SlaveCamp;

    /// <summary>
    /// The chooser's desire of <see cref="DutyAsk.Row"/>: the goal's own from its pick (its finder's factor is the chooser's), with the gates on the
    /// asker's own state read live; the rows on the way their own.
    /// </summary>
    float Desire(World world, in DutyAsk ask)
    {
        if (world.Characters.Cold(ask.Slot) is not { } cold) return 0;
        if (ask.Row != ask.Type) return RowDesire(ask.Row, cold);

        return ask.Type switch
        {
            TaskType.PlayBecauseIHaveALimbInMouth => CaptiveSystem.PlayDesire(world, cold),
            TaskType.ChaseAllyDogsWithMouthLimbs => PickOf(ask) is null ? 0 : CaptiveSystem.ChaseDesire(world, cold),
            TaskType.NewSlaveProcessing or TaskType.SlaveWatching or TaskType.CaptureNewSlaves => PickOf(ask) is null ? 0 : WatchDesire(ask, cold),
            TaskType.CarryWoundedSlaves => PickOf(ask) is null ? 0 : RescueDesire(cold),
            _ => PickOf(ask)?.Desire ?? 0,
        };
    }

    /// <summary>
    /// AI slot 61 (<c>FUN_140597b20</c>, <b>Verified</b>) of 167, 183 and 260: with a target kept (the goal holds the asker on its job) slot 59's, 0
    /// while carrying; with none, 1 while the character carries nothing and its posture is 0, else 0.
    /// </summary>
    float WatchDesire(in DutyAsk ask, CharacterCold cold)
    {
        if (!cold.Carrying.IsNone) return 0;
        return captives.JobOf(ask.Id, ask.Type) is not null || CharacterFacts.PostureZero(cold) ? 1 : 0;
    }

    /// <summary>
    /// 168's desire, the rescue one (<c>FUN_140599480</c>, <b>Verified</b>): 0 on the raw Hold bit (also while sneaking) or while carrying, else 1.
    /// </summary>
    static float RescueDesire(CharacterCold cold) => (cold.Orders & CharacterOrders.Hold) != 0 || !cold.Carrying.IsNone ? 0 : 1;

    /// <summary>
    /// The desire of a row on the way: 3 (AI slot 72) 0 while down; 170 (AI slot 59, the lift's) 0 while carrying; the rest slot 19's 1.0
    /// (<b>Verified</b>, the registration's stubs; 40's following desire is above 0 whenever the target lives, and the planner reads only that).
    /// </summary>
    static float RowDesire(TaskType row, CharacterCold cold) => row switch
    {
        TaskType.Pickup => CharacterFacts.Down(cold) ? 0 : 1,
        TaskType.LiftObjectButHealFirst => cold.Carrying.IsNone ? 1 : 0,
        _ => 1,
    };

    #endregion

    #region Finders

    /// <summary>
    /// The finder of <paramref name="type"/> for the member (serial; null: nothing found). 192 has none: its pick only names the shipping town.
    /// </summary>
    Found? FindFor(World world, int s, CharacterCold cold, Squad squad, Blackboard board, TaskType type, int site) => type switch
    {
        TaskType.KillARandomCageOccupant or TaskType.EatARandomCageOccupantMeasuredRate or TaskType.EatARandomCageOccupant =>
            FateCage(world, s, cold, board, site),
        TaskType.EatARandomDeadBody or TaskType.EatARandomKoBody =>
            OfCharacter(world, captives.BodyToEat(world, s, cold, dead: type == TaskType.EatARandomDeadBody)),
        TaskType.FindCropsToEat => Farm(world, s, cold, board),
        TaskType.FogmanWorshipVictim => captives.WorshipTarget(world, s) is { } shrine ? OfCage(shrine) : null,
        TaskType.AnimalFetchALimb => CaptiveSystem.LimbFor(world, s, cold) is { } limb ? new Found(PlanTarget.OfPlace(limb.Position), "", limb.Id, -1) : null,
        TaskType.ChaseAllyDogsWithMouthLimbs => OfCharacter(world, captives.MouthfulAlly(world, s, cold)),
        TaskType.NewSlaveProcessing or TaskType.SlaveWatching => ToProcess(world, s, cold, board, type),
        TaskType.CaptureNewSlaves => OfCharacter(world, captives.NearestDown(world, s, cold, capture: true)),
        TaskType.CarryWoundedSlaves => captives.Kidnap is null ? null : OfCharacter(world, captives.NearestDown(world, s, cold, capture: false)),
        TaskType.GatherSlavesFromCages or TaskType.GatherSlavesFromCagesIfFemaleOrBeast or TaskType.GatherSlavesFromCagesIfItsAnExportTown =>
            ToGather(world, s, cold, type),
        TaskType.WorkTheSlaves => SlaveToShout(world, s, squad),
        TaskType.GoToSomewhereForDeliveringSlaves => board.MissionTown >= 0 ? new Found(default, "", -1, board.MissionTown) : null,
        TaskType.GiveAllMySlavesTo or TaskType.GiveAllMySlavesToIfItsAnImportTown =>
            captives.ReceiverHere(world, s, cold, out int town) is { } receiver ? Character(world, receiver, town) : null,
        TaskType.SlaveGetInMyMastersCage => captives.OwnCage(world, s, cold) is { } cage ? OfCage(cage) : null,
        TaskType.LiberateAllThePrisoners => captives.LiberateTarget(world, s, cold, board) is { } freed ? Occupant(freed) : null,
        _ => null,
    };

    /// <summary>
    /// The finder of 135, 139 and 282 (<see cref="CaptiveSystem.FateTarget"/>, its memory written here): the cage's occupant, at the cage. A fresh
    /// choice has the occupant say event 10 MARKED_FOR_DEATH to the one who picked it (<c>FUN_1408d0d90</c>), as the finder runs.
    /// </summary>
    Found? FateCage(World world, int s, CharacterCold cold, Blackboard board, int site)
    {
        if (captives.FateTarget(world, s, cold, board.OwnHome?.Key, out bool fresh) is not { } cage) return null;

        var picker = world.Characters.IdOf(s);
        if (fresh) world.Defer(new Dialogue.DialogueEvent(cage.Occupant, picker, CaptiveSystem.MarkedForDeath) { Tick = world.Tick + 1 });
        return new Found(PlanTarget.OfCharacter(cage.Occupant, cage.Position), cage.Id, -1, site);
    }

    /// <summary>
    /// 232's farm (<see cref="CaptiveSystem.CropsToEat"/>), its place the farm building's.
    /// </summary>
    Found? Farm(World world, int s, CharacterCold cold, Blackboard board) =>
        captives.CropsToEat(world, s, cold, board) is { } farm && world.Buildings.Find(farm) is { } site
            ? new Found(PlanTarget.OfPlace(site.Position), farm, -1, -1)
            : null;

    /// <summary>
    /// 183's and 260's captive (<c>FUN_1408cd630</c>, <c>FUN_1408cc110</c>): 183 a loose slave in sight first, else a caged one of the squad's town.
    /// </summary>
    Found? ToProcess(World world, int s, CharacterCold cold, Blackboard board, TaskType type)
    {
        if (type == TaskType.SlaveWatching && captives.FreeSlaveToProcess(world, s, cold) is >= 0 and var loose) return OfCharacter(world, loose);
        if (board.Town < 0) return null;

        return captives.ProcessTarget(world, s, cold, board.Town, watching: type == TaskType.SlaveWatching) is { } cage ? Occupant(cage) : null;
    }

    /// <summary>
    /// The gatherers' captive (<see cref="CaptiveSystem.GatherTarget"/>) in the town the gatherer stands in: 217 any cage of it, 173 and 245 its
    /// faction's within one zone cell.
    /// </summary>
    Found? ToGather(World world, int s, CharacterCold cold, TaskType type)
    {
        var at = world.Characters.Next[s].Position;
        int site = CaptiveSystem.TownAt(data, at);
        bool export = type == TaskType.GatherSlavesFromCagesIfItsAnExportTown;
        if (export && site < 0) return null;

        var cages = export ? captives.CagesOf(world, site) : captives.NearbyCages(world, site, at, cold.Faction);
        return captives.GatherTarget(world, s, cold, cages, type) is { } cage ? Occupant(cage) : null;
    }

    /// <summary>
    /// 188's slave (<c>FUN_1408cd940</c>): a random live slave of the platoon's list, uncarried, held in nothing, not on the give-up list and no other
    /// job's.
    /// </summary>
    Found? SlaveToShout(World world, int s, Squad squad)
    {
        var table = world.Characters;
        double now = world.GameHours;
        var slaves = new List<int>();
        foreach (int slave in CaptiveSystem.SlavesOf(world, squad))
        {
            var id = table.IdOf(slave);
            if (table.Cold(slave) is { CarriedBy.IsNone: true, InSomething: HeldState.None } && !captives.Waiting(captives.GivenUp, id, now)
                && !captives.Busy(id))
                slaves.Add(slave);
        }

        if (slaves.Count == 0) return null;
        ulong roll = Rng.Hash(world.Seed, Rng.Key(table.IdOf(s)), RngPurpose.Slow, CaptiveSystem.ShoutSalt + (ulong)world.Tick);
        return OfCharacter(world, slaves[Rng.Int(roll, slaves.Count)]);
    }

    /// <summary>
    /// The character at <paramref name="slot"/> where it stands (-1: none).
    /// </summary>
    static Found? OfCharacter(World world, int slot) =>
        slot < 0 ? null : new Found(PlanTarget.OfCharacter(world.Characters.IdOf(slot), world.Characters.Next[slot].Position), "", -1, -1);

    /// <summary>
    /// <paramref name="who"/> where it stands, in the town <paramref name="site"/>.
    /// </summary>
    static Found? Character(World world, CharacterId who, int site) =>
        world.Characters.TryResolveNext(who, out int at) ? new Found(PlanTarget.OfCharacter(who, world.Characters.Next[at].Position), "", -1, site) : null;

    /// <summary>
    /// A cage's occupant, at the cage.
    /// </summary>
    static Found Occupant(Fixture cage) => new(PlanTarget.OfCharacter(cage.Occupant, cage.Position), cage.Id, -1, cage.Site);

    /// <summary>
    /// A cage itself.
    /// </summary>
    static Found OfCage(Fixture cage) => new(PlanTarget.OfFixture(cage.Id, cage.Position), cage.Id, -1, cage.Site);

    /// <summary>
    /// The target the finder gives the asker: its job's while the goal holds it (the original keeps the current goal's finder hand), else its pick;
    /// factor 1.0 with one (<c>DAT_14167c308</c>), else 0.
    /// </summary>
    float Find(in DutyAsk ask, out PlanTarget target)
    {
        target = captives.JobOf(ask.Id, ask.Type) is { } job ? job.Target : PickOf(ask)?.Found.Target ?? default;
        return target.IsNone ? 0 : 1;
    }

    #endregion

    #region States

    /// <summary>
    /// The states of the goals' rows (parallel: <c>Previous</c> places, cold state and fixtures only serial steps write): the asker's own whatever
    /// the target (149 for 277's limb, 79 for 172's cage, 155 for 194 / 218's receiver), then a finder's target's (<see cref="PlanTarget.Found"/>: a
    /// character, a cage, a farm or a limb); null for the rest.
    /// </summary>
    bool? State(World world, in DutyAsk ask, StateType state)
    {
        if (OwnState(world, ask, state) is { } own) return own;

        var target = ask.Target;
        if (target.Kind != PlanTarget.Found) return null;
        if (target.Character is { } who) return CharacterState(world, ask, who, state);
        if (target.Fixture is { } id) return CageState(world, ask, id, state);

        return ask.Type == TaskType.FindCropsToEat ? FarmState(world, ask, state) : LimbState(world, ask, state);
    }

    /// <summary>
    /// The asker's own states the rows list: 149 gotAFoodItem (<c>FUN_140597e20</c>: a mouthful, or food among its items or in its worn bag), 79
    /// I_AM_IMPRISONED (held in a cage), 155 iHaveSomeSlaves (<c>FUN_14059a0e0</c>: the platoon's slave list is not empty; asked only by 194 / 218, whose look's desire is 0.25 x
    /// that list),
    /// 144 "am hungry" always true and 135, 146 always false (<b>Verified</b>, docs/game/ai-tasks.md).
    /// </summary>
    bool? OwnState(World world, in DutyAsk ask, StateType state)
    {
        var cold = world.Characters.Cold(ask.Slot);
        return state switch
        {
            StateType.GotAFoodItem => cold is not null && GotFood(cold),
            StateType.AmImprisoned => cold is { InSomething: HeldState.Imprisoned },
            StateType.IHaveSomeSlaves => PickOf(ask) is { Desire: > 0 },
            StateType.AmHungry => true,
            StateType.TargetIsEaten or StateType.TargetWorshiped => false,
            _ => null,
        };
    }

    /// <summary>
    /// State 149's reading: a limb in the mouth, or a food item among the character's items or in its worn bag.
    /// </summary>
    static bool GotFood(CharacterCold cold) =>
        cold.Mouth is not null || cold.Inventory is { } inv && (inv.Items.Exists(Items.FeedSystem.IsFood)
            || inv.Backpack?.Contents is { } bag && bag.Items.Exists(Items.FeedSystem.IsFood));

    /// <summary>
    /// The states of a character target (<b>Verified</b> evaluators, each id's registry thunk followed in the 1.0.68 exe): 1 / 46 at it (its cage's
    /// reach when caged), 7 <see cref="CharacterFacts.TargetDown"/>, 22 needsFirstAid, 28 within 10, 73 false (no building), 78 caged, 98 not dead
    /// (<c>FUN_1405a1940</c>), 109 / 110 false until the job ends, 115 carried by the asker and needing no first aid (<c>FUN_14059ab30</c>), 116 its
    /// slave (<see cref="MySlave"/>), 118 unarmed, 120 looks like a slave to the asker (<c>FUN_1405a6f60</c>), 124 slave state other than 1
    /// (<c>FUN_1405a2080</c>), 125 conscious, 134 chained (<c>FUN_1405a1db0</c>, the chained flag <c>setChainedMode</c> sets:
    /// <see cref="LockSystem.Chained"/>, worn shackles locked or not), 142 dead, down or held in something (<c>FUN_1405a17a0</c>).
    /// </summary>
    bool? CharacterState(World world, in DutyAsk ask, CharacterId who, StateType state)
    {
        var table = world.Characters;
        var c = table.IsAlive(who) ? table.Cold(who.Slot) : null;
        var me = table.Cold(ask.Slot)!;
        return state switch
        {
            StateType.IsAtLocation or StateType.IsAtLocation46 => c is not null && AtCharacter(world, ask, who, c),
            StateType.IsTargetDown => CharacterFacts.TargetDown(c),
            StateType.NeedsFirstAid => c is not null && FirstAid.FirstAidSystem.NeedsFirstAid(world, c),
            StateType.IsNearTo => c is not null && Vector3.Distance(table.Previous[ask.Slot].Position, table.Previous[who.Slot].Position) <= 10,
            StateType.MachineHasFreeOperatorSlot or StateType.IsDialogDelivered or StateType.IsDialogDelivered110 => false,
            StateType.TargetIsImprisoned => c is { InSomething: HeldState.Imprisoned },
            StateType.MachineOccupantIsAlive => c is { Medical: not { Dead: true } },
            StateType.IsCarryingTargetHealthy => c is not null && me.Carrying == who && !FirstAid.FirstAidSystem.NeedsFirstAid(world, c),
            StateType.TargetIsMySlave => c is not null && MySlave(me, c),
            StateType.TargetIsUnarmed => c is not null && CharacterFacts.TargetUnarmed(c),
            StateType.TargetLooksLikeASlave => c is not null && captives.LooksLikeASlave(me, c),
            StateType.IsTargetEscapee => c is { SlaveState: not 1 },
            StateType.TargetIsConscious => c is not null && CharacterFacts.Conscious(c),
            StateType.IsChained => LockSystem.Chained(c),
            StateType.IsTargetRestrainedOrKo => c is not null && (c.Medical is { Dead: true } || CharacterFacts.Down(c) || c.InSomething != HeldState.None),
            _ => null,
        };
    }

    /// <summary>
    /// 116 TARGET_IS_MY_SLAVE (<c>FUN_1405a2170</c>, <b>Verified</b>): chained, and the asker is among its owners (Meitou: a slave of the asker's
    /// faction obeying its squad).
    /// </summary>
    static bool MySlave(CharacterCold me, CharacterCold c) =>
        LockSystem.Chained(c) && c.SlaveState == 1 && c.SlaveOwner == me.Faction && c.SlaveMaster == me.SquadId;

    /// <summary>
    /// Whether the asker is at its character target: its job is at work there, or it stands within the job's reach (the cage's use range +
    /// <see cref="CaptiveSettings.Reach"/> for a caged one; <see cref="CaptiveSettings.ShoutReach"/> for the talks; <see cref="CaptiveSettings.Reach"/>
    /// for the rest) on the last tick's places.
    /// </summary>
    bool AtCharacter(World world, in DutyAsk ask, CharacterId who, CharacterCold c)
    {
        if (captives.JobOf(ask.Id, ask.Type) is { Phase: CaptivePhase.Work }) return true;

        var table = world.Characters;
        var me = table.Previous[ask.Slot].Position;
        var settings = captives.Settings;
        if (c is { IsImprisoned: true, InWhat: { } cageId } && world.Fixtures.Find(cageId) is { } cage)
            return Near(me, cage.Position, cage.UseRange + settings.Reach);

        bool talks = ask.Type is TaskType.WorkTheSlaves or TaskType.GiveAllMySlavesTo or TaskType.GiveAllMySlavesToIfItsAnImportTown;
        return Near(me, table.Previous[who.Slot].Position, talks ? settings.ShoutReach : settings.Reach);
    }

    /// <summary>
    /// The states of a cage target (237's shrine, 172's own cage): 1 / 46 within the worship reach (236) or the cage's use range +
    /// <see cref="CaptiveSettings.Reach"/> (107), 73 the cage free.
    /// </summary>
    bool? CageState(World world, in DutyAsk ask, string id, StateType state)
    {
        if (world.Fixtures.Find(id) is not { } cage) return null;

        float reach = ask.Type == TaskType.FogmanWorshipVictim ? CaptiveSystem.WorshipReach : cage.UseRange + captives.Settings.Reach;
        return state switch
        {
            StateType.IsAtLocation or StateType.IsAtLocation46 => Near(world.Characters.Previous[ask.Slot].Position, cage.Position, reach),
            StateType.MachineHasFreeOperatorSlot => FixtureRegistry.IsFree(world.Characters, cage),
            _ => null,
        };
    }

    /// <summary>
    /// The states of 232's farm: 143 farm has food (<c>FUN_1408ca350</c>: a farm grown past 0.4 and dead below 0.9,
    /// <see cref="Production.ProductionSystem.HasFood"/>), 144 always true, 1 at the farm (or its job eating there).
    /// </summary>
    bool? FarmState(World world, in DutyAsk ask, StateType state)
    {
        var job = captives.JobOf(ask.Id, ask.Type);
        string? farm = job?.Cage ?? (PickOf(ask) is { } pick && pick.Found.Target.Position == ask.Target.Position ? pick.Found.Place : null);
        return state switch
        {
            StateType.FarmHasFood => farm is not null && world.Machines.Find(farm) is { Farm: { } f } && Production.ProductionSystem.HasFood(f),
            StateType.AmHungry => true,
            StateType.IsAtLocation => job is { Phase: CaptivePhase.Work }
                || Near(world.Characters.Previous[ask.Slot].Position, ask.Target.Position, captives.Settings.Reach + CaptiveSystem.CropReach),
            _ => null,
        };
    }

    /// <summary>
    /// The states of row 3 for 277's limb: 1 within the pick-up's reach, 15 still on the ground, 3 no longer on it.
    /// </summary>
    bool? LimbState(World world, in DutyAsk ask, StateType state)
    {
        int item = captives.JobOf(ask.Id, ask.Type)?.Item
            ?? (PickOf(ask) is { } pick && pick.Found.Target.Position == ask.Target.Position ? pick.Found.Item : -1);
        bool onGround = item >= 0 && world.GroundItems.Find(item) is not null;
        return state switch
        {
            StateType.IsAtLocation => Items.PickUps.InReach(world.Characters.Previous[ask.Slot].Position, ask.Target.Position),
            StateType.IsItemLootable => onGround,
            StateType.IsSubjectInInventory => !onGround,
            _ => null,
        };
    }

    /// <summary>
    /// Whether <paramref name="at"/> lies within <paramref name="reach"/> of <paramref name="spot"/> on the ground plane.
    /// </summary>
    static bool Near(Vector3 at, Vector3 spot, float reach) =>
        Vector2.DistanceSquared(new Vector2(at.X, at.Z), new Vector2(spot.X, spot.Z)) <= reach * reach;

    #endregion

    #region Starts

    /// <summary>
    /// The goal's start (serial): its job on the plan's target, refused when the target is gone, no longer fits or another member's job works it
    /// (the finders' <c>FUN_1402690e0</c>).
    /// </summary>
    bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan)
    {
        var target = ask.Target.IsNone ? PlanTargetOf(plan) : ask.Target;
        if (world.Characters.Cold(ask.Slot) is not { } cold || JobFor(world, ask, cold, target) is not { } job) return false;

        captives.Begin(world, job);
        return true;
    }

    /// <summary>
    /// The finder's target in the plan (the first step on one; none without).
    /// </summary>
    static PlanTarget PlanTargetOf(IReadOnlyList<PlanStep> plan)
    {
        foreach (var step in plan)
            if (step.Target.Kind == PlanTarget.Found) return step.Target;
        return default;
    }

    /// <summary>
    /// The job of the asker's goal on <paramref name="target"/>; null when it cannot start.
    /// </summary>
    CaptiveJob? JobFor(World world, in DutyAsk ask, CharacterCold cold, in PlanTarget target) => ask.Type switch
    {
        TaskType.KillARandomCageOccupant => FateJob(world, ask, cold, target, CaptiveJobKind.Kill),
        TaskType.EatARandomCageOccupant or TaskType.EatARandomCageOccupantMeasuredRate => FateJob(world, ask, cold, target, CaptiveJobKind.Eat),
        TaskType.EatARandomDeadBody or TaskType.EatARandomKoBody => LooseJob(world, ask, cold, target, CaptiveJobKind.EatBody),
        TaskType.FindCropsToEat => CropJob(world, ask, cold, target),
        TaskType.FogmanWorshipVictim => CageOnlyJob(world, ask, cold, target, CaptiveJobKind.Worship),
        TaskType.AnimalFetchALimb => FetchJob(world, ask, cold, target),
        TaskType.PlayBecauseIHaveALimbInMouth => cold.Mouth is null ? null : New(ask, cold, CaptiveJobKind.DogPlay, CharacterId.None, "", -1, target),
        TaskType.ChaseAllyDogsWithMouthLimbs => LooseJob(world, ask, cold, target, CaptiveJobKind.DogChase),
        TaskType.NewSlaveProcessing or TaskType.SlaveWatching => ProcessJob(world, ask, cold, target),
        TaskType.CaptureNewSlaves => LooseJob(world, ask, cold, target, CaptiveJobKind.Capture),
        TaskType.CarryWoundedSlaves => captives.Kidnap is null || !CarrySystem.CanCarry(world, cold, ask.Type) ? null
            : LooseJob(world, ask, cold, target, CaptiveJobKind.Carry),
        TaskType.GatherSlavesFromCages or TaskType.GatherSlavesFromCagesIfFemaleOrBeast or TaskType.GatherSlavesFromCagesIfItsAnExportTown =>
            CagedJob(world, ask, cold, target, CaptiveJobKind.Gather, claims: ask.Type == TaskType.GatherSlavesFromCagesIfFemaleOrBeast),
        TaskType.WorkTheSlaves => LooseJob(world, ask, cold, target, CaptiveJobKind.Shout),
        TaskType.GoToSomewhereForDeliveringSlaves => PickOf(ask) is { Found.Site: >= 0 } trip
            ? New(ask, cold, CaptiveJobKind.Deliver, CharacterId.None, "", trip.Found.Site, target) : null,
        TaskType.GiveAllMySlavesTo or TaskType.GiveAllMySlavesToIfItsAnImportTown => SellJob(world, ask, cold, target),
        TaskType.SlaveGetInMyMastersCage => CageOnlyJob(world, ask, cold, target, CaptiveJobKind.CageSelf),
        TaskType.LiberateAllThePrisoners => CagedJob(world, ask, cold, target, CaptiveJobKind.Liberate, claims: true),
        _ => null,
    };

    /// <summary>
    /// A new job of the asker's goal.
    /// </summary>
    static CaptiveJob New(in DutyAsk ask, CharacterCold cold, CaptiveJobKind kind, CharacterId captive, string cage, int site, in PlanTarget target,
        int item = -1) =>
        new()
        {
            Kind = kind, Worker = ask.Id, Captive = captive, Cage = cage, Faction = cold.Faction, Site = site, Goal = ask.Type, Target = target, Item = item,
        };

    /// <summary>
    /// A kill or a meal on a caged captive: the kill draws its weapon (refused with none: row 134 needs it), the eater sheathes (220's
    /// !hasWeaponEquipped). The town is the one its desire read (the kill timer the captive's death resets).
    /// </summary>
    CaptiveJob? FateJob(World world, in DutyAsk ask, CharacterCold cold, in PlanTarget target, CaptiveJobKind kind)
    {
        if (CagedJob(world, ask, cold, target, kind, claims: true) is not { } job) return null;
        if (kind == CaptiveJobKind.Kill && !captives.DrawWeapon(cold)) return null;
        if (kind == CaptiveJobKind.Eat) cold.DrawnWeapon = WeaponKinds.None;

        return New(ask, cold, kind, job.Captive, job.Cage, PickOf(ask)?.Found.Site ?? -1, target);
    }

    /// <summary>
    /// A job on a live caged captive: the cage it is in still holds it, and (with <paramref name="claims"/>) no other job works the cage or the
    /// captive.
    /// </summary>
    CaptiveJob? CagedJob(World world, in DutyAsk ask, CharacterCold cold, in PlanTarget target, CaptiveJobKind kind, bool claims)
    {
        var table = world.Characters;
        if (target.Character is not { } captive || !table.TryResolveNext(captive, out int c) || table.Cold(c) is not { InWhat: { } cageId } cc
            || !cc.IsImprisoned || world.Fixtures.Find(cageId) is not { } cage || CaptiveSystem.OccupantSlot(world, cage) != c)
            return null;

        if (claims && (captives.Taken(cage.Id) || captives.Busy(captive))) return null;
        return New(ask, cold, kind, captive, cage.Id, cage.Site, target);
    }

    /// <summary>
    /// A job on a character out of any cage (a body to eat, a mouthful ally, a downed one, a slave to shout at): alive in the table, uncarried, held
    /// in nothing, and no other job's.
    /// </summary>
    CaptiveJob? LooseJob(World world, in DutyAsk ask, CharacterCold cold, in PlanTarget target, CaptiveJobKind kind)
    {
        var table = world.Characters;
        if (target.Character is not { } who || !table.TryResolveNext(who, out int c) || table.Cold(c) is not { } cc || !cc.CarriedBy.IsNone
            || cc.InSomething != HeldState.None || captives.Busy(who))
            return null;

        return New(ask, cold, kind, who, "", -1, target);
    }

    /// <summary>
    /// A processing job: a caged captive's (<see cref="CagedJob"/>), else a loose slave's.
    /// </summary>
    CaptiveJob? ProcessJob(World world, in DutyAsk ask, CharacterCold cold, in PlanTarget target)
    {
        if (target.Character is { } who && world.Characters.TryResolveNext(who, out int c) && world.Characters.Cold(c) is { IsImprisoned: true })
            return CagedJob(world, ask, cold, target, CaptiveJobKind.Process, claims: true);

        return LooseJob(world, ask, cold, target, CaptiveJobKind.Process) is { } loose ? New(ask, cold, loose.Kind, loose.Captive, "",
            world.SystemOf<AiSystem>() is not null ? ask.Squad.Ai?.Town ?? -1 : -1, target) : null;
    }

    /// <summary>
    /// A job at a cage itself (237's shrine, 172's own cage): the shrine still holds someone; the own cage is still free and no other job heads for
    /// it.
    /// </summary>
    CaptiveJob? CageOnlyJob(World world, in DutyAsk ask, CharacterCold cold, in PlanTarget target, CaptiveJobKind kind)
    {
        if (target.Fixture is not { } id || world.Fixtures.Find(id) is not { } cage) return null;
        if (kind == CaptiveJobKind.Worship)
            return CaptiveSystem.OccupantSlot(world, cage) < 0 ? null : New(ask, cold, kind, cage.Occupant, cage.Id, cage.Site, target);

        if (!FixtureRegistry.IsFree(world.Characters, cage) || captives.Taken(cage.Id) || cold.InSomething != HeldState.None) return null;
        return New(ask, cold, kind, ask.Id, cage.Id, cage.Site, target);
    }

    /// <summary>
    /// 232's job on the pick's farm while it still has food and no other eater is on it.
    /// </summary>
    CaptiveJob? CropJob(World world, in DutyAsk ask, CharacterCold cold, in PlanTarget target)
    {
        if (PickOf(ask) is not { } pick || pick.Found.Target.Position != target.Position || pick.Found.Place is not { Length: > 0 } farm) return null;
        if (world.Machines.Find(farm) is not { Farm: { } f } || !Production.ProductionSystem.HasFood(f) || captives.Taken(farm)) return null;

        return New(ask, cold, CaptiveJobKind.EatCrops, CharacterId.None, farm, -1, target);
    }

    /// <summary>
    /// 277's job: the pick-up (row 3, <see cref="Items.PickUps"/>) of the pick's limb, a walk to it or the take where the animal stands within reach;
    /// null when the limb is gone, another character fetches it or the row is not the animal's.
    /// </summary>
    CaptiveJob? FetchJob(World world, in DutyAsk ask, CharacterCold cold, in PlanTarget target)
    {
        var table = world.Characters;
        if (PickOf(ask) is not { } pick || pick.Found.Target.Position != target.Position || pick.Found.Item is not (>= 0 and var item)) return null;
        if (world.GroundItems.Find(item) is null || CarrySystem.Claimed(table).Contains(item)) return null;

        var task = Items.PickUps.InReach(table.Next[ask.Slot].Position, target.Position) ? CharacterTask.Idle : CharacterTask.GoTo;
        var request = new TaskRequest(task, TaskType.Pickup, TaskSource.Goal) { Point = target.Position, Detail = item };
        if (TaskStart.Start(world, ask.Slot, request) != StartResult.Started) return null;

        return New(ask, cold, CaptiveJobKind.Fetch, CharacterId.None, "", -1, target, item);
    }

    /// <summary>
    /// 194 / 218's job on the receiver of the town the pick found it in, while the leader still has slaves.
    /// </summary>
    CaptiveJob? SellJob(World world, in DutyAsk ask, CharacterCold cold, in PlanTarget target)
    {
        if (target.Character is not { } receiver || !world.Characters.IsAlive(receiver) || PickOf(ask) is not { Found.Site: >= 0 } pick) return null;
        if (!CaptiveSystem.SlavesOf(world, ask.Squad).Any()) return null;

        return New(ask, cold, CaptiveJobKind.Sell, receiver, "", pick.Found.Site, target);
    }

    #endregion

    #region Goals

    /// <summary>
    /// The worker is no longer held: its job goes.
    /// </summary>
    void Drop(World world, CharacterId id, TaskType type) => captives.Drop(world, id, type);

    /// <summary>
    /// One captive goal as the chooser weighs it (tier and weight from the package's or race's AI_TASK record; no commitment).
    /// </summary>
    sealed class Goal(CaptiveGoals goals, TaskType type) : IDutyGoal
    {
        /// <summary>
        /// The goal's or a plan row's desire (<see cref="CaptiveGoals.Desire"/>).
        /// </summary>
        public float Desire(World world, in DutyAsk ask) => goals.Desire(world, ask);

        /// <summary>
        /// The goal's finder (<see cref="CaptiveGoals.Find"/>).
        /// </summary>
        public float Find(World world, in DutyAsk ask, out PlanTarget target) => goals.Find(ask, out target);

        /// <summary>
        /// Its rows' states (<see cref="CaptiveGoals.State"/>).
        /// </summary>
        public bool? State(World world, in DutyAsk ask, StateType state) => goals.State(world, ask, state);

        /// <summary>
        /// Starts its job on the plan's target.
        /// </summary>
        public bool Start(World world, in DutyAsk ask, IReadOnlyList<PlanStep> plan) => goals.Start(world, ask, plan);

        /// <summary>
        /// The worker is no longer held: its job goes (<see cref="CaptiveSystem.Drop"/>).
        /// </summary>
        public void End(World world, int slot, CharacterId id, DutyEnd why) => goals.Drop(world, id, type);
    }

    #endregion
}
