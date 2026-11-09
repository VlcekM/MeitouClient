using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Crime;

namespace Meitou.Simulation.Locks;

/// <summary>
/// What one <see cref="Lockpicking.Step"/> did.
/// </summary>
public enum PickOutcome : byte
{
    /// <summary>
    /// The lock is not effectively locked: nothing to pick.
    /// </summary>
    Open,
    /// <summary>
    /// The picker may not pick it (skill too low, down, carried, on a pole...).
    /// </summary>
    NotAllowed,
    /// <summary>
    /// Time added, no roll yet.
    /// </summary>
    Picking,
    /// <summary>
    /// A roll failed; the next comes after a new 5-20 s wait.
    /// </summary>
    Failed,
    /// <summary>
    /// A roll succeeded: the lock is unlocked.
    /// </summary>
    Success,
}

/// <summary>
/// Lockpicking, breaking (docs/game/locks.md "Lockpicking", "Cutting and breaking"): the service the pick-lock order and the AI's tasks call each
/// update of the task, in serial phases only (it changes the lock, the picker's stats and its pending crime). The roll comes from the world seed, the
/// fixture's id and the lock's roll counter. Shackles (an item's lock) use the same rules: <see cref="StepShackles"/>, <see cref="CutShackles"/>,
/// <see cref="BreakShackles"/>.
/// </summary>
public sealed class Lockpicking(GameConstants constants, BodyOptions options)
{
    /// <summary>
    /// The chance at or above the lock's level, and the skill a caged picker loses for the roll (<b>Verified</b>).
    /// </summary>
    public const float MaxChance = 0.9f, CagedPenalty = 10;

    /// <summary>
    /// The XP the picking trains.
    /// </summary>
    readonly XpService _xp = new(constants, options);

    /// <summary>
    /// <c>p = 0.9</c> when <paramref name="skill"/> &gt;= <paramref name="level"/>, else <c>0.9 / 2^((level - skill) / 10)</c> (<c>FUN_140297dc0</c>,
    /// <b>Verified</b>).
    /// </summary>
    public static float Chance(int level, float skill) => skill >= level ? MaxChance : MaxChance / MathF.Pow(2, (level - skill) / 10f);

    /// <summary>
    /// The effective lockpicking skill: stat 37 times its body multiplier.
    /// </summary>
    public static float Skill(CharacterCold c) =>
        (c.Stats?[StatsEnumerated.Lockpicking] ?? 0) * (c.Medical?.StatMultiplier(StatsEnumerated.Lockpicking) ?? 1);

    /// <summary>
    /// The chance of the roll and the bar: a picker imprisoned in a cage uses skill - 10.
    /// </summary>
    public static float RollChance(CharacterCold picker, int level) => Chance(level, Skill(picker) - (picker.IsImprisoned ? CagedPenalty : 0));

    /// <summary>
    /// The skill condition (166/167): the chance without the cage penalty is above <c>minimum lockpick chance</c> (0.05, strict); and the engine's
    /// conditions for the Unknown cases of locks.md: conscious, not carried or carrying, one arm working, not tied to a pole.
    /// </summary>
    public bool Allowed(World world, CharacterCold picker, int level)
    {
        if (picker.Medical is { Incapacitated: true } || !picker.CarriedBy.IsNone || !picker.Carrying.IsNone)
            return false;

        if (picker.Medical?.Aggregates().BothArmsDisabled == true) return false;
        if (picker.IsImprisoned && picker.InWhat is { } id && world.Fixtures.Find(id) is { Restrains: true })
            return false;

        return Chance(level, Skill(picker)) > constants.MinimumLockpickChance;
    }

    /// <summary>
    /// One update of picking <paramref name="target"/> by the character in <paramref name="picker"/> for <paramref name="dt"/> game seconds (the
    /// task's update time): sets the pending crime, adds the time, and once it passes the lock's next-roll time rolls <c>rand() &lt; p</c>: success
    /// unlocks; failure waits uniform 5-20 s. Every roll trains lockpicking (1.2 x race[37] on success, 0.24 x on failure).
    /// </summary>
    public PickOutcome Step(World world, int picker, Fixture target, float dt, CrimeSystem? crimes = null)
    {
        var c = world.Characters.Cold(picker);
        if (target.Lock is not { EffectivelyLocked: true } l) return PickOutcome.Open;
        if (c is null || !Allowed(world, c, l.Level)) return PickOutcome.NotAllowed;
        Crime(world, picker, c, target, crimes);
        var outcome = Roll(world, c, l, Rng.StableHash(target.Id), dt);
        if (outcome == PickOutcome.Success) world.Fixtures.Touch(target);
        return outcome;
    }

    /// <summary>
    /// Adds <paramref name="dt"/> to the lock's pick time and, once it passes the next-roll time, rolls (success unlocks; failure waits uniform 5-20
    /// s) and trains lockpicking.
    /// </summary>
    PickOutcome Roll(World world, CharacterCold c, LockState l, ulong key, float dt)
    {
        l.PickTime += dt;
        if (l.PickTime < l.NextRoll) return PickOutcome.Picking;
        l.PickTime = 0;
        ulong counter = (ulong)++l.Rolls * 2;
        bool success = Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Lock, counter)) < RollChance(c, l.Level);
        if (success)
        {
            l.Locked = false;
            l.NextRoll = LockState.FirstRoll;
        }
        else
        {
            l.NextRoll = LockState.RetryMin
                    + (LockState.RetryMax - LockState.RetryMin) * Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Lock, counter + 1));
        }

        if (c.Stats is { } stats && c.Race is { } race) _xp.Lockpicking(stats, race, success);
        return success ? PickOutcome.Success : PickOutcome.Failed;
    }

    /// <summary>
    /// The rolls of <paramref name="wearer"/>'s shackles <paramref name="item"/>: keyed by the wearer and the item's record.
    /// </summary>
    static ulong ShackleKey(CharacterId wearer, Items.ItemInstance item) => Rng.Key(wearer) ^ Rng.StableHash(item.Record);

    /// <summary>
    /// One update of picking shackles (Task_PickLock_Shackles 201, the same rules as a cage's lock): the picker's own (a caged picker takes the -10)
    /// or another character's, which books code 7 against the wearer's faction.
    /// </summary>
    public PickOutcome StepShackles(World world, int picker, CharacterId wearer, Items.ItemInstance item, float dt, CrimeSystem? crimes = null)
    {
        var c = world.Characters.Cold(picker);
        if (item.Lock is not { EffectivelyLocked: true } l) return PickOutcome.Open;
        if (c is null || !Allowed(world, c, l.Level)) return PickOutcome.NotAllowed;
        ShackleCrime(world, picker, wearer, crimes);
        return Roll(world, c, l, ShackleKey(wearer, item), dt);
    }

    /// <summary>
    /// The effective strength the cut and break tests use (stat x the CharStats scale +0x34, FUN_140641480: 1 for people, the age for animals).
    /// </summary>
    static float Strength(CharacterCold c)
            => (c.Stats?[StatsEnumerated.Strength] ?? 0) * (c.Stats?.Scale ?? 1) * (c.Medical?.StatMultiplier(StatsEnumerated.Strength) ?? 1);

    /// <summary>
    /// The strength the cut tasks use (<c>FUN_140359f80</c>): the stat times the multiplier asked with the hunger term only (<c>FUN_1406457e0(stats,
    /// 1, 1)</c>), no limb, light or damage terms.
    /// </summary>
    static float CutStrength(CharacterCold c)
            => (c.Stats?[StatsEnumerated.Strength] ?? 0) * (c.Stats?.Scale
                    ?? 1) * (c.Medical?.StatMultiplier(StatsEnumerated.Strength, StatUse.Hunger) ?? 1);

    /// <summary>
    /// The best tool the character carries (ITEM <c>item function</c> 9, the condition <c>hasTools</c>: Hacksaw, Tools), by quality; null: "I don't
    /// have any tools".
    /// </summary>
    public static Items.ItemInstance? BestTool(CharacterCold c) =>
        c.Inventory?.All().Where(i => i.ItemFunction == ToolFunction).OrderByDescending(i => i.Quality).FirstOrDefault();

    /// <summary>
    /// ITEM <c>item function</c> of a tool (<c>hasTools</c> tests the inventory for it, <b>Verified</b>: <c>FUN_1405989e0</c>).
    /// </summary>
    public const int ToolFunction = 9;

    /// <summary>
    /// One update of cutting shackles (Task_CutShackles 185, <b>Observed</b>): hardness falls by <c>clamp(0.01 x strength, 0.05, 0.2) x dt x 2</c>;
    /// at 0 the best tool is used up and the lock is broken (true). False while still cutting. Books the picking crime.
    /// </summary>
    public bool CutShackles(World world, int cutter, CharacterId wearer, Items.ItemInstance item, float dt, CrimeSystem? crimes = null)
    {
        var c = world.Characters.Cold(cutter);
        if (c is null || item.Lock is not { Broken: false } l) return false;
        ShackleCrime(world, cutter, wearer, crimes);
        l.Hardness -= Math.Clamp(0.01f * CutStrength(c), 0.05f, 0.2f) * dt * 2;
        if (l.Hardness > 0) return false;
        l.Hardness = 0;
        if (BestTool(c) is { } tool) c.Inventory!.RemoveOne(tool);
        l.Broken = true;
        return true;
    }

    /// <summary>
    /// One update of cutting a fixture's lock (Task_CutLock 285, the shackle task's update, <b>Observed</b>): hardness falls by <c>clamp(0.01 x
    /// strength, 0.05, 0.2) x dt x 2</c>; at 0 the best tool is used up and the lock is broken, so it no longer holds (true). Books the picking
    /// crime. The needed tool is checked by the caller.
    /// </summary>
    public bool CutLock(World world, int cutter, Fixture target, float dt, CrimeSystem? crimes = null)
    {
        var c = world.Characters.Cold(cutter);
        if (c is null || target.Lock is not { Broken: false } l) return false;
        Crime(world, cutter, c, target, crimes);
        l.Hardness -= Math.Clamp(0.01f * CutStrength(c), 0.05f, 0.2f) * dt * 2;
        if (l.Hardness > 0) return false;
        l.Hardness = 0;
        if (BestTool(c) is { } tool) c.Inventory!.RemoveOne(tool);
        l.Broken = true;
        world.Fixtures.Touch(target);
        return true;
    }

    /// <summary>
    /// Breaks shackles by force (Task_BruteForceShackles 186, <b>Observed</b>): at once when strength &gt; 3 x hardness (true, "*Crunck*"), else "I'm
    /// not strong enough to do that".
    /// </summary>
    public bool BreakShackles(World world, int breaker, CharacterId wearer, Items.ItemInstance item, CrimeSystem? crimes = null)
    {
        var c = world.Characters.Cold(breaker);
        if (c is null || item.Lock is not { Broken: false } l) return false;
        ShackleCrime(world, breaker, wearer, crimes);
        if (Strength(c) <= 3 * l.Hardness) return false;
        l.Hardness = 0;
        l.Broken = true;
        return true;
    }

    /// <summary>
    /// Another character's shackles: code 7 against the faction that owns the wearer as a slave (status <c>+0xe0</c>, <c>FUN_140359d90</c>,
    /// <c>FUN_140359f80</c>, <c>FUN_140352c50</c>, <b>Verified</b>), booked every update before the roll. A wearer without an owner (-1) books
    /// nothing: <see cref="CrimeSystem.SetPending"/> refuses a faction that does not exist (the original sets the crime against a null faction, whose
    /// booking is <b>Unknown</b>), as it refuses the culprit's own faction. The wearer stays the crime's victim character (the original passes the
    /// wearer's owner handle); a self-target is not special-cased (the worker's own slave owner).
    /// </summary>
    static void ShackleCrime(World world, int worker, CharacterId wearer, CrimeSystem? crimes)
    {
        var table = world.Characters;
        if (crimes is null || !table.TryResolveNext(wearer, out int w) || table.Cold(w) is not { } victim) return;
        if (victim.SlaveOwner >= 0)
            crimes.SetPending(world, worker, CrimeCode.Terrorism, victim.SlaveOwner, wearer, CrimeSystem.MinTimer);
    }

    /// <summary>
    /// Breaks a lock by force (Task_BruteForceLock, <b>Observed</b>): succeeds at once when the effective strength is above 3 x hardness (the
    /// stat-scale term is taken as 1, an engine choice), setting the broken flag; books the same crime as picking. False: "I'm not strong enough to
    /// do that".
    /// </summary>
    public bool Break(World world, int picker, Fixture target, CrimeSystem? crimes = null)
    {
        var c = world.Characters.Cold(picker);
        if (c is null || target.Lock is not { } l || l.Broken) return false;
        Crime(world, picker, c, target, crimes);
        float strength = (c.Stats?[StatsEnumerated.Strength] ?? 0) * (c.Stats?.Scale ?? 1) * (c.Medical?.StatMultiplier(StatsEnumerated.Strength)
                ?? 1);
        if (strength <= 3 * l.Hardness) return false;
        l.Hardness = 0;
        l.Broken = true;
        if (target.Kind == FixtureKind.Door) target.DoorBroken = true;
        world.Fixtures.Touch(target);
        return true;
    }

    /// <summary>
    /// The pending crime of every update (<b>Verified</b>, <c>FUN_140359160</c>, <c>FUN_140359d90</c>): a cage holding a character resolves the
    /// task's target to that character, so it books code 7 against the captive's slave owner faction (a captive that is no slave books nothing, as
    /// <see cref="CrimeSystem.SetPending"/> refuses a faction that does not exist; the original books it against a null faction, <b>Unknown</b>); a
    /// self-pick is the same case. Any other fixture owned by another faction is Burglary (2) against its owner.
    /// </summary>
    static void Crime(World world, int picker, CharacterCold c, Fixture target, CrimeSystem? crimes)
    {
        if (crimes is null) return;
        var table = world.Characters;
        bool captive = target.Kind == FixtureKind.Cage && !FixtureRegistry.IsFree(table, target);
        if (captive)
        {
            if (table.TryResolveNext(target.Occupant, out int o) && table.Cold(o) is { SlaveOwner: >= 0 } oc)
                crimes.SetPending(world, picker, CrimeCode.Terrorism, oc.SlaveOwner, target.Occupant, CrimeSystem.MinTimer);
        }
        else if (target.Owner >= 0 && target.Owner != c.Faction)
            crimes.SetPending(world, picker, CrimeCode.Burglary, target.Owner, CharacterId.None, CrimeSystem.MinTimer);
    }
}