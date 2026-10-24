using System.Numerics;
using Meitou.Data.World;

namespace Meitou.Simulation.Locks;

/// <summary>The original's <c>Character+0x2f8</c> (save key <c>in something</c>, docs/game/carry-loot.md): 0 normal, 1 in a bed, 2 imprisoned.</summary>
public enum HeldState : byte { None = 0, Bed = 1, Imprisoned = 2 }

/// <summary>What a <see cref="Fixture"/> is.</summary>
public enum FixtureKind : byte
{
    /// <summary>One door of a placed building (an entry of its <c>doors</c> list).</summary>
    Door,
    /// <summary>A BUILDING with <c>has lock</c> (safes, chests, lockers).</summary>
    Container,
    /// <summary>BUILDING_FUNCTIONALITY function 8: cages, the prisoner pole and the peeler (<see cref="Fixture.Restrains"/>).</summary>
    Cage,
    /// <summary>A bed of an interior layout, or one the player built; no lock.</summary>
    Bed,
}

/// <summary>A bed as the host picks it (<see cref="WorldSnapshot.Beds"/>): its fixture id, BUILDING record, where it is and its owner faction (-1: none).</summary>
public sealed record BedSpot(string Id, string Record, Vector3 Position, int Owner = -1, float Yaw = 0);

/// <summary>
/// What a bed's BUILDING says (docs/game/locks.md "Beds"): its functionality's <c>function</c> (6 BF_BED, 25 BF_SKELETON_BED), the clip it is lain in (the functionality's <c>animation</c>:
/// <c>sleepinbed</c>, <c>sleeponfloor</c>), the sum of its construction materials (the bed search weighs a bed that needs more than 1 three times) and its use nodes
/// (null with fewer than 2: it is lain in at its own origin and facing).
/// </summary>
public readonly record struct BedData(int Function, string? Clip, float Materials, BedNode[]? Nodes = null);

/// <summary>
/// A <c>node type</c> 4 (NODE_USE) instance of a bed's BUILDING, in the bed's own space: where a sleeper is put and the way it faces (the node's yaw - 90°; a node looks
/// along its local -X). <c>sleepinbed</c> holds <c>Bip01</c> about 12 units to the character's side, on the mattress (docs/game/locks.md "Beds").
/// </summary>
public readonly record struct BedNode(Vector3 At, float Yaw);

/// <summary>A container with an inventory as the host picks it: its fixture id, BUILDING record, position, owner faction (-1: none) and whether its lock is locked.</summary>
public sealed record ContainerSpot(string Id, string Record, Vector3 Position, int Owner = -1, bool Locked = false);

/// <summary>A door for the host's picking: its fixture id, its key in <see cref="WorldSnapshot.Doors"/>, its owner faction, where its closed leaf is (else its building's origin) and how far it slides fully open (<see cref="DoorSpot.Slide"/>).</summary>
/// <summary>A cage for picking: its fixture id, CAGE-function BUILDING record, place, owner faction (-1 none), whether its lock holds and who is in it (None: free).</summary>
public sealed record CageSpot(string Id, string Record, Vector3 Position, int Owner, bool Locked, CharacterId Occupant);

public sealed record DoorPick(string Id, string Building, int Door, int Owner, Vector3 Position, Vector3 Slide = default);

/// <summary>The door's motion state as saved in <c>state&lt;i&gt;</c> (docs/game/locks.md "Doors"; not the DoorState enum): 0 closed, 1 open, 2 opening, 3 closing.</summary>
public enum DoorMotion : byte { Closed = 0, Open = 1, Opening = 2, Closing = 3 }

/// <summary>
/// The lock object of docs/game/locks.md "Lock object": one structure for doors, lockable buildings, cages and shackles. The pick timer lives on the lock, as in
/// the original; <see cref="Rolls"/> counts the rolls made on it, the counter of its stateless random stream.
/// </summary>
public sealed class LockState
{
    /// <summary>The first pick roll comes after this many seconds; a failed one draws the next wait uniform in [<see cref="RetryMin"/>, <see cref="RetryMax"/>].</summary>
    public const float FirstRoll = 5, RetryMin = 5, RetryMax = 20;

    /// <summary>0..100.</summary>
    public int Level { get; set; }
    /// <summary>Resistance to cutting and breaking.</summary>
    public float Hardness { get; set; }
    public bool Locked { get; set; }
    public bool Broken { get; set; }
    /// <summary>Seconds of picking since the last roll, and the time of the next roll.</summary>
    public float PickTime { get; set; }
    public float NextRoll { get; set; } = FirstRoll;
    public int Rolls { get; set; }

    /// <summary>Locked flag set, not broken and level at least 1 (the owner's destruction is not modelled): a broken or level-0 lock is open whatever its flag.</summary>
    public bool EffectivelyLocked => Locked && !Broken && Level >= 1;

    internal void Hash(ref StateHasher h)
    {
        h.Add(Level); h.Add(Hardness); h.Add(Locked); h.Add(Broken); h.Add(PickTime); h.Add(NextRoll); h.Add(Rolls);
    }
}

/// <summary>
/// A door, lockable container, cage or bed of a placed building, in the world's <see cref="FixtureRegistry"/>. <see cref="Id"/>: the building's instance id for its own lock
/// (a placed cage or safe), <c>&lt;instance&gt;#&lt;i&gt;</c> for its door i, the <see cref="Meitou.Data.World.TownFixture.Id"/> for an interior cage or container, and
/// <c>&lt;instance&gt;@bed&lt;k&gt;</c> for its k-th bed (engine names).
/// </summary>
public sealed class Fixture
{
    public required string Id { get; init; }
    /// <summary>The instance id of the placed building it belongs to (the house of a door, the building of an interior item): what <see cref="IDoorGate"/> and the save name.</summary>
    public required string Building { get; init; }
    public required FixtureKind Kind { get; init; }
    /// <summary>The BUILDING record (the door's own record for a door).</summary>
    public string RecordId { get; init; } = "";
    /// <summary>The town site it was registered with, and the owner faction (a position in the data's factions; -1 none).</summary>
    public int Site { get; init; } = -1;
    public int Owner { get; set; } = -1;
    /// <summary>Where an occupant is held, and which way it faces (the item's origin: the cage's attach node is not read, an engine choice).</summary>
    public Vector3 Position { get; init; }
    public float Yaw { get; init; }
    /// <summary>Null for beds, and for a container with no <c>has lock</c>.</summary>
    public LockState? Lock { get; init; }
    /// <summary>A container's own inventory (<c>has inventory</c>; docs/game/locks.md "Containers"): null for the rest. Starts empty (what the original puts in is <b>Unknown</b>).</summary>
    public Items.Inventory? Contents { get; init; }

    /// <summary>Doors: the index in the building's <c>doors</c> list (the save's suffix), the motion state, the open amount 0..1, the door's own broken flag, <c>gatecode</c> (kept as read) and <c>wants lock</c>.</summary>
    public int DoorIndex { get; init; } = -1;
    public DoorMotion Motion { get; set; }
    public float OpenAmount { get; set; }
    public bool DoorBroken { get; set; }
    public int GateCode { get; set; } = -1;
    public bool WantsLock { get; set; }
    /// <summary>Doors: how the leaf moves (its record's <c>door move speed</c> etc.), and the parent BUILDING record (the door's position is looked up by it and <see cref="DoorIndex"/>; a player building's bed has it too, so an upgrade is noticed).</summary>
    public DoorLeaf Leaf { get; init; }
    public string HostRecordId { get; init; } = "";
    /// <summary>Doors: where the leaf is in the world, once the door gate has told it (<see cref="FixtureRegistry.DoorSpotOf"/>); derived from the data, not hashed or saved.</summary>
    internal DoorSpot? Spot { get; set; }
    internal bool SpotAsked { get; set; }

    /// <summary>Cages: the functionality's <c>restrains movement</c> (pole, peeler). Cages and beds: <c>use range</c>, and who is in it.</summary>
    public bool Restrains { get; init; }
    public float UseRange { get; init; }
    public CharacterId Occupant { get; set; } = CharacterId.None;
    /// <summary>Beds: its record's <see cref="BedData"/>; derived from the data, not hashed or saved.</summary>
    public BedData Bed { get; init; }
    /// <summary>Beds: inside a building (an interior's bed; the bed search weighs it twice), not one standing on its own.</summary>
    public bool Indoors { get; init; }

    /// <summary>Its state came from a loaded save (the squad's <c>initial door state</c> then does not apply).</summary>
    public bool Restored { get; set; }

    /// <summary>Doors: a walker opened it (Meitou: it closes again once nobody has gone through it for a while, <see cref="LockSystem.DoorsCloseBehind"/>), and the tick a character last
    /// went through or stood in it. Engine state, not saved; hashed only while set.</summary>
    public bool AutoOpened { get; set; }
    public long LastPass { get; set; }

    internal void Hash(ref StateHasher h)
    {
        h.Add(Rng.StableHash(Id)); h.Add((int)Kind); h.Add(Owner);
        if (Lock is { } l) l.Hash(ref h); else h.Add(-1);
        if (Kind == FixtureKind.Door) { h.Add((int)Motion); h.Add(OpenAmount); h.Add(DoorBroken); h.Add(WantsLock); if (AutoOpened) h.Add(LastPass); }
        h.Add(Occupant);
        Contents?.Hash(ref h);
    }
}

/// <summary>
/// A door as the snapshot shows it (<see cref="WorldSnapshot.Doors"/>): its open amount 0..1, motion, whether it is effectively locked and whether it wants to lock; for the sounds,
/// whether it is broken, where it is (the leaf's centre once known, else its building's origin) and its own BUILDING record (<c>door type</c>).
/// </summary>
public readonly record struct DoorView(float Open, DoorMotion Motion, bool Locked, bool WantsLock, bool Broken = false, Vector3 Position = default, string Record = "");

/// <summary>
/// The doors of the building the HUD shows (<see cref="InspectBuildingCommand"/>), in the building's door order: what the panel's door lines say; whether a fixture of it is the player's
/// (<paramref name="Owned"/>: a town building the player owns gets the panel's buttons) and its Open to public flag as the player set it (null: never set).
/// </summary>
public sealed record BuildingDetail(string Instance, IReadOnlyList<DoorDetail> Doors, bool Owned = false, bool? Public = null)
{
    public long? SalePrice { get; init; }
}

/// <summary>
/// One door for the panel: open (open or opening: where it is heading), effectively locked, whether it wants its lock, its lock's level (0..100), whether the door is broken, and its
/// BUILDING record (its Door Condition row).
/// </summary>
public readonly record struct DoorDetail(bool Open, bool Locked, int Level, bool Broken, string Record = "", bool WantsLock = false);

/// <summary>Where the simulation tells navigation that a building's doors are shut for paths (the game host forwards it to the navmesh's door table). Serial phases only.</summary>
public interface IDoorGate
{
    /// <summary><paramref name="blocked"/>: every door of the building instance is effectively locked (and none broken), so paths may not cross them.</summary>
    void SetDoorsBlocked(string buildingInstance, bool blocked);
    /// <summary>Opens every door again (the world was replaced).</summary>
    void ResetDoors();
    /// <summary>
    /// Where door <paramref name="doorIndex"/> of a BUILDING record is, in the building's frame (scaled, not rotated or moved; from the door's collision, as the navmesh
    /// paints it); null when not known. Pure data: the same answer every time.
    /// </summary>
    DoorSpot? DoorSpotOf(string buildingRecordId, int doorIndex) => null;
}

/// <summary>
/// A door leaf's closed box: its centre, the horizontal unit normal of its thinnest side (the way through), half its width along the wall and half its height (0: not known).
/// <see cref="Slide"/> is how far the leaf moves when fully open (<c>DoorLeaf.LocalOffset</c> at 1), in the same frame.
/// </summary>
public readonly record struct DoorSpot(Vector3 Centre, Vector3 Normal, float HalfWidth, float HalfHeight = 0)
{
    public Vector3 Slide { get; init; }

    /// <summary>The height of the leaf's foot, where a walker through it stands (its centre when the height is not known).</summary>
    public float Foot => Centre.Y - HalfHeight;
}
