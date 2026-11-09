using Meitou.Simulation.Items;
using Meitou.Simulation.Player;

namespace Meitou.Simulation.Production;

/// <summary>
/// Sends characters (the selection when empty) to work a complete player building: operate a well, harvest a farm, research at a bench; on a storage,
/// one haul trip there.
/// </summary>
public sealed record OperateOrder(IReadOnlyList<CharacterId> Characters, string Site) : OrderCommand;

/// <summary>
/// Gives characters (the selection when empty) the job an order of <paramref name="Type"/> on a player building becomes, at the end of their job lists (the original's Shift with an order;
/// <see cref="PermaJobs.Add"/>).
/// </summary>
public sealed record AddJob(IReadOnlyList<CharacterId> Characters, Meitou.Data.Gameplay.Ai.TaskType Type, string Site) : SimCommand;

/// <summary>
/// Gives characters (the selection when empty) the job an order of <paramref name="Ordered"/> on a character becomes, untargeted, at the end of their job lists (Shift with First Aid,
/// Splint Injuries, an attack or a loot; <see cref="PermaJobs.Add"/>): 25 → 58 Medic (60 → 57 Robotics, which a first aid on a robot <paramref name="Target"/> is), 249 → 250 Splint
/// Rigging, 4 / 5 / 61 → 16 Attacking enemies, 26 → 238 Foraging animals.
/// </summary>
public sealed record AddOrderJob(IReadOnlyList<CharacterId> Characters, Meitou.Data.Gameplay.Ai.TaskType Ordered, CharacterId Target) : SimCommand;

/// <summary>
/// The original's order 146 USE_TURRET on a complete turret of the player's (function 12): the nearest of the characters (the selection when empty) that can act operates it, standing at
/// its gunner spot (<see cref="Ai.AiSystem.TurretSpot"/>); the combat system fires it (docs/simulation.md "Production", "Combat").
/// </summary>
public sealed record UseTurretOrder(IReadOnlyList<CharacterId> Characters, string Site) : OrderCommand;

/// <summary>
/// The original's order 136 FEED_CORPSE_INTO_MACHINE without Shift (Corpse Disposal on a corpse machine): each of the characters carrying a dead body takes it to an incinerator as the
/// corpse disposal job does. <b>Engine reading</b>: the order's condition 99 is taken as carrying a dead body, and the body goes to the nearest free incinerator, as the job's.
/// </summary>
public sealed record DisposeCorpseOrder(IReadOnlyList<CharacterId> Characters, string Site) : OrderCommand;

/// <summary>
/// Takes the job at <paramref name="Index"/> off a character's job list (the list's X button).
/// </summary>
public sealed record RemoveJob(CharacterId Character, int Index) : SimCommand;

/// <summary>
/// Moves a character's job from <paramref name="From"/> to <paramref name="To"/> in its list (the list's rows are dragged to reorder, the original's
/// tutorial).
/// </summary>
public sealed record MoveJob(CharacterId Character, int From, int To) : SimCommand;

/// <summary>
/// A character puts an item of its own into a machine: <paramref name="Slot"/> k into recipe input k (as many units as the input still takes), -1 into the storage grid at
/// (<paramref name="X"/>, <paramref name="Y"/>), or with X below 0 where it first fits (stacks of it first, <see cref="Inventory.Add"/>). <paramref name="Count"/> above 0 moves at most that
/// many units (a right click moves one), 0 the whole stack. The character must stand within <see cref="ProductionSettings.TransferReach"/> of the building.
/// </summary>
public sealed record PutInMachine(CharacterId Character, InventoryItemToken Item, string Site, int Slot, int X = 0, int Y = 0, int Count = 0) : SimCommand;

/// <summary>
/// A character takes from a machine: <paramref name="Slot"/> -2 the output's items, k the items of input k, -1 the storage item at <paramref name="Index"/>; into its inventory where they first
/// fit (a storage item as the right click's move, <see cref="InventoryCommands.MoveInto"/>: its open bag first with <see cref="BagFirst"/>). <paramref name="Count"/> above 0 takes at most that
/// many units, 0 all. Dragging goes through the held item (<see cref="LiftItem"/>).
/// </summary>
public sealed record TakeFromMachine(CharacterId Character, string Site, int Slot, int Index = 0, int Count = 0) : SimCommand
{
    public bool BagFirst { get; init; }
}

/// <summary>
/// Queues a research (paying its cost from the research benches when it was never paid).
/// </summary>
public sealed record QueueResearch(string Research) : SimCommand;

/// <summary>
/// Takes a research off the queue; its progress is kept for when it is queued again.
/// </summary>
public sealed record CancelResearch(string Research) : SimCommand;

/// <summary>
/// Queues a craft at a crafting bench: an option's key (<see cref="Meitou.Data.Gameplay.Construction.CraftOption.Key"/>), <paramref name="Count"/>
/// times.
/// </summary>
public sealed record QueueCraft(string Site, string Option, int Count = 1) : SimCommand;

/// <summary>
/// Takes the craft at <paramref name="Index"/> off a bench's queue (its progress is lost, as the original).
/// </summary>
public sealed record CancelCraft(string Site, int Index) : SimCommand;

/// <summary>
/// Sets a bench's repeat flag: a finished craft goes back to the end of the queue.
/// </summary>
public sealed record RepeatCrafts(string Site, bool Repeat) : SimCommand;

/// <summary>
/// A machine's Power toggle (<c>power on</c>) or, for a consumer, its Battery toggle (<c>gets battery</c>); null leaves one as it is.
/// </summary>
public sealed record SetMachinePower(string Site, bool? PowerOn = null, bool? GetsBattery = null) : SimCommand;

/// <summary>
/// An item furnace's Active button: starts burning its grid, or stops it (<c>FurnaceBuilding::incinerate</c>).
/// </summary>
public sealed record IncinerateFurnace(string Site) : SimCommand;