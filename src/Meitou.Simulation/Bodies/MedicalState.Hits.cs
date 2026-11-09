using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// Hits and their consequences: choosing the part a blow lands on, the damage, cut-off limbs and prosthetics, and the knock-out and death rules.
/// </summary>
public sealed partial class MedicalState
{
    #region Hits

    /// <summary>
    /// The game's F10 debug heal (Meitou only, not the original's): every part whole (no wear, stun, bandage or splint), full blood, no wounds and no
    /// KO time left and awake (also a character playing dead). A dead character stays dead; a stump or crushed limb stays one
    /// (<see cref="RestoreLimbs"/> gives it back).
    /// </summary>
    public void Heal(in MedicalContext ctx)
    {
        if (Dead) return;
        foreach (var p in _parts)
        {
            p.Wear = p.Stun = p.Bandage = p.Rig = 0;
            p.Flesh = p.MaxHp;
            p.HitMult = 1;
            p.WasDown = false;
        }
        _wounds.Clear();
        Blood = Capacity(ctx);
        BloodKo = false;
        KoTimer = 0;

        // Also up from a knock-out: a character lying on "playing dead" (hostiles near) would otherwise stay down while they are, however whole it
        // is.
        if (PlayingDead) StandUp();
        Unconscious = false;
    }

    /// <summary>
    /// The F10 debug full heal's limbs (Meitou only): every stump and crushed limb is the original again; a fitted prosthetic stays. True when one
    /// changed.
    /// </summary>
    public bool RestoreLimbs()
    {
        if (Dead) return false;
        bool changed = false;
        foreach (var p in _parts)
            if (p.Limb is LimbState.Stump or LimbState.Crushed)
            {
                p.Limb = LimbState.Original;
                changed = true;
            }
        return changed;
    }

    /// <summary>
    /// The next roll of the character's medical stream, 0..1 (the world seed, the character's key and the roll counter).
    /// </summary>
    float NextRoll(in MedicalContext ctx) => BodyRolls.Float(ctx.Seed, ctx.CharacterKey, BodyRolls.MedicalDomain, _rollCounter++);

    /// <summary>
    /// Chooses the part a blow lands on (<c>FUN_1406508d0</c>, <b>Verified</b>): among the parts whose flesh is above -M (not stunned or rigged away)
    /// and that are not the stump of a limb, weighted by hit weight times <see cref="HealthPart.HitMult"/> (the weight alone for hit direction 6); a
    /// low strike excludes arms and head. Draws one roll from the character's stream. With no part eligible the last part of the body is taken. The
    /// chosen part's <see cref="HealthPart.HitMult"/> is then updated, before the damage lands: 0 when its flesh is gone; else it gains 1 while under
    /// 4, or, with a negative health fraction, is held to 2 and gains 1 only while under 1. Returns -1 only for a body without parts.
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

    /// <summary>
    /// The part a weighted roll picks (<see cref="Weight"/>); the last part when no part has weight.
    /// </summary>
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
    /// The debug menu's forced part (<see cref="BodyOptions.ForcedHitPart"/>): the part of that template string id, else of that name (another race's
    /// body), when a blow could pick it (whatever the strike's height); -1 without one, and no roll is drawn.
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

    /// <summary>
    /// A part a blow can still pick: its flesh is above -M, and it is not the stump of a limb.
    /// </summary>
    static bool Alive(HealthPart p) => p.Flesh > -p.MaxHp;

    /// <summary>
    /// A part's weight in the hit roll: 0 for a part already gone, the stump of an arm or leg, or an arm or head on a low strike; else its hit weight
    /// times <see cref="HealthPart.HitMult"/> (the weight alone for hit direction 6).
    /// </summary>
    static float Weight(HealthPart p, bool lowStrike, bool directionSix)
    {
        if (!Alive(p)) return 0;
        if (p.Limb == LimbState.Stump && p.Template.Type is BodyPartType.Leg or BodyPartType.Arm) return 0;
        if (lowStrike && p.Template.Type is BodyPartType.Arm or BodyPartType.Head) return 0;
        return p.HitWeight * (directionSix ? 1 : p.HitMult);
    }

    /// <summary>
    /// Applies a blow to a part (<c>FUN_14064f300</c>): h falls by cut + pierce + blunt x <c>blunt permanent organ damage</c>; s rises by the rest of
    /// the blunt plus the extra stun; blood falls by (cut + pierce) x <c>immediate blood loss</c> x scale x race bleed rate; robots and prosthetics
    /// gain wear; a cut or pierce opens a wound. Then the part is checked: down (a vital one knocks the character out), destroyed (vital: death;
    /// other: severed, subject to the Dismemberment setting and to the owner having a stats object). The part's <see cref="HealthPart.HitMult"/> is
    /// not touched here: <see cref="ChoosePart"/> updates it when it picks the part.
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

        // The wound (FUN_1406508d0): strength max(cut, pierce) x bleed rate x scale, losing `bleeding clot rate` a second (TickBlood).
        // Opened whatever the race's bleed rate; a bloodless race's wounds drain nothing but still need first aid.
        if (MathF.Max(hit.Cut, hit.Pierce) > 0) _wounds.Add(MathF.Max(hit.Cut, hit.Pierce) * c.BleedRate * hit.Scale);

        CheckPart(part, ctx, events, inHitPath: true);
        CheckBlood(ctx, events);
        LegCollapse(events);
    }

    /// <summary>
    /// The checks after a part's damage: the part-down event, a blow leaving a vital part down restarts the KO timer, and a destroyed part kills
    /// (vital) or is cut off by the dismemberment rule (a blow needs Rare and an original or crushed limb, a tick Frequent and an original one).
    /// </summary>
    void CheckPart(int index, in MedicalContext ctx, List<MedicalEvent>? events, bool inHitPath)
    {
        if (Dead) return;
        var p = Parts[index];
        bool down = p.IsDown;
        if (down && !p.WasDown) events?.Add(new MedicalEvent(MedicalEventKind.PartDown, index));

        // Every blow that leaves a vital part under 0 restarts the KO timer (FUN_14064f300); a part going under 0 in the tick does not (the KO point
        // rule does, VitalPartsKeepDown).
        if (down && inHitPath && p.IsVital) RestartKo(KoTime(ctx), events);
        p.WasDown = down;
        bool destroyed = inHitPath ? p.IsDestroyed : p.Flesh - p.Stun < -p.MaxHp;
        if (!destroyed) return;

        // The cut-off rule (FUN_14064f300 for a blow, FUN_14064f5c0 for a tick, <b>Verified</b>): a blow needs the setting at Rare or more, a stats
        // object and an original or crushed limb; the tick (a wound worsening) needs Frequent and an original limb. A prosthetic is never cut off
        // (FUN_14064edc0 returns for a replaced slot).
        var setting = ctx.Options.Dismemberment;
        bool blowSevers = setting >= Dismemberment.Rare && ctx.HasStats && HasStatsObject
            && (p.Limb is LimbState.Original or LimbState.Crushed) && p.Template.Severance;
        if (p.IsVital)
        {
            Die(DeathCause.VitalPart, events);

            // Meitou (Decapitation): the blow's rule cuts the head off too; the original returns here for every vital part (the head's record has
            // severance set).
            if (inHitPath && ctx.Options.Decapitation && blowSevers && p.Template.Type == BodyPartType.Head) Decapitate(events);
            return;
        }
        bool severs = inHitPath ? blowSevers : setting >= Dismemberment.Frequent && p.Limb == LimbState.Original && p.Template.Severance;
        if (severs) Sever(index, ctx, events);
    }

    /// <summary>
    /// The head was cut off by the blow that killed the character (Meitou, <see cref="BodyOptions.Decapitation"/>); saved as <c>meitou
    /// decapitated</c>.
    /// </summary>
    public bool Decapitated { get; private set; }

    /// <summary>
    /// Cuts the head off a character the blow has just killed (<see cref="BodyOptions.Decapitation"/>): only the look changes, nothing drops.
    /// </summary>
    public void Decapitate(List<MedicalEvent>? events = null)
    {
        if (Decapitated) return;
        Decapitated = true;
        events?.Add(new MedicalEvent(MedicalEventKind.Decapitated, -1));
    }

    /// <summary>
    /// Cuts a limb off (<c>FUN_14064edc0</c>, <b>Verified</b>): the part becomes a stump (base HP 5), the character loses rand(5, 30) blood and gets
    /// a wound of strength <c>bleed rate</c> x 200. A leg's stump is down, so a character standing (posture 0) is knocked out by
    /// <see cref="LegCollapse"/>, which the caller runs after its checks. The caller awards the toughness XP
    /// (<see cref="XpService.ToughnessFromLimbLoss"/>) and spawns the severed limb item from the race's <c>severed limbs</c>.
    /// </summary>
    public void Sever(int part, in MedicalContext ctx, List<MedicalEvent>? events = null)
    {
        var p = Parts[part];
        p.Limb = LimbState.Stump;
        p.Replacement = null;
        Blood -= 5 + 25 * NextRoll(ctx);
        _wounds.Add(ctx.Constants.BleedRate * 200);
        events?.Add(new MedicalEvent(MedicalEventKind.PartSevered, part));
    }

    /// <summary>
    /// The KO time a leg going down at posture 0 writes, in seconds of character time (<b>Verified</b> in game for a leg cut off standing, #134): the
    /// collapse check (<c>FUN_140649320</c>) sets posture 2 for a leg down at posture 0, and the posture setter (<c>FUN_1405c7e20</c>) writes 3 over
    /// the KO timer on that change (docs/game/character-stats.md "Cut-off and leg loss"). The sever's own rolled time (<c>FUN_140644980</c> x a roll)
    /// is always overwritten, so it is not modelled.
    /// </summary>
    public const float LegLossKoSeconds = 3;

    /// <summary>
    /// Posture 0 (<c>PS_NORMAL</c>) as of the end of the last <see cref="Tick"/>, or unknown before the first.
    /// </summary>
    enum PostureState : byte
    {
        /// <summary>
        /// No tick has set it yet (the first tick after a load or a new state).
        /// </summary>
        Unknown,
        /// <summary>
        /// Posture 0 (<c>PS_NORMAL</c>): standing.
        /// </summary>
        Normal,
        /// <summary>
        /// Any other posture.
        /// </summary>
        Other
    }

    /// <summary>
    /// The posture the character update last set (the original's <c>getProneState</c>, refreshed once a tick from <see cref="Standing"/>): a
    /// knock-out turns it from 0 only on the next update, so a leg going down between (another blow of the same tick) still finds it at 0. Not saved:
    /// unknown after a load, when the live <see cref="Standing"/> stands in.
    /// </summary>
    PostureState _posture;

    /// <summary>
    /// Whether the posture is normal: the last tick's, or <see cref="Standing"/> while it is unknown.
    /// </summary>
    bool PostureNormal => _posture == PostureState.Unknown ? Standing : _posture == PostureState.Normal;

    /// <summary>
    /// The posture setter's 0 -> 2 change (<c>FUN_140649320</c> then <c>FUN_1405c7e20</c>, <b>Verified</b> decompile): a leg down (stump, crushed or
    /// under the KO point, <see cref="HealthPart.LegDown"/>) while the posture is 0 sets posture 2 and writes <see cref="LegLossKoSeconds"/> to the
    /// KO timer, over any running knock-out, and the character is out until it runs down. Cut off or not, standing or already knocked out by a vital
    /// part since the last update.
    /// </summary>
    void LegCollapse(List<MedicalEvent>? events)
    {
        if (Dead || !PostureNormal || !LegsDown) return;
        _posture = PostureState.Other;
        RestartKo(LegLossKoSeconds, events);
    }

    /// <summary>
    /// <c>FUN_140644980</c> (<b>Verified</b>): max(3, (2 x <c>knockout time base</c> + |capacity - blood| x k x 0.5) x <paramref name="factor"/> x
    /// 4), k = lerp(<c>knockout mult 1</c>, <c>knockout mult 99</c>, toughness / 100).
    /// </summary>
    float KoBlood(in MedicalContext ctx, float factor)
    {
        var c = ctx.Constants;
        float t = ctx.Toughness / 100;
        float k = c.KnockoutMult1 + (c.KnockoutMult99 - c.KnockoutMult1) * t;
        float missingBlood = MathF.Abs(Capacity(ctx) - Blood) * k * 0.5f;
        return MathF.Max(3, (2 * c.KnockoutTimeBase + missingBlood) * factor * 4);
    }

    /// <summary>
    /// Fits a prosthetic on the part of its slot (<c>FUN_1406445b0</c>): the part's base HP becomes the limb's HP at the item's level, its wear and
    /// stun the item's, and its flesh the maximum less the item's damage (<paramref name="damage"/>, <paramref name="wear"/>,
    /// <paramref name="stun"/>: the INVENTORY_ITEM_STATE <c>dam</c>, <c>wear</c>, <c>stun</c> a removed limb carries). Bandage and splint are cleared
    /// (an engine choice).
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
    /// Takes the prosthetic off limb slot <paramref name="slot"/> (the reverse of <see cref="FitReplacement"/>): its item keeps <c>dam</c> = maximum
    /// - flesh, the part's wear and stun; the part becomes a stump with flesh 5, wear and stun 0. Null when the slot has no prosthetic.
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
    /// One CHARACTER <c>starting health</c> entry on a new body (<c>FUN_140645ef0</c>): a limb becomes a stump (flesh 0, the treated pool 5) when the
    /// value is below -M, or when the limb is no longer original (an earlier entry or fit already changed it, only reachable with a second entry for
    /// the same limb); otherwise the flesh is <paramref name="value"/> clamped to [-M, M] and the rest, M - flesh, is already treated. A part the
    /// anatomy lacks is ignored. A non-limb entry below -M is a stump in the original too (it writes past its limb table); that is not copied (no
    /// base-game record has one: all 8 entries are arms or legs).
    /// </summary>
    public void ApplyStartingHealth(string partId, float value)
    {
        if (Parts.FirstOrDefault(p => p.Template.StringId == partId) is not { } p) return;
        float max = p.MaxHp;
        if (p.Template.LimbSlot is >= 0 and < 4 && (value < -max || p.Limb != LimbState.Original))
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

    #endregion

    #region KO and death

    /// <summary>
    /// Knocks the character out for its rolled KO time (<see cref="KoTime"/>).
    /// </summary>
    void StartKo(in MedicalContext ctx, List<MedicalEvent>? events) => ApplyKo(KoTime(ctx), events);

    /// <summary>
    /// The factor of the KO given to a non-friendly character that is put down (docs/game/carry-loot.md "Putting down"): lerp(0.1, 0.4, the carrier's
    /// assassination / 100) (<b>Observed</b>, <c>Task_PutDownObject</c>; the help text says assassination decides how long they stay out).
    /// </summary>
    public static float PutDownKoFactor(float assassination) => 0.1f + 0.3f * Math.Clamp(assassination / 100, 0, 1);

    /// <summary>
    /// The KO time of a character put down by a non-friendly carrier, knocked out by stealth or shackled, in seconds of character time, with no
    /// random roll: <see cref="KoBlood"/> with the caller's <paramref name="factor"/> (<c>FUN_140644980</c>, <b>Verified</b>: the one KO-time
    /// function of leg loss, put-down, stealth and shackles).
    /// </summary>
    public float PutDownKoSeconds(in MedicalContext ctx, float factor) => KoBlood(ctx, factor);

    /// <summary>
    /// Knocks the character out for <paramref name="seconds"/> seconds of character time from outside the medical rules (a drug, a scripted event); a
    /// longer running KO is kept.
    /// </summary>
    public void KnockOut(float seconds, List<MedicalEvent>? events = null) => ApplyKo(seconds, events);

    /// <summary>
    /// Knocks the character out for <paramref name="seconds"/>, or keeps the longer of the running KO and it; ends playing dead. Nothing for
    /// one dead.
    /// </summary>
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

    /// <summary>
    /// Ends a knock-out (or playing dead) at once without healing anything (agent-playtest control channel: wake): the character is conscious and
    /// controllable. A flat-blood character keeps the blood-KO flag, as after a natural waking, so it does not fall again at once; a vital part still
    /// under the KO point does put it down again on the next tick (heal first then). False when it is dead or not unconscious.
    /// </summary>
    public bool WakeUp(List<MedicalEvent>? events = null)
    {
        if (Dead || !Unconscious) return false;
        Unconscious = false;
        PlayingDead = false;
        KoTimer = 0;
        if (Blood <= 25) BloodKo = true;
        events?.Add(new MedicalEvent(MedicalEventKind.WokeUp));
        return true;
    }

    /// <summary>
    /// Starts the KO timer over at <paramref name="seconds"/>, also when a longer KO is running (<c>startKnockoutTimer</c> overwrites it).
    /// </summary>
    void RestartKo(float seconds, List<MedicalEvent>? events)
    {
        if (Dead) return;
        ApplyKo(seconds, events);
        KoTimer = seconds;
    }

    /// <summary>
    /// Kills the character because one of its death items was taken while it was down (docs/game/animals.md "Death items").
    /// </summary>
    public void Butcher(List<MedicalEvent>? events = null) => Die(DeathCause.Butchered, events);

    /// <summary>
    /// Kills the character outright (the dev console's <c>kill</c>; a vital part counts as the cause). Nothing happens to one already dead.
    /// </summary>
    public void Kill(List<MedicalEvent>? events = null) => Die(DeathCause.VitalPart, events);

    /// <summary>
    /// Kills the character with <paramref name="cause"/> (nothing for one already dead).
    /// </summary>
    void Die(DeathCause cause, List<MedicalEvent>? events)
    {
        if (Dead) return;
        Dead = true;
        Cause = cause;
        events?.Add(new MedicalEvent(MedicalEventKind.Died, Cause: cause));
    }

    /// <summary>
    /// Blood rules shared by a hit and the tick: blood at 0 (or -25 once flagged) starts a KO, at -capacity the character dies. The flag clears in
    /// <c>TickBlood</c>.
    /// </summary>
    void CheckBlood(in MedicalContext ctx, List<MedicalEvent>? events)
    {
        if (Dead) return;
        float cap = Capacity(ctx);
        if (Blood <= -cap)
        {
            Die(DeathCause.BloodLoss, events);
            return;
        }
        if (Blood <= 0 && !BloodKo)
        {
            BloodKo = true;
            StartKo(ctx, events);
        }
        else if (BloodKo && Blood <= -25 && !Unconscious) StartKo(ctx, events);
    }

    #endregion
}