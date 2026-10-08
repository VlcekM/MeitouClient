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
    public WeaponInstance? Weapon { get; }
    /// <summary>The worn pieces, in the order they are stacked in.</summary>
    public ReadOnlySpan<ArmourPiece> Armour => armour;
    readonly ArmourPiece[] armour;
    /// <summary>A creature (an ANIMAL_CHARACTER): the target kind for the weapon multipliers, and the techniques of its own weapon category apply.</summary>
    public bool IsAnimal { get; init; }
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
    /// <summary>Product of the pieces' <c>dexterity mult</c>.</summary>
    public float DexterityMult { get; }
    /// <summary>Product of the pieces' <c>dodge mult</c> (the gear's dodge value for <see cref="DefenceFormulas.DodgeSkill"/>, x 100).</summary>
    public float DodgeMult { get; }
    /// <summary>Product of the pieces' <c>combat speed mult</c>: scales the speed attack animations play at.</summary>
    public float CombatSpeed { get; }

    public Fighter(WeaponInstance? weapon, IEnumerable<ArmourPiece>? armour = null)
    {
        Weapon = weapon;
        this.armour = armour is null ? [] : [.. armour];
        float def = 0, atk = 0, unarmed = 0, output = 1, dex = 1, dodge = 1, speed = 1;
        foreach (var p in this.armour)
        {
            def += p.Data.CombatDefenceBonus;
            atk += p.Data.CombatAttackBonus;
            unarmed += p.Data.UnarmedBonus;
            output *= p.Data.DamageOutputMult;
            dex *= p.Data.DexterityMult;
            dodge *= p.Data.DodgeMult;
            speed *= p.Data.CombatSpeedMult;
        }
        DefenceBonus = def;
        AttackBonus = atk;
        UnarmedBonus = unarmed;
        DamageOutput = output;
        DexterityMult = dex;
        DodgeMult = dodge;
        CombatSpeed = speed;
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
