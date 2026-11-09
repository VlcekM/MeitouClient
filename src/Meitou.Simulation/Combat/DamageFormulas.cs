using Meitou.Data.Gameplay.Combat;

namespace Meitou.Simulation.Combat;

/// <summary>
/// What the target is, for the weapon's damage multipliers against robots, humans and animals; <see cref="None"/> for a blow with no character target
/// (a wall, a door, the HUD's figure), multiplier 1.
/// </summary>
public enum TargetKind
{
    Human,
    Animal,
    Robot,
    None,
}

/// <summary>
/// A blow as it leaves the weapon, before armour and toughness (<c>FUN_1408868b0</c>): cut, blunt and pierce damage, the stun it carries (0 for
/// weapons), the bleed multiplier and the weapon's armour penetration.
/// </summary>
public readonly record struct DamagePacket(float Cut, float Blunt, float Pierce, float Stun, float BleedMultiplier, float ArmourPenetration)
{
    public float Sum => Cut + Blunt + Pierce + Stun;
}

/// <summary>
/// The damage of a blow from the wielder's stats and the weapon (docs/game/combat.md "Damage per blow", all <b>Observed</b>: <c>FUN_140883ba0</c>,
/// <c>FUN_140883c60</c>, <c>FUN_140883d10</c>, <c>FUN_140884020</c>). Every stat passed in is the <i>effective</i> stat: the stat times the injury and hunger
/// multiplier of <c>Bodies.StatMultiplier</c> (and the race scale slot, 1 for humans). <c>lerp(t, a, b)</c> is the game's, parameter first, and is not clamped.
/// </summary>
public static class DamageFormulas
{
    /// <summary>
    /// A weapon whose cut multiplier is strictly below its blunt multiplier (clubs, maces): its cut base and blunt base differ.
    /// </summary>
    public static bool IsBluntDominant(in WeaponStats w) => w.CutMultiplier < w.BluntMultiplier;

    /// <summary>
    /// cut = lerp((skill + dex) x cutMult / 200, cutBase, cut99), cutBase the stored <c>cut damage 1</c> (13) or, for a blunt-dominant weapon, 0.4 x the stored
    /// <c>blunt damage 1</c>. There is no floor: the weapon's <c>min cut damage</c> fields are read by no hit code (docs/game/combat.md "Weapons and damage").
    /// </summary>
    public static float Cut(float skill, float dexterity, in WeaponStats w, CombatConstants c)
    {
        float cutBase = IsBluntDominant(w) ? 0.4f * c.BluntDamage1 : c.CutDamage1;
        return Lerp((skill + dexterity) * w.CutMultiplier / 200, cutBase, c.CutDamage99);
    }

    /// <summary>
    /// blunt = lerp((0.5 skill + 1.5 strength) x bluntMult / 200, bluntBase, blunt99). The blunt base of a blunt-dominant weapon is the stored
    /// <c>blunt damage 1</c> (13); the base of the other weapons is 0 (<b>Verified</b>: the stats constructor <c>FUN_140897f30</c> zeroes it and sets it only for
    /// a blunt-dominant weapon), so a katana (blunt multiplier 0) does not stun with every cut.
    /// </summary>
    public static float Blunt(float skill, float strength, in WeaponStats w, CombatConstants c)
    {
        float bluntBase = IsBluntDominant(w) ? c.BluntDamage1 : 0;
        return Lerp((0.5f * skill + 1.5f * strength) * w.BluntMultiplier / 200, bluntBase, c.BluntDamage99);
    }

    /// <summary>
    /// pierce = the stored pierce multiplier x (skill + dex) x pierceMult + 10 x pierceMult (0 for every sword, club and fist; creature weapons have it).
    /// </summary>
    public static float Pierce(float skill, float dexterity, float weaponPierceMultiplier, CombatConstants c) =>
        c.PierceDamageMultiplier * (skill + dexterity) * weaponPierceMultiplier + 10 * weaponPierceMultiplier;

    /// <summary>
    /// The rating a fist blow is scaled by (<c>FUN_140884e00</c>, <b>Verified</b>): lerp(e, 0.2, 1) x (martial arts + the gear's <c>unarmed bonus</c>
    /// - the dust's attack penalty), e the encumbrance factor (1 unburdened). <paramref name="unarmedBonus"/> carries the indoors modifier inside
    /// (<c>FUN_140884580</c> with the flag every combat caller sets). <paramref name="martialArts"/> is the stat times the stats' read-time scale,
    /// without the injury multiplier (the owner's slot 0x398 factor is 1 for a person, <c>FUN_1405e5990</c>).
    /// </summary>
    public static float UnarmedRating(float martialArts, float unarmedBonus, float dustPenalty, float encumbrance) =>
        Lerp(Math.Clamp(encumbrance, 0, 1), 0.2f, 1f) * (martialArts + unarmedBonus - dustPenalty);

    /// <summary>
    /// A fist (<b>Verified</b>, <c>FUN_1408868b0</c> disassembled): with f = lerp(rating x 0.01, 0.25, 1), total = f x (<c>unarmed damage mult</c> x
    /// toughness + strength + 10), at least 1; blunt share g = lerp(rating x 0.01, 1, 0.4); blunt = g x total, cut = (1 - g) x total. Toughness is
    /// the stat times the read-time scale (no multiplier row); strength is the effective strength. No term is halved.
    /// </summary>
    public static (float Cut, float Blunt) Unarmed(float rating, float toughness, float strength, CombatConstants c)
    {
        float f = Lerp(rating * 0.01f, 0.25f, 1f);
        float total = MathF.Max(1, f * (c.UnarmedDamageMult * toughness + strength + 10));
        float g = Lerp(rating * 0.01f, 1f, 0.4f);

        return ((1 - g) * total, g * total);
    }

    /// <summary>
    /// The stumble threshold (<c>FUN_140884020</c>): toughness x scale x 0.01 x the stored <c>stumble damage max</c> (0.65 x 80), i.e. toughness x 0.52.
    /// </summary>
    public static float StumbleThreshold(float toughness, CombatConstants c) => toughness * 0.01f * c.StumbleDamageMax;

    /// <summary>
    /// The packet of an armed blow: the three damages, scaled by the weapon's multiplier for the target kind, the weapon's per-race damage (percent x
    /// 0.01) and the wearer's general damage multiplier (worn gear's <c>damage output mult</c>); the weapon's bleed multiplier and armour penetration
    /// ride along.
    /// <paramref name="targetRace"/> is the target's RACE string id (null for none).
    /// </summary>
    public static DamagePacket Packet(
        float skill,
        float dexterity,
        float strength,
        WeaponInstance weapon,
        TargetKind kind,
        string? targetRace,
        float damageOutput,
        CombatConstants c)
    {
        var w = weapon.Stats;
        float cut = Cut(skill, dexterity, w, c);
        float blunt = Blunt(skill, strength, w, c);
        float pierce = Pierce(skill, dexterity, weapon.Data.PierceDamageMultiplier, c);
        float scale = GeneralMultiplier(damageOutput) * KindMultiplier(weapon.Data, kind) * RaceMultiplier(weapon.Data, targetRace);

        return new DamagePacket(cut * scale, blunt * scale, pierce * scale, 0, weapon.Data.BleedMult, weapon.Data.ArmourPenetration);
    }

    /// <summary>
    /// The packet of a fist: cut and blunt from <see cref="Unarmed"/> (<paramref name="rating"/> from <see cref="UnarmedRating"/>), no pierce, no
    /// stun, the fists item's bleed multiplier and penetration (1 and 0 without one).
    /// </summary>
    public static DamagePacket UnarmedPacket(
        float rating,
        float toughness,
        float strength,
        WeaponInstance? fists,
        TargetKind kind,
        string? targetRace,
        float damageOutput,
        CombatConstants c)
    {
        var (cut, blunt) = Unarmed(rating, toughness, strength, c);
        float scale = GeneralMultiplier(damageOutput) * (fists is null ? 1 : KindMultiplier(fists.Data, kind) * RaceMultiplier(fists.Data, targetRace));

        return new DamagePacket(cut * scale, blunt * scale, 0, 0, fists?.Data.BleedMult ?? 1, fists?.Data.ArmourPenetration ?? 0);
    }

    public static float KindMultiplier(WeaponData weapon, TargetKind kind) => kind switch
    {
        TargetKind.Robot => weapon.RobotDamageMult,
        TargetKind.Animal => weapon.AnimalDamageMult,
        TargetKind.None => 1,
        _ => weapon.HumanDamageMult,
    };

    public static float RaceMultiplier(WeaponData weapon, string? targetRace) =>
        targetRace is not null && weapon.RaceDamage.TryGetValue(targetRace, out int percent) ? percent * 0.01f : 1;

    /// <summary>
    /// The wearer's general damage multiplier (CharStats +0x48): applied only when it is not 1 and above 0 (<c>FUN_1408868b0</c>, <b>Verified</b>),
    /// so a zero or negative product counts as 1.
    /// </summary>
    static float GeneralMultiplier(float damageOutput) => damageOutput > 0 ? damageOutput : 1;

    static float Lerp(float t, float a, float b) => a + (b - a) * t;
}
