using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// What weather, acid and local effects do to the body (docs/formats/weather.md "Effects on characters"; <c>FUN_140646c80</c>, <c>FUN_140647010</c>,
/// <c>FUN_1406472c0</c>, <c>FUN_140648460</c>, <c>FUN_140647e30</c>, <c>FUN_140648ef0</c>, <b>Verified</b> by decompilation). The damage goes
/// straight into the parts' fields, as the original's <c>FUN_140644a70</c> does: no wound, no blood loss, no robot wear, no hit multiplier; the
/// medical tick sees the result (down parts, knock-outs, deaths). <c>dt</c> is in seconds of character time; <c>protection</c> is the sum of the worn
/// pieces' <c>weather protection amount</c> for the kind (unclamped).
/// </summary>
public sealed partial class MedicalState
{
    /// <summary>
    /// The dust value (medical <c>+0x14c</c>): s - s x protection clamped to [0, 1], written by each dust-storm update. <see cref="DustSet"/>
    /// (<c>+0x148</c>) turns on with the first positive one and nothing turns it off, and nothing else writes the value, so it keeps its last value
    /// after the storm (<b>Observed</b>: no other writer in the binary). Not saved.
    /// </summary>
    public float Dust { get; private set; }
    /// <summary>
    /// Whether a dust-storm update has set a positive <see cref="Dust"/>; nothing clears it.
    /// </summary>
    public bool DustSet { get; private set; }

    /// <summary>
    /// The dust in effect: <see cref="Dust"/> when it is set and positive, else 0.
    /// </summary>
    public float DustInEffect => DustSet && Dust > 0 ? Dust : 0;

    /// <summary>
    /// The attack and unarmed skill penalty of the dust, the "Weather penalty" (<c>FUN_140643a30</c>): (int)(dust x 10).
    /// </summary>
    public int DustAttackPenalty => (int)(DustInEffect * 10);

    /// <summary>
    /// The dust factor of the perception and friendly-fire stat multipliers (<c>FUN_1406439f0</c>): lerp(dust, 1, 0.5); 1 without dust.
    /// </summary>
    public float DustStatFactor => 1 - 0.5f * DustInEffect;

    /// <summary>
    /// A dust-storm update with the region's strength <paramref name="s"/> (<c>FUN_140648ef0</c>). True when it was positive.
    /// </summary>
    public bool DustStorm(float s, float protection)
    {
        Dust = s;
        if (!(s > 0)) return false;
        DustSet = true;
        Dust = Math.Clamp(s - s * protection, 0, 1);
        return true;
    }

    /// <summary>
    /// Whether a part takes acid and fire: every part but a limb that is not its original one (stump, prosthetic or crushed; the original's
    /// limb-state test before each part).
    /// </summary>
    static bool Burns(HealthPart p) => p.Template.LimbSlot < 0 || p.Limb == LimbState.Original;

    /// <summary>
    /// Blunt damage <paramref name="blunt"/> to a part as <c>FUN_140644a70</c> lands it: flesh by <c>blunt permanent organ damage</c> x it, stun by
    /// the rest.
    /// </summary>
    static void Blunt(HealthPart p, float blunt, GameConstants c)
    {
        float perm = c.BluntPermanentOrganDamage;
        p.Flesh -= perm * blunt;
        p.Stun += (1 - perm) * blunt;
    }

    /// <summary>
    /// Swimming in acidic water (<c>FUN_140646c80</c>): <paramref name="dose"/> = dt x <c>acidic water</c>; every part that burns takes blunt dose x
    /// 0.25. True when dose &gt; 0.
    /// </summary>
    public bool AcidWater(float dose, GameConstants c)
    {
        if (!(dose > 0)) return false;
        float d = dose * 0.25f;
        foreach (var p in _parts) if (Burns(p)) Blunt(p, d, c);
        return true;
    }

    /// <summary>
    /// Bare feet on acidic ground (<c>FUN_140647010</c>): <paramref name="dose"/> = dt x <c>acidic ground</c>; nothing for a robot race; each
    /// original leg whose flesh is above 50 takes blunt dose x 0.2. True when a leg took it.
    /// </summary>
    public bool AcidGround(float dose, RaceData race, GameConstants c)
    {
        if (!(dose > 0) || race.IsRobot) return false;
        bool any = false;
        foreach (var p in _parts)
        {
            if (p.Template.Type != BodyPartType.Leg || p.Flesh <= 50 || p.Limb != LimbState.Original) continue;
            Blunt(p, dose * 0.2f, c);
            any = true;
        }
        return any;
    }

    /// <summary>
    /// Acid rain (<c>FUN_1406472c0</c>): every part that burns takes blunt 0.08 x dt x (1 - protection) when that is positive (not scaled by the
    /// weather's strength). True when any did.
    /// </summary>
    public bool AcidRain(float dt, float protection, GameConstants c)
    {
        float d = dt * 0.08f - protection * dt * 0.08f;
        if (!(dt > 0) || !(d > 0)) return false;
        bool any = false;
        foreach (var p in _parts) if (Burns(p))
        {
            Blunt(p, d, c);
            any = true;
        }
        return any;
    }

    /// <summary>
    /// Fire (<c>FUN_140648460</c>): <paramref name="dose"/> = dt x the effect's strength; every part that burns loses 10 x dose x (1 - protection) of
    /// flesh. True when that is positive.
    /// </summary>
    public bool Burning(float dose, float protection)
    {
        if (!(dose > 0)) return false;
        float d = dose * 10 - dose * 10 * protection;
        if (!(d > 0)) return false;
        foreach (var p in _parts) if (Burns(p)) p.Flesh -= d;
        return true;
    }

    /// <summary>
    /// Poison gas (<c>FUN_140647e30</c>): <paramref name="dose"/> = dt x the effect's strength, x4, halved while unconscious, less the protection's
    /// share; the first torso part without a side takes it as blunt (the original takes the first in its part map, whose order is <b>Unknown</b>;
    /// Meitou the first in <see cref="Parts"/>, an engine choice). True when positive.
    /// </summary>
    public bool Gas(float dose, float protection, GameConstants c)
    {
        if (!(dose > 0)) return false;
        float d = dose * 4;
        if (Unconscious) d *= 0.5f;
        d -= protection * d;
        if (!(d > 0)) return false;
        foreach (var p in _parts)
            if (p.Template.Type == BodyPartType.Torso && p.Template.Side == BodySide.None)
            {
                Blunt(p, d, c);
                break;
            }
        return true;
    }
}