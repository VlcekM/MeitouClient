using System.Numerics;

namespace Meitou.Simulation.Combat;

public sealed partial class CombatSystem
{
    /// <summary>The push a downing blow gives the ragdoll (world units/s): this much per point of damage, within these bounds, and the lift with it (engine choice).</summary>
    const float PushPerDamage = 0.4f, PushMin = 10, PushMax = 50, PushLift = 0.2f;

    /// <summary>
    /// Meitou (<see cref="BodyOptions.AdvancedRagdolls"/>, docs/game/ragdolls.md "Initial state"): a hit that knocked the victim out or killed it leaves <see cref="CharacterCold.Blow"/>, the speed its ragdoll starts with:
    /// the victim's own motion of the last tick plus a push along the blow's <paramref name="direction"/> (X/Z) sized by the <paramref name="damage"/>.
    /// </summary>
    void Knockdown(World world, int victim, CharacterCold vc, Vector2 direction, float damage)
    {
        if (vc.Medical is not { Incapacitated: true }) return;
        if (!options.AdvancedRagdolls) { vc.Knockdown = Vector3.Zero; return; }
        var motion = world.Characters.Previous[victim].Velocity;
        var push = Vector3.Zero;
        if (direction.LengthSquared() > 1e-6f)
        {
            direction = Vector2.Normalize(direction);
            float speed = Math.Clamp(damage * PushPerDamage, PushMin, PushMax);
            push = new Vector3(direction.X, PushLift, direction.Y) * speed;
        }
        vc.Blow = (motion + push, push);
        vc.Knockdown = motion + push;
    }
}
