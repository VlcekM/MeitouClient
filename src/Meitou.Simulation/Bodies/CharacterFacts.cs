using Meitou.Data.Gameplay;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Perception;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// One member per predicate of the original, named after it and citing the original function, so that a fact the simulation and the UI both need has
/// one definition. Merge by original function, not by English word: "down" is three different originals (<see cref="Down"/>,
/// <see cref="TargetDown"/>, and the capture finders' KO / crawling sense tags, <c>CaptiveSystem</c>). Pure reads; callable from any phase that may
/// read the character. docs/simulation.md "Shared predicates".
/// </summary>
public static class CharacterFacts
{
    /// <summary>
    /// Posture 0 (vfunc <c>0x368</c>, <b>Verified</b> in <c>FUN_14028eca0</c> and <c>FUN_1409c70f0</c>): not knocked out or dead, not staying low,
    /// crawling or playing dead.
    /// </summary>
    public static bool PostureZero(CharacterCold cold) =>
        cold.Medical is not ({ Incapacitated: true } or { StayingLow: true } or { Crawling: true } or { PlayingDead: true });

    /// <summary>
    /// State 118 TARGET_IS_UNARMED (<c>FUN_1405a2290</c>, <b>Verified</b>): the target holds no weapon in hand (vfunc <c>0x3c0</c>) and every
    /// inventory section of category 0 (the weapon sections, <c>hip</c> and <c>back</c>) is empty, locked items included.
    /// </summary>
    public static bool TargetUnarmed(CharacterCold target) =>
        target.DrawnWeapon == WeaponKinds.None && (target.Inventory is not { } inv || !inv.Items.Exists(i => i.Section is "hip" or "back"));

    /// <summary>
    /// State 26 IS_CROUCHING_OR_LYING (<c>FUN_140599e90</c>, <b>Verified</b>), read by the planner and by the slave delivery: false when crippled (a
    /// crawler; an incapacitated one stands in for it), else true staying low (posture 1), playing dead (3) or in a bed. A knocked-out character is
    /// posture 4 and does not count.
    /// </summary>
    public static bool CrouchingOrLying(CharacterCold cold) =>
        cold.Medical is { Crawling: false, Incapacitated: false } m && (m.StayingLow || m.PlayingDead || cold.InSomething == Locks.HeldState.Bed);

    /// <summary>
    /// <c>Character</c> slot 106 (vfunc <c>0x350</c>, <c>FUN_140791d50</c>, <b>Verified</b>), the "Kidnapped" status and the witnesses' innocent
    /// victim: the kidnap victim flag (<see cref="CharacterCold.KidnapVictim"/>) while the character is carried, is no slave, has no bounty and is
    /// not of NPC class 8 (Bandit).
    /// </summary>
    public static bool Kidnapped(CharacterCold cold) =>
        cold.KidnapVictim && !cold.CarriedBy.IsNone && cold.SlaveState == 0 && cold.Bounties is not { Any: true }
        && cold.NpcClass != FundamentalType.Bandit;

    /// <summary>
    /// <c>Character::isDiplomaticStatus(factorInBiome)</c> (<c>FUN_140796240</c>, <b>Verified</b> by disassembly): NPC class 5 Diplomat and, with
    /// <paramref name="factorInBiome"/>, standing in a biome group of its own faction's <c>biomes</c>
    /// (<see cref="Population.PopulationData.InHomeBiome"/>). The war manager lookup of its platoon it also makes is discarded.
    /// </summary>
    public static bool DiplomaticStatus(CharacterCold cold, System.Numerics.Vector3 at, Population.PopulationData? data, bool factorInBiome) =>
        cold.NpcClass == FundamentalType.Diplomat && (!factorInBiome || data?.InHomeBiome(cold.Faction, at) == true);

    /// <summary>
    /// The campaign its faction's war manager holds the character's platoon in (<c>FUN_1409c4b90</c>, <b>Verified</b> by disassembly: none without a
    /// platoon) is still in phase 0, marching to its target (the record's <c>+0xc0</c>). Read by the attack-on-sight finder and the leave-alone test.
    /// </summary>
    public static bool MarchingToCampaign(World world, CharacterCold cold) =>
        cold.SquadId >= 0 && world.Squads.Find(cold.SquadId) is { PlatoonId: >= 0 } squad
        && world.Platoons.Find(squad.PlatoonId)?.Campaign is { Phase: 0 };

    /// <summary>
    /// The character's platoon is a <c>UniquePlatoon</c> (<c>Platoon</c> vfunc <c>0xb0</c> returns the platoon itself only for that class,
    /// <c>FUN_1409b0680</c>, the base <c>FUN_1407f22f0</c> 0; <b>Verified</b>): one made from an UNIQUE_SQUAD_TEMPLATE, or a saved PLATOON with
    /// <c>special</c> (<c>FUN_14057c6f0</c>). Engine reading: a character with no squad is in no such platoon (the original asks its faction's
    /// default platoon, <c>FUN_1406b9c60</c>).
    /// </summary>
    public static bool UniquePlatoon(World world, Meitou.Data.GameDatabase? db, CharacterCold cold)
    {
        if (cold.SquadId < 0 || world.Squads.Find(cold.SquadId) is not { } squad) return false;
        if (db?.Find(squad.TemplateId) is { Type: Meitou.Data.Fcs.FcsRecordType.UniqueSquadTemplate }) return true;

        return world.Platoons.Find(squad.PlatoonId)?.Saved?.Source.Record.Bools.GetValueOrDefault("special") == true;
    }

    /// <summary>
    /// <c>Character</c> slot 6 <c>isDown</c> (vfunc 0x30, <c>FUN_1405c9e10</c>, <b>Verified</b>): dead or out cold, playing dead (which is
    /// unconscious to everyone), ragdolled or carried. A crawler is not down. A ragdoll is only ever whole for an incapacitated character, so
    /// <see cref="Ragdolls.RagdollSystem.IsRagdoll"/> adds nothing the first test lacks, and is kept for the original's reading.
    /// </summary>
    public static bool Down(CharacterCold cold) =>
        cold.Medical is { Incapacitated: true } or { PlayingDead: true } || !cold.CarriedBy.IsNone || Ragdolls.RagdollSystem.IsRagdoll(cold);

    /// <summary>
    /// <c>isTargetDown</c> (state 7, <c>FUN_14099a5a0</c>, <b>Verified</b>): gone, carried, unconscious or dead, in a bed or cage, or crawling or
    /// playing dead (posture 2 or more; staying low is up).
    /// </summary>
    public static bool TargetDown(CharacterCold? c) =>
        c is null || !c.CarriedBy.IsNone || c.Medical is { Incapacitated: true } or { Crawling: true } or { PlayingDead: true }
            || c.InSomething != Locks.HeldState.None;

    /// <summary>
    /// Set Free's precondition 125 TARGET_IS_CONSCIOUS (<c>FUN_14099a480</c>, <b>Verified</b>; no row provides it): not dead, unconscious or carried.
    /// A ragdolled prisoner also fails in the original (not modelled apart from the knock-out).
    /// </summary>
    public static bool Conscious(CharacterCold c) => c.Medical is not { Incapacitated: true } && c.CarriedBy.IsNone;

    /// <summary>
    /// Awake: not dead or unconscious (a body that can be fought or traded with, whether carried or not).
    /// </summary>
    public static bool Standing(CharacterCold c) => c.Medical is not { Incapacitated: true };

    /// <summary>
    /// Plans with the animal repertoire (<see cref="Repertoire.Animal"/>, <see cref="SquadRegistry.IsAnimal"/>).
    /// </summary>
    public static bool Animal(CharacterCold c) => SquadRegistry.IsAnimal(c);

    /// <summary>
    /// The faction is hostile to the player's (<c>Relations.IsHostile</c>; another faction counts as hostile when the world has no relation table or
    /// the faction is unknown), or the character <paramref name="fightsPlayer"/>: a neutral squad robbing the player's characters is an enemy. Engine
    /// reading of the original's enemy test (docs/game/ui-input.md "Context menu verbs").
    /// </summary>
    public static bool HostileToPlayer(FactionRelations? relations, int player, int faction, bool fightsPlayer) =>
        fightsPlayer || relations is null || player < 0 || !relations.Contains(faction) || relations.IsHostile(player, faction);

    /// <summary>
    /// The "getting up" byte (<c>Character</c> <c>+0x278</c>): set while the character rises from a knock-down
    /// (<see cref="CharacterCold.GettingUpUntil"/>).
    /// </summary>
    public static bool GettingUp(CharacterCold cold, long tick) => cold.GettingUpUntil > tick;

    /// <summary>
    /// Whether a waking character stands through the get-up clip: not one carried, and not one waking in a bed (it lies on; <c>AnimationSystem</c>
    /// plays no clip, so nothing holds it).
    /// </summary>
    public static bool PlaysGetUp(CharacterCold cold) => cold.CarriedBy.IsNone && cold.InSomething != Locks.HeldState.Bed;

    /// <summary>
    /// <c>Character::isInCombatMode(melee, ranged)</c> (<c>FUN_140436610</c>, <see cref="CombatSystem.IsInCombatMode"/>).
    /// </summary>
    internal static bool InCombatMode(CombatSystem combat, CharacterTable table, CharacterId id, CharacterCold cold, bool melee, bool ranged) =>
        combat.IsInCombatMode(table, id, cold, melee, ranged);

    /// <summary>
    /// <c>Character::isStealthMode</c> (<c>FUN_1405c9750</c>, <b>Verified</b> disassembly: it calls <c>isInCombatMode(1, 1)</c> first): the Sneak
    /// order, and not in combat mode, melee or ranged.
    /// </summary>
    internal static bool StealthMode(World world, int slot, CharacterCold cold)
    {
        if ((cold.Orders & Player.CharacterOrders.Sneak) == 0) return false;

        var table = world.Characters;
        return world.SystemOf<CombatSystem>() is not { } combat || !combat.IsInCombatMode(table, table.IdOf(slot), cold, melee: true, ranged: true);
    }

    /// <summary>
    /// Slot 126, "A regards B as friendly" (<c>FUN_1407923d0</c>, <see cref="DialogueSystem.RegardsFriendly(World, int, int)"/>), by slot.
    /// </summary>
    internal static bool RegardsFriendly(DialogueSystem dialogue, World world, int a, int b) => dialogue.RegardsFriendly(world, a, b);

    /// <summary>
    /// Slot 125, "A regards B as an enemy" (<c>FUN_14079c950</c>), faction part: hostile and not coexisting in either direction
    /// (<see cref="AggroSystem.IsEnemy"/>), another faction when the world has no relation table. Engine reading: the original's squad memory tests
    /// are not read.
    /// </summary>
    public static bool RegardsEnemy(FactionRelations? relations, int mine, int theirs) =>
        relations is not null ? AggroSystem.IsEnemy(relations, mine, theirs) : mine != theirs;

    /// <summary>
    /// A fight is on in the squad: a member in combat or with an attack order (engine reading of the squad's "in combat" test).
    /// </summary>
    public static bool SquadFighting(CharacterTable table, Squad squad)
    {
        foreach (var m in squad.Members)
            if (table.TryResolveNext(m, out int s) && (table.Cold(s)!.InCombat || table.Next[s].Task == (byte)CharacterTask.Attack)) return true;
        return false;
    }

    /// <summary>
    /// The character's senses (<c>SensoryData</c>, Character vfunc 0x68; docs/game/perception.md "Senses"), as of its last look
    /// (<see cref="AggroSystem"/>); null before its first look or in a world without the look.
    /// </summary>
    public static SenseBlock? Senses(World world, int slot) => world.SystemOf<AggroSystem>()?.SensesOf(slot);

    /// <summary>
    /// <c>FUN_140855960</c> with its flag clear (docs/game/ai-tasks.md "Combat goals"): <paramref name="looker"/> knows <paramref name="who"/>:
    /// itself, or an entry of its senses (a world without senses knows everyone).
    /// </summary>
    public static bool Knows(World world, int looker, CharacterId who) =>
        who.Slot == looker || Senses(world, looker) is not { } b || b.Knows(who) || world.SystemOf<AggroSystem>() is null;

    /// <summary>
    /// Whether <paramref name="target"/> is on the looker's sensed list, as the original's finders that walk it (<c>FUN_140853960</c>) take their
    /// candidates: an entry of its senses, with <paramref name="now"/> only one seen or heard at the last look. The capped block
    /// (<see cref="SenseBlock.Cap"/>) may lack a character the original's unbounded map holds, so one it lacks is judged by the shared sight test
    /// (<see cref="SightRay.Sees"/>: same building or outdoors, unless either stands on top, and the sight ray; engine choice), except a sneaker on its feet, which only
    /// the look senses (its spot meter, #649). True in a world without the look; false for a looker that has not looked yet (#759: a guard's first
    /// think after load took a sneaking escaped slave 480 away).
    /// </summary>
    public static bool Perceives(World world, int looker, int target, bool now = false)
    {
        var table = world.Characters;
        if (Senses(world, looker) is not { } block) return world.SystemOf<AggroSystem>() is null;
        if (block.TryGet(table.IdOf(target), out var entry)) return !now || entry.Now;
        if (table.Cold(looker) is not { } cold || table.Cold(target) is not { } seen) return false;
        if ((seen.Orders & Player.CharacterOrders.Sneak) != 0 && seen.Medical is not { Incapacitated: true }) return false;

        float outdoor = AggroSystem.RangeOf(cold, AggroSystem.OutdoorSight);
        return SightRay.Sees(world, looker, target, table.Next[looker].Position, table.Next[target].Position, outdoor) > 0;
    }

    /// <summary>
    /// The planner's <c>iAmBeingMeleeAttacked immediate</c> (<c>+0x2b0</c>, <c>FUN_1404378f0</c>, <see cref="CombatSystem.ImmediateThreat"/>).
    /// </summary>
    internal static bool ImmediateThreat(CombatSystem combat, World world, int self) => combat.ImmediateThreat(world, self);
}