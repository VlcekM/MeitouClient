using System.Numerics;

namespace Meitou.Simulation.Combat;

/// <summary>
/// The push a downing blow or a blast gives the victim's ragdoll.
/// </summary>
public sealed partial class CombatSystem
{
    /// <summary>
    /// The push a downing blow gives the ragdoll (world units/s): this much per point of damage, at least this, and the lift with it (engine choice).
    /// </summary>
    const float PushPerDamage = 0.5f, PushMin = 12.5f, PushLift = 0.2f;

    /// <summary>
    /// Meitou (<see cref="Bodies.BodyOptions.AdvancedRagdolls"/>, docs/game/ragdolls.md "Initial state"): a hit that knocked the victim out or killed
    /// it leaves <see cref="CharacterCold.Blow"/>, the speed its ragdoll starts with: the victim's own motion of the last tick plus a push along the
    /// blow's <paramref name="direction"/> (X/Z) sized by the <paramref name="damage"/>, and the struck <paramref name="part"/>'s bone, which the
    /// ragdoll takes more of the push on.
    /// </summary>
    void Knockdown(World world, int victim, CharacterCold vc, Vector2 direction, float damage, int part, float lift = PushLift)
    {
        if (vc.Medical is not { Incapacitated: true }) return;

        if (!_options.AdvancedRagdolls)
        {
            vc.Knockdown = Vector3.Zero;
            return;
        }

        var motion = world.Characters.Previous[victim].Velocity;
        var push = Vector3.Zero;
        if (direction.LengthSquared() > 1e-6f)
        {
            direction = Vector2.Normalize(direction);
            float speed = MathF.Max(damage * PushPerDamage, PushMin);
            push = new Vector3(direction.X, lift, direction.Y) * speed;
        }

        vc.Blow = new Ragdolls.RagdollBlow(motion + push, push, BoneOf(vc.Medical, part));
        vc.Knockdown = motion + push;
    }

    /// <summary>
    /// The debug explosion's throw (<c>explode</c>): sets <see cref="CharacterCold.Launch"/> on a knocked-out or dead victim, a push along
    /// <paramref name="direction"/> (X/Z) of <paramref name="force"/> world units/s (at least <see cref="PushMin"/>) with the lift.
    /// <see cref="Ragdolls.RagdollSystem"/> applies it to the live ragdoll bodies (a frozen one is woken) once the ragdoll exists, or slides a
    /// victim without a ragdoll. Independent of <see cref="Bodies.BodyOptions.AdvancedRagdolls"/>. Returns false (nothing thrown) for a victim who
    /// is not down.
    /// </summary>
    internal bool Blast(CharacterCold vc, Vector2 direction, float force)
    {
        if (vc.Medical is not { Incapacitated: true } || force < 0.5f) return false;

        direction = direction.LengthSquared() > 1e-6f ? Vector2.Normalize(direction) : Vector2.UnitX;
        float speed = MathF.Min(force, BlastMaxSpeed);
        vc.Launch = new Vector3(direction.X, BlastLift, direction.Y) * speed;

        return true;
    }

    /// <summary>
    /// Largest launch speed (world units/s) a blast gives, so a huge --force cannot break the ragdoll physics.
    /// </summary>
    internal const float BlastMaxSpeed = 400;

    /// <summary>
    /// The explosion's lift, as a share of the throw speed (gravity is 98.1 units/s², so 100 out and 15 up at the centre flies about 0.3 s, about 30
    /// units before the ground slows it; flight range is 2 x lift x speed² / 98.1).
    /// </summary>
    const float BlastLift = 0.15f;

    /// <summary>
    /// The struck part's <c>bone name</c>, else its <c>bone name 2</c>; null for no part or neither.
    /// </summary>
    static string? BoneOf(Bodies.MedicalState medical, int part)
    {
        if ((uint)part >= (uint)medical.Parts.Count) return null;

        var template = medical.Parts[part].Template;
        return template.BoneName is { Length: > 0 } bone ? bone : template.BoneName2 is { Length: > 0 } second ? second : null;
    }
}
