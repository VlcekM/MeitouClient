using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Combat;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Combat;

/// <summary>
/// Orders the characters to fight <see cref="Target"/> (the AI's "attack" task, a player's attack command): they draw their weapon, close in and
/// attack until one of them is down.
/// </summary>
public sealed record AttackOrder(IReadOnlyList<CharacterId> Attackers, CharacterId Target) : OrderCommand
{
    /// <summary>
    /// A fight the attackers did not pick: an answer to being attacked (<see cref="RetaliationSystem"/>). The player's defence does not count as an
    /// assault on the other faction.
    /// </summary>
    public bool Defensive { get; init; }

    /// <summary>
    /// The player picked this target (a right click, Attack Target, Attack Unprovoked): the attackers keep it rather than turning on one that lost
    /// the initiative roll to them.
    /// </summary>
    public bool Picked { get; init; }

    /// <summary>
    /// The player's Attack Unprovoked (row 61): the only attack that books an Assault and marks the attacker ordered (<c>Task_FocusedMeleeAttack</c>
    /// flag, <b>Verified</b>); Attack Target (5) and the AI's fights do neither. Needs <see cref="Picked"/>.
    /// </summary>
    public bool Unprovoked { get; init; }

    /// <summary>
    /// The chooser's source the fight stands in for (<see cref="TaskStart.Start"/>); null: an order when <see cref="Picked"/>, a reaction when
    /// <see cref="Defensive"/>, else a goal.
    /// </summary>
    public TaskSource? Source { get; init; }

    /// <summary>
    /// The job of the attackers' lists the fight runs (<see cref="TaskRequest.Job"/>; -1: none).
    /// </summary>
    public int Job { get; init; } = -1;
}

/// <summary>
/// Shoot At (order 235, offered to a character on a turret): the gunners (the selection when empty) that man a turret fire at <see cref="Target"/>
/// instead of choosing; the turret keeps the target while it scores for it and is no friend, and waits while it is out of range; otherwise it chooses
/// itself (<see cref="CharacterCold.TurretTarget"/>).
/// </summary>
public sealed record ShootAtOrder(IReadOnlyList<CharacterId> Gunners, CharacterId Target) : OrderCommand;

/// <summary>
/// What a resolved blow did, for the log (a combat log, damage numbers, tests).
/// </summary>
public readonly record struct CombatLogEntry(
    long Tick,
    int Attacker,
    int Defender,
    int Blow,
    BlowOutcome Outcome,
    int Part,
    float Cut,
    float Blunt,
    float Pierce,
    float Stun,
    bool Knockout,
    bool Death)
{
    /// <summary>
    /// A crossbow bolt (blow 0), and whether it hit the character it was shot at (false: a friend or another enemy in the way).
    /// </summary>
    public bool Ranged { get; init; }

    /// <summary>
    /// Whether the bolt hit the character it was shot at (false: a friend or another enemy in the way).
    /// </summary>
    public bool Intended { get; init; }
}

/// <summary>
/// Settings of the <see cref="CombatSystem"/> that are the host's choice rather than the game's.
/// </summary>
public sealed record CombatOptions
{
    /// <summary>
    /// The system closes the distance to the target itself, in a straight line (no navmesh). Off when a movement system does it.
    /// </summary>
    public bool SelfApproach { get; init; } = true;

    /// <summary>
    /// The system runs the medical tick (bleeding, healing, waking) of the characters that have a body. Off when a needs system does it.
    /// </summary>
    public bool TickMedical { get; init; } = true;

    /// <summary>
    /// Keep a <see cref="CombatSystem.Log"/> of every resolved blow.
    /// </summary>
    public bool RecordLog { get; init; }

    /// <summary>
    /// The speed (units per second) a character closes in at when it has no speed stat set.
    /// </summary>
    public float ApproachSpeed { get; init; } = 30;

    /// <summary>
    /// How many enemies may melee one target at once (before the race's extra slots); null: CONSTANTS <c>max num attack slots</c> (1).
    /// </summary>
    public int? AttackSlots { get; init; }

    /// <summary>
    /// A dodge or block that passed its roll but starts too late joins its clip partway so it still counts (Meitou balance choice); false: the game's
    /// rule, it fails.
    /// </summary>
    public bool LateDefences { get; init; }

    /// <summary>
    /// A turret's gunner faces along the barrel as the gun turns (Meitou fix); false: the game's facing, toward the turret.
    /// </summary>
    public bool GunnerFacesBarrel { get; init; }

    /// <summary>
    /// A fist attack with a small negative attack distance ("ma badpunch") reads it as positive (Meitou fix); false: as written, as the game does.
    /// </summary>
    public bool PunchReachFix { get; init; }

    /// <summary>
    /// Most bolts left stuck in the world per bolt mesh (the original's <c>harpoonLimit</c> setting, 10 to 10000).
    /// </summary>
    public int StuckLimit { get; init; } = RangedTuning.DefaultStuckLimit;
}
