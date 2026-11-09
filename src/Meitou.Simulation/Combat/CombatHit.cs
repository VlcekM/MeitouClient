using System.Numerics;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Combat;

/// <summary>
/// The ordinary damage path that resolved a landed contact: a melee blow, a projectile, or an execution blow of a captive
/// (<c>CombatSystem.Execute</c>, worked by the captives' <c>SlowWorld</c>).
/// </summary>
public enum CombatHitSource
{
    /// <summary>
    /// A melee blow.
    /// </summary>
    Melee,
    /// <summary>
    /// A projectile (a bolt or a harpoon).
    /// </summary>
    Projectile,
    /// <summary>
    /// An execution blow on a captive.
    /// </summary>
    Execution
}

/// <summary>
/// A value-only observation of one resolved melee or projectile contact, after its ordinary consequences. A landed contact fully absorbed by armour
/// still counts; a miss, dodge or block does not. No mutable character state or reused medical-event buffer is retained.
/// </summary>
/// <param name="Attacker">The original slot and generation; a projectile's shooter may no longer resolve in the world.</param>
/// <param name="Sequence">The attack sequence for melee, the projectile id, or 0 for an execution. Interpret together with Source and
/// Attacker.</param>
/// <param name="Blow">The melee blow index, or the execution job's blow count; -1 for a projectile.</param>
/// <param name="Part">Index in the victim's medical parts, as selected by the damage resolver.</param>
/// <param name="Damage">The delivered packet after armour and toughness.</param>
/// <param name="VictimPosition">The victim's world-space root/feet position, not an exact surface impact point.</param>
/// <param name="Travel">World-space travel direction; melee uses Y = 0. It is not guaranteed to be normalized.</param>
/// <param name="CutDirection">The melee technique direction after facing; -1 for a projectile or an execution.</param>
/// <param name="Intended">The victim was the selected target, rather than another body reached by the sweep or projectile.</param>
/// <param name="VictimWasAlive">The victim's medical state was not dead immediately before this contact.</param>
/// <param name="VictimWasIncapacitated">The victim's medical incapacitation immediately before this contact; no pre-damage part state is
/// included.</param>
public readonly record struct CombatHit(
    long Tick, CombatHitSource Source, CharacterId Attacker, CharacterId Victim, int Sequence, int Blow, int Part,
    HitDamage Damage, ArmourAbsorbed Absorbed, Vector3 VictimPosition, Vector3 Travel, float VictimYaw,
    int CutDirection, bool LowStrike, bool Intended, bool VictimWasAlive, bool VictimWasIncapacitated);

/// <summary>
/// A system observing ordinary resolved combat hits. Register it with the existing ModContext.AddSystem; observers run serially on the tick thread in
/// system order. The callback may read the world and update its own system state, but must not change characters, orders or other world state, or
/// invoke combat recursively. Queue a command for a later tick for gameplay changes; hash simulation-relevant state through IStateHashed. A throw
/// disables this observer for that world.
/// </summary>
public interface ICombatHitObserver : ITickSystem
{
    /// <summary>
    /// Called once for each ordinary resolved hit, serially on the tick thread.
    /// </summary>
    void HitResolved(World world, in CombatHit hit);
}