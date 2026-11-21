using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Crime;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.FirstAid;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Production;
using static Meitou.Tests.Bodies.BodyFixtures;

namespace Meitou.Tests.Simulation;

/// <summary>
/// The decision step's precedence for the player's characters (docs/simulation.md "Decision step"; the original's chooser, docs/game/ai-tasks.md
/// "Precedence"): one scenario per source pair, on the task start rig with self-defence and feeding.
/// </summary>
public class DecisionTests
{
    /// <summary>
    /// The rig after its opening fight is stopped: the fighter stands idle, no longer sneaking, with the decision trace on.
    /// </summary>
    static TaskStartTests.Rig Quiet(bool senses = false, FactionRelations? relations = null)
    {
        var rig = new TaskStartTests.Rig(carrying: false, decide: true, senses: senses, relations: relations, ai: true);
        rig.World.RunTicks(3);
        rig.Say(new StopCommand([rig.Fighter]) { Tick = rig.World.Tick });
        rig.World.RunTick();
        rig.Cold(rig.Fighter).Orders &= ~CharacterOrders.Sneak;
        rig.World.Decisions.Tracing = true;
        return rig;
    }

    /// <summary>
    /// An NPC of the enemy's faction right beside the fighter, attacking it.
    /// </summary>
    static CharacterId Attacker(TaskStartTests.Rig rig)
    {
        var at = rig.World.Characters.Next[rig.Fighter.Slot].Position;
        var foe = rig.Add(at.X + 12, at.Z, player: false);
        rig.Say(new AttackOrder([foe], rig.Fighter) { Tick = rig.World.Tick });
        return foe;
    }

    static void RunUntil(TaskStartTests.Rig rig, Func<bool> done, int max, string what)
    {
        for (int t = 0; t < max && !done(); t++) rig.World.RunTick();

        Assert.True(done(),
                $"{what} within {max} ticks; "
                + $"last decision: {(rig.Cold(rig.Fighter).Decision is { } d ? $"{d.Winner} | {string.Join(" | ", d.Lines)}" : "none")}");
    }

    static bool Hurt(CharacterCold cold) => cold.Medical!.Parts.Any(p => p.Flesh < p.MaxHp);

    /// <summary>
    /// Orders beat Self preservation: a resting character attacked from beside it keeps resting; the blows that land stagger it, and the step asked
    /// again picks the order. Catches a character on an order that turns to fight its attacker.
    /// </summary>
    [Fact]
    public void An_order_beats_self_defence_and_a_landed_blow_asks_again()
    {
        using var rig = Quiet();
        var f = rig.Fighter;
        rig.Say(new RestOrder([f]) { Tick = rig.World.Tick });
        rig.World.RunTick();

        Assert.Equal(CharacterTask.Rest, rig.Task(f));

        Attacker(rig);
        RunUntil(rig, () => Hurt(rig.Cold(f)), 600, "a blow landing");
        long hit = rig.World.Tick;
        RunUntil(rig, () => rig.Cold(f).Decision is { } d && d.Tick >= hit - 1, 60, "a decision after the blow");

        Assert.Equal("Orders", rig.Cold(f).Decision!.Winner);
        Assert.Equal(CharacterTask.Rest, rig.Task(f));
        Assert.False(rig.Cold(f).InCombat);
    }

    /// <summary>
    /// Self preservation beats a job: a character operating the well on its job fights the attacker at its side. Catches a job's task that keeps its
    /// character from defending.
    /// </summary>
    [Fact]
    public void Self_defence_beats_a_jobs_task()
    {
        using var rig = Quiet();
        var f = rig.Fighter;
        rig.Say(new AddJob([f], TaskType.OperateMachinery, rig.WellSite) { Tick = rig.World.Tick });
        RunUntil(rig, () => rig.Task(f) == CharacterTask.Operate, 600, "operating the well");
        var foe = Attacker(rig);
        RunUntil(rig, () => rig.Task(f) == CharacterTask.Attack, 120, "fighting back");

        Assert.Equal(foe.Slot, rig.Combat.StateOf(f).TargetSlot);
        Assert.Equal("32 Self preservation", rig.Cold(f).Decision!.Winner);
    }

    /// <summary>
    /// The desire of goal 15 Protect allies on the character's chooser rung (the player faction's <c>AI Goals</c> entry): its first ally above 0
    /// times its finder's best (<see cref="AiSystem.Desire"/>, as the tier scores it).
    /// </summary>
    static float ProtectDesire(TaskStartTests.Rig rig, CharacterId who)
    {
        const TaskType type = TaskType.ProtectAllies;
        var cold = rig.Cold(who);
        var squad = rig.World.Squads.Find(cold.SquadId)!;
        var ai = rig.World.SystemOf<AiSystem>()!;

        return ai.Desire(rig.World, TaskBehaviours.Of(type), type, TaskProfiles.Of(type), squad, AiSystem.PlayerSideBoard(squad), cold.Ai!, who,
                TaskTargetType.Specific);
    }

    /// <summary>
    /// A fight on the mate: an NPC of the enemy's faction beside it attacks it; the mate (a player's character) fights back.
    /// </summary>
    static CharacterId AttackMate(TaskStartTests.Rig rig)
    {
        var at = rig.World.Characters.Next[rig.Mate.Slot].Position;
        var foe = rig.Add(at.X + 12, at.Z, player: false);
        rig.Say(new AttackOrder([foe], rig.Mate) { Tick = rig.World.Tick });
        return foe;
    }

    /// <summary>
    /// 15 Protect allies for a player character (FUN_140599b90, FUN_14099ca10): any sensed ally of the player's faction under attack is joined,
    /// whatever its squad, at its fight threat (FUN_14099a190) weighted by the distance (1 within about 3 units, 0.1 from 30). Catches the old
    /// squad-and-range join with its fixed desire.
    /// </summary>
    [Fact]
    public void A_player_character_joins_a_fighting_allys_fight_by_goal_15_weighted_by_distance()
    {
        using var rig = Quiet(senses: true);
        var (far, mate) = (rig.Fighter, rig.Mate);   // 40 units apart
        var at = rig.World.Characters.Next[mate.Slot].Position;
        var near = rig.Add(at.X + 2, at.Z, player: true);
        rig.Join(near, null);   // in a squad of its own, not the mate's
        // the rig's own enemy still attacks the fighter: the fighter would be an ally under attack too
        rig.Cold(rig.Enemy).Medical!.KnockOut(100000);
        AttackMate(rig);
        var senses = rig.World.SystemOf<AggroSystem>()!;
        float Threat() => rig.Combat.FightThreat(rig.World, mate.Slot, senses.SensesOf(mate.Slot));

        // Each one's desire as it starts, before the far one has run up: the mate's threat, x1 for the near one, x0.1 for the one 40 away.
        float? nearAt = null, farAt = null, nearThreat = null, farThreat = null;
        for (int t = 0; t < 32 && (nearAt is null || farAt is null); t++)
        {
            rig.World.RunTick();
            if (nearAt is null && rig.Task(near) == CharacterTask.Attack)
                (nearAt, nearThreat) = (ProtectDesire(rig, near), Threat());
            if (farAt is null && rig.Task(far) == CharacterTask.Attack)
                (farAt, farThreat) = (ProtectDesire(rig, far), Threat());
        }

        Assert.True(nearAt is not null && farAt is not null, "both join the mate's fight");
        Assert.Equal("15 Protect allies", rig.Cold(far).Decision!.Winner);
        Assert.Equal("15 Protect allies", rig.Cold(near).Decision!.Winner);
        Assert.True(nearThreat > 0, "the attacked mate has a fight threat");
        // desire (the first ally above 0: the mate, or the far fighter once it fights, at 0.1) x factor (the best, the mate at about 1)
        Assert.InRange(nearAt!.Value, 0.05f * nearThreat!.Value * nearThreat.Value, 1.01f * nearThreat.Value * nearThreat.Value);
        Assert.InRange(farAt!.Value, 0.005f * farThreat!.Value * farThreat.Value, 0.25f * farThreat.Value * farThreat.Value);
    }

    /// <summary>
    /// A player character's own list starts with its faction's <c>AI Goals</c> (<c>setupAI</c> <c>FUN_140621b70</c>, <c>AiSystem.PlayerOwnGoals</c>):
    /// the player's faction 204 Nameless lists 15 Protect allies (AI_TASK 957, weight 1; docs/game/ai-tasks.md "Combat goals"), so a player character's
    /// mind holds goal 15 at weight 1, ahead of 243 Getting food. Catches the faction's list going missing from a player mind, which only the protect
    /// fight above would show, and only as a fight not joined.
    /// </summary>
    [Fact]
    public void A_player_character_holds_goal_15_from_its_factions_list()
    {
        using var rig = Quiet(senses: true);
        var goals = rig.Cold(rig.Mate).Ai!.RaceGoals;

        Assert.Equal(TaskType.ProtectAllies, goals[0].Task.Type);
        Assert.Equal(1f, goals[0].Weight);
        Assert.Single(goals, g => g.Task.Type == TaskType.ProtectAllies);
    }

    /// <summary>
    /// An order with no desire leaves the queue at the order step and the chooser goes on: a First Aid order without a medkit (desire 0,
    /// <c>FUN_140998200</c>) is dropped and its character defends itself. Catches a kitless order that keeps its character from its goals.
    /// </summary>
    [Fact]
    public void An_order_with_no_desire_falls_through_to_self_defence()
    {
        using var rig = Quiet();
        var f = rig.Fighter;
        var inventory = rig.Cold(f).Inventory!;

        Assert.True(inventory.Remove(inventory.All().First(i => i.ItemFunction == ItemInfo.FirstAidKitFunction)));

        rig.Say(new TreatOrder([f], rig.Mate) { Tick = rig.World.Tick });
        rig.World.RunTick();

        Assert.Empty(rig.Cold(f).OrderQueue);

        var foe = Attacker(rig);
        RunUntil(rig, () => rig.Task(f) == CharacterTask.Attack, 120, "fighting back");

        Assert.Equal(foe.Slot, rig.Combat.StateOf(f).TargetSlot);
    }

    /// <summary>
    /// An order beats a job and the job waits while it runs: a character operating on its job rests at once when ordered and is not taken back to the
    /// well at the re-asks. Catches a job that takes a character off its order.
    /// </summary>
    [Fact]
    public void An_order_beats_a_job()
    {
        using var rig = Quiet();
        var f = rig.Fighter;
        rig.Say(new AddJob([f], TaskType.OperateMachinery, rig.WellSite) { Tick = rig.World.Tick });
        RunUntil(rig, () => rig.Task(f) == CharacterTask.Operate, 600, "operating the well");
        rig.Say(new RestOrder([f]) { Tick = rig.World.Tick });
        rig.World.RunTick();

        Assert.Equal(CharacterTask.Rest, rig.Task(f));

        rig.World.RunTicks(3 * ProductionSystem.JobCheckTicks);

        Assert.Equal(CharacterTask.Rest, rig.Task(f));
        Assert.Equal(TaskSource.Order, rig.Cold(f).Running!.Source);
    }

    /// <summary>
    /// The jobs switch (<c>FUN_14050c1c0</c>): turned off, it stops the operate job (87) the character runs and lets the medic job (58, one of 16,
    /// 31, 45, 57, 58, 250) below it run. Catches a switch that stops every job, or none.
    /// </summary>
    [Fact]
    public void The_jobs_switch_off_stops_jobs_but_the_listed_ones()
    {
        using var rig = Quiet();
        var f = rig.Fighter;
        rig.Say(new AddJob([f], TaskType.OperateMachinery, rig.WellSite) { Tick = rig.World.Tick });
        rig.Say(new MedicJobCommand([f]) { Tick = rig.World.Tick + 1 });
        RunUntil(rig, () => rig.Task(f) == CharacterTask.Operate && rig.Cold(f).Jobs.Count == 2, 600, "operating the well (the higher job)");
        rig.Say(new OrdersCommand([f], CharacterOrders.Jobs, On: false) { Tick = rig.World.Tick });
        RunUntil(rig, () => rig.Task(f) == CharacterTask.Treat, 2 * ProductionSystem.JobCheckTicks + 2, "the medic job running with the switch off");

        Assert.Equal((TaskType.JobMedic, 1), (rig.Cold(f).Running!.Row, rig.Cold(f).Running!.Job));
        Assert.Contains($"5 Jobs [0] {(int)TaskType.OperateMachinery}: jobs switch off", rig.Cold(f).Decision!.Lines);
    }

    /// <summary>
    /// A plain material (item function 0) for the implicit ditch job 242 (Ditch items, on in a new game) to carry to the rig's store.
    /// </summary>
    static void GiveWater(TaskStartTests.Rig rig) =>

        Assert.True(rig.Cold(rig.Fighter).Inventory!.Add(new ItemInstance { Record = "50-t", Name = "Water", Type = Data.Fcs.FcsRecordType.Item,
                Quantity = 1 }));

    static bool Ditching(CharacterCold cold) => cold.Running is { Implicit: true, Row: TaskType.DitchAllResources };

    /// <summary>
    /// An implicit job beats idleness (step 5 after the list, before step 6): an idle character carrying a material takes it to the store, and stands
    /// idle once it has none. Catches implicit jobs that wait for a schedule pass or a yield, or idleness that wins over them.
    /// </summary>
    [Fact]
    public void An_implicit_job_beats_idleness()
    {
        using var rig = Quiet();
        var f = rig.Fighter;
        GiveWater(rig);
        RunUntil(rig, () => Ditching(rig.Cold(f)), 2 * ProductionSystem.JobCheckTicks + 2, "ditching the water");

        Assert.Equal(CharacterTask.Haul, rig.Task(f));
        Assert.Equal("Implicit jobs", rig.Cold(f).Decision!.Winner);

        RunUntil(rig, () => rig.Task(f) == CharacterTask.Idle && rig.Cold(f).Decision is { Winner: "14 Idleness" }, 1200,
                "idle once the water is stored");

        Assert.DoesNotContain(rig.Cold(f).Inventory!.All(), i => i.Record == "50-t");
    }

    /// <summary>
    /// A list job beats an implicit job: a character ditching its water is taken off the trip by the operate job given to it, and is not sent back
    /// while it operates. Catches an implicit job that outranks the list or comes back over a running list job.
    /// </summary>
    [Fact]
    public void A_list_job_beats_an_implicit_job()
    {
        using var rig = Quiet();
        var f = rig.Fighter;
        GiveWater(rig);
        RunUntil(rig, () => Ditching(rig.Cold(f)), 2 * ProductionSystem.JobCheckTicks + 2, "ditching the water");
        rig.Say(new AddJob([f], TaskType.OperateMachinery, rig.WellSite) { Tick = rig.World.Tick });
        RunUntil(rig, () => rig.Task(f) == CharacterTask.Operate, 2 * ProductionSystem.JobCheckTicks + 2, "the operate job taking over");

        Assert.Equal("Jobs", rig.Cold(f).Decision!.Winner);

        for (int t = 0; t < 3 * ProductionSystem.JobCheckTicks; t++)
        {
            rig.World.RunTick();

            Assert.False(Ditching(rig.Cold(f)), "back to ditching while the operate job runs");
        }

        Assert.Equal(CharacterTask.Operate, rig.Task(f));
    }

    /// <summary>
    /// The urgent job pass (step 3, before Self preservation): a gunner on the Turret guard job (234) that senses an enemy keeps manning its turret
    /// while that enemy closes in to attack it, and fights it only once it is within 40 (146 needs !158). Catches a turret job that gives way to Self
    /// preservation as a plain job, or that never does.
    /// </summary>
    [Fact]
    public void An_urgent_turret_job_beats_self_defence_until_a_melee_threat()
    {
        using var rig = new OrderQueueTests.TurretRig(aggro: true);
        var (gunner, enemy) = (rig.Gunner, rig.Enemy);
        rig.World.Decisions.Tracing = true;
        rig.Say(new AddJob([gunner], TaskType.ManATurretPlayerJob, rig.Turret) { Tick = 0 });
        rig.RunUntil(() => rig.Task(gunner) == CharacterTask.Operate && rig.Cold(gunner).OperateSite == rig.Turret, 600, "manning the turret");
        rig.Say(new AttackOrder([enemy], gunner) { Tick = rig.World.Tick, Picked = true });
        bool held = false;
        rig.RunUntil(() =>
        {
            var gap = rig.World.Characters.Next[enemy.Slot].Position - rig.World.Characters.Next[gunner.Slot].Position;
            float d = MathF.Sqrt(gap.X * gap.X + gap.Z * gap.Z);
            if (d > 50 && d < 150 && rig.Cold(gunner).Decision is { Winner: "Urgent jobs" } && rig.Task(gunner) == CharacterTask.Operate)
                held = true;
            return rig.Task(gunner) == CharacterTask.Attack;
        }, 900, "the gunner fighting back");

        Assert.True(held, "the gunner kept its turret on the urgent job while the attacker was beyond 40");
        Assert.Equal(enemy.Slot, rig.World.SystemOf<CombatSystem>()!.StateOf(gunner).TargetSlot);
    }

    /// <summary>
    /// A player character the step decides, beside the rig's bed or in the open, with the stats a failed roll needs (<c>strong</c>: strength 100
    /// against a kidnapper's 0; else toughness).
    /// </summary>
    static CharacterId Victim(TaskStartTests.Rig rig, float x, float z)
    {
        var victim = rig.Add(x, z, player: true);
        rig.Cold(victim).Stats![StatsEnumerated.Strength] = 100;
        rig.Cold(victim).Stats![StatsEnumerated.Toughness] = 100;
        return victim;
    }

    /// <summary>
    /// The target of a failed stealth knock-out (<c>Character::attackTarget</c>: a replan, then the chooser) whose attacker then stands idle does not
    /// fight it: the push puts the attacker on no list 32 Self preservation's scorer reads (<c>FUN_14050df10</c>), and a standing order keeps
    /// running. Catches a player target pushed into a fight by the push alone, or over its order.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_target_of_a_failed_stealth_ko_does_not_fight_an_attacker_that_stands_idle_and_keeps_its_order(bool resting)
    {
        using var rig = Quiet();
        var target = Victim(rig, 1300, 2300);
        var attacker = rig.Add(1330, 2300, player: false);
        rig.Cold(attacker).Orders |= CharacterOrders.Sneak;
        rig.Cold(attacker).Stats![StatsEnumerated.Assassination] = 0;
        if (resting) rig.Say(new RestOrder([target]) { Tick = rig.World.Tick });
        rig.World.RunTick();
        rig.World.Decisions.Tracing = true;
        rig.Say(new StealthKoOrder([attacker], target) { Tick = rig.World.Tick });
        var carry = rig.World.SystemOf<CarrySystem>()!;
        bool missed = false;
        for (int t = 0; t < 600 && !(missed && (rig.Task(target) == CharacterTask.Attack || resting && t > 0
                && rig.Cold(target).Decision is { Winner: "Orders" })); t++)
        {
            rig.World.RunTick();
            missed |= carry.Knockouts.Any(k => !k.Success);
        }

        Assert.True(missed, "the knock-out missed");

        rig.World.RunTicks(20);
        if (resting)
        {
            Assert.Equal(CharacterTask.Rest, rig.Task(target));
            Assert.False(rig.Cold(target).InCombat);

            return;
        }

        // The attacker stands idle after the miss, so it is on no threats list the target's chooser reads (the look's refill, FUN_140854370, wants
        // its fight aimed at the target): 32 is dropped before any plan (FUN_14050df10, list A !159 holding) and the push alone starts no fight.
        Assert.NotEqual(CharacterTask.Attack, rig.Task(target));
        Assert.False(rig.Cold(target).InCombat);
        Assert.DoesNotContain("32 Self preservation", rig.Cold(target).Decision!.Winner ?? "");
    }

    /// <summary>
    /// The NPC victim of a failed stealth knock-out whose attacker then stands idle in its view takes 32 Self preservation once it witnesses the
    /// Assault, not at the miss: the witness function pushes the culprit from the victim's own perception update after its sense pass cleared the
    /// list (<c>FUN_1408580d0</c> → <c>FUN_140854d10</c>), so a look that senses the culprit lists it. Catches a witnessed push that never reaches
    /// the chooser, or one that fights at the miss while the sneaker is unseen.
    /// </summary>
    [Fact]
    public void The_npc_victim_of_a_failed_stealth_ko_fights_its_idle_attacker_once_it_witnesses_the_assault()
    {
        using var rig = Quiet(senses: true);
        var crime = rig.World.SystemOf<CrimeSystem>()!;
        var carry = rig.World.SystemOf<CarrySystem>()!;
        (carry.Crimes, rig.Combat.Crimes, rig.World.SystemOf<AiSystem>()!.Crimes) = (crime, crime, crime);
        var target = rig.Add(1300, 2300, player: false);
        var attacker = rig.Add(1330, 2300, player: true);
        rig.Cold(attacker).Orders |= CharacterOrders.Sneak;
        rig.Cold(attacker).Stats![StatsEnumerated.Assassination] = 0;
        rig.World.RunTick();
        rig.Say(new StealthKoOrder([attacker], target) { Tick = rig.World.Tick });
        for (int t = 0; t < 600 && !carry.Knockouts.Any(k => !k.Success); t++) rig.World.RunTick();

        Assert.Contains(carry.Knockouts, k => !k.Success);

        // At the miss the sneaker behind it is not sensed again: no fight.
        rig.World.RunTicks(30);
        Assert.False(rig.Cold(target).InCombat);

        // Idle in its view, the sneaker is spotted, the Assault witnessed and 32 taken.
        var front = rig.World.Characters.Next[target.Slot].Position + new Vector3(0, 0, 12);
        rig.World.Characters.Place(attacker.Slot, front, MathF.PI);
        for (int t = 0; t < 300 && !rig.Cold(target).InCombat; t++) rig.World.RunTick();

        Assert.Equal(TaskType.SelfPreservation, rig.Cold(target).Ai?.Goal?.Task.Type);
        Assert.Equal(CharacterTask.Attack, rig.Task(target));
        Assert.Equal(attacker.Slot, rig.Combat.StateOf(target).TargetSlot);
    }

    /// <summary>
    /// The rig with the fighter enslaved to a master squad 300 units off whose Slave AI Goals list <paramref name="goal"/> (docs/game/ai-tasks.md
    /// "Obedient Slave"): the state setter gave it job 187, which the urgent pass runs.
    /// </summary>
    static (TaskStartTests.Rig Rig, CharacterId Master) Enslaved(TaskType goal = TaskType.FollowSlavemaster, bool flee = false)
    {
        var rig = new TaskStartTests.Rig(carrying: false, decide: true, slaves: true, ai: flee, flee: flee);
        rig.World.RunTicks(3);
        rig.Say(new StopCommand([rig.Fighter]) { Tick = rig.World.Tick });
        rig.World.RunTick();
        rig.Cold(rig.Fighter).Orders &= ~CharacterOrders.Sneak;
        rig.World.Decisions.Tracing = true;
        var follow = new AiGoal(new AiTask("t", "goal", goal, TaskPriority.NonUrgent, TaskTargetType.Specific, TaskEndEvent.Nothing), 0, 24, 1);
        var board = new Blackboard([new(0, new AiPackage { Id = "slaves", Name = "slaves", Roles = [[], [], [], [], [follow]] })]);
        board.Update();
        var squad = new Squad { Id = rig.World.Squads.NextId(), TemplateId = "m", Faction = 2, Ai = board };
        rig.World.Squads.Add(squad);
        var master = rig.Add(1300, 2000, player: false);
        rig.Join(master, squad);
        FixtureRegistry.Enslave(rig.World, rig.Cold(rig.Fighter), 2, squad.Id);
        return (rig, master);
    }

    /// <summary>
    /// An obeying slave's goal 35 Running away from its master's Slave list (<c>Task_Runaway</c> tests no faction, <c>FUN_14034b440</c>,
    /// <c>FUN_140336820</c>; the player-faction desire of slot 55 <c>FUN_1407916d0</c> is 0 while either arm works): a player slave with both arms
    /// stumps, attacked beside it, runs as an NPC does, its run the goal's task. Catches a player-faction character whose goal 35 never starts the
    /// flee step.
    /// </summary>
    [Fact]
    public void An_armless_player_slave_told_to_run_away_runs_as_goal_35()
    {
        var (rig, _) = Enslaved(TaskType.RunAway, flee: true);
        using var _ = rig;
        var f = rig.Fighter;
        var medical = rig.Cold(f).Medical!;
        medical.Parts[LArm].Limb = medical.Parts[RArm].Limb = LimbState.Stump;
        var at = rig.World.Characters.Next[f.Slot].Position;
        var foe = rig.Add(at.X + 12, at.Z, player: false);
        rig.Cold(foe).Faction = 0;
        rig.Say(new AttackOrder([foe], f) { Tick = rig.World.Tick });
        RunUntil(rig, () => rig.Cold(f).Fleeing, 300, "running away");

        Assert.Equal((TaskType.RunAway, TaskSource.Goal), (rig.Cold(f).Running!.Row, rig.Cold(f).Running!.Source));
        Assert.Equal(TaskType.RunAway, rig.Cold(f).Ai!.Goal!.Task.Type);
    }

    /// <summary>
    /// A Bodyguard job's follow row plans its fight for its charge first (45 needs <c>!16 !39</c>, provided by 13 Attacking attackers of, whose chain
    /// to 4 reads neither Passive nor sneaking, <c>FUN_1405a4260</c>; docs/game/ai-tasks.md "Precedence"): a Passive bodyguard fights its charge's
    /// attacker under its job's row, and once that attacker is down follows its charge again. Catches Passive keeping a bodyguard out of its
    /// charge's fight, or a bodyguard that does not take its follow up again.
    /// </summary>
    [Fact]
    public void A_passive_bodyguard_joins_its_charges_fight_and_follows_again_after_it()
    {
        using var rig = Quiet(senses: true);
        var (guard, charge) = (rig.Fighter, rig.Mate);
        rig.Cold(guard).Orders |= CharacterOrders.Passive;
        rig.Cold(rig.Enemy).Medical!.KnockOut(100000);   // the rig's own enemy would make the guard a target of its own
        rig.Say(new AddOrderJob([guard], TaskType.Bodyguard, charge) { Tick = rig.World.Tick });
        RunUntil(rig, () => rig.Task(guard) == CharacterTask.Follow, 2 * ProductionSystem.JobCheckTicks + 2, "guarding");
        var foe = AttackMate(rig);
        RunUntil(rig, () => rig.Task(guard) == CharacterTask.Attack, 300, "defending its charge");

        Assert.Equal(foe.Slot, rig.Combat.StateOf(guard).TargetSlot);
        Assert.Equal((TaskType.Bodyguard, TaskSource.Job), (rig.Cold(guard).Running!.Row, rig.Cold(guard).Running!.Source));

        rig.Cold(foe).Medical!.KnockOut(100000);
        RunUntil(rig, () => rig.Task(guard) == CharacterTask.Follow, 300, "following again");

        Assert.Equal(charge, rig.Cold(guard).FollowOrder);
        Assert.True(Orders.Has(rig.Cold(guard), CharacterOrders.Passive));
    }
}
