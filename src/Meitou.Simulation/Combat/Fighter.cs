using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Combat;

/// <summary>
/// What a character fights with: the weapon in hand (null for fists) and the worn armour, with the sums of the gear's bonuses taken once
/// (docs/game/combat.md "Armour": the worn-gear totals of <c>FUN_140887030</c>). Cold state of <see cref="CharacterCold"/>; it changes only in the serial phases
/// (equipping, an inventory change), never in a parallel phase. The order of <see cref="Armour"/> is the order the pieces are stacked in.
/// </summary>
public sealed class Fighter
{
    /// <summary>
    /// The weapon in hand (docs/game/combat.md "Arms"): with both arms working the <see cref="BackWeapon"/>, else the hip one; with only the right arm the hip one; without
    /// the right arm none (fists). An animal's natural weapon always.
    /// </summary>
    public WeaponInstance? Weapon => IsAnimal ? hip : !rightArm ? null : leftArm ? BackWeapon ?? hip : hip;
    /// <summary>The first weapon in the <c>back</c> section, if any (the constructor's weapon is the <c>hip</c> one).</summary>
    public WeaponInstance? BackWeapon { get; init; }
    /// <summary>Whether <see cref="Weapon"/> is the <see cref="BackWeapon"/> (the renderer shows that one drawn).</summary>
    public bool WeaponIsBack => Weapon is { } w && w == BackWeapon;
    /// <summary>The first weapon in the <c>hip</c> section, if any.</summary>
    public WeaponInstance? HipWeapon => hip;
    /// <summary>
    /// The <c>length</c> of the weapon in hand, the <see cref="Fists"/>' for fists (CharStats +0x200, set from the weapon bound, the fists item with nothing in hand).
    /// </summary>
    public float WeaponLength => Weapon?.Data.Length ?? Fists?.Data.Length ?? 0;
    /// <summary>
    /// A person's fists item (its race's <c>fists lv1</c> WEAPON, else <c>FISTS</c>: 18 long in the base game): its length, kind and race multipliers, bleed and
    /// penetration are a fist blow's; null when not set (fists then have length 0 and multipliers 1).
    /// </summary>
    public WeaponInstance? Fists { get; init; }
    /// <summary>The item a fist blow's packet reads its multipliers from: the weapon in hand (an animal's), else the <see cref="Fists"/>.</summary>
    public WeaponInstance? FistsItem => Weapon ?? Fists;
    readonly WeaponInstance? hip;
    bool rightArm = true, leftArm = true;
    /// <summary>The crossbow carried in a weapon section (back or hip), if any: the ranged weapon (docs/simulation.md "Combat", ranged). The melee <see cref="Weapon"/> stays what it was.</summary>
    public CrossbowInstance? Crossbow { get; init; }
    /// <summary>The worn pieces, in the order they are stacked in.</summary>
    public ReadOnlySpan<ArmourPiece> Armour => armour;
    readonly ArmourPiece[] armour;
    /// <summary>A creature (an ANIMAL_CHARACTER): the target kind for the weapon multipliers, and the techniques of its own weapon category apply.</summary>
    public bool IsAnimal { get; init; }
    /// <summary>
    /// The creature kind its techniques are picked by (COMBAT_TECHNIQUE <c>animal</c>): its natural weapon's <c>skill category</c> (9 to 20), 0 for a person (docs/game/animals.md).
    /// </summary>
    public int AnimalCategory => IsAnimal && Weapon is { } w ? w.Data.SkillCategory : 0;
    /// <summary>
    /// The weapon category a technique's <c>animal</c> value is compared with (<see cref="TechniqueChooser.Fits"/>): the weapon's <c>skill category</c>, 5 (unarmed) for fists.
    /// The martial arts techniques have <c>animal</c> 5, the creature ones 9 to 20, the weapon ones 0 (docs/game/animals.md).
    /// </summary>
    public int TechniqueCategory => Weapon?.Data.SkillCategory ?? (int)WeaponCategory.Unarmed;
    /// <summary>Whether it has an attack: an animal without a natural weapon (a swamp turtle) has none; it can still be hit.</summary>
    public bool CanAttack => !IsAnimal || Weapon is not null;
    /// <summary>The guarding state (+20 melee defence in the block chance).</summary>
    public bool Guarding { get; set; }
    /// <summary>The encumbrance factor in [0, 1] (<see cref="Encumbrance.Factor"/>); 1 is unburdened.</summary>
    public float EncumbranceFactor { get; set; } = 1;

    /// <summary>Sum of the pieces' <c>combat def bonus</c> (the "float equipment bonus" of the block chance).</summary>
    public float DefenceBonus { get; }
    /// <summary>Sum of the pieces' <c>combat attk bonus</c> and, for fists, <c>unarmed bonus</c>.</summary>
    public float AttackBonus { get; }
    public float UnarmedBonus { get; }
    /// <summary>Product of the pieces' <c>damage output mult</c>.</summary>
    public float DamageOutput { get; }
    /// <summary>Product of the pieces' <c>fist injury mult</c> (CharStats <c>+0x4c</c>): scales the damage a fist blow does to the puncher's own limb.</summary>
    public float FistInjury { get; }
    /// <summary>Product of the pieces' <c>dexterity mult</c>; 1.1 with no armour and no backpack (<see cref="Naked"/>).</summary>
    public float DexterityMult => Naked ? NakedBonus : dexterity;
    /// <summary>Product of the pieces' <c>dodge mult</c> (the gear's dodge value for <see cref="DefenceFormulas.DodgeSkill"/>, x 100); 1.1 when <see cref="Naked"/>.</summary>
    public float DodgeMult => Naked ? NakedBonus : dodgeMult;
    /// <summary>
    /// The gear's stealth factor (CharStats <c>+0x3c</c>, <c>FUN_140887030</c>, <b>Verified</b>): the backpack's <c>stealth mult</c> (1 without one) times each piece's
    /// <see cref="ArmourPiece.Stealth"/>; 1.2 when <see cref="Naked"/>. The backpack's value also seeds <see cref="DodgeMult"/>.
    /// </summary>
    public float StealthMult => Naked ? NakedStealth : stealth;
    readonly float dexterity, dodgeMult, assassin, ranged, stealth;
    /// <summary>The dodge and dexterity factor of a <see cref="Naked"/> character (Verified literal).</summary>
    public const float NakedBonus = 1.1f;
    /// <summary>The stealth factor of a <see cref="Naked"/> character (Verified literal).</summary>
    public const float NakedStealth = 1.2f;
    /// <summary>
    /// The gear factors of the stat multiplier (<see cref="StatFactors"/>, <c>CharStats</c> +0x40 to +0x50 as <c>FUN_140887030</c> writes them, <b>Verified</b>): the products of
    /// <c>asassination mult</c>, <c>dexterity mult</c>, <c>damage output mult</c> and <c>ranged skill mult</c>. No dust storm here (its factor is 1).
    /// </summary>
    public StatFactors Factors => new(assassin, DexterityMult, DamageOutput, ranged, 1);
    /// <summary>
    /// Wearing no armour and no backpack: the gear refresh then puts 1.1 for dodge and dexterity and 1.2 for stealth (<c>FUN_140887030</c>, <b>Verified</b> literals).
    /// Never for an animal (engine choice: whether the refresh runs for animals is <b>Unknown</b>).
    /// </summary>
    public bool Naked => !IsAnimal && armour.Length == 0 && !backpack;
    readonly bool backpack;
    /// <summary>Product of the pieces' combat speed factors (<see cref="ArmourPiece.CombatSpeed"/>) and the backpack's: scales the speed attack animations play at.</summary>
    public float CombatSpeed { get; }

    /// <summary>
    /// The gear's run speed factor (CharStats <c>+0x18</c>, <c>FUN_140887030</c>, <b>Verified</b>): the largest piece factor (at least 1) times the smallest, the backpack's
    /// <c>athletics mult</c> (1 without one) seeding the smallest; <see cref="Bodies.Speed.Run"/> scales the run speed by it.
    /// </summary>
    public float AthleticsFactor { get; }
    /// <summary>Sum of the pieces' <c>perception bonus</c> (CharStats <c>+0x30</c>), added to the trained perception before its multiplier (<c>FUN_140884ac0</c>).</summary>
    public int PerceptionBonus { get; }

    /// <param name="backpackStealth">The worn backpack's <c>stealth mult</c>, null without one (a negative value counts as 1).</param>
    /// <param name="backpackAthletics">The worn backpack's <c>athletics mult</c> (1 without one).</param>
    /// <param name="backpackSkill">The worn backpack's <c>combat skill bonus</c> (0 without one): it seeds both the attack and the defence bonus sums (<c>FUN_140887030</c>, <b>Verified</b>).</param>
    /// <param name="backpackSpeed">The worn backpack's <c>combat speed mult</c> (1 without one): it seeds the combat speed product.</param>
    public Fighter(WeaponInstance? weapon, IEnumerable<ArmourPiece>? armour = null, float? backpackStealth = null, float backpackAthletics = 1, int backpackSkill = 0, float backpackSpeed = 1)
    {
        hip = weapon;
        this.armour = armour is null ? [] : [.. armour];
        backpack = backpackStealth is not null;
        float seed = backpackStealth is { } b ? b < 0 ? 1 : b : 1;
        float def = backpackSkill, atk = backpackSkill, unarmed = 0, output = 1, fist = 1, dex = 1, dodge = seed, speed = backpackSpeed, sneak = seed, runMax = 1, runMin = backpackAthletics;
        int perception = 0;
        assassin = ranged = 1;
        foreach (var p in this.armour)
        {
            def += p.Data.CombatDefenceBonus;
            atk += p.Data.CombatAttackBonus;
            unarmed += p.Data.UnarmedBonus;
            perception += p.Data.PerceptionBonus;
            runMax = MathF.Max(runMax, p.Athletics);
            runMin = MathF.Min(runMin, p.Athletics);
            output *= p.Data.DamageOutputMult;
            fist *= p.Data.FistInjuryMult;
            dex *= p.Data.DexterityMult;
            dodge *= p.Data.DodgeMult;
            speed *= p.CombatSpeed;
            assassin *= p.Data.AssassinationMult;
            ranged *= p.Data.RangedSkillMult;
            sneak *= p.Stealth;
        }
        stealth = sneak;
        DefenceBonus = def;
        AttackBonus = atk;
        UnarmedBonus = unarmed;
        DamageOutput = output;
        FistInjury = fist;
        dexterity = dex;
        dodgeMult = dodge;
        CombatSpeed = speed;
        AthleticsFactor = runMax * runMin;
        PerceptionBonus = perception;
    }

    /// <summary>Sets which arms work (<see cref="Weapon"/>); true when that changes the weapon in hand. Only in the owner's own Act or a serial phase.</summary>
    public bool SetArms(bool right, bool left)
    {
        var before = Weapon;
        (rightArm, leftArm) = (right, left);
        return Weapon != before;
    }

    /// <summary>Fights with fists: no weapon, or a weapon record of the unarmed category (a creature attack).</summary>
    public bool Unarmed => Weapon is null || Weapon.Data.IsUnarmed;

    /// <summary>The skill that rates the fighter's blows: its weapon's, martial arts for fists.</summary>
    public StatsEnumerated WeaponSkill => Unarmed ? StatsEnumerated.MartialArts : Weapon!.Skill;

    /// <summary>The weapon type techniques are matched with (the unarmed bit for fists).</summary>
    public Meitou.Data.Gameplay.WeaponKinds Kind => Weapon?.Kind ?? Meitou.Data.Gameplay.WeaponKinds.Unarmed;

    /// <summary>A weapon that can parry (fists cannot: those fighters dodge).</summary>
    public bool CanBlock => Weapon is { } w && !Unarmed && w.CanBlock;
}
