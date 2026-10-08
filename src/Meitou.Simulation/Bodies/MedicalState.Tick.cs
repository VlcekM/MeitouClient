using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

public sealed partial class MedicalState
{
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

}
