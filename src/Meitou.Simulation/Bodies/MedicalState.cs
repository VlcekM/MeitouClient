using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

public enum MedicalEventKind
{
    KnockedOut,
    WokeUp,
    /// <summary>A part went below zero (<see cref="MedicalEvent.Part"/>); a vital one has already knocked the character out.</summary>
    PartDown,
    PartSevered,
    Died,
}

public enum DeathCause
{
    None,
    /// <summary>A vital part destroyed.</summary>
    VitalPart,
    /// <summary>Blood at or below minus the capacity.</summary>
    BloodLoss,
    /// <summary>Hunger at 0.</summary>
    Starvation,
}

/// <summary>Something that happened to a character in a tick or a hit, for the caller to act on (XP, drops, messages, the commit phase).</summary>
public readonly record struct MedicalEvent(MedicalEventKind Kind, int Part = -1, DeathCause Cause = DeathCause.None);

/// <summary>A blow as the medical system takes it, after armour and toughness (docs/game/combat.md "The hit pipeline", step 5).</summary>
/// <param name="Cut">Cut damage.</param>
/// <param name="Blunt">Blunt damage; with <c>blunt permanent organ damage</c> 0 it only stuns.</param>
/// <param name="Pierce">Pierce damage.</param>
/// <param name="ExtraStun">Stun added on top.</param>
/// <param name="Scale">The hit scale of the blood-loss and wound terms.</param>
public readonly record struct HitDamage(float Cut, float Blunt, float Pierce, float ExtraStun = 0, float Scale = 1);

/// <summary>
/// A character's medical state: body parts, blood, wounds, hunger and the KO timer (docs/game/character-stats.md "Needs", "Blood and bleeding", "Body parts and
/// injuries", "First aid"). It belongs to one character and is stepped with <see cref="Tick"/> in game hours; the world runs characters in parallel, so
/// nothing here touches shared state. Randomness comes from the world's seeded rolls through the <see cref="MedicalContext"/>, with a per-character counter kept in the state.
/// <para>Time: <c>dt</c> is game hours (<b>Observed</b>; the <c>dt * 0.05</c> factors of the original are kept as written). One simulation tick of 1/30 s is
/// 11/36000 game hours. The KO timer counts game minutes (an engine choice read from the starvation code multiplying hours by 60).</para>
/// Field names follow the save keys of MEDICAL_STATE (docs/formats/save.md): <see cref="Blood"/> <c>blood</c>, <see cref="Bleeding"/> <c>bleeding</c>,
/// <see cref="Hunger"/> <c>hung</c>, <see cref="Fed"/> <c>fed</c>, <see cref="KoTimer"/> <c>KO</c>.
/// </summary>
public sealed class MedicalState
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

    // ------------------------------------------------------------------ hits

    float NextRoll(in MedicalContext ctx) => BodyRolls.Float(ctx.Seed, ctx.CharacterKey, BodyRolls.MedicalDomain, rollCounter++);

    /// <summary>
    /// Chooses the part a blow lands on (<c>FUN_1406508d0</c>, <b>Observed</b>): among the parts not yet destroyed, weighted by hit weight times <see cref="HealthPart.HitMult"/>
    /// (the weight alone for hit direction 6); a low strike excludes arms and head. Draws one roll from the character's stream. Returns -1 when no part qualifies.
    /// </summary>
    public int ChoosePart(in MedicalContext ctx, bool lowStrike = false, bool directionSix = false)
    {
        float total = 0;
        for (int i = 0; i < Parts.Count; i++) total += Weight(Parts[i], lowStrike, directionSix);
        if (total <= 0) return -1;
        float target = NextRoll(ctx) * total;
        int last = -1;
        for (int i = 0; i < Parts.Count; i++)
        {
            float w = Weight(Parts[i], lowStrike, directionSix);
            if (w <= 0) continue;
            last = i;
            if (target < w) return i;
            target -= w;
        }
        return last;
    }

    static float Weight(HealthPart p, bool lowStrike, bool directionSix)
    {
        if (p.IsDestroyed) return 0;
        if (lowStrike && p.Template.Type is BodyPartType.Arm or BodyPartType.Head) return 0;
        return p.HitWeight * (directionSix ? 1 : p.HitMult);
    }

    /// <summary>
    /// Applies a blow to a part (<c>FUN_14064f300</c>): h falls by cut + pierce + blunt x <c>blunt permanent organ damage</c>; s rises by the rest of the blunt plus the extra
    /// stun; blood falls by (cut + pierce) x <c>immediate blood loss</c> x scale x race bleed rate; robots and prosthetics gain wear; a cut opens a wound. Then the
    /// part is checked: down (a vital one knocks the character out), destroyed (vital: death; other: severed, subject to the Dismemberment setting and to the owner having a stats object).
    /// The part's <see cref="HealthPart.HitMult"/> is raised by 1 per hit (to 4; held to 2 when its fraction is negative, 0 when destroyed).
    /// </summary>
    public void ApplyHit(int part, in HitDamage hit, in MedicalContext ctx, List<MedicalEvent>? events = null)
    {
        if (Dead) return;
        var c = ctx.Constants;
        var race = ctx.Race;
        var p = Parts[part];
        float perm = c.BluntPermanentOrganDamage;
        p.Flesh -= hit.Cut + hit.Pierce + hit.Blunt * perm;
        p.Stun += (1 - perm) * hit.Blunt + hit.ExtraStun;
        Blood -= (hit.Cut + hit.Pierce) * c.ImmediateBloodLoss * hit.Scale * race.BleedRate;
        if (p.IsRobotic(race) && !ctx.ExemptFromWear)
            p.Wear += (hit.Cut + hit.Pierce + hit.Blunt) * 0.04f * c.RobotWearRate;
        // How a cut turns into a wound strength is not traced (the editor text of `bleed rate` is "cutDamage * this * TIME"): strength = cut x bleed rate x scale.
        if (hit.Cut > 0 && race.BleedRate > 0) wounds.Add(hit.Cut * c.BleedRate * hit.Scale);

        p.HitMult = p.IsDestroyed ? 0 : p.Fraction < 0 ? MathF.Min(p.HitMult + 1, 2) : MathF.Min(p.HitMult + 1, 4);

        CheckPart(part, ctx, events, inHitPath: true);
        CheckBlood(ctx, events);
    }

    void CheckPart(int index, in MedicalContext ctx, List<MedicalEvent>? events, bool inHitPath)
    {
        if (Dead) return;
        var p = Parts[index];
        bool down = p.IsDown;
        if (down && !p.WasDown)
        {
            events?.Add(new MedicalEvent(MedicalEventKind.PartDown, index));
            if (p.IsVital) StartKo(ctx, events);
        }
        p.WasDown = down;
        bool destroyed = inHitPath ? p.IsDestroyed : p.Flesh - p.Stun < -p.MaxHp;
        if (!destroyed) return;
        if (p.IsVital) { Die(DeathCause.VitalPart, events); return; }
        var setting = ctx.Options.Dismemberment;
        bool allowed = inHitPath ? setting >= Dismemberment.Rare && ctx.HasStats : setting >= Dismemberment.Frequent;
        if (allowed && p.Limb == LimbState.Original && p.Template.Severance) Sever(index, ctx, events);
    }

    /// <summary>
    /// Cuts a limb off (<c>FUN_14064edc0</c>): the part becomes a stump (base HP 5), the character loses rand(5, 30) blood and gets a wound of strength <c>bleed rate</c> x 200;
    /// a lost leg also knocks the character out a second time. The caller awards the toughness XP (<see cref="XpService.ToughnessFromLimbLoss"/>) and spawns the
    /// severed limb item from the race's <c>severed limbs</c>.
    /// </summary>
    public void Sever(int part, in MedicalContext ctx, List<MedicalEvent>? events = null)
    {
        var p = Parts[part];
        p.Limb = LimbState.Stump;
        p.Replacement = null;
        Blood -= 5 + 25 * NextRoll(ctx);
        wounds.Add(ctx.Constants.BleedRate * 200);
        events?.Add(new MedicalEvent(MedicalEventKind.PartSevered, part));
        if (p.Template.LimbSlot is 2 or 3)
        {
            // The second KO after losing a leg (FUN_140644980): (2 x base + terms) x (1 - toughness / 100) x 4, at least 3, times a random number in [0, 1].
            float t = (2 * ctx.Constants.KnockoutTimeBase + KoTerms(ctx)) * (1 - Math.Clamp(ctx.Toughness / 100, 0, 1)) * 4;
            ApplyKo(MathF.Max(t, 3) * NextRoll(ctx), events);
        }
    }

    /// <summary>Fits a prosthetic on the part of its slot (a stump, usually): the part is healthy again at the new maximum, without stun, bandage or wear.</summary>
    public bool FitReplacement(LimbReplacement limb, float quality)
    {
        foreach (var p in Parts)
            if (p.Template.LimbSlot == limb.LimbSlot)
            {
                p.Limb = LimbState.Replaced;
                p.Replacement = limb;
                p.ReplacementQuality = Math.Clamp(quality, 0, 1);
                p.Wear = 0;
                p.Stun = 0;
                p.Bandage = 0;
                p.Rig = 0;
                p.Flesh = p.MaxHp;
                p.WasDown = false;
                return true;
            }
        return false;
    }

    // ------------------------------------------------------------------ KO and death

    void StartKo(in MedicalContext ctx, List<MedicalEvent>? events) => ApplyKo(KoTime(ctx), events);

    /// <summary>Knocks the character out for <paramref name="minutes"/> game minutes from outside the medical rules (a drug, a scripted event); a longer running KO is kept.</summary>
    public void KnockOut(float minutes, List<MedicalEvent>? events = null) => ApplyKo(minutes, events);

    void ApplyKo(float minutes, List<MedicalEvent>? events)
    {
        if (Dead) return;
        if (!Unconscious)
        {
            Unconscious = true;
            KoTimer = minutes;
            events?.Add(new MedicalEvent(MedicalEventKind.KnockedOut));
        }
        else KoTimer = MathF.Max(KoTimer, minutes);
    }

    void Die(DeathCause cause, List<MedicalEvent>? events)
    {
        if (Dead) return;
        Dead = true;
        Cause = cause;
        events?.Add(new MedicalEvent(MedicalEventKind.Died, Cause: cause));
    }

    /// <summary>Blood rules shared by a hit and the tick: blood at 0 (or -25 once flagged) starts a KO, at -capacity the character dies, above 25 the flag clears.</summary>
    void CheckBlood(in MedicalContext ctx, List<MedicalEvent>? events)
    {
        if (Dead) return;
        if (Blood > 25) BloodKo = false;
        float cap = BloodCapacity(ctx.Race, ctx.Strength, ctx.HasStats);
        if (Blood <= -cap) { Die(DeathCause.BloodLoss, events); return; }
        if (Blood <= 0 && !BloodKo) { BloodKo = true; StartKo(ctx, events); }
        else if (BloodKo && Blood <= -25 && !Unconscious) StartKo(ctx, events);
    }

    // ------------------------------------------------------------------ food

    /// <summary>Puts food into the stomach buffer (hunger points; the nutrition of items is <b>Unknown</b>). The buffer digests into the hunger level in <see cref="Tick"/>.</summary>
    public void Feed(float amount) => Fed += MathF.Max(0, amount);

    // ------------------------------------------------------------------ first aid

    /// <summary>
    /// The bandaging rate of a treatment (<c>FUN_140649a00</c>): lerp(1, 15, min(skill, kit quality) x 0.01) x <c>medic speed mult</c>, x <c>robot medic speed mult</c> for a robot kit.
    /// </summary>
    public static float TreatmentRate(GameConstants c, float skill, float kitQuality, bool robotKit)
    {
        float t = Math.Clamp(MathF.Min(skill, kitQuality) * 0.01f, 0, 1);
        return (1 + 14 * t) * c.MedicSpeedMult * (robotKit ? c.RobotMedicSpeedMult : 1);
    }

    /// <summary>
    /// Treats the patient for <paramref name="dt"/>: each matching part's bandaged pool rises by the rate x dt, capped so h + b stays at most M. An ordinary kit treats only
    /// organic parts, a robot kit only robotic ones. The unit of <c>dt</c> for treatment is <b>Unknown</b> (taken as game hours here). Returns the total bandaged.
    /// </summary>
    public float Treat(float dt, float skill, float kitQuality, bool robotKit, in MedicalContext ctx)
    {
        if (Dead) return 0;
        float rate = TreatmentRate(ctx.Constants, skill, kitQuality, robotKit) * dt;
        float total = 0;
        foreach (var p in Parts)
        {
            if (p.IsRobotic(ctx.Race) != robotKit) continue;
            float room = p.MaxHp - p.Flesh - p.Bandage;
            float add = Math.Clamp(rate, 0, MathF.Max(room, 0));
            p.Bandage += add;
            total += add;
        }
        return total;
    }

    /// <summary>How much of a kit a treatment uses (<c>FUN_140644250</c>): lerp(<c>medkit drain 99</c>, <c>medkit drain 1</c>, (100 - skill) / 100) x dt, x <c>robot medic speed mult</c> for robot kits. An expert uses a kit 20 times slower than a beginner.</summary>
    public static float KitDrain(GameConstants c, float skill, float dt, bool robotKit)
    {
        float t = Math.Clamp((100 - skill) / 100, 0, 1);
        return (c.MedkitDrain99 + (c.MedkitDrain1 - c.MedkitDrain99) * t) * dt * (robotKit ? c.RobotMedicSpeedMult : 1);
    }

    // ------------------------------------------------------------------ the tick

    /// <summary>
    /// Steps the character by <paramref name="dt"/> game hours (<c>FUN_140652000</c> and what it calls): every part degenerates, heals, recovers from stun and wear; wounds clot and
    /// blood runs out or recovers; hunger falls or is refilled from the stomach; the KO timer counts down; blood loss, starvation and destroyed vital parts kill. Events go to
    /// <paramref name="events"/> if given. A dead character is not stepped.
    /// </summary>
    public void Tick(float dt, in MedicalContext ctx, List<MedicalEvent>? events = null)
    {
        if (Dead || dt <= 0) return;
        var c = ctx.Constants;
        var race = ctx.Race;
        float chance = ctx.Options.ChanceOfDeath;

        // Part healing rates. Bed-like objects (kinds 6 and 0x19) force degeneration to 0 and scale the organic or the robotic rate.
        float degMult = ctx.Bed != BedKind.None ? 0 : DegenerationMult(c, ctx.Toughness);
        float organic = race.HealRate * (ctx.Bed == BedKind.Bed ? ctx.BedFactor : 1) * ctx.HealMult;
        float robotic = race.HealRate * (ctx.Bed == BedKind.RepairBed ? ctx.BedFactor : 1) * ctx.HealMult;
        bool conscious = !Unconscious;
        float dtb = dt * ctx.BodyTimeScale;

        for (int i = 0; i < Parts.Count && !Dead; i++)
        {
            var p = Parts[i];
            float m = p.MaxHp;
            if (m <= 0.01f) m = 0.01f;
            bool isRobotic = p.IsRobotic(race);
            float rate = isRobotic ? robotic : organic;
            if (p.Limb == LimbState.Stump && !isRobotic) rate *= 2;       // a stump heals at twice the rate
            float r = m / 100 * rate;
            float d = UntreatedDamage(m, p.Flesh, p.Bandage);

            // Degeneration of untreated damage; none for non-vital robotic parts.
            if (p.Flesh > -3 * m)
            {
                float dm = isRobotic && !p.IsVital ? 0 : degMult;
                p.Flesh -= dtb * 0.05f * c.BodypartDegenerationRate * chance * dm * d * 1.2f;
            }
            // Healing of treated damage.
            if (p.Bandage > 0 && p.Flesh < m)
            {
                float heal = MathF.Min(p.Bandage, dtb * 0.05f * c.HealRateMult * r);
                p.Bandage -= heal;
                p.Flesh = MathF.Min(p.Flesh + heal, m);
            }
            // Self healing: untreated damage becomes treated at 0.4 of the heal speed.
            if (p.SelfHealing && conscious && p.Flesh - p.Stun < m - p.Bandage)
                p.Bandage = MathF.Min(p.Bandage + dtb * 0.05f * c.HealRateMult * r * 0.4f, MathF.Max(m - p.Flesh, 0));
            // Stun recovers; the extra buffer is held under 50 - (h - s).
            if (p.Stun > 0) p.Stun = MathF.Max(0, p.Stun - dtb * 0.05f * c.StunRecoveryRate * c.HealRateMult * r);
            if (p.Rig > 0) p.Rig = MathF.Max(0, MathF.Min(p.Rig, 50 - (p.Flesh - p.Stun)));
            // Wear repair at a repair object: wear moves into the bandaged pool.
            if (p.Wear > 0 && ctx.RepairRate > 0)
            {
                float amount = MathF.Min(p.Wear, dtb * 0.05f * c.RestingHealRateMult * ctx.RepairRate);
                p.Wear -= amount;
                p.Bandage += amount;
            }
            // The hit-focus multiplier decays towards 1 while the flesh is not negative, and is reset once it is.
            if (p.Flesh < 0) p.HitMult = 1;
            else if (p.HitMult > 1) p.HitMult = MathF.Max(1, p.HitMult - 0.01f * dtb);

            CheckPart(i, ctx, events, inHitPath: false);
        }
        if (Dead) return;

        TickBlood(dtb, ctx);
        TickHunger(dt, ctx);
        CheckBlood(ctx, events);
        if (Dead) return;

        if (Hunger <= 0) { Die(DeathCause.Starvation, events); return; }

        // Starvation passes the character out below a toughness-dependent point; after waking, a grace period keeps it from happening at once.
        starveGrace = MathF.Max(0, starveGrace - dt);
        if (!Unconscious && starveGrace <= 0 && Hunger < StarvationKoThreshold(c, ctx.Toughness, Fed > 0))
        {
            starvationKo = true;
            StartKo(ctx, events);
        }

        if (Unconscious)
        {
            KoTimer -= dt * 60;
            if (KoTimer <= 0 && VitalPartsUp() && !BloodKo)
            {
                Unconscious = false;
                KoTimer = 0;
                if (starvationKo)
                {
                    starvationKo = false;
                    starveGrace = 0.5f + 1.5f * NextRoll(ctx);
                }
                events?.Add(new MedicalEvent(MedicalEventKind.WokeUp));
            }
        }
    }

    void TickBlood(float dt, in MedicalContext ctx)
    {
        var c = ctx.Constants;
        var race = ctx.Race;
        // Wounds clot: their strength falls by `bleeding clot rate` (already x0.1) per hour and they close at 0.
        float decay = c.BleedingClotRate * dt;
        float woundSum = 0;
        int keep = 0;
        for (int i = 0; i < wounds.Count; i++)
        {
            float w = wounds[i] - decay;
            if (w <= 0) continue;
            wounds[keep++] = w;
            woundSum += w;
        }
        wounds.RemoveRange(keep, wounds.Count - keep);
        // A damaged stump keeps bleeding: -(h + b) x 0.002 while h + b is negative.
        float stump = 0;
        foreach (var p in Parts)
            if (p.Limb == LimbState.Stump && p.Flesh + p.Bandage < 0) stump += -(p.Flesh + p.Bandage) * 0.002f;
        Bleeding = (c.ExtraBloodLossFromBodyparts * stump + woundSum) * ctx.Options.ChanceOfDeath * 1.2f * race.BleedRate;
        Blood -= Bleeding * dt;
        float cap = BloodCapacity(race, ctx.Strength, ctx.HasStats);
        if (Bleeding <= 0 && Blood < cap)
            Blood = MathF.Min(cap, Blood + c.BloodRecoveryRate * dt * race.HealRate * (ctx.Resting || Unconscious ? c.RestingHealRateMult : 1));
    }

    void TickHunger(float dt, in MedicalContext ctx)
    {
        var c = ctx.Constants;
        float t = c.StarvationTime * ctx.Options.HungerTime;
        if (t <= 0) return;
        float hungerRate = ctx.Race.HungerRate;
        if (Fed <= 0)
        {
            float situation = HungerSituationFactor(c, ctx.Resting || Unconscious, ctx.UsingMachine, ctx.EncumbranceFactor);
            Hunger -= dt / t * hungerRate * situation;
        }
        else
        {
            float moved = MathF.Min(Fed, dt / t * c.FedRecoveryRateMult * hungerRate);
            Fed -= moved;
            Hunger += moved;
        }
        Hunger = Math.Min(Hunger, MaxHunger);
    }

    // ------------------------------------------------------------------ save keys

    /// <summary>Writes the MEDICAL_STATE fields (docs/formats/save.md): the bools <c>coma dead unconcious incapacitated</c>, the floats <c>blood bleeding hung fed KO</c> and per part <c>k</c> <c>flesh hit bandage rig stun wear hitmult</c> and the string <c>sid</c>. Limb states and wounds are not part of these keys.</summary>
    public void WriteSave(IDictionary<string, float> floats, IDictionary<string, bool> bools, IDictionary<string, string> strings)
    {
        bools["coma"] = Coma;
        bools["dead"] = Dead;
        bools["unconcious"] = Unconscious;
        bools["incapacitated"] = Incapacitated;
        floats["blood"] = Blood;
        floats["bleeding"] = Bleeding;
        floats["hung"] = Hunger;
        floats["fed"] = Fed;
        floats["KO"] = KoTimer;
        for (int k = 0; k < Parts.Count; k++)
        {
            var p = Parts[k];
            floats["flesh" + k] = p.Flesh;
            floats["hit" + k] = p.HitWeight;
            floats["bandage" + k] = p.Bandage;
            floats["rig" + k] = p.Rig;
            floats["stun" + k] = p.Stun;
            floats["wear" + k] = p.Wear;
            floats["hitmult" + k] = p.HitMult;
            strings["sid" + k] = p.Template.StringId;
        }
    }

    /// <summary>
    /// Reads what <see cref="WriteSave"/> wrote. <paramref name="resolve"/> finds a LOCATIONAL_DAMAGE template by string id; the base HP of each part comes from the race's anatomy
    /// (100 when the race does not list the part). The parts come back in the saved order.
    /// </summary>
    public static MedicalState ReadSave(RaceData race, IReadOnlyDictionary<string, float> floats, IReadOnlyDictionary<string, bool> bools,
        IReadOnlyDictionary<string, string> strings, Func<string, BodyPartTemplate?> resolve)
    {
        var parts = new List<HealthPart>();
        for (int k = 0; strings.TryGetValue("sid" + k, out var sid); k++)
        {
            if (resolve(sid) is not { } template) continue;
            float baseHp = 100;
            foreach (var a in race.Anatomy) if (a.Part.StringId == sid) { baseHp = a.BaseHp; break; }
            float F(string name, float fallback = 0) => floats.TryGetValue(name + k, out float v) ? v : fallback;
            parts.Add(new HealthPart(template, (int)F("hit"), baseHp)
            {
                Flesh = F("flesh"), Bandage = F("bandage"), Rig = F("rig"), Stun = F("stun"), Wear = F("wear"), HitMult = F("hitmult", 1),
                SelfHealing = race.SelfHealing,
            });
        }
        var state = new MedicalState(parts)
        {
            Blood = floats.GetValueOrDefault("blood"),
            Hunger = floats.GetValueOrDefault("hung", MaxHunger),
            Fed = floats.GetValueOrDefault("fed"),
            KoTimer = floats.GetValueOrDefault("KO"),
            Unconscious = bools.GetValueOrDefault("unconcious"),
            Dead = bools.GetValueOrDefault("dead"),
            Bleeding = floats.GetValueOrDefault("bleeding"),
        };
        foreach (var p in state.Parts) p.WasDown = p.IsDown;
        return state;
    }
}
