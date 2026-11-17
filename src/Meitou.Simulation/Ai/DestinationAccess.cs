using System.Numerics;
using Meitou.Data.Gameplay.Ai;
using Meitou.Simulation.Locks;

namespace Meitou.Simulation.Ai;

/// <summary>
/// States 59 DESTINATION_IS_ACCESSIBLE and 168 DESTINATION_IS_ACCESSIBLE_ANIMAL (<c>FUN_140597030</c>, <b>Verified</b>) with their providers folded
/// in, for any character: the AI's planner (<see cref="AiSystem"/>'s plan context) and the engine's walks (<see cref="TaskStart.Walk(World, int,
/// Vector3, byte, TaskType, TaskSource)"/>) ask the same test (docs/game/locks.md "Locked doors on the way"). Pure reads, any thread.
/// </summary>
public static class DestinationAccess
{
    #region Accessible

    /// <summary>
    /// Whether the character in <paramref name="slot"/> can get into <paramref name="building"/> (null: outdoors). Accessible when there is none,
    /// when it is the one the character stands in, or when its doors let the character in: an animal it keeps out (<see cref="KeptOut"/>) gets the
    /// state's default (<paramref name="animalDefault"/>: false for 59, true for 168); else a door open, opening or broken (<c>hasAnOpenDoor</c>),
    /// or the chain the planner would add (85 Open destination door, 72 Opening door, 78 Unlocking door, 81 Bashing door) when the character's
    /// repertoire registers it: a closed unlocked door is opened as it passes (72, the movement's), a locked one unlocked or bashed by its way
    /// through (<see cref="LockSystem.WayThrough"/>, the lock system's); cut or picked (285, 76) only where the restricted rows are open
    /// (<paramref name="restricted"/>).
    /// </summary>
    public static bool Accessible(World world, int slot, string? building, bool animalDefault, bool restricted)
    {
        if (building is null || building == world.Sight.BuildingOf(slot)) return true;

        var cold = world.Characters.Cold(slot)!;
        if (KeptOut(world, slot, cold, building)) return animalDefault;
        if (world.Fixtures.HasOpenDoor(building)) return true;

        // 85 and 72 are registered 0x41, 78 0x45, 81 0x21, 285 and 76 0x55 (FUN_140319a60).
        int category = Repertoire.Of(world, cold);
        if ((category & 0x41) == 0) return false;
        if (!world.Fixtures.IsShut(building)) return true;
        if (world.SystemOf<LockSystem>() is not { } locks) return false;

        return locks.WayThrough(world, slot, building) switch
        {
            LockSystem.DoorWayKind.Unlock => (category & 0x45) != 0,
            LockSystem.DoorWayKind.Bash => (category & 0x21) != 0,
            LockSystem.DoorWayKind.Pick or LockSystem.DoorWayKind.Cut => restricted && (category & 0x55) != 0,
            _ => false,
        };
    }

    /// <summary>
    /// State 59's own test with its default false and no provider folded in (as state 131 asks it): the building the character stands in, or one that
    /// does not keep it out and has a door open.
    /// </summary>
    public static bool OpenTo(World world, int slot, string building)
    {
        if (building == world.Sight.BuildingOf(slot)) return true;

        return !KeptOut(world, slot, world.Characters.Cold(slot)!, building) && world.Fixtures.HasOpenDoor(building);
    }

    /// <summary>
    /// Whether <paramref name="building"/> keeps the character out as an animal (<see cref="AnimalEntry"/>): by its first door's flags, else by its
    /// BUILDING record.
    /// </summary>
    public static bool KeptOut(World world, int slot, CharacterCold cold, string building)
    {
        if (LockSystem.FirstDoor(world, building) is { } door) return AnimalEntry.KeptOut(cold, door);

        var at = world.Characters.Next[slot].Position;
        return world.SystemOf<AiSystem>()?.BuildingRecordOf(world, building, at) is { } record
            && AnimalEntry.KeptOut(cold, AnimalEntry.Allows(record), record.GetBool("cavernous"));
    }

    /// <summary>
    /// The building a point lies in (the room at it; a point with no floor height taken on the ground); null outdoors.
    /// </summary>
    public static string? BuildingAt(World world, Vector3 point)
    {
        if (float.IsNaN(point.Y)) point.Y = world.Walkability.GroundHeight(point.X, point.Z);
        int place = world.Sight.PlaceAt(point);
        return place >= 0 ? world.Sight.PlaceKey(place) : null;
    }

    #endregion

    #region Engine walks

    /// <summary>
    /// The rows whose plan walks to a place (<see cref="Walks"/>), worked out once from the row table.
    /// </summary>
    static readonly HashSet<TaskType> TestedRows = Tested();

    /// <summary>
    /// Whether an engine walk of row <paramref name="row"/> to <paramref name="point"/> is refused (docs/game/locks.md "Locked doors on the way"): the
    /// original's walk is the step 1 Moving or 67 Running of that row's plan (<see cref="Walks"/>), whose list B holds 60 and 59, so its plan fails
    /// when the walker stands in a building shut to it with no way out (60, <see cref="LockSystem.CanLeave"/>) or the destination is a building shut
    /// to it (59, <see cref="Accessible"/>; restricted rows open to a character not of the player's side). A row without a TaskData row, or whose own
    /// task moves it (a flight 35, a stalk 165, blood tracking 176, a formation 215), walks untested.
    /// </summary>
    public static bool WalkRefused(World world, int slot, TaskType row, Vector3 point)
    {
        if (!TestedRows.Contains(row)) return false;
        if (world.SystemOf<LockSystem>() is { } locks && !locks.CanLeave(world, slot, new Vector2(point.X, point.Z))) return true;
        if (BuildingAt(world, point) is not { } building) return false;

        var cold = world.Characters.Cold(slot)!;
        bool restricted = !PlayerSide(world, cold);
        return !Accessible(world, slot, building, animalDefault: false, restricted);
    }

    /// <summary>
    /// Whether the character is of the player's side (one of the player's, or of the player's faction): the restricted rows are closed to its plans
    /// outside an order's search (<c>FUN_14051bae0</c>, <b>Verified</b>).
    /// </summary>
    public static bool PlayerSide(World world, CharacterCold cold) => cold.IsPlayer || world.Player.Faction >= 0 && cold.Faction == world.Player.Faction;

    /// <summary>
    /// Every row whose plan walks to a place (<see cref="Walks"/>).
    /// </summary>
    static HashSet<TaskType> Tested()
    {
        var tested = new HashSet<TaskType>();
        foreach (var row in TaskDataTable.Rows)
            if (Walks(row, [])) tested.Add(row.Type);
        return tested;
    }

    /// <summary>
    /// Whether <paramref name="row"/>'s plan has a walk: its list B lists 59 or 168 itself (the walks, follows and attacks), or its list B or its
    /// effect holds <c>isAtLocation</c> (1 or 46), whose only providers are the walks 1 Moving and 67 Running (both list 59 in list B;
    /// <b>Verified</b>, row table), or a provider of one of its list B entries or of its effect
    /// walks (the providers the planner expands, <c>FUN_14051bae0</c>: rows listing the state in their list A, not
    /// <see cref="TaskDataFlags.NotProvider"/>, in any registry; the way through doors not followed, <see cref="WayThrough"/>). <paramref name="seen"/>
    /// holds the rows already looked at.
    /// </summary>
    static bool Walks(TaskDataRow row, HashSet<TaskType> seen)
    {
        if (!seen.Add(row.Type)) return false;
        if (Array.Exists(row.Needs, c => AtLocation(c) || Destination(c)) || row.Effect is { } wanted && AtLocation(wanted)) return true;

        foreach (var c in row.Needs)
            if (!WayThrough(c) && ProviderWalks(c, seen)) return true;
        return row.Effect is { } effect && ProviderWalks(effect, seen);
    }

    /// <summary>
    /// The states of the way through doors and walls (59 and 168 the destination's, 60 the walker's own building's, 90 the fortifications): their
    /// providers are the door and gate chains on the way (84, 85, 72, 74, 78, 80, 81, 287, 122), not a walk to the target, so they are not followed.
    /// </summary>
    static bool WayThrough(TaskCondition c) => Destination(c) || c.State is StateType.LocationIsAccessible
        or StateType.DestinationNotBlockedByFortifications;

    /// <summary>
    /// Whether a condition wants the character at its target (states 1 and 46 <c>isAtLocation</c>).
    /// </summary>
    static bool AtLocation(TaskCondition c) => c is { State: StateType.IsAtLocation or StateType.IsAtLocation46, Holds: true };

    /// <summary>
    /// Whether a condition is the destination's accessibility (59, 168), which a walk row (1, 29, 67), a follow or an attack lists itself.
    /// </summary>
    static bool Destination(TaskCondition c) => c.State is StateType.DestinationIsAccessible or StateType.DestinationIsAccessibleAnimal;

    /// <summary>
    /// Whether a provider of <paramref name="wanted"/> walks (<see cref="Walks"/>).
    /// </summary>
    static bool ProviderWalks(TaskCondition wanted, HashSet<TaskType> seen)
    {
        foreach (var provider in TaskDataTable.Rows)
        {
            if (provider.Is(TaskDataFlags.NotProvider) || Array.IndexOf(provider.Done, wanted) < 0) continue;
            if (Walks(provider, seen)) return true;
        }

        return false;
    }

    #endregion
}
