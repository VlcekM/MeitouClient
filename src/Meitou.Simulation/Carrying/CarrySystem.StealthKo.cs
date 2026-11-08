using System.Numerics;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Weather;

namespace Meitou.Simulation.Carrying;

/// <summary>
/// The stealth knock-out (<see cref="CharacterTask.StealthKo"/>, the original's <c>Task_StealthKO</c>; docs/game/combat.md "Stealth knock-out"): a sneaking character creeps to a point
/// behind its target, swings the <c>knockout</c> clip at x1.2 and at half of it rolls 0-100 against <see cref="StealthKoChance"/>. A hit knocks the target out for
/// <see cref="MedicalState.PutDownKoSeconds"/> with the attacker's assassination / 100; a miss turns the target on the attacker. Either trains assassination (a miss three times as much).
/// The walk breaks off when the target has spotted the attacker (its <see cref="AggroSystem"/> spot meter is full). A miss also has the target say event 56, alarm its hearing and go for the attacker.
/// </summary>
public sealed partial class CarrySystem
{
    /// <summary>The attacker's clip and the rate it plays at (<c>Task_StealthKO</c>: <c>knockout</c> at 1.2, <b>Verified</b> literals).</summary>
    public const string KnockoutClip = "knockout";
    public const float KnockoutPlaySpeed = 1.2f;
    /// <summary>The fractions of the clip at which the roll is made and the task ends (0.5 and 0.85, <b>Verified</b> literals).</summary>
    public const float KnockoutStrikeAt = 0.5f, KnockoutDoneAt = 0.85f;
    /// <summary>The swing starts within this distance of the point behind the target (14, <b>Verified</b> literal).</summary>
    public const float KnockoutStartRange = 14;
    /// <summary>How far behind the target that point is: the original subtracts the target's heading from its position, a unit vector (+0xd0 of its movement object, written normalised by <c>FUN_140665250</c>, <b>Verified</b>).</summary>
    public const float KnockoutBehind = 1;
    /// <summary>The dialogue event the victim of a missed stealth knock-out says at the attacker (EV_ASSASSINATION_FAILED, 56 = the 0x38 <c>FUN_140685660</c> is called with, <b>Verified</b>).</summary>
    const int AssassinationFailed = 56;

    /// <summary>The length of the <c>knockout</c> clip in seconds (1 when the clip lengths are not known); the swing lasts this / <see cref="KnockoutPlaySpeed"/>.</summary>
    public float KnockoutSeconds { get; set; } = 1;
    /// <summary>The senses the walk checks (a target that spotted the attacker turns round); null: never spotted.</summary>
    public AggroSystem? Aggro { get; set; }

    readonly List<StealthKoEntry> _knockouts = [];
    XpService? _xp;

    /// <summary>The swings rolled since the last <see cref="TakeKnockouts"/> (the host shows them as "*Thunk*" or "Failed!").</summary>
    public IReadOnlyList<StealthKoEntry> Knockouts => _knockouts;

    public StealthKoEntry[] TakeKnockouts()
    {
        StealthKoEntry[] all = [.. _knockouts];
        _knockouts.Clear();
        return all;
    }

    /// <summary>A character that may be sent on a stealth knock-out: sneaking, awake, not carrying or carried (a fight it is in ends with the order's new task).</summary>
    public static bool CanKnockOut(CharacterCold cold) => (cold.Orders & CharacterOrders.Sneak) != 0 && CanRunKnockOut(cold);

    /// <summary>
    /// A character the stealth knock-out order may start on: the sneaking is not asked, the task switches it on when it starts (<c>FUN_140345eb0</c> calls <c>FUN_1405ca9a0(attacker, 1)</c> unless the
    /// attacker is in stealth mode, <b>Verified</b>), so a queued order whose character stopped sneaking meanwhile still runs.
    /// </summary>
    static bool CanRunKnockOut(CharacterCold cold) =>
        cold is { Medical.Incapacitated: false, Race: not null, Stats: not null } && cold.Carrying.IsNone && cold.CarriedBy.IsNone;

    /// <summary>A character that may be knocked out from behind: alive (a downed one too, the verb list offers 228 on it), with a body, not carried, not an animal (the verb list's <c>isAnimal</c> gate, so no cleansers).</summary>
    public static bool CanBeKnockedOut(CharacterCold cold) =>
        cold is { Medical.Dead: false, Race: not null } && cold.CarriedBy.IsNone && !CharacterFacts.Animal(cold);

    /// <summary>
    /// The chance in per cent that a stealth knock-out lands (<c>FUN_1408852f0</c>, <b>Verified</b> literals; docs/game/combat.md "Stealth knock-out"). X = the attacker's assassination minus
    /// the target's best of: assassination, half its thievery, half its stealth term x 100, half its melee defence, its toughness (each skill times its injury multiplier); then
    /// 100 x (X + K) / 2K with K = 30 for X >= 0 and 15 below, unclamped (50 at X = 0, 100 at +30, 0 at -15). 0 when the target has spotted the attacker, or is fighting, aware of
    /// it and the attacker is not unseen (<paramref name="noticedInFight"/>); halved when it only heard the attacker.
    /// </summary>
    public static float StealthKoChance(CharacterCold attacker, CharacterCold target, bool spotted = false, bool heard = false, bool noticedInFight = false)
    {
        if (spotted || noticedInFight || attacker.Stats is not { } a || target.Stats is not { } t) return 0;
        static float Skill(CharacterCold c, CharacterStats s, StatsEnumerated stat) => s[stat] * (c.Medical?.StatMultiplier(stat) ?? 1) * CombatSystem.GearFactor(c, stat);
        float defence = Skill(target, t, StatsEnumerated.Assassination);
        defence = MathF.Max(defence, Skill(target, t, StatsEnumerated.Thieving) * 0.5f);
        defence = MathF.Max(defence, Orders.Stealth(target) * 100 * 0.5f);
        defence = MathF.Max(defence, Skill(target, t, StatsEnumerated.MeleeDefence) * 0.5f);
        defence = MathF.Max(defence, Skill(target, t, StatsEnumerated.Toughness));
        float x = Skill(attacker, a, StatsEnumerated.Assassination) - defence;
        float k = x < 0 ? 15 : 30;
        return (x + k) / (2 * k) * 100 * (heard ? 0.5f : 1);
    }

    void BeginKnockOut(World world, StealthKoOrder order)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(order.Target, out int target) || !CanBeKnockedOut(table.Cold(target)!)) return;
        // The first that can go, in the order given (the selection's order), so the chance the interface showed is the one rolled.
        var entry = new OrderEntry(Meitou.Data.Gameplay.Ai.TaskType.StealthKnockout, this) { Target = order.Target };
        foreach (var id in Who(world, order.Attackers))
            if (table.TryResolveNext(id, out int attacker) && PlayerOrders.CanRun(world, attacker, entry).CanRun)
            {
                PlayerOrders.Give(world, attacker, entry, order.Queued);
                return;
            }
    }

    /// <summary>The point behind the target the attacker creeps to.</summary>
    static Vector2 Behind(in CharacterHot target) =>
        new(target.Position.X - MathF.Sin(target.Yaw) * KnockoutBehind, target.Position.Z - MathF.Cos(target.Yaw) * KnockoutBehind);

    /// <summary>
    /// Where the attacker walks (<c>Task_StealthKO</c> <c>FUN_14034cd80</c>, <b>Verified</b>): the point behind the target, or, for a target in a bed or imprisoned, that building's position marker
    /// nearest the attacker (<see cref="Locks.FixtureRegistry.PositionMarker"/>: the nearest use node, else its marker).
    /// </summary>
    static Vector2 KnockoutGoal(World world, CharacterCold target, in CharacterHot targetHot, in CharacterHot attacker)
    {
        if (target.InSomething != Locks.HeldState.None && target.InWhat is { } id
            && world.Fixtures.PositionMarker(id, attacker.Position, world.GameHours) is { } marker) return new Vector2(marker.X, marker.Z);
        return Behind(targetHot);
    }

    /// <summary>
    /// The walk of a stealth knock-out (Schedule, serial): to the goal behind the target, re-aimed as it moves, through the swing too (the goal is set every update before the state logic, and
    /// the knockout clip has no <c>disables movement</c>, so only reaching the point stops the attacker); within reach the swing starts.
    /// </summary>
    void ScheduleKnockOuts(World world)
    {
        var table = world.Characters;
        var next = table.Next;
        float dt = world.TickSeconds;
        for (int i = 0; i < table.HighWater; i++)
        {
            ref var n = ref next[i];
            if (!n.Alive || n.Task != (byte)CharacterTask.StealthKo) continue;
            var cold = table.Cold(i)!;
            // Only the target's sensing the attacker cancels the walk (FUN_14034cd80, no combat-state test of either side: orders beat goals); the roll's fight zero is in Strike.
            if (cold.Medical is not { Incapacitated: false } || !table.TryResolveNext(cold.CarryTarget, out int t) || table.Cold(t) is not { } target
                || !CanBeKnockedOut(target) || Senses(world, table, t, i))
            {
                End(world, i);
                continue;
            }
            var goal = KnockoutGoal(world, target, next[t], n);
            bool sameFloor = MathF.Abs(n.Position.Y - next[t].Position.Y) <= MovementSystem.FloorSlack;   // not swung at through a floor (#292)
            if (cold.LiftTime < 0 && sameFloor && Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), goal) < KnockoutStartRange)
            {
                // The target's sense entry: heard on this last step (the sneaking hearing roll at the step's speed; the original's pass cadence is not traced).
                float stealth = Orders.Stealth(cold), step = new Vector2(n.Velocity.X, n.Velocity.Z).Length();
                float hear = step > 0.01f ? Orders.SneakHearChance(Vector3.Distance(n.Position, next[t].Position), Orders.SneakNoise(stealth, WeatherSystem.NoiseFactor(cold)), stealth, !Asleep(table, t)) : 0;
                cold.KnockoutHeard = hear > 0 && Rng.Float(Rng.Hash(world.Seed, Rng.Key(table.IdOf(i)), RngPurpose.KnockoutHear, (ulong)world.Tick)) < hear;
                cold.LiftTime = 0;
                Face(ref n, next[t]);
            }
            n.TaskTime -= dt;
            bool asking = (n.Flags & (ushort)(MoveFlags.NeedPath | MoveFlags.Pending)) != 0;
            bool stale = (n.Flags & (ushort)MoveFlags.HasPath) == 0 || Vector2.DistanceSquared(goal, n.Goal) > PursuitSystem.RepathDistance * PursuitSystem.RepathDistance;
            if (asking || !stale || n.TaskTime > 0) continue;
            n.Goal = goal;
            (cold.GoalHeight, cold.GoalHeightAt) = (next[t].Position.Y, goal);   // the target's floor, not the attacker's (#292)
            n.Mode = SpeedMode.Free;
            n.Flags |= (ushort)MoveFlags.NeedPath;
            n.TaskTime = PursuitSystem.RepathSeconds;
        }
    }

    /// <summary>
    /// <c>FUN_140855830</c>: the target's sense entry for the attacker has its seen flag. Stand-in: its spot meter on the attacker is full (<see cref="AggroSystem.Spotted"/>), or it fights the
    /// attacker (a fighter has the enemy in sight). Checked every tick, in every state, by the original's update; a hit cancels the task silently.
    /// </summary>
    bool Senses(World world, CharacterTable table, int target, int attacker)
    {
        var attackerId = table.IdOf(attacker);
        return Aggro?.Spotted(target, attackerId) == true
            || world.SystemOf<CombatSystem>()?.StateOf(table.IdOf(target)) is { } fight && fight.TargetSlot == attacker && fight.TargetGeneration == attackerId.Generation;
    }

    static void Face(ref CharacterHot n, in CharacterHot target)
    {
        float dx = target.Position.X - n.Position.X, dz = target.Position.Z - n.Position.Z;
        if (dx * dx + dz * dz > 1e-4f) n.Yaw = MathF.Atan2(dx, dz);
    }

    /// <summary>The swing (SlowWorld, serial): the roll at <see cref="KnockoutStrikeAt"/> of the clip, the end at <see cref="KnockoutDoneAt"/>.</summary>
    void Swing(World world, CharacterTable table, int i, float dt)
    {
        var cold = table.Cold(i)!;
        if (cold.LiftTime < 0) return;   // still creeping
        float length = KnockoutSeconds / KnockoutPlaySpeed;
        float before = cold.LiftTime;
        cold.LiftTime += dt;
        if (cold.Medical is not { Incapacitated: false } || !table.TryResolveNext(cold.CarryTarget, out int t) || table.Cold(t) is not { } target)
        {
            End(world, i);
            return;
        }
        // A target that senses the attacker cancels the swing too, every tick before the roll: silently, no "Failed!", event or XP (FUN_14034cd80).
        if (Senses(world, table, t, i))
        {
            End(world, i);
            return;
        }
        Face(ref table.Next[i], table.Next[t]);
        if (before < KnockoutStrikeAt * length && cold.LiftTime >= KnockoutStrikeAt * length)
        {
            // The swing has no distance end: a target that walked off mid-swing is still rolled (FUN_14034cd80); one that went down is not.
            if (!CanBeKnockedOut(target))
            {
                End(world, i);
                return;
            }
            Strike(world, table, i, t);
        }
        if (cold.LiftTime >= KnockoutDoneAt * length) End(world, i);
    }

    void Strike(World world, CharacterTable table, int i, int t)
    {
        var cold = table.Cold(i)!;
        var target = table.Cold(t)!;
        var attackerId = table.IdOf(i);
        var targetId = table.IdOf(t);
        bool spotted = Aggro?.Spotted(t, attackerId) == true;
        // Fighting and aware of the attacker (having an entry for it: it saw or heard it, or is fighting it) while the attacker is not unseen: no chance (FUN_1408852f0, FUN_1408593d0 makes an entry on a sight or a hearing).
        bool aware = spotted || cold.KnockoutHeard || world.SystemOf<CombatSystem>()?.StateOf(targetId) is { } fight && fight.TargetSlot == i && fight.TargetGeneration == attackerId.Generation;
        bool noticed = target.InCombat && aware && Aggro?.Unseen(table, attackerId) == false;
        float chance = StealthKoChance(cold, target, spotted, cold.KnockoutHeard, noticed);
        float roll = Rng.Float(Rng.Hash(world.Seed, Rng.Key(attackerId), RngPurpose.StealthKo, (ulong)world.Tick)) * 100;
        bool success = chance > roll;
        // XP: assassination through the generic entry with dt 1, 100 on a hit and 300 on a miss (Verified literals); x0.07 on a target in prison, x0.5 on a sleeping one
        // (Observed, +0x2f8). A miss on a target that is fighting and aware of the attacker gives none: "Failed! (No XP)" (Observed).
        bool noXp = !success && target.InCombat && aware;
        if (!noXp && cold.Stats is { } stats && cold.Race is { } race)
        {
            float f = target.IsImprisoned ? 0.07f : Asleep(table, t) ? 0.5f : 1;
            (_xp ??= new XpService(Constants, _bodyOptions)).Continuous(stats, race, StatsEnumerated.Assassination, 1, f * (success ? 100 : 300));
        }
        if (success)
        {
            if (target.Medical is { } medical && target.Race is { } targetRace)
            {
                var ctx = new MedicalContext(Constants, _bodyOptions, targetRace) { Toughness = target.Stats?.ScaledToughness ?? 0, Strength = target.Stats?.Strength ?? 50, HasStats = target.Stats is not null };
                float assassination = (cold.Stats?[StatsEnumerated.Assassination] ?? 0) * (cold.Medical?.StatMultiplier(StatsEnumerated.Assassination) ?? 1);
                medical.KnockOut(medical.PutDownKoSeconds(ctx, assassination * 0.01f));
                TaskStart.Stand(table, t);
                table.Next[t].Velocity = Vector3.Zero;
            }
        }
        else if (!target.Medical!.Incapacitated)
        {
            // A miss (Task_StealthKO, FUN_14034cd80, Verified): the victim says event 56 ASSASSINATION_FAILED at the attacker, notices it (its hearing of it is alarmed for 5 s), each
            // side's squad tags the other TEMP_ENEMY, and the victim goes for the attacker (Character::attackTarget; no assault is booked for that, so the order is a defensive one).
            world.Defer(new Dialogue.DialogueEvent(targetId, attackerId, AssassinationFailed) { Tick = world.Tick + 1 });
            Aggro?.Alarm(world, targetId, attackerId);
            if (world.SystemOf<Dialogue.DialogueSystem>() is { } talk)
            {
                talk.TagEnemy(world, t, i);
                talk.TagEnemy(world, i, t);
            }
            if (DecisionSystem.Decides(target)) Engage(world, t, i);   // a player character: attackTarget's replan, the step decides (CarrySystem.Engage)
            else world.Defer(new AttackOrder([targetId], attackerId) { Tick = world.Tick + 1, Defensive = true });
        }
        // The swing books an Assault on the target's faction at the roll, hit or miss (the pending-crime setter FUN_140852c80 with code 5, called unconditionally, Verified).
        Crimes?.SetPending(world, i, Crime.CrimeCode.Assault, target.Faction, targetId, Crime.CrimeSystem.MinTimer);
        // The floater over the attacker, shown to anyone (Verified): "*Thunk*" or "Failed!" + " (No XP)".
        world.Floaters?.Enqueue(Floater.Strike(attackerId, table.Next[i].Position, success ? Loc.Tr("*Thunk*") : Loc.Tr("Failed!") + (noXp ? Loc.Tr(" (No XP)") : ""), success));
        _knockouts.Add(new StealthKoEntry(world.Tick, attackerId, targetId, chance, success, noXp));
    }
}