using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Carrying;

/// <summary>
/// The kidnap (verb 246, the original's <c>Task_Kidnapping</c> over the lift; docs/game/carry-loot.md "Kidnapping"): a
/// <see cref="PickUpOrder.Kidnap"/> may also take a sleeping character, and on arrival the carrier rolls 0-100 against <see cref="KidnapChance"/>. A
/// hit knocks a sleeper out and the lift goes on; a miss wakes it, raises its town's alarm and turns it on the kidnapper. Either way the victim's
/// squad takes the kidnapper for an enemy and the kidnapping is a pending crime.
/// </summary>
public sealed partial class CarrySystem
{
    #region Kidnapping

    /// <summary>
    /// The knock-out a kidnap gives a sleeper: lerp(2, 300, the kidnapper's assassination / 100) seconds (<b>Verified</b> literals).
    /// </summary>
    public const float KidnapKoMin = 2, KidnapKoMax = 300;

    /// <summary>
    /// Asleep (the original's state 1): in a bed, or resting with the sleep behaviour; awake otherwise, and never when down.
    /// </summary>
    public static bool Asleep(CharacterTable table, int slot) =>
        table.Cold(slot) is { Medical.Incapacitated: false } c
        && (c.InSomething == Locks.HeldState.Bed || table.Next[slot].Task == (byte)CharacterTask.Rest && c.Ai?.Behaviour == AiBehaviour.Sleep);

    /// <summary>
    /// A sleeping character a kidnap may go for: of a <c>carriable</c> race, free, not carried.
    /// </summary>
    public static bool CanBeKidnapped(CharacterTable table, int slot) =>
        Asleep(table, slot) && table.Cold(slot) is { Race.Carriable: true, IsImprisoned: false } c && c.CarriedBy.IsNone;

    /// <summary>
    /// The chance in per cent that a kidnap takes <paramref name="target"/> at <paramref name="tick"/> (<c>Character::getKidnappingChance</c>,
    /// <b>Verified</b> literals): 100 for a down target (<see cref="CharacterFacts.Down"/>: out, playing dead, carried or ragdolled) that is not
    /// getting up (+0x278); else the stealth knock-out's formula (<see cref="StealthKoChance"/>) with the target's strength in the place of its
    /// toughness and no sense terms: X = the kidnapper's assassination minus the target's best of assassination, half its thievery, half its stealth
    /// term x 100, half its melee defence, its strength; 100 x (X + K) / 2K, K = 30 for X >= 0 else 15, unclamped.
    /// </summary>
    public static float KidnapChance(CharacterCold kidnapper, CharacterCold target, long tick)
    {
        if (CharacterFacts.Down(target) && !CharacterFacts.GettingUp(target, tick)) return 100;
        if (kidnapper.Stats is not { } a || target.Stats is not { } t) return 0;
        static float Skill(CharacterCold c, CharacterStats s, StatsEnumerated stat) =>
            s[stat] * (c.Medical?.StatMultiplier(stat) ?? 1) * Combat.CombatSystem.GearFactor(c, stat);
        float defence = Skill(target, t, StatsEnumerated.Assassination);
        defence = MathF.Max(defence, Skill(target, t, StatsEnumerated.Thieving) * 0.5f);
        defence = MathF.Max(defence, Orders.Stealth(target) * 100 * 0.5f);
        defence = MathF.Max(defence, Skill(target, t, StatsEnumerated.MeleeDefence) * 0.5f);
        defence = MathF.Max(defence, Skill(target, t, StatsEnumerated.Strength));
        float x = Skill(kidnapper, a, StatsEnumerated.Assassination) - defence;
        float k = x < 0 ? 15 : 30;
        return (x + k) / (2 * k) * 100;
    }

    /// <summary>
    /// The kidnap's roll on arrival (<c>Task_Kidnapping</c> vfunc 1, <b>Observed</b>); true when the lift goes on. First the victim's squad tags the
    /// kidnapper TEMP_ENEMY and the kidnapping becomes a pending crime on it. A hit (roll &lt;= chance) knocks an awake victim out
    /// (<see cref="KidnapKoMin"/>..<see cref="KidnapKoMax"/>) and trains assassination by 100. A miss: the victim's town alarm is raised at the
    /// kidnapper; its squad remembers it (long-term intruder) and says event 8 to it; assassination trains by 300; a sleeping victim wakes and goes
    /// for the kidnapper. The victim notices the kidnapper for 5 s (<see cref="AggroSystem.Alarm"/>). Engine: the kidnapper's field +0x110 = 250
    /// (<b>Unknown</b>) are not modelled.
    /// </summary>
    bool Kidnap(World world, CharacterTable table, int i)
    {
        var cold = table.Cold(i)!;
        if (!table.TryResolveNext(cold.CarryTarget, out int b) || table.Cold(b) is not { } victim) return false;
        var kidnapperId = table.IdOf(i);
        var victimId = table.IdOf(b);
        var talk = world.SystemOf<Dialogue.DialogueSystem>();
        talk?.TagEnemy(world, b, i);
        Crimes?.SetPending(world, i, Crime.CrimeCode.Kidnapping, victim.Faction, victimId, Crime.CrimeSystem.MinTimer);
        float chance = KidnapChance(cold, victim, world.Tick);
        float roll = Rng.Float(Rng.Hash(world.Seed, Rng.Key(kidnapperId), RngPurpose.Kidnap, (ulong)world.Tick)) * 100;
        bool success = roll <= chance;

        // A miss always trains assassination by 300; a hit by 100 only on a victim neither out nor dead (the branch that knocks it out,
        // FUN_14034ffa0).
        void Train(float amount)
        {
            if (cold.Stats is { } stats && cold.Race is { } race)
                (_xp ??= new XpService(Constants, _bodyOptions)).Continuous(stats, race, StatsEnumerated.Assassination, 1, amount);
        }
        if (success)
        {
            if (victim.Medical is { Incapacitated: false } medical)
            {
                float assassination = cold.Stats?[StatsEnumerated.Assassination] ?? 0;
                medical.KnockOut(KidnapKoMin + (KidnapKoMax - KidnapKoMin) * assassination * 0.01f);
                TaskStart.Stand(table, b);
                table.Next[b].Velocity = System.Numerics.Vector3.Zero;
                Train(100);
            }
            return true;
        }
        Train(300);
        bool asleep = Asleep(table, b);
        int town = victim.SquadId >= 0 ? world.Squads.Find(victim.SquadId)?.Town ?? -1 : -1;
        Crimes?.TownAlarm(world, i, town, Crime.CrimeCode.Kidnapping, victim.Faction);

        // The kidnapper is marked recently wanted (its status +0x110 = 250), so a looker who knows it recognises it at once.
        if (table.Cold(i) is { } kidnapper) kidnapper.RecentlyWanted = Crime.CrimeSystem.RecentlyWantedSeconds;
        if (talk is not null && victim.SquadId >= 0) talk.Memory.Remember(world, victim.SquadId, kidnapperId, Dialogue.MemoryTag.Intruder);
        world.Defer(new Dialogue.DialogueEvent(victimId, kidnapperId, 8) { Tick = world.Tick + 1 });

        // Every victim notices the kidnapper for 5 s: the forced sense with the alarm flag (FUN_14034ffa0:73 -> FUN_140858f40, entry seen flag set,
        // timer +0x3c = 5), then the step decides what a player character does (CarrySystem.Engage).
        bool decides = DecisionSystem.Decides(victim);
        Aggro?.Alarm(world, victimId, kidnapperId);
        if (asleep)
        {
            if (victim.InSomething == Locks.HeldState.Bed) world.Fixtures.LeaveBed(world, victimId);
            TaskStart.Become(world, b, CharacterTask.Idle, Meitou.Data.Gameplay.Ai.TaskType.Idle, TaskSource.Reaction);
            if (decides)
            {
                victim.OrderQueue.Clear();   // order 116 Getting out of bed replaces the whole queue (FUN_1405d20d0, clear flag 1)
                Engage(world, b, i);
            }
            else world.Defer(new Combat.AttackOrder([victimId], kidnapperId) { Tick = world.Tick + 1, Source = TaskSource.Reaction });
        }
        return false;
    }

    #endregion
}