namespace Meitou.Simulation.Bodies;

/// <summary>
/// The original's medical tests a status mod reads, named after them (docs/modding.md "Medical reads"; <c>MedicalSystem</c> methods, offsets
/// from the medical system's start, docs/game/character-stats.md "Per-part state"). Pure reads of the state as of the last medical tick, any
/// thread.
/// </summary>
public sealed partial class MedicalState
{
    /// <summary>
    /// The hunger under which <see cref="ReallyHungry"/> holds (<c>DAT_14168ba14</c>, <b>Verified</b> bytes: 1.25).
    /// </summary>
    public const float ReallyHungryBelow = 1.25f;

    /// <summary>
    /// The hunger under which starvation keeps a character down whatever its refractory period (<c>MedicalSystem::isHungerKO</c>
    /// <c>FUN_140644320</c>'s first test, <c>DAT_14167fad0</c>, <b>Verified</b> bytes: 0.1).
    /// </summary>
    public const float StarvedBelow = 0.1f;

    /// <summary>
    /// <c>MedicalSystem::isProbablyDying</c> (<c>FUN_140884a70</c>, <b>Verified</b> decompile): a vital part (<c>death</c>) whose flesh plus its
    /// bandaged pool is below 0, so it stays below 0 even once its treated damage heals.
    /// </summary>
    public bool ProbablyDying
    {
        get
        {
            foreach (var p in _parts) if (p.IsVital && p.Flesh + p.Bandage < 0) return true;
            return false;
        }
    }

    /// <summary>
    /// <c>MedicalSystem::canGetUpWakeUp</c> (<c>FUN_140596460</c>, <b>Verified</b> decompile): not knocked out (<c>+0x161</c>), not dead
    /// (<c>+0x164</c>) and not kept down by starvation (<c>isHungerKO</c>). Meitou reads that last test as hunger at or above
    /// <see cref="StarvedBelow"/>: its second branch (hunger under the starvation point once the refractory period is over, which arms the next
    /// period as a side effect) is the medical tick's own starvation knock-out, which leaves the character unconscious.
    /// </summary>
    public bool CanGetUpWakeUp => !Unconscious && !Dead && Hunger >= StarvedBelow;

    /// <summary>
    /// <c>MedicalSystem::isReallyHungry</c> (<c>FUN_1404120e0</c>, <b>Verified</b> decompile): hunger (<c>+0x60</c>) below
    /// <see cref="ReallyHungryBelow"/>.
    /// </summary>
    public bool ReallyHungry => Hunger < ReallyHungryBelow;

    /// <summary>
    /// <c>MedicalSystem::canIkick</c> (<c>FUN_140883fa0</c>, <b>Verified</b> decompile): the crippled flag (<c>+0x160</c>, a leg down, set by the
    /// medical tick conscious or not; <c>isCrippled</c> <c>FUN_14051c790</c> reads the same byte, <see cref="Crawling"/> while conscious) clear,
    /// and both legs (the parts at <c>+0x80</c> / <c>+0x88</c>; here every part with a leg <c>collapse part</c>) up: flesh - stun + rig at least 0
    /// (<see cref="HealthPart.IsDown"/> false).
    /// </summary>
    public bool CanKick
    {
        get
        {
            if (LegsDown) return false;

            foreach (var p in _parts) if (p.IsLeg && p.IsDown) return false;
            return true;
        }
    }
}
