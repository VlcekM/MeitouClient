using Meitou.Data.Gameplay.Combat;

namespace Meitou.Simulation.Combat;

/// <summary>What the target is, for the weapon's damage multipliers against robots, humans and animals.</summary>
public enum TargetKind { Human, Animal, Robot }

/// <summary>
/// A blow as it leaves the weapon, before armour and toughness (<c>FUN_1408868b0</c>): cut, blunt and pierce damage, the stun it carries (0 for weapons), the bleed
/// multiplier and the weapon's armour penetration.
/// </summary>
public readonly record struct DamagePacket(float Cut, float Blunt, float Pierce, float Stun, float BleedMultiplier, float ArmourPenetration)
{
    public float Sum => Cut + Blunt + Pierce + Stun;
}

/// <summary>
/// The damage of a blow from the wielder's stats and the weapon (docs/game/combat.md "Damage per blow", all <b>Observed</b>: <c>FUN_140883ba0</c>, <c>FUN_140883c60</c>,
/// <c>FUN_140883d10</c>, <c>FUN_140884020</c>). Every stat passed in is the <i>effective</i> stat: the stat times the injury and hunger multiplier of
/// <c>Bodies.StatMultiplier</c> (and the race scale slot, 1 for humans). <c>lerp(t, a, b)</c> is the game's, parameter first, and is not clamped.
/// </summary>
public static class DamageFormulas
{
    static float Lerp(float t, float a, float b) => a + (b - a) * t;

    /// <summary>A weapon whose cut multiplier is strictly below its blunt multiplier (clubs, maces): its cut base and blunt base differ.</summary>
    public static bool IsBluntDominant(in WeaponStats w) => w.CutMultiplier < w.BluntMultiplier;

    /// <summary>
    /// cut = lerp((skill + dex) x cutMult / 200, cutBase, cut99), cutBase the stored <c>cut damage 1</c> (13) or, for a blunt-dominant weapon, 0.4 x the stored <c>blunt damage 1</c>.
    /// The result is not below the weapon's minimum cut damage.
    /// </summary>
    public static float Cut(float skill, float dexterity, in WeaponStats w, CombatConstants c)
    {
        float cutBase = IsBluntDominant(w) ? 0.4f * c.BluntDamage1 : c.CutDamage1;
        float cut = Lerp((skill + dexterity) * w.CutMultiplier / 200, cutBase, c.CutDamage99);
        return MathF.Max(cut, w.MinCut);
    }

    /// <summary>
    /// blunt = lerp((0.5 skill + 1.5 strength) x bluntMult / 200, bluntBase, blunt99). The blunt base of a blunt-dominant weapon is the stored <c>blunt damage 1</c> (13); the base of
    /// the other weapons was not decoded (<b>Unknown</b>) and is 0 here, which keeps a katana (blunt multiplier 0) from stunning with every cut.
    /// </summary>
    public static float Blunt(float skill, float strength, in WeaponStats w, CombatConstants c)
    {
        float bluntBase = IsBluntDominant(w) ? c.BluntDamage1 : 0;
        return Lerp((0.5f * skill + 1.5f * strength) * w.BluntMultiplier / 200, bluntBase, c.BluntDamage99);
    }

    /// <summary>pierce = the stored pierce multiplier x (skill + dex) x pierceMult + 10 x pierceMult (0 for every sword, club and fist; creature weapons have it).</summary>
    public static float Pierce(float skill, float dexterity, float weaponPierceMultiplier, CombatConstants c) =>
        c.PierceDamageMultiplier * (skill + dexterity) * weaponPierceMultiplier + 10 * weaponPierceMultiplier;

    /// <summary>
    /// A fist (<b>Observed</b>, the term shapes are <b>Unknown</b>): martial arts factor f = lerp(martial x 0.01, 0.25, 1); total = f x (0.8 x toughness term + strength term + 10), at least 1;
    /// blunt share g = lerp(martial x 0.01, 1, 0.4); blunt = g x total, cut = (1 - g) x total. The 0.8 is <c>unarmed damage mult</c>. The engine takes each term as half the stat.
    /// </summary>
    public static (float Cut, float Blunt) Unarmed(float martialArts, float toughness, float strength, CombatConstants c)
    {
        float f = Lerp(martialArts * 0.01f, 0.25f, 1f);
        float total = MathF.Max(1, f * (c.UnarmedDamageMult * (0.5f * toughness) + 0.5f * strength + 10));
        float g = Lerp(martialArts * 0.01f, 1f, 0.4f);
        return ((1 - g) * total, g * total);
    }

    /// <summary>The stumble threshold (<c>FUN_140884020</c>): toughness x scale x 0.01 x the stored <c>stumble damage max</c> (0.65 x 80), i.e. toughness x 0.52.</summary>
    public static float StumbleThreshold(float toughness, CombatConstants c) => toughness * 0.01f * c.StumbleDamageMax;

    /// <summary>
    /// The packet of an armed blow: the three damages, scaled by the weapon's multiplier for the target kind, the weapon's per-race damage (percent x 0.01) and the wearer's general
    /// damage multiplier (worn gear's <c>damage output mult</c>); the weapon's bleed multiplier and armour penetration ride along. <paramref name="targetRace"/> is the target's RACE
    /// string id (null for none).
    /// </summary>
    public static DamagePacket Packet(float skill, float dexterity, float strength, WeaponInstance weapon, TargetKind kind, string? targetRace, float damageOutput, CombatConstants c)
    {
        var w = weapon.Stats;
        float cut = Cut(skill, dexterity, w, c);
        float blunt = Blunt(skill, strength, w, c);
        float pierce = Pierce(skill, dexterity, weapon.Data.PierceDamageMultiplier, c);
        float scale = damageOutput * KindMultiplier(weapon.Data, kind) * RaceMultiplier(weapon.Data, targetRace);
        return new DamagePacket(cut * scale, blunt * scale, pierce * scale, 0, weapon.Data.BleedMult, weapon.Data.ArmourPenetration);
    }

    /// <summary>The packet of a fist: cut and blunt from <see cref="Unarmed"/>, no pierce, no stun, bleed multiplier 1 and no penetration.</summary>
    public static DamagePacket UnarmedPacket(float martialArts, float toughness, float strength, WeaponInstance? fists, TargetKind kind, string? targetRace, float damageOutput, CombatConstants c)
    {
        var (cut, blunt) = Unarmed(martialArts, toughness, strength, c);
        float scale = damageOutput * (fists is null ? 1 : KindMultiplier(fists.Data, kind) * RaceMultiplier(fists.Data, targetRace));
        return new DamagePacket(cut * scale, blunt * scale, 0, 0, fists?.Data.BleedMult ?? 1, fists?.Data.ArmourPenetration ?? 0);
    }

    public static float KindMultiplier(WeaponData weapon, TargetKind kind) => kind switch
    {
        TargetKind.Robot => weapon.RobotDamageMult,
        TargetKind.Animal => weapon.AnimalDamageMult,
        _ => weapon.HumanDamageMult,
    };

    public static float RaceMultiplier(WeaponData weapon, string? targetRace) =>
        targetRace is not null && weapon.RaceDamage.TryGetValue(targetRace, out int percent) ? percent * 0.01f : 1;
}
