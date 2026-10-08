using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// A character's medical state: body parts, blood, wounds, hunger and the KO timer (docs/game/character-stats.md "Needs", "Blood and bleeding", "Body parts and
/// injuries", "First aid"). It belongs to one character and is stepped with <see cref="Tick"/> in game hours; the world runs characters in parallel, so
/// nothing here touches shared state. Randomness comes from the world's seeded rolls through the <see cref="MedicalContext"/>, with a per-character counter kept in the state.
/// <para>Time: <c>dt</c> is game hours (<b>Observed</b>; the <c>dt * 0.05</c> factors of the original are kept as written). One simulation tick of 1/30 s is
/// 11/36000 game hours. The KO timer counts game minutes (an engine choice read from the starvation code multiplying hours by 60).</para>
/// Field names follow the save keys of MEDICAL_STATE (docs/formats/save.md): <see cref="Blood"/> <c>blood</c>, <see cref="Bleeding"/> <c>bleeding</c>,
/// <see cref="Hunger"/> <c>hung</c>, <see cref="Fed"/> <c>fed</c>, <see cref="KoTimer"/> <c>KO</c>.
/// </summary>
public sealed partial class MedicalState
{
    /// <summary>Hunger starts and tops out here (the UI shows level x 100).</summary>
    public const float MaxHunger = 3;

    readonly List<float> wounds = [];
    ulong rollCounter;

    public MedicalState(IEnumerable<HealthPart> parts) => Parts = [.. parts];

    public IReadOnlyList<HealthPart> Parts { get; }

    /// <summary>Save key <c>blood</c>. Current blood; a character has capacity <see cref="BloodCapacity(RaceData, float, bool, float, float)"/>.</summary>
    public float Blood { get; set; }

    /// <summary>Save key <c>bleeding</c>: the current bleed rate (blood per game hour), recomputed each tick.</summary>
    public float Bleeding { get; private set; }

    /// <summary>Save key <c>hung</c>: the hunger level 0 to 3 (3 full; 2 and above well fed, below 1 starving).</summary>
    public float Hunger { get; set; } = MaxHunger;

    /// <summary>Save key <c>fed</c>: the stomach buffer, food waiting to be digested (<b>Unknown</b> in the save, inferred from the name).</summary>
    public float Fed { get; set; }

    /// <summary>Save key <c>KO</c>: game minutes of knock-out left (<b>Unknown</b> in the save, inferred from the name).</summary>
    public float KoTimer { get; set; }

    /// <summary>Save key <c>unconcious</c> (sic): knocked out.</summary>
    public bool Unconscious { get; private set; }

    /// <summary>Save key <c>dead</c>.</summary>
    public bool Dead { get; private set; }

    public DeathCause Cause { get; private set; }

    /// <summary>Set when blood reached 0 (<c>+0x163</c>, "unconscious from blood loss"); clears above 25.</summary>
    public bool BloodKo { get; private set; }

    /// <summary>Save key <c>coma</c>: knocked out with a critical part still down ("Recovery coma").</summary>
    public bool Coma => Unconscious && !VitalPartsUp();

    /// <summary>Save key <c>incapacitated</c>: <b>Unknown</b> meaning; here, knocked out or dead.</summary>
    public bool Incapacitated => Unconscious || Dead;

    /// <summary>The pain value P of the stat multiplier (1 = no penalty). Its source in the original is <b>Unknown</b>, so nothing drives it yet.</summary>
    public float Pain { get; set; } = 1;

    /// <summary>The untreated wounds' strengths, in creation order.</summary>
    public IReadOnlyList<float> Wounds => wounds;

    /// <summary>Game hours until a starving character may pass out again after waking (an engine choice; the original's timing was not traced).</summary>
    float starveGrace;
    bool starvationKo;

    // ------------------------------------------------------------------ creation

    /// <summary>
    /// A healthy new character: every part at its maximum, full blood, hunger 3. <paramref name="strength"/> sets the blood capacity.
    /// </summary>
    public static MedicalState Create(RaceData race, float strength, bool hasStats = true, float hpMultiplier = 1, float bodyScale = 1)
    {
        var parts = race.Anatomy.Select(a =>
        {
            var p = new HealthPart(a.Part, a.HitWeight, a.BaseHp)
            {
                HpMultiplier = hpMultiplier,
                BodyScale = bodyScale,
                SelfHealing = race.SelfHealing,
            };
            p.Flesh = p.MaxHp;
            return p;
        });
        var state = new MedicalState(parts);
        state.Blood = BloodCapacity(race, strength, hasStats);
        return state;
    }

    /// <summary>
    /// The blood capacity (<c>FUN_1406440a0</c>): <c>min blood</c> + (<c>max blood</c> - <c>min blood</c>) x s x k x z, s = strength / 100 for a character with a stats
    /// object (1 otherwise), k and z the two scale slots (1.0 for humans). So a human holds 75 at strength 0 and 150 at 100.
    /// </summary>
    public static float BloodCapacity(RaceData race, float strength, bool hasStats = true, float k = 1, float z = 1) =>
        race.MinBlood + (race.MaxBlood - race.MinBlood) * (hasStats ? strength / 100 : 1) * k * z;

    // ------------------------------------------------------------------ queries

    /// <summary>The aggregates of <c>FUN_140644e60</c>; <paramref name="exemptFromWear"/> gives the arm aggregate 1.0 (characters with the slot-0x248 object).</summary>
    public BodyAggregates Aggregates(bool exemptFromWear = false)
    {
        float arm = 0, head = 1, torso = float.MaxValue;
        bool anyArm = false, anyTorso = false, left = false, right = false;
        foreach (var p in Parts)
        {
            float f = Math.Clamp(p.Fraction, 0, 1);
            switch (p.Template.Type)
            {
                case BodyPartType.Arm:
                    anyArm = true;
                    arm = MathF.Max(arm, f);
                    if (p.Template.Collapses && p.IsDown)
                    {
                        if (p.Template.Side == BodySide.Left) left = true;
                        else if (p.Template.Side == BodySide.Right) right = true;
                    }
                    break;
                case BodyPartType.Head: head = f; break;
                case BodyPartType.Torso: anyTorso = true; torso = MathF.Min(torso, f); break;
            }
        }
        return new BodyAggregates(exemptFromWear || !anyArm ? 1 : arm, head, anyTorso ? torso : 1, left, right);
    }

    /// <summary>The stat multiplier in [0, 1] (<c>FUN_1406457e0</c>); see <see cref="StatMultiplier"/>.</summary>
    public float StatMultiplier(StatsEnumerated stat, StatUse use = StatUse.Hunger | StatUse.Limbs | StatUse.Pain | StatUse.DamageState, StatFactors? factors = null, bool exemptFromWear = false) =>
        Bodies.StatMultiplier.Compute(this, stat, use, factors ?? Bodies.StatFactors.None, Aggregates(exemptFromWear), Pain);

    /// <summary>The product of the fitted arm prosthetics' multipliers for a stat (1 with none).</summary>
    public float ArmItemFactor(StatsEnumerated stat) => ItemFactor(stat, legs: false);

    /// <summary>The product of the fitted leg prosthetics' multipliers for a stat (1 with none).</summary>
    public float LegItemFactor(StatsEnumerated stat) => ItemFactor(stat, legs: true);

    float ItemFactor(StatsEnumerated stat, bool legs)
    {
        float f = 1;
        foreach (var p in Parts)
        {
            bool isLeg = p.Template.Type == BodyPartType.Leg;
            bool isArm = p.Template.Type == BodyPartType.Arm;
            if (p.Limb == LimbState.Replaced && p.Replacement is { } r && (legs ? isLeg : isArm))
                f *= r.Multiplier(stat, p.ReplacementQuality);
        }
        return f;
    }

    bool VitalPartsUp()
    {
        foreach (var p in Parts)
            if (p.IsVital && !(p.Fraction > 0)) return false;
        return true;
    }

    /// <summary>The passing-out point of starvation (<c>FUN_140657620</c>): (100 - K x f) / 100, K = lerp(min, max toughness ko point, toughness / 100), f 1 with an empty stomach and 2 with food.</summary>
    public static float StarvationKoThreshold(GameConstants c, float toughness, bool foodInStomach)
    {
        float k = c.MinToughnessKoPoint + (c.MaxToughnessKoPoint - c.MinToughnessKoPoint) * Math.Clamp(toughness * 0.01f, 0, 1);
        return (100 - k * (foodInStomach ? 2 : 1)) / 100;
    }

    /// <summary>The negative HP at which a character goes into a coma (<c>FUN_140643ae0</c>): -lerp(min, max toughness ko point, toughness x 0.01); -10 at toughness 0 and -85 at 100 in the base game. Clamped to toughness 0 to 100 here.</summary>
    public static float KoPoint(GameConstants c, float toughness) =>
        -(c.MinToughnessKoPoint + (c.MaxToughnessKoPoint - c.MinToughnessKoPoint) * Math.Clamp(toughness * 0.01f, 0, 1));

    /// <summary>The wound degeneration multiplier lerp(<c>degeneration mult 1</c>, <c>degeneration mult 99</c>, toughness / 100): toughness 1 deteriorates about 57 times faster than 99.</summary>
    public static float DegenerationMult(GameConstants c, float toughness) =>
        c.DegenerationMult1 + (c.DegenerationMult99 - c.DegenerationMult1) * Math.Clamp(toughness * 0.01f, 0, 1);

    /// <summary>
    /// The untreated damage fraction D of a part (<c>FUN_14064f5c0</c>): clamp(((M - h - b) x 100 / M - 20) / (90 - 20), 0, 1), x0.25 if h + b &gt; 0. Damage under 20% of M does nothing; 90% or more is full speed.
    /// </summary>
    public static float UntreatedDamage(float maxHp, float flesh, float bandage)
    {
        if (maxHp <= 0) return 1;
        float d = Math.Clamp(((maxHp - flesh - bandage) * 100 / maxHp - 20) / (90 - 20), 0, 1);
        return flesh + bandage > 0 ? d * 0.25f : d;
    }

    /// <summary>The hunger-rate situation factor (<c>FUN_1408837a0</c>): resting, knocked out or dead <c>bed hunger rate</c>; using a machine 0.8; else 1 + (1 - e) x <c>encumbrance hunger rate</c>.</summary>
    public static float HungerSituationFactor(GameConstants c, bool resting, bool usingMachine, float encumbranceFactor) =>
        resting ? c.BedHungerRate : usingMachine ? 0.8f : 1 + (1 - encumbranceFactor) * c.EncumbranceHungerRate;

    // ------------------------------------------------------------------ KO time

    /// <summary>
    /// The terms of the KO time without the base (<c>FUN_1406447d0</c>): with k = lerp(<c>knockout mult 1</c>, <c>knockout mult 99</c>, toughness / 100), for each vital part
    /// (M - (h + s)) x k x 0.25 / M x 100; for each part with h - s &lt; 0, |h - s| x k x KO mult / M x 100; once |capacity - blood| x k x 0.5 / capacity x 100.
    /// </summary>
    public float KoTerms(in MedicalContext ctx)
    {
        var c = ctx.Constants;
        float k = c.KnockoutMult1 + (c.KnockoutMult99 - c.KnockoutMult1) * Math.Clamp(ctx.Toughness / 100, 0, 1);
        float sum = 0;
        foreach (var p in Parts)
        {
            float m = p.MaxHp;
            if (m <= 0) continue;
            if (p.IsVital) sum += (m - (p.Flesh + p.Stun)) * k * 0.25f / m * 100;
            if (p.Flesh - p.Stun < 0) sum += MathF.Abs(p.Flesh - p.Stun) * k * p.Template.KoMult / m * 100;
        }
        float cap = BloodCapacity(ctx.Race, ctx.Strength, ctx.HasStats);
        if (cap > 0) sum += MathF.Abs(cap - Blood) * k * 0.5f / cap * 100;
        return sum;
    }

    /// <summary>The KO time in game minutes: <c>knockout time base</c> (20) plus <see cref="KoTerms"/>.</summary>
    public float KoTime(in MedicalContext ctx) => ctx.Constants.KnockoutTimeBase + KoTerms(ctx);
}
