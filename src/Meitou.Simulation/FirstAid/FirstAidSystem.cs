using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.FirstAid;

/// <summary>
/// First aid and resting in play (docs/simulation.md "Bodies", docs/game/character-stats.md "First aid, medkits and robot repair" and "Sleep, beds
/// and resting").
/// <list type="bullet">
/// <item><b>Treat</b> (<see cref="CharacterTask.Treat"/>): <see cref="TreatOrder"/> (the player), the medic job and the NPC rule start it.
/// <c>Schedule</c> walks the medic to the patient
/// (the pursuit system's pattern: a path at free speed, asked again when the patient has moved) and, in reach, asks the patient to stand still
/// (a request of 62 on its platoon's blackboard, <see cref="Ai.BoardRequests"/>); <c>Act</c> (own slot, read-only on the patient) emits one effect per tick while the medic is in
/// reach, awake, not fighting and still, and the patient is still or down; <c>Apply</c> (serial commit, in target / source order) picks the kit,
/// bandages the patient, drains the kit and trains the medic. So nothing on another character is written in a parallel phase.</item>
/// <item><b>Rate</b> (<b>Observed</b>, character-stats.md): <c>lerp(1, 15, min(skill, kit quality) x 0.01) x medic speed mult</c> (x <c>robot medic
/// speed mult</c> for a robot kit) per unit of <c>dt</c> on the first part with room (the next only once it fills), after any open wound is closed;
/// the kit's <c>charges</c> fall by <c>lerp(medkit drain 99, medkit drain 1, (100 - skill) / 100) x dt</c> per wound step or part bandaged (bandaging
/// drains only the player's kits; character-stats.md "First aid update"). The skill is the medic's <c>medic</c> (first aid kit) or <c>robotics</c>
/// (robot kit) (raw, x0.8 on oneself: the original reads the CharStats field with no injury or hunger multiplier, also for splints).
/// <c>dt</c> is the character's update time, the scaled frame time summed over the frames it was not visited (<c>FUN_1405cf130</c>, <b>Verified</b>),
/// so <see cref="World.TickSeconds"/> in the long run.</item>
/// <item><b>Auto</b> (<c>SlowWorld</c>, each NPC every <see cref="FirstAidSettings.AutoEveryTicks"/> ticks on its slot's turn, serial in slot order):
/// an idle, wandering or following NPC looks for a patient with the original's Medic finder (<see cref="FindPatient"/>, FirstAidSystem.Finder.cs), as
/// the decision step asks for the player characters' medic, robotics and splint jobs (<see cref="RegisterSources"/>).</item>
/// <item><b>Rest</b> (<see cref="CharacterTask.Rest"/>): <see cref="RestOrder"/>; <see cref="BodySystem"/> reads the task as
/// <see cref="MedicalContext.Resting"/>. Lying in a bed is
/// <see cref="Locks.BedSystem"/>'s (<see cref="CharacterTask.Sleep"/>); <see cref="BodySystem"/> gives it the bed's healing.</item>
/// </list>
/// Placed after the movement system (a later move order replaces a treatment, since it sets the task) and before the body system.
/// </summary>
public sealed partial class FirstAidSystem(GameConstants constants, BodyOptions options, ItemFactory items, FirstAidSettings? settings = null)
        : ITickSystem, ITaskOwner, ITaskHandler, IOrderOwner, IDecisionOwner, IGoalSource
{
    #region State

    /// <summary>
    /// The effect kind: one tick of treatment from the source (medic) on the target (patient).
    /// </summary>
    public const int EffectTreat = 1;
    /// <summary>
    /// Untreated damage (HP) under which a treatment in progress counts as finished.
    /// </summary>
    const float Done = 1e-3f;
    /// <summary>
    /// The kit quality closing a wound without a kit (<c>FUN_14064d800</c>).
    /// </summary>
    const float KitlessQuality = 5;

    /// <summary>
    /// The first-aid settings.
    /// </summary>
    public FirstAidSettings Settings { get; } = settings ?? new FirstAidSettings();
    /// <summary>
    /// The game constants.
    /// </summary>
    public GameConstants Constants { get; } = constants;
    /// <summary>
    /// The item factory.
    /// </summary>
    public ItemFactory Items { get; } = items;
    /// <summary>
    /// The relation event of bandaging an unconscious patient of another faction (<see cref="FactionDeeds.FirstAid"/>); null: none.
    /// </summary>
    public FactionDeeds? Deeds { get; set; }
    /// <summary>
    /// The XP of the treatments.
    /// </summary>
    readonly XpService _xp = new(constants, options);
    /// <summary>
    /// The body options.
    /// </summary>
    readonly BodyOptions _bodyOptions = options;

    #endregion

    #region Orders

    /// <summary>
    /// The <see cref="OrderEntry.Detail"/> of the Medic button's one-shot order (the job's finder looked at once, then a First aid 25).
    /// </summary>
    static readonly object MedicButton = new();
    /// <summary>
    /// The same with Robotics 57 given too: the look takes robots as well.
    /// </summary>
    static readonly object RoboticsButton = new();
    /// <summary>
    /// Dialogue events 18-22 (EV_HEALING_OTHER_START ... EV_FIRSTAID_KIT_EMPTY).
    /// </summary>
    const int HealingOtherStart = 18, BeingHealedStart = 19, HealingOtherFinished = 20, BeingHealedFinished = 21, KitEmpty = 22;

    /// <summary>
    /// The first-aid orders: treat, splint, rest and the medic job commands.
    /// </summary>
    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        var table = world.Characters;
        foreach (var command in commands)
        {
            switch (command)
            {
                case TreatOrder order:
                    if (!table.TryResolveNext(order.Patient, out int patient) || table.Cold(patient)!.Medical is not { Dead: false })
                        break;

                    foreach (var id in Who(world, order.Medics))
                    {
                        if (table.TryResolveNext(id, out int slot))
                            PlayerOrders.Give(world, slot, new OrderEntry(TaskType.FirstAidOrder, this) { Target = order.Patient }, order.Queued);
                    }

                    break;
                case SplintOrder splint:
                    if (!table.TryResolveNext(splint.Patient, out int splinted) || table.Cold(splinted)!.Medical is not { Dead: false })
                        break;

                    foreach (var id in Who(world, splint.Medics))
                    {
                        if (table.TryResolveNext(id, out int slot))
                            PlayerOrders.Give(world, slot, new OrderEntry(TaskType.SplintOrder, this) { Target = splint.Patient }, splint.Queued);
                    }

                    break;
                case RestOrder rest:
                    foreach (var id in Who(world, rest.Characters))
                    {
                        if (table.TryResolveNext(id, out int slot))
                            PlayerOrders.Give(world, slot, new OrderEntry(TaskType.Rest, this), rest.Queued);
                    }

                    break;
                case MedicJobCommand job:
                    PressMedic(world, job);
                    break;
            }
        }
    }

    /// <summary>
    /// The Medic button (<c>FUN_1407fac20</c>, <b>Verified</b>; docs/game/ui-input.md): Medic 58 for the characters, and Robotics 57 too when the
    /// active squad has a robot (<see cref="SquadNeedsRobotics"/>). With Shift each becomes a job (58 first); else one look for a patient, with the
    /// Robotics finder when 57 was given (its finder takes everyone the Medic's does, robots too).
    /// </summary>
    void PressMedic(World world, MedicJobCommand job)
    {
        var table = world.Characters;
        bool robotics = SquadNeedsRobotics(world);
        foreach (var id in Who(world, job.Characters))
        {
            if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { } cold) continue;
            if (!job.Job)
            {
                var detail = robotics ? RoboticsButton : MedicButton;

                // the button: one look for a patient
                PlayerOrders.Give(world, slot, new OrderEntry(TaskType.None, this) { Detail = detail }, job.Queued);
                continue;
            }

            Production.PermaJobs.Give(cold, TaskType.JobMedic, Production.PermaJob.Untargeted(TaskType.JobMedic));
            if (robotics)
                Production.PermaJobs.Give(cold, TaskType.JobRepairRobot, Production.PermaJob.Untargeted(TaskType.JobRepairRobot));
        }
    }

    /// <summary>
    /// Whether the Medic button gives Robotics 57 too (<c>FUN_1407fac20</c>, <b>Verified</b>): a member of the player's active squad
    /// (<see cref="Dialogue.DialogueSystem.ActiveSquad"/>) is of a robot race or holds an item of function 12 (a robot repair kit) in its own
    /// inventory, the backpack's contents not searched (<c>FUN_14004b84e</c> on the flat item list).
    /// </summary>
    static bool SquadNeedsRobotics(World world)
    {
        if (Dialogue.DialogueSystem.ActiveSquad(world) is not { } squad) return false;
        var table = world.Characters;
        foreach (var member in squad.Members)
        {
            if (!table.TryResolveNext(member, out int slot) || table.Cold(slot) is not { } cold) continue;
            if (cold.Race?.IsRobot == true) return true;
            if (cold.Inventory is { } inventory && inventory.Items.Exists(item => item.ItemFunction == ItemInfo.RobotKitFunction))
                return true;
        }

        return false;
    }

    /// <summary>
    /// First aid 25 and Splint 249 need the patient alive (else the order leaves the queue) and an able medic with a kit (a medkit or a robot kit
    /// anyone of its squad near it carries; its own splint kit) with a working arm (<see cref="HasWorkingArm"/>), Rest an able character; the Medic
    /// button's look anyone's (the button's 58 / 57 is a plain order in the queue: <c>FUN_1405d20d0</c> tests the faction only on its permanent path;
    /// the finder decides).
    /// The failure texts are the orders' refusals.
    /// </summary>
    OrderCheck IOrderOwner.CanRun(World world, int slot, OrderEntry order)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        if (order.Detail == MedicButton || order.Detail == RoboticsButton) return OrderCheck.Run;

        if (order.Row == TaskType.Rest) return CanAct(cold) ? OrderCheck.Run : OrderCheck.Blocked();
        if (!table.TryResolveNext(order.Target, out int patient) || table.Cold(patient)!.Medical is not { Dead: false })
            return OrderCheck.Gone;

        if (cold is not { Race: not null, Stats: not null } || !CanAct(cold) || !HasWorkingArm(cold))
            return OrderCheck.Blocked();
        if (order.Row == TaskType.SplintOrder)
        {
            // the medic's own kit only (the original's kit search)
            return OwnKit(cold, ItemInfo.SplintKitFunction) is null ? OrderCheck.Blocked("I don't have a splint kit.") : OrderCheck.Run;
        }

        return FindKit(world, slot, cold, ItemInfo.FirstAidKitFunction) is null && FindKit(world, slot, cold, ItemInfo.RobotKitFunction) is null
            ? OrderCheck.Blocked("I don't have a medkit.")   // refused before the medic moves
            : OrderCheck.Run;
    }

    /// <inheritdoc/>
    void IOrderOwner.Start(World world, int slot, OrderEntry order)
    {
        if (order.Detail == MedicButton || order.Detail == RoboticsButton)
        {
            HealSomeone(world, slot, SquadGoals.Of(world), robotics: order.Detail == RoboticsButton, TaskSource.Order);
            return;
        }

        if (order.Row == TaskType.Rest)
        {
            TaskStart.Start(world, slot, new TaskRequest(CharacterTask.Rest, TaskType.Rest, TaskSource.Order));
            return;
        }

        TaskStart.Start(world, slot, new TaskRequest(CharacterTask.Treat, order.Row, TaskSource.Order)
        {
            Target = order.Target,
            Resume = TaskStart.ResumeOf(world, slot, CharacterTask.Treat, CharacterTask.Idle),
        });
    }

    /// <summary>
    /// A started treatment order is planned again each update (<c>FUN_14050d970</c> re-tests the running row's list B): a medic whose arms both go
    /// out stops and the order waits queued (<see cref="HasWorkingArm"/>).
    /// </summary>
    bool IOrderOwner.StillHolds(World world, int slot, OrderEntry order) =>
        order.Row == TaskType.Rest || HasWorkingArm(world.Characters.Cold(slot)!);

    /// <summary>
    /// The characters an order names, else the player's selection.
    /// </summary>
    static IReadOnlyList<CharacterId> Who(World world, IReadOnlyList<CharacterId> ids) => ids.Count > 0 ? ids : world.Player.Selection.ToArray();

    /// <summary>
    /// A character that can be ordered to treat or rest: it has a medical state, is awake and not carried. Orders outrank Self preservation and First
    /// aid 25 and Splint 249 test no threat (docs/game/combat.md "Fight lifecycle and orders"): a fighting character's fight ends with the order's
    /// new task (<see cref="TaskStart.Start"/>).
    /// </summary>
    static bool CanAct(CharacterCold cold) => cold.Medical is { Incapacitated: false } && cold.CarriedBy.IsNone;

    /// <summary>
    /// State 38 <c>hasOneWorkingArm</c> (evaluator <c>FUN_140998470</c>, <b>Verified</b>): an animal always; anyone else awake, alive and with
    /// either arm working (<see cref="MedicalState.ArmWorks"/>). The treatment rows 25 First aid, 60 Repairing and 249 Splinting need it in list B
    /// and no row provides it (<b>Verified</b>, registration <c>FUN_140319a60</c>): no order, job or NPC look starts a treatment without it, so a
    /// medic with both arms out (a robot one too) treats no one, itself included.
    /// </summary>
    internal static bool HasWorkingArm(CharacterCold cold) =>
        cold.Animal is not null
        || Awake(cold.Medical) && (cold.Medical!.ArmWorks(left: true) || cold.Medical.ArmWorks(left: false));

    /// <summary>
    /// A medic awake for its treatments: alive and not knocked out, or playing dead (posture 3 is set only while the medical knocked-out and dead
    /// bytes <c>+0x161</c> / <c>+0x164</c> are clear, and the AI then updates as an awake one's, <c>FUN_1405d00b0</c> / <c>FUN_140598c20</c>,
    /// <b>Verified</b>), which <see cref="MedicalState.PlayingDead"/> keeps <see cref="MedicalState.Unconscious"/> to everyone else.
    /// </summary>
    internal static bool Awake(MedicalState? medical) => medical is { Dead: false } m && (!m.Unconscious || m.PlayingDead);

    /// <summary>
    /// Registers the treat and rest tasks.
    /// </summary>
    public void RegisterTasks(TaskHandlers tasks)
    {
        tasks.Register(CharacterTask.Treat, this);
        tasks.Register(CharacterTask.Rest, this);
    }

    /// <summary>
    /// A treatment of <see cref="TaskRequest.Target"/> (rows 249 and 250 splint), or the Rest order's sitting down.
    /// </summary>
    void ITaskHandler.Start(World world, int slot, in TaskRequest request)
    {
        if (request.Task == CharacterTask.Treat)
        {
            Begin(world, slot, request.Target, splint: request.Row is TaskType.SplintOrder or TaskType.SplintJob);
            return;
        }

        var table = world.Characters;
        var cold = table.Cold(slot)!;
        TaskStart.Stand(table, slot);
        table.Next[slot].Task = (byte)CharacterTask.Rest;
        cold.TreatTarget = CharacterId.None;
        cold.TreatSplint = false;
    }

    /// <summary>
    /// A treatment that ends lets go of its patient and kit (the task it goes back to is the new start's, or its provenance's when it ends by
    /// itself).
    /// </summary>
    void ITaskHandler.End(World world, int slot, CharacterTask task, TaskEnd reason)
    {
        if (task == CharacterTask.Treat) LetGo(world.Characters.Cold(slot)!);
    }

    /// <summary>
    /// Starts a treatment (serial phases): the medic stands, then walks to the patient (the task it goes back to afterwards is in its provenance,
    /// <see cref="CharacterCold.Running"/>).
    /// </summary>
    static void Begin(World world, int slot, CharacterId patient, bool splint = false)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        if (cold.TreatTarget != patient) Say(world, slot, patient, HealingOtherStart, BeingHealedStart);
        ref var n = ref table.Next[slot];

        // a new treatment looks for its own kit
        if (cold.TreatTarget != patient || cold.TreatSplint != splint || n.Task != (byte)CharacterTask.Treat) cold.TreatKit = null;
        TaskStart.Stand(table, slot);
        n.Task = (byte)CharacterTask.Treat;
        n.Mode = SpeedMode.Free;
        n.TaskTime = 0;
        cold.TreatTarget = patient;
        cold.TreatSplint = splint;
        cold.PathFailed = false;   // a failure from before is not this walk's
    }

    /// <summary>
    /// The treatment's dialogue events (<c>Task_FirstAid</c> start and finish, <c>FUN_14034ecf0</c> / <c>FUN_1403563a0</c>): the medic fires
    /// <paramref name="medicEvent"/> at the patient and a patient that is awake fires <paramref name="patientEvent"/> back (-1: none); nothing for a
    /// medic treating itself. Said from the next tick.
    /// </summary>
    static void Say(World world, int medic, CharacterId patient, int medicEvent, int patientEvent = -1)
    {
        var table = world.Characters;
        var id = table.IdOf(medic);
        if (patient == id || patient.IsNone) return;
        world.Defer(new Dialogue.DialogueEvent(id, patient, medicEvent) { Tick = world.Tick + 1 });
        if (patientEvent >= 0 && table.TryResolveNext(patient, out int p) && table.Cold(p)?.Medical is { Incapacitated: false })
            world.Defer(new Dialogue.DialogueEvent(patient, id, patientEvent) { Tick = world.Tick + 1 });
    }

    /// <summary>
    /// Whether the character kneels at a treatment (task <see cref="CharacterTask.Treat"/>, <see cref="CharacterCold.TreatKneels"/>): its leg coming
    /// back up does not stand it up (no get-up hold, <c>CombatSystem</c>, no <c>standing up 3</c>, <see cref="AnimationSystem"/>); it stands when the
    /// treatment ends. <b>Observed</b> in the original for a character treating its own leg (docs/game/ai-tasks.md "Medic jobs", Fixing Legs).
    /// </summary>
    public static bool AtTreatment(CharacterCold cold, CharacterTask task) => task == CharacterTask.Treat && cold.TreatKneels;

    /// <summary>
    /// Ends a treatment by itself (serial phases; <see cref="TaskEnd.Done"/>): back to the task before it, path dropped.
    /// </summary>
    static void End(World world, int slot)
    {
        if (world.Characters.Next[slot].Task == (byte)CharacterTask.Treat) TaskStart.End(world, slot, TaskEnd.Done);
        else LetGo(world.Characters.Cold(slot)!);
    }

    /// <summary>
    /// Clears a character's treatment fields: kneeling, target, splint and kit.
    /// </summary>
    static void LetGo(CharacterCold cold)
    {
        cold.TreatKneels = false;
        cold.TreatTarget = CharacterId.None;
        cold.TreatSplint = false;
        cold.TreatKit = null;
    }

    /// <summary>
    /// Where a medic treats <paramref name="patient"/> from: a caged one's cage use node (in front of its door,
    /// <see cref="FixtureRegistry.ExitSpot"/>; the medic cannot stand in the cage), else where the patient is.
    /// </summary>
    static Vector3 TreatSpot(World world, CharacterCold patient, in CharacterHot at) =>
        patient is { IsImprisoned: true, InWhat: { } id } && world.Fixtures.Find(id) is { Kind: FixtureKind.Cage } cage
                && FixtureRegistry.ExitSpot(cage) is { } door
            ? door
            : at.Position;

    /// <summary>
    /// In reach of the treatment spot: within <see cref="FirstAidSettings.Reach"/> on X/Z and <see cref="FirstAidSettings.ReachHeight"/> in height
    /// (on its floor).
    /// </summary>
    bool InReach(in CharacterHot medic, Vector3 spot)
    {
        float dx = medic.Position.X - spot.X, dz = medic.Position.Z - spot.Z;
        return dx * dx + dz * dz <= Settings.Reach * Settings.Reach && MathF.Abs(medic.Position.Y - spot.Y) <= Settings.ReachHeight;
    }

    /// <summary>
    /// The desire of a treatment order on <paramref name="patient"/>, which the order step pops at 0 or less (docs/game/ai-tasks.md "Finished
    /// tests"; <b>Verified</b>): 25 First aid (<c>FUN_14099e0a0</c>) and 60 Repairing (<c>FUN_14099df10</c>, which first gives 0 while the medic
    /// itself is out or dead) are the patient's need of this medic (<see cref="Need"/>, <c>FUN_140998200</c>) with the kit flag of the order's
    /// kind: the medic's items or its backpack's hold a kit of that class (<see cref="HoldsKit"/>, charges not looked at). 0 for a patient that does
    /// not resolve.
    /// </summary>
    internal float TreatOrderDesire(World world, int medic, CharacterId patient, bool robot)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(patient, out int p)) return 0;

        var cold = table.Cold(medic)!;
        if (robot && cold.Medical is { Incapacitated: true }) return 0;

        bool kit = HoldsKit(cold, FunctionOf(robot));
        return Need(world, medic, p, organicKit: !robot && kit, robotKit: robot && kit);
    }

    /// <summary>
    /// The desire of a splint order on <paramref name="patient"/> (249 Splinting, <c>FUN_14099dc40</c>, <b>Verified</b>): 0 for a patient that does
    /// not resolve, a medic out or dead, a patient fighting; X = min(the medic's skill, the quality of the splint kit with the fewest charges above
    /// zero in the first of its items and its backpack's that holds one of the class, none: 0); <c>needsSplint</c>(X) (0.1 when a part qualifies, else
    /// 0 or less); a patient of another faction x the relation / 100, at least 0.05 (no ally test here).
    /// </summary>
    internal float SplintOrderDesire(World world, int medic, CharacterId patient)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(patient, out int p) || table.Cold(p) is not { Medical: { } med, Race: { } race } other) return 0;

        var cold = table.Cold(medic)!;
        if (cold.Medical is not { Incapacitated: false } own || other.InCombat || cold.Stats is not { } stats) return 0;
        if (SplintKit(cold) is not { } kit) return 0;

        float skill = MathF.Min(stats[StatsEnumerated.Medic] * own.StatMultiplier(StatsEnumerated.Medic), Items.Info(kit.Record)?.Quality ?? 0);
        if (!med.NeedsSplint(race, skill)) return 0;
        if (cold.Faction == other.Faction) return SplintWeight;

        float relation = Relations is { } r && r.Contains(cold.Faction) && r.Contains(other.Faction) ? r.Get(cold.Faction, other.Faction) * 0.01f : 0;
        return SplintWeight * MathF.Max(relation, TakenWeight);
    }

    #endregion

    #region Walking to the patient

    /// <summary>
    /// The weight of the request to stand still a treating medic files (<c>DAT_14169bb48</c>, <b>Verified</b>).
    /// </summary>
    const float StandStillWeight = 1.4f;

    /// <summary>
    /// The walk to the patient: each medic on a treatment walks to the treatment spot, or stands still in reach; a hopeless way ends the treatment.
    /// </summary>
    public void Schedule(World world)
    {
        var table = world.Characters;
        var next = table.Next;
        float dt = world.TickSeconds;
        for (int i = 0; i < table.HighWater; i++)
        {
            ref var n = ref next[i];
            if (!n.Alive || n.Task != (byte)CharacterTask.Treat) continue;
            var cold = table.Cold(i)!;
            if (!Awake(cold.Medical) || cold.InCombat
                || !table.TryResolveNext(cold.TreatTarget, out int t) || table.Cold(t)!.Medical is not { Dead: false })
            {
                End(world, i);
                continue;
            }

            if (ArmsOut(world, i, cold)) continue;
            if (OwnLegsStepEnds(world, cold, i, t))
            {
                End(world, i);
                continue;
            }

            const ushort path = (ushort)MoveFlags.AnyPath;
            var spot = TreatSpot(world, table.Cold(t)!, next[t]);
            if (t == i || InReach(next[i], spot))
            {
                // In reach (or treating itself): stand still and let the Act step work, and ask the patient to stand still too. The medic kneels only
                // while the patient keeps still (the original plays the clip in the first aid task's update, which runs once the target stands still,
                // row 25's state 42).
                bool patientStill = Speed2(table.Previous[t].Velocity) <= Settings.StillSpeed * Settings.StillSpeed;
                if (t != i) AskToStandStill(world, i, t, patientStill);
                cold.TreatKneels = !cold.Medical!.PlayingDead && (t == i || table.Cold(t)!.Medical!.Incapacitated || patientStill);
                if ((n.Flags & path) != 0)
                {
                    n.Flags = (ushort)(n.Flags & ~path);
                    n.PathCursor = 0;
                    cold.PathRequest = 0;
                }

                n.TaskTime = 0;
                continue;
            }

            cold.TreatKneels = false;
            n.TaskTime -= dt;
            var goal = new Vector2(spot.X, spot.Z);
            bool asking = (n.Flags & (ushort)(MoveFlags.NeedPath | MoveFlags.Pending)) != 0;

            // No way to the patient: no path, or one that ends short of the asked goal (the walkability answers an unreachable goal with the way to
            // the reachable point nearest it). The original's task walk reports it (notifyPathImpossible): the patient goes on the medic's path
            // failures for 20 s, which the finder skips, and the treatment ends. Without it a medic asked for the same hopeless path every half
            // second (issue #124: searches run to the iteration limit, ~280 MB/s of garbage).
            if (!asking
                    && (cold.PathFailed || (n.Flags & (ushort)MoveFlags.HasPath) != 0 && cold.Path.Length > 0
                            && EndsShort(MovementSystem.PathEnd(cold, n), n.Goal, spot.Y)))
            {
                cold.PathFailed = false;
                PursuitSystem.GiveUp(world, cold, table.IdOf(t));
                End(world, i);
                continue;
            }

            bool stale = (n.Flags & (ushort)MoveFlags.HasPath) == 0
                    || Vector2.DistanceSquared(goal, n.Goal) > PursuitSystem.RepathDistance * PursuitSystem.RepathDistance;
            if (asking || !stale || n.TaskTime > 0) continue;
            n.Goal = goal;

            // the spot's floor: an upstairs patient is walked to up the stairs, not to the ground below it
            (cold.GoalHeight, cold.GoalHeightAt) = (spot.Y, goal);
            n.Mode = SpeedMode.Free;
            n.Flags |= (ushort)MoveFlags.NeedPath;
            n.TaskTime = PursuitSystem.RepathSeconds;
        }
    }

    /// <summary>
    /// The plan step ends a running treatment whose medic has lost both arms (<c>FUN_14050d970</c> re-tests list B, state 38,
    /// <see cref="HasWorkingArm"/>): a job's or an NPC's ends here; an order's is only sent to the decision step, whose order queue ends it and keeps
    /// the order waiting (<see cref="IOrderOwner.StillHolds"/>). True when the arms are out.
    /// </summary>
    static bool ArmsOut(World world, int i, CharacterCold cold)
    {
        if (HasWorkingArm(cold)) return false;
        if (cold.Running is { Source: TaskSource.Order }) DecisionSystem.Request(cold);
        else
            End(world, i);
        return true;
    }

    /// <summary>
    /// The first aid task's every update asks the patient to stand still (<c>Task_FirstAid</c> / <c>Task_FirstAidRig</c> /
    /// <c>Task_MakeTargetStandStill</c> slot 5 <c>FUN_1403346d0</c>, <b>Verified</b>; docs/game/ai-tasks.md "Medic jobs"): it files 62 Standing still at
    /// priority 3, weight 1.4, on the patient's platoon blackboard, for the patient, or for its carrier while it is carried (the call takes the
    /// carrier then). The splint's own update, which runs once the patient stands still (<paramref name="still"/>; before, row 64 asks as above),
    /// files it on the medic's own platoon blackboard for the patient, no carrier swap (<c>Task_FirstAidRig</c>'s update <c>FUN_14034ef20</c>,
    /// <b>Verified</b>), so a patient of another squad never reads it. The request competes in that character's priority-3 pick (<see cref="Ai.TierRungs"/>), so a patient stands still unless an
    /// order, an urgent job or a goal that scores higher wins, and not under a threat within 40 (62's list B <c>!39</c>). <see cref="Act"/> treats
    /// only a patient standing still.
    /// </summary>
    static void AskToStandStill(World world, int medic, int patient, bool still)
    {
        var table = world.Characters;
        var asker = table.IdOf(medic);
        if (still && table.Cold(medic)!.TreatSplint)
        {
            Ai.BoardRequests.FileOn(world, asker, asker, table.IdOf(patient), TaskType.StandStill, TaskPriority.Urgent, StandStillWeight);
            return;
        }

        var subject = table.Cold(patient)!.CarriedBy is { IsNone: false } carrier ? carrier : table.IdOf(patient);
        Ai.BoardRequests.FileOn(world, asker, subject, TaskType.StandStill, TaskPriority.Urgent, StandStillWeight);
    }

    /// <summary>
    /// A path whose last waypoint is farther than the reach from the goal on X/Z, or off the treatment spot's floor: the patient cannot be got to.
    /// </summary>
    bool EndsShort(Vector3 end, Vector2 goal, float spotHeight) =>
        Vector2.Distance(new Vector2(end.X, end.Z), goal) > Settings.Reach || MathF.Abs(end.Y - spotHeight) > Settings.ReachHeight;

    #endregion

    #region The treatment

    /// <summary>
    /// The treatment step: each medic standing still with its patient in reach treats it (<see cref="EffectTreat"/>).
    /// </summary>
    public void Act(World world, Partition part, EffectBuffer effects)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        float still2 = Settings.StillSpeed * Settings.StillSpeed;
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive || prev[i].Dormant || !world.Steps(i) || next[i].Task != (byte)CharacterTask.Treat)
                continue;

            var cold = table.Cold(i)!;
            if (!Awake(cold.Medical) || cold.InCombat || cold.Medical!.Aggregates().BothArmsDisabled
                    || cold.StaggeredUntil > world.Tick)
                continue;

            var target = cold.TreatTarget;
            if (!table.IsAlive(target)) continue;
            int t = target.Slot;
            if (table.Cold(t) is not { Medical: { Dead: false } patient }) continue;
            if (Speed2(prev[i].Velocity) > still2) continue;
            if (t != i)
            {
                if (!InReach(prev[i], TreatSpot(world, table.Cold(t)!, prev[t]))) continue;
                if (!patient.Incapacitated && Speed2(prev[t].Velocity) > still2) continue;
            }

            effects.Emit(i, t, EffectTreat);
        }
    }

    /// <summary>
    /// The squared speed on the ground plane.
    /// </summary>
    static float Speed2(Vector3 v) => v.X * v.X + v.Z * v.Z;

    /// <summary>
    /// Serial, in (patient, medic) order: the kit, the bandaging, the drain and the medic's XP. Ends the treatment when nothing is left to bandage or
    /// no kit fits.
    /// </summary>
    public void Apply(World world, in Effect effect)
    {
        if (effect.Kind != EffectTreat) return;
        var table = world.Characters;
        int m = effect.Source, p = effect.Target;
        if (!table.Next[m].Alive || table.Next[m].Task != (byte)CharacterTask.Treat) return;
        var medicCold = table.Cold(m)!;
        if (!table.TryResolveNext(medicCold.TreatTarget, out int t) || t != p) return;
        var patientCold = table.Cold(p)!;
        if (!Awake(medicCold.Medical) || medicCold is not { Medical: { } medical, Stats: { } stats, Race: { } medicRace }
                || patientCold is not { Medical: { Dead: false } body, Race: { } race })
        {
            End(world, m);
            return;
        }

        if (medicCold.TreatSplint)
        {
            Splint(world, m, medicCold, medical, stats, medicRace, body, race);
            return;
        }

        float dt = world.StepSeconds(m);   // a far medic's step covers the ticks since its last (the far cadence, World.StepTicks)

        // A medic not of the player's treats an imprisoned patient without a kit (Task_FirstAid FUN_1403563a0 skips the kit search; also for a
        // chained slave of the medic's faction, FUN_14032e2a0).
        bool kitless = !medicCold.IsPlayer && (patientCold.IsImprisoned || OwnedChained(medicCold, patientCold));
        var ctx = new MedicalContext(Constants, _bodyOptions, race);

        // Open wounds are closed first, newest first, with a kit matching the patient's race (none: quality 5); nothing is bandaged meanwhile
        // (FUN_14064d800). This drain is not gated.
        if (body.Wounds.Count > 0)
        {
            CloseWound(world, m, p, medicCold, stats, medicRace, body, race, dt, kitless);
            return;
        }

        if (kitless)
        {
            TreatKitless(world, m, p, medicCold, patientCold, stats, medicRace, body, race, dt);
            return;
        }

        // The first aid kit phase while an organic part has room and a first aid kit is found, else the robot repair kit phase (Task_FirstAid
        // FUN_1403563a0: a search that finds no first aid kit goes on to the robot phase, #884). Treatment runs until the last bit is bandaged (the
        // start threshold is MedicalState.FirstAidThreshold).
        bool organic = body.BandageRoom(race, false) > Done;
        bool robot = !organic || KitOf(world, m, medicCold, FunctionOf(false)) is null;
        if (robot && body.BandageRoom(race, true) <= Done)
        {
            if (organic) Say(world, m, medicCold.TreatTarget, KitEmpty);   // organic parts left, no first aid kit
            else Say(world, m, medicCold.TreatTarget, HealingOtherFinished, BeingHealedFinished);

            End(world, m);
            return;
        }

        if (KitOf(world, m, medicCold, FunctionOf(robot)) is not { } kit)
        {
            Say(world, m, medicCold.TreatTarget, KitEmpty);   // Task_FirstAidRig's empty kit (FUN_14034ef20)
            End(world, m);   // no kit of the right kind within reach
            return;
        }

        patientCold.StuckBolts = null;   // bolts go with the first update that finds no open wound (FUN_14064d800 -> FUN_14052bc10)

        float skill = SkillOf(stats, m == p, robot);
        float quality = Items.Info(kit.Item.Record)?.Quality ?? 0;
        float bandaged = body.Treat(dt, skill, quality, robot, ctx, out int touched);
        if (bandaged <= 0)
        {
            End(world, m);
            return;
        }

        // once per part bandaged; only the player's medics use up kits here (FUN_140649a00: faction +0x250)
        if (medicCold.IsPlayer) Drain(world, m, kit, skill, dt, robot, touched);
        medicCold.UseSkill(StatOf(robot), world.Tick);
        _xp.Medic(stats, race, medicRace, dt, StatOf(robot));

        // An unconscious patient's faction is grateful to the medic's (engine choice: every update that bandages; the original's update fires it only
        // when its loop got past the wounds).
        Deeds?.FirstAid(world, m, p, dt);
    }

    /// <summary>
    /// The skill and trained stat: medic with a first aid kit, robotics with a robot repair kit; treating oneself x0.8 (FUN_14033c5a0,
    /// Task_FirstAid's kit phase).
    /// </summary>
    static StatsEnumerated StatOf(bool robotKit) => robotKit ? StatsEnumerated.Robotics : StatsEnumerated.Medic;

    /// <summary>
    /// The medic's skill for a kit: the raw stat, no injury or hunger multiplier; x0.8 on itself.
    /// </summary>
    static float SkillOf(CharacterStats stats, bool self, bool robotKit) => stats[StatOf(robotKit)] * (self ? 0.8f : 1);

    /// <summary>
    /// One update of an open wound: closed with a kit matching the patient's race (none: quality 5), the kit drained, the medic's skill and XP.
    /// </summary>
    void CloseWound(World world, int m, int p, CharacterCold medicCold, CharacterStats stats, RaceData medicRace, MedicalState body, RaceData race,
        float dt, bool kitless)
    {
        var woundKit = kitless ? null : KitOf(world, m, medicCold, FunctionOf(race.IsRobot));
        if (woundKit is null && !kitless)
        {
            Say(world, m, medicCold.TreatTarget, KitEmpty);
            End(world, m);
            return;
        }

        float woundSkill = SkillOf(stats, m == p, race.IsRobot && !kitless);
        body.CloseWound(dt, woundSkill, woundKit is { } wk ? Items.Info(wk.Item.Record)?.Quality ?? 0 : KitlessQuality, Constants);
        if (woundKit is { } used) Drain(world, m, used, woundSkill, dt, race.IsRobot, 1);
        medicCold.UseSkill(StatOf(race.IsRobot && !kitless), world.Tick);
        _xp.Medic(stats, race, medicRace, dt, StatOf(race.IsRobot && !kitless));
    }

    /// <summary>
    /// One update without a kit: 1 HP a second, one part after another in part order as with a kit; nothing drains; ends once the parts are
    /// bandaged up.
    /// </summary>
    void TreatKitless(World world, int m, int p, CharacterCold medicCold, CharacterCold patientCold, CharacterStats stats, RaceData medicRace,
        MedicalState body, RaceData race, float dt)
    {
        patientCold.StuckBolts = null;   // bolts go with the first update that finds no open wound (FUN_14064d800 -> FUN_14052bc10)

        // Without a kit 1 HP a second goes to the parts, organic or robotic alike, in part order as with a kit, and nothing drains (FUN_140649a00).
        // Done as with a kit: once the parts are bandaged up, healing turns bandage into flesh and float dust leaves a sliver of room every update,
        // which would keep the treatment going for ever.
        if (body.BandageRoom(race, false) + body.BandageRoom(race, true) <= Done || body.Bandage(dt, null, race, out _) <= 0)
        {
            Say(world, m, medicCold.TreatTarget, HealingOtherFinished, BeingHealedFinished);
            End(world, m);
            return;
        }

        medicCold.UseSkill(StatsEnumerated.Medic, world.Tick);
        _xp.Medic(stats, race, medicRace, dt, StatsEnumerated.Medic);
        Deeds?.FirstAid(world, m, p, dt);
    }

    /// <summary>
    /// One update of a splint (task "Splinting", <c>FUN_140649d50</c>, docs/game/character-stats.md "Splints"): the medic's <c>medic</c> skill and
    /// the kit's quality set the rate and the cap; the kit drains as first aid's only when a part took rig; medic XP every update. Ends when no part
    /// is left to splint or no splint kit is in reach.
    /// </summary>
    void Splint(World world, int m, CharacterCold medicCold, MedicalState medical, CharacterStats stats, RaceData medicRace, MedicalState body,
            RaceData race)
    {
        var table = world.Characters;
        if (OwnKit(medicCold, ItemInfo.SplintKitFunction) is not { } kit)
        {
            Say(world, m, medicCold.TreatTarget, KitEmpty);   // the rig task's kit search finds none (FUN_14034ef20, event 22)
            End(world, m);
            return;
        }

        var item = kit.Item;
        float dt = world.TickSeconds;
        float skill = stats[StatsEnumerated.Medic];   // the raw stat (FUN_14034ef20 passes CharStats +0x98)
        float quality = Items.Info(item.Record)?.Quality ?? 0;
        float added = body.Splint(dt, skill, quality, new MedicalContext(Constants, _bodyOptions, race));
        if (added <= 0)
        {
            End(world, m);   // nothing left to splint
            return;
        }

        Drain(world, m, kit, skill, dt, false, 1);
        medicCold.UseSkill(StatsEnumerated.Medic, world.Tick);
        _xp.Medic(stats, race, medicRace, dt, StatsEnumerated.Medic);
    }

    #endregion

    #region Kits

    /// <summary>
    /// Uses <paramref name="times"/> drains of a kit (<see cref="MedicalState.KitDrain"/>) by medic <paramref name="m"/>; a used-up kit is gone.
    /// Observers hear the charges it had that the drain spent (<see cref="ItemUseKind.FirstAid"/>).
    /// </summary>
    void Drain(World world, int m, (ItemInstance Item, Inventory Owner) kit, float skill, float dt, bool robotKit, int times)
    {
        float amount = MedicalState.KitDrain(Constants, skill, dt, robotKit) * times;
        float spent = MathF.Min(amount, MathF.Max(kit.Item.Charges, 0));
        kit.Item.Charges -= amount;
        bool finished = kit.Item.Charges <= 0;
        if (finished) kit.Owner.RemoveOne(kit.Item);

        ItemUseObservers.Report(world, ItemUseKind.FirstAid, kit.Item.Record, world.Characters.IdOf(m), null, spent, finished);
    }

    /// <summary>
    /// The item function of a robot kit or a first-aid kit.
    /// </summary>
    static int FunctionOf(bool robot) => robot ? ItemInfo.RobotKitFunction : ItemInfo.FirstAidKitFunction;

    /// <summary>
    /// The kit a medic uses for splints, from its own inventory only (the original's kit search, <c>FUN_14033c740</c> at vtable +0x38 of the splint
    /// and first aid tasks, <b>Verified</b>): the main grid, else the worn backpack's contents; the item of the function with the fewest charges
    /// above zero (<see cref="Fewest"/>). Squad mates' kits are not searched.
    /// </summary>
    static (ItemInstance Item, Inventory Owner)? OwnKit(CharacterCold cold, int function)
    {
        if (cold.Inventory is not { } own) return null;
        if (Fewest(own.Items, function) is { } main) return (main, own);
        return Fewest(own.Backpack?.Contents?.Items, function) is { } worn ? (worn, own) : null;
    }

    /// <summary>
    /// The planner's <c>haveFirstAidKit</c> (state 23, <c>FUN_140598860</c>, <b>Verified</b>): the character's items, or else its backpack's, hold
    /// one of the kit class (<paramref name="function"/>); charges are not looked at.
    /// </summary>
    internal static bool HoldsKit(CharacterCold cold, int function)
    {
        if (cold.Inventory is not { } own) return false;
        return Holds(own.Items, function) || Holds(own.Backpack?.Contents?.Items, function);
    }

    /// <summary>
    /// Whether <paramref name="items"/> hold an item of the <c>item function</c> (<c>FUN_14004b84e</c>: flat, charges not looked at).
    /// </summary>
    static bool Holds(IEnumerable<ItemInstance>? items, int function)
    {
        if (items is null) return false;
        foreach (var item in items)
            if (item.Type == FcsRecordType.Item && item.ItemFunction == function) return true;
        return false;
    }

    /// <summary>
    /// The splint order's kit (<c>FUN_14099dc40</c>): the medic's items when they hold a splint kit, else its backpack's when they do; of those, the
    /// one with the fewest charges above zero (<see cref="Fewest"/>). Null when none.
    /// </summary>
    static ItemInstance? SplintKit(CharacterCold cold)
    {
        if (cold.Inventory is not { } own) return null;
        if (Holds(own.Items, ItemInfo.SplintKitFunction)) return Fewest(own.Items, ItemInfo.SplintKitFunction);

        var worn = own.Backpack?.Contents?.Items;
        return Holds(worn, ItemInfo.SplintKitFunction) ? Fewest(worn, ItemInfo.SplintKitFunction) : null;
    }

    /// <summary>
    /// The item of the function with charges above zero and the fewest of them, the first of equals (<c>FUN_140746480</c>, <b>Verified</b>: the
    /// search starts at 99999, so an item with 99999 charges or more never qualifies). Null when none.
    /// </summary>
    static ItemInstance? Fewest(IEnumerable<ItemInstance>? items, int function)
    {
        if (items is null) return null;
        ItemInstance? best = null;
        foreach (var item in items)
        {
            if (item.Type != FcsRecordType.Item || item.ItemFunction != function || item.Charges <= 0 || item.Charges >= 99999 || item.Quantity <= 0)
                continue;

            if (best is null || item.Charges < best.Charges) best = item;
        }

        return best;
    }

    /// <summary>
    /// The medic's own kit of an <c>item function</c> by the original's search (<see cref="OwnKit"/>: main grid, then the worn backpack; fewest
    /// charges above zero), else a squad mate's within <see cref="FirstAidSettings.KitReach"/> by the same rule (an engine choice: the original
    /// searches only the medic's own).
    /// </summary>
    (ItemInstance Item, Inventory Owner)? FindKit(World world, int slot, CharacterCold cold, int function)
    {
        if (OwnKit(cold, function) is { } mine) return mine;
        if (cold.SquadId < 0 || world.Squads.Find(cold.SquadId) is not { } squad) return null;
        var table = world.Characters;
        var here = table.Next[slot].Position;
        float reach2 = Settings.KitReach * Settings.KitReach;
        foreach (var m in squad.Members)
        {
            if (m.Slot == slot || !table.TryResolveNext(m, out int s) || table.Cold(s) is not { Inventory: { } mates, Medical: { Dead: false } })
                continue;

            var d = table.Next[s].Position - here;
            if (d.X * d.X + d.Z * d.Z > reach2) continue;
            if (OwnKit(table.Cold(s)!, function) is { } found) return found;
        }

        return null;
    }

    /// <summary>
    /// The kit a running treatment uses (<c>FUN_14033c7e0</c>, <b>Verified</b>): the one it took last time, until it is used up (no charges left) or
    /// no longer of the wanted function; only then <see cref="FindKit"/> searches again (the original also drops a kit no longer held; a kit lost
    /// another way is found again at once). Null: none.
    /// </summary>
    (ItemInstance Item, Inventory Owner)? KitOf(World world, int slot, CharacterCold cold, int function)
    {
        if (cold.TreatKit is { } kept && kept.Item.ItemFunction == function && kept.Item.Charges > 0 && kept.Owner.All().Contains(kept.Item))
            return kept;

        return cold.TreatKit = FindKit(world, slot, cold, function);
    }

    #endregion

    #region Treating by themselves

    /// <summary>
    /// Resolved on the first sweep: whose goal lists hold the Medic goal.
    /// </summary>
    AiSystem? _ai;

    /// <summary>
    /// The NPCs' automatic medic looks, each on its turn.
    /// </summary>
    public void SlowWorld(World world)
    {
        // Each NPC looks every AutoEveryTicks ticks, a slot's turn falling on its own tick (slot mod the period), so the looks spread over the ticks
        // instead of all landing on one (the original runs its AI updates 8 characters a frame in turn, FUN_140786e30).
        if (Settings.AutoEveryTicks <= 0) return;
        int every = world.Every(Settings.AutoEveryTicks);
        var table = world.Characters;
        var next = table.Next;

        // The treated list and the sweep are made on the turn's first NPC that weighs the Medic goal (a turn with none makes neither).
        SquadGoals? taken = null;
        try
        {
            for (int i = 0; i < table.HighWater; i++)
            {
                if (!next[i].Alive || !world.Turn(i, every)) continue;   // its turn (the far cadence, World.Turn)
                var cold = table.Cold(i)!;
                if (cold.IsPlayer || !Settings.AutoNpcs) continue;   // a player's medic jobs are the decision step's (MedicJobs)

                // The AI's own: its Medic goal weighs the patient found here (AiBehaviour.Medic). One whose lists hold no Medic goal does not treat,
                // nor does a character without a full mind: in the original a character treats only through goal 58, a job or an order.
                if (cold.Ai is not { Engine: false } mind) continue;
                if ((_ai ??= world.SystemOf<AiSystem>())?.ListsGoal(world, i, TaskType.JobMedic) != true)
                {
                    mind.MedicScore = 0;
                    continue;
                }

                if (taken is null)
                {
                    taken = SquadGoals.Of(world);
                    BeginSweep(world);
                }

                KeepPick(world, i, mind, taken);
            }
        }
        finally
        {
            EndSweep();
        }
    }

    /// <summary>
    /// The Medic goal's finder result for the think (<see cref="AiMind.MedicPatient"/>, <see cref="AiMind.MedicScore"/>; none and 0 without a
    /// patient).
    /// </summary>
    void KeepPick(World world, int i, AiMind mind, SquadGoals taken)
    {
        var (patient, score) = FindPatient(world, i, taken, robotics: false);
        mind.MedicPatient = patient >= 0 ? world.Characters.IdOf(patient) : CharacterId.None;
        mind.MedicScore = patient >= 0 ? score : 0;
    }

    /// <summary>
    /// Starts the treatment of each NPC whose think chose its Medic goal (<see cref="AiMind.MedicAsk"/>) or reached Fixing Legs' own step
    /// (<see cref="AiMind.HealLegsAsk"/>; serial, after the think, in slot order). The Medic finder runs again on the state of now, so a patient
    /// treated meanwhile is not walked to; one that finds none drops its kept pick, and the next think chooses again.
    /// </summary>
    internal void SendGoalTreatments(World world)
    {
        var table = world.Characters;
        SquadGoals? taken = null;
        bool swept = false;
        try
        {
            for (int s = 0; s < table.HighWater; s++)
            {
                if (table.Cold(s)?.Ai is not { } mind || !mind.MedicAsk && !mind.HealLegsAsk) continue;
                bool legs = mind.HealLegsAsk;
                mind.MedicAsk = false;
                mind.HealLegsAsk = false;
                if (!table.Next[s].Alive) continue;
                taken ??= SquadGoals.Of(world);
                if (legs)
                {
                    TreatOwnLegs(world, s, taken);
                    continue;
                }

                // the patient sweep only for a Medic ask (a self-treatment reads no other character's need)
                if (!swept)
                {
                    BeginSweep(world);
                    swept = true;
                }

                if (HealSomeone(world, s, taken, robotics: false, TaskSource.Npc)) continue;
                mind.MedicPatient = CharacterId.None;
                mind.MedicScore = 0;
            }
        }
        finally
        {
            EndSweep();
        }
    }

    /// <summary>
    /// Fixing Legs (269) acted out: <c>Task_FirstAid</c> on the character itself (a goal with no target hand treats its own character,
    /// <c>FUN_14034ecf0</c>, <b>Verified</b>), the same treatment as First aid (skill x0.8 on oneself, <see cref="SkillOf"/>). Started only while the
    /// planner's tests still hold now: the legs messed up (state 103), a first aid kit held (23), a working arm (38), awake, not carried and not
    /// fighting; otherwise the next think chooses again.
    /// </summary>
    static void TreatOwnLegs(World world, int s, SquadGoals taken)
    {
        var cold = world.Characters.Cold(s)!;
        if (!CanAct(cold) || !Awake(cold.Medical) || cold.InCombat || cold.Medical is not { LegsMessedUp: true }) return;
        if (!HasWorkingArm(cold) || !HoldsKit(cold, ItemInfo.FirstAidKitFunction)) return;

        StartJob(world, s, s, taken, new TaskRequest(CharacterTask.Treat, TaskType.HealMyLegs, TaskSource.Npc));
    }

    /// <summary>
    /// Whether a Fixing Legs treatment (<see cref="TreatOwnLegs"/>) ends before the treatment's own end (nothing left to close or bandage,
    /// <see cref="Apply"/>): row 269's list B no longer holds, as the plan step ends a running step whose list B fails (<c>FUN_14050d970</c>, row 269
    /// has no <c>+0x16</c>, <b>Verified</b> upstream): no first aid kit of its own (23; a squad mate's kit in reach does not count), an immediate
    /// threat on itself (39), deep water (139) or a weapon drawn (157); the arms (38) are <see cref="ArmsOut"/>'s. The leg holding again does not
    /// end it: in the original the character keeps bandaging until the leg is fully bandaged and stands only then (<b>Observed</b>, original 1.0.68,
    /// docs/game/ai-tasks.md "Medic jobs", Fixing Legs). The next think chooses again.
    /// </summary>
    static bool OwnLegsStepEnds(World world, CharacterCold cold, int i, int t)
    {
        if (t != i || cold.Running is not { Task: CharacterTask.Treat, Row: TaskType.HealMyLegs }) return false;
        if (!HoldsKit(cold, ItemInfo.FirstAidKitFunction)) return true;
        if (cold is { WaterState: 3, InSomething: HeldState.None } || cold.DrawnWeapon != WeaponKinds.None) return true;

        return RowStates.Holds(world, i, StateType.IsAggressionTowardsMe, default) == true;
    }

    /// <summary>
    /// The first aid jobs as the decision step's contributor (step 5; docs/game/ai-tasks.md "Medic jobs"): Medic 58 and Robotics 57 run while the
    /// finder (<see cref="FindPatient"/>) finds a patient, Splint Rigging 250 while its finder does (<see cref="FindSplint"/>); all three run with
    /// the jobs switch off. A job's treatment runs until it ends by itself.
    /// </summary>
    public void RegisterSources(DecisionSystem decisions)
    {
        var jobs = new MedicJobs(this);
        foreach (var type in (ReadOnlySpan<TaskType>)[TaskType.JobMedic, TaskType.JobRepairRobot, TaskType.SplintJob])
            decisions.RegisterJob(type, jobs);
    }

    /// <summary>
    /// The first aid jobs' contributor (<see cref="IJobSource"/>).
    /// </summary>
    sealed class MedicJobs(FirstAidSystem owner) : IJobSource
    {
        /// <summary>
        /// What the last <see cref="CanRun"/> found: the tick, the slot and job, and the patient (tick -1: nothing).
        /// </summary>
        (long Tick, int Slot, int K, int Patient) _found = (-1, -1, -1, -1);

        /// <summary>
        /// Whether medic job <paramref name="k"/> can run (or runs already): its finder must find a patient.
        /// </summary>
        public DecisionCheck CanRun(World world, int slot, int k)
        {
            var table = world.Characters;
            var cold = table.Cold(slot)!;
            var type = cold.Jobs[k].Type;
            if (cold.Running is { Source: TaskSource.Job } run && run.Job == k && run.Runs((CharacterTask)table.Next[slot].Task)
                    && run.Task == CharacterTask.Treat)
                return DecisionCheck.Runs;

            var taken = SquadGoals.Of(world);
            int patient = type == TaskType.SplintJob ? owner.FindSplint(world, slot, taken)
                    : owner.FindPatient(world, slot, taken, robotics: type == TaskType.JobRepairRobot).Slot;
            if (patient < 0) return DecisionCheck.No("no patient");
            _found = (world.Tick, slot, k, patient);
            return DecisionCheck.Run;
        }

        /// <summary>
        /// Starts the treatment of the patient <see cref="CanRun"/> found this tick.
        /// </summary>
        public void Start(World world, int slot, int k)
        {
            if (_found.Tick != world.Tick || _found.Slot != slot || _found.K != k) return;
            var type = world.Characters.Cold(slot)!.Jobs[k].Type;
            var request = type == TaskType.SplintJob ? Splinting(k)
                    : new TaskRequest(CharacterTask.Treat, type == TaskType.JobRepairRobot ? TaskType.JobRepairRobot : TaskType.JobMedic,
                            TaskSource.Job) { Job = k };
            StartJob(world, slot, _found.Patient, SquadGoals.Of(world), request);
        }
    }

    #endregion
}
