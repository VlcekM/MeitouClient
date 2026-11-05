using System.Numerics;

namespace Meitou.Simulation.Combat;

/// <summary>A claimed target policy. Part is an exact medical index; an invalid or missing part never selects a substitute.</summary>
public readonly record struct CombatTargetOverride(bool Allowed, bool AllowIncapacitated = false, int? Part = null,
    Vector3? AimPoint = null, bool GroundTarget = false);

/// <summary>
/// Optional targeting rules, registered as a tick system. The first rule claiming a target wins. Queries run during serial and
/// parallel combat/pursuit reads: they must be deterministic, read-only and thread-safe. The system owns and hashes its policy state.
/// No claim preserves ordinary targeting. A claim can admit a living incapacitated target, never a corpse, caged or carried target.
/// </summary>
public interface ICombatTargetRules : ITickSystem
{
    bool TryTarget(World world, CharacterId attacker, CharacterId target, bool picked, out CombatTargetOverride rule);
}
