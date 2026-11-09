using Meitou.Data.Gameplay.Bodies;

namespace Meitou.Simulation.Bodies;

/// <summary>
/// What one frame on a torture rack left: whether any part is still being stretched, and the output progress the rack shows (0 whole, 1 every part
/// past its limit).
/// </summary>
public readonly record struct RackStretch(bool Busy, float Progress);

/// <summary>
/// A torture rack's frames on its user (<see cref="StretchLimbs"/>).
/// </summary>
public sealed partial class MedicalState
{
    /// <summary>
    /// The part's destruction test the rack reads: flesh less 1.1 x stun against minus its maximum (the eaten test's idiom, <c>FUN_14029af90</c>,
    /// <b>Verified</b>).
    /// </summary>
    static float RackLevel(HealthPart p) => p.Flesh - 1.1f * p.Stun;

    /// <summary>
    /// One frame of a torture rack on its user (<c>TortureBuilding</c> slot 28 <c>FUN_14029af90</c>, <b>Verified</b> decompile; docs/game/locks.md
    /// "Torture racks"). Every part with a side (arms and legs) that is not yet past its limit (flesh - 1.1 stun above minus its maximum) loses
    /// <paramref name="dt"/> of flesh (<c>FUN_140644a70</c> with only the first term set: no stun, no wound); one past it is cut off when
    /// <paramref name="limbLoss"/> (the original's <c>DAT_142133504</c>, read as the dismemberment setting) and its limb is original or crushed.
    /// While any of those parts still has flesh above 0 the frame is busy; once none has, the parts without a side (head, torso) are stretched the
    /// same way, each frame busy while one still takes damage. The progress is 0.5 - 0.25 x (the best level of the sideless parts + the best of the
    /// sided parts, each as (flesh - 1.1 stun) / maximum, -1 with none).
    /// </summary>
    public RackStretch StretchLimbs(float dt, in MedicalContext ctx, bool limbLoss, List<MedicalEvent>? events = null)
    {
        bool anyFlesh = false, busy = false;
        for (int i = 0; i < _parts.Length; i++)
        {
            var p = _parts[i];
            if (p.Template.Side == BodySide.None) continue;
            if (RackLevel(p) <= -p.MaxHp)
            {
                if (limbLoss && p.Limb is LimbState.Original or LimbState.Crushed) Sever(i, ctx, events);
            }
            else Stretch(i, p, dt, ctx, events);
            anyFlesh |= p.Flesh > 0;
        }
        if (anyFlesh) busy = true;
        else
            for (int i = 0; i < _parts.Length; i++)
            {
                var p = _parts[i];
                if (p.Template.Side != BodySide.None || RackLevel(p) <= -p.MaxHp) continue;
                Stretch(i, p, dt, ctx, events);
                busy = true;
            }
        float none = -1, sided = -1;
        foreach (var p in _parts)
        {
            float ratio = RackLevel(p) / p.MaxHp;
            if (p.Template.Side == BodySide.None) none = MathF.Max(none, ratio);
            else sided = MathF.Max(sided, ratio);
        }
        return new RackStretch(busy, 0.5f - (none + sided) * 0.25f);
    }

    /// <summary>
    /// One part's frame on the rack: <paramref name="dt"/> off its flesh, then the part checks and the leg collapse.
    /// </summary>
    void Stretch(int index, HealthPart p, float dt, in MedicalContext ctx, List<MedicalEvent>? events)
    {
        p.Flesh -= dt;
        CheckPart(index, ctx, events, inHitPath: false);
        LegCollapse(events);
    }
}