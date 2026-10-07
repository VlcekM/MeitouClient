using System.Collections.Concurrent;

namespace Meitou.Navigation;

/// <summary>
/// The run-time state of doors, shared by every query: a door is named by its building's instance id (the placement id, which is also what the
/// simulation knows it by). The mesh is never rebuilt: a door's polygons keep the area <see cref="NavArea.Door"/> and a query asks this table
/// whether the door they belong to is closed. Doors start open (the generated face data is 4, "open door"; docs/game/pathfinding.md, "Face data").
/// Safe from any thread: a flip is one volatile write, and a query that is already running sees it at its next polygon.
/// </summary>
public sealed class NavDoors
{
    sealed class State { public volatile bool Closed; }

    readonly ConcurrentDictionary<string, State> doors = new(StringComparer.Ordinal);
    int version;

    /// <summary>Increases with every change that flips a door; a path cached under an older number may cross a door that has closed since.</summary>
    public int Version => Volatile.Read(ref version);

    /// <summary>Opens or closes the door of a building instance. Unknown ids are remembered (the zone may load later).</summary>
    public void Set(string buildingInstance, bool closed)
    {
        var state = doors.GetOrAdd(buildingInstance, _ => new State());
        if (state.Closed == closed) return;
        state.Closed = closed;
        Interlocked.Increment(ref version);
    }

    public void Open(string buildingInstance) => Set(buildingInstance, false);
    public void Close(string buildingInstance) => Set(buildingInstance, true);

    public bool IsClosed(string buildingInstance) => doors.TryGetValue(buildingInstance, out var s) && s.Closed;
}
