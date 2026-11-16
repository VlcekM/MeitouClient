using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.World;
using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Ai;

/// <summary>
/// The town data and finders a player character's Obedient Slave job (187, Kidnap/ObedientSlave.cs) works with: the worker goals' finder (189 / 190)
/// over the town a point is in, and the combat goals 41 and 143 of its master's Slave list.
/// </summary>
public sealed partial class AiSystem
{
    #region Work

    /// <summary>
    /// Whether the town at <paramref name="at"/> has a workplace goal <paramref name="type"/> may work at for the character (the finders of 189 / 190
    /// return 0 without one: <see cref="PickWork"/>'s list, the player-flag gate and a free operator slot, the one it operates counting as free).
    /// </summary>
    internal bool HasWorkplaces(World world, CharacterId self, Vector3 at, TaskType type)
    {
        if (TownAt(at) is not (>= 0 and var site) || !WorkAllowed(world, self.Slot, site)) return false;

        var held = HeldWork(world, self);
        return WorkplacesFor(site, type).Any(b => HasFreeSlot(b, held));
    }

    /// <summary>
    /// The building a worker on goal <paramref name="type"/> works at in the town at <paramref name="at"/> (<see cref="PickWork"/>: a random
    /// workplace, the roll the character's and its <paramref name="epoch"/> of picks, 189 only one it can run), the clip it plays there, the
    /// building's functionality, its <c>hunger rate</c> and the faction owning the town; null when the town has none.
    /// </summary>
    internal ObedientWorkplace? WorkAt(World world, CharacterId self, Vector3 at, int epoch, TaskType type)
    {
        if (TownAt(at) is not (>= 0 and var site) || PickWork(world, self, site, epoch, type, HeldWork(world, self)) is not { } building)
            return null;

        return new ObedientWorkplace(building, _work.ClipOf(building), _work.FunctionalityOf(building), _work.HungerRateOf(building),
            WorkRangeOf(building), WorkOwnerOf(site));
    }

    /// <summary>
    /// The workplaces of the town at <paramref name="at"/> (none outside a town): row 94's candidates
    /// (<see cref="Production.ProductionSystem.InputSource"/>).
    /// </summary>
    internal IReadOnlyList<TownBuilding> TownWorkplaces(Vector3 at) => TownAt(at) is >= 0 and var site ? Places(site).Workplaces : [];

    /// <summary>
    /// The workplace an obedient player slave operates now (<see cref="CharacterCold.SlaveWork"/>), null for none.
    /// </summary>
    static TownBuilding? HeldWork(World world, CharacterId self) => world.Characters.Cold(self.Slot)?.SlaveWork?.Place.Building;

    /// <summary>
    /// The operator slots obedient player slaves hold: one standing at its workplace runs Task_OperateMachine (87 for 189, 221 for 190), whose
    /// start adds it to the building's operators and whose end removes it (<c>FUN_14035b430</c>, <c>FUN_140347350</c>, <b>Verified</b>), so it
    /// takes a slot of the functionality's <c>max operators</c> as an NPC worker does. Counted on the cadence pass with the members' kept slots.
    /// </summary>
    void ReserveObedientWork(World world)
    {
        var table = world.Characters;
        for (int s = 0; s < table.HighWater; s++)
        {
            if (table.Next[s].Alive && table.Cold(s) is { Ai: null, SlaveWork.Place.Building: var b })
                _workTaken[b] = _workTaken.GetValueOrDefault(b) + 1;
        }
    }

    #endregion

    #region Combat goals

    /// <summary>
    /// Combat goal 41 or 143 of the master's Slave list for an obedient player slave (docs/game/ai-tasks.md "Combat goals"): the desire
    /// (<see cref="FightDesire"/>: 41's constant 2, 143's 1; 0 when it cannot fight) times its finder's factor (41: 1 with a sensed enemy of the
    /// master, <see cref="Aggro.AggroSystem.FoeFinder.MasterEnemies"/>; 143: the squad mate's or owned character's fight threat, capped, whose
    /// attacker row 13 takes, <see cref="ProtectPick"/>), and the character it would fight. <paramref name="squad"/> is the slave's own squad (143's
    /// mates). While its fight runs (<paramref name="running"/>) the goal is kept as the AI keeps it (<see cref="FightFactor"/>): 41 while it senses
    /// an enemy or its finder still finds one, 143 while the finder values a mate above 0.
    /// </summary>
    internal (float Score, CharacterId Target) ObedientFight(World world, int slot, Squad squad, TaskType type, bool running)
    {
        float desire = FightDesire(world, slot, type, squad);
        if (!(desire > 0)) return (0, CharacterId.None);

        if (running)
        {
            if (Protects(type)) return (desire * ProtectPick(world, slot, type, squad, attacker: false).Factor, CharacterId.None);
            if (_aggro?.SensesOf(slot) is { Enemies: > 0 }) return (desire, CharacterId.None);
        }

        var (factor, target) = FightPick(world, slot, type, squad);
        return (desire * factor, target);
    }

    #endregion

    #region Running away

    /// <summary>
    /// The skill categories of a hip weapon that leave a player-faction character's row 35 wanted (<c>FUN_1405a34e0</c>): Heavy (3), crossbow (6),
    /// polearm (8).
    /// </summary>
    static readonly int[] RunningWeapons = [3, 6, 8];

    /// <summary>
    /// Goal 35 Running away of the master's Slave list for an obedient player slave: done (0) while its list A holds (<c>!39</c> no immediate threat
    /// and <c>6</c> no enemy sensed); else row 35's desire (AI slot 55 <c>FUN_1405a34e0</c>, <b>Verified</b>), whose player-faction branch gives 0
    /// while either arm works (<c>FUN_140644150</c>), while it shoots at a target that is not down (<c>FUN_1404366b0</c>: the crossbow controller's
    /// combat mode, <see cref="CharacterCold.RangedCombatMode"/>) or while a weapon in its <c>hip</c> section has another <c>skill category</c>
    /// than <see cref="RunningWeapons"/>; then the body's desire (<see cref="SelfPreservation.BodyRunDesire"/>).
    /// </summary>
    internal float ObedientRunDesire(World world, int slot)
    {
        var table = world.Characters;
        if (table.Cold(slot) is not { Medical: { } medical } cold) return 0;

        bool threatened = _combat is { } combat && Bodies.CharacterFacts.ImmediateThreat(combat, world, slot);
        if (!threatened && _aggro?.SensesOf(slot) is not { Enemies: > 0 }) return 0;

        if (medical.ArmWorks(left: true) || medical.ArmWorks(left: false) || Shoots(world, slot, cold) || FightsFromHip(cold)) return 0;

        return SelfPreservation.BodyRunDesire(cold, SelfPreservation.Walled(world, slot));
    }

    /// <summary>
    /// <c>FUN_1404366b0</c>: the crossbow controller is in combat mode and its target is there and not down.
    /// </summary>
    bool Shoots(World world, int slot, CharacterCold cold)
    {
        if (!cold.RangedCombatMode || _combat is not { } combat) return false;

        var table = world.Characters;
        var (target, generation) = combat.TargetOf(slot, table.Previous[slot].Generation);
        return target >= 0 && table.TryResolveNext(new CharacterId(target, generation), out int t) && table.Cold(t) is { } tc
            && !Bodies.CharacterFacts.Down(tc);
    }

    /// <summary>
    /// A weapon in the <c>hip</c> section whose <c>skill category</c> is not one of <see cref="RunningWeapons"/> (the loop of
    /// <c>FUN_1405a34e0</c>).
    /// </summary>
    bool FightsFromHip(CharacterCold cold)
    {
        if (cold.Inventory is not { } inventory) return false;

        foreach (var item in inventory.Items)
        {
            if (item.Section != "hip" || data.Combat.Weapon(item.Record) is not { } weapon) continue;
            if (Array.IndexOf(RunningWeapons, weapon.SkillCategory) < 0) return true;
        }

        return false;
    }

    #endregion
}

/// <summary>
/// A workplace an obedient player slave's 189 / 190 picked (<see cref="AiSystem.WorkAt"/>): the building, the clip it plays there, the building's
/// functionality (null: none), its functionality's <c>hunger rate</c>, how near the worker must come to it and the faction owning its town (-1: none).
/// </summary>
public sealed record ObedientWorkplace(TownBuilding Building, string Clip, string? Functionality, float HungerRate, float Range, int Owner);

/// <summary>
/// An obedient player slave standing at its workplace (<see cref="CharacterCold.SlaveWork"/>): the workplace, and whether its goal is 189 (real work:
/// row 87 operates the machine, row 92 empties a full output) rather than 190 (pretending, row 221).
/// </summary>
public sealed record ObedientWork(ObedientWorkplace Place, bool Operates);
