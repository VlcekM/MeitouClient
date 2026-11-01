using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation.FirstAid;

/// <summary>
/// First aid and resting in play (docs/simulation.md "Bodies", docs/game/character-stats.md "First aid, medkits and robot repair" and "Sleep, beds and resting").
/// <list type="bullet">
/// <item><b>Treat</b> (<see cref="CharacterTask.Treat"/>): <see cref="TreatOrder"/> (the player), the medic job and the NPC rule start it. <c>Schedule</c> walks the medic to the patient
/// (the pursuit system's pattern: a path at free speed, asked again when the patient has moved); <c>Act</c> (own slot, read-only on the patient) emits one effect per tick while the medic
/// is in reach, awake, not fighting and still, and the patient is still or down; <c>Apply</c> (serial commit, in target / source order) picks the kit, bandages the patient, drains the kit
/// and trains the medic. So nothing on another character is written in a parallel phase.</item>
/// <item><b>Rate</b> (<b>Observed</b>, character-stats.md): <c>lerp(1, 15, min(skill, kit quality) x 0.01) x medic speed mult</c> (x <c>robot medic speed mult</c> for a robot kit) per unit of <c>dt</c> on the first part with room (the next only once it fills), after any open wound is closed;
/// the kit's <c>charges</c> fall by <c>lerp(medkit drain 99, medkit drain 1, (100 - skill) / 100) x dt</c> per wound step or part bandaged (bandaging drains only the player's kits; character-stats.md "First aid update"). The skill is the medic's <c>medic</c> (first aid kit) or <c>robotics</c> (robot kit) times
/// the stat multiplier of injuries and hunger. <c>dt</c> is <b>game seconds</b> (<b>engine choice</b>, the unit is <b>Unknown</b>).</item>
/// <item><b>Auto</b> (<c>SlowWorld</c>, every <see cref="FirstAidSettings.AutoEveryTicks"/> ticks, serial in slot order): an idle, wandering or following NPC looks for a patient with the original's
/// Medic finder (<see cref="FindPatient"/>, FirstAidSystem.Finder.cs), as the decision step asks for the player characters' medic, robotics and splint jobs (<see cref="RegisterSources"/>).</item>
/// <item><b>Rest</b> (<see cref="CharacterTask.Rest"/>): <see cref="RestOrder"/>; <see cref="BodySystem"/> reads the task as <see cref="MedicalContext.Resting"/>. Lying in a bed is
/// <see cref="Locks.BedSystem"/>'s (<see cref="CharacterTask.Sleep"/>); <see cref="BodySystem"/> gives it the bed's healing.</item>
/// </list>
/// Placed after the movement system (a later move order replaces a treatment, since it sets the task) and before the body system.
/// </summary>
public sealed partial class FirstAidSystem(GameConstants constants, BodyOptions options, ItemFactory items, FirstAidSettings? settings = null) : ITickSystem, ITaskOwner, ITaskHandler, IOrderOwner, IDecisionOwner
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
                    foreach (var id in Who(world, job.Characters))
                    {
                        if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { IsPlayer: true } cold) continue;
                        if (job.Job) Production.PermaJobs.Add(cold, TaskType.JOB_MEDIC, Production.PermaJob.Untargeted(TaskType.JOB_MEDIC));
                        else PlayerOrders.Give(world, slot, new OrderEntry(TaskType.None, this) { Detail = MedicButton }, job.Queued);   // the button: one look for a patient
                    }
                    break;
            }
        }
    }

    /// <summary>The <see cref="OrderEntry.Detail"/> of the Medic button's one-shot order (the job's finder looked at once, then a First aid 25).</summary>
    static readonly object MedicButton = new();

    /// <summary>
    /// First aid 25 and Splint 249 need the patient alive (else the order leaves the queue) and an able medic with a kit (a medkit or a robot kit anyone of its squad near it carries; its own splint kit),
    /// Rest an able character; the Medic button an able player character. The failure texts are the orders' refusals.
    /// </summary>
    OrderCheck IOrderOwner.CanRun(World world, int slot, OrderEntry order)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        if (order.Detail == MedicButton) return cold.IsPlayer ? OrderCheck.Run : OrderCheck.Gone;
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
        if (order.Detail == MedicButton)
        {
            HealSomeone(world, slot, Treated(world.Characters), robotics: false, TaskSource.Order);
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
        cold.TreatTarget = CharacterId.None;
        cold.TreatSplint = false;
        cold.TreatKit = null;
    }

    static float Gap(in CharacterHot a, in CharacterHot b)
    {
        float dx = a.Position.X - b.Position.X, dz = a.Position.Z - b.Position.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
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
            if (t == i || Gap(next[i], next[t]) <= Settings.Reach)
            {
                // In reach (or treating itself): stand still and let the Act step work.
                if ((n.Flags & Path) != 0)
                {
                    n.Flags = (ushort)(n.Flags & ~Path);
                    n.PathCursor = 0;
                    cold.PathRequest = 0;
                }
                n.TaskTime = 0;
                continue;
            }
            n.TaskTime -= dt;
            var goal = new Vector2(next[t].Position.X, next[t].Position.Z);
            bool asking = (n.Flags & (ushort)(MoveFlags.NeedPath | MoveFlags.Pending)) != 0;
            bool stale = (n.Flags & (ushort)MoveFlags.HasPath) == 0 || Vector2.DistanceSquared(goal, n.Goal) > PursuitSystem.RepathDistance * PursuitSystem.RepathDistance;
            if (asking || !stale || n.TaskTime > 0) continue;
            n.Goal = goal;
            n.Mode = SpeedMode.Free;
            n.Flags |= (ushort)MoveFlags.NeedPath;
            n.TaskTime = PursuitSystem.RepathSeconds;
        }
    }

    // ------------------------------------------------------------------ the treatment

    public void Act(World world, Partition part, EffectBuffer effects)
    {
        var table = world.Characters;
        var prev = table.Previous;
        var next = table.Next;
        float still2 = Settings.StillSpeed * Settings.StillSpeed;
        float reach = Settings.Reach;
        for (int i = part.Start; i < part.End; i++)
        {
            if (!prev[i].Alive || next[i].Task != (byte)CharacterTask.Treat) continue;
            var cold = table.Cold(i)!;
            if (cold.Medical is not { Incapacitated: false } medical || cold.InCombat || medical.Aggregates().BothArmsDisabled || cold.StaggeredUntil > world.Tick) continue;
            var target = cold.TreatTarget;
            if (!table.IsAlive(target)) continue;
            int t = target.Slot;
            if (table.Cold(t) is not { Medical: { Dead: false } patient }) continue;
            if (Speed2(prev[i].Velocity) > still2) continue;
            if (t != i)
            {
                if (Gap(prev[i], prev[t]) > reach) continue;
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
        float dt = world.TickSeconds;
        // The skill and trained stat: medic with a first aid kit, robotics with a robot repair kit; treating oneself x0.8 (FUN_14033c5a0, Task_FirstAid's kit phase).
        StatsEnumerated StatOf(bool robotKit) => robotKit ? StatsEnumerated.Robotics : StatsEnumerated.Medic;
        float SkillOf(bool robotKit) => stats[StatOf(robotKit)] * medical.StatMultiplier(StatOf(robotKit)) * (m == p ? 0.8f : 1);
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
            // Without a kit every part takes 1 HP a second, organic or robotic alike, and nothing drains (FUN_140649a00).
            if (body.Bandage(dt, null, race, out _) <= 0)
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
        float skill = stats[StatsEnumerated.Medic] * medical.StatMultiplier(StatsEnumerated.Medic);
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

    /// <summary>The best kit of a function in an inventory (and its backpack): the highest record quality with charges left; the first of equals.</summary>
    ItemInstance? Best(Inventory? inventory, int function)
    {
        if (inventory is null) return null;
        ItemInstance? best = null;
        float bestQuality = float.MinValue;
        foreach (var item in inventory.All())
        {
            if (item.Type != FcsRecordType.ITEM || item.ItemFunction != function || item.Charges <= 0 || item.Quantity <= 0) continue;
            float q = Items.Info(item.Record)?.Quality ?? 0;
            if (q > bestQuality)
            {
                best = item;
                bestQuality = q;
            }
        }
        return best;
    }

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

    public void SlowWorld(World world)
    {
        if (Settings.AutoEveryTicks <= 0 || world.Tick % Settings.AutoEveryTicks != 0) return;
        var table = world.Characters;
        var next = table.Next;
        var taken = Treated(table);
        BeginSweep(world);
        try
        {
            for (int i = 0; i < table.HighWater; i++)
            {
                if (!next[i].Alive) continue;
                var cold = table.Cold(i)!;
                if (cold.IsPlayer || !Settings.AutoNpcs) continue;   // a player's medic jobs are the decision step's (MedicJobs)
                // Idle: no task, following, wandering or an implicit job.
                bool idle = next[i].Task is (byte)CharacterTask.Idle or (byte)CharacterTask.Wander or (byte)CharacterTask.Follow || Production.PermaJobs.ListJob(cold, (CharacterTask)next[i].Task) is { Implicit: true };
                if (idle) HealSomeone(world, i, taken, robotics: false, TaskSource.Npc);
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
            var taken = Treated(table);
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
            StartJob(world, slot, found.Patient, Treated(world.Characters), request);
        }
    }

}
