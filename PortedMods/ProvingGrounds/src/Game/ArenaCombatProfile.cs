using Meitou.Data.Gameplay.Bodies;
using Meitou.Simulation;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Player;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Port of the engine half of upstream/ProvingGrounds/plugin/src/ArenaCombatProfile.cpp (Proving Grounds by Shutori, GPLv3): Read (:62-87), task 7.2
// (TW §2.3). The formula, the weapon-category decisions and the text are Core/ArenaCombatProfile.cs (task 1.13a); this file reads the character.
// Meitou 0.17.3 (fa34a1b); paths relative to its repo root.
//
// Engine calls of Read and what replaces them (E1; docs/divergences.md "Matchmaking snapshot and search worker"):
//   character->isValid(), getStats() ... CharacterTable.TryResolveNext + Cold(slot).Stats; a character with no medical or combat state
//       (CharacterCold.Medical / Fighter null) also reads as an unusable profile (the getters below need both; every person has them)
//   getInventory()->getPrimaryWeapon(), else getSecondaryWeapon() when the primary is missing, invalid or unarmed ... Fighter.BackWeapon (the
//       first weapon of the back section, Combat/Fighter.cs:62), else Fighter.HipWeapon (the hip section, :72): Meitou's two weapon slots, in the
//       order it prefers them in hand (Fighter.Weapon, :53-57); like the C++, the arms are not consulted (sheathing never changes the card)
//   weapon->getCategory() ... WeaponData.SkillCategory (Kenshi's numbering, Meitou.Data/Gameplay/Combat/WeaponData.cs:8-51), mapped onto
//       Core.ArenaCombatProfile.WeaponCategory in Category below
//   stats->getMeleeAttack_melee() ... CombatSystem.AttackRating (internal, Members.CombatAttackRating; FUN_140884ee0)
//   stats->getMeleeAttack_unarmed(true) ... CombatSystem.UnarmedRating (internal, Members.CombatUnarmedRating; FUN_140884e00 with its flag set)
//   stats->getMeleeDefence_melee(false) ... CombatSystem.DefenceRating(..., guard: false) (internal, Members.CombatDefenceRating; FUN_140886290)
//   stats->getDodge(true) ... the HUD's Dodge value (FUN_140885190), computed here from public reads as Meitou's internal HudTips.AddDodge does
//       (Bodies/HudTips.cs:371-392): the trained dodge less the encumbrance and leg terms plus the gear's, +20 with fists in defensive mode
//       (the flag read as "with the defensive term"; not rounded: the HUD casts to int only to print)
//   stats->strengthActual() / dexterityActual() ... CombatSystem.Eff (internal; Combat/CombatSystem.Act.cs:47-48, FUN_140884ac0 "the original's
//       stat getter"): (stat + gear bonus) x scale x MedicalState.StatMultiplier(stat) x gear factor, whose gear bonus and factor are 0 and 1 for
//       these two stats, so computed here from the public pieces
//   stats->toughness() ... CharacterStats.ScaledToughness (CharacterStats.cs:63, as PgEngine.Stats)
//   stats->katanas / sabres / ... / polearms (SkillFor) ... the stored levels (CharacterStats[StatsEnumerated.X]); getEquippedWeaponSkill() ... the
//       stored level of the weapon-in-hand's skill (Fighter.WeaponSkill, :138)

/// <summary>C++ <c>ArenaCombatProfile::Read(Character*)</c>. Sim thread, serial steps only (it reads the character table like PgEngine).</summary>
static class ArenaCombatProfile
{
    delegate float RatingFn(World world, int slot, CharacterCold c, Fighter f);
    delegate float DefenceFn(World world, int slot, CharacterCold c, Fighter f, bool guard);

    static RatingFn? attackRating, unarmedRating;
    static DefenceFn? defenceRating;
    static bool bound, missing;

    /// <summary>Binds the three internal ratings once (false, with one log line, when one is missing: Part.CombatProfile).</summary>
    static bool Bind()
    {
        if (bound) return true;
        if (missing) return false;
        try
        {
            if (Members.CombatAttackRating is { } a && Members.CombatUnarmedRating is { } u && Members.CombatDefenceRating is { } d)
            {
                attackRating = a.CreateDelegate<RatingFn>();
                unarmedRating = u.CreateDelegate<RatingFn>();
                defenceRating = d.CreateDelegate<DefenceFn>();
                bound = true;
                return true;
            }
            Missing("not found");
        }
        catch (Exception e) { Missing($"{e.GetType().Name}: {e.Message}"); }
        return false;
    }

    static void Missing(string why)
    {
        missing = true;
        ProvingGroundsMod.Ctx?.Log($"Part CombatProfile off: CombatSystem ratings {why}; every town fighter's combat stats read as unavailable");
    }

    /// <summary>The port's mapping of Kenshi's <c>WeaponCategory</c> (Meitou's numbering, WeaponData.cs:8-51) onto
    /// <see cref="Core.ArenaCombatProfile.WeaponCategory"/>: 0-8 by name, anything else (the attack-only categories) <c>Other</c>.</summary>
    internal static Core.ArenaCombatProfile.WeaponCategory Category(int skillCategory) => skillCategory switch
    {
        (int)Meitou.Data.Gameplay.Combat.WeaponCategory.Katanas => Core.ArenaCombatProfile.WeaponCategory.Katanas,
        (int)Meitou.Data.Gameplay.Combat.WeaponCategory.Sabres => Core.ArenaCombatProfile.WeaponCategory.Sabres,
        (int)Meitou.Data.Gameplay.Combat.WeaponCategory.Blunt => Core.ArenaCombatProfile.WeaponCategory.Blunt,
        (int)Meitou.Data.Gameplay.Combat.WeaponCategory.Heavy => Core.ArenaCombatProfile.WeaponCategory.Heavy,
        (int)Meitou.Data.Gameplay.Combat.WeaponCategory.Hackers => Core.ArenaCombatProfile.WeaponCategory.Hackers,
        (int)Meitou.Data.Gameplay.Combat.WeaponCategory.Unarmed => Core.ArenaCombatProfile.WeaponCategory.Unarmed,
        (int)Meitou.Data.Gameplay.Combat.WeaponCategory.Bow => Core.ArenaCombatProfile.WeaponCategory.Bow,
        (int)Meitou.Data.Gameplay.Combat.WeaponCategory.Turret => Core.ArenaCombatProfile.WeaponCategory.Turret,
        (int)Meitou.Data.Gameplay.Combat.WeaponCategory.Polearms => Core.ArenaCombatProfile.WeaponCategory.Polearms,
        _ => Core.ArenaCombatProfile.WeaponCategory.Other,
    };

    // C++: ArenaCombatProfile.cpp:23-36 SkillFor (the stored levels; default = getEquippedWeaponSkill)
    static float SkillFor(CharacterStats stats, Fighter f, Core.ArenaCombatProfile.WeaponCategory category) => category switch
    {
        Core.ArenaCombatProfile.WeaponCategory.Katanas => stats[StatsEnumerated.Katanas],
        Core.ArenaCombatProfile.WeaponCategory.Sabres => stats[StatsEnumerated.Sabres],
        Core.ArenaCombatProfile.WeaponCategory.Blunt => stats[StatsEnumerated.Blunt],
        Core.ArenaCombatProfile.WeaponCategory.Heavy => stats[StatsEnumerated.HeavyWeapons],
        Core.ArenaCombatProfile.WeaponCategory.Hackers => stats[StatsEnumerated.Hackers],
        Core.ArenaCombatProfile.WeaponCategory.Unarmed => stats[StatsEnumerated.MartialArts],
        Core.ArenaCombatProfile.WeaponCategory.Bow => stats[StatsEnumerated.Crossbows],
        Core.ArenaCombatProfile.WeaponCategory.Turret => stats[StatsEnumerated.Turrets],
        Core.ArenaCombatProfile.WeaponCategory.Polearms => stats[StatsEnumerated.Polearms],
        _ => stats[f.WeaponSkill],
    };

    /// <summary>C++ <c>strengthActual()</c> / <c>dexterityActual()</c>: Meitou's internal <c>CombatSystem.Eff</c> for a stat with no gear bonus or
    /// factor, <c>stat x scale x MedicalState.StatMultiplier(stat)</c> (CombatSystem.Act.cs:47-48).</summary>
    internal static float Actual(CharacterStats stats, MedicalState medical, StatsEnumerated stat) =>
        stats[stat] * stats.Scale * medical.StatMultiplier(stat);

    /// <summary>C++ <c>stats-&gt;strengthActual()</c> and <c>dexterityActual()</c> of a live character (the Arena window's card, ArenaUI.cpp:567-568):
    /// <see cref="Actual"/>; a character without a medical state (none in practice) gives its stored levels. False for a stale id or no stats.</summary>
    internal static bool Actuals(World world, CharacterId id, out float strength, out float dexterity)
    {
        strength = dexterity = 0;
        if (id.IsNone || !world.Characters.TryResolveNext(id, out int slot) || world.Characters.Cold(slot) is not { Stats: { } stats } c) return false;
        strength = c.Medical is { } m ? Actual(stats, m, StatsEnumerated.Strength) : stats[StatsEnumerated.Strength];
        dexterity = c.Medical is { } n ? Actual(stats, n, StatsEnumerated.Dexterity) : stats[StatsEnumerated.Dexterity];
        return true;
    }

    /// <summary>C++ <c>getDodge(true)</c>: the HUD's Dodge (HudTips.AddDodge, Bodies/HudTips.cs:371-392, FUN_140885190), unrounded.</summary>
    static float Dodge(CharacterCold c, CharacterStats stats, MedicalState medical, Fighter f)
    {
        float dodge = stats[StatsEnumerated.Dodge];
        float encumbrance = -(1 - Math.Clamp(f.EncumbranceFactor, 0.4f, 1)) * dodge;
        float legs = -(1 - Math.Clamp(Speed.LegFactor(medical), 0.5f, 1)) * dodge;
        float gear = dodge * f.DodgeMult - dodge;
        bool defensive = (c.Orders & CharacterOrders.Block) != 0;
        return dodge + encumbrance + legs + gear + (defensive && f.Unarmed ? 20 : 0);
    }

    /// <summary>C++: ArenaCombatProfile.cpp:62-87 <c>Read(character)</c>: a new (unusable) profile for a stale id, no stats, no medical or combat
    /// state, or the ratings unbound.</summary>
    internal static Core.ArenaCombatProfile.Profile Read(World world, CharacterId id)
    {
        var profile = new Core.ArenaCombatProfile.Profile();
        if (id.IsNone || !world.Characters.TryResolveNext(id, out int slot) || world.Characters.Cold(slot) is not { } c) return profile;
        if (c.Stats is not { } stats) return profile;
        if (c.Medical is not { } medical || c.Fighter is not { } f || !Bind()) return profile;

        // Combat's preferred weapon can be the natural/unarmed weapon while an
        // NPC is idle. Inspect equipment slots so sheathing never changes the card.
        WeaponInstance? weapon = f.BackWeapon;
        if (weapon is null || weapon.Data.IsUnarmed)
            weapon = f.HipWeapon;
        bool usableWeapon = weapon is not null;
        var category = usableWeapon ? Category(weapon!.Data.SkillCategory) : Core.ArenaCombatProfile.WeaponCategory.Unarmed;
        bool martial = Core.ArenaCombatProfile.IsMartial(usableWeapon, category);
        double attack = martial ? unarmedRating!(world, slot, c, f) : attackRating!(world, slot, c, f);
        double defence = martial ? Dodge(c, stats, medical, f) : defenceRating!(world, slot, c, f, false);
        return Core.ArenaCombatProfile.FromStats(usableWeapon, category,
            attack: attack, defence: defence,
            strength: Actual(stats, medical, StatsEnumerated.Strength),
            toughness: stats.ScaledToughness,
            dexterity: Actual(stats, medical, StatsEnumerated.Dexterity),
            weaponSkill: martial ? 0 : SkillFor(stats, f, category));
    }

    /// <summary>C++ <c>TownMatchmakingRuntime::Ability(c)</c> (TownMatchmakingRuntime.cpp:30-32): <c>Evaluate(Read(c))</c>, -1 when unusable.</summary>
    internal static double Ability(World world, CharacterId id) => Core.ArenaCombatProfile.Evaluate(Read(world, id));
}
