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
/// First aid and resting in play (docs/simulation.md "Bodies", docs/game/character-stats.md "First aid, medkits and robot repair" and "Sleep, beds and resting").
/// <list type="bullet">
/// <item><b>Treat</b> (<see cref="CharacterTask.Treat"/>): <see cref="TreatOrder"/> (the player), the medic job and the NPC rule start it. <c>Schedule</c> walks the medic to the patient
/// (the pursuit system's pattern: a path at free speed, asked again when the patient has moved) and, in reach, asks the patient to stand still (<see cref="CharacterCold.StandStillUntil"/>); <c>Act</c> (own slot, read-only on the patient) emits one effect per tick while the medic
/// is in reach, awake, not fighting and still, and the patient is still or down; <c>Apply</c> (serial commit, in target / source order) picks the kit, bandages the patient, drains the kit
/// and trains the medic. So nothing on another character is written in a parallel phase.</item>
/// <item><b>Rate</b> (<b>Observed</b>, character-stats.md): <c>lerp(1, 15, min(skill, kit quality) x 0.01) x medic speed mult</c> (x <c>robot medic speed mult</c> for a robot kit) per unit of <c>dt</c> on the first part with room (the next only once it fills), after any open wound is closed;
/// the kit's <c>charges</c> fall by <c>lerp(medkit drain 99, medkit drain 1, (100 - skill) / 100) x dt</c> per wound step or part bandaged (bandaging drains only the player's kits; character-stats.md "First aid update"). The skill is the medic's <c>medic</c> (first aid kit) or <c>robotics</c> (robot kit) (raw, x0.8 on oneself: the original reads the CharStats field with no injury or hunger multiplier, also for splints).
/// <c>dt</c> is the character's update time, the scaled frame time summed over the frames it was not visited (<c>FUN_1405cf130</c>, <b>Verified</b>), so <see cref="World.TickSeconds"/> in the long run.</item>
/// <item><b>Auto</b> (<c>SlowWorld</c>, each NPC every <see cref="FirstAidSettings.AutoEveryTicks"/> ticks on its slot's turn, serial in slot order): an idle, wandering or following NPC looks for a patient with the original's
/// Medic finder (<see cref="FindPatient"/>, FirstAidSystem.Finder.cs), as the decision step asks for the player characters' medic, robotics and splint jobs (<see cref="RegisterSources"/>).</item>
/// <item><b>Rest</b> (<see cref="CharacterTask.Rest"/>): <see cref="RestOrder"/>; <see cref="BodySystem"/> reads the task as <see cref="MedicalContext.Resting"/>. Lying in a bed is
/// <see cref="Locks.BedSystem"/>'s (<see cref="CharacterTask.Sleep"/>); <see cref="BodySystem"/> gives it the bed's healing.</item>
/// </list>
/// Placed after the movement system (a later move order replaces a treatment, since it sets the task) and before the body system.
/// </summary>
public sealed partial class FirstAidSystem(GameConstants constants, BodyOptions options, ItemFactory items, FirstAidSettings? settings = null) : ITickSystem, ITaskOwner, ITaskHandler, IOrderOwner, IDecisionOwner, IGoalSource
{
    /// <summary>The effect kind: one tick of treatment from the source (medic) on the target (patient).</summary>
    public const int EffectTreat = 1;
    /// <summary>Untreated damage (HP) under which a treatment in progress counts as finished.</summary>
    const float Done = 1e-3f;
    /// <summary>The kit quality closing a wound without a kit (<c>FUN_14064d800</c>).</summary>
    const float KitlessQuality = 5;

    public FirstAidSettings Settings { get; } = settings ?? new FirstAidSettings();
    public GameConstants Constants { get; } = constants;
    public ItemFactory Items { get; } = items;
    /// <summary>The relation event of bandaging an unconscious patient of another faction (<see cref="FactionDeeds.FirstAid"/>); null: none.</summary>
    public FactionDeeds? Deeds { get; set; }
    readonly XpService xp = new(constants, options);
    readonly BodyOptions bodyOptions = options;

    // ------------------------------------------------------------------ orders

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        var table = world.Characters;
        foreach (var command in commands)
        {
            switch (command)
            {
                case TreatOrder order:
                    if (!table.TryResolveNext(order.Patient, out int patient) || table.Cold(patient)!.Medical is not { Dead: false }) break;
                    foreach (var id in Who(world, order.Medics))
                        if (table.TryResolveNext(id, out int slot)) PlayerOrders.Give(world, slot, new OrderEntry(TaskType.FIRST_AID_ORDER, this) { Target = order.Patient }, order.Queued);
                    break;
                case SplintOrder splint:
                    if (!table.TryResolveNext(splint.Patient, out int splinted) || table.Cold(splinted)!.Medical is not { Dead: false }) break;
                    foreach (var id in Who(world, splint.Medics))
                        if (table.TryResolveNext(id, out int slot)) PlayerOrders.Give(world, slot, new OrderEntry(TaskType.SPLINT_ORDER, this) { Target = splint.Patient }, splint.Queued);
                    break;
                case RestOrder rest:
                    foreach (var id in Who(world, rest.Characters))
                        if (table.TryResolveNext(id, out int slot)) PlayerOrders.Give(world, slot, new OrderEntry(TaskType.REST, this), rest.Queued);
                    break;
                case MedicJobCommand job:
                    PressMedic(world, job);
                    break;
            }
        }
    }

    /// <summary>
    /// The Medic button (<c>FUN_1407fac20</c>, <b>Verified</b>; docs/game/ui-input.md): Medic 58 for the characters, and Robotics 57 too when the active squad has a robot (<see cref="SquadNeedsRobotics"/>).
    /// With Shift each becomes a job (58 first); else one look for a patient, with the Robotics finder when 57 was given (its finder takes everyone the Medic's does, robots too).
    /// </summary>
    void PressMedic(World world, MedicJobCommand job)
    {
        var table = world.Characters;
        bool robotics = SquadNeedsRobotics(world);
        foreach (var id in Who(world, job.Characters))
        {
            if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { IsPlayer: true } cold) continue;
            if (!job.Job)
            {
                var detail = robotics ? RoboticsButton : MedicButton;
                PlayerOrders.Give(world, slot, new OrderEntry(TaskType.None, this) { Detail = detail }, job.Queued);   // the button: one look for a patient
                continue;
            }
            Production.PermaJobs.Give(cold, TaskType.JOB_MEDIC, Production.PermaJob.Untargeted(TaskType.JOB_MEDIC));
            if (robotics) Production.PermaJobs.Give(cold, TaskType.JOB_REPAIR_ROBOT, Production.PermaJob.Untargeted(TaskType.JOB_REPAIR_ROBOT));
        }
    }

    /// <summary>
    /// Whether the Medic button gives Robotics 57 too (<c>FUN_1407fac20</c>, <b>Verified</b>): a member of the player's active squad (<see cref="Dialogue.DialogueSystem.ActiveSquad"/>) is of a
    /// robot race or holds an item of function 12 (a robot repair kit) in its own inventory, the backpack's contents not searched (<c>FUN_14004b84e</c> on the flat item list).
    /// </summary>
    static bool SquadNeedsRobotics(World world)
    {
        if (Dialogue.DialogueSystem.ActiveSquad(world) is not { } squad) return false;
        var table = world.Characters;
        foreach (var member in squad.Members)
        {
            if (!table.TryResolveNext(member, out int slot) || table.Cold(slot) is not { } cold) continue;
            if (cold.Race?.IsRobot == true) return true;
            if (cold.Inventory is { } inventory && inventory.Items.Exists(item => item.ItemFunction == ItemInfo.RobotKitFunction)) return true;
        }
        return false;
    }

    /// <summary>The <see cref="OrderEntry.Detail"/> of the Medic button's one-shot order (the job's finder looked at once, then a First aid 25).</summary>
    static readonly object MedicButton = new();
    /// <summary>The same with Robotics 57 given too: the look takes robots as well.</summary>
    static readonly object RoboticsButton = new();

    /// <summary>
    /// First aid 25 and Splint 249 need the patient alive (else the order leaves the queue) and an able medic with a kit (a medkit or a robot kit anyone of its squad near it carries; its own splint kit),
    /// Rest an able character; the Medic button an able player character. The failure texts are the orders' refusals.
    /// </summary>
    OrderCheck IOrderOwner.CanRun(World world, int slot, OrderEntry order)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        if (order.Detail == MedicButton || order.Detail == RoboticsButton) return cold.IsPlayer ? OrderCheck.Run : OrderCheck.Gone;
        if (order.Row == TaskType.REST) return CanAct(cold) ? OrderCheck.Run : OrderCheck.Blocked();
        if (!table.TryResolveNext(order.Target, out int patient) || table.Cold(patient)!.Medical is not { Dead: false }) return OrderCheck.Gone;
        if (cold is not { Race: not null, Stats: not null } || !CanAct(cold)) return OrderCheck.Blocked();
        if (order.Row == TaskType.SPLINT_ORDER)
            return OwnKit(cold, ItemInfo.SplintKitFunction) is null ? OrderCheck.Blocked("I don't have a splint kit.") : OrderCheck.Run;   // the medic's own kit only (the original's kit search)
        return FindKit(world, slot, cold, ItemInfo.FirstAidKitFunction) is null && FindKit(world, slot, cold, ItemInfo.RobotKitFunction) is null
            ? OrderCheck.Blocked("I don't have a medkit.")   // refused before the medic moves
            : OrderCheck.Run;
    }

    void IOrderOwner.Start(World world, int slot, OrderEntry order)
    {
        if (order.Detail == MedicButton || order.Detail == RoboticsButton)
        {
            HealSomeone(world, slot, SquadGoals.Of(world), robotics: order.Detail == RoboticsButton, TaskSource.Order);
            return;
        }
        if (order.Row == TaskType.REST)
        {
            TaskStart.Start(world, slot, new TaskRequest(CharacterTask.Rest, TaskType.REST, TaskSource.Order));
            return;
        }
        TaskStart.Start(world, slot, new TaskRequest(CharacterTask.Treat, order.Row, TaskSource.Order)
        {
            Target = order.Target, Resume = TaskStart.ResumeOf(world, slot, CharacterTask.Treat, CharacterTask.Idle),
        });
    }

    static IReadOnlyList<CharacterId> Who(World world, IReadOnlyList<CharacterId> ids) => ids.Count > 0 ? ids : world.Player.Selection.ToArray();

    /// <summary>
    /// A character that can be ordered to treat or rest: it has a medical state, is awake and not carried. Orders outrank Self preservation and First aid 25 and Splint 249 test no threat
    /// (docs/game/combat.md "Fight lifecycle and orders"): a fighting character's fight ends with the order's new task (<see cref="TaskStart.Start"/>).
    /// </summary>
    static bool CanAct(CharacterCold cold) => cold.Medical is { Incapacitated: false } && cold.CarriedBy.IsNone;

    public void RegisterTasks(TaskHandlers tasks)
    {
        tasks.Register(CharacterTask.Treat, this);
        tasks.Register(CharacterTask.Rest, this);
    }

    /// <summary>A treatment of <see cref="TaskRequest.Target"/> (rows 249 and 250 splint), or the Rest order's sitting down.</summary>
    void ITaskHandler.Start(World world, int slot, in TaskRequest request)
    {
        if (request.Task == CharacterTask.Treat)
        {
            Begin(world, slot, request.Target, splint: request.Row is TaskType.SPLINT_ORDER or TaskType.SPLINT_JOB);
            return;
        }
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        TaskStart.Stand(table, slot);
        table.Next[slot].Task = (byte)CharacterTask.Rest;
        cold.TreatTarget = CharacterId.None;
        cold.TreatSplint = false;
    }

    /// <summary>A treatment that ends lets go of its patient and kit (the task it goes back to is the new start's, or its provenance's when it ends by itself).</summary>
    void ITaskHandler.End(World world, int slot, CharacterTask task, TaskEnd reason)
    {
        if (task == CharacterTask.Treat) LetGo(world.Characters.Cold(slot)!);
    }

    /// <summary>Starts a treatment (serial phases): the medic stands, then walks to the patient (the task it goes back to afterwards is in its provenance, <see cref="CharacterCold.Running"/>).</summary>
    static void Begin(World world, int slot, CharacterId patient, bool splint = false)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        if (cold.TreatTarget != patient) Say(world, slot, patient, HealingOtherStart, BeingHealedStart);
        ref var n = ref table.Next[slot];
        if (cold.TreatTarget != patient || cold.TreatSplint != splint || n.Task != (byte)CharacterTask.Treat) cold.TreatKit = null;   // a new treatment looks for its own kit
        TaskStart.Stand(table, slot);
        n.Task = (byte)CharacterTask.Treat;
        n.Mode = SpeedMode.Free;
        n.TaskTime = 0;
        cold.TreatTarget = patient;
        cold.TreatSplint = splint;
        cold.PathFailed = false;   // a failure from before is not this walk's
    }

    /// <summary>Dialogue events 18-22 (EV_HEALING_OTHER_START ... EV_FIRSTAID_KIT_EMPTY).</summary>
    const int HealingOtherStart = 18, BeingHealedStart = 19, HealingOtherFinished = 20, BeingHealedFinished = 21, KitEmpty = 22;

    /// <summary>
    /// The treatment's dialogue events (<c>Task_FirstAid</c> start and finish, <c>FUN_14034ecf0</c> / <c>FUN_1403563a0</c>): the medic fires <paramref name="medicEvent"/> at the
    /// patient and a patient that is awake fires <paramref name="patientEvent"/> back (-1: none); nothing for a medic treating itself. Said from the next tick.
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

    /// <summary>Ends a treatment by itself (serial phases; <see cref="TaskEnd.Done"/>): back to the task before it, path dropped.</summary>
    static void End(World world, int slot)
    {
        if (world.Characters.Next[slot].Task == (byte)CharacterTask.Treat) TaskStart.End(world, slot, TaskEnd.Done);
        else LetGo(world.Characters.Cold(slot)!);
    }

    static void LetGo(CharacterCold cold)
    {
        cold.TreatKneels = false;
        cold.TreatTarget = CharacterId.None;
        cold.TreatSplint = false;
        cold.TreatKit = null;
    }

    /// <summary>
    /// Where a medic treats <paramref name="patient"/> from: a caged one's cage use node (in front of its door, <see cref="FixtureRegistry.ExitSpot"/>; the medic cannot stand in
    /// the cage), else where the patient is.
    /// </summary>
    static Vector3 TreatSpot(World world, CharacterCold patient, in CharacterHot at) =>
        patient is { IsImprisoned: true, InWhat: { } id } && world.Fixtures.Find(id) is { Kind: FixtureKind.Cage } cage && FixtureRegistry.ExitSpot(cage) is { } door
            ? door
            : at.Position;

    /// <summary>In reach of the treatment spot: within <see cref="FirstAidSettings.Reach"/> on X/Z and <see cref="FirstAidSettings.ReachHeight"/> in height (on its floor).</summary>
    bool InReach(in CharacterHot medic, Vector3 spot)
    {
        float dx = medic.Position.X - spot.X, dz = medic.Position.Z - spot.Z;
        return dx * dx + dz * dz <= Settings.Reach * Settings.Reach && MathF.Abs(medic.Position.Y - spot.Y) <= Settings.ReachHeight;
    }

    // ------------------------------------------------------------------ walking to the patient

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
            if (cold.Medical is not { Incapacitated: false } || cold.InCombat
                || !table.TryResolveNext(cold.TreatTarget, out int t) || table.Cold(t)!.Medical is not { Dead: false })
            {
                End(world, i);
                continue;
            }
            const ushort Path = (ushort)MoveFlags.AnyPath;
            var spot = TreatSpot(world, table.Cold(t)!, next[t]);
            if (t == i || InReach(next[i], spot))
            {
                // In reach (or treating itself): stand still and let the Act step work, and ask the patient to stand still too. The medic kneels only while the patient
                // keeps still (the original plays the clip in the first aid task's update, which runs once the target stands still, row 25's state 42).
                if (t != i) AskToStandStill(world, table.Cold(t)!);
                bool patientStill = Speed2(table.Previous[t].Velocity) <= Settings.StillSpeed * Settings.StillSpeed;
                cold.TreatKneels = t == i || table.Cold(t)!.Medical!.Incapacitated || patientStill;
                if ((n.Flags & Path) != 0)
                {
                    n.Flags = (ushort)(n.Flags & ~Path);
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
            // No way to the patient: no path, or one that ends short of the asked goal (the walkability answers an unreachable goal with the way to the reachable point nearest it). The
            // original's task walk reports it (notifyPathImpossible): the patient goes on the medic's path failures for 20 s, which the finder skips, and the treatment ends. Without it
            // a medic asked for the same hopeless path every half second (issue #124: searches run to the iteration limit, ~280 MB/s of garbage).
            if (!asking && (cold.PathFailed || (n.Flags & (ushort)MoveFlags.HasPath) != 0 && cold.Path.Length > 0 && EndsShort(ReachedEnd(cold, n), n.Goal, spot.Y)))
            {
                cold.PathFailed = false;
                PursuitSystem.GiveUp(world, cold, table.IdOf(t));
                End(world, i);
                continue;
            }
            bool stale = (n.Flags & (ushort)MoveFlags.HasPath) == 0 || Vector2.DistanceSquared(goal, n.Goal) > PursuitSystem.RepathDistance * PursuitSystem.RepathDistance;
            if (asking || !stale || n.TaskTime > 0) continue;
            n.Goal = goal;
            (cold.GoalHeight, cold.GoalHeightAt) = (spot.Y, goal);   // the spot's floor: an upstairs patient is walked to up the stairs, not to the ground below it
            n.Mode = SpeedMode.Free;
            n.Flags |= (ushort)MoveFlags.NeedPath;
            n.TaskTime = PursuitSystem.RepathSeconds;
        }
    }

    /// <summary>How long a medic's ask to stand still lasts after its last renewal: the request's time to live (3 s, <c>FUN_140269ff0</c>).</summary>
    const float StandStillSeconds = 3;

    /// <summary>
    /// The first aid task's every update asks the patient to stand still (<c>Task_FirstAid</c> / <c>Task_FirstAidRig</c> call <c>FUN_1403346d0</c>, docs/game/ai-tasks.md "Medic jobs"):
    /// a crawling or walking patient stops, so the treatment can run (<see cref="Act"/> treats only a patient standing still). Without it a medic knelt, stood to follow, knelt again (issue #232).
    /// </summary>
    static void AskToStandStill(World world, CharacterCold patient) =>
        patient.StandStillUntil = world.Tick + (long)MathF.Ceiling(StandStillSeconds / world.TickSeconds);

    /// <summary>
    /// Where the path is known to lead: the end of its navmesh part. A provisional route (the goal unreachable over the loaded zones: a medic shut in a locked house) runs
    /// on from the reachable point nearest the goal to the goal itself, so its last waypoint is the goal; the medic waited at that point for ever.
    /// </summary>
    static Vector3 ReachedEnd(CharacterCold cold, in CharacterHot n) =>
        cold.PathExact >= cold.Path.Length ? cold.Path[^1] : cold.PathExact > 0 ? cold.Path[cold.PathExact - 1] : n.Position;

    /// <summary>A path whose last waypoint is farther than the reach from the goal on X/Z, or off the treatment spot's floor: the patient cannot be got to.</summary>
    bool EndsShort(Vector3 end, Vector2 goal, float spotHeight) =>
        Vector2.Distance(new Vector2(end.X, end.Z), goal) > Settings.Reach || MathF.Abs(end.Y - spotHeight) > Settings.ReachHeight;

    // ------------------------------------------------------------------ the treatment

    public void Act(World world, Partition part, EffectBuffer effects)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        float still2 = Settings.StillSpeed * Settings.StillSpeed;
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive || prev[i].Dormant || !world.Steps(i) || next[i].Task != (byte)CharacterTask.Treat) continue;
            var cold = table.Cold(i)!;
            if (cold.Medical is not { Incapacitated: false } medical || cold.InCombat || medical.Aggregates().BothArmsDisabled || cold.StaggeredUntil > world.Tick) continue;
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

    static float Speed2(Vector3 v) => v.X * v.X + v.Z * v.Z;

    /// <summary>Serial, in (patient, medic) order: the kit, the bandaging, the drain and the medic's XP. Ends the treatment when nothing is left to bandage or no kit fits.</summary>
    public void Apply(World world, in Effect effect)
    {
        if (effect.Kind != EffectTreat) return;
        var table = world.Characters;
        int m = effect.Source, p = effect.Target;
        if (!table.Next[m].Alive || table.Next[m].Task != (byte)CharacterTask.Treat) return;
        var medicCold = table.Cold(m)!;
        if (!table.TryResolveNext(medicCold.TreatTarget, out int t) || t != p) return;
        var patientCold = table.Cold(p)!;
        if (medicCold is not { Medical: { Incapacitated: false } medical, Stats: { } stats, Race: { } medicRace } || patientCold is not { Medical: { Dead: false } body, Race: { } race })
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
        // The skill and trained stat: medic with a first aid kit, robotics with a robot repair kit; treating oneself x0.8 (FUN_14033c5a0, Task_FirstAid's kit phase).
        StatsEnumerated StatOf(bool robotKit) => robotKit ? StatsEnumerated.Robotics : StatsEnumerated.Medic;
        float SkillOf(bool robotKit) => stats[StatOf(robotKit)] * (m == p ? 0.8f : 1);   // the raw stat: no injury or hunger multiplier
        // A medic not of the player's treats an imprisoned patient without a kit (Task_FirstAid FUN_1403563a0 skips the kit search; also for a chained slave of the medic's faction, FUN_14032e2a0).
        bool kitless = !medicCold.IsPlayer && (patientCold.IsImprisoned || OwnedChained(medicCold, patientCold));
        var ctx = new MedicalContext(Constants, bodyOptions, race);
        // Open wounds are closed first, newest first, with a kit matching the patient's race (none: quality 5); nothing is bandaged meanwhile (FUN_14064d800). This drain is not gated.
        if (body.Wounds.Count > 0)
        {
            var woundKit = kitless ? null : KitOf(world, m, medicCold, FunctionOf(race.IsRobot));
            if (woundKit is null && !kitless)
            {
                Say(world, m, medicCold.TreatTarget, KitEmpty);
                End(world, m);
                return;
            }
            float woundSkill = SkillOf(race.IsRobot && !kitless);
            body.CloseWound(dt, woundSkill, woundKit is { } wk ? Items.Info(wk.Item.Record)?.Quality ?? 0 : KitlessQuality, Constants);
            if (woundKit is { } used) Drain(used, woundSkill, dt, race.IsRobot, 1);
            medicCold.UseSkill(StatOf(race.IsRobot && !kitless), world.Tick);
            xp.Medic(stats, race, medicRace, dt, StatOf(race.IsRobot && !kitless));
            return;
        }
        if (kitless)
        {
            // Without a kit every part takes 1 HP a second, organic or robotic alike, and nothing drains (FUN_140649a00). Done as with a kit: once the parts are bandaged up,
            // healing turns bandage into flesh and float dust leaves a sliver of room every update, which would keep the treatment going for ever.
            if (body.BandageRoom(race, false) + body.BandageRoom(race, true) <= Done || body.Bandage(dt, null, race, out _) <= 0)
            {
                Say(world, m, medicCold.TreatTarget, HealingOtherFinished, BeingHealedFinished);
                patientCold.StuckBolts = null;
                End(world, m);
                return;
            }
            medicCold.UseSkill(StatsEnumerated.Medic, world.Tick);
            xp.Medic(stats, race, medicRace, dt, StatsEnumerated.Medic);
            Deeds?.FirstAid(world, m, p, dt);
            return;
        }
        // Ordinary kits first; a robotic part is treated once the organic ones are done. Treatment runs until the last bit is bandaged (the start threshold is MedicalState.FirstAidThreshold).
        bool robot = body.BandageRoom(race, false) <= Done;
        if (robot && body.BandageRoom(race, true) <= Done)
        {
            Say(world, m, medicCold.TreatTarget, HealingOtherFinished, BeingHealedFinished);
            patientCold.StuckBolts = null;   // the bolts go when first aid has nothing left to do (FUN_14064d800 -> FUN_14052bc10)
            End(world, m);   // nothing left to bandage
            return;
        }
        if (KitOf(world, m, medicCold, FunctionOf(robot)) is not { } kit)
        {
            Say(world, m, medicCold.TreatTarget, KitEmpty);   // Task_FirstAidRig's empty kit (FUN_14034ef20)
            End(world, m);   // no kit of the right kind within reach
            return;
        }
        float skill = SkillOf(robot);
        float quality = Items.Info(kit.Item.Record)?.Quality ?? 0;
        float bandaged = body.Treat(dt, skill, quality, robot, ctx, out int touched);
        if (bandaged <= 0)
        {
            End(world, m);
            return;
        }
        if (medicCold.IsPlayer) Drain(kit, skill, dt, robot, touched);   // once per part bandaged; only the player's medics use up kits here (FUN_140649a00: faction +0x250)
        medicCold.UseSkill(StatOf(robot), world.Tick);
        xp.Medic(stats, race, medicRace, dt, StatOf(robot));
        // An unconscious patient's faction is grateful to the medic's (engine choice: every update that bandages; the original's update fires it only when its loop got past the wounds).
        Deeds?.FirstAid(world, m, p, dt);
    }

    /// <summary>
    /// One update of a splint (task "Splinting", <c>FUN_140649d50</c>, docs/game/character-stats.md "Splints"): the medic's <c>medic</c> skill and the kit's quality set the rate and the cap;
    /// the kit drains as first aid's only when a part took rig; medic XP every update. Ends when no part is left to splint or no splint kit is in reach.
    /// </summary>
    void Splint(World world, int m, CharacterCold medicCold, MedicalState medical, CharacterStats stats, RaceData medicRace, MedicalState body, RaceData race)
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
        float added = body.Splint(dt, skill, quality, new MedicalContext(Constants, bodyOptions, race));
        if (added <= 0)
        {
            End(world, m);   // nothing left to splint
            return;
        }
        Drain(kit, skill, dt, false, 1);
        medicCold.UseSkill(StatsEnumerated.Medic, world.Tick);
        xp.Medic(stats, race, medicRace, dt, StatsEnumerated.Medic);
    }

    // ------------------------------------------------------------------ kits

    /// <summary>Uses <paramref name="times"/> drains of a kit (<see cref="MedicalState.KitDrain"/>); a used-up kit is gone.</summary>
    void Drain((ItemInstance Item, Inventory Owner) kit, float skill, float dt, bool robotKit, int times)
    {
        kit.Item.Charges -= MedicalState.KitDrain(Constants, skill, dt, robotKit) * times;
        if (kit.Item.Charges <= 0) kit.Owner.RemoveOne(kit.Item);
    }

    /// <summary>Whether an inventory (or its backpack) holds an item of the function: the medic finder's kit flags test only the item function, no charge, quantity or type (<c>FUN_14004b84e</c>).</summary>
    internal static bool HasFunction(Inventory? inventory, int function) => inventory is not null && inventory.All().Any(item => item.ItemFunction == function);

    static int FunctionOf(bool robot) => robot ? ItemInfo.RobotKitFunction : ItemInfo.FirstAidKitFunction;

    /// <summary>
    /// The kit a medic uses for splints, from its own inventory only (the original's kit search, <c>FUN_14033c740</c> at vtable +0x38 of the splint and first aid tasks, <b>Verified</b>):
    /// the main grid, else the worn backpack's contents; the item of the function with the fewest charges above zero (<see cref="Fewest"/>). Squad mates' kits are not searched.
    /// </summary>
    static (ItemInstance Item, Inventory Owner)? OwnKit(CharacterCold cold, int function)
    {
        if (cold.Inventory is not { } own) return null;
        if (Fewest(own.Items, function) is { } main) return (main, own);
        return Fewest(own.Backpack?.Contents?.Items, function) is { } worn ? (worn, own) : null;
    }

    /// <summary>
    /// The planner's <c>haveFirstAidKit</c> (state 23, <c>FUN_140598860</c>, <b>Verified</b>): the character's items, or else its backpack's, hold one of the kit class
    /// (<paramref name="function"/>); charges are not looked at.
    /// </summary>
    internal static bool HoldsKit(CharacterCold cold, int function)
    {
        if (cold.Inventory is not { } own) return false;
        return Holds(own.Items, function) || Holds(own.Backpack?.Contents?.Items, function);

        static bool Holds(IEnumerable<ItemInstance>? items, int function)
        {
            if (items is null) return false;
            foreach (var item in items)
                if (item.Type == FcsRecordType.ITEM && item.ItemFunction == function) return true;
            return false;
        }
    }

    /// <summary>
    /// The item of the function with charges above zero and the fewest of them, the first of equals (<c>FUN_140746480</c>, <b>Verified</b>: the search starts at 99999, so an item with
    /// 99999 charges or more never qualifies). Null when none.
    /// </summary>
    static ItemInstance? Fewest(IEnumerable<ItemInstance>? items, int function)
    {
        if (items is null) return null;
        ItemInstance? best = null;
        foreach (var item in items)
        {
            if (item.Type != FcsRecordType.ITEM || item.ItemFunction != function || item.Charges <= 0 || item.Charges >= 99999 || item.Quantity <= 0) continue;
            if (best is null || item.Charges < best.Charges) best = item;
        }
        return best;
    }

    /// <summary>
    /// The medic's own kit of an <c>item function</c> by the original's search (<see cref="OwnKit"/>: main grid, then the worn backpack; fewest charges above zero), else a squad mate's
    /// within <see cref="FirstAidSettings.KitReach"/> by the same rule (an engine choice: the original searches only the medic's own).
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
            if (m.Slot == slot || !table.TryResolveNext(m, out int s) || table.Cold(s) is not { Inventory: { } mates, Medical: { Dead: false } }) continue;
            var d = table.Next[s].Position - here;
            if (d.X * d.X + d.Z * d.Z > reach2) continue;
            if (OwnKit(table.Cold(s)!, function) is { } found) return found;
        }
        return null;
    }

    /// <summary>
    /// The kit a running treatment uses (<c>FUN_14033c7e0</c>, <b>Verified</b>): the one it took last time, until it is used up (no charges left) or no longer of the wanted function; only then
    /// <see cref="FindKit"/> searches again (the original also drops a kit no longer held; a kit lost another way is found again at once). Null: none.
    /// </summary>
    (ItemInstance Item, Inventory Owner)? KitOf(World world, int slot, CharacterCold cold, int function)
    {
        if (cold.TreatKit is { } kept && kept.Item.ItemFunction == function && kept.Item.Charges > 0 && kept.Owner.All().Contains(kept.Item)) return kept;
        return cold.TreatKit = FindKit(world, slot, cold, function);
    }

    // ------------------------------------------------------------------ treating by themselves

    // Resolved on the first sweep: whose goal lists hold the Medic goal.
    AiSystem? ai;

    public void SlowWorld(World world)
    {
        // Each NPC looks every AutoEveryTicks ticks, a slot's turn falling on its own tick (slot mod the period), so the looks spread over the ticks instead of all landing on one
        // (the original runs its AI updates 8 characters a frame in turn, FUN_140786e30).
        if (Settings.AutoEveryTicks <= 0) return;
        int every = world.Every(Settings.AutoEveryTicks);
        var table = world.Characters;
        var next = table.Next;
        // The treated list and the sweep are made on the turn's first idle NPC (a turn with none, most of them in a quiet world, makes neither).
        SquadGoals? taken = null;
        try
        {
            for (int i = 0; i < table.HighWater; i++)
            {
                if (!next[i].Alive || !world.Turn(i, every)) continue;   // its turn (the far cadence, World.Turn)
                var cold = table.Cold(i)!;
                if (cold.IsPlayer || !Settings.AutoNpcs) continue;   // a player's medic jobs are the decision step's (MedicJobs)
                if (cold.Ai is { Engine: false } mind)
                {
                    // The AI's own: its Medic goal weighs the patient found here (AiBehaviour.Medic); one whose lists hold no Medic goal does not treat.
                    if ((ai ??= world.SystemOf<AiSystem>())?.ListsGoal(world, i, TaskType.JOB_MEDIC) != true) mind.MedicScore = 0;
                    else
                    {
                        if (taken is null) { taken = SquadGoals.Of(world); BeginSweep(world); }
                        KeepPick(world, i, mind, taken);
                    }
                    continue;
                }
                // An NPC the AI does not run (an engine mind, or none) treats when idle: no task, following, wandering or an implicit job.
                bool idle = next[i].Task is (byte)CharacterTask.Idle or (byte)CharacterTask.Wander or (byte)CharacterTask.Follow || Production.PermaJobs.ListJob(cold, (CharacterTask)next[i].Task) is { Implicit: true };
                if (!idle) continue;
                if (taken is null) { taken = SquadGoals.Of(world); BeginSweep(world); }
                HealSomeone(world, i, taken, robotics: false, TaskSource.Npc);
            }
        }
        finally { EndSweep(); }
    }

    /// <summary>The Medic goal's finder result for the think (<see cref="AiMind.MedicPatient"/>, <see cref="AiMind.MedicScore"/>; none and 0 without a patient).</summary>
    void KeepPick(World world, int i, AiMind mind, SquadGoals taken)
    {
        var (patient, score) = FindPatient(world, i, taken, robotics: false);
        mind.MedicPatient = patient >= 0 ? world.Characters.IdOf(patient) : CharacterId.None;
        mind.MedicScore = patient >= 0 ? score : 0;
    }

    /// <summary>
    /// Starts the treatment of each NPC whose think chose its Medic goal (<see cref="AiMind.MedicAsk"/>; serial, after the think): the finder runs again on the state of now, so a patient
    /// treated meanwhile is not walked to; one that finds none drops its kept pick, and the next think chooses again.
    /// </summary>
    internal void SendGoalTreatments(World world)
    {
        var table = world.Characters;
        SquadGoals? taken = null;
        try
        {
            for (int s = 0; s < table.HighWater; s++)
            {
                if (table.Cold(s)?.Ai is not { MedicAsk: true } mind) continue;
                mind.MedicAsk = false;
                if (!table.Next[s].Alive) continue;
                if (taken is null) { taken = SquadGoals.Of(world); BeginSweep(world); }
                if (HealSomeone(world, s, taken, robotics: false, TaskSource.Npc)) continue;
                mind.MedicPatient = CharacterId.None;
                mind.MedicScore = 0;
            }
        }
        finally { EndSweep(); }
    }

    /// <summary>
    /// The first aid jobs as the decision step's contributor (step 5; docs/game/ai-tasks.md "Medic jobs"): Medic 58 and Robotics 57 run while the finder (<see cref="FindPatient"/>) finds a
    /// patient, Splint Rigging 250 while its finder does (<see cref="FindSplint"/>); all three run with the jobs switch off. A job's treatment runs until it ends by itself.
    /// </summary>
    public void RegisterSources(DecisionSystem decisions)
    {
        var jobs = new MedicJobs(this);
        foreach (var type in (ReadOnlySpan<TaskType>)[TaskType.JOB_MEDIC, TaskType.JOB_REPAIR_ROBOT, TaskType.SPLINT_JOB]) decisions.RegisterJob(type, jobs);
    }

    sealed class MedicJobs(FirstAidSystem owner) : IJobSource
    {
        (long Tick, int Slot, int K, int Patient) found = (-1, -1, -1, -1);

        public DecisionCheck CanRun(World world, int slot, int k)
        {
            var table = world.Characters;
            var cold = table.Cold(slot)!;
            var type = cold.Jobs[k].Type;
            if (cold.Running is { Source: TaskSource.Job } run && run.Job == k && run.Runs((CharacterTask)table.Next[slot].Task) && run.Task == CharacterTask.Treat) return DecisionCheck.Runs;
            var taken = SquadGoals.Of(world);
            int patient = type == TaskType.SPLINT_JOB ? owner.FindSplint(world, slot, taken) : owner.FindPatient(world, slot, taken, robotics: type == TaskType.JOB_REPAIR_ROBOT).Slot;
            if (patient < 0) return DecisionCheck.No("no patient");
            found = (world.Tick, slot, k, patient);
            return DecisionCheck.Run;
        }

        public void Start(World world, int slot, int k)
        {
            if (found.Tick != world.Tick || found.Slot != slot || found.K != k) return;
            var type = world.Characters.Cold(slot)!.Jobs[k].Type;
            var request = type == TaskType.SPLINT_JOB ? Splinting(k) : new TaskRequest(CharacterTask.Treat, type == TaskType.JOB_REPAIR_ROBOT ? TaskType.JOB_REPAIR_ROBOT : TaskType.JOB_MEDIC, TaskSource.Job) { Job = k };
            StartJob(world, slot, found.Patient, SquadGoals.Of(world), request);
        }
    }

}
