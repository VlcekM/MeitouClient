using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Bodies;

namespace Meitou.Simulation.Combat;

/// <summary>Orders the characters to fight <see cref="Target"/> (the AI's "attack" task, a player's attack command): they draw their weapon, close in and attack until one of them is down.</summary>
public sealed record AttackOrder(IReadOnlyList<CharacterId> Attackers, CharacterId Target) : SimCommand;

/// <summary>What a resolved blow did, for the log (a combat log, damage numbers, tests).</summary>
public readonly record struct CombatLogEntry(long Tick, int Attacker, int Defender, int Blow, BlowOutcome Outcome, int Part, float Cut, float Blunt, float Pierce, float Stun, bool Knockout, bool Death);

/// <summary>Settings of the <see cref="CombatSystem"/> that are the host's choice rather than the game's.</summary>
public sealed record CombatOptions
{
    /// <summary>The system closes the distance to the target itself, in a straight line (no navmesh). Off when a movement system does it.</summary>
    public bool SelfApproach { get; init; } = true;
    /// <summary>The system runs the medical tick (bleeding, healing, waking) of the characters that have a body. Off when a needs system does it.</summary>
    public bool TickMedical { get; init; } = true;
    /// <summary>Keep a <see cref="CombatSystem.Log"/> of every resolved blow.</summary>
    public bool RecordLog { get; init; }
    /// <summary>The speed (units per second) a character closes in at when it has no speed stat set.</summary>
    public float ApproachSpeed { get; init; } = 30;
}
