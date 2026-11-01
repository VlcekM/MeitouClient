using Meitou.Data.Gameplay.Ai;
using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Carrying;

/// <summary>
/// Carrying downed characters, rescue and looting (docs/game/carry-loot.md for the original's rules, docs/simulation.md "Carrying, rescue and looting" for what is built).
/// <list type="bullet">
/// <item><b>The link</b>: <see cref="CharacterCold.Carrying"/> on the carrier and <see cref="CharacterCold.CarriedBy"/> on the body, set and cleared together, in serial phases only. The
/// body is always on the left shoulder (the original's side setter, <b>Verified</b>), so a carrier needs a usable left arm. The body keeps the carrier's position (the renderer is told the
/// bone, <see cref="AttachBone"/>), weighs <c>carry person weight</c> in the carrier's load (<see cref="BodySystem"/>) and lifts a carrier's speed mode from walk to run (<see cref="MovementSystem"/>).</item>
/// <item><b>Pick up</b> (<see cref="CharacterTask.PickUp"/>): the walk to the body (the first aid pattern), then the <c>shoulder lift left</c> clip: attached at 0.4 of it, done at 0.95.
/// <b>Put down</b> (<see cref="CharacterTask.PutDown"/>): the clip backwards, let go below 0.2; a body the carrier does not regard as friendly is knocked out again.</item>
/// <item><b>Dropped</b> at once when the carrier is knocked out or dies or its left arm fails or is cut off. A friendly body that comes round is set down; a non-friendly one is knocked out again on the
/// shoulder (engine choices). A squad mate lifted awake (<see cref="CharacterCold.CarriedAwake"/>) rides until put down.</item>
/// <item><b>Rescue</b>: the rescue job, and NPC squads' knocked-out leaders (<see cref="CarrySettings.RescueNpcLeaders"/>).</item>
/// <item><b>Loot</b> (<see cref="CharacterTask.Loot"/>): the walk, then everything of the main inventory and the backpack that fits moves to the looter (<see cref="LootOrder"/>), or the body's
/// inventory opens in a window to take from (<see cref="OpenLootOrder"/>); a member of the victim's faction who sees it makes it a theft. NPCs with goal 177 loot by the original's
/// rules (CarrySystem.Scavenge.cs).</item>
/// </list>
/// Everything runs in the serial phases (<c>Inputs</c>, <c>Schedule</c>, <c>SlowWorld</c>), in slot order, so the result does not depend on threads or partitions. Placed after the body system (its
/// <c>SlowWorld</c> sees this tick's knock-outs and the corpses already removed) and before combat.
/// </summary>
public sealed partial class CarrySystem(GameConstants constants, BodyOptions options, float liftSeconds = 1, CarrySettings? settings = null) : ITickSystem, ITaskOwner, ITaskHandler, IOrderOwner, IDecisionOwner
{
    /// <summary>The bone of the carrier the body hangs from (the original's <c>Bip01 L UpperArm</c> for the left side, <b>Verified</b>).</summary>
    public const string AttachBone = "Bip01 L UpperArm";
    /// <summary>The clip of the carrier lifting (played forwards) and putting down (backwards), and the held pose of a carried body (<b>Observed</b> names).</summary>
    public const string LiftClip = "shoulder lift left", CarriedClip = "carry me";

    public CarrySettings Settings { get; } = settings ?? new CarrySettings();
    public GameConstants Constants { get; } = constants;
    /// <summary>How long the lift takes in seconds: the <c>shoulder lift left</c> clip's length over its <see cref="LiftPlaySpeed"/> (1 when the clip lengths are not known).</summary>
    public float LiftSeconds { get; set; } = liftSeconds > 0 ? liftSeconds : 1;
    /// <summary>The record's <c>play speed</c> (2 in the base data): the lift clip plays at this many clip seconds per second, forwards to lift and backwards to put down (<b>Verified</b>, docs/game/carry-loot.md "Lift and put-down timing").</summary>
    public float LiftPlaySpeed { get; set; } = 1;
    /// <summary>The live relation table: with it an ally counts as friendly. Null: only the same faction does.</summary>
    public FactionRelations? Relations { get; set; }
    /// <summary>The crime system a player's looting is reported to (a pending Looting; null: no crimes).</summary>
    public Crime.CrimeSystem? Crimes { get; set; }
    /// <summary>Re-dresses a character whose worn gear the loot window moved (null: the looks and fighters stay).</summary>
    internal CharacterAssembly? Assembly { get; set; }

    /// <summary>The game data, to price money taken out of a loot (<see cref="PlayerSystem.Cash(World, PopulationData?, ItemInstance, int)"/>); null: money moves as an item.</summary>
    internal PopulationData? Data { get; set; }
    readonly BodyOptions bodyOptions = options;
    readonly List<LootEntry> loots = [];

    /// <summary>The loots done since the last <see cref="TakeLoots"/> (tests read it, the host shows them).</summary>
    public IReadOnlyList<LootEntry> Loots => loots;

    public LootEntry[] TakeLoots()
    {
        LootEntry[] all = [.. loots];
        loots.Clear();
        return all;
    }

    // ------------------------------------------------------------------ rules

    /// <summary>
    /// Whether the left arm can carry (the original's <c>isCarryingArmOK</c>, the medical object's left-arm byte, <see cref="MedicalState.ArmWorks"/>; <b>Verified</b>: the bit and the
    /// three writers, docs/game/carry-loot.md). The right arm does not matter.
    /// </summary>
    public static bool LeftArmUsable(MedicalState medical) => medical.ArmWorks(left: true);

    /// <summary>
    /// A character that can be given a pick-up: awake, with a body, not an animal, not fighting (but for an <paramref name="ordered"/> lift: the order's new task ends the fight), not carrying
    /// or carried, with a usable left arm. An animal never carries: every carry task (rescue, leader rescue, kidnap, capture, cage, arrest, foraging) is registered for the human repertoire only
    /// and an animal plans with its own, and its context menu has no Pick Up (docs/game/carry-loot.md "Who carries").
    /// </summary>
    public static bool CanCarry(World world, CharacterCold cold, TaskType row = TaskType.LIFT_PERSON_PLAYER_ORDER, bool ordered = false) =>
        cold is { Medical: { Incapacitated: false } m, Race: not null } && (ordered || !cold.InCombat) && cold.Carrying.IsNone && cold.CarriedBy.IsNone && LeftArmUsable(m) && TaskStart.Eligible(world, cold, row);

    /// <summary>A character that may be lifted: knocked out or dead, of a <c>carriable</c> race (every base race but the Leviathan, <b>Verified</b>), not carried already, not in a cage.</summary>
    public static bool CanBeLifted(CharacterCold cold) => cold is { Medical.Incapacitated: true, Race.Carriable: true, IsImprisoned: false } && cold.CarriedBy.IsNone;

    /// <summary>
    /// <see cref="CanBeLifted"/>, or the occupant of a cage that is not effectively locked, of a <c>carriable</c> race, awake or not (the lift out of a cage, locks.md "Cages and beds";
    /// <b>engine choice</b>: whether the original lifts an awake prisoner is Unknown), or one of the player's own standing characters that carries no one (the original's Pick Up on a squad
    /// mate, ui-input.md "Context menu verbs"). The player's lift; the rescue and the arrest lift only the downed and free.
    /// </summary>
    public static bool CanBeLifted(World world, CharacterCold cold) => CanBeLifted(cold) || InOpenCage(world, cold) is not null || AwakeMate(cold);

    /// <summary>One of the player's own characters, awake, of a <c>carriable</c> race, free, carrying no one and not carried.</summary>
    /// <summary>A down body of a <c>carriable</c> race that someone else carries: what a snatching lift (<see cref="CharacterCold.Snatching"/>) may take from its carrier.</summary>
    static bool Snatchable(CharacterCold cold) => cold is { Medical.Incapacitated: true, Race.Carriable: true, IsImprisoned: false } && !cold.CarriedBy.IsNone;

    /// <summary>Whether the lift of the carrier in <paramref name="carrier"/> may still take the body in <paramref name="body"/>: <see cref="CanBeLifted(World, CharacterCold)"/>, or a snatch from another carrier.</summary>
    static bool Liftable(World world, int carrier, int body)
    {
        var table = world.Characters;
        var b = table.Cold(body)!;
        return CanBeLifted(world, b) || table.Cold(carrier)!.Snatching && Snatchable(b) && b.CarriedBy != table.IdOf(carrier);
    }

    static bool AwakeMate(CharacterCold cold) =>
        cold is { IsPlayer: true, Medical.Incapacitated: false, Race.Carriable: true, IsImprisoned: false } && cold.Carrying.IsNone && cold.CarriedBy.IsNone;

    /// <summary>The cage a prisoner that may be lifted out sits in (null: not caged, or locked in, or not liftable).</summary>
    public static Locks.Fixture? InOpenCage(World world, CharacterCold cold) =>
        cold is { IsImprisoned: true, Race.Carriable: true, InWhat: { } id } && cold.CarriedBy.IsNone && world.Fixtures.Find(id) is { } cage && cage.Lock is not { EffectivelyLocked: true } ? cage : null;

    /// <summary>How near a carrier must come to lift a body: <see cref="CarrySettings.Reach"/>, or for a caged one the cage's <c>use range</c> + <see cref="Locks.LockSystem.Reach"/> (the cage is in the way).</summary>
    float ReachOf(World world, CharacterCold body) =>
        body.IsImprisoned && body.InWhat is { } id && world.Fixtures.Find(id) is { } cage ? MathF.Max(Settings.Reach, cage.UseRange + Locks.LockSystem.Reach) : Settings.Reach;

    /// <summary>A character that may be looted: knocked out, dead, playing dead or carried, with an inventory (the bedded and imprisoned that the original's <c>isItemLootable</c> also takes are opened as a pickpocket, <see cref="CanBePickpocketed"/>).</summary>
    /// <remarks>isItemLootable's down test (<c>FUN_14059dcc0</c> over <c>FUN_1405c9e10</c>, <b>Verified</b>): out, playing dead or carried; a crawler is not down.</remarks>
    public static bool CanBeLooted(CharacterCold cold) => cold is { Inventory: not null } && (cold.Medical is { Incapacitated: true } or { PlayingDead: true } || !cold.CarriedBy.IsNone);

    /// <summary>
    /// A character whose inventory an <see cref="OpenLootOrder"/> may open: a lootable one, or one of the player's own standing (the original's Trade, verb 26, on a player character;
    /// engine choice: the walk and the window are the loot's).
    /// </summary>
    public static bool CanBeOpened(CharacterCold cold) => CanBeLooted(cold) || cold is { IsPlayer: true, Inventory: not null };

    /// <summary>
    /// Another faction's character that is not down but whose inventory Loot (26) opens as a pickpocket: asleep (the original's state 1) or imprisoned (state 2), not carried
    /// (docs/game/ui-input.md "Context menu verbs"); what is taken from it rolls the steal check (<see cref="Crime.CrimeSystem.Pickpocket"/>).
    /// </summary>
    public static bool CanBePickpocketed(CharacterTable table, int slot) =>
        table.Cold(slot) is { IsPlayer: false, Inventory: not null, Medical.Incapacitated: false } c && c.CarriedBy.IsNone && (c.IsImprisoned || Asleep(table, slot));

    /// <summary>Whether an <see cref="OpenLootOrder"/> may open the inventory of slot <paramref name="slot"/>: lootable (down, playing dead, carried), the player's own, or a pickpocket target; the menu's test (<see cref="CharacterSnapshot.CanOpenInventory"/>).</summary>
    public static bool Openable(CharacterTable table, int slot) => CanBeOpened(table.Cold(slot)!) || CanBePickpocketed(table, slot);

    /// <summary>Whether slot <paramref name="i"/>'s loot task is the walk of an <see cref="OpenLootOrder"/> (it opens the window instead of taking all).</summary>
    static bool Opening(World world, int i) =>
        world.Player.LootWalker == world.Characters.IdOf(i) && world.Player.OpeningLoot == world.Characters.Cold(i)!.CarryTarget;

    /// <summary>
    /// Whether <paramref name="a"/> regards <paramref name="b"/> as friendly for the put-down KO: the same faction, or allied factions (relation 50 or more) when a table is known
    /// (engine reading of <c>CharacterAnimal::vfunc_126</c>, whose last faction test is <b>Unknown</b>).
    /// </summary>
    public bool Friendly(CharacterCold a, CharacterCold b) =>
        a.Faction == b.Faction || Relations is { } r && r.Contains(a.Faction) && r.Contains(b.Faction) && r.IsAlly(a.Faction, b.Faction);

    static float Gap(in CharacterHot a, in CharacterHot b)
    {
        float dx = a.Position.X - b.Position.X, dz = a.Position.Z - b.Position.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    static IReadOnlyList<CharacterId> Who(World world, IReadOnlyList<CharacterId> ids) => ids.Count > 0 ? ids : world.Player.Selection.ToArray();

    // ------------------------------------------------------------------ orders

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        var table = world.Characters;
        foreach (var command in commands)
        {
            switch (command)
            {
                case PickUpOrder order:
                {
                    // The order's lift (225 Lifting/Snatching Order) may take a body another carries; a kidnap (246) may not. The nearest of the characters that can lift it gets it.
                    var lift = new OrderEntry(order.Kidnap ? TaskType.KIDNAP_ORDER : TaskType.LIFT_PERSON_PLAYER_ORDER, this) { Target = order.Target };
                    if (table.TryResolveNext(order.Target, out int body) && LiftOf(world, lift, body) && Nearest(table, Who(world, order.Carriers), body, s => PlayerOrders.CanRun(world, s, lift).CanRun) is int carrier)
                        PlayerOrders.Give(world, carrier, lift, order.Queued);
                    break;
                }
                case LootOrder loot:
                {
                    var entry = new OrderEntry(TaskType.LOOT_TARGET, this) { Target = loot.Target };
                    if (table.TryResolveNext(loot.Target, out int victim) && CanBeLooted(table.Cold(victim)!) && Nearest(table, Who(world, loot.Looters), victim, s => PlayerOrders.CanRun(world, s, entry).CanRun) is int looter)
                        PlayerOrders.Give(world, looter, entry, loot.Queued);
                    break;
                }
                case OpenLootOrder open:
                {
                    var entry = new OrderEntry(TaskType.LOOT_TARGET, this) { Target = open.Target, Detail = OpenInventory };
                    if (table.TryResolveNext(open.Target, out int body2) && Openable(table, body2) && Nearest(table, Who(world, open.Looters), body2, s => PlayerOrders.CanRun(world, s, entry).CanRun) is int opener)
                        PlayerOrders.Give(world, opener, entry, open.Queued);
                    break;
                }
                case TakeLootItem take:
                    TakeLoot(world, take);
                    break;
                case PutLootItem put:
                    PutLoot(world, put);
                    break;
                case CloseContainer:
                    world.Player.OpenLoot = world.Player.OpeningLoot = CharacterId.None;
                    break;
                case StealthKoOrder ko:
                    BeginKnockOut(world, ko);
                    break;
                case PutDownOrder put:
                    foreach (var id in Who(world, put.Carriers))
                        if (table.TryResolveNext(id, out int slot) && table.Cold(slot) is { Carrying.IsNone: false } c)
                            PlayerOrders.Give(world, slot, new OrderEntry(TaskType.PUT_DOWN_OBJECT, this) { Target = c.Carrying }, put.Queued);
                    break;
                case RescueJobCommand job:
                    foreach (var id in Who(world, job.Characters))
                    {
                        if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { IsPlayer: true } cold) continue;
                        if (job.Job) Production.PermaJobs.Add(cold, TaskType.FIND_AND_RESCUE_IF_THERES_BEDS, Production.PermaJob.Untargeted(TaskType.FIND_AND_RESCUE_IF_THERES_BEDS));
                        else PlayerOrders.Give(world, slot, new OrderEntry(TaskType.None, this) { Detail = RescueButton }, job.Queued);   // the button: one look for a body
                    }
                    break;
            }
        }
    }

    /// <summary>The <see cref="OrderEntry.Detail"/> of the Open Inventory order (a loot that opens the window) and of the Rescue button's one-shot order (the job's look, then a lift).</summary>
    static readonly object OpenInventory = new(), RescueButton = new();

    // Saved: the Open Inventory loot is kind 1; the Rescue button's look (row None) is not saved.
    int IOrderOwner.SaveKind(OrderEntry order) => order.Detail is null ? 0 : order.Detail == OpenInventory ? 1 : -1;
    object? IOrderOwner.LoadDetail(World world, CharacterId self, TaskType row, int kind, CharacterId target) => kind == 1 ? OpenInventory : null;

    /// <summary>Whether <paramref name="body"/> can be taken by the lift order (a kidnap's sleeper too; a snatch from another carrier only by the order 225).</summary>
    static bool LiftOf(World world, OrderEntry lift, int body)
    {
        var bc = world.Characters.Cold(body)!;
        bool kidnap = lift.Row == TaskType.KIDNAP_ORDER;
        return CanBeLifted(world, bc) || kidnap && CanBeKidnapped(world.Characters, body) || !kidnap && Snatchable(bc);
    }

    /// <summary>
    /// The carry orders' checks: the target must still fit the verb (else the order leaves the queue); the character must be able to lift (<see cref="CanCarry"/>, ordered: a fight ends with the
    /// order's task), loot (awake, an inventory, not carried), knock out (<see cref="CanKnockOut"/>) or put down (awake); the Rescue button needs a rescuer (not fleeing, able to lift, not on
    /// Hold) and a free bed.
    /// </summary>
    OrderCheck IOrderOwner.CanRun(World world, int slot, OrderEntry order)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        if (order.Detail == RescueButton)
        {
            if (!cold.IsPlayer) return OrderCheck.Gone;
            if (cold.Fleeing || !CanCarry(world, cold, TaskType.FIND_AND_RESCUE_IF_THERES_BEDS, ordered: true) || !Rescuing(cold)) return OrderCheck.Blocked();
            // The one-shot order has no bed to take anyone to: said, not dropped silently (engine choice; the job just waits with desire 0).
            return world.Fixtures.FindBed(world, slot, Relations) is null ? OrderCheck.Blocked(Meitou.Localisation.Loc.Tr("No free bed to take anyone to.")) : OrderCheck.Run;
        }
        if (order.Row == TaskType.PUT_DOWN_OBJECT)
        {
            if (cold.Carrying.IsNone) return OrderCheck.Gone;
            return cold.Medical is { Incapacitated: false } ? OrderCheck.Run : OrderCheck.Blocked();
        }
        if (!table.TryResolveNext(order.Target, out int target) || target == slot) return OrderCheck.Gone;
        var tc = table.Cold(target)!;
        switch (order.Row)
        {
            case TaskType.LIFT_PERSON_PLAYER_ORDER or TaskType.KIDNAP_ORDER:
                if (!LiftOf(world, order, target)) return OrderCheck.Gone;
                return CanCarry(world, cold, order.Row, ordered: true) && cold.Carrying != order.Target ? OrderCheck.Run : OrderCheck.Blocked();
            case TaskType.LOOT_TARGET:
                if (order.Detail == OpenInventory ? !Openable(table, target) : !CanBeLooted(tc)) return OrderCheck.Gone;
                return cold is { Medical.Incapacitated: false, Inventory: not null } && cold.CarriedBy.IsNone ? OrderCheck.Run : OrderCheck.Blocked();
            case TaskType.STEALTH_KNOCKOUT:
                if (!CanBeKnockedOut(tc)) return OrderCheck.Gone;
                return CanKnockOut(cold) ? OrderCheck.Run : OrderCheck.Blocked();
        }
        return OrderCheck.Gone;
    }

    void IOrderOwner.Start(World world, int slot, OrderEntry order)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        if (order.Detail == RescueButton)
        {
            RescueSomeone(world, slot, Lifting(table), TaskSource.Order);
            return;
        }
        switch (order.Row)
        {
            case TaskType.PUT_DOWN_OBJECT:
                BeginPutDown(world, slot, TaskSource.Order);
                break;
            case TaskType.LIFT_PERSON_PLAYER_ORDER or TaskType.KIDNAP_ORDER:
                if (Start(world, slot, CharacterTask.PickUp, order.Row, TaskSource.Order, order.Target, CharacterTask.Idle))
                {
                    cold.Kidnapping = order.Row == TaskType.KIDNAP_ORDER;
                    cold.Snatching = !cold.Kidnapping;
                }
                break;
            case TaskType.LOOT_TARGET when order.Detail == OpenInventory:
                Start(world, slot, CharacterTask.Loot, TaskType.LOOT_TARGET, TaskSource.Order, order.Target, CharacterTask.Idle);
                (world.Player.OpenLoot, world.Player.OpeningLoot, world.Player.LootWalker) = (CharacterId.None, order.Target, table.IdOf(slot));
                break;
            case TaskType.LOOT_TARGET:
                if (world.Player.LootWalker == table.IdOf(slot)) world.Player.OpeningLoot = CharacterId.None;
                Start(world, slot, CharacterTask.Loot, TaskType.LOOT_TARGET, TaskSource.Order, order.Target, CharacterTask.Idle);
                break;
            case TaskType.STEALTH_KNOCKOUT:
                Start(world, slot, CharacterTask.StealthKo, TaskType.STEALTH_KNOCKOUT, TaskSource.Order, order.Target, CharacterTask.Idle);
                break;
        }
    }

    /// <summary>
    /// Starts a pick-up of <paramref name="body"/> by the character in <paramref name="carrier"/> (serial phases; the arrest and kidnap systems' lift), going back to <paramref name="resume"/>
    /// afterwards: false when it cannot carry or the body cannot be lifted. With <paramref name="kidnap"/> a sleeper may be taken too (<see cref="CanBeKidnapped"/>): the lift is then
    /// a kidnap, which rolls on arrival. A job's lift (<paramref name="source"/> <see cref="TaskSource.Job"/>, a carrying errand) names its <paramref name="job"/> and <paramref name="row"/>.
    /// </summary>
    public bool BeginPickUp(World world, int carrier, CharacterId body, CharacterTask resume = CharacterTask.Idle, bool kidnap = false, TaskSource source = TaskSource.Npc, int job = -1, TaskType? row = null)
    {
        var table = world.Characters;
        var lift = kidnap ? TaskType.KIDNAP_ORDER : TaskType.LIFT_PERSON_PLAYER_ORDER;
        if (!table.TryResolveNext(body, out int b) || b == carrier || table.Cold(carrier) is not { } c || !CanCarry(world, c, lift)) return false;
        bool asleep = !CanBeLifted(table.Cold(b)!);
        if (asleep && !(kidnap && CanBeKidnapped(table, b))) return false;
        if (!Start(world, carrier, CharacterTask.PickUp, row ?? lift, source, body, resume, job)) return false;
        c.Kidnapping = asleep;
        return true;
    }

    /// <summary>
    /// Starts the put-down (row 69) of the body the character in <paramref name="slot"/> carries (the order 'Put Down', the carrying jobs' and town duties' drop; serial phases): false when it
    /// carries nothing or cannot act, or is putting down already.
    /// </summary>
    public bool BeginPutDown(World world, int slot, TaskSource source = TaskSource.Npc)
    {
        var table = world.Characters;
        if (table.Cold(slot) is not { Carrying.IsNone: false, Medical.Incapacitated: false } cold || table.Next[slot].Task == (byte)CharacterTask.PutDown) return false;
        return Start(world, slot, CharacterTask.PutDown, TaskType.PUT_DOWN_OBJECT, source, cold.Carrying, CharacterTask.Idle);
    }

    /// <summary>The put-down's start: the carrier stands and plays the lift clip backwards.</summary>
    static void PutDownStart(CharacterTable table, int slot)
    {
        var cold = table.Cold(slot)!;
        TaskStart.Stand(table, slot);
        table.Next[slot].Task = (byte)CharacterTask.PutDown;
        table.Next[slot].TaskTime = 0;
        cold.CarryTarget = cold.Carrying;
        cold.LiftTime = 0;
    }

    /// <summary>
    /// A hit outside a fight ended the character's task (<c>Character::_startStumble</c>, docs/game/combat.md "Stagger outside a fight"). The put-down's end handler
    /// (<c>FUN_1403509f0</c>, <b>Verified</b>) always releases the body (and knocks it out again when the carrier does not regard it as friendly), so a put-down cut off by a hit is
    /// finished at once, not started over; a lift already past its attach point (the link is made in the task's update at 0.4 of the clip) keeps the body. Serial phases.
    /// </summary>
    public void Interrupted(World world, int slot)
    {
        var table = world.Characters;
        if (table.Cold(slot) is not { } cold) return;
        byte task = table.Next[slot].Task;
        if (task == (byte)CharacterTask.PutDown && !cold.Carrying.IsNone && table.TryResolveNext(cold.Carrying, out int body))
        {
            Unlink(world, slot, body, drop: false);
            End(world, slot);
        }
        else if (task == (byte)CharacterTask.PickUp && !cold.Carrying.IsNone && cold.Carrying == cold.CarryTarget) End(world, slot);
    }

    /// <summary>Lets go of the carried body where the carrier stands, without the put-down knock-out (serial phases; the arrest system's put-in-cage). False when it carries nothing.</summary>
    public bool LetGo(World world, int carrier)
    {
        var table = world.Characters;
        if (table.Cold(carrier) is not { Carrying.IsNone: false } c || !table.TryResolveNext(c.Carrying, out int b)) return false;
        Unlink(world, carrier, b, drop: true);
        return true;
    }


    /// <summary>The slot among <paramref name="ids"/> that <paramref name="accept"/> takes and is nearest the target (ties: the first listed).</summary>
    static int? Nearest(CharacterTable table, IReadOnlyList<CharacterId> ids, int target, Func<int, bool> accept)
    {
        int? best = null;
        float bestD = float.MaxValue;
        foreach (var id in ids)
        {
            if (!table.TryResolveNext(id, out int s) || !accept(s)) continue;
            float d = Gap(table.Next[s], table.Next[target]);
            if (d < bestD) { best = s; bestD = d; }
        }
        return best;
    }

    /// <summary>Starts a pick-up, a loot or a stealth knock-out of <paramref name="target"/> as row <paramref name="row"/> through <see cref="TaskStart.Start"/>: false when the row is not the character's.</summary>
    static bool Start(World world, int slot, CharacterTask task, TaskType row, TaskSource source, CharacterId target, CharacterTask resume, int job = -1) =>
        TaskStart.Start(world, slot, new TaskRequest(task, row, source) { Target = target, Resume = resume, Job = job }) == StartResult.Started;

    public void RegisterTasks(TaskHandlers tasks)
    {
        tasks.Register(CharacterTask.PickUp, this);
        tasks.Register(CharacterTask.PutDown, this);
        tasks.Register(CharacterTask.Loot, this);
        tasks.Register(CharacterTask.StealthKo, this);
    }

    void ITaskHandler.Start(World world, int slot, in TaskRequest request)
    {
        if (request.Task == CharacterTask.PutDown) PutDownStart(world.Characters, slot);
        else Approach(world.Characters, slot, request.Target, request.Task);
    }

    /// <summary>
    /// A pick-up, put-down, loot or knock-out that ends lets go of its target (a body already on the shoulder stays there; the task it goes back to is the new start's, or its provenance's
    /// when it ends by itself). One that another task replaces or the Stop order ends also drops its kidnap, snatch and foraging marks; one that ends by itself keeps them for its system.
    /// </summary>
    void ITaskHandler.End(World world, int slot, CharacterTask task, TaskEnd reason)
    {
        var cold = world.Characters.Cold(slot)!;
        cold.CarryTarget = CharacterId.None;
        cold.LiftTime = -1;
        cold.LootFlags = LootStart.None;
        if (reason == TaskEnd.Done) return;
        cold.Kidnapping = false;
        cold.Snatching = false;
        cold.Foraging = false;
    }


    /// <summary>The act-out's start of a pick-up, a loot or a stealth knock-out (serial phases): the character stands, then walks to the target.</summary>
    static void Approach(CharacterTable table, int slot, CharacterId target, CharacterTask task)
    {
        var cold = table.Cold(slot)!;
        ref var n = ref table.Next[slot];
        TaskStart.Stand(table, slot);
        n.Task = (byte)task;
        n.Mode = SpeedMode.Free;
        n.TaskTime = 0;
        cold.CarryTarget = target;
        cold.LiftTime = -1;
        cold.Kidnapping = false;
        cold.Snatching = false;
        cold.Foraging = false;
        cold.LootFlags = LootStart.None;
    }

    /// <summary>Ends a pick-up, put-down, loot or knock-out by itself (serial phases; <see cref="TaskEnd.Done"/>): back to the task before it.</summary>
    static void End(World world, int slot)
    {
        if (world.Characters.Next[slot].Task is (byte)CharacterTask.PickUp or (byte)CharacterTask.PutDown or (byte)CharacterTask.Loot or (byte)CharacterTask.StealthKo)
        {
            TaskStart.End(world, slot, TaskEnd.Done);
            return;
        }
        var cold = world.Characters.Cold(slot)!;
        cold.CarryTarget = CharacterId.None;
        cold.LiftTime = -1;
        cold.LootFlags = LootStart.None;
    }

    // ------------------------------------------------------------------ the walk

    public void Schedule(World world)
    {
        ScheduleKnockOuts(world);
        var table = world.Characters;
        var next = table.Next;
        float dt = world.TickSeconds;
        for (int i = 0; i < table.HighWater; i++)
        {
            ref var n = ref next[i];
            if (!n.Alive || n.Task is not ((byte)CharacterTask.PickUp or (byte)CharacterTask.Loot)) continue;
            var cold = table.Cold(i)!;
            bool lifting = n.Task == (byte)CharacterTask.PickUp;
            if (cold.Medical is not { Incapacitated: false } || cold.InCombat || !table.TryResolveNext(cold.CarryTarget, out int t)
                || (lifting ? cold.LiftTime < 0 && !(CanCarry(world, cold, cold.Kidnapping ? TaskType.KIDNAP_ORDER : TaskType.LIFT_PERSON_PLAYER_ORDER) && (Liftable(world, i, t) || cold.Kidnapping && CanBeKidnapped(table, t))) : !(Opening(world, i) ? Openable(table, t) : CanBeLooted(table.Cold(t)!))))
            {
                if (!(lifting && cold.LiftTime >= 0)) End(world, i);   // a lift under way is finished or broken off in SlowWorld
                continue;
            }
            const ushort Path = (ushort)MoveFlags.AnyPath;
            if (Gap(next[i], next[t]) <= (lifting ? ReachOf(world, table.Cold(t)!) : Settings.Reach))
            {
                if ((n.Flags & Path) != 0)
                {
                    n.Flags = (ushort)(n.Flags & ~Path);
                    n.PathCursor = 0;
                    cold.PathRequest = 0;
                }
                n.TaskTime = 0;
                if (lifting && cold.LiftTime < 0) cold.LiftTime = 0;   // in reach: the lift clip starts
                continue;
            }
            if (lifting && cold.LiftTime >= 0) continue;   // lifting: it does not walk after the body
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

    // ------------------------------------------------------------------ lifting, putting down, the links, rescue

    public void SlowWorld(World world)
    {
        var table = world.Characters;
        var next = table.Next;
        float dt = world.TickSeconds;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!next[i].Alive) continue;
            switch ((CharacterTask)next[i].Task)
            {
                case CharacterTask.PickUp: Lift(world, table, i, dt); break;
                case CharacterTask.PutDown: PutDown(world, i, dt); break;
                case CharacterTask.Loot: Loot(world, table, i, dt); break;
                case CharacterTask.StealthKo: Swing(world, table, i, dt); break;
            }
        }
        if (world.Tick % RestoreEveryTicks == 0) RestoreLinks(world);
        LootWindow(world);
        KeepLinks(world);
        if (Settings.AutoEveryTicks > 0)
        {
            bool beat = world.Tick % Settings.AutoEveryTicks == 0;
            if (beat) Rescue(world, table);
            Scavenge(world, table);   // each looter on its slot's turn
            if (beat) Arm(world, table);
        }
    }

    // How often the saved carries still waiting for their partner are looked at (engine choice; the original checks its deferred link every frame, FUN_140797ec0).
    const int RestoreEveryTicks = 20;

    /// <summary>
    /// The carries a save held (<see cref="Saving.SavedCharacterLink.CarryPartner"/>; docs/formats/save.md "Carry links"): a carrier whose partner is now in the world takes it up again at
    /// once, without the lift. One waits while its partner is not loaded (the original's deferred link, <c>FUN_140797ec0</c>); either one dead or gone drops the link.
    /// </summary>
    void RestoreLinks(World world)
    {
        var table = world.Characters;
        var next = table.Next;
        Dictionary<(int C, int I), int>? bySave = null;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!next[i].Alive || table.Cold(i) is not { Save: { CarryPartner.IsNull: false } link } cold) continue;
            if (!link.CarryIsCarrier) { if (!cold.CarriedBy.IsNone) link.CarryPartner = Meitou.Data.Save.Hand.Null; continue; }   // the carrier's side links it
            if (bySave is null)
            {
                bySave = [];
                for (int s = 0; s < table.HighWater; s++)
                    if (next[s].Alive && table.Cold(s)?.Save is { } other) bySave[(other.Source.Handle.C, other.Source.Handle.I)] = s;
            }
            var hand = link.CarryPartner;
            if (!bySave.TryGetValue((hand.C, hand.I), out int b) || b == i) continue;
            link.CarryPartner = Meitou.Data.Save.Hand.Null;
            var body = table.Cold(b)!;
            body.Save?.CarryPartner = Meitou.Data.Save.Hand.Null;
            if (!cold.Carrying.IsNone || !body.CarriedBy.IsNone || cold.Medical is not { Incapacitated: false }) continue;
            if (body.Medical is { Incapacitated: false } && Friendly(cold, body)) body.CarriedAwake = true;
            Link(table, i, b);
        }
    }

    void Lift(World world, CharacterTable table, int i, float dt)
    {
        var cold = table.Cold(i)!;
        if (cold.LiftTime < 0) return;   // still walking
        float before = cold.LiftTime;
        // A kidnap rolls on arrival (Task_Kidnapping's override of the lift): a miss ends the task.
        if (before == 0 && cold.Kidnapping)
        {
            cold.Kidnapping = false;
            if (!Kidnap(world, table, i))
            {
                End(world, i);
                return;
            }
        }
        cold.LiftTime += dt;
        bool attached = !cold.Carrying.IsNone && cold.Carrying == cold.CarryTarget;
        if (!attached && cold.LiftTime > CarrySettings.AttachAt * LiftSeconds)
        {
            // The body is attached once, at 0.4 of the clip, if it can still be lifted and is still near (the reach was tested when the clip started).
            if (!table.TryResolveNext(cold.CarryTarget, out int b) || !CanCarry(world, cold, cold.Kidnapping ? TaskType.KIDNAP_ORDER : TaskType.LIFT_PERSON_PLAYER_ORDER) || !Liftable(world, i, b)
                || Gap(table.Next[i], table.Next[b]) > 2 * ReachOf(world, table.Cold(b)!))   // pushed about while lifting: some slack, not a pick-up from afar
            {
                End(world, i);
                return;
            }
            // Out of an unlocked cage: no longer held there (and the crimes of lifting someone out of another faction's cage).
            if (table.Cold(b)!.IsImprisoned && !world.Fixtures.LiftOut(world, cold.CarryTarget, i, Crimes))
            {
                End(world, i);
                return;
            }
            // Out of a bed: the attach clears the state (carry-loot.md, Character +0x2f8).
            if (table.Cold(b)!.InSomething == Locks.HeldState.Bed) world.Fixtures.LeaveBed(world, cold.CarryTarget);
            var body = table.Cold(b)!;
            // A snatch: the body's carrier lets go of it first (FUN_14034fbe0's unlink, as a drop: no put-down knock-out).
            if (table.TryResolveNext(body.CarriedBy, out int holder)) Unlink(world, holder, b, drop: true);
            if (body.Medical is { Incapacitated: false } && Friendly(cold, body))
            {
                // A squad mate lifted awake: it leaves its fight and task and rides until put down.
                TaskStart.End(world, b, TaskEnd.Stopped);
                body.CarriedAwake = true;
            }
            Link(table, i, b);
        }
        else if (!attached && before > CarrySettings.AttachAt * LiftSeconds) { End(world, i); return; }   // dropped again mid-lift
        if (cold.LiftTime >= CarrySettings.DoneAt * LiftSeconds) End(world, i);
    }

    void PutDown(World world, int i, float dt)
    {
        var table = world.Characters;
        var cold = table.Cold(i)!;
        if (cold.Carrying.IsNone || !table.TryResolveNext(cold.Carrying, out int b))
        {
            End(world, i);
            return;
        }
        cold.LiftTime += dt;
        // The clip runs backwards from its end; the body is let go when it is below 0.2 of the way.
        if (LiftSeconds - cold.LiftTime >= CarrySettings.ReleaseBelow * LiftSeconds) return;
        Unlink(world, i, b, drop: false, released: true);
        End(world, i);
    }

    /// <summary>Links a carrier and a body (serial). The body stops whatever it did and takes the carrier's place.</summary>
    static void Link(CharacterTable table, int carrier, int body)
    {
        var c = table.Cold(carrier)!;
        var b = table.Cold(body)!;
        c.Carrying = table.IdOf(body);
        b.CarriedBy = table.IdOf(carrier);
        TaskStart.Stand(table, body);
        ref var n = ref table.Next[body];
        n.Velocity = Vector3.Zero;
        n.Position = table.Next[carrier].Position;
        n.Yaw = table.Next[carrier].Yaw;
        if (b.Animation is { } anim) anim.Action = -1;   // no falling or getting up while held
    }

    /// <summary>
    /// Ends a link (serial). <paramref name="drop"/>: the body falls where the carrier stands (the carrier went down, lost its arm or turned to fight); else it is set down beside the
    /// carrier and, when the carrier does not regard it as friendly, knocked out again (<see cref="MedicalState.PutDownKoSeconds"/>). <paramref name="released"/>: the put-down ran its
    /// course (its update's release, <c>FUN_1403504f0</c>), which also spares a chained slave of the carrier's faction (<c>FUN_14032e2a0</c>); a put-down cut short (its end handler,
    /// <c>FUN_1403509f0</c>) has no such test.
    /// </summary>
    void Unlink(World world, int carrier, int body, bool drop, bool released = false)
    {
        var table = world.Characters;
        var c = table.Cold(carrier)!;
        var b = table.Cold(body)!;
        c.Carrying = CharacterId.None;
        b.CarriedBy = CharacterId.None;
        b.CarriedAwake = false;
        ref var bn = ref table.Next[body];
        ref readonly var cn = ref table.Next[carrier];
        bn.Velocity = Vector3.Zero;
        bn.Position = cn.Position;
        if (!drop)
        {
            // Beside the carrier, on its left (engine choice), when that ground is walkable.
            var side = new Vector2(MathF.Cos(cn.Yaw), -MathF.Sin(cn.Yaw)) * Settings.PutDownSide;
            float x = cn.Position.X + side.X, z = cn.Position.Z + side.Y;
            if (world.Walkability.IsWalkable(x, z, cn.Position.Y)) bn.Position = new Vector3(x, world.Walkability.GroundHeight(x, z, cn.Position.Y), z);
            if (!Friendly(c, b) && !(released && FirstAid.FirstAidSystem.OwnedChained(c, b))) KnockOutHeld(c, b);
        }
    }

    /// <summary>The put-down KO of a body its carrier does not regard as friendly (nothing for the dead).</summary>
    void KnockOutHeld(CharacterCold carrier, CharacterCold body)
    {
        if (body.Medical is not { Dead: false } medical || body.Race is not { } race) return;
        var ctx = new MedicalContext(Constants, bodyOptions, race) { Toughness = body.Stats?.Toughness ?? 0, Strength = body.Stats?.Strength ?? 50, HasStats = body.Stats is not null };
        medical.KnockOut(medical.PutDownKoSeconds(ctx, MedicalState.PutDownKoFactor(carrier.Stats?[StatsEnumerated.Assassination] ?? 0)));
    }

    /// <summary>The links every tick (serial, after the body tick): drops, wake-ups, broken links, and the body kept at its carrier.</summary>
    void KeepLinks(World world)
    {
        var table = world.Characters;
        var next = table.Next;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!next[i].Alive) continue;
            var cold = table.Cold(i)!;
            if (!cold.CarriedBy.IsNone && (!table.TryResolveNext(cold.CarriedBy, out int k) || table.Cold(k)!.Carrying != table.IdOf(i)))
                (cold.CarriedBy, cold.CarriedAwake) = (CharacterId.None, false);   // the carrier is gone: the body stays where it was
            if (cold.Carrying.IsNone) continue;
            if (!table.TryResolveNext(cold.Carrying, out int b) || table.Cold(b)!.CarriedBy != table.IdOf(i))
            {
                cold.Carrying = CharacterId.None;   // the body is gone (a corpse removed, a zone unloaded)
                continue;
            }
            var body = table.Cold(b)!;
            // A carrier that is knocked out or dies, or whose left arm goes limp or is cut off, drops the body (the original's ragdoll switch and limb removal).
            if (cold.Medical is not { Incapacitated: false } medical || !LeftArmUsable(medical))
            {
                Unlink(world, i, b, drop: true);
                continue;
            }
            if (body.Medical is { Incapacitated: true }) body.CarriedAwake = false;   // knocked out on the shoulder: set down when it comes round
            else if (body.Medical is not null && !body.CarriedAwake)
            {
                // The body came round. A friendly one is set down (the AI's PUT_DOWN_CARRIED_DUDE_IF_THEY_CAN_WALK); a captive is knocked out again on the shoulder (engine stand-ins).
                if (Friendly(cold, body)) { Unlink(world, i, b, drop: false); continue; }
                KnockOutHeld(cold, body);
            }
            ref var bn = ref next[b];
            bn.Position = next[i].Position;
            bn.Yaw = next[i].Yaw;
            bn.Velocity = Vector3.Zero;
        }
    }

    // ------------------------------------------------------------------ looting

    void Loot(World world, CharacterTable table, int i, float dt)
    {
        var cold = table.Cold(i)!;
        if (!table.TryResolveNext(cold.CarryTarget, out int t) || Gap(table.Next[i], table.Next[t]) > ReachOf(world, table.Cold(t)!)) return;   // still walking (Schedule ends a loot whose target went)
        var victim = table.Cold(t)!;
        bool opening = Opening(world, i);
        if (cold.Inventory is not { } bag || !(opening ? Openable(table, t) : CanBeLooted(victim)))
        {
            End(world, i);
            return;
        }
        if (cold.Foraging)
        {
            ForageTake(world, table, i, t, dt);
            return;
        }
        if (!cold.IsPlayer && !opening)
        {
            NpcLoot(world, table, i, t, dt);
            return;
        }
        var player = world.Player;
        if (opening)
        {
            // An open-loot order: the window opens instead of a take-all.
            (player.OpenLoot, player.LootOpener, player.OpeningLoot) = (player.OpeningLoot, player.LootWalker, CharacterId.None);
            End(world, i);
            return;
        }
        int death = DeathUnits(victim.Inventory!);
        int cashed = cold.Faction == player.Faction ? CashMoney(world, victim.Inventory!) : 0;
        var (taken, left) = TakeAll(bag, victim.Inventory!, Crime.CrimeSystem.IsLootTheft(cold, victim) ? StolenMark.Of(victim, world.Characters.IdOf(t)) : null);
        taken += cashed;
        Butcher(world, i, t, death);
        bool seen = Looted(world, table, i, t, taken, left, crime: true);
        if (cold.IsPlayer)
        {
            world.Post(left > 0 ? $"{cold.Name} took {taken} items from {victim.Name}; {left} did not fit" : $"{cold.Name} took {taken} items from {victim.Name}");
            if (seen) world.Post($"{cold.Name} was seen looting {victim.Name}");
        }
        End(world, i);
    }

    /// <summary>
    /// What a loot of <paramref name="taken"/> items sets off: being seen by the victim's faction (true), the pending Looting crime, the <see cref="Loots"/> entry. No relation event: none of
    /// the game's relation events comes from looting (docs/game/factions-squads-towns.md 3.5); the crime's witnesses do the rest.
    /// </summary>
    bool Looted(World world, CharacterTable table, int i, int t, int taken, int left, bool crime = true)
    {
        var cold = table.Cold(i)!;
        var victim = table.Cold(t)!;
        bool seen = taken > 0 && cold.IsPlayer && !victim.IsPlayer && cold.Faction != victim.Faction && Witnessed(world, table, i, t);
        if (crime && taken > 0) Crimes?.Loot(world, i, t);   // one item at a time: the take's own crime (Crime.CrimeSystem.Pickpocket)
        loots.Add(new LootEntry(world.Tick, table.IdOf(i), table.IdOf(t), taken, left, seen));
        return seen;
    }

    /// <summary>
    /// The open loot window (<see cref="PlayerState.OpenLoot"/>, engine choices): a walk to open one is dropped when the walker gets another task; an open one closes when the body can no longer
    /// be opened (it came round, or is gone; a carried body stays lootable) or the looter is down, carried, gone or farther than 1.5 x <see cref="CarrySettings.Reach"/>.
    /// </summary>
    void LootWindow(World world)
    {
        var player = world.Player;
        var table = world.Characters;
        if (!player.OpeningLoot.IsNone && (!table.TryResolveNext(player.LootWalker, out int w) || table.Next[w].Task != (byte)CharacterTask.Loot || table.Cold(w)!.CarryTarget != player.OpeningLoot))
            player.OpeningLoot = CharacterId.None;
        if (!player.OpenLoot.IsNone && OpenLootPair(world) is null) player.OpenLoot = CharacterId.None;
        if (player.OpenLoot.IsNone) seenLoot = CharacterId.None;
    }

    /// <summary>The open loot's looter and body slots while it may stay open (see <see cref="LootWindow"/>), else null.</summary>
    (int Looter, int Body)? OpenLootPair(World world)
    {
        var player = world.Player;
        var table = world.Characters;
        return table.TryResolveNext(player.OpenLoot, out int t) && table.Cold(t) is { } body && Openable(table, t)
            && table.TryResolveNext(player.LootOpener, out int o) && table.Cold(o) is { Medical.Incapacitated: false, Inventory: not null } looter && looter.CarriedBy.IsNone
            && Gap(table.Next[o], table.Next[t]) <= ReachOf(world, body) * 1.5f ? (o, t) : null;
    }

    /// <summary>
    /// A right click on an item of the open loot (its grids, bag or worn gear): units into the looter's inventory (<see cref="InventoryCommands.MoveInto"/>; what does not fit stays), marked
    /// stolen when the loot is a theft (<see cref="Crime.CrimeSystem.IsLootTheft"/>). Gear changing hands re-dresses both.
    /// </summary>
    void TakeLoot(World world, TakeLootItem take)
    {
        if (OpenLootPair(world) is not var (i, t)) return;
        var table = world.Characters;
        var cold = table.Cold(i)!;
        var victim = table.Cold(t)!;
        var from = victim.Inventory!;
        var token = take.Item;
        if (from.All().ElementAtOrDefault(token.Index) is not { } item || item.Record != token.Record || item.Section != token.Section || item.X != token.X || item.Y != token.Y ||
            item.Quantity != token.Quantity || !InventoryCommands.IsMovableSection(item.Section)) return;
        if (item.LockedOn)
        {
            if (cold.IsPlayer) world.Post("This item is locked in place and cannot be removed. You can try to pick the lock, find a saw, or use brute strength.");
            return;
        }
        // The right click's crime and the steal roll (the owner watching unless down); caught, the item stays.
        if (!victim.IsPlayer && Crimes?.Pickpocket(world, i, t, item, Crime.CrimeSystem.TakeKind.RightClick) == true) return;
        bool worn = InventoryCommands.IsEquipmentSection(item.Section);
        var mark = item.Mark;
        int death = DeathUnits(from);
        if (Crime.CrimeSystem.IsLootTheft(cold, victim)) item.Mark = StolenMark.Of(victim, table.IdOf(t));
        var owner = from.Items.Contains(item) ? from : from.Backpack?.Contents ?? from;
        // Money taken by the player's own becomes cats, as when it is dropped into their window (PlayerSystem.Place, FUN_140714a20).
        if (item.ItemFunction == PlayerSystem.MoneyFunction && cold.Faction == world.Player.Faction && Data is not null)
        {
            int units = item.Quantity;   // money is never split (InventoryCommands.ClickCount)
            PlayerSystem.Cash(world, Data, item, units);
            if (units >= item.Quantity) owner.Remove(item); else item.Quantity -= units;
            item.Mark = mark;
            Butcher(world, i, t, death);
            Seen(world, i, t, owner.Items.Contains(item) ? 1 : 0);
            return;
        }
        int moved = InventoryCommands.MoveInto(owner, item, InventoryCommands.ClickCount(item, take.Count), cold.Inventory!, take.BagFirst, out bool wear, cold);
        if (owner.Items.Contains(item)) item.Mark = mark;   // what stays keeps its mark
        if (moved == 0)
        {
            if (cold.IsPlayer) world.Post($"{cold.Name} has no room for {item.Name}.");
            return;
        }
        Butcher(world, i, t, death);
        if (worn) Assembly?.RefreshEquipment(victim);
        if (wear) Assembly?.RefreshEquipment(cold);
        Seen(world, i, t, owner.Items.Contains(item) ? 1 : 0);
    }

    /// <summary>Seen taking from one open window: one message, as a take-all (engine choice).</summary>
    void Seen(World world, int looter, int body, int left)
    {
        var table = world.Characters;
        bool first = seenLoot != world.Player.OpenLoot;
        if (Looted(world, table, looter, body, 1, left, crime: false))
        {
            if (first && table.Cold(looter)!.IsPlayer) world.Post($"{table.Cold(looter)!.Name} was seen looting {table.Cold(body)!.Name}");
            seenLoot = world.Player.OpenLoot;
        }
    }

    /// <summary>The open loot's looter and body while it may stay open, for the held item (<see cref="PlayerState.Held"/>); null when none is open.</summary>
    internal (CharacterId Looter, CharacterId Body)? OpenLoot(World world) =>
        OpenLootPair(world) is var (i, t) ? (world.Characters.IdOf(i), world.Characters.IdOf(t)) : null;

    /// <summary>
    /// A held item that came out of the open loot's body went to someone else (placed in a window or given; its stolen mark is the held item's, <see cref="PlayerState.Held"/>): a death item
    /// butchers a live body (<paramref name="death"/>: it was one), and the looting may be seen (<see cref="Looted"/>), as a right click's take.
    /// </summary>
    internal void TookHeld(World world, bool death)
    {
        if (OpenLootPair(world) is not var (i, t)) return;
        var victim = world.Characters.Cold(t)!;
        if (death && victim.Medical is { Dead: false } medical && CharacterFacts.Down(victim))
        {
            medical.Butcher();
            world.Post($"{victim.Name} has been butchered");
        }
        Seen(world, i, t, 0);
    }

    /// <summary>The units of <paramref name="inventory"/>'s death items (<see cref="ItemInstance.Death"/>).</summary>
    static int DeathUnits(Inventory inventory)
    {
        int n = 0;
        foreach (var item in inventory.All()) if (item.Death) n += item.Quantity;
        return n;
    }

    /// <summary>
    /// A live, down body (<see cref="CharacterFacts.Down"/>: also a carried awake one) that had <paramref name="before"/> units of death items and now has fewer is butchered: it dies, "{name} has been butchered" (<c>FUN_14075e7e0</c>, docs/game/animals.md
    /// "Death items"; posted when the looter or the body is the player's, engine choice).
    /// </summary>
    void Butcher(World world, int looter, int body, int before)
    {
        var table = world.Characters;
        var victim = table.Cold(body)!;
        if (victim.Medical is not { Dead: false } medical || !CharacterFacts.Down(victim) || DeathUnits(victim.Inventory!) >= before) return;
        medical.Butcher();
        if (table.Cold(looter)!.IsPlayer || victim.IsPlayer) world.Post($"{victim.Name} has been butchered");
    }

    /// <summary>The open loot whose looting was already seen (see <see cref="TakeLoot"/>).</summary>
    CharacterId seenLoot = CharacterId.None;

    /// <summary>A right click on one of the looter's items: units into the open loot (<see cref="InventoryCommands.MoveInto"/>); a worn bag with contents stays.</summary>
    void PutLoot(World world, PutLootItem put)
    {
        if (OpenLootPair(world) is not var (i, t) || put.Character != world.Player.LootOpener) return;
        var table = world.Characters;
        var looter = table.Cold(i)!;
        var body = table.Cold(t)!;
        var item = looter.Inventory!.All().ElementAtOrDefault(put.Item.Index);
        if (item is null || item.Contents is not null && item.Section == "backpack_attach") return;
        bool worn = InventoryCommands.IsEquipmentSection(item.Section);
        var token = put.Item;
        if (item.Record != token.Record || item.Section != token.Section || item.X != token.X || item.Y != token.Y || item.Quantity != token.Quantity || item.LockedOn ||
            !InventoryCommands.IsMovableSection(item.Section)) return;
        var owner = looter.Inventory.Items.Contains(item) ? looter.Inventory : looter.Inventory.Backpack?.Contents;
        if (owner is null || InventoryCommands.MoveInto(owner, item, InventoryCommands.ClickCount(item, put.Count), body.Inventory!, put.BagFirst, out bool wear, body) == 0) return;
        if (worn) Assembly?.RefreshEquipment(looter);
        if (wear) Assembly?.RefreshEquipment(body);
    }

    /// <summary>
    /// "Take all": every item of <paramref name="from"/>'s main inventory and of its worn backpack, in order, goes into <paramref name="into"/> where it fits (a stack joins a stack,
    /// else the first free cell of the main grid, then of the backpack: <see cref="Inventory.Add"/>); what does not fit stays. Worn clothing, armour, weapons and the backpack itself stay
    /// (engine choice: the fighter's gear is fixed at spawn). Returns the items moved and the ones left. With <paramref name="stolenFrom"/> at 0 or above (a theft) what moves is marked
    /// stolen from that faction; what stays keeps its mark.
    /// </summary>
    /// <summary>The money items of a loot's grid and bag into the player's purse (a take-all by the player's own); the number of stacks taken.</summary>
    int CashMoney(World world, Inventory from)
    {
        if (Data is null) return 0;
        int n = 0;
        foreach (var holder in new[] { from, from.Backpack?.Contents })
        {
            if (holder is null) continue;
            foreach (var item in holder.Items.Where(x => x.ItemFunction == PlayerSystem.MoneyFunction && (holder != from || x.Section == "main")).ToList())
            {
                PlayerSystem.Cash(world, Data, item, item.Quantity);
                holder.Remove(item);
                n++;
            }
        }
        return n;
    }

    public static (int Taken, int Left) TakeAll(Inventory into, Inventory from, StolenMark? stolen = null)
    {
        var items = new List<ItemInstance>();
        foreach (var item in from.Items)
        {
            if (item.Section == "main") items.Add(item);
            else if (item.Contents is { } pack) items.AddRange(pack.Items);
        }
        int taken = 0, left = 0;
        foreach (var item in items)
        {
            var mark = item.Mark;
            bool death = item.Death;
            if (stolen is { } s) item.Mark = s;
            item.Death = false;
            if (into.Add(item))
            {
                from.Remove(item);
                taken++;
            }
            else
            {
                item.Mark = mark;
                item.Death = death;
                left++;
            }
        }
        return (taken, left);
    }

    /// <summary>
    /// Whether a member of the victim's faction sees the looter: awake, not carried, and the looter within its sight (<see cref="SightMap.Sees"/>, outdoor range
    /// <see cref="CarrySettings.Sight"/> x its race's <c>vision range mult</c>). The original's witness rules are richer (docs/game/carry-loot.md "Witnesses"); this is the
    /// part the engine has.
    /// </summary>
    bool Witnessed(World world, CharacterTable table, int looter, int victim)
    {
        var next = table.Next;
        int faction = table.Cold(victim)!.Faction;
        var at = next[looter].Position;
        for (int w = 0; w < table.HighWater; w++)
        {
            if (w == looter || w == victim || !next[w].Alive) continue;
            if (table.Cold(w) is not { Medical.Incapacitated: false } c || c.Faction != faction || !c.CarriedBy.IsNone) continue;
            if (world.Sight.Sees(w, looter, next[w].Position, at, AggroSystem.RangeOf(c, Settings.Sight)) > 0) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ rescue

    /// <summary>
    /// Every <see cref="CarrySettings.AutoEveryTicks"/> ticks (serial, slot order): NPCs with a rescue job (a loaded list), idle, wandering or following (or on a list job below it), able to carry
    /// and with no fight in their squad, pick up the nearest knocked-out member of their faction within their sight (<see cref="FindRescue"/>); then NPC squads whose leader is knocked out
    /// send their nearest free member to pick it up. A player character's rescue job is the decision step's (<see cref="RegisterSources"/>).
    /// </summary>
    void Rescue(World world, CharacterTable table)
    {
        var next = table.Next;
        var taken = Lifting(table);
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!next[i].Alive || table.Cold(i) is not { IsPlayer: false, RescueJob: true } cold) continue;
            // Free, or on a list job below the rescue job (which ranks above it).
            int rescue = cold.Jobs.FindIndex(j => j.Type == Meitou.Data.Gameplay.Ai.TaskType.FIND_AND_RESCUE_IF_THERES_BEDS);
            if (!Free(world, i) && !(Production.PermaJobs.ListJob(cold, (CharacterTask)next[i].Task) is { Implicit: false } job && job.Job > rescue && cold is { Fleeing: false }
                && CanCarry(world, cold, TaskType.FIND_AND_RESCUE_IF_THERES_BEDS))) continue;
            if (!MayRescue(world, i, cold)) continue;
            RescueSomeone(world, i, taken, TaskSource.Job, rescue);
        }
        if (Settings.RescueNpcLeaders) RescueLeaders(world, table, taken);
    }

    /// <summary>
    /// The rescue (148) and foraging (238) jobs as the decision step's contributor (step 5): a character not fleeing and able to carry, with no fight in its squad, whose finder finds a body
    /// (<see cref="FindRescue"/>, which also needs <see cref="MayRescue"/>; <see cref="FindForage"/>) lifts or loots it. It runs while its lift or loot does.
    /// </summary>
    public void RegisterSources(DecisionSystem decisions)
    {
        var jobs = new CarryJobs(this);
        decisions.RegisterJob(TaskType.FIND_AND_RESCUE_IF_THERES_BEDS, jobs);
        decisions.RegisterJob(TaskType.LOOT_ANIMALS_JOB, jobs);
        decisions.Register(new EngagedDefence(this));
    }

    sealed class CarryJobs(CarrySystem owner) : IJobSource
    {
        (long Tick, int Slot, int K, int Body) found = (-1, -1, -1, -1);

        public DecisionCheck CanRun(World world, int slot, int k)
        {
            var table = world.Characters;
            var cold = table.Cold(slot)!;
            var type = cold.Jobs[k].Type;
            var task = type == TaskType.LOOT_ANIMALS_JOB ? CharacterTask.Loot : CharacterTask.PickUp;
            if (cold.Running is { Source: TaskSource.Job } run && run.Job == k && run.Task == task && run.Runs((CharacterTask)table.Next[slot].Task)) return DecisionCheck.Runs;
            if (cold.Fleeing || !CanCarry(world, cold, type)) return DecisionCheck.No("cannot carry");
            int body;
            if (type == TaskType.LOOT_ANIMALS_JOB) body = cold.Inventory is null ? -1 : owner.FindForage(world, slot, Looting(table));
            else body = owner.MayRescue(world, slot, cold) ? owner.FindRescue(world, slot, Lifting(table)) : -1;
            if (body < 0) return DecisionCheck.No("nobody found");
            found = (world.Tick, slot, k, body);
            return DecisionCheck.Run;
        }

        public void Start(World world, int slot, int k)
        {
            if (found.Tick != world.Tick || found.Slot != slot || found.K != k) return;
            var table = world.Characters;
            var cold = table.Cold(slot)!;
            var type = cold.Jobs[k].Type;
            bool forage = type == TaskType.LOOT_ANIMALS_JOB;
            if (CarrySystem.Start(world, slot, forage ? CharacterTask.Loot : CharacterTask.PickUp, type, TaskSource.Job, table.IdOf(found.Body), CharacterTask.Idle, k) && forage) cold.Foraging = true;
        }
    }

    /// <summary>The bodies being picked up now (by slot).</summary>
    static HashSet<int> Lifting(CharacterTable table)
    {
        var taken = new HashSet<int>();
        for (int i = 0; i < table.HighWater; i++)
            if (table.Next[i].Alive && table.Next[i].Task == (byte)CharacterTask.PickUp && table.TryResolveNext(table.Cold(i)!.CarryTarget, out int busy)) taken.Add(busy);
        return taken;
    }

    /// <summary>
    /// The desire of 148 Find and rescue if there's beds (<c>FUN_1405994c0</c>, <b>Verified</b>): 0 with the Hold order (the raw flag) or while carrying, else the bed search's factor (a
    /// bed must be found for the rescuer: <see cref="Locks.FixtureRegistry.FindBed"/>, which keeps the bed it lies in). Also gates the one-shot Rescue order (engine reading).
    /// </summary>
    bool MayRescue(World world, int slot, CharacterCold cold) => Rescuing(cold) && world.Fixtures.FindBed(world, slot, Relations) is not null;

    /// <summary>The bed-free part of <see cref="MayRescue"/>: not on Hold and not carrying.</summary>
    static bool Rescuing(CharacterCold cold) => (cold.Orders & CharacterOrders.Hold) == 0 && cold.Carrying.IsNone;

    /// <summary>
    /// One look for a body (an NPC's rescue job, and the Rescue button without Shift once): the character picks up the body <see cref="FindRescue"/> finds; true when it started.
    /// </summary>
    bool RescueSomeone(World world, int i, HashSet<int> taken, TaskSource source, int job = -1)
    {
        var table = world.Characters;
        int best = FindRescue(world, i, taken);
        if (best < 0 || !Start(world, i, CharacterTask.PickUp, TaskType.FIND_AND_RESCUE_IF_THERES_BEDS, source, table.IdOf(best),
            TaskStart.Standing(table.Cold(i)!, (CharacterTask)table.Next[i].Task), job)) return false;
        taken.Add(best);
        return true;
    }

    /// <summary>
    /// The rescue finder (a pure read): with no fight in its squad, the nearest knocked-out member of the character's faction (<see cref="Rescues"/>) within its sight that nobody else lifts
    /// (<paramref name="taken"/>), not an animal, not in a bed or cage; -1 for none.
    /// </summary>
    int FindRescue(World world, int i, HashSet<int> taken)
    {
        var table = world.Characters;
        var next = table.Next;
        var cold = table.Cold(i)!;
        if (cold.SquadId >= 0 && world.Squads.Find(cold.SquadId) is { } squad && CharacterFacts.SquadFighting(table, squad)) return -1;
        float range = AggroSystem.RangeOf(cold, Settings.Sight), best2 = range * range;
        int best = -1;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (s == i || !next[s].Alive || taken.Contains(s)) continue;
            if (table.Cold(s) is not { Medical: { Unconscious: true, Dead: false } } other || other.InSomething != Locks.HeldState.None
                || other.Fighter is { IsAnimal: true } || other.Appearance?.Animal is not null   // never an animal (the finder's slot 0x248 test)
                || !Rescues(world, cold, other) || !CanBeLifted(other)) continue;
            float d2 = Vector2.DistanceSquared(new Vector2(next[i].Position.X, next[i].Position.Z), new Vector2(next[s].Position.X, next[s].Position.Z));
            if (d2 <= best2 && (best < 0 || d2 < best2)) { best = s; best2 = d2; }
        }
        return best;
    }

    /// <summary>NPC squads whose leader is knocked out send their nearest free member to pick it up.</summary>
    void RescueLeaders(World world, CharacterTable table, HashSet<int> taken)
    {
        var next = table.Next;
        foreach (var squad in world.Squads.All)
        {
            if (!table.TryResolveNext(squad.Leader, out int l) || taken.Contains(l)) continue;
            if (table.Cold(l) is not { IsPlayer: false, Medical: { Unconscious: true, Dead: false } } leader || leader.InSomething != Locks.HeldState.None || !CanBeLifted(leader) || CharacterFacts.SquadFighting(table, squad)) continue;
            int best = -1;
            float best2 = float.MaxValue;
            foreach (var m in squad.Members)
            {
                if (!table.TryResolveNext(m, out int s) || s == l || !Free(world, s) || table.Cold(s)!.IsPlayer) continue;
                float range = AggroSystem.RangeOf(table.Cold(s)!, Settings.Sight);
                float d2 = Vector2.DistanceSquared(new Vector2(next[s].Position.X, next[s].Position.Z), new Vector2(next[l].Position.X, next[l].Position.Z));
                if (d2 <= range * range && d2 < best2) { best = s; best2 = d2; }
            }
            if (best < 0 || !Start(world, best, CharacterTask.PickUp, TaskType.FIND_AND_RESCUE_IF_THERES_BEDS, TaskSource.Npc, squad.Leader, (CharacterTask)next[best].Task)) continue;
            taken.Add(l);
        }
    }

    /// <summary>A character free to start a rescue: alive, idle, wandering or following, not fleeing, able to carry.</summary>
    static bool Free(World world, int slot) =>
        world.Characters.Next[slot].Alive && world.Characters.Next[slot].Task is (byte)CharacterTask.Idle or (byte)CharacterTask.Wander or (byte)CharacterTask.Follow
        && world.Characters.Cold(slot) is { Fleeing: false } cold && CanCarry(world, cold, TaskType.FIND_AND_RESCUE_IF_THERES_BEDS);

}
