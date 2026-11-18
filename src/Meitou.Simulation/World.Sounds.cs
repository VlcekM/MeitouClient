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
    /// <summary>
    /// The subject opened a container's window (the start of <c>Task_Loot_Order</c> on a target that is no character, <c>FUN_140354dc0</c>):
    /// <c>Character_Inspect</c> on it.
    /// </summary>
    Inspected,
    /// <summary>
    /// A shopper bought a unit for more than nothing (<c>FUN_1409a14b0</c>): <c>Sell_Item</c> on the shopper, 200 units.
    /// </summary>
    NpcBought,
    /// <summary>
    /// A jailer's release freed a prisoner of the player's faction who is no slave (<c>Task_ReleasePrisoner</c>, <c>FUN_1403570a0</c>): the
    /// interface's <c>Create_Character</c>.
    /// </summary>
    ReleasedFromPrison,
    /// <summary>
    /// A carried body was fed into a machine (<c>Task_FeedCorpseIntoMachine</c>, <c>FUN_1403567a0</c>; the subject is the body, gone from the world
    /// by the time it is played): <c>Character_Drop_Person</c> on the body with its <c>Armor_Worn</c>, <see cref="SoundEvent.Armour"/>.
    /// </summary>
    FedToMachine,
    /// <summary>
    /// A code mod's named event (<see cref="World.PostSound(string, CharacterId, string?, string?)"/>, <see cref="SoundEvent.Posted"/>): on the
    /// subject when there is one (the host keeps a switch off the subject's own object), else at <see cref="SoundEvent.Position"/>.
    /// </summary>
    Posted,
    /// <summary>
    /// A code mod's named event on the interface's object (<see cref="World.PostInterfaceSound"/>, <see cref="SoundEvent.Posted"/>), heard the
    /// same wherever the camera is.
    /// </summary>
    PostedInterface,
}

/// <summary>
/// A named sound event a code mod posts (<see cref="World.PostSound(string, CharacterId, string?, string?)"/>): the event's name in the loaded
/// banks (the game's, or a bank a mod's <c>RE_Kenshi.json</c> <c>SoundBanks</c> loads), and a switch set on the object first (both null: none).
/// </summary>
public sealed record PostedSound(string Event, string? SwitchGroup = null, string? SwitchValue = null);

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
/// <param name="Armour">The subject's body armour <c>material type</c> (0 cloth .. 3 plate) when it happened
/// (<see cref="SoundKind.FedToMachine"/>).</param>
public readonly record struct SoundEvent(SoundKind Kind, CharacterId Subject, CharacterId Other, Vector3 Position,
    bool Heavy = false, bool AttackerUnarmed = false, bool AttackerRobot = false, bool Armoured = false, bool Fatal = false, string? Item = null,
    int Armour = 0)
{
    /// <summary>
    /// A code mod's event (<see cref="SoundKind.Posted"/>, <see cref="SoundKind.PostedInterface"/>); null for the game's own kinds.
    /// </summary>
    public PostedSound? Posted { get; init; }
}

/// <summary>
/// The world's sound events for the host.
/// </summary>
public sealed partial class World
{
    /// <summary>
    /// ARMOUR <c>slot</c> of body armour (ATTACH_BODY), the slot <c>FUN_1405d28b0</c> reads.
    /// </summary>
    const int BodyArmourSlot = 5;

    /// <summary>
    /// The sound events' queue (see <see cref="Sounds"/>).
    /// </summary>
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
    public void EmitSound(SoundKind kind, int slot, Vector3 at, string? item = null, int armour = 0)
    {
        if (_sounds is not { } queue) return;

        var subject = slot >= 0 ? Characters.IdOf(slot) : CharacterId.None;
        queue.Enqueue(new SoundEvent(kind, subject, CharacterId.None, at, Item: item, Armour: armour));
    }

    /// <summary>
    /// A code mod plays the named event <paramref name="eventName"/> on the character <paramref name="on"/> (docs/modding.md "Sounds"), with the
    /// switch <paramref name="switchGroup"/> at <paramref name="switchValue"/> when both are given (set on an object of the mods' own at the
    /// character's place, never on the character's). Display only: queued for the host like the game's own sounds (nothing while
    /// <see cref="Sounds"/> is null, as without audio), never read by the simulation; the host plays it while the character is near enough to have
    /// a sound object. Callable from any thread. Throws for an empty name; an empty <paramref name="on"/> plays nothing.
    /// </summary>
    public void PostSound(string eventName, CharacterId on, string? switchGroup = null, string? switchValue = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventName);
        if (on.IsNone) return;

        PostModSound(SoundKind.Posted, eventName, on, Vector3.Zero, switchGroup, switchValue);
    }

    /// <summary>
    /// A code mod plays the named event <paramref name="eventName"/> at the world point <paramref name="at"/>, on a pooled object placed there,
    /// with an optional switch; otherwise as <see cref="PostSound(string, CharacterId, string?, string?)"/>.
    /// </summary>
    public void PostSound(string eventName, Vector3 at, string? switchGroup = null, string? switchValue = null) =>
        PostModSound(SoundKind.Posted, eventName, CharacterId.None, at, switchGroup, switchValue);

    /// <summary>
    /// A code mod plays the named event <paramref name="eventName"/> on the interface's object (heard alike wherever the camera is), with an
    /// optional switch; otherwise as <see cref="PostSound(string, CharacterId, string?, string?)"/>.
    /// </summary>
    public void PostInterfaceSound(string eventName, string? switchGroup = null, string? switchValue = null) =>
        PostModSound(SoundKind.PostedInterface, eventName, CharacterId.None, Vector3.Zero, switchGroup, switchValue);

    /// <summary>
    /// Queues a code mod's event (<see cref="SoundKind.Posted"/> or <see cref="SoundKind.PostedInterface"/>); a switch with only one of its two
    /// names is left out. Throws for an empty name or a point that is not finite.
    /// </summary>
    void PostModSound(SoundKind kind, string eventName, CharacterId on, Vector3 at, string? switchGroup, string? switchValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventName);
        if (!float.IsFinite(at.X + at.Y + at.Z)) throw new ArgumentOutOfRangeException(nameof(at), at, "A sound's point must be finite.");
        if (_sounds is not { } queue) return;

        bool hasSwitch = !string.IsNullOrEmpty(switchGroup) && !string.IsNullOrEmpty(switchValue);
        var posted = hasSwitch ? new PostedSound(eventName, switchGroup, switchValue) : new PostedSound(eventName);
        queue.Enqueue(new SoundEvent(kind, on, CharacterId.None, at) { Posted = posted });
    }

    /// <summary>
    /// The <c>material type</c> of the heaviest worn body armour (ARMOUR <c>slot</c> 5) of the character in <paramref name="slot"/>, 0 (cloth) with
    /// none: the <c>Armor_Worn</c> value the exe keeps on the character (<c>+0x68c</c>, refreshed by <c>FUN_1405d28b0</c> whenever an item is put
    /// on or taken off, Character slots 51 and 52). Display only.
    /// </summary>
    public int BodyArmourMaterial(int slot)
    {
        if (Characters.Cold(slot)?.Fighter is not { } fighter) return 0;

        int material = 0;
        foreach (var piece in fighter.Armour)
            if (piece.Data.Slot == BodyArmourSlot) material = Math.Max(material, (int)piece.Data.Material);

        return material;
    }
}