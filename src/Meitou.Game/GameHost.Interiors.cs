using Meitou.Data.World;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>The interior view (docs/formats/zones.md, "Interior view"): buildings with a player character inside show their interior and hide their upper floors.</summary>
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
    /// floor; every other building shows its outside. Game: the player interface's per-frame interior driver over the player's characters. Then the characters
    /// in those buildings above the viewed floor, and those inside the other buildings, are marked to be left out of the draw list.
    /// </summary>
    void UpdateInteriorView()
    {
        if (gpu.Objects is not { } objects) return;
        interiorView.Clear();
        viewed.Clear();
        hiddenIndoors.Clear();
        var characters = session.CurrentSnapshot.Characters;
        FollowFloor(objects);
        foreach (var c in characters)
            if (c.IsPlayer && objects.InteriorAt(DrawnPosition(c), InsideMargin) is { } building && interiorView.TryAdd(building.PlacementId, viewedFloor))
                viewed.Add(building);
        objects.InteriorView = interiorView;
        session.Camera.Floors ??= CameraFloor;
        foreach (var c in characters)
        {
            var at = DrawnPosition(c);
            if (viewed.Find(b => b.Contains(at, InsideMargin)) is { } inView)
            {
                if (viewedFloor < 3 && inView.FloorAt(at) > viewedFloor) hiddenIndoors.Add(c.Id);
            }
            // Strictly inside (no margin): one on the doorstep stays drawn.
            else if (objects.InteriorAt(at, ref InteriorMemo(c.Id.Slot)) is { } closed && objects.Construction?.ContainsKey(closed.PlacementId) != true) hiddenIndoors.Add(c.Id);
        }
    }

    // By character slot, the last answer of the test above (asked again once the character moves; a slot's new character only inherits a place's answer).
    Meitou.Rendering.WorldObjectRenderer.InteriorMemo[] interiorMemos = [];

    ref Meitou.Rendering.WorldObjectRenderer.InteriorMemo InteriorMemo(int slot)
    {
        if (slot >= interiorMemos.Length) Array.Resize(ref interiorMemos, Math.Max(slot + 1, interiorMemos.Length * 2));
        return ref interiorMemos[slot];
    }

    /// <summary>
    /// While the camera follows a character: a new follow sets the viewed floor to the character's floor, and so does a change of that floor later; a PageUp /
    /// PageDown in between stays until then (game: FUN_1407f5400 and the per-frame FUN_140801630). Outside a building a character is on floor 0.
    /// </summary>
    void FollowFloor(Meitou.Rendering.WorldObjectRenderer objects)
    {
        var follow = player.Follow;
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
    /// The highest floor up to the viewed one of the buildings in the interior view at (x, z), or null: the strategy camera's pivot rides on it, so a followed
    /// character's floor stays the view's centre when panning lets go of it. Engine limit: other buildings are not ground to the camera (the game's rays hit them too).
    /// </summary>
    float? CameraFloor(float x, float z)
    {
        float? best = null;
        foreach (var b in viewed)
            if (interiorView.TryGetValue(b.PlacementId, out int floor) && b.FloorHeightAt(x, z, floor) is { } y && (best is null || y > best)) best = y;
        return best;
    }

    /// <summary>How far (units) outside the indoor hull a character still counts as inside (engine choice: the feet stand on the floor at the hull's bottom).</summary>
    const float InsideMargin = 2;

    /// <summary>
    /// Where a click ray first comes down onto a walkable navmesh surface clear of the terrain (a wall top, a ramp, a deck) within <paramref name="distance"/>;
    /// the terrain and room picks do not see those, so a click there went to the blocked ground under it. A surface inside a building's rooms and under its ceiling is left to the
    /// room pick (it honours the viewed floor); an open deck or roof walk, though within the interior's height, is not. Null when there is none.
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
                if (gpu.Objects?.InteriorAt(at, InsideMargin) is not { } room || !room.Covered(at)) return at;   // inside the rooms only under a ceiling: an open deck or roof walk is picked here
            }
            previousY = p.Y;
        }
        return null;
    }
}
