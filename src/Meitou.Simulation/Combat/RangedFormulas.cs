using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Combat;

/// <summary>
/// A crossbow in a character's weapon slot: its record and its numbers at the item's quality (docs/game/combat.md "Ranged and turrets").
/// </summary>
public sealed class CrossbowInstance(CrossbowData data, float quality, CrossbowStats stats)
{
    public CrossbowData Data { get; } = data;

    /// <summary>
    /// The quality fraction (0 to 1) the stats were lerped at: the item's level x 0.01.
    /// </summary>
    public float Quality { get; } = quality;

    public CrossbowStats Stats { get; } = stats;

    /// <summary>
    /// The item weight (<c>weight kg</c> x <c>weapon inventory weight mult</c>, the Weapon constructor <c>FUN_140898830</c>; the crossbow keeps it).
    /// </summary>
    public float Weight { get; init; }

    /// <summary>
    /// In the <c>back</c> section (else the <c>hip</c>).
    /// </summary>
    public bool AtBack { get; init; }

    /// <summary>
    /// Shots one reload fills the crossbow with (<c>num shots</c>).
    /// </summary>
    public int NumShots => Math.Max(Data.NumShots, 1);

    /// <summary>
    /// No <c>ammo</c> reference: the crossbow never runs dry.
    /// </summary>
    public bool InfiniteAmmo => Data.AmmoId.Length == 0;

    /// <summary>
    /// The gun of a CROSSBOW at quality fraction <paramref name="q"/> with the CONSTANTS <c>bow damage 1/99</c> and the global damage multiplier
    /// (<c>FUN_14043c180</c>).
    /// </summary>
    public static CrossbowInstance Create(
        float q,
        CrossbowData data,
        CombatConstants constants,
        float globalDamageMultiplier,
        float weight = 0,
        bool atBack = false) =>
        new(data, q, data.At(q, constants, globalDamageMultiplier)) { Weight = weight, AtBack = atBack };
}

/// <summary>
/// The formulas of the crossbow (docs/game/combat.md "Ranged and turrets"; all <b>Observed</b> in the decompile of <c>kenshi_x64.exe</c> unless the doc says
/// otherwise). Pure functions, tested against the doc.
/// </summary>
public static class RangedFormulas
{
    /// <summary>
    /// Seconds the aim must be held before a shot, reset after every shot, every reload and whenever the gun is not ready (<c>FUN_140437ad0</c>).
    /// </summary>
    public const float SteadyAim = 0.4f;

    /// <summary>
    /// The bleed multiplier of a bolt's damage packet (a constant 2, <c>FUN_140439c90</c>).
    /// </summary>
    public const float BoltBleed = 2;

    /// <summary>
    /// Degrees per second a spent bolt's nose pitches down once its range is used up (<c>FUN_1404380e0</c>).
    /// </summary>
    public const float DropDegreesPerSecond = 8;

    /// <summary>
    /// A bolt is dropped this many units past its range (<c>FUN_1404380e0</c>).
    /// </summary>
    public const float DropAfter = 5000;

    /// <summary>
    /// A terrain hit whose |cos| between the surface normal and the flight is below this grazes off and the bolt is dropped (<c>FUN_1404380e0</c>).
    /// </summary>
    public const float Grazing = 0.1f;

    /// <summary>
    /// XP rates of the ranged controller (<c>FUN_1408c7250</c>'s per-stat amounts, x dt): crossbows 1.2, perception 3.0, dexterity 0.5 while reloading.
    /// </summary>
    public const float CrossbowXp = 1.2f, PerceptionXp = 3f, DexterityReloadXp = 0.5f;

    /// <summary>
    /// FRIENDLY_FIRE XP (<c>FUN_1408c7310</c>, dt 1): a bolt that passed a friend, and one that hit a friend.
    /// </summary>
    public const float FriendlyPassXp = 20, FriendlyHitXp = 100;

    /// <summary>
    /// The general skill blend <c>FUN_140885ce0</c>: 0.01 x (0.6 x skill + 0.4 x dexterity x m), with both stats already times their injury multipliers and m the
    /// stats multiplier (<c>CharStats +0x34</c>, taken as the gear's dexterity product here). The reload uses it with the crossbow skill.
    /// </summary>
    public static float SkillBlend(float skill, float dexterity, float m = 1) => 0.01f * (0.6f * skill + 0.4f * dexterity * m);

    /// <summary>
    /// Reload seconds (<c>FUN_140436010</c>): clamp(lerp(s, reload max, reload min), reload min, reload max). Spring Bat's max above its min at high quality is
    /// clamped the same way.
    /// </summary>
    public static float ReloadTime(float s, float reloadMin, float reloadMax)
    {
        float lo = MathF.Min(reloadMin, reloadMax);
        float hi = MathF.Max(reloadMin, reloadMax);

        return Math.Clamp(Lerp(s, reloadMax, reloadMin), lo, hi);
    }

    /// <summary>
    /// The half-angle of the deviation cone in degrees (<c>FUN_14043a730</c>, the same <c>0.005 x (skill + perception)</c> as <c>FUN_140435160</c> and the turret
    /// tooltip, <b>Verified</b>): max(0, deviation x (1 - t)), t = 0.005 (skill + perception). 0 at skill + perception 200.
    /// </summary>
    public static float Deviation(float deviationAtZero, float skill, float perception) => MathF.Max(0, Lerp(0.005f * (skill + perception), deviationAtZero, 0));

    /// <summary>
    /// The height factor (<c>FUN_140435f60</c>): f = (|dy| - 20) / 280 capped at 1 (not floored, so a small |dy| gives a small negative f); above the target
    /// (dy &gt; 0) 1 + 0.5 f, level or below 1 - 0.4 f; 1 for a target at height exactly 0 (a guard in the code). dy = barrel height - target height.
    /// </summary>
    public static float HeightFactor(float barrelY, float targetY)
    {
        if (targetY == 0) return 1;

        float dy = barrelY - targetY;
        float f = MathF.Min(1, (MathF.Abs(dy) - 20) / 280);

        return dy > 0 ? 1 + 0.5f * f : 1 - 0.4f * f;
    }

    /// <summary>
    /// In range (<c>FUN_140436860</c>): squared distance &lt; h x range^2 (the factor multiplies the squared range, unlike the bolt's own range: a quirk kept).
    /// </summary>
    public static bool InRange(float distanceSquared, float range, float heightFactor) => distanceSquared < heightFactor * range * range;

    /// <summary>
    /// The flight of one bolt of a crossbow (R = W = 1): its damage from a roll w in [0, 1), its range and its speed (<c>FUN_14043a730</c> step 7).
    /// </summary>
    public static (float Damage, float Range, float Speed) Bolt(in CrossbowStats stats, float heightFactor, float w) =>
        (
            (stats.PierceMin + w * (stats.PierceMax - stats.PierceMin)) * heightFactor,
            stats.Range * heightFactor,
            stats.ShotSpeed * (0.8f * heightFactor + 0.2f));

    /// <summary>
    /// The chance a bolt passes a friend in its way (<c>FUN_14043a730</c> step 8): min(1, FRIENDLY_FIRE x 0.015).
    /// </summary>
    public static float FriendlyAvoidance(float friendlyFire) => MathF.Min(1, MathF.Max(0, friendlyFire) * 0.015f);

    /// <summary>
    /// A direction tilted off <paramref name="dir"/> (unit) by <paramref name="degrees"/> about an axis perpendicular to it, turned by <paramref name="spin"/>
    /// (0 to 1 of a turn) round <paramref name="dir"/> (Ogre's <c>Vector3::randomDeviant</c>). With a tilt of u x D degrees for a uniform u the shots fill a cone
    /// of half-angle D, denser in the middle.
    /// </summary>
    public static Vector3 Deviate(Vector3 dir, float degrees, float spin)
    {
        if (degrees <= 0) return dir;

        // Any vector perpendicular to dir, turned round dir by the spin, is the axis of the tilt.
        var helper = MathF.Abs(dir.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var axis = Vector3.Normalize(Vector3.Cross(dir, helper));
        axis = Vector3.Transform(axis, Quaternion.CreateFromAxisAngle(dir, spin * MathF.Tau));

        return Vector3.Normalize(Vector3.Transform(dir, Quaternion.CreateFromAxisAngle(axis, degrees * (MathF.PI / 180))));
    }

    static float Lerp(float t, float a, float b) => a + (b - a) * t;
}

/// <summary>
/// The formulas of a turret's gun (<c>GunClassTurret</c>, docs/game/combat.md "Turrets"; <b>Observed</b> in the decompile of <c>kenshi_x64.exe</c>). Pure
/// functions. A crossbow is the same controller and shot with all the building factors 1 (<see cref="RangedFormulas.Bolt"/>).
/// </summary>
public static class TurretFormulas
{
    /// <summary>
    /// Degrees the turning parts may still be off for the gun to count as aimed (<c>FUN_140437ad0</c>: the turret's largest error at most 1).
    /// </summary>
    public const float AimedDegrees = 1;

    /// <summary>
    /// XP rate of the turret skill (<c>FUN_1408c7250</c>'s per-stat amount, x dt): 1.0; perception 3.0 and dexterity 0.5 as for the crossbow.
    /// </summary>
    public const float TurretXp = 1f;

    /// <summary>
    /// The range factor of a candidate target in the main pass and in the Shoot first pass (<c>FUN_1404374d0</c>: 1.0, 0.6).
    /// </summary>
    public const float FullRange = 1, ShootFirstRange = 0.6f;

    /// <summary>
    /// The smallest target score that counts (<c>FUN_1404374d0</c>).
    /// </summary>
    public const float MinScore = 1e-6f;

    /// <summary>
    /// The power factor of a shot (gun slot 19 <c>FUN_140435380</c>): 0.8 e + 0.2, e the building's efficiency (health x power grant, 0 unpowered;
    /// <c>FUN_140296e00</c>).
    /// </summary>
    public static float PowerFactor(float efficiency) => efficiency * 0.8f + 0.2f;

    /// <summary>
    /// The mount bonus (gun slot 18 <c>FUN_1404353b0</c>): 1 + 0.01 x the output value of the building the turret is mounted on (a wall's 10 to 30); 1 unmounted.
    /// </summary>
    public static float MountFactor(float mountOutput) => mountOutput * 0.01f + 1;

    /// <summary>
    /// The pierce ends of a turret of damage <paramref name="damage"/> (gun slots 22 and 23, <c>FUN_140435320</c>, <c>FUN_140435350</c>): trunc(0.75 D),
    /// trunc(1.25 D).
    /// </summary>
    public static (int Min, int Max) PierceEnds(float damage) => ((int)(damage * 0.75f), (int)(damage * 1.25f));

    /// <summary>
    /// The turning speed in radians per second (<c>TurretBuilding</c> slot 150 <c>FUN_140434d20</c>, called by the gunner every frame): efficiency x
    /// <c>aim speed</c> x (1 + 0.01 x the turret's own damage). A turret with no speed does not turn at all.
    /// </summary>
    public static float TurnRate(float efficiency, float aimSpeed, float damage) => efficiency * aimSpeed * (damage * 0.01f + 1);

    /// <summary>
    /// One harpoon (<c>FUN_14043a730</c>): damage U[min, max] x P x hM, range P x range x hM, speed shotSpeed x (0.8 P hM + 0.2), with P the
    /// <see cref="PowerFactor"/> and hM the height factor times the <see cref="MountFactor"/>. <paramref name="w"/> is the roll in [0, 1).
    /// </summary>
    public static (float Damage, float Range, float Speed) Harpoon(
        int pierceMin,
        int pierceMax,
        float range,
        float shotSpeed,
        float power,
        float heightTimesMount,
        float w) =>
        (
            (pierceMin + w * (pierceMax - pierceMin)) * power * heightTimesMount,
            power * range * heightTimesMount,
            shotSpeed * 0.8f * power * heightTimesMount + shotSpeed * 0.2f);

    /// <summary>
    /// A candidate's score before the turret's own weights (<c>FUN_140435d70</c>): (f x g)², g = 1 - d / (<paramref name="rangeFactor"/> x range),
    /// <paramref name="f"/> the posture factor (0.1 crippled, x0.1 not upright); a target out of that range scores 0.001 (it may still be chosen, never fired at).
    /// </summary>
    public static float Closeness(float distance, float range, float rangeFactor, float f)
    {
        float g = distance < 0 ? 0 : 1 - distance / (rangeFactor * range);
        if (g <= 0) g = 0;

        float s = f * g * f * g;

        return s <= 0 ? 0.001f : s;
    }

    /// <summary>
    /// The turret's own weight of a target (gun slot 6 <c>FUN_1404348e0</c>, <b>Observed</b>): 1 in front of it (positive dot product with its facing); behind,
    /// 0.1 for one outdoors or on a roof and for one inside the turret's own building on its floor (<paramref name="indoors"/> false), 0 for one inside elsewhere.
    /// </summary>
    public static float Arc(Vector3 facing, Vector3 toTarget, bool indoors = false) => Vector3.Dot(facing, toTarget) > 0 ? 1 : indoors ? 0 : 0.1f;
}

/// <summary>
/// Finding and using up a crossbow's ammunition in an inventory: the main inventory first, then the worn backpack (<c>CharacterAnimal</c> slot 47,
/// <c>FUN_1405cb5b0</c>).
/// </summary>
public static class Ammunition
{
    /// <summary>
    /// The first item of record <paramref name="ammoId"/>: in the character's own sections, then in the backpack's contents; null when there is none.
    /// </summary>
    public static ItemInstance? Find(Inventory? inventory, string ammoId)
    {
        if (inventory is null || ammoId.Length == 0) return null;

        foreach (var i in inventory.Items)
            if (i.Record == ammoId && i.Type != FcsRecordType.Container) return i;

        if (inventory.Backpack?.Contents is { } bag)
            foreach (var i in bag.Items)
                if (i.Record == ammoId) return i;

        return null;
    }

    /// <summary>
    /// Uses one charge (one bolt) of the first matching item (<c>FUN_140437ce0</c>: charges minus 1, the item removed at 0). A stack of several units refills
    /// from <see cref="ItemInstance.MaxCharges"/> and loses one unit instead (<b>engine</b>: how charges and stacks combine is <b>Unknown</b>). False when there
    /// is none.
    /// </summary>
    public static bool UseOne(Inventory? inventory, string ammoId)
    {
        if (Find(inventory, ammoId) is not { } item) return false;

        item.Charges -= 1;
        if (item.Charges > 0) return true;

        if (item.Quantity > 1)
        {
            item.Quantity--;
            item.Charges = item.MaxCharges > 0 ? item.MaxCharges : 1;
        }
        else
        {
            inventory!.RemoveOne(item);
        }

        return true;
    }

    /// <summary>
    /// The bolts left: the charges of every matching item and backpack item (stacks count their full units).
    /// </summary>
    public static int Count(Inventory? inventory, string ammoId)
    {
        if (inventory is null || ammoId.Length == 0) return 0;

        float n = 0;
        foreach (var i in inventory.All())
            if (i.Record == ammoId) n += i.Charges + (i.Quantity - 1) * MathF.Max(i.MaxCharges, 1);

        return (int)n;
    }
}
