using System.Numerics;

namespace Meitou.Simulation.Combat;

public sealed partial class CombatSystem
{
    /// <summary>The push a downing blow gives the ragdoll (world units/s): this much per point of damage, at least this, and the lift with it (engine choice).</summary>
    const float PushPerDamage = 0.5f, PushMin = 12.5f, PushLift = 0.2f;

    /// <summary>
    /// Meitou (<see cref="BodyOptions.AdvancedRagdolls"/>, docs/game/ragdolls.md "Initial state"): a hit that knocked the victim out or killed it leaves <see cref="CharacterCold.Blow"/>, the speed its ragdoll starts with:
    /// the victim's own motion of the last tick plus a push along the blow's <paramref name="direction"/> (X/Z) sized by the <paramref name="damage"/>, and the struck
    /// <paramref name="part"/>'s bone, which the ragdoll takes more of the push on.
    /// </summary>
    void Knockdown(World world, int victim, CharacterCold vc, Vector2 direction, float damage, int part)
    {
        if (vc.Medical is not { Incapacitated: true }) return;
        if (!options.AdvancedRagdolls) { vc.Knockdown = Vector3.Zero; return; }
        var motion = world.Characters.Previous[victim].Velocity;
        var push = Vector3.Zero;
        if (direction.LengthSquared() > 1e-6f)
        {
            direction = Vector2.Normalize(direction);
            float speed = MathF.Max(damage * PushPerDamage, PushMin);
            push = new Vector3(direction.X, PushLift, direction.Y) * speed;
        }
        vc.Blow = new Ragdolls.RagdollBlow(motion + push, push, BoneOf(vc.Medical, part));
        vc.Knockdown = motion + push;
    }

    /// <summary>The struck part's <c>bone name</c>, else its <c>bone name 2</c>; null for no part or neither.</summary>
    static string? BoneOf(Bodies.MedicalState medical, int part)
    {
        if ((uint)part >= (uint)medical.Parts.Count) return null;
        var template = medical.Parts[part].Template;
        return template.BoneName is { Length: > 0 } bone ? bone : template.BoneName2 is { Length: > 0 } second ? second : null;
    }
}
