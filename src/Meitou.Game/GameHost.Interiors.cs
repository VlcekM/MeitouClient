using Meitou.Data.World;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>The interior view (docs/render-objects.md, "Closed interiors"): buildings with a player character inside show their interior and hide their upper floors.</summary>
sealed partial class GameHost
{
    /// <summary>
    /// The viewed floor, 0..3 (the game's global floor): 0 at the start, stepped by PageUp / PageDown (<see cref="StepFloor"/>), and set to the followed
    /// character's floor when the camera starts following it or that character changes floor. Every building in the interior view hides what is above it.
    /// </summary>
    internal int ViewedFloor
    {
        get => viewedFloor;
        set => viewedFloor = Math.Clamp(value, 0, 3);
    }

    int viewedFloor;
    readonly Dictionary<string, int> interiorView = new(StringComparer.Ordinal);
    readonly List<InteriorVolume> viewed = [];
    // Buildings a player character was in lately and the game time (seconds) one was last inside: left, a building shows every floor for 10 s after, then closes
    // (game: the interior's keep-open timer, 10 s of game time set each frame while occupied, FUN_1407f7230 / FUN_14054a790; left buildings get floor 4, all).
    readonly Dictionary<string, (InteriorVolume Room, double Occupied)> keepOpen = new(StringComparer.Ordinal);
    readonly List<InteriorVolume> held = [];
    readonly List<string> expired = [];
    const double KeepOpenSeconds = 10;
    /// <summary>
    /// The characters not drawn: those above the viewed floor of a building in the view (game: FUN_1409fb540 hides objects above their building's floor), and
    /// those inside a building seen from outside, whose interior is not drawn (a construction site is open, so its builders stay).
    /// </summary>
    readonly HashSet<CharacterId> hiddenIndoors = [];
    // The followed character and the floor it was last seen on (game: PI+0x270 / +0x290): the viewed floor follows that floor's changes only.
    CharacterId? floorFollow;
    int followedFloor = -1;

    /// <summary>The game's floor_up / floor_down (PageUp / PageDown): one floor up or down, clamped to 0..3.</summary>
    void StepFloor(int step) => ViewedFloor = viewedFloor + step;

    /// <summary>
    /// Every frame: each non-destroyed building that holds a player character (its indoor hull contains the character) is put in the interior view at the viewed
    /// floor; one left within the last 10 s of game time stays in it with every floor shown; every other building shows its outside. Game: the player interface's
    /// per-frame interior driver over the player's characters. Then the characters in those buildings above their shown floor, and those inside the other
    /// buildings, are marked to be left out of the draw list.
    /// </summary>
    void UpdateInteriorView()
    {
        if (ActiveObjects is not { } objects) return;
        interiorView.Clear();
        viewed.Clear();
        hiddenIndoors.Clear();
        var characters = session.CurrentSnapshot.Characters;
        FollowFloor(objects);
        foreach (var c in characters)
            if (c.IsPlayer && objects.InteriorAt(DrawnPosition(c), InsideMargin) is { } building && interiorView.TryAdd(building.PlacementId, viewedFloor))
                viewed.Add(building);
        HoldLeftOpen(objects);
        objects.InteriorView = interiorView;
        objects.CutIntoViewed = config.FixPartsInBuildings;        objects.HiddenItems = hiddenGroundKeys;
        session.Camera.Floors ??= CameraFloor;
        foreach (var c in characters)
        {
            var at = DrawnPosition(c);
            if (ViewedAt(at) is { } inView)
            {
                int shown = interiorView[inView.PlacementId];
                if (shown < 3 && inView.FloorAt(at) > shown) hiddenIndoors.Add(c.Id);
            }
            // Strictly inside (no margin): one on the doorstep stays drawn.
            else if (objects.InteriorAt(at, ref InteriorMemo(c.Id.Slot)) is { } closed && objects.Construction?.ContainsKey(closed.PlacementId) != true) hiddenIndoors.Add(c.Id);
        }
        UpdateGroundVisibility();
    }

    // By character slot, the last answer of the test above (asked again once the character moves; a slot's new character only inherits a place's answer).
    Meitou.Rendering.WorldObjectRenderer.InteriorMemo[] interiorMemos = [];

    ref Meitou.Rendering.WorldObjectRenderer.InteriorMemo InteriorMemo(int slot)
    {
        if (slot >= interiorMemos.Length) Array.Resize(ref interiorMemos, Math.Max(slot + 1, interiorMemos.Length * 2));
        return ref interiorMemos[slot];
    }

    /// <summary>Whether a place is above the shown floor of a building in the interior view, so what stands there is not drawn (as a character there).</summary>
    bool AboveViewedFloor(System.Numerics.Vector3 at) => ViewedAt(at) is { } inView && interiorView[inView.PlacementId] is var shown && shown < 3 && inView.FloorAt(at) > shown;

    /// <summary>The viewed or held-open building <paramref name="at"/> is in (a loop: a lambda here made a closure and a delegate per character per frame).</summary>
    InteriorVolume? ViewedAt(System.Numerics.Vector3 at)
    {
        foreach (var b in viewed) if (b.Contains(at, InsideMargin)) return b;
        foreach (var b in held) if (b.Contains(at, InsideMargin)) return b;
        return null;
    }

    /// <summary>
    /// The keep-open time: the occupied buildings' last occupied time is now; a building left within 10 s of game time of it is put in the interior view with every
    /// floor shown (<see cref="held"/>), and one past it, or no longer loaded (unloaded, removed, destroyed), closes. Game time is the simulation's (it stops while
    /// paused and runs faster at higher speeds); a time ahead of now (a loaded save's clock went back) closes at once.
    /// </summary>
    void HoldLeftOpen(Meitou.Rendering.WorldObjectRenderer objects)
    {
        held.Clear();
        expired.Clear();
        var world = session.World;
        double now = world.Tick * (double)world.TickSeconds;
        foreach (var b in viewed) keepOpen[b.PlacementId] = (b, now);
        foreach (var (id, open) in keepOpen)
        {
            if (interiorView.ContainsKey(id)) continue;   // occupied: in the view already
            if (now - open.Occupied > KeepOpenSeconds || open.Occupied > now || objects.LoadedInterior(open.Room) is not { } room) expired.Add(id);
            else
            {
                interiorView.Add(id, 3);
                held.Add(room);
            }
        }
        foreach (var id in expired) keepOpen.Remove(id);
    }

    /// <summary>
    /// While the camera follows a character: a new follow sets the viewed floor to the character's floor, and so does a change of that floor later; a PageUp /
    /// PageDown in between stays until then (game: FUN_1407f5400 and the per-frame FUN_140801630). Outside a building a character is on floor 0.
    /// </summary>
    void FollowFloor(Meitou.Rendering.WorldObjectRenderer objects)
    {
        var follow = player?.Follow;
        if (follow is not { } id || session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id) is not { } c)
        {
            (floorFollow, followedFloor) = (null, -1);
            return;
        }
        var at = DrawnPosition(c);
        int floor = objects.InteriorAt(at, InsideMargin) is { } building ? building.FloorAt(at) : 0;
        if (floorFollow != id || floor != followedFloor) ViewedFloor = floor;
        (floorFollow, followedFloor) = (id, floor);
    }

    /// <summary>
    /// The highest building floor at (x, z) that the strategy camera's pivot rides on, or null (docs/formats/camera.md "Height"): every building's ground floor, and a building
    /// in the interior view's floors up to the viewed one, so a followed character's floor stays the view's centre when panning lets go of it.
    /// </summary>
    float? CameraFloor(float x, float z) => ActiveObjects?.FloorHeightAt(x, z, cameraFloorOf ??= key => interiorView.GetValueOrDefault(key));

    Func<string, int>? cameraFloorOf;

    /// <summary>How far (units) outside the indoor hull a character still counts as inside (engine choice: the feet stand on the floor at the hull's bottom).</summary>
    const float InsideMargin = 2;

    /// <summary>
    /// Where a click ray first comes down onto a walkable navmesh surface clear of the terrain (a wall top, a ramp, a deck) within <paramref name="distance"/>;
    /// the terrain and room picks do not see those, so a click there went to the blocked ground under it. A surface inside a building's rooms and under its ceiling is left to the
    /// room pick (it honours the viewed floor); an open deck or roof walk, though within the interior's height, is not, unless the building is opened and that floor hidden. Null when there is none.
    /// </summary>
    System.Numerics.Vector3? SurfaceMoveHit(System.Numerics.Vector3 origin, System.Numerics.Vector3 direction, float distance)
    {
        if (nav is null) return null;
        var heights = new List<float>();
        float previousY = origin.Y;
        // Engine choices: 2-unit steps (a cell), at most 6000 units along the ray, and a surface counts when it stands more than a step (MaxClimb 5) above the terrain.
        for (float t = 2; t <= Math.Min(distance, 6000); t += 2)
        {
            var p = origin + direction * t;
            heights.Clear();
            nav.Walkability.World.HeightsAt(p.X, p.Z, heights);
            foreach (float h in heights)
            {
                if (h > previousY + 0.5f || h < p.Y - 0.5f || h - nav.Walkability.TerrainHeight(p.X, p.Z) <= 5) continue;
                var at = new System.Numerics.Vector3(p.X, h, p.Z);
                // Inside the rooms only under a ceiling: an open deck or roof walk is picked here, unless its building is opened with that floor hidden above the viewed one.
                if (ActiveObjects?.InteriorAt(at, InsideMargin) is not { } room
                    || !room.Covered(at) && !(interiorView.ContainsKey(room.PlacementId) && room.FloorAt(at) > viewedFloor)) return at;
            }
            previousY = p.Y;
        }
        return null;
    }
}
