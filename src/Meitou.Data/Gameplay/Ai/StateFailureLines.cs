namespace Meitou.Data.Gameplay.Ai;

/// <summary>
/// The failure line each state registration gives (the state registry <c>FUN_140610250</c>, the fourth argument of each <c>FUN_140610170</c> call;
/// docs/game/ai-tasks.md "StateType ids", <b>Verified</b> by reading the registrations): a player's order whose plan fails says the line of the
/// first list B entry of its row that does not hold (<c>FUN_140610020</c> via <c>FUN_14051a0a0</c>). Only the registrations whose name is set
/// beside the line are listed; every other state's line is empty (7 and 16 among them).
/// </summary>
public static class StateFailureLines
{
    /// <summary>
    /// The lines by state.
    /// </summary>
    static readonly Dictionary<StateType, string> Lines = new()
    {
        [StateType.IsDoorLocked] = "It's locked.",
        [StateType.DoorIsLockedHere] = "It's locked.",
        [StateType.DestinationIsAccessible] = "I can't get there.",
        [StateType.LocationIsAccessible] = "I can't get out of here.",
        [StateType.MachineHasEmptyInputs] = "It's empty.",
        [StateType.IsMachineOutputFull] = "The machine is full.",
        [StateType.HaveSomeResourcesFor] = "It's out of resources.",
        [StateType.IsCarryingArmOk] = "My arm is messed up too badly to lift anything.",
        [StateType.GotInventoryRoomForResources] = "I don't have space to pick that up!",
        [StateType.BuildingHasPower] = "It needs electricity.",
        [StateType.DoorIsLockedAndImInside] = "It's locked.",
        [StateType.DoorIsLockedAndImOutside] = "It's locked.",
        [StateType.MeSwim] = "I'm busy swimming.",
        [StateType.TargetSwimming] = "He's in the water.",
        [StateType.DestinationIsAccessibleAnimal] = "I can't get there.",
    };

    /// <summary>
    /// The state's failure line; null for an empty one.
    /// </summary>
    public static string? Of(StateType state) => Lines.GetValueOrDefault(state);
}
