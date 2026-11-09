namespace Meitou.Simulation.Bodies;

/// <summary>
/// Being eaten alive: the eat rate by toughness and the passes over the parts that end in death (<see cref="BeingEaten"/>).
/// </summary>
public sealed partial class MedicalState
{
    /// <summary>
    /// Passes of <see cref="BeingEaten"/> that found nothing left to eat (the original's medical <c>+0x15c</c>); past
    /// <see cref="EatenPassesToDeath"/> the character dies. Not saved.
    /// </summary>
    public int EatenPasses { get; private set; }

    /// <summary>
    /// The passes with nothing left to eat after which the character is eaten alive (<b>Observed</b>, <c>FUN_140650370</c>: more than 5).
    /// </summary>
    public const int EatenPassesToDeath = 5;

    /// <summary>
    /// The original's being-eaten counter (Character <c>+0x279 &amp; 7</c>, <b>Verified</b>): 7 at every <see cref="BeingEaten"/> call
    /// (<c>FUN_1404356f0</c> sets it first), one less per <see cref="Tick"/> (the character update <c>FUN_1405d00b0</c>). Above 0 the HUD's health
    /// word reads BEING EATEN ALIVE!! and the portrait shows Eaten. Not saved.
    /// </summary>
    public int EatenCountdown { get; private set; }

    /// <summary>
    /// A vampiric meal (<c>FUN_140650370</c>, <b>Verified</b> literals): the step is rate x dt x 0.5; the hunger level loses 0.01 of it and the blood
    /// 0.07, until the hunger level is -0.5.
    /// </summary>
    public const float VampiricFactor = 0.5f, VampiricHunger = 0.01f, VampiricBlood = 0.07f, VampiricFloor = -0.5f;

    /// <summary>
    /// The eating rate of a character being eaten, per second (<c>FUN_140650370</c>, <b>Observed</b>): the eater passes 1, times lerp(2, 0.4,
    /// toughness x 0.01) (the CharStats toughness slot <c>+0x90</c> times the stat scale <c>+0x34</c>), or times 5 instead when a medical flag
    /// (<c>+0x164</c>, meaning <b>Unknown</b>) is set, which Meitou never sets.
    /// </summary>
    public static float EatRate(float toughness, float statScale = 1) => 2f + (0.4f - 2f) * (toughness * statScale * 0.01f);

    /// <summary>
    /// One update of being eaten (<c>Task_EatPrisoner</c> calls the character's being-eaten virtual, <c>FUN_1404356f0</c> then <c>FUN_140650370</c>;
    /// docs/game/ai-tasks.md "Prisoner fate", <b>Observed</b>): nothing happens to a robot, and nothing else is tested: a dead body is eaten the same
    /// way (the goals 230 and 233 use this action). The parts are walked from the last down: a limb that is not in its original state (a stump, a
    /// prosthetic, a crushed one) is passed over (<c>FUN_1400cd450</c> must read 0, <b>Verified</b>); the first part with h - 1.1 s above -M loses
    /// <paramref name="rate"/> x <paramref name="dt"/> from both its flesh and its treated pool, is checked as the medical tick checks a part (a
    /// vital part destroyed kills), and the update ends. A limb with nothing left is cut off on the way when <paramref name="limbLoss"/> (the
    /// original's global flag <c>DAT_142133504</c>, read as the dismemberment setting). When no part is left to eat the pass is counted; past
    /// <see cref="EatenPassesToDeath"/> the character dies (<see cref="DeathCause.Eaten"/>, the original's death cause "eaten"). With
    /// <paramref name="vampiric"/> (the eater's race) it is drunk from instead. True when the meal is over by this call: an eaten-alive death, or for
    /// a body already dead the end of its parts (the original then destroys the character, reason "eaten").
    /// </summary>
    public bool BeingEaten(float rate, float dt, in MedicalContext ctx, bool limbLoss, List<MedicalEvent>? events = null, bool vampiric = false)
    {
        EatenCountdown = 7;
        if (ctx.Race.IsRobot) return false;   // no dead test: a dead body is bitten like a living one (FUN_1404356f0, FUN_140650370, Verified)
        if (vampiric)
        {
            // A vampiric eater (its race's vampiric flag, FUN_1404356f0 passes the EATER's, Verified) drinks instead: hunger and blood drain, no part
            // is eaten, until the hunger level is at VampiricFloor; then the passes count as below.
            float d = rate * dt * VampiricFactor;
            Hunger -= d * VampiricHunger;
            Blood -= d * VampiricBlood;
            if (Hunger > VampiricFloor) return false;
        }
        else
        {
            float bite = rate * dt;
            for (int i = _parts.Length - 1; i >= 0; i--)
            {
                var p = _parts[i];
                bool limb = p.Template.LimbSlot is >= 0 and < 4;
                // only a limb in its original state is handled (FUN_1400cd450 == 0): not a stump, a prosthetic or a crushed one
                if (limb && p.Limb != LimbState.Original) continue;
                if (p.Flesh - 1.1f * p.Stun > -p.MaxHp)
                {
                    p.Flesh -= bite;
                    p.Bandage -= bite;
                    CheckPart(i, ctx, events, inHitPath: false);
                    LegCollapse(events);   // the bite runs the collapse check (FUN_140650370 calls FUN_140649320)
                    return Dead && Cause == DeathCause.Eaten;
                }
                // nothing left to bite: the limb slot is cut off (FUN_14064edc0), whatever the part's severance flag
                if (limbLoss && limb) Sever(i, ctx, events);
            }
        }
        if (EatenPasses > EatenPassesToDeath)
        {
            Die(DeathCause.Eaten, events);
            return true;
        }
        EatenPasses++;
        return false;
    }
}