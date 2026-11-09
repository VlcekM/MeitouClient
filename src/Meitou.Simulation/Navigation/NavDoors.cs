using System.Collections.Concurrent;

namespace Meitou.Simulation.Navigation;

/// <summary>
/// The run-time state of doors, shared by every query: a door is named by its building's <see cref="Meitou.Data.World.BuildingKey"/> (zone and
/// instance id; the simulation knows it by the same key). The mesh is never rebuilt: a door's polygons keep the area <see cref="NavArea.Door"/> and a
/// query asks this table whether the door they belong to is closed. Doors start open (the generated face data is 4, "open door";
/// docs/game/pathfinding.md, "Face data"). Safe from any thread: a flip is one volatile write, and a query that is already running sees it at its
/// next polygon.
/// </summary>
public sealed class NavDoors
{
    /// <summary>
    /// One building's door state, shared by every query that reads it.
    /// </summary>
    sealed class State
    {
        /// <summary>
        /// Whether the building's doors are closed; written by <see cref="Set"/> and <see cref="Clear"/>, read by running queries.
        /// </summary>
        public volatile bool Closed;
    }

    /// <summary>
    /// The door state by building key (<see cref="Meitou.Data.World.BuildingKey"/>); an entry is made by the first <see cref="Set"/> and
    /// never removed.
    /// </summary>
    readonly ConcurrentDictionary<string, State> _doors = new(StringComparer.Ordinal);
    /// <summary>
    /// The change counter behind <see cref="Version"/>.
    /// </summary>
    int _version;

    /// <summary>
    /// Increases with every change that flips a door; a path cached under an older number may cross a door that has closed since.
    /// </summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>
    /// Opens or closes the doors of a building, by its <see cref="Meitou.Data.World.BuildingKey"/>. Unknown ids are remembered (the zone may
    /// load later).
    /// </summary>
    public void Set(string buildingInstance, bool closed)
    {
        var state = _doors.GetOrAdd(buildingInstance, _ => new State());
        if (state.Closed == closed) return;

        state.Closed = closed;
        Interlocked.Increment(ref _version);
    }

    /// <summary>
    /// Opens the doors of a building (<see cref="Set"/> with closed false).
    /// </summary>
    public void Open(string buildingInstance) => Set(buildingInstance, false);

    /// <summary>
    /// Closes the doors of a building (<see cref="Set"/> with closed true).
    /// </summary>
    public void Close(string buildingInstance) => Set(buildingInstance, true);

    /// <summary>
    /// Opens every door (a new world replaced the one that closed them).
    /// </summary>
    public void Clear()
    {
        foreach (var s in _doors.Values)
        {
            if (s.Closed)
            {
                s.Closed = false;
                Interlocked.Increment(ref _version);
            }
        }
    }

    /// <summary>
    /// Whether the building's doors are closed; false for a building never set.
    /// </summary>
    public bool IsClosed(string buildingInstance) => _doors.TryGetValue(buildingInstance, out var s) && s.Closed;
}
