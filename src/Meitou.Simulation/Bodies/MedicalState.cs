using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// A character's medical state: body parts, blood, wounds, hunger and the KO timer (docs/game/character-stats.md "Needs", "Blood and bleeding", "Body parts and
/// injuries", "First aid"). It belongs to one character and is stepped with <see cref="Tick"/> in seconds of character time; the world runs characters in parallel, so
/// nothing here touches shared state. Randomness comes from the world's seeded rolls through the <see cref="MedicalContext"/>, with a per-character counter kept in the state.
/// <para>Time (<b>Verified</b>, docs/game/character-stats.md "Time"): <c>dt</c> is the character time, real seconds x game speed (the <c>dt * 0.05</c> factors of the
/// original are kept as written); hunger follows the game clock in hours. The KO timer counts the same seconds.</para>
/// Field names follow the save keys of MEDICAL_STATE (docs/formats/save.md): <see cref="Blood"/> <c>blood</c>, <see cref="Bleeding"/> <c>bleeding</c>,
/// <see cref="Hunger"/> <c>hung</c>, <see cref="Fed"/> <c>fed</c>, <see cref="KoTimer"/> <c>KO</c>.
/// </summary>
public sealed partial class MedicalState
{
    /// <summary>Hunger starts and tops out here (the UI shows level x 100).</summary>
    public const float MaxHunger = 3;

    readonly List<float> wounds = [];
    ulong rollCounter;

    public MedicalState(IEnumerable<HealthPart> parts) => this.parts = [.. parts];

    public IReadOnlyList<HealthPart> Parts => parts;
    readonly HealthPart[] parts;
    /// <summary><see cref="Parts"/> for the every-tick loops (no enumerator to allocate).</summary>
    internal ReadOnlySpan<HealthPart> PartSpan => parts;

    /// <summary>Save key <c>blood</c>. Current blood; a character has capacity <see cref="BloodCapacity(RaceData, float, bool, float, float)"/>.</summary>
    public float Blood { get; set; }

    /// <summary>Save key <c>bleeding</c>: the current bleed rate (blood per second of character time), recomputed each tick.</summary>
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

    /// <summary>
    /// Set when blood reached 0 (<c>+0x163 bloodlossTrauma</c>); clears above 25 (the original only on an update that found blood below capacity, docs/game/character-stats.md
    /// "Blood and KO"). While set, a conscious character goes down only under -25, and the HUD's Toughness XP shows "Blood loss trauma: +2x".
    /// </summary>
    public bool BloodKo { get; private set; }

    /// <summary>
    /// Save key <c>coma</c> (medical <c>+0x162</c>, "Recovery coma"): set when a vital part fell under the KO point, cleared once no vital part is under 0
    /// (<see cref="VitalPartsKeepDown"/>).
    /// </summary>
    public bool Coma { get; private set; }

    /// <summary>Knocked out or dead. Also written as save key <c>incapacitated</c>, which in the original is medical <c>+0x160</c> (a leg-collapse part down, <b>Observed</b>; not matched).</summary>
    public bool Incapacitated => Unconscious || Dead;

    /// <summary>
    /// The KO point (<see cref="KoPoint"/>) the legs' collapse test uses, from the owner's toughness at the last <see cref="Tick"/> (0 before the first). Derived, not saved: the first tick
    /// after a load sets it again.
    /// </summary>
    public float LegKoPoint { get => legKoPoint; private set { legKoPoint = value; legKoKnown = true; } }
    float legKoPoint;
    bool legKoKnown;

    /// <summary>
    /// The original's forced crawl (posture 2, <c>FUN_140649320</c>, <b>Observed</b>): a conscious character with a stats object (not an animal) any of whose legs is down by
    /// <see cref="HealthPart.LegDown"/>. One leg is enough; it moves at <see cref="Speed.CrawlSpeed"/> and plays the grounded clips. Read live, so a hit shows at once.
    /// </summary>
    public bool Crawling
    {
        get
        {
            // The original's flag (medical +0x160) is only set by the medical tick: before the first one (KO point not yet known) nobody is crippled, where a KO point of 0
            // would ground anyone with a leg merely below 0 (between 0 and the KO point a leg only limps).
            return !Incapacitated && LegsDown;
        }
    }

    /// <summary>A leg is down by <see cref="HealthPart.LegDown"/> whether or not the character is conscious (the original's flag +0x160 is set by the tick either way); needs the KO point known.</summary>
    bool LegsDown
    {
        get
        {
            if (!HasStatsObject || !legKoKnown) return false;
            foreach (var p in parts) if (p.IsLeg && p.LegDown(LegKoPoint)) return true;
            return false;
        }
    }

    /// <summary>
    /// Set by <see cref="Tick"/> on the update that finds a leg down where the last one did not (the original's first set of the crippled flag, which calls <c>FUN_1405c8330</c>:
    /// the character stops and its orders and tasks are cleared, docs/game/ragdolls.md "Forced crawl"); <see cref="BodySystem"/> takes it in a serial phase. The first tick after a
    /// load only learns the state. Not saved.
    /// </summary>
    public bool CollapseOnset { get; set; }
    int legsWereDown = -1;

    /// <summary>
    /// The planner's <c>myLegsAreMessedUp</c> (state 103, <c>FUN_1409980c0</c>, docs/game/ai-tasks.md "Planner states"): false when a leg is a stump (the limb-state table's legs, <c>FUN_14002a6e9</c>);
    /// else true when a leg is down by <see cref="HealthPart.LegDown"/> (<c>FUN_140644450</c>) and the KO point is under its maximum health less its stun, i.e. the leg could still be fixed.
    /// The original names its two leg parts (<c>+0x4d8</c>, <c>+0x4e0</c>); here they are the parts with a leg <c>collapse part</c>. Like <see cref="Crawling"/> it needs the KO point known.
    /// </summary>
    public bool LegsMessedUp
    {
        get
        {
            if (!legKoKnown) return false;
            foreach (var p in parts) if (p.IsLeg && p.Limb == LimbState.Stump) return false;
            foreach (var p in parts) if (p.IsLeg && p.LegDown(LegKoPoint) && LegKoPoint < p.MaxHp - p.Stun) return true;
            return false;
        }
    }

    /// <summary>
    /// Posture 0 (<c>PS_NORMAL</c>, docs/game/ragdolls.md "Forced crawl"): conscious, neither staying low nor crawling. Only a standing character loses a leg into a knock-out
    /// (<see cref="Sever"/>, <b>Verified</b>: the original tests the posture before it sets the knock-out). Read before a blow or a tick changes the legs.
    /// </summary>
    public bool Standing => !Unconscious && !StayingLow && !Crawling;

    /// <summary>
    /// The voluntary crawl (posture 1 <c>PS_STAYING_LOW</c>, docs/game/ragdolls.md "Staying low"): set on waking while sneaking, cleared by a speed order, sneak off, Stand Up or a melee
    /// attack. Kept on the medical state beside <see cref="Crawling"/> so <see cref="Prone"/> reads both; not saved (the original's save key, if any, is <b>Unknown</b>).
    /// </summary>
    public bool StayingLow { get; set; }

    /// <summary>
    /// Playing dead (posture 3 <c>PS_PLAYING_DEAD</c>, docs/game/ragdolls.md "Playing dead"): the knockout is over but the character lies on, still <see cref="Unconscious"/> to everyone,
    /// until it is safe to get up (<see cref="MedicalContext.PlayDead"/>) or <see cref="StandUp"/>. Not saved: after a load the next tick decides again.
    /// </summary>
    public bool PlayingDead { get; private set; }

    /// <summary>Knocked out with nothing but the KO timer keeping it down (vital parts and blood allow waking): whether it may wake within <paramref name="dt"/> seconds.</summary>
    public bool WakesWithin(float dt) => Unconscious && !Dead && KoTimer <= dt && !VitalPartsKeepDown(LegKoPoint) && Blood >= 0;

    /// <summary>How <see cref="StandUp"/> ended playing dead (staying low or not) for the get-up to take, instead of the sneak order; null when it did not.</summary>
    public bool? WokenLow { get; set; }

    /// <summary>Posture 0 (or 1 with <paramref name="stayLow"/>): ends staying low, and playing dead at once.</summary>
    public void StandUp(bool stayLow = false)
    {
        StayingLow = stayLow;
        if (!PlayingDead) return;
        PlayingDead = false;
        Unconscious = false;
        KoTimer = 0;
        WokenLow = stayLow;
    }

    /// <summary>Posture not 0 while conscious: <see cref="Crawling"/> or <see cref="StayingLow"/>. What the grounded clips, the prone techniques and the stealth x2 read.</summary>
    public bool Prone => !Incapacitated && HasStatsObject && (StayingLow || Crawling);

    /// <summary>
    /// The character's light level P that the stat multiplier's light term reads (<c>FUN_1406457e0</c> reads Character <c>+0xd8</c>, the light level of docs/game/perception.md
    /// "Sight", <b>Verified</b>): 1 in daylight, 0 in the dark. <see cref="BodySystem"/> copies it from <see cref="SightMap.Light"/> every tick; derived, not saved.
    /// </summary>
    public float Light { get; set; } = 1;

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
        // In the original's part order (BodyPartTemplate.MedicalOrder; parts of one place keep the race's order, which gives vanilla's stomach before chest).
        var parts = race.Anatomy.OrderBy(a => a.Part.MedicalOrder).Select(a =>
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
        var state = new MedicalState(parts) { HasStatsObject = hasStats, ScaleK = hpMultiplier, ScaleZ = bodyScale };
        state.Blood = BloodCapacity(race, strength, hasStats, hpMultiplier, bodyScale);
        return state;
    }

    /// <summary>
    /// False for an animal: the character's slot 0x250 (the humanoid stats object) is null for a <c>CharacterAnimal</c> (<b>Observed</b>, docs/game/animals.md), so the blood
    /// capacity takes s = 1 and the hit path cannot sever limbs. Set at creation; ANDed with <see cref="MedicalContext.HasStats"/>.
    /// </summary>
    public bool HasStatsObject { get; private set; } = true;
    /// <summary>The character's slot-0x390 factor k (1 for humans; an animal's size, lerp(age, scale min, scale max)): the parts' HP multiplier, the blood capacity and the hunger rate.</summary>
    public float ScaleK { get; private set; } = 1;
    /// <summary>The character's slot-0x238 factor z (1 for humans; an animal's <c>HP mult</c>): the parts' body scale and the blood capacity.</summary>
    public float ScaleZ { get; private set; } = 1;

    /// <summary>
    /// Makes this an animal's body (docs/game/animals.md "Body"): no humanoid stats object, size <paramref name="size"/> (slot 0x390) as the parts' HP multiplier and
    /// <paramref name="hpMult"/> (slot 0x238) as their body scale. A save holds neither, so a loaded animal gets them here; the ageing calls it with the new size every
    /// tick, as the original reads slot 0x390 into the parts' multiplier every tick.
    /// </summary>
    public void SetAnimalBody(float size, float hpMult)
    {
        (HasStatsObject, ScaleK, ScaleZ) = (false, size, hpMult);
        foreach (var p in parts) (p.HpMultiplier, p.BodyScale) = (size, hpMult);
    }

    /// <summary>This character's blood capacity under <paramref name="ctx"/> (its stats object and scale factors applied).</summary>
    public float Capacity(in MedicalContext ctx) => BloodCapacity(ctx.Race, ctx.Strength, ctx.HasStats && HasStatsObject, ScaleK, ScaleZ);

    /// <summary>
    /// The blood capacity (<c>FUN_1406440a0</c>): <c>min blood</c> + (<c>max blood</c> - <c>min blood</c>) x s x k x z, s = strength / 100 for a character with a stats
    /// object (1 otherwise), k and z the two scale slots (1.0 for humans). So a human holds 75 at strength 0 and 150 at 100.
    /// </summary>
    public static float BloodCapacity(RaceData race, float strength, bool hasStats = true, float k = 1, float z = 1) =>
        race.MinBlood + (race.MaxBlood - race.MinBlood) * (hasStats ? strength / 100 : 1) * k * z;

    // ------------------------------------------------------------------ queries

    /// <summary>
    /// Whether an arm works (the medical object's arm bytes, set every tick by <c>FUN_140649320</c> through <c>FUN_140644450</c>, <b>Observed</b>; docs/game/combat.md "Arms"):
    /// no part whose <c>collapse part</c> has the arm's bit (4 left, 2 right) is a stump or crushed, or collapses and is down (flesh - stun + rig &lt; 0). A fitted prosthetic works.
    /// </summary>
    public bool ArmWorks(bool left)
    {
        int bit = left ? 4 : 2;
        foreach (var p in parts)
        {
            if ((p.Template.CollapsePart & bit) == 0) continue;
            if (p.Limb is LimbState.Stump or LimbState.Crushed) return false;
            if (p.Template.Collapses && p.IsDown) return false;
        }
        return true;
    }

    /// <summary>Both arms work (<see cref="ArmWorks"/>): the planner's <c>hasTwoWorkingArm</c>, which a crossbow, a heavy weapon or a polearm needs.</summary>
    public bool BothArmsWork => ArmWorks(left: true) && ArmWorks(left: false);

    /// <summary>The aggregates of <c>FUN_140644e60</c>; <paramref name="exemptFromWear"/> gives the arm aggregate 1.0 (characters with the slot-0x248 object).</summary>
    public BodyAggregates Aggregates(bool exemptFromWear = false) => exemptFromWear ? Summary.Aggregates with { Arm = 1 } : Summary.Aggregates;

    /// <summary>The lowest part fraction (1 for a body without parts), the way <c>Enumerable.Min</c> reads floats: a NaN wins.</summary>
    public float WorstPart => Summary.Worst;

    /// <summary>The blood the first 8 parts show, a byte each (<see cref="BloodAmounts(MedicalState)"/>).</summary>
    public ulong ShownBlood => Summary.Blood;

    // The part figures, kept while no part changed (HealthPart.Version only grows, so the sum moves with every change). One reference, swapped whole: the publish reads
    // it from several threads.
    sealed record PartSummary(int Version, BodyAggregates Aggregates, float Worst, ulong Blood);
    PartSummary? summary;

    PartSummary Summary
    {
        get
        {
            int version = 0;
            foreach (var p in parts) version += p.Version;
            var s = summary;
            if (s is not null && s.Version == version) return s;
            return summary = new(version, ComputeAggregates(), ComputeWorstPart(), BloodAmounts(this));
        }
    }

    float ComputeWorstPart()
    {
        if (parts.Length == 0) return 1;
        float worst = parts[0].Fraction;
        if (float.IsNaN(worst)) return worst;
        for (int k = 1; k < parts.Length; k++)
        {
            float f = parts[k].Fraction;
            if (f < worst) worst = f;
            else if (float.IsNaN(f)) return f;
        }
        return worst;
    }

    BodyAggregates ComputeAggregates()
    {
        float arm = 0, head = 1, torso = float.MaxValue;
        bool anyArm = false, anyTorso = false, left = false, right = false;
        foreach (var p in parts)
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
        return new BodyAggregates(!anyArm ? 1 : arm, head, anyTorso ? torso : 1, left, right);
    }

    /// <summary>The stat multiplier in [0, 1] (<c>FUN_1406457e0</c>); see <see cref="StatMultiplier"/>.</summary>
    public float StatMultiplier(StatsEnumerated stat, StatUse use = StatUse.Hunger | StatUse.Limbs | StatUse.Light | StatUse.DamageState | StatUse.Stun, StatFactors? factors = null, bool exemptFromWear = false) =>
        Bodies.StatMultiplier.Compute(this, stat, use, factors ?? (Bodies.StatFactors.None with { Stun = DustStatFactor }), Aggregates(exemptFromWear), Light);

    /// <summary>
    /// The body term of stealth (<c>FUN_140644da0</c>, medical <c>+0xb4</c>): the lowest health fraction over the torso and leg parts (1 with none) plus
    /// <paramref name="toughness"/> x 0.01, clamped to [0, 1]. Stealth has no hunger or arm term (docs/game/character-stats.md "Effective stealth").
    /// </summary>
    public float StealthBodyFactor(float toughness)
    {
        float f = 1;
        foreach (var p in parts)
            if (p.Template.Type is BodyPartType.Torso or BodyPartType.Leg) f = MathF.Min(f, p.Fraction);
        return Math.Clamp(f + toughness * 0.01f, 0, 1);
    }

    /// <summary>The product of the fitted arm prosthetics' multipliers for a stat (1 with none).</summary>
    public float ArmItemFactor(StatsEnumerated stat) => ItemFactor(stat, legs: false);

    /// <summary>The product of the fitted leg prosthetics' multipliers for a stat (1 with none).</summary>
    public float LegItemFactor(StatsEnumerated stat) => ItemFactor(stat, legs: true);

    /// <summary>A prosthetic is fitted to an arm or a leg (<c>FUN_140883690</c>: one of the four limb slots holds an item).</summary>
    public bool HasProsthetics
    {
        get
        {
            foreach (var p in parts)
                if (p.Limb == LimbState.Replaced && p.Replacement is not null && p.Template.Type is BodyPartType.Arm or BodyPartType.Leg) return true;
            return false;
        }
    }

    float ItemFactor(StatsEnumerated stat, bool legs)
    {
        float f = 1;
        foreach (var p in parts)
        {
            bool isLeg = p.Template.Type == BodyPartType.Leg;
            bool isArm = p.Template.Type == BodyPartType.Arm;
            if (p.Limb == LimbState.Replaced && p.Replacement is { } r && (legs ? isLeg : isArm))
                f *= r.Multiplier(stat, p.ReplacementQuality);
        }
        return f;
    }

    /// <summary>
    /// Whether a vital part keeps the character out (<c>reassessCollapseMode</c> through <c>getCollapseStage</c>, docs/game/character-stats.md "Blood and KO"): a vital part with
    /// h - s + extra under <paramref name="koPoint"/> sets <see cref="Coma"/>; while it is set any vital part under 0 keeps the character out, and it clears once none does.
    /// A vital part between the KO point and 0 does not keep a character out without it.
    /// </summary>
    bool VitalPartsKeepDown(float koPoint)
    {
        bool down = false;
        foreach (var p in parts)
        {
            if (!p.IsVital) continue;
            float v = p.Flesh - p.Stun + p.Rig;
            if (v < koPoint) Coma = down = true;
            else if (v < 0 && Coma) down = true;
        }
        if (!down) Coma = false;
        return down;
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

    /// <summary>
    /// The hunger-rate situation factor (<c>FUN_1408837a0</c>): resting, knocked out or dead <c>bed hunger rate</c>; the AI flag (<paramref name="usingMachine"/>) 0.8; idle on a machine or chair its
    /// functionality's <c>hunger rate</c>; else 1 + (1 - e) x <c>encumbrance hunger rate</c> (the original's rule moving or fighting; Meitou's idle without a machine too).
    /// </summary>
    public static float HungerSituationFactor(GameConstants c, bool resting, bool usingMachine, float encumbranceFactor, float? useRate = null) =>
        resting ? c.BedHungerRate : usingMachine ? 0.8f : useRate ?? 1 + (1 - encumbranceFactor) * c.EncumbranceHungerRate;

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
        foreach (var p in parts)
        {
            float m = p.MaxHp;
            if (m <= 0) continue;
            if (p.IsVital) sum += (m - (p.Flesh + p.Stun)) * k * 0.25f / m * 100;
            if (p.Flesh - p.Stun < 0) sum += MathF.Abs(p.Flesh - p.Stun) * k * p.Template.KoMult / m * 100;
        }
        float cap = Capacity(ctx);
        if (cap > 0) sum += MathF.Abs(cap - Blood) * k * 0.5f / cap * 100;
        return sum;
    }

    /// <summary>The KO time in seconds of character time: <c>knockout time base</c> (20) plus <see cref="KoTerms"/>.</summary>
    public float KoTime(in MedicalContext ctx) => ctx.Constants.KnockoutTimeBase + KoTerms(ctx);
}
