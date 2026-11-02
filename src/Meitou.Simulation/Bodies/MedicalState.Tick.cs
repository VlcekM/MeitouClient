using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

public sealed partial class MedicalState
{
    // ------------------------------------------------------------------ food

    /// <summary>
    /// Puts food into the stomach buffer, in hunger levels (a bite of n nutrition is n / 100, <see cref="Items.FeedSystem"/>). The buffer digests into the hunger level in
    /// <see cref="Tick"/>. The original sets the buffer (it eats only with an empty stomach); adding is the same there.
    /// </summary>
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
    /// One update of bandaging (<c>FUN_140649a00</c>): the first part in part order that the kit type treats and that has room (h + b &lt; M) gets the rate x dt on its bandaged
    /// pool; only when that fills it (clamped to h + b = M) does the next such part get the full rate x dt too, and so on. An ordinary kit treats only organic parts, a robot kit
    /// only robotic ones. <paramref name="touched"/> is how many parts took some (the kit drains once for each). The unit of <c>dt</c> is <b>Unknown</b> in the original; Meitou passes
    /// <b>game seconds</b> (an engine choice: with hours a 100 HP part would take 2 to 33 game hours to bandage; the medic XP function takes seconds too). Returns the total bandaged.
    /// </summary>
    public float Treat(float dt, float skill, float kitQuality, bool robotKit, in MedicalContext ctx, out int touched) =>
        Bandage(TreatmentRate(ctx.Constants, skill, kitQuality, robotKit) * dt, robotKit, ctx.Race, out touched);

    /// <summary>Gives <paramref name="amount"/> to the bandaged pool of the first part with room that the kit type treats (null: no kit, any part), moving on only when a part fills (see <see cref="Treat"/>). A dead patient takes nothing.</summary>
    public float Bandage(float amount, bool? robotKit, RaceData race, out int touched)
    {
        touched = 0;
        if (Dead || amount <= 0) return 0;
        float total = 0;
        foreach (var p in parts)
        {
            if (robotKit is { } robot && p.IsRobotic(race) != robot) continue;
            float room = p.MaxHp - p.Flesh - p.Bandage;
            if (room <= 0) continue;
            float add = MathF.Min(amount, room);
            p.Bandage += add;
            total += add;
            touched++;
            if (amount < room) break;   // not filled: one part per update
        }
        return total;
    }

    /// <summary>
    /// One update of closing a wound (<c>FUN_14064d800</c>), which first aid does before any bandaging: the newest wound's strength falls by
    /// <c>medic speed mult</c> x 2 x lerp(0.0005, 0.03, min(skill, kit quality) x 0.01) x dt, and it closes below 0. Returns false when there is no wound.
    /// </summary>
    public bool CloseWound(float dt, float skill, float kitQuality, GameConstants c)
    {
        if (wounds.Count == 0) return false;
        float t = Math.Clamp(MathF.Min(skill, kitQuality) * 0.01f, 0, 1);
        int last = wounds.Count - 1;
        wounds[last] -= c.MedicSpeedMult * 2 * (0.0005f + (0.03f - 0.0005f) * t) * dt;
        if (wounds[last] < 0) wounds.RemoveAt(last);
        return true;
    }

    /// <summary>The untreated damage a kit of this type could still bandage: the sum over the parts it treats of max(0, M - h - b). Read-only.</summary>
    public float BandageRoom(RaceData race, bool robotKit)
    {
        float total = 0;
        foreach (var p in parts)
        {
            if (p.IsRobotic(race) != robotKit) continue;
            total += MathF.Max(0, p.MaxHp - p.Flesh - p.Bandage);
        }
        return total;
    }

    /// <summary>Below this much untreated damage (HP summed over the parts) a patient counts as treated (an engine choice that keeps float dust from starting treatments).</summary>
    public const float FirstAidThreshold = 0.5f;

    /// <summary>Whether first aid with a kit of this type has something to do: <see cref="BandageRoom"/> above <see cref="FirstAidThreshold"/>. A dead patient needs none.</summary>
    public bool NeedsAid(RaceData race, bool robotKit) => !Dead && BandageRoom(race, robotKit) > FirstAidThreshold;

    /// <summary>
    /// The first aid need score the medic finders and the quick right click read (medical <c>+0xac</c> organic / <c>+0xa8</c> robot, KenshiLib <c>needsFirstAidScoreTotal_fleshy</c> / <c>_robot</c>;
    /// the aggregates function <c>FUN_140644e60</c>, <b>Verified</b> by decompile; read through <c>scoreFirstAidNeed</c> <c>FUN_140643ca0</c>): -0.1 for a dead patient. Per part
    /// pct = 100 / M x (flesh + bandage); the organic parts (no robot race, no fitted limb) and the robotic ones are taken apart, each with the minimum over its vital parts and over all of
    /// them, scaled by 0.01 and set to 1 above 0.999. need = max(0.5 x (1 - worst), 1 - worst vital); the blood term (1 - blood / capacity) x max(0, bleeding - 1.5 x <paramref name="extraBloodLoss"/>) x 10
    /// replaces it for the race's own kind of kit when larger and above 0.2. So 0 for a patient with everything bandaged.
    /// </summary>
    public float FirstAidNeed(RaceData race, bool robotKit, float capacity, float extraBloodLoss) =>
        FirstAidNeeds(race, capacity, extraBloodLoss) is var (organic, robot) && robotKit ? robot : organic;

    /// <summary>Both of <see cref="FirstAidNeed"/>'s scores from one walk of the parts: the organic kit's and the robot kit's.</summary>
    public (float Organic, float Robot) FirstAidNeeds(RaceData race, float capacity, float extraBloodLoss)
    {
        if (Dead) return (-0.1f, -0.1f);
        float vitalOrganic = 100, anyOrganic = 100, vitalRobot = 100, anyRobot = 100;
        foreach (var p in parts)
        {
            float pct = 100f / p.MaxHp * (p.Flesh + p.Bandage);
            if (!p.IsRobotic(race))
            {
                if (pct < vitalOrganic && p.IsVital) vitalOrganic = pct;
                if (pct < anyOrganic) anyOrganic = pct;
            }
            else
            {
                if (pct < vitalRobot && p.IsVital) vitalRobot = pct;
                if (pct < anyRobot) anyRobot = pct;
            }
        }
        static float Scaled(float v) { float x = (float)((double)v * 0.01); return x > 0.999f ? 1 : x; }
        float blood = (2f - (Blood / capacity + 1f)) * MathF.Max(0, Bleeding - extraBloodLoss * 1.5f) * 10f;
        float organic = MathF.Max((2f - (Scaled(anyOrganic) + 1f)) * 0.5f, 2f - (Scaled(vitalOrganic) + 1f));
        float robot = MathF.Max((2f - (Scaled(anyRobot) + 1f)) * 0.5f, 2f - (Scaled(vitalRobot) + 1f));
        if (!race.IsRobot && organic < blood && blood > 0.2) organic = blood;
        if (race.IsRobot && robot < blood && blood > 0.2) robot = blood;
        return (organic, robot);
    }

    /// <summary>The need above which a patient counts as needing first aid (<c>0.01</c>, the quick right click's and the medic finders' floor, <b>Verified</b>: <c>DAT_141682ad8</c>).</summary>
    public const float AidNeedFloor = 0.01f;

    /// <summary>How much of a kit a treatment uses (<c>FUN_140644250</c>): lerp(<c>medkit drain 99</c>, <c>medkit drain 1</c>, (100 - skill) / 100) x dt, x <c>robot medic speed mult</c> for robot kits. An expert uses a kit 20 times slower than a beginner.</summary>
    public static float KitDrain(GameConstants c, float skill, float dt, bool robotKit)
    {
        float t = Math.Clamp((100 - skill) / 100, 0, 1);
        return (c.MedkitDrain99 + (c.MedkitDrain1 - c.MedkitDrain99) * t) * dt * (robotKit ? c.RobotMedicSpeedMult : 1);
    }

    // ------------------------------------------------------------------ splints

    /// <summary>Effective HP (h - s + rig, absolute) under which a part can take a splint, and to which healing wears the splint down (<c>FUN_140649d50</c>, <c>FUN_14064f5c0</c>).</summary>
    public const float SplintLimit = 50;

    /// <summary>
    /// Whether a splint would help (<c>needsSplint</c>, <c>FUN_140646400</c>): some non-vital part has rig &lt; b, (h - s + rig) x 1.05 &lt; 50 and rig + 1 &lt; trunc(<paramref name="skill"/>).
    /// The menu asks with skill 50, a medic with min(medic, kit quality). Robot races never need one; a dead patient neither.
    /// </summary>
    public bool NeedsSplint(RaceData race, float skill)
    {
        if (Dead || race.IsRobot) return false;
        float cap = MathF.Truncate(skill);
        foreach (var p in parts)
            if (!p.IsVital && p.Rig < p.Bandage && (p.Flesh - p.Stun + p.Rig) * 1.05f < SplintLimit && p.Rig + 1 < cap) return true;
        return false;
    }

    /// <summary>
    /// One update of splinting (<c>FUN_140649d50</c>): with t = min(skill, kit quality) x 0.01, the first non-vital part (in part order) with h - s + rig &lt; 50, rig &lt; s + b and
    /// rig &lt; cap = 1 - trunc(t x -100) (float rounding: quality 20 caps at 20, 50 at 51) gets rig += the first aid rate x dt, clamped to s + b and the cap. Returns the rig added (0: nothing left to splint).
    /// </summary>
    public float Splint(float dt, float skill, float kitQuality, in MedicalContext ctx)
    {
        if (Dead || ctx.Race.IsRobot) return 0;
        float t = MathF.Min(skill, kitQuality) * 0.01f;
        float cap = 1 - (int)(float)(t * -100.0);   // t in float, x -100 in double, back to float, truncated: quality 20 gives 20 (0.2f is just under 0.2)
        foreach (var p in parts)
        {
            if (p.IsVital || p.Flesh - p.Stun + p.Rig >= SplintLimit || p.Rig >= p.Stun + p.Bandage || p.Rig >= cap) continue;
            float before = p.Rig;
            p.Rig = MathF.Min(p.Rig + TreatmentRate(ctx.Constants, skill, kitQuality, false) * dt, MathF.Min(p.Stun + p.Bandage, cap));
            return p.Rig - before;
        }
        return 0;
    }

    // ------------------------------------------------------------------ the tick

    /// <summary>Game hours in one second of character time (the Verified 1200/11 s per game hour, <c>GameClock</c>).</summary>
    public const float GameHoursPerSecond = 11f / 1200;

    /// <summary>
    /// Steps the character by <paramref name="dt"/> seconds of character time (real seconds x game speed, <c>FUN_140652000</c> and what it calls; docs/game/character-stats.md
    /// "Time"): every part degenerates or heals itself, heals, is repaired and recovers from stun; wounds clot and blood runs out or recovers; hunger falls or is refilled from
    /// the stomach (by the game clock, in hours); the KO timer counts down; blood loss, starvation and destroyed vital parts kill. Events go to <paramref name="events"/> if given.
    /// A dead character is not stepped.
    /// </summary>
    public void Tick(float dt, in MedicalContext ctx, List<MedicalEvent>? events = null)
    {
        if (Dead || dt <= 0) return;
        if (EatenCountdown > 0) EatenCountdown--;
        var c = ctx.Constants;
        var race = ctx.Race;
        float chance = ctx.Options.ChanceOfDeath;
        float hours = dt * GameHoursPerSecond;
        LegKoPoint = KoPoint(c, ctx.Toughness);
        bool standing = Standing;   // the posture of the last update: a leg that goes down in the loop below still counts from a standing start

        // Part healing rates. In a bed (kinds 6 and 0x19) degeneration is 0 and the bed scales the organic or the robotic rate; state 2 doubles the organic rate (HealMult).
        float degMult = ctx.Bed != BedKind.None ? 0 : DegenerationMult(c, ctx.Toughness);
        float organic = race.HealRate * (ctx.Bed == BedKind.Bed ? ctx.BedFactor : 1) * ctx.HealMult;
        float robotic = race.HealRate * (ctx.Bed == BedKind.RepairBed ? ctx.BedFactor : 1);
        bool conscious = !Unconscious;
        float dtb = dt * ctx.BodyTimeScale;

        for (int i = 0; i < Parts.Count && !Dead; i++)
        {
            var p = Parts[i];
            float m = p.MaxHp;
            if (m <= 0.01f) m = 0.01f;
            bool isRobotic = p.IsRobotic(race);
            // R: M / 100 x the organic or robotic rate; a stump's is twice the organic rate, without M / 100.
            float r = p.Limb == LimbState.Stump ? organic * 2 : m / 100 * (isRobotic ? robotic : organic);

            // The hit-focus multiplier decays towards 1 while the flesh is not negative, and is reset once it is.
            if (p.Flesh < 0) p.HitMult = 1;
            else if (p.HitMult > 1) p.HitMult = MathF.Max(1, p.HitMult - 0.01f * dtb);
            // Self healing (the race's): untreated damage becomes treated at 0.4 of the heal speed, in place of degenerating.
            if (p.SelfHealing && conscious && p.Flesh - p.Stun < m - p.Bandage)
                p.Bandage += dtb * 0.05f * c.HealRateMult * r * 0.4f;
            // Degeneration of untreated damage; none for non-vital robotic parts.
            else if (p.Flesh < m && p.Flesh > -3 * m)
            {
                float dm = isRobotic && !p.IsVital ? 0 : degMult;
                p.Flesh -= dtb * 0.05f * c.BodypartDegenerationRate * chance * dm * UntreatedDamage(m, p.Flesh, p.Bandage) * 1.2f;
            }
            // Healing of treated damage, 250 times faster on robotic parts; a crushed limb does not heal.
            if (p.Bandage > 0 && p.Flesh < m && p.Limb != LimbState.Crushed)
            {
                float heal = MathF.Min(p.Bandage, dtb * 0.05f * c.HealRateMult * r * (isRobotic ? 250 : 1));
                p.Bandage -= heal;
                p.Flesh = MathF.Min(p.Flesh + heal, m);
            }
            // Wear repair in a repair bed: wear moves into the treated pool, which stays within the part's HP.
            if (p.Wear > 0 && ctx.RepairRate > 0)
            {
                float amount = dtb * 0.05f * c.RestingHealRateMult * ctx.RepairRate;
                float full = p.MaxHp + p.Wear;
                p.Wear = MathF.Max(0, p.Wear - amount);
                p.Bandage = MathF.Min(p.Bandage + amount, MathF.Max(full - p.Wear - p.Flesh, 0));
            }
            // The extra buffer is held under 50 - (h - s); stun recovers.
            if (p.Rig > 0) p.Rig = MathF.Max(0, MathF.Min(p.Rig, 50 - (p.Flesh - p.Stun)));
            if (p.Stun > 0) p.Stun = MathF.Max(0, p.Stun - dtb * 0.05f * c.StunRecoveryRate * c.HealRateMult * r);

            CheckPart(i, ctx, events, inHitPath: false, standing);
        }
        if (Dead) return;

        TickBlood(dtb, ctx);
        TickHunger(hours, ctx);
        CheckBlood(ctx, events);
        if (Dead) return;

        // Not while being eaten: a vampiric meal drains the hunger level to -0.5 before it kills (whether the original's own update lets it pass 0 is Unknown).
        if (Hunger <= 0 && EatenCountdown <= 0) { Die(DeathCause.Starvation, events); return; }

        // Starvation passes the character out below a toughness-dependent point; after waking, a grace period (game hours) keeps it from happening at once.
        starveGrace = MathF.Max(0, starveGrace - hours);
        if (!Unconscious && starveGrace <= 0 && Hunger < StarvationKoThreshold(c, ctx.Toughness, Fed > 0))
        {
            starvationKo = true;
            StartKo(ctx, events);
        }

        // A vital part under the KO point puts the character into a coma at once, without a KO timer.
        bool vitalDown = VitalPartsKeepDown(LegKoPoint);
        if (vitalDown && !Unconscious) ApplyKo(0, events);

        if (Unconscious)
        {
            KoTimer -= dt;
            // Blood under 0 keeps an unconscious character out (reassessCollapseMode); the blood-KO flag only lowers the conscious threshold to -25 (CheckBlood).
            if (KoTimer <= 0 && !vitalDown && Blood >= 0 && ctx.PlayDead)
            {
                // Awake but not safe to get up: it lies on, playing dead (posture 3), until it is (docs/game/ragdolls.md "Playing dead").
                KoTimer = 0;
                PlayingDead = true;
            }
            else if (KoTimer <= 0 && !vitalDown && Blood >= 0)
            {
                Unconscious = false;
                PlayingDead = false;
                KoTimer = 0;
                if (starvationKo)
                {
                    starvationKo = false;
                    starveGrace = 0.5f + 1.5f * NextRoll(ctx);
                }
                events?.Add(new MedicalEvent(MedicalEventKind.WokeUp));
            }
        }
        bool legsDown = LegsDown;
        if (legsDown && legsWereDown == 0) CollapseOnset = true;
        legsWereDown = legsDown ? 1 : 0;
    }

    /// <summary>
    /// Catches up <paramref name="hours"/> game hours spent off screen in one step (Meitou; the original changes nothing off screen, docs/simulation.md "Off-screen tier"): the
    /// squad is taken to have tended itself, so every wound is treated, clotted and healing at the part's heal rate; stun is gone, blood recovers at the resting rate and a
    /// character whose vital parts and blood allow it wakes. Hunger, degeneration and death are not stepped. A dead character is left as it is.
    /// </summary>
    public void Recover(float hours, in MedicalContext ctx)
    {
        if (Dead || hours <= 0) return;
        var c = ctx.Constants;
        float seconds = hours / GameHoursPerSecond;
        foreach (var p in parts)
        {
            float m = MathF.Max(p.MaxHp, 0.01f);
            p.Bandage = MathF.Max(p.Bandage, MathF.Max(m - p.Flesh, 0));
            bool robotic = p.IsRobotic(ctx.Race);
            float r = p.Limb == LimbState.Stump ? ctx.Race.HealRate * 2 : m / 100 * ctx.Race.HealRate;
            float heal = MathF.Min(p.Bandage, seconds * 0.05f * c.HealRateMult * r * (robotic ? 250 : 1));
            p.Bandage -= heal;
            p.Flesh = MathF.Min(p.Flesh + heal, m);
            p.Stun = 0;
            p.Rig = 0;
            p.HitMult = 1;
        }
        wounds.Clear();
        Bleeding = 0;
        Blood = MathF.Min(Capacity(ctx), Blood + c.BloodRecoveryRate * seconds * ctx.Race.HealRate * c.RestingHealRateMult);
        if (Blood > 25) BloodKo = false;
        if (Unconscious && !VitalPartsKeepDown(KoPoint(c, ctx.Toughness)) && Blood >= 0)
        {
            Unconscious = false;
            PlayingDead = false;
            KoTimer = 0;
            starvationKo = false;
        }
    }

    void TickBlood(float dt, in MedicalContext ctx)
    {
        var c = ctx.Constants;
        var race = ctx.Race;
        // Wounds clot: their strength falls by `bleeding clot rate` (already x0.1) per second and they close at 0.
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
        foreach (var p in parts)
            if (p.Limb == LimbState.Stump && p.Flesh + p.Bandage < 0) stump += -(p.Flesh + p.Bandage) * 0.002f;
        // Floored at 0 (a negative race `bleed rate` gains no blood); blood is then held to capacity every tick.
        Bleeding = MathF.Max(0, (c.ExtraBloodLossFromBodyparts * stump + woundSum) * ctx.Options.ChanceOfDeath * 1.2f * race.BleedRate);
        Blood -= Bleeding * dt;
        float cap = Capacity(ctx);
        Blood = MathF.Min(Blood, cap);
        if (Bleeding <= 0 && Blood < cap)
            Blood = MathF.Min(cap, Blood + c.BloodRecoveryRate * dt * race.HealRate * (ctx.Resting ? c.RestingHealRateMult : 1));
    }

    /// <summary>
    /// Hunger by the game clock: <paramref name="hours"/> game hours (<c>FUN_14064da70</c> reads the clock's elapsed time, not the character time). First the free food: below the
    /// <see cref="MedicalContext.HungerFloor"/> with an empty stomach, the stomach gets min(floor - hunger, 0.04). Then with an empty stomach hunger falls by hours / T x race
    /// <c>hunger rate</c> x the situation factor; with food it rises by what the stomach gives, at most hours / T x <c>fed recovery rate mult</c> x race rate x the slot-0x390 factor.
    /// </summary>
    void TickHunger(float hours, in MedicalContext ctx)
    {
        var c = ctx.Constants;
        if (ctx.HungerFloor > Hunger && Fed <= 0) Fed = Math.Clamp(ctx.HungerFloor - Hunger, 0, 0.04f);
        if (Hunger > MaxHunger) Hunger = MaxHunger;
        float t = c.StarvationTime * ctx.Options.HungerTime;
        if (t <= 0) return;
        if (Fed <= 0)
        {
            float situation = HungerSituationFactor(c, ctx.Resting || Unconscious, ctx.UsingMachine, ctx.EncumbranceFactor, ctx.UseHungerRate);
            Hunger -= hours / t * ctx.Race.HungerRate * situation;
        }
        else
        {
            float moved = MathF.Min(Fed, hours / t * c.FedRecoveryRateMult * ctx.Race.HungerRate * ScaleK);
            Fed -= moved;
            Hunger += moved;
        }
    }

}
