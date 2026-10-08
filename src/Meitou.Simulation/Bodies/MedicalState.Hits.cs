using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

public sealed partial class MedicalState
{
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

}
