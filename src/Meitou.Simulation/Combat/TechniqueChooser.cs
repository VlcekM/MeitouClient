using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Combat;

namespace Meitou.Simulation.Combat;

/// <summary>What the attacker-side choice reads (docs/game/combat.md "Attack technique choice").</summary>
public readonly record struct AttackQuery
{
    /// <summary>The weapon category in hand (<see cref="Fighter.TechniqueCategory"/>: 5 for fists, 9 to 20 for a creature's natural weapon).</summary>
    public int Category { get; init; }
    /// <summary>The level of that category's skill, as stored (no injury or gear term).</summary>
    public float Skill { get; init; }
    /// <summary>The unarmed skill of the fallback passes (<c>FUN_140884e00</c>): lerp(e, 0.2, 1) x (martial arts + the gear's unarmed bonus).</summary>
    public float UnarmedSkill { get; init; }
    /// <summary>A creature: one pass only, no unarmed fallback.</summary>
    public bool Animal { get; init; }
    /// <summary>Not standing (posture != 0): the <c>is prone</c> techniques only, and no kicks.</summary>
    public bool Prone { get; init; }
    /// <summary>The encumbrance factor e in [0, 1] (1 unburdened); a technique needs (1 - e) x 100 &lt;= <c>max encumbrance</c>.</summary>
    public float Encumbrance { get; init; }
    /// <summary>The weapon's <c>length</c> L (0 for fists).</summary>
    public float WeaponLength { get; init; }
    /// <summary>The character's size factor (slot 0x390: 1 for people, an animal's age scale).</summary>
    public float Scale { get; init; }
    /// <summary>The gap to the target (<see cref="CombatSystem.Gap"/>), floored at 0.</summary>
    public float Gap { get; init; }
    /// <summary>The fighter's last technique (index), -1 for none or a defence; it weighs x0.2 unless <see cref="TargetStaggered"/>.</summary>
    public int Previous { get; init; }
    public bool TargetStaggered { get; init; }
    /// <summary>The limbs: the left and the right arm work (<c>MedicalState.ArmWorks</c>); a leg is down.</summary>
    public bool LeftArm { get; init; }
    public bool RightArm { get; init; }
    public bool LegDown { get; init; }
}

/// <summary>
/// Picking a technique, pure given the rolls. The attack side is the original's (<c>FUN_140888030</c> / <c>FUN_1408876d0</c>, docs/game/combat.md "Attack technique choice"); the
/// defence side is <c>FUN_140887970</c>.
/// </summary>
public static class TechniqueChooser
{
    /// <summary>
    /// The attack technique to start, by weighted pick (<paramref name="roll"/> in [0, 1)); -1 when none. Up to three passes over the attack techniques (not disabled, not a block
    /// or dodge): the weapon's category and skill; then, for a person only, the unarmed techniques at <see cref="AttackQuery.UnarmedSkill"/>; then those again accepting hurt limbs.
    /// <paramref name="maxReach"/> is the longest reach (<c>attack distance</c> + L / 2, at least 1) among the techniques the filters let through, whatever the distance:
    /// the original's CharStats +0x150, which sets how close the fighter walks in.
    /// </summary>
    public static int ChooseAttack(IReadOnlyList<CombatTechnique> all, in AttackQuery q, float roll, out float maxReach)
    {
        maxReach = 1;
        Span<float> weights = all.Count <= 256 ? stackalloc float[all.Count] : new float[all.Count];
        float total = Pass(all, q, q.Category, q.Skill, false, weights, ref maxReach);
        if (total <= 0 && !q.Animal)
        {
            total = Pass(all, q, (int)WeaponCategory.Unarmed, q.UnarmedSkill, false, weights, ref maxReach);
            if (total <= 0) total = Pass(all, q, (int)WeaponCategory.Unarmed, q.UnarmedSkill, true, weights, ref maxReach);
        }
        if (total <= 0) return -1;
        float target = roll * total;
        int last = -1;
        for (int i = 0; i < all.Count; i++)
        {
            float w = weights[i];
            if (w <= 0) continue;
            last = i;
            if (target < w) return i;
            target -= w;
        }
        return last;
    }

    /// <summary>One pass of <c>FUN_1408876d0</c>: fills <paramref name="weights"/> and returns their sum.</summary>
    static float Pass(IReadOnlyList<CombatTechnique> all, in AttackQuery q, int category, float skill, bool hurtLimbs, Span<float> weights, ref float maxReach)
    {
        float total = 0;
        float half = q.WeaponLength * 0.5f;
        float burden = (1 - q.Encumbrance) * 100;
        for (int i = 0; i < all.Count; i++)
        {
            weights[i] = 0;
            var t = all[i];
            if (t.Disabled || t.IsBlock || t.IsDodge || !Fits(t, category)) continue;
            if (skill > t.MaxSkill || skill < t.MinSkill || burden > t.MaxEncumbrance || t.IsProne != q.Prone) continue;
            float limbs = LimbFactor(t, q);
            if (limbs <= 0 || !hurtLimbs && limbs < 1) continue;
            float reach = t.AttackDistance + half;
            maxReach = MathF.Max(maxReach, reach);
            if (q.Gap > reach) continue;
            // Closeness of the gap to the technique's own distance (attack distance + L / 4, times the size), both + 3: the smaller over the larger.
            float actual = q.Gap + 3, ideal = q.Scale * (t.AttackDistance + half * 0.5f) + 3;
            float w = (ideal <= actual ? ideal / actual : actual / ideal) * limbs;
            if (q.Gap < t.AttackDistanceMinVsStatic) w *= 0.02f;
            if (i == q.Previous && !q.TargetStaggered) w *= 0.2f;
            w *= t.Chance;
            if (w <= 0) continue;
            weights[i] = w;
            total += w;
        }
        return total;
    }

    /// <summary>
    /// The limb factor (<c>FUN_140886030</c>): the product over the blows of 1 for the weapon (limb 4), 0.001 for an arm (limb 0 left, 1 right) that does not work, x0.25 for a leg
    /// (2, 3) while a leg is down; a kick while prone rules the technique out (0), and so does a product with no blow above 0.01.
    /// </summary>
    static float LimbFactor(CombatTechnique t, in AttackQuery q)
    {
        float product = 1;
        bool any = false;
        for (int blow = 1; blow <= t.Blows; blow++)
        {
            float f = 1;
            switch (t.LimbOf(blow))
            {
                case 0: if (!q.LeftArm) f = 0.001f; break;
                case 1: if (!q.RightArm) f = 0.001f; break;
                case 2 or 3:
                    if (q.Prone) return 0;
                    if (q.LegDown) f = 0.25f;
                    break;
            }
            if (f > 0.01f) any = true;
            product *= f;
        }
        return any ? product : 0;
    }

    /// <summary>
    /// Whether a technique is for weapon category <paramref name="category"/> (the technique's per-category table, <c>FUN_1406684c0</c>, <b>Verified</b>): a creature technique
    /// (<c>animal</c> 9 and up) for that category only; any other by its flags, <c>katanas</c> 0, <c>sabre</c> 1, <c>blunt</c> 2, <c>heavy weapons</c> 3, <c>hackers</c> 4,
    /// <c>unarmed</c> 5, <c>bow</c> 6 and <c>polearm</c> 8 (the <c>1 handed</c> flag is not read; a category 7 or a creature one never fits).
    /// </summary>
    internal static bool Fits(CombatTechnique t, int category)
    {
        if (t.Animal >= FirstCreatureCategory) return t.Animal == category;
        var kind = category switch
        {
            (int)WeaponCategory.Katanas => WeaponKinds.Katana,
            (int)WeaponCategory.Sabres => WeaponKinds.Sabre,
            (int)WeaponCategory.Blunt => WeaponKinds.Blunt,
            (int)WeaponCategory.Heavy => WeaponKinds.Heavy,
            (int)WeaponCategory.Hackers => WeaponKinds.Hacker,
            (int)WeaponCategory.Unarmed => WeaponKinds.Unarmed,
            (int)WeaponCategory.Polearms => WeaponKinds.Polearm,
            _ => WeaponKinds.None,   // bow (6): no technique has the flag (CombatTechnique does not read it)
        };
        return (t.Kinds & kind) != 0;
    }

    /// <summary>The first creature attack category (9, ELEPHANT).</summary>
    public const int FirstCreatureCategory = 9;

    /// <summary>How far a technique strikes: the gap it may start at, <c>attack distance</c> + half the weapon's <c>length</c>.</summary>
    public static float ReachOf(CombatTechnique t, float weaponLength) => t.AttackDistance + weaponLength * 0.5f;

    /// <summary>The front directions (1 down, 2 left, 3 right, 4 thrust, 5 up, 6 pierced, 0 default) and the rear ones (7 to 9) are two classes; a wrong-direction block stays in the blow's class.</summary>
    internal static bool SameClass(int a, int b) => (a >= 7) == (b >= 7);

    /// <summary>
    /// The defender's reaction (<c>FUN_140887970</c>, <b>Observed</b>), given the block roll and the pick roll. Candidates are the block or dodge techniques of the defender's weapon category
    /// (<see cref="Fits"/>) and posture. A fighter with a weapon that can block rolls to block: on success the candidates are the block techniques that are not dodges (<b>Verified</b>,
    /// the block pool needs <c>is dodge</c> 0, so the "Dodge back" rows flagged as blocks too are out) whose direction equals the blow's, on failure those of a different direction in the same class,
    /// so the blow passes. A fighter without one is dodge-capable only when the roll against <paramref name="dodgeChancePercent"/> succeeds, and then picks among the dodge techniques. The pick is
    /// weighted on <c>chance</c>. -1 when there is no reaction.
    /// </summary>
    public static int ChooseReaction(IReadOnlyList<CombatTechnique> all, int category, bool prone, bool canBlock, int blowDirection,
        float blockChancePercent, float dodgeChancePercent, float chanceRoll, float pickRoll, bool stumbling = false)
    {
        ReactionPool pool;
        if (stumbling) pool = ReactionPool.StumbleDodges;   // the break-out already rolled (FUN_1406085e0)
        else if (canBlock) pool = chanceRoll * 100 < blockChancePercent ? ReactionPool.Facing : ReactionPool.WrongDirection;
        else if (chanceRoll * 100 < dodgeChancePercent) pool = ReactionPool.Dodges;
        else return -1;
        float total = 0;
        for (int i = 0; i < all.Count; i++) total += ReactionWeight(all[i], category, prone, pool, blowDirection);
        if (total <= 0) return -1;
        float target = pickRoll * total;
        int last = -1;
        for (int i = 0; i < all.Count; i++)
        {
            float w = ReactionWeight(all[i], category, prone, pool, blowDirection);
            if (w <= 0) continue;
            last = i;
            if (target < w) return i;
            target -= w;
        }
        return last;
    }

    /// <summary>Which techniques a reaction is picked from.</summary>
    enum ReactionPool
    {
        /// <summary>A block roll that succeeded: the block techniques whose direction is the blow's.</summary>
        Facing,
        /// <summary>A block roll that failed: block techniques of another direction in the blow's class, never a dodge.</summary>
        WrongDirection,
        /// <summary>A fighter without a blocking weapon that rolled a dodge: the dodge techniques.</summary>
        Dodges,
        /// <summary>A stumbling fighter breaking out: the <c>is stumble dodge</c> rows (<b>Verified</b>, <c>FUN_140887970</c>), whatever its weapon.</summary>
        StumbleDodges,
    }

    static float ReactionWeight(CombatTechnique t, int category, bool prone, ReactionPool pool, int blowDirection)
    {
        if (t.Disabled || t.IsStumbleDodge != (pool == ReactionPool.StumbleDodges) || !(t.IsBlock || t.IsDodge) || !Fits(t, category) || t.IsProne != prone) return 0;
        bool inPool = pool switch
        {
            ReactionPool.StumbleDodges => t.IsDodge,
            ReactionPool.Facing => t.IsBlock && !t.IsDodge && t.AttackDirection1 == blowDirection,
            ReactionPool.WrongDirection => t.IsBlock && !t.IsDodge && t.AttackDirection1 != blowDirection && SameClass(t.AttackDirection1, blowDirection),
            _ => t.IsDodge,
        };
        return inPool ? MathF.Max(t.Chance, 0) : 0;
    }
}