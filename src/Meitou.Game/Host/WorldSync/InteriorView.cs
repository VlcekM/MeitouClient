using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation;

namespace Meitou.Game.Host.WorldSync;

/// <summary>
/// The interior view (docs/render-objects.md, "Closed interiors"): buildings with a player character inside show their interior and hide their upper
/// floors.
/// </summary>
sealed class InteriorView(GameHost host)
{
    /// <summary>
    /// The value behind <see cref="ViewedFloor"/>, 0..3.
    /// </summary>
    internal int ViewedFloorValue;
    /// <summary>
    /// The buildings in the interior view this frame and the floor each shows (3, every floor, for one held open), by placement id; handed to the
    /// object renderer.
    /// </summary>
    internal readonly Dictionary<string, int> InteriorFloors = new(StringComparer.Ordinal);
    /// <summary>
    /// The buildings that hold a player character this frame, shown at the viewed floor.
    /// </summary>
    readonly List<InteriorVolume> _viewed = [];
    /// <summary>
    /// Buildings a player character was in lately and the game time (seconds) one was last inside: left, a building shows every floor for 10 s after,
    /// then closes (game: the interior's keep-open timer, 10 s of game time set each frame while occupied, FUN_1407f7230 / FUN_14054a790; left
    /// buildings get floor 4, all).
    /// </summary>
    readonly Dictionary<string, (InteriorVolume Room, double Occupied)> _keepOpen = new(StringComparer.Ordinal);
    /// <summary>
    /// The buildings left within the keep-open time, shown with every floor this frame (<see cref="HoldLeftOpen"/>).
    /// </summary>
    readonly List<InteriorVolume> _held = [];

    /// <summary>
    /// The buildings build mode opens this frame for an interior-furniture ghost (<see cref="OpenForFurniture"/>), shown at the viewed floor with no
    /// keep-open after.
    /// </summary>
    readonly List<InteriorVolume> _furnished = [];

    /// <summary>
    /// The ids of the buildings <see cref="OpenForFurniture"/> opens this frame (reused).
    /// </summary>
    readonly HashSet<string> _furnishIds = new(StringComparer.Ordinal);
    /// <summary>
    /// The keep-open entries that closed this frame, removed after the loop.
    /// </summary>
    readonly List<string> _expired = [];
    /// <summary>
    /// The keep-open time (game seconds) after the last player character leaves a building: the game's 10 s.
    /// </summary>
    const double KeepOpenSeconds = 10;
    /// <summary>
    /// The characters not drawn: those above the viewed floor of a building in the view (game: FUN_1409fb540 hides objects above their building's
    /// floor), and those inside a building seen from outside, whose interior is not drawn (a construction site is open, so its builders stay).
    /// </summary>
    internal readonly HashSet<CharacterId> HiddenIndoors = [];
    /// <summary>
    /// The buildings last handed to the simulation as shown (<see cref="WorldSession.ShownInteriors"/>), a new set each time the view changes.
    /// </summary>
    HashSet<string> _published = new(StringComparer.Ordinal);
    /// <summary>
    /// The followed character the viewed floor follows (game: PI+0x270): the viewed floor follows that character's floor changes only.
    /// </summary>
    CharacterId? _floorFollow;
    /// <summary>
    /// The floor <see cref="_floorFollow"/> was last seen on (game: PI+0x290); -1 without one.
    /// </summary>
    int _followedFloor = -1;
    /// <summary>
    /// By character slot, the last answer of the strictly-inside test in <see cref="UpdateInteriorView"/> (asked again once the character moves; a
    /// slot's new character only inherits a place's answer).
    /// </summary>
    Rendering.Objects.WorldObjectRenderer.InteriorMemo[] _interiorMemos = [];
    /// <summary>
    /// The floor shown per building for <see cref="CameraFloor"/> (0 for a building not in the view); made once.
    /// </summary>
    Func<string, int>? _cameraFloorOf;
    /// <summary>
    /// How far (units) outside the indoor hull a character still counts as inside (engine choice: the feet stand on the floor at the hull's bottom).
    /// </summary>
    internal const float InsideMargin = 2;

    /// <summary>
    /// The viewed floor, 0..3 (the game's global floor): 0 at the start, stepped by PageUp / PageDown (<see cref="StepFloor"/>), and set to the
    /// followed character's floor when the camera starts following it or that character changes floor. Every building in the interior view hides what
    /// is above it.
    /// </summary>
    internal int ViewedFloor
    {
        get => ViewedFloorValue;
        set => ViewedFloorValue = Math.Clamp(value, 0, 3);
    }

    /// <summary>
    /// Puts a building in the view at the current floor (the agent channel's <c>interior-view</c>).
    /// </summary>
    internal void AddViewed(InteriorVolume room)
    {
        if (InteriorFloors.TryAdd(room.PlacementId, ViewedFloorValue)) _viewed.Add(room);
    }

    /// <summary>
    /// The game's floor_up / floor_down (PageUp / PageDown): one floor up or down, clamped to 0..3.
    /// </summary>
    internal void StepFloor(int step) => ViewedFloor = ViewedFloorValue + step;

    /// <summary>
    /// Every frame: each non-destroyed building that holds a player character (its indoor hull contains the character) is put in the interior view at
    /// the viewed floor; one left within the last 10 s of game time stays in it with every floor shown; every other building shows its outside. Game:
    /// the player interface's per-frame interior driver over the player's characters. Then the characters in those buildings above their shown floor,
    /// and those inside the other buildings, are marked to be left out of the draw list.
    /// </summary>
    internal void UpdateInteriorView()
    {
        if (host.ActiveObjects is not { } objects) return;
        InteriorFloors.Clear();
        _viewed.Clear();
        _furnished.Clear();
        HiddenIndoors.Clear();
        var characters = host.Session.CurrentSnapshot.Characters;
        FollowFloor(objects);
        foreach (var c in characters)
            if (c.IsPlayer && objects.InteriorAt(host.DrawnPosition(c), InsideMargin) is { } building
                    && InteriorFloors.TryAdd(building.PlacementId, ViewedFloorValue))
                _viewed.Add(building);
        OpenForFurniture(objects);
        host.ApplyForcedInteriorView(objects);
        HoldLeftOpen(objects);
        PublishShown();
        objects.InteriorView = InteriorFloors;
        objects.CutIntoViewed = host.Config.FixPartsInBuildings;
        objects.CutRainOverViewed = host.Config.FixRainInBuildings;
        objects.KeepHeldGround = host.Config.FixRuinGround;
        objects.HiddenItems = host.GroundItemSync.HiddenGroundKeys;
        host.Session.Camera.Floors ??= CameraFloor;
        host.Session.Camera.Features ??= CameraFeature;
        foreach (var c in characters)
        {
            var at = host.TrackedPosition(c);
            if (ViewedAt(at) is { } inView)
            {
                int shown = InteriorFloors[inView.PlacementId];
                if (shown < 3 && inView.FloorAt(at) > shown) HiddenIndoors.Add(c.Id);
            }
            // Strictly inside (no margin): one on the doorstep stays drawn.
            else if (objects.InteriorAt(at, ref InteriorMemo(c.Id.Slot)) is { } closed
                    && objects.Construction?.ContainsKey(closed.PlacementId) != true)
                HiddenIndoors.Add(c.Id);
        }
        host.GroundItemSync.UpdateGroundVisibility();
    }

    /// <summary>
    /// While a ghost of interior furniture (or a node) is out in build mode, the buildings the player owns in the town nearest it are in the view at
    /// the viewed floor, so the piece can be placed inside one with nobody there, as the original's opener does (<c>FUN_1407f7230</c>: with the
    /// build-mode ghost's <c>is interior furniture</c> set, the nearest town to the cursor's ground point within its radius, <c>FUN_140928890</c>,
    /// and each of its buildings the player's faction owns; docs/render-objects.md "Closed interiors"). The towns are the level's (their radius, <see
    /// cref="Meitou.Data.Gameplay.TownData.Radius"/>) and the player's outposts; the player's buildings are its complete sites within the town's
    /// radius and the town houses it owns. They take no keep-open: the original's branch writes no timer. Per frame, only while such a ghost is out.
    /// </summary>
    void OpenForFurniture(Rendering.Objects.WorldObjectRenderer objects)
    {
        if (host.Player?.Build is not { Active: true, Ghost: { } ghost } || !(ghost.Building.IsInteriorFurniture || ghost.Building.IsNode)) return;

        var snapshot = host.Session.CurrentSnapshot;
        if (NearestTown(snapshot, ghost.Position) is not var (centre, radius)) return;

        CollectOwnedIds(snapshot, centre, radius);
        if (_furnishIds.Count == 0) return;

        AddFurnishedInteriors(objects, centre, radius);
    }

    /// <summary>
    /// The centre and radius of the town or player outpost nearest <paramref name="at"/> on the ground plane that has it within its radius, or null.
    /// </summary>
    (Vector3 Centre, float Radius)? NearestTown(WorldSnapshot snapshot, Vector3 at)
    {
        (Vector3, float)? best = null;
        float nearest = float.MaxValue;
        foreach (var o in snapshot.Outposts) ConsiderTown(o.Position, o.Radius, at, ref best, ref nearest);
        if (host.PopData is not { } data) return best;

        foreach (var site in data.Sites) ConsiderTown(site.Position, site.Town.Radius, at, ref best, ref nearest);
        return best;
    }

    /// <summary>
    /// Takes the town at <paramref name="position"/> as <paramref name="best"/> when <paramref name="at"/> is within its radius and nearer than any
    /// so far.
    /// </summary>
    static void ConsiderTown(Vector3 position, float radius, Vector3 at, ref (Vector3, float)? best, ref float nearest)
    {
        float d = FlatLengthSquared(position - at);
        if (radius <= 0 || d > radius * radius || d >= nearest) return;

        best = (position, radius);
        nearest = d;
    }

    /// <summary>
    /// Fills <see cref="_furnishIds"/> with the player's complete top-level sites within the town and the town houses it owns.
    /// </summary>
    void CollectOwnedIds(WorldSnapshot snapshot, Vector3 centre, float radius)
    {
        int faction = host.Session.World.Player.Faction;
        _furnishIds.Clear();
        foreach (var b in snapshot.Buildings)
        {
            bool own = b.Owner == faction && b.Complete && !b.Destroyed && b.Parent is null;
            if (own && FlatLengthSquared(b.Position - centre) <= radius * radius) _furnishIds.Add(b.Id);
        }

        _furnishIds.UnionWith(snapshot.OwnedTownBuildings);
    }

    /// <summary>
    /// Opens at the viewed floor each loaded interior in <see cref="_furnishIds"/> whose first footprint corner lies within the town.
    /// </summary>
    void AddFurnishedInteriors(Rendering.Objects.WorldObjectRenderer objects, Vector3 centre, float radius)
    {
        foreach (var room in objects.LoadedInteriors())
        {
            if (!_furnishIds.Contains(room.PlacementId) || room.Footprint is not [var corner, ..]) continue;
            if (FlatLengthSquared(new Vector3(corner.X, 0, corner.Y) - centre) > radius * radius) continue;
            if (InteriorFloors.TryAdd(room.PlacementId, ViewedFloorValue)) _furnished.Add(room);
        }
    }

    /// <summary>
    /// The squared length of <paramref name="v"/> on the ground plane (x, z).
    /// </summary>
    static float FlatLengthSquared(Vector3 v) => v.X * v.X + v.Z * v.Z;

    /// <summary>
    /// A character slot's interior memo, the array grown to fit it.
    /// </summary>
    ref Rendering.Objects.WorldObjectRenderer.InteriorMemo InteriorMemo(int slot)
    {
        if (slot >= _interiorMemos.Length) Array.Resize(ref _interiorMemos, Math.Max(slot + 1, _interiorMemos.Length * 2));
        return ref _interiorMemos[slot];
    }

    /// <summary>
    /// Whether a place is above the shown floor of a building in the interior view, so what stands there is not drawn (as a character there).
    /// </summary>
    internal bool AboveViewedFloor(System.Numerics.Vector3 at) => ViewedAt(at) is { } inView && InteriorFloors[inView.PlacementId] is var shown
        && shown < 3 && inView.FloorAt(at) > shown;

    /// <summary>
    /// Whether a layout object on <paramref name="floor"/> of <paramref name="building"/> (its placement id) is not drawn, as the object renderer
    /// decides: above the floor the building shows in the interior view, or, while the building shows its outside, not an <paramref name="outside"/>
    /// one.
    /// </summary>
    internal bool FloorHidden(string building, int floor, bool outside) =>
        InteriorFloors.TryGetValue(building, out int shown) ? shown < 3 && floor > shown : !outside;

    /// <summary>
    /// The viewed or held-open building <paramref name="at"/> is in (a loop: a lambda here made a closure and a delegate per character per frame).
    /// </summary>
    internal InteriorVolume? ViewedAt(System.Numerics.Vector3 at)
    {
        foreach (var b in _viewed) if (b.Contains(at, InsideMargin)) return b;
        foreach (var b in _furnished) if (b.Contains(at, InsideMargin)) return b;
        foreach (var b in _held) if (b.Contains(at, InsideMargin)) return b;
        return null;
    }

    /// <summary>
    /// The keep-open time: the occupied buildings' last occupied time is now; a building left within 10 s of game time of it is put in the interior
    /// view with every floor shown (<see cref="_held"/>), and one past it (the timer runs only while the building is complete, a construction site
    /// stays open: <c>FUN_14054a790</c> reads the owner's is-complete byte, <b>Verified</b>), or no longer loaded (unloaded, removed, destroyed),
    /// closes. Game time is the simulation's (it stops while paused and runs faster at higher speeds); a time ahead of now (a loaded save's clock
    /// went back) closes at once.
    /// </summary>
    void HoldLeftOpen(Rendering.Objects.WorldObjectRenderer objects)
    {
        _held.Clear();
        _expired.Clear();
        var world = host.Session.World;
        double now = world.Tick * (double)world.TickSeconds;
        foreach (var b in _viewed) _keepOpen[b.PlacementId] = (b, now);
        foreach (var (id, open) in _keepOpen)
        {
            if (InteriorFloors.ContainsKey(id)) continue;   // occupied: in the view already
            if (now - open.Occupied > KeepOpenSeconds && objects.Construction?.ContainsKey(id) != true || open.Occupied > now
                    || objects.LoadedInterior(open.Room) is not { } room)
                _expired.Add(id);
            else
            {
                InteriorFloors.Add(id, 3);
                _held.Add(room);
            }
        }
        foreach (var id in _expired) _keepOpen.Remove(id);
    }

    /// <summary>
    /// Hands the buildings in the view this frame to the simulation (<see cref="WorldSession.ShownInteriors"/>) when they changed: a new set, as the
    /// ticks may run beside the frame.
    /// </summary>
    void PublishShown()
    {
        if (_published.Count == InteriorFloors.Count && InteriorFloors.Keys.All(_published.Contains) && host.Session.ShownInteriors == _published)
            return;

        _published = new HashSet<string>(InteriorFloors.Keys, StringComparer.Ordinal);
        host.Session.ShownInteriors = _published;
    }

    /// <summary>
    /// While the camera follows a character: a new follow sets the viewed floor to the character's floor, and so does a change of that floor later; a
    /// PageUp / PageDown in between stays until then (game: FUN_1407f5400 and the per-frame FUN_140801630). Outside a building a character is on
    /// floor 0.
    /// </summary>
    void FollowFloor(Rendering.Objects.WorldObjectRenderer objects)
    {
        var follow = host.Player?.Follow;
        if (follow is not { } id || host.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id) is not { } c)
        {
            (_floorFollow, _followedFloor) = (null, -1);
            return;
        }
        var at = host.DrawnPosition(c);
        int floor = objects.InteriorAt(at, InsideMargin) is { } building ? building.FloorAt(at) : 0;
        if (_floorFollow != id || floor != _followedFloor) ViewedFloor = floor;
        (_floorFollow, _followedFloor) = (id, floor);
    }

    /// <summary>
    /// The highest building floor at (x, z) that the strategy camera's pivot rides on, or null (docs/formats/camera.md "Height"): every building's
    /// ground floor, and a building in the interior view's floors up to the viewed one, so a followed character's floor stays the view's centre when
    /// panning lets go of it.
    /// </summary>
    float? CameraFloor(float x, float z) => host.ActiveObjects?.FloorHeightAt(x, z, _cameraFloorOf ??= key => InteriorFloors.GetValueOrDefault(key));

    /// <summary>
    /// The highest map feature surface at (x, z) at or under a height, or null: the camera's group-14 ray (docs/formats/camera.md "Height").
    /// </summary>
    float? CameraFeature(float x, float z, float top) => host.ActiveObjects?.FeatureHeightBelow(x, z, top);

    /// <summary>
    /// Where a click ray first comes down onto a walkable navmesh surface clear of the terrain (a wall top, a ramp, a deck) within <paramref
    /// name="distance"/>; the terrain and room picks do not see those, so a click there went to the blocked ground under it. A surface inside a
    /// building's rooms and under its ceiling is left to the room pick (it honours the viewed floor); an open deck or roof walk, though within the
    /// interior's height, is not, unless the building is opened and that floor hidden. Null when there is none.
    /// </summary>
    internal System.Numerics.Vector3? SurfaceMoveHit(System.Numerics.Vector3 origin, System.Numerics.Vector3 direction, float distance)
    {
        if (host.Nav is null) return null;
        var heights = new List<float>();
        float previousY = origin.Y;
        // Engine choices: 2-unit steps (a cell), at most 6000 units along the ray, and a surface counts when it stands more than a step (MaxClimb 5)
        // above the terrain.
        for (float t = 2; t <= Math.Min(distance, 6000); t += 2)
        {
            var p = origin + direction * t;
            heights.Clear();
            host.Nav.Walkability.World.HeightsAt(p.X, p.Z, heights);
            foreach (float h in heights)
            {
                if (h > previousY + 0.5f || h < p.Y - 0.5f || h - host.Nav.Walkability.TerrainHeight(p.X, p.Z) <= 5) continue;
                var at = new System.Numerics.Vector3(p.X, h, p.Z);
                // Inside the rooms only under a ceiling: an open deck or roof walk is picked here, unless its building is opened with that floor
                // hidden above the viewed one.
                if (host.ActiveObjects?.InteriorAt(at, InsideMargin) is not { } room
                    || !room.Covered(at) && !(InteriorFloors.ContainsKey(room.PlacementId) && room.FloorAt(at) > ViewedFloorValue)) return at;
            }
            previousY = p.Y;
        }
        return null;
    }

    /// <summary>
    /// The height of the highest walkable navmesh surface at <paramref name="p"/>'s (x, z) that is at most half a unit over it: what stands at the
    /// foot of a face a click met there. Null when the mesh has none there.
    /// </summary>
    internal float? SurfaceBelow(System.Numerics.Vector3 p)
    {
        if (host.Nav is null) return null;

        var heights = new List<float>();
        host.Nav.Walkability.World.HeightsAt(p.X, p.Z, heights);
        float? best = null;
        foreach (float h in heights)
        {
            if (h <= p.Y + 0.5f && (best is null || h > best)) best = h;
        }

        return best;
    }
}