namespace Meitou.Simulation.Combat;

/// <summary>
/// A melee blow, bolt or execution blow about to land on a character (docs/modding.md "Hits before they land"), as the filters see it: who
/// strikes whom, which blow, and the damage packet as it leaves the weapon, before armour and toughness. A filter may change
/// <see cref="Packet"/> or set <see cref="Cancelled"/>.
/// </summary>
public struct PendingHit
{
    /// <summary>
    /// The tick the hit lands on.
    /// </summary>
    public long Tick { get; init; }
    /// <summary>
    /// A melee blow, a projectile or an execution blow.
    /// </summary>
    public CombatHitSource Source { get; init; }
    /// <summary>
    /// The striker; a projectile's shooter may no longer resolve in the world.
    /// </summary>
    public CharacterId Attacker { get; init; }
    /// <summary>
    /// The character about to be hit.
    /// </summary>
    public CharacterId Victim { get; init; }
    /// <summary>
    /// The attack sequence for melee, the projectile id, or 0 for an execution (as <see cref="CombatHit.Sequence"/>).
    /// </summary>
    public int Sequence { get; init; }
    /// <summary>
    /// The melee blow index, or the execution job's blow count; -1 for a projectile.
    /// </summary>
    public int Blow { get; init; }
    /// <summary>
    /// The victim is the attacker's chosen target, not another body the sweep or projectile reached; always true for an execution.
    /// </summary>
    public bool Intended { get; init; }
    /// <summary>
    /// The damage as it leaves the weapon: cut, blunt, pierce, stun, bleed multiplier and armour penetration. A value the filter changes must be
    /// finite and, but for armour penetration, not negative. A melee blow's "hit dealt" experience follows the packet the filters leave. Ignored,
    /// though a cancel still holds, while the one-punch debug cheat makes the player's hits lethal.
    /// </summary>
    public DamagePacket Packet { get; set; }
    /// <summary>
    /// The hit does not land: a melee blow becomes a miss, a projectile flies on past the victim, an execution blow does no damage (the captive
    /// jobs that strike it keep striking, and the kill job's scream still plays, until one lands). Later filters are not asked.
    /// </summary>
    public bool Cancelled { get; set; }
}

/// <summary>
/// A system that may change or cancel every melee blow, bolt and execution blow before it lands (docs/modding.md "Hits before they land"):
/// called serially on the tick thread, in system order, each filter seeing the hit as the earlier ones left it. It may read the world and
/// change its own hashed state, and must not change characters or other world state or call combat; its answer must follow from the world
/// and the hit alone. Only <see cref="PendingHit.Packet"/> and <see cref="PendingHit.Cancelled"/> are taken back. A filter that throws, or changes
/// the packet against <see cref="PendingHit.Packet"/>'s rule, is undone for that hit and disabled for that world, and the fault enters the state
/// hash.
/// </summary>
public interface ICombatHitFilter : ITickSystem
{
    /// <summary>
    /// A hit is about to land; change <see cref="PendingHit.Packet"/> or set <see cref="PendingHit.Cancelled"/>.
    /// </summary>
    void HitLanding(World world, ref PendingHit hit);
}
