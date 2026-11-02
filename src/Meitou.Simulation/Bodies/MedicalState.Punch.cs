using Meitou.Data.Gameplay;

namespace Meitou.Simulation.Bodies;

/// <summary>A martial artist's blow hurting its own limb (docs/game/combat.md "Punch self-damage", <c>MedicalSystem::punchSomething</c> <c>FUN_140666780</c>, <b>Verified</b>).</summary>
public sealed partial class MedicalState
{
    /// <summary>
    /// Cut and blunt damage to the limb in slot <paramref name="limbSlot"/> (0 left arm, 1 right arm, 2 left leg, 3 right leg), written as <c>FUN_140644a70</c> does: flesh by cut and by
    /// <c>blunt permanent organ damage</c> x blunt, stun by the rest of the blunt. Nothing for a fitted prosthetic or a slot with no part. True when a part took it.
    /// </summary>
    public bool PunchDamage(int limbSlot, float cut, float blunt, GameConstants c)
    {
        foreach (var p in parts)
        {
            if (p.Template.LimbSlot != limbSlot) continue;
            if (p.Replacement is not null) return false;
            p.Flesh -= cut;
            Blunt(p, blunt, c);
            return true;
        }
        return false;
    }
}
