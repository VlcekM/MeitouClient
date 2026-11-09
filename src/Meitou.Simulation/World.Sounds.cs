using System.Numerics;

namespace Meitou.Simulation;

/// <summary>
/// What happened that the original gives a sound (docs/audio.md "Combat").
/// </summary>
public enum SoundKind : byte
{
    /// <summary>
    /// A melee blow was blocked: <c>Impact</c> with <c>Impacts</c> = <c>Parry</c> (or <c>HtH_Impacts</c>) on the defender.
    /// </summary>
    Blocked,
    /// <summary>
    /// A melee blow landed: <c>Impact</c> with <c>Impacts</c> = <c>Light_Hit</c> / <c>Heavy_Hit</c> (or <c>HtH_Impacts</c>) on the defender, and its
    /// pain voice.
    /// </summary>
    Hit,
    /// <summary>
    /// A crossbow bolt struck a body: <c>Impact</c> (<c>Deflection</c> when armour covers the spot) on the victim, and its pain voice.
    /// </summary>
    BoltHit,
    /// <summary>
    /// A crossbow bolt stuck in the ground: <c>Impact</c> at the spot, <c>Impacts</c> = the ground.
    /// </summary>
    BoltStuck,
    /// <summary>
    /// A crossbow was fired: <c>Attack</c> on the shooter.
    /// </summary>
    Shot,
    /// <summary>
    /// A character went down (knocked out or dead) by a blow or a bolt; <see cref="SoundEvent.Other"/> is who did it.
    /// </summary>
    Down,
    /// <summary>
    /// An AI character takes up a fight (the aggro system sends it at a target): an animal's <c>VO_Creature_Alert</c>;
    /// <see cref="SoundEvent.Subject"/> is the fighter, <see cref="SoundEvent.Other"/> its target.
    /// </summary>
    Alert,
    /// <summary>
    /// A character ate (<see cref="Items.FeedSystem"/>): <c>Character_Consume</c> with <c>Consume</c> = <c>Eat</c> (200 units).
    /// </summary>
    Eat,
    /// <summary>
    /// A lock opened for the subject (a pick that worked, a warden unlocking a door or cage): <c>Character_Unlock_Success</c>.
    /// </summary>
    Unlocked,
    /// <summary>
    /// The subject locked a door or cage: <c>Character_Lock</c>.
    /// </summary>
    Locked,
    /// <summary>
    /// The subject picked up a ground item (all or part of it fitted): <c>Character_Pick_Up_Ground</c> (<c>Task_Pickup</c>).
    /// </summary>
    PickedUp,
    /// <summary>
    /// The player put shackles on someone: the interface's <c>Item_Drop</c> with <c>Inventory_Items</c> of <see cref="SoundEvent.Item"/> (the exe's
    /// drag onto the armour slot).
    /// </summary>
    ShacklesOn,
    /// <summary>
    /// The player was paid a bounty: the interface's <c>Notifications</c> = <c>Bounty_Fulfilled</c>.
    /// </summary>
    BountyPaid,
    /// <summary>
    /// A building finished being taken apart: <c>Notifications</c> = <c>Thing_Complete</c>.
    /// </summary>
    DismantleDone,
    /// <summary>
    /// A turret fired (<see cref="SoundEvent.Position"/> its muzzle, the subject its gunner): <c>Attack</c> with <c>Weapons</c> = <c>Turret</c> on
    /// the turret (<c>FUN_14043a730</c>).
    /// </summary>
    TurretShot,
    /// <summary>
    /// A wall line was hit down to nothing (<see cref="SoundEvent.Position"/> the wall's): <c>Break</c> on the building
    /// (<c>WallBuilding::vfunc_62</c>).
    /// </summary>
    WallBroken,
}

/// <summary>
/// One sound-worthy happening for the host. Display only like <see cref="Floater"/>: the host decides what it sounds like, nothing in the simulation
/// reads it.
/// </summary>
/// <param name="Subject">The character it happens to (the defender, the victim, the shooter, the one who went down).</param>
/// <param name="Other">The one that caused it (the attacker, the shooter; none when unknown).</param>
/// <param name="Position">Where (the subject's position; the spot a bolt stuck at).</param>
/// <param name="Heavy">A heavy blow (it staggers the defender).</param>
/// <param name="AttackerUnarmed">The attacker fights with its body (martial arts, the animals' bites are armed with a creature weapon).</param>
/// <param name="AttackerRobot">The attacker's race is a robot.</param>
/// <param name="Armoured">A bolt: some armour covers the part it hit.</param>
/// <param name="Fatal">A <see cref="SoundKind.Down"/> that is a death, not a knock-out.</param>
/// <param name="Item">An item's record id (<see cref="SoundKind.ShacklesOn"/>: the shackles).</param>
public readonly record struct SoundEvent(SoundKind Kind, CharacterId Subject, CharacterId Other, Vector3 Position,
    bool Heavy = false, bool AttackerUnarmed = false, bool AttackerRobot = false, bool Armoured = false, bool Fatal = false, string? Item = null);

public sealed partial class World
{
    volatile System.Collections.Concurrent.ConcurrentQueue<SoundEvent>? _sounds;

    /// <summary>
    /// Where combat queues what the host should play (blocks, hits, shots, bolts, characters going down); null (the default) records none. The host
    /// sets it while it plays sounds and empties it every frame. Nothing in the simulation reads it, so it cannot change a run.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentQueue<SoundEvent>? Sounds { get => _sounds; set => _sounds = value; }

    /// <summary>
    /// The characters that parried a weapon blow, for the host's sparks at their weapon (docs/game/combat.md "Block sparks"); null (the default)
    /// records none. Display only: nothing in the simulation reads it.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentQueue<CharacterId>? BlockSparks { get; set; }

    /// <summary>
    /// Queues a sound-worthy happening for the host (nothing while <see cref="Sounds"/> is null); <paramref name="slot"/> is the character it happens
    /// to (-1 none). Display only.
    /// </summary>
    public void EmitSound(SoundKind kind, int slot, Vector3 at, string? item = null)
    {
        if (_sounds is { } queue)
            queue.Enqueue(new SoundEvent(kind, slot >= 0 ? Characters.IdOf(slot) : CharacterId.None, CharacterId.None, at, Item: item));
    }
}