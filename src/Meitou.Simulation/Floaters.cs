using System.Numerics;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Combat;

namespace Meitou.Simulation;

public enum FloaterKind : byte
{
    Damage,
    /// <summary>A line of text (<see cref="Floater.Part"/>), white: "Ate {1}" over a character that ate (<c>FUN_1405d0780</c>).</summary>
    Text,
    /// <summary>A machine's label (<see cref="Floater.Part"/>) at a fixed point, grey: its functionality's <c>string</c> when a body is fed into it (<c>FUN_1403567a0</c>).</summary>
    Label,
    /// <summary>A stealth knock-out's "*Thunk*" (<see cref="Floater.Part"/>) over the attacker, green (<c>Task_StealthKO</c> <c>FUN_14034cd80</c>).</summary>
    StealthHit,
    /// <summary>A stealth knock-out's "Failed!" (<see cref="Floater.Part"/>) over the attacker, red-grey (<c>FUN_14034cd80</c>).</summary>
    StealthMiss,
    /// <summary>A building's damage number (<see cref="Floater.Part"/>, the int of the clamped damage) at the point hit, grey (<c>FUN_14029d2b0</c>: a door's bashing blow; a wall's is not built).</summary>
    Building,
    /// <summary>A lock pick roll's "Pick success!" (<see cref="Floater.Part"/>) over the picker, in the stealth hit's green (Task_PickLock update <c>FUN_140359160</c>).</summary>
    PickSuccess,
    /// <summary>A lock pick roll's "Pick failed" (<see cref="Floater.Part"/>) over the picker, in the stealth miss's red-grey (<c>FUN_140359160</c>).</summary>
    PickFailed,
}

/// <summary>
/// A number to float over a character (docs/game/ui-screens.md "Damage floaters"): the damage a blow did, with the armour's share.
/// Display only: the world queues these for the host (<see cref="World.Floaters"/>) and nothing in the simulation reads them.
/// </summary>
/// <param name="Part">The name of the part hit, or the text of a <see cref="FloaterKind.Text"/>.</param>
/// <param name="Damage">What the medical system took, after armour, toughness and the difficulty multiplier.</param>
/// <param name="Absorbed">What the armour alone stopped.</param>
public readonly record struct Floater(FloaterKind Kind, CharacterId Target, Vector3 Position, bool TargetIsPlayer, bool SourceIsPlayer, string Part, HitDamage Damage, ArmourAbsorbed Absorbed)
{
    /// <summary>The active squad's memory holds TEMP ENEMY / TEMP ALLY on the target (the original's tint tests, <c>FUN_140678830</c>); display only.</summary>
    public bool TargetTempEnemy { get; init; }
    public bool TargetTempAlly { get; init; }

    public static Floater Hit(CharacterId target, Vector3 position, bool targetIsPlayer, bool sourceIsPlayer, string part, in HitDamage damage, in ArmourAbsorbed absorbed) =>
        new(FloaterKind.Damage, target, position, targetIsPlayer, sourceIsPlayer, part, damage, absorbed);

    public static Floater Text(CharacterId target, Vector3 position, string text) =>
        new(FloaterKind.Text, target, position, false, false, text, default, default);

    public static Floater Strike(CharacterId attacker, Vector3 position, string text, bool hit) =>
        new(hit ? FloaterKind.StealthHit : FloaterKind.StealthMiss, attacker, position, false, false, text, default, default);

    public static Floater Pick(CharacterId picker, Vector3 position, string text, bool success) =>
        new(success ? FloaterKind.PickSuccess : FloaterKind.PickFailed, picker, position, false, false, text, default, default);

    public static Floater BuildingHit(Vector3 position, int amount) =>
        new(FloaterKind.Building, CharacterId.None, position, false, false, amount.ToString(System.Globalization.CultureInfo.InvariantCulture), default, default);

    public static Floater Label(Vector3 position, string text) => new(FloaterKind.Label, CharacterId.None, position, false, false, text, default, default);
}

public sealed partial class World
{
    volatile System.Collections.Concurrent.ConcurrentQueue<Floater>? _floaters;

    /// <summary>
    /// Where hits and texts queue their floaters for the host to draw; null (the default) records none. The host sets it while it shows floaters and empties it every frame.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentQueue<Floater>? Floaters { get => _floaters; set => _floaters = value; }
}