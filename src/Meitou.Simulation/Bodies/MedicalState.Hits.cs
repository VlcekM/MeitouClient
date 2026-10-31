using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

public sealed partial class MedicalState
{
    // ------------------------------------------------------------------ hits

    /// <summary>
    /// The game's F10 debug heal (Meitou only, not the original's): every part whole (no wear, stun, bandage or splint), full blood, no wounds and no KO time left,
    /// so the next body tick wakes the character. A dead character stays dead.
    /// </summary>
    public void Heal(in MedicalContext ctx)
    {
        if (Dead) return;
        foreach (var p in parts)
        {
            p.Wear = p.Stun = p.Bandage = p.Rig = 0;
            p.Flesh = p.MaxHp;
            p.HitMult = 1;
            p.WasDown = false;
        }
        wounds.Clear();
        Blood = Capacity(ctx);
        BloodKo = false;
        KoTimer = 0;
    }

    float NextRoll(in MedicalContext ctx) => BodyRolls.Float(ctx.Seed, ctx.CharacterKey, BodyRolls.MedicalDomain, rollCounter++);

    /// <summary>
    /// Chooses the part a blow lands on (<c>FUN_1406508d0</c>, <b>Verified</b>): among the parts whose flesh is above -M (not stunned or rigged away) and that are not the stump of a limb,
    /// weighted by hit weight times <see cref="HealthPart.HitMult"/> (the weight alone for hit direction 6); a low strike excludes arms and head. Draws one roll from the character's
    /// stream. With no part eligible the last part of the body is taken. The chosen part's <see cref="HealthPart.HitMult"/> is then updated, before the damage lands:
    /// 0 when its flesh is gone; else it gains 1 while under 4, or, with a negative health fraction, is held to 2 and gains 1 only while under 1.
    /// Returns -1 only for a body without parts.
    /// </summary>
    public int ChoosePart(in MedicalContext ctx, bool lowStrike = false, bool directionSix = false)
    {
        if (Parts.Count == 0) return -1;
        int chosen = Forced(ctx.Options.ForcedHitPart);
        if (chosen < 0) chosen = Rolled(ctx, lowStrike, directionSix);
        var p = Parts[chosen];
        float cap = float.NegativeInfinity;
        if (!Alive(p)) p.HitMult = 0;
        else if (p.Fraction < 0)
        {
            cap = 1;
            if (p.HitMult > 2) p.HitMult = 2;
        }
        else cap = 4;
        if (p.HitMult < cap) p.HitMult += 1;
        return chosen;
    }

    int Rolled(in MedicalContext ctx, bool lowStrike, bool directionSix)
    {
        float total = 0;
        for (int i = 0; i < Parts.Count; i++) total += Weight(Parts[i], lowStrike, directionSix);
        if (total <= 0) return Parts.Count - 1;
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

    /// <summary>
    /// The debug menu's forced part (<see cref="BodyOptions.ForcedHitPart"/>): the part of that template string id, else of that name (another race's body), when a blow could pick it
    /// (whatever the strike's height); -1 without one, and no roll is drawn.
    /// </summary>
    int Forced((string Id, string Name)? forced)
    {
        if (forced is not ({ } id, { } name)) return -1;
        int named = -1;
        for (int i = 0; i < Parts.Count; i++)
        {
            var p = Parts[i];
            if (Weight(p, false, true) <= 0) continue;
            if (p.Template.StringId == id) return i;
            if (named < 0 && string.Equals(p.Template.Name, name, StringComparison.OrdinalIgnoreCase)) named = i;
        }
        return named;
    }

    /// <summary>A part a blow can still pick: its flesh is above -M, and it is not the stump of a limb.</summary>
    static bool Alive(HealthPart p) => p.Flesh > -p.MaxHp;

    static float Weight(HealthPart p, bool lowStrike, bool directionSix)
    {
        if (!Alive(p)) return 0;
        if (p.Limb == LimbState.Stump && p.Template.Type is BodyPartType.Leg or BodyPartType.Arm) return 0;
        if (lowStrike && p.Template.Type is BodyPartType.Arm or BodyPartType.Head) return 0;
        return p.HitWeight * (directionSix ? 1 : p.HitMult);
    }

    /// <summary>
    /// Applies a blow to a part (<c>FUN_14064f300</c>): h falls by cut + pierce + blunt x <c>blunt permanent organ damage</c>; s rises by the rest of the blunt plus the extra
    /// stun; blood falls by (cut + pierce) x <c>immediate blood loss</c> x scale x race bleed rate; robots and prosthetics gain wear; a cut or pierce opens a wound. Then the
    /// part is checked: down (a vital one knocks the character out), destroyed (vital: death; other: severed, subject to the Dismemberment setting and to the owner having a stats object).
    /// The part's <see cref="HealthPart.HitMult"/> is not touched here: <see cref="ChoosePart"/> updates it when it picks the part.
    /// </summary>
    public void ApplyHit(int part, in HitDamage hit, in MedicalContext ctx, List<MedicalEvent>? events = null)
    {
        if (Dead) return;
        bool standing = Standing;   // the posture before the blow (a leg lost in it still counts from a standing start)
        var c = ctx.Constants;
        var race = ctx.Race;
        var p = Parts[part];
        float perm = c.BluntPermanentOrganDamage;
        p.Flesh -= hit.Cut + hit.Pierce + hit.Blunt * perm;
        p.Stun += (1 - perm) * hit.Blunt + hit.ExtraStun;
        Blood -= (hit.Cut + hit.Pierce) * c.ImmediateBloodLoss * hit.Scale * race.BleedRate;
        if (p.IsRobotic(race) && !ctx.ExemptFromWear)
            p.Wear += (hit.Cut + hit.Pierce + hit.Blunt) * 0.04f * c.RobotWearRate;
        // The wound (FUN_1406508d0): strength max(cut, pierce) x bleed rate x scale, losing `bleeding clot rate` a second (TickBlood).
        // Opened whatever the race's bleed rate; a bloodless race's wounds drain nothing but still need first aid.
        if (MathF.Max(hit.Cut, hit.Pierce) > 0) wounds.Add(MathF.Max(hit.Cut, hit.Pierce) * c.BleedRate * hit.Scale);

        CheckPart(part, ctx, events, inHitPath: true, standing);
        CheckBlood(ctx, events);
    }

    void CheckPart(int index, in MedicalContext ctx, List<MedicalEvent>? events, bool inHitPath, bool standing)
    {
        if (Dead) return;
        var p = Parts[index];
        bool down = p.IsDown;
        if (down && !p.WasDown) events?.Add(new MedicalEvent(MedicalEventKind.PartDown, index));
        // Every blow that leaves a vital part under 0 restarts the KO timer (FUN_14064f300); a part going under 0 in the tick does not (the KO point rule does, VitalPartsKeepDown).
        if (down && inHitPath && p.IsVital) RestartKo(KoTime(ctx), events);
        p.WasDown = down;
        bool destroyed = inHitPath ? p.IsDestroyed : p.Flesh - p.Stun < -p.MaxHp;
        if (!destroyed) return;
        // The cut-off rule (FUN_14064f300 for a blow, FUN_14064f5c0 for a tick, <b>Verified</b>): a blow needs the setting at Rare or more, a stats object and an original or
        // crushed limb; the tick (a wound worsening) needs Frequent and an original limb. A prosthetic is never cut off (FUN_14064edc0 returns for a replaced slot).
        var setting = ctx.Options.Dismemberment;
        bool blowSevers = setting >= Dismemberment.Rare && ctx.HasStats && HasStatsObject && (p.Limb is LimbState.Original or LimbState.Crushed) && p.Template.Severance;
        if (p.IsVital)
        {
            Die(DeathCause.VitalPart, events);
            // Meitou (Decapitation): the blow's rule cuts the head off too; the original returns here for every vital part (the head's record has severance set).
            if (inHitPath && ctx.Options.Decapitation && blowSevers && p.Template.Type == BodyPartType.Head) Decapitate(events);
            return;
        }
        bool severs = inHitPath ? blowSevers : setting >= Dismemberment.Frequent && p.Limb == LimbState.Original && p.Template.Severance;
        if (severs) Sever(index, ctx, standing, events);
    }

    /// <summary>The head was cut off by the blow that killed the character (Meitou, <see cref="BodyOptions.Decapitation"/>); saved as <c>meitou decapitated</c>.</summary>
    public bool Decapitated { get; private set; }

    /// <summary>Cuts the head off a character the blow has just killed (<see cref="BodyOptions.Decapitation"/>): only the look changes, nothing drops.</summary>
    public void Decapitate(List<MedicalEvent>? events = null)
    {
        if (Decapitated) return;
        Decapitated = true;
        events?.Add(new MedicalEvent(MedicalEventKind.Decapitated, -1));
    }

    /// <summary>
    /// Cuts a limb off (<c>FUN_14064edc0</c>, <b>Verified</b>): the part becomes a stump (base HP 5), the character loses rand(5, 30) blood and gets a wound of strength <c>bleed rate</c> x 200.
    /// A leg lost while <paramref name="standing"/> (posture 0) also knocks the character out for <see cref="LegLossKo"/> times a uniform roll in [0, 1), the KO time set before the blood
    /// is lost; a leg lost while crawling, staying low, or already out does not. The caller awards the toughness XP (<see cref="XpService.ToughnessFromLimbLoss"/>) and spawns the
    /// severed limb item from the race's <c>severed limbs</c>.
    /// </summary>
    public void Sever(int part, in MedicalContext ctx, bool standing, List<MedicalEvent>? events = null)
    {
        var p = Parts[part];
        p.Limb = LimbState.Stump;
        p.Replacement = null;
        if (standing && p.Template.LimbSlot is 2 or 3) ApplyKo(LegLossKo(ctx) * NextRoll(ctx), events);
        Blood -= 5 + 25 * NextRoll(ctx);
        wounds.Add(ctx.Constants.BleedRate * 200);
        events?.Add(new MedicalEvent(MedicalEventKind.PartSevered, part));
    }

    /// <summary>
    /// The leg-loss KO time before its roll (<c>FUN_140644980</c>, <b>Verified</b> by decompile): max(3, (2 x <c>knockout time base</c> + |capacity - blood| x k x 0.5) x (1 - toughness / 100) x 4),
    /// k = lerp(<c>knockout mult 1</c>, <c>knockout mult 99</c>, toughness / 100). Only the missing blood enters it; the other parts' terms of <see cref="KoTerms"/> do not.
    /// </summary>
    public float LegLossKo(in MedicalContext ctx) => KoBlood(ctx, 1 - Math.Clamp(ctx.Toughness / 100, 0, 1));

    /// <summary>
    /// <c>FUN_140644980</c> (<b>Verified</b>): max(3, (2 x <c>knockout time base</c> + |capacity - blood| x k x 0.5) x <paramref name="factor"/> x 4), k = lerp(<c>knockout mult 1</c>,
    /// <c>knockout mult 99</c>, toughness / 100).
    /// </summary>
    float KoBlood(in MedicalContext ctx, float factor)
    {
        var c = ctx.Constants;
        float t = Math.Clamp(ctx.Toughness / 100, 0, 1);
        float k = c.KnockoutMult1 + (c.KnockoutMult99 - c.KnockoutMult1) * t;
        float missingBlood = MathF.Abs(Capacity(ctx) - Blood) * k * 0.5f;
        return MathF.Max(3, (2 * c.KnockoutTimeBase + missingBlood) * factor * 4);
    }

    /// <summary>
    /// Fits a prosthetic on the part of its slot (<c>FUN_1406445b0</c>): the part's base HP becomes the limb's HP at the item's level, its wear and stun the item's, and its flesh
    /// the maximum less the item's damage (<paramref name="damage"/>, <paramref name="wear"/>, <paramref name="stun"/>: the INVENTORY_ITEM_STATE <c>dam</c>, <c>wear</c>,
    /// <c>stun</c> a removed limb carries). Bandage and splint are cleared (an engine choice).
    /// </summary>
    public bool FitReplacement(LimbReplacement limb, Items.ItemInstance? item, float damage = 0, float wear = 0, float stun = 0)
    {
        if (LimbPart(limb.LimbSlot) is not { } p) return false;
        p.Limb = LimbState.Replaced;
        p.Replacement = limb;
        p.ReplacementItem = item;
        p.ReplacementQuality = Math.Clamp((item?.Level ?? 0) * 0.01f, 0, 1);
        p.Wear = wear;
        p.Stun = stun;
        p.Bandage = 0;
        p.Rig = 0;
        float top = p.MaxHp;
        p.Flesh = MathF.Min(top - damage, top);
        p.WasDown = p.IsDown;
        return true;
    }

    /// <summary>
    /// Takes the prosthetic off limb slot <paramref name="slot"/> (the reverse of <see cref="FitReplacement"/>): its item keeps <c>dam</c> = maximum - flesh, the part's wear and stun;
    /// the part becomes a stump with flesh 5, wear and stun 0. Null when the slot has no prosthetic.
    /// </summary>
    public Items.ItemInstance? RemoveReplacement(int slot)
    {
        if (LimbPart(slot) is not { Limb: LimbState.Replaced } p) return null;
        var item = p.ReplacementItem;
        if (item is not null) (item.LimbDamage, item.LimbWear, item.LimbStun) = (MathF.Max(0, p.MaxHp - p.Flesh), p.Wear, p.Stun);
        p.Limb = LimbState.Stump;
        p.Replacement = null;
        p.ReplacementItem = null;
        p.ReplacementQuality = 0;
        p.Wear = 0;
        p.Stun = 0;
        p.Flesh = 5;
        p.WasDown = p.IsDown;
        return item;
    }

    /// <summary>
    /// One CHARACTER <c>starting health</c> entry on a new body (<c>FUN_140645ef0</c>): below -M a limb becomes a stump (flesh 0, the treated pool 5); otherwise the flesh is
    /// <paramref name="value"/> clamped to [-M, M] and the rest, M - flesh, is already treated. A part the anatomy lacks is ignored.
    /// </summary>
    public void ApplyStartingHealth(string partId, float value)
    {
        if (Parts.FirstOrDefault(p => p.Template.StringId == partId) is not { } p) return;
        float max = p.MaxHp;
        if (value < -max && p.Template.LimbSlot is >= 0 and < 4)
        {
            p.Limb = LimbState.Stump;
            p.Flesh = 0;
            p.Bandage = 5;
        }
        else
        {
            p.Flesh = Math.Clamp(value, -max, max);
            p.Bandage = max - p.Flesh;
        }
        p.WasDown = p.IsDown;
    }

    // ------------------------------------------------------------------ KO and death

    void StartKo(in MedicalContext ctx, List<MedicalEvent>? events) => ApplyKo(KoTime(ctx), events);

    /// <summary>
    /// The factor of the KO given to a non-friendly character that is put down (docs/game/carry-loot.md "Putting down"): lerp(0.1, 0.4, the carrier's assassination / 100)
    /// (<b>Observed</b>, <c>Task_PutDownObject</c>; the help text says assassination decides how long they stay out).
    /// </summary>
    public static float PutDownKoFactor(float assassination) => 0.1f + 0.3f * Math.Clamp(assassination / 100, 0, 1);

    /// <summary>
    /// The KO time of a character put down by a non-friendly carrier, knocked out by stealth or shackled, in seconds of character time, with no random roll: <see cref="KoBlood"/> with the
    /// caller's <paramref name="factor"/> (<c>FUN_140644980</c>, <b>Verified</b>: the one KO-time function of leg loss, put-down, stealth and shackles).
    /// </summary>
    public float PutDownKoSeconds(in MedicalContext ctx, float factor) => KoBlood(ctx, factor);

    /// <summary>Knocks the character out for <paramref name="seconds"/> seconds of character time from outside the medical rules (a drug, a scripted event); a longer running KO is kept.</summary>
    public void KnockOut(float seconds, List<MedicalEvent>? events = null) => ApplyKo(seconds, events);

    void ApplyKo(float seconds, List<MedicalEvent>? events)
    {
        if (Dead) return;
        if (!Unconscious)
        {
            Unconscious = true;
            KoTimer = seconds;
            events?.Add(new MedicalEvent(MedicalEventKind.KnockedOut));
        }
        else KoTimer = MathF.Max(KoTimer, seconds);
        PlayingDead = false;
    }

    /// <summary>Starts the KO timer over at <paramref name="seconds"/>, also when a longer KO is running (<c>startKnockoutTimer</c> overwrites it).</summary>
    void RestartKo(float seconds, List<MedicalEvent>? events)
    {
        if (Dead) return;
        ApplyKo(seconds, events);
        KoTimer = seconds;
    }

    /// <summary>Kills the character because one of its death items was taken while it was down (docs/game/animals.md "Death items").</summary>
    public void Butcher(List<MedicalEvent>? events = null) => Die(DeathCause.Butchered, events);

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
        float cap = Capacity(ctx);
        if (Blood <= -cap) { Die(DeathCause.BloodLoss, events); return; }
        if (Blood <= 0 && !BloodKo) { BloodKo = true; StartKo(ctx, events); }
        else if (BloodKo && Blood <= -25 && !Unconscious) StartKo(ctx, events);
    }

}
