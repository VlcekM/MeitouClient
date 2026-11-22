namespace Meitou.Simulation.Combat;

/// <summary>
/// How a melee blow was stopped.
/// </summary>
public enum CombatDefenceKind : byte
{
    /// <summary>
    /// The defender blocked it (a block technique, or a dodge technique that counts as a block, the base game's "Dodge back" rows).
    /// </summary>
    Blocked,

    /// <summary>
    /// The defender dodged it.
    /// </summary>
    Dodged,
}

/// <summary>
/// A melee blow the defender stopped, as the combat log records it (docs/modding.md "Defences").
/// </summary>
/// <param name="Tick">The tick the blow was decided.</param>
/// <param name="Attacker">The character that swung.</param>
/// <param name="Defender">The character that stopped it.</param>
/// <param name="Sequence">The attack's sequence number, as <see cref="CombatHit.Sequence"/>.</param>
/// <param name="Blow">The blow of the attack (1 based), as <see cref="CombatHit.Blow"/>.</param>
/// <param name="Kind">Blocked or dodged.</param>
/// <param name="Parry">A block with a weapon against a blow (the sparks' parry): false for a dodge, a block with fists or a dodge-back row.</param>
/// <param name="Intended">The defender was the attacker's chosen target.</param>
public readonly record struct CombatDefence(
    long Tick, CharacterId Attacker, CharacterId Defender, int Sequence, int Blow, CombatDefenceKind Kind, bool Parry, bool Intended);

/// <summary>
/// A system told of every melee blow a defender blocked or dodged, after its counts, XP, sound and log entry (docs/modding.md "Defences").
/// Called serially in Commit in system order (mods in load order). It may read the world and change its own state, and must not change
/// characters, items or other world state in the call (gameplay changes go through commands). One that throws is disabled for that world, and
/// the fault enters the state hash. Engine API, not in the original.
/// </summary>
public interface ICombatDefenceObserver : ITickSystem
{
    /// <summary>
    /// A blow was blocked or dodged.
    /// </summary>
    void Defended(World world, in CombatDefence defence);
}
