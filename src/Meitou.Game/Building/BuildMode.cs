using System.Globalization;
using System.Numerics;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.Ui;
using Meitou.Data.World;
using Meitou.Game.Editor;
using Meitou.Game.Host.Input;
using Meitou.Game.Rendering.Objects;
using Meitou.Game.Ui;
using Meitou.Simulation;
using Meitou.Simulation.Construction;
using EngineKey = Meitou.Game.Host.Input.Key;

namespace Meitou.Game.Building;

/// <summary>
/// Build mode (docs/game/buildings-production.md "Placement" and "Build menu", docs/simulation.md "Buildings"): B opens <c>Kenshi_BuildWindow</c>
/// (categories, groups, the stats, Confirm / Undo / Build, the floor arrows) and a ghost of the chosen building follows the cursor on the terrain,
/// green where <see cref="PlacementCheck"/> allows it and red where not, with the original's message. A click (or the build map's apply key) queues
/// the ghost, Confirm sends the queue as <see cref="PlaceBuildings"/> and leaves build mode. The ghost's controls are the build map's keys at the
/// documented speeds. Right-clicking a site with characters selected orders them to build it; the context menu offers Dismantle.
/// </summary>
sealed class BuildMode(PlayerInterface player)
{
    #region State

    /// <summary>
    /// Rotation per second (degrees), move up and down per second (units; with the slow modifier, <see cref="SlowMove"/>) and the tilt factor's
    /// change per second (docs/game/buildings-production.md "Controls").
    /// </summary>
    public const float RotateDegreesPerSecond = 80, MoveUnitsPerSecond = 60, SlowMove = 6, TiltPerSecond = 1, DefaultTilt = 0.2f;
    /// <summary>
    /// The ghost's height offset is reset above this (<b>Verified</b>, the original's control code).
    /// </summary>
    const float MaxLift = 1000;
    /// <summary>
    /// How far below the ceiling a <c>ceiling placement</c> ghost's origin hangs (docs/game/buildings-production.md "Placement": 1 below the upward
    /// ray's hit).
    /// </summary>
    const float CeilingHang = 1;

    /// <summary>
    /// The host's building services (data, menu, placement checks); null until the host sets them, and build mode does not open without them.
    /// </summary>
    internal BuildServices? Services;
    /// <summary>
    /// Whether build mode is open.
    /// </summary>
    internal bool Active;
    /// <summary>
    /// Whether build mode paused a running game: leaving it resumes the speed the player had. Build mode paused a running game (the original's
    /// requested pause, FUN_1404e65e0): leaving it resumes the speed the player had.
    /// </summary>
    bool _buildPausedGame;
    /// <summary>
    /// The building category chosen in the menu; null for the first that shows.
    /// </summary>
    string? _buildCategory;
    /// <summary>
    /// The index of the chosen group in its category's list.
    /// </summary>
    int _buildGroup;

    /// <summary>
    /// A right click dropped the held building: no building is held until one is chosen from the lists again (<c>FUN_1404e27e0</c>).
    /// </summary>
    internal bool BuildDiscarded;
    /// <summary>
    /// Which member of the group is chosen (null: its default pick).
    /// </summary>
    internal BuildingBuildData? BuildMember;
    /// <summary>
    /// The ghosts placed and not yet confirmed, sent as one order on Confirm.
    /// </summary>
    internal readonly List<QueuedGhost> BuildQueue = [];
    /// <summary>
    /// The ghost's turn (degrees), lift over where the cursor meets (units) and tilt factor.
    /// </summary>
    float _ghostYaw, _ghostLift, _ghostTilt = DefaultTilt;
    /// <summary>
    /// How far the ghost may still sink and rise at its last place (PlacementCheck.Margins, after the nudge); the height keys stop at them.
    /// </summary>
    PlacementCheck.VerticalMargins _ghostMargins = PlacementCheck.VerticalMargins.Unlimited;
    /// <summary>
    /// The ghost now: where the cursor meets the ground or a building's floor (On), its rotation (tilt included), the first failure.
    /// </summary>
    internal (Vector3 Position, Quaternion Rotation, PlacementFailure Failure, BuildingBuildData Building, Vector2[]? Hull, GhostFloor? On)? Ghost;
    /// <summary>
    /// The build map's keys held down (the ghost's controls run while held).
    /// </summary>
    readonly HashSet<Key> _buildHeld = [];
    /// <summary>
    /// The timestamp of the last ghost update (<see cref="System.Diagnostics.Stopwatch"/>); 0 to start the frame time afresh.
    /// </summary>
    long _buildClock;
    /// <summary>
    /// Whether the queue's ghosts were drawn last frame, so an emptied queue clears them once.
    /// </summary>
    bool _ghostsDrawn;
    /// <summary>
    /// The placement failure shown in the message panel; null while the ghost can stand.
    /// </summary>
    string? _buildMessage;
    /// <summary>
    /// The wall line being laid (docs/game/buildings-production.md "Walls"): its wall, start, the snap there and whether it runs backwards; null
    /// outside a line.
    /// </summary>
    internal (BuildingBuildData Master, Vector3 Start, WallSnap? StartSnap, bool Reversed)? WallLine;
    /// <summary>
    /// The wall line's pieces this frame, each with its own placement check and footprint.
    /// </summary>
    internal readonly List<(WallPiece Piece, PlacementFailure Failure, Vector2[]? Hull)> WallPieces = [];
    /// <summary>
    /// The snap at the wall line's end, and the snap of the ghost before the first click.
    /// </summary>
    WallSnap? _wallEndSnap, _ghostSnap;
    /// <summary>
    /// The buildable buildings by record id, made on first use.
    /// </summary>
    Dictionary<string, BuildingBuildData>? _buildIndex;

    /// <summary>
    /// The widgets the HUD gives way to while the build window is open (it covers the same lower edge of the screen).
    /// </summary>
    internal static readonly string[] BuildHiddenHud =
    [
        "InformationPanel", "MedicalPanel", "SquadPanel", "OrdersPanel", "FloorPanel", "ShortcutsPanel", "SpeedButtonsPanel", "TimeMoneyPanel",
        "BuildPanel",
    ];

    /// <summary>
    /// How far round the cursor the level's walls are read for a snap: the snap radius times sqrt 2, plus the longest wall (so the joins at the ends
    /// in reach are counted).
    /// </summary>
    const float LevelWallReach = 300;

    #endregion

    #region Mode

    /// <summary>
    /// What build mode needs from the host (null: no build mode, B does nothing).
    /// </summary>
    public BuildServices? BuildHost { get => Services; set => Services = value; }

    /// <summary>
    /// Whether build mode is open.
    /// </summary>
    public bool BuildModeOn => Active;

    /// <summary>
    /// The buildings the player may build: every building while the level editor builds, otherwise the snapshot's unlocked ones.
    /// </summary>
    internal IReadOnlySet<string> Unlocked() =>
        player.LevelEditorPanel.LevelBuild ? player.LevelEditorPanel.LevelEveryBuilding() : player.Session.CurrentSnapshot.UnlockedBuildings;

    /// <summary>
    /// B: opens build mode (pausing the game and closing the inventory windows), or closes it (the queue of unconfirmed ghosts is dropped).
    /// </summary>
    public void ToggleBuildMode() => SetBuildMode(!Active);

    /// <summary>
    /// Opens build mode (pausing a running game and clearing the ghost) or closes it (dropping the queue and resuming the speed it paused); does not
    /// open without services or over a modal screen.
    /// </summary>
    internal void SetBuildMode(bool on)
    {
        if (on && (Services is null || player.FaithfulScreens.HasModal)) return;
        if (on == Active) return;
        Active = on;
        _buildHeld.Clear();
        Ghost = null;
        _buildMessage = null;
        _buildClock = 0;
        WallLine = null;
        WallPieces.Clear();
        if (on)
        {
            BuildQueue.Clear();
            _ghostYaw = _ghostLift = 0;
            _ghostMargins = PlacementCheck.VerticalMargins.Unlimited;
            _ghostTilt = DefaultTilt;
            _buildCategory = null;
            _buildGroup = 0;
            BuildMember = null;
            BuildDiscarded = false;
            player.InventoryWindows.CloseInventoryWindows();
            player.FaithfulScreens.Open("build");
            player.ContextMenuWindow.CloseContextMenu();
            // a player pause: the speed buttons and the pause key resume it
            if (player.Session.Simulation.Speed != 0)
            {
                player.Session.Simulation.Pause();
                _buildPausedGame = true;
            }
        }
        else
        {
            BuildQueue.Clear();
            player.FaithfulScreens.Close("build");
            ShowGhosts();
            // Confirm, Esc and the window's close all clear the request (FUN_1404d1870 -> FUN_140787d40(0)); a game that was paused before stays
            // paused.
            if (_buildPausedGame && player.Session.Simulation.Speed == 0) player.Session.Simulation.TogglePause();
            _buildPausedGame = false;
        }
    }

    #endregion

    #region Menu

    /// <summary>
    /// The menu's categories with something unlocked (sorted by name while the level editor builds).
    /// </summary>
    IReadOnlyList<string> BuildCategories()
    {
        if (Services?.Menu.Categories(Unlocked()) is not { } categories) return [];

        return player.LevelEditorPanel.LevelBuild ? [.. categories.Order(StringComparer.Ordinal)] : categories;
    }

    /// <summary>
    /// The chosen category: the first that shows until one is chosen, none when nothing is unlocked.
    /// </summary>
    string? CurrentCategory()
    {
        var categories = BuildCategories();
        if (categories.Count == 0) return null;
        return _buildCategory is { } chosen && categories.Contains(chosen) ? chosen : categories[0];
    }

    /// <summary>
    /// The chosen category's groups with something unlocked.
    /// </summary>
    IReadOnlyList<BuildGroup> BuildGroups() =>
        CurrentCategory() is { } category && Services is { } host ? host.Menu.Groups(category, Unlocked()) : [];

    /// <summary>
    /// The chosen group; null without groups or after a right click dropped the held building.
    /// </summary>
    BuildGroup? CurrentGroup()
    {
        var groups = BuildGroups();
        return groups.Count == 0 || BuildDiscarded ? null : groups[Math.Clamp(_buildGroup, 0, groups.Count - 1)];
    }

    /// <summary>
    /// The building the ghost shows: the chosen member of the chosen group (its default pick otherwise).
    /// </summary>
    internal BuildingBuildData? CurrentBuilding() =>
        CurrentGroup() is { } group ? BuildMember is { } member && group.Members.Contains(member) ? member : group.Default : null;

    /// <summary>
    /// Steps the chosen group's member by <paramref name="step"/> (wrapping round), resetting the lift.
    /// </summary>
    void StepVariant(int step)
    {
        if (CurrentGroup() is not { Members.Count: > 1 } group || CurrentBuilding() is not { } current) return;
        int count = group.Members.Count;
        int index = ((group.Members.ToList().IndexOf(current) + step) % count + count) % count;
        BuildMember = group.Members[index];
        _ghostLift = 0;
    }

    #endregion

    #region Keys

    /// <summary>
    /// A key press while build mode is on: the build map (docs/game/ui-input.md section 4, checked first in build mode) takes <c>,</c> <c>.</c>
    /// rotate, <c>-</c> <c>=</c> move down / up, <c>[</c> <c>]</c> tilt, Backspace undo and Space apply. Returns true when the key was taken, so the
    /// host does not also pause or turn the camera. NUM+ / NUM- are the original's second keys of both the move and the tilt pairs, and the later
    /// registration (tilt) wins in its key map, so they tilt (<c>FUN_1403636c0</c>, <c>FUN_140362260</c>).
    /// </summary>
    public bool BuildKey(Key key, bool shift)
    {
        player.UiInput++;
        if (!Active) return false;
        switch (key)
        {
            case EngineKey.Comma or EngineKey.Period or EngineKey.Minus or EngineKey.Equal
                or EngineKey.LeftBracket or EngineKey.RightBracket or EngineKey.KeypadAdd or EngineKey.KeypadSubtract:
                _buildHeld.Add(key);
                return true;
            case EngineKey.Backspace:
                BuildUndo();
                return true;
            case EngineKey.Space:
                QueueGhost();
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="key"/> is a build map key that the cinema's numpad must not take (NUM+ / NUM- while building).
    /// </summary>
    public bool OwnsBuildNumpad(Key key) => Active && key is EngineKey.KeypadAdd or EngineKey.KeypadSubtract;

    /// <summary>
    /// A key went up: the build map's controls stop.
    /// </summary>
    public void BuildKeyUp(Key key)
    {
        player.UiInput++;
        _buildHeld.Remove(key);
    }

    #endregion

    #region Update

    /// <summary>
    /// Once a frame: the held keys move the ghost at the documented speeds, the ghost follows the cursor, is judged and drawn.
    /// </summary>
    internal void UpdateBuild()
    {
        if (Active && !player.FaithfulScreens.IsOpen("build")) SetBuildMode(false);   // Esc closed the window
        player.LevelEditorPanel.UpdateLevelEditor();
        UpdateSite();
        UpdateStorageOpening();
        player.Furniture.UpdateRock();
        if (!Active || Services is not { } host)
        {
            if (_ghostsDrawn) ShowGhosts();
            return;
        }

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = _buildClock == 0 ? 0 : (float)Math.Min(System.Diagnostics.Stopwatch.GetElapsedTime(_buildClock, now).TotalSeconds, 0.1);
        _buildClock = now;
        float Held(Key plus, Key minus) => (_buildHeld.Contains(plus) ? 1f : 0f) - (_buildHeld.Contains(minus) ? 1f : 0f);
        _ghostYaw += Held(EngineKey.Period, EngineKey.Comma) * RotateDegreesPerSecond * MathF.PI / 180 * dt;

        // The height keys (FUN_1404d5420): up goes by at most the up margin, and when it cannot (or is not held) down goes by at most the down
        // margin.
        float lift = (player.SlowModifier ? SlowMove : MoveUnitsPerSecond) * dt;
        if (_buildHeld.Contains(EngineKey.Equal) && _ghostMargins.Up > 0) _ghostLift += MathF.Min(lift, _ghostMargins.Up);
        else if (_buildHeld.Contains(EngineKey.Minus) && _ghostMargins.Down > 0) _ghostLift -= MathF.Min(lift, _ghostMargins.Down);
        if (MathF.Abs(_ghostLift) > MaxLift) _ghostLift = 0;

        float tiltKeys = Math.Clamp(Held(EngineKey.RightBracket, EngineKey.LeftBracket) + Held(EngineKey.KeypadAdd, EngineKey.KeypadSubtract), -1, 1);
        _ghostTilt = Math.Clamp(_ghostTilt + tiltKeys * TiltPerSecond * dt, 0, 1);

        Ghost = null;
        _ghostMargins = PlacementCheck.VerticalMargins.Unlimited;
        WallPieces.Clear();
        _wallEndSnap = _ghostSnap = null;
        if (WallLine is { } line && line.Master != CurrentBuilding()) WallLine = null;

        if (WallLine is { } active && !player.PointerPanel.PointerOverUi && player.Ray(player.Mouse) is { } lineRay
            && player.Picking.GroundHit(lineRay.Origin, lineRay.Direction) is { } lineEnd)
        {
            LayWall(host, active, lineEnd);
        }
        else if (WallLine is null && CurrentBuilding() is { } building && !player.PointerPanel.PointerOverUi && player.Ray(player.Mouse) is { } ray
            && player.Picking.GroundHit(ray.Origin, ray.Direction) is { } terrainHit)
        {
            PlaceGhost(host, building, ray, terrainHit);
        }

        _buildMessage = Ghost is { Failure: not PlacementFailure.None } g ? PlacementCheck.Message(g.Failure)
            : WallPieces.FirstOrDefault(p => p.Failure != PlacementFailure.None) is { Piece.Data: not null } bad
                ? PlacementCheck.Message(bad.Failure) : null;
        ShowGhosts();
    }

    /// <summary>
    /// The ghost of the chosen building under the cursor: on a building's floor when one is under the ray, else on the terrain, tilted to the
    /// ground, settled and judged; a wall ghost snaps to its link points first.
    /// </summary>
    void PlaceGhost(BuildServices host, BuildingBuildData building, (Vector3 Origin, Vector3 Direction) ray, Vector3 terrainHit)
    {
        // A building's floor before the terrain: the ghost stands on it, level.
        var floorHit = building.LinksTogether ? null
            : host.Placement.FloorAlong(ray.Origin, ray.Direction, Vector3.Distance(ray.Origin, terrainHit));
        var on = floorHit?.Floor;
        var hit = floorHit?.Position ?? terrainHit;
        var parts = host.Parts(building.Id);
        var footprint = host.Placement.Footprint(building.Id);
        var at = new Vector2(hit.X, hit.Z);
        var box = footprint?.Bounds ?? (new Vector2(-4), new Vector2(4));

        // The ghost tilts to the ground: all the way for a building that matches the slope, else by the player's tilt factor, never more than its
        // slope limit.
        var normal = on is not null ? Vector3.UnitY : PlacementCheck.GroundNormal(host.Placement.Ground, at, _ghostYaw, box);
        float angle = MathF.Acos(Math.Clamp(normal.Y, -1, 1)) * (building.MatchSlope ? 1 : _ghostTilt);
        if (building.MaxSlope > 0) angle = MathF.Min(angle, building.MaxSlope * MathF.PI / 180);
        var axis = Vector3.Cross(Vector3.UnitY, normal);
        var tilt = axis.LengthSquared() < 1e-10f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angle);
        var rotation = tilt * Quaternion.CreateFromAxisAngle(Vector3.UnitY, _ghostYaw);

        // A ceiling light hangs CeilingHang below the ceiling over the floor point (its mesh hangs below its origin, so on the floor it was buried
        // under it).
        float y = building.CeilingPlacement && on?.Ceiling is { } ceiling ? ceiling - CeilingHang : hit.Y;
        var position = new Vector3(hit.X, y + _ghostLift, hit.Z);
        float yaw = _ghostYaw;
        Func<string, Vector3, Quaternion, bool>? ignore = null;

        // A wall ghost before its first click snaps to the nearest link point: it starts there, or ends there on a free start (Verified,
        // FUN_140427950).
        if (building is { LinksTogether: true, LinkType: 0 or 3 } && Building.WallLine.Find(WallLinks(position), position) is { } snap)
        {
            _ghostSnap = snap;
            rotation = Building.WallLine.LookRotation(snap.Direction);
            position = snap.Code == 2 ? snap.Point - Vector3.Transform(new Vector3(0, 0, building.LinkLength), rotation) : snap.Point;
            yaw = PlacementCheck.YawOf(rotation);
            ignore = WallIgnore(new WallPiece(building, position, rotation), [snap.Point]);
        }

        var snapshot = player.Session.CurrentSnapshot;
        var query = new PlacementQuery(building, position, rotation, yaw, on?.Floor ?? 0, parts,
            BuildQueue, snapshot.Characters, snapshot.Buildings, host.Towns(), host.PlayerFactionId(), host.Placement)
        { Ignore = ignore, On = on, Lift = _ghostLift };

        // The ghost settles on the ground by itself (the original nudges it every frame, FUN_1404d1680): the height it keeps is what the placement
        // is judged at and what a click queues.
        if (PlacementCheck.Settle(query) is { } settled)
        {
            _ghostLift += settled.Step;
            position.Y += settled.Step;
            _ghostMargins = settled.After;
            query = query with { Position = position, Lift = _ghostLift };
        }

        var failure = player.LevelEditorPanel.LevelBuild ? PlacementFailure.None
            : !Unlocked().Contains(building.Id) ? PlacementFailure.NotAvailable
            : PlacementCheck.Evaluate(query);
        Ghost = (position, rotation, failure, building, footprint?.Place(position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw)), on);
    }

    #endregion

    #region Walls

    /// <summary>
    /// A buildable building by record id; null for one not in the menu's catalog.
    /// </summary>
    BuildingBuildData? BuildData(string id)
    {
        _buildIndex ??= Services?.Menu.Catalog.GroupBy(b => b.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return _buildIndex?.GetValueOrDefault(id);
    }

    /// <summary>
    /// The link points a line can snap to (<b>Verified</b>, <c>FUN_1404289f0</c>): the queued wall ghosts and every building that links together in
    /// the loaded zones, any owner. Those are the player's sites and the level's town walls near <paramref name="near"/>; a level wall that is a site
    /// already is the site's.
    /// </summary>
    List<WallLink> WallLinks(Vector3 near)
    {
        var sites = player.Session.CurrentSnapshot.Buildings;
        var known = sites.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var level = Services is { } host
            ? Enumerable.Where<(string Key, string Record, Vector3 Position, Quaternion Rotation)>(
                    host.Placement.LinkedAround(new Vector2(near.X, near.Z), LevelWallReach), w => !known.Contains(w.Key))
                .Select(w => (Data: BuildData(w.Record), w.Position, w.Rotation))
            : [];
        return Building.WallLine.Links(
            BuildQueue.Select(q => (q.Building, q.Position, q.Rotation))
                .Concat(sites.Select(s => (Data: BuildData(s.Record), s.Position, s.Rotation)).Where(w => w.Data is not null)
                    .Select(w => (w.Data!, w.Position, w.Rotation)))
                .Concat(level.Where(w => w.Data is not null).Select(w => (w.Data!, w.Position, w.Rotation))));
    }

    /// <summary>
    /// What never blocks a wall piece (<b>Observed</b>, <c>FUN_140427b00</c>): connectors and wedges, and near a snapped joint the walls with a link
    /// point at that joint.
    /// </summary>
    Func<string, Vector3, Quaternion, bool> WallIgnore(WallPiece piece, IReadOnlyList<Vector3> joints) => (record, position, rotation) =>
    {
        if (BuildData(record) is not { LinksTogether: true } other) return false;
        if (other.LinkType is 1 or 2) return true;

        var (start, end) = Building.WallLine.Ends(other, position, rotation);
        foreach (var joint in joints)
        {
            if (!Building.WallLine.NearJoint(piece, joint)) continue;

            bool reached = FlatDistance(start, joint) <= Building.WallLine.JointReach || FlatDistance(end, joint) <= Building.WallLine.JointReach;
            if (reached) return true;
        }

        return false;
    };

    /// <summary>
    /// The distance between two points on the ground plane (x, z).
    /// </summary>
    static float FlatDistance(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));

    /// <summary>
    /// Lays the line from its start to the cursor (docs/game/buildings-production.md "Walls"): the end snaps to another wall's start (a backwards
    /// line: to an end), every piece is checked on its own (one failure refuses the line, each is drawn by its own result).
    /// </summary>
    void LayWall(BuildServices host, (BuildingBuildData Master, Vector3 Start, WallSnap? StartSnap, bool Reversed) line, Vector3 hit)
    {
        var end = new Vector3(hit.X, hit.Y + _ghostLift, hit.Z);
        var endSnap = Building.WallLine.Find(WallLinks(end), end, line.Reversed ? 1 : 2);
        if (endSnap is { } es && FlatDistance(es.Point, line.Start) < 1) endSnap = null;
        _wallEndSnap = endSnap;
        var pieces = Building.WallLine.Layout(line.Master, BuildData, host.Placement.Ground, line.Start, line.StartSnap, endSnap?.Point ?? end,
            endSnap, line.Reversed);
        var joints = new List<Vector3>();
        if (line.StartSnap is { } s) joints.Add(s.Point);
        if (endSnap is { } e) joints.Add(e.Point);
        var snapshot = player.Session.CurrentSnapshot;
        var towns = host.Towns();
        string faction = host.PlayerFactionId();
        bool available = Unlocked().Contains(line.Master.Id);
        foreach (var piece in pieces)
        {
            float yaw = PlacementCheck.YawOf(piece.Rotation);
            var failure = player.LevelEditorPanel.LevelBuild ? PlacementFailure.None
                : !available ? PlacementFailure.NotAvailable
                : PlacementCheck.Evaluate(new PlacementQuery(piece.Data, piece.Position, piece.Rotation, yaw, 0, host.Parts(piece.Data.Id),
                    BuildQueue, snapshot.Characters, snapshot.Buildings, towns, faction, host.Placement)
                { WallPiece = true, Ignore = WallIgnore(piece, joints) });
            var hull = host.Placement.Footprint(piece.Data.Id)?.Place(piece.Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw));
            WallPieces.Add((piece, failure, hull));
        }
    }

    /// <summary>
    /// A click (or Space) for walls (<b>Verified</b>, <c>FUN_140428100</c>): the first starts a line at the ghost (a single piece, a tower or gate,
    /// only when snapped); the next queues every piece when all can stand and, unless the end snapped or the wall is a single, goes on with a new
    /// line from the far end. False: not a wall click.
    /// </summary>
    bool WallClick()
    {
        if (WallLine is { } line)
        {
            if (WallPieces.Count == 0) return true;
            if (WallPieces.FirstOrDefault(p => p.Failure != PlacementFailure.None) is { Piece.Data: not null } bad)
            {
                player.Notice(PlacementCheck.Message(bad.Failure), error: true);
                return true;
            }
            foreach (var (piece, _, hull) in WallPieces) BuildQueue.Add(new QueuedGhost(piece.Data, piece.Position, piece.Rotation, 0, hull));
            WallLine = null;
            if (_wallEndSnap is null && line.Master.LinkType != 3)
            {
                // The chain goes on from the far end of the last piece, snapped to it.
                var last = WallPieces[^1].Piece;
                var (start, end) = Building.WallLine.Ends(last.Data, last.Position, last.Rotation);
                var forward = Vector3.Transform(Vector3.UnitZ, last.Rotation);
                var dir = Vector3.Normalize(new Vector3(forward.X, 0, forward.Z));
                WallLine = line.Reversed
                    ? (line.Master, start, new WallSnap(start, dir, 2), true)
                    : (line.Master, end, new WallSnap(end, dir, 1), false);
            }
            WallPieces.Clear();
            return true;
        }
        if (Ghost is { Building: { LinksTogether: true } wall } g && g.Failure != PlacementFailure.NotAvailable
            && (wall.LinkType == 0 || wall.LinkType == 3 && _ghostSnap is not null))
        {
            var start = _ghostSnap?.Point ?? g.Position;
            WallLine = (wall, start, _ghostSnap, _ghostSnap?.Reverses ?? false);
            return true;
        }
        return false;
    }

    /// <summary>
    /// A right click in build mode over the world with a building held: it is discarded; the queued ghosts stay (Clear Building: Right-Click).
    /// </summary>
    internal bool BuildDiscard()
    {
        if (!Active || CurrentBuilding() is null) return false;
        BuildDiscarded = true;
        Ghost = null;
        return true;
    }

    /// <summary>
    /// A right click in build mode while a line is laid: drops the line (true: taken).
    /// </summary>
    internal bool WallCancel()
    {
        if (!Active || WallLine is null) return false;
        WallLine = null;
        WallPieces.Clear();
        return true;
    }

    /// <summary>
    /// Hands the renderer the ghost at the cursor and the queued ones.
    /// </summary>
    void ShowGhosts()
    {
        if (Services is not { } host) return;
        var draws = new List<WorldObjectRenderer.GhostDraw>();
        if (Active)
        {
            foreach (var queued in BuildQueue)
            {
                var queuedWorld = Matrix4x4.CreateFromQuaternion(queued.Rotation) * Matrix4x4.CreateTranslation(queued.Position);
                draws.Add(new(host.Parts(queued.Building.Id), queuedWorld, WorldObjectRenderer.GhostState.Queued));
            }

            if (Ghost is { } g)
            {
                var ghostWorld = Matrix4x4.CreateFromQuaternion(g.Rotation) * Matrix4x4.CreateTranslation(g.Position);
                draws.Add(new(host.Parts(g.Building.Id), ghostWorld,
                    g.Failure == PlacementFailure.None ? WorldObjectRenderer.GhostState.Valid : WorldObjectRenderer.GhostState.Invalid));
            }

            foreach (var (piece, failure, _) in WallPieces)
            {
                var pieceWorld = Matrix4x4.CreateFromQuaternion(piece.Rotation) * Matrix4x4.CreateTranslation(piece.Position);
                draws.Add(new(host.Parts(piece.Data.Id), pieceWorld,
                    failure == PlacementFailure.None ? WorldObjectRenderer.GhostState.Valid : WorldObjectRenderer.GhostState.Invalid));
            }
        }
        host.DrawGhosts(draws);
        _ghostsDrawn = draws.Count > 0;
    }

    #endregion

    #region Mouse

    /// <summary>
    /// A left click in the world during build mode: queues the ghost when it can stand (true: the click is taken).
    /// </summary>
    internal bool BuildClick()
    {
        if (!Active) return false;
        if (WallClick()) return true;
        if (Ghost is { Failure: not PlacementFailure.None } failed)
        {
            player.InterfaceSounds.Sound("Building_Cant_Build_There");
            player.Notice(PlacementCheck.Message(failed.Failure), error: true);
        }
        else
        {
            QueueGhost();
        }

        return true;
    }

    /// <summary>
    /// Queues the ghost as it stands (the original's click: the ghost joins the queue and a new one starts).
    /// </summary>
    void QueueGhost()
    {
        if (WallClick()) return;
        if (Ghost is not { Failure: PlacementFailure.None } g) return;
        BuildQueue.Add(new QueuedGhost(g.Building, g.Position, g.Rotation, g.On?.Floor ?? 0, g.Hull, g.On?.Parent));
        player.InterfaceSounds.Sound("Building_Place", "Building_Place", g.On is null ? "Outdoors" : "Indoors");
        _ghostLift = 0;
    }

    /// <summary>
    /// The size of a ghost's bounding box (<c>Aabb::getSize</c> of the ghost's bounds, <c>FUN_1404d6d90</c>): the merge of its parts' mesh bounds in
    /// their own space, the full X, Y and Z extent. Zero without readable mesh bounds.
    /// </summary>
    Vector3 GhostBoundsSize(BuildingBuildData building)
    {
        if (Services is not { } host) return default;
        Vector3? min = null, max = null;
        foreach (var part in host.Parts(building.Id))
        {
            if (host.Placement.PartBounds(part.MeshPath) is not { } box) continue;
            min = min is { } lo ? Vector3.Min(lo, box.Min) : box.Min;
            max = max is { } hi ? Vector3.Max(hi, box.Max) : box.Max;
        }
        return min is { } a && max is { } b ? b - a : default;
    }

    /// <summary>
    /// Drops the wall line being laid, else the last queued ghost (with its sound).
    /// </summary>
    void BuildUndo()
    {
        if (WallCancel()) return;
        if (BuildQueue.Count == 0) return;

        player.InterfaceSounds.Sound("Building_Undo");
        BuildQueue.RemoveAt(BuildQueue.Count - 1);
    }

    /// <summary>
    /// Sends the queue as one <see cref="PlaceBuildings"/> order (the level editor's own confirm while it builds) and leaves build mode.
    /// </summary>
    void BuildConfirm()
    {
        if (player.LevelEditorPanel.LevelBuild)
        {
            player.LevelEditorPanel.LevelConfirm();
            return;
        }

        if (BuildQueue.Count == 0) return;

        player.InterfaceSounds.Sound("Building_Confirm");
        var planned = BuildQueue.Select(q =>
            new PlannedBuilding(q.Building.Id, q.Position, q.Rotation, q.Floor, q.Parent, GhostBoundsSize(q.Building)));
        player.Say(new PlaceBuildings([.. planned]) { Tick = player.Session.World.Tick });
        BuildQueue.Clear();
        SetBuildMode(false);
    }

    /// <summary>
    /// The pointer in build mode: <c>build</c> over the world while the ghost can stand, <c>invalidmove</c> while it cannot.
    /// </summary>
    internal string? BuildPointer() => !Active ? null
        : WallLine is not null ? WallPieces.Exists(p => p.Failure != PlacementFailure.None) ? "invalidmove" : "build"
        : Ghost is not { } g ? null : g.Failure == PlacementFailure.None ? "build" : "invalidmove";

    #endregion

    #region Window

    /// <summary>
    /// Fills the build screen's bindings: the categories, groups and members, the message panel, and the buttons' actions.
    /// </summary>
    internal void BindBuildScreen(UiBindings b)
    {
        void Enable(string name, string action)
        {
            b.Disabled.Remove(name);
            b.Actions[name] = action;
        }

        var categories = BuildCategories();
        var category = CurrentCategory();
        var groups = BuildGroups();
        var group = CurrentGroup();
        var building = CurrentBuilding();
        b.Lists["CategoriesList"] = [.. categories.Select(c => new UiListItem(c, c))];
        b.SelectedItems["CategoriesList"] = category is null ? -1 : categories.ToList().IndexOf(category);
        b.Lists["BuildingsList"] = [.. groups.Select(g => new UiListItem(g.Name, g.Name))];
        b.SelectedItems["BuildingsList"] = group is null ? -1 : groups.ToList().IndexOf(group);
        b.Text["BuildingName"] = building?.Name ?? "";
        b.Text["DescriptionText"] = building is null || Services is null ? "" : Services.Description(building.Id);
        b.Properties["DescriptionText"] = new Dictionary<string, string>
        {
            ["WordWrap"] = "true", ["TextAlign"] = "Left Top",
            ["TextColour"] = MyGuiColours.TemplateInk(player.UiResources, "Kenshi_EditBoxStrechEmpty"),
        };

        // The building's picture (docs/game/ui-screens.md "Building icons"): the game's saved one, else the renderer's drawing; the frame stays
        // empty while it loads.
        if (BuildingImage(building) is { } image) b.Properties["BuildingImage"] = image;
        else b.Hidden.Add("BuildingImage");

        b.Text["CommandsTextBox"] = player.LevelEditorPanel.LevelBuild ? string.Join('\n', LevelEditorPanel.LevelCommandLines) : BuildCommandsText();
        b.Properties["CommandsTextBox"] = new Dictionary<string, string>
        {
            ["WordWrap"] = "true", ["TextAlign"] = "Left Top", ["TextColour"] = MyGuiColours.TemplateInk(player.UiResources, "Kenshi_GenericTextBox"),
        };

        b.Text["FloorText"] = Loc.Tr("Floor {1,num}", player.Floor?.Get() ?? 0);
        Enable("FloorArrowUp", "FloorArrowUp");
        Enable("FloorArrowDown", "FloorArrowDown");
        Enable("BuildButton", "BuildButton");
        Enable("ConfirmButton", "ConfirmButton");
        Enable("UndoButton", "UndoButton");
        if (BuildQueue.Count == 0)
        {
            b.Disabled.Add("ConfirmButton");
            b.Disabled.Add("UndoButton");
        }

        Enable("BuldingTypePrevButton", "BuldingTypePrevButton");
        Enable("BuldingTypeNextButton", "BuldingTypeNextButton");
        if (group is not { Members.Count: > 1 })
        {
            b.Disabled.Add("BuldingTypePrevButton");
            b.Disabled.Add("BuldingTypeNextButton");
        }

        // The failure the ghost has now, where the original puts its messages.
        b.Properties["MessagePanel"] = new Dictionary<string, string> { ["Visible"] = _buildMessage is null ? "false" : "true" };
    }

    /// <summary>
    /// The <c>BuildingImage</c> properties showing <paramref name="building"/>'s picture once it is on the GPU, null until then (or without one).
    /// </summary>
    Dictionary<string, string>? BuildingImage(BuildingBuildData? building)
    {
        if (building is null || Services is not { } host || player.UiPainter is not { } painter) return null;
        if (host.IconFile(building.Id) is { } file)
        {
            return painter.IconReady(file)
                ? new() { ["ImageTexture"] = Rendering.Ui.UiPainter.IconTexture(file), ["Caption"] = "" }
                : null;
        }

        string rendered = Rendering.Ui.UiPainter.RenderedBuildingIconTexture(building.Id);
        return painter.RenderedIconReady(rendered)
            ? new() { ["ImageTexture"] = rendered, ["ImageFlipV"] = "true", ["Caption"] = "" }
            : null;
    }

    /// <summary>
    /// The Commands box (<c>FUN_1404d7d90</c>, <b>Verified</b>, the key strings read from the exe's data): per line a label in the dark tag, a space
    /// and the value in the light tag, the lines joined by line breaks; the last reads "Press [Confirm] when finished". The texts are the original's,
    /// not Meitou's bindings (Space also places, Escape leaves build mode).
    /// </summary>
    static string BuildCommandsText()
    {
        const string labelColour = "#140806", valueColour = "#afa68b";
        static string Row(string label, string value) => labelColour + Loc.Tr(label) + valueColour + " " + value;
        return string.Join('\n',
            Row("Place Building:", Loc.Tr("Left-Click")), Row("Clear Building:", Loc.Tr("Right-Click")),
            Row("Rotate:", Loc.Tr("{1} or {2} keys", "<>", ",.")), Row("Change Height:", Loc.Tr("{1} and {2}", "+", "-")),
            Row("Change Slope:", Loc.Tr("{1} and {2}", "[", "]")), Row("Undo:", Loc.Tr("Backspace")), Row("Cancel All:", Loc.Tr("Esc")),
            labelColour + Loc.Tr("Press") + " " + valueColour + "[" + Loc.Tr("Confirm") + "]" + labelColour + " " + Loc.Tr("when finished"));
    }

    /// <summary>
    /// The stats panel's lines, the failure message and the queue count, over the arranged window.
    /// </summary>
    internal void AddBuildContent(List<UiControl> controls)
    {
        if (Services is not { } host) return;
        if (CurrentBuilding() is { } building)
        {
            var lines = BuildMenu.Stats(building, host.ItemName).Select(l => new PlayerInterface.HudLine(l.Label, l.Value)).ToList();
            if (controls.FirstOrDefault(c => c.Name == "StatsPanel") is { } panel)
                player.AddLines(controls, "StatsPanel", lines, MathF.Max(14, panel.Bounds.Height / 11));
        }

        if (_buildMessage is { } message && controls.FirstOrDefault(c => c.Name == "MessagePanel") is { } messagePanel)
        {
            var messageStyle = new Dictionary<string, string>
            {
                ["FontName"] = "Kenshi_PaintedTextFont_Medium", ["TextAlign"] = "Left VCenter", ["TextColour"] = "1 0.45 0.38",
            };
            controls.Add(new("MessageText", "TextBox", messagePanel.Bounds, Loc.Tr(message), "", "Kenshi_TextboxStandardText", true,
                messageStyle, Vector4.Zero, Pickable: false, Clip: messagePanel.Clip));
        }

        if (BuildQueue.Count > 0 && controls.FirstOrDefault(c => c.Name == "ButtonPanel") is { } buttons)
        {
            var queueStyle = new Dictionary<string, string>
            {
                ["FontName"] = "Kenshi_StandardFont_Medium", ["TextAlign"] = "Right VCenter",
                ["TextColour"] = MyGuiColours.TemplateInk(player.UiResources, "Kenshi_TextboxStandardText"),
            };
            controls.Add(new("QueueText", "TextBox", new(buttons.Bounds.X, buttons.Bounds.Y - 20, buttons.Bounds.Width, 18),
                $"{BuildQueue.Count} placed", "", "Kenshi_TextboxStandardText", true, queueStyle, Vector4.Zero, Pickable: false));
        }
    }

    /// <summary>
    /// A button or list of the window.
    /// </summary>
    internal void BuildAction(MyGuiEvent e)
    {
        switch (e.Widget, e.Action)
        {
            case ("CategoriesList", "select"):
                _buildCategory = e.Text;
                BuildDiscarded = false;
                _buildGroup = 0;
                BuildMember = null;
                break;
            case ("BuildingsList", "select"):
                if (!BuildDiscarded && (int)e.Value == _buildGroup)
                {
                    player.LevelEditorPanel.DropPlacementChoice();
                    break;
                }

                _buildGroup = (int)e.Value;
                BuildDiscarded = false;
                BuildMember = null;
                _ghostLift = 0;
                break;
            case ("BuldingTypePrevButton", _):
                StepVariant(-1);
                break;
            case ("BuldingTypeNextButton", _):
                StepVariant(1);
                break;
            case ("ConfirmButton", _):
                BuildConfirm();
                break;
            case ("UndoButton", _):
                BuildUndo();
                break;
            case ("BuildButton", _):
                SetBuildMode(false);
                break;
            case ("FloorArrowUp", _):
                player.Floor?.Step(1);
                break;
            case ("FloorArrowDown", _):
                player.Floor?.Step(-1);
                break;
        }
    }

    #endregion

    #region Sites

    /// <summary>
    /// A right press on a site: when, where and which, for the quick order (build) given on release or the menu opened after the hold.
    /// </summary>
    (BuildingSnapshot Site, Vector2 At, long Since)? _sitePending;

    /// <summary>
    /// A storage the selection was sent to open (a quick right click) and when; it opens when one of them is near enough, or is dropped when they
    /// stop short.
    /// </summary>
    (BuildingSnapshot Site, long Since)? _storageOpening;

    /// <summary>
    /// The storage whose window shows the character's inventory beside it (opened by an order, not by selecting it).
    /// </summary>
    internal string? StorageWithInventory;

    /// <summary>
    /// How near (X/Z) a selected character must be to open the storage it was sent to; a character takes from a machine within 200.
    /// </summary>
    const float StorageArrive = 120;

    /// <summary>
    /// The pick-cache of the ghost's collision box per building record (null: no readable box).
    /// </summary>
    readonly Dictionary<string, (Vector3 Min, Vector3 Max)?> _ghostBoxes = new(StringComparer.Ordinal);

    /// <summary>
    /// The nearest of the player's sites (and finished buildings) under a pixel, by a ray against each one's footprint prism (a sphere round its
    /// position without a footprint).
    /// </summary>
    internal BuildingSnapshot? PickSite(Vector2 pixel) => PickSiteAt(pixel)?.Site;

    /// <summary>
    /// <see cref="PickSite"/> with the distance along the ray.
    /// </summary>
    internal (BuildingSnapshot Site, float T)? PickSiteAt(Vector2 pixel)
    {
        if (Services is not { } host || player.Session.CurrentSnapshot.Buildings.Count == 0 || player.Ray(pixel) is not { } ray) return null;

        BuildingSnapshot? best = null;

        // Only what the ray meets before the ground counts, as in PickBuilding and the original's raycast (terrain ends the walk): a building's
        // hull and collision reach below the ground, and the part of the ray that has gone through the ground must not find them (the pointer
        // showed the hammer on bare ground beside a building).
        float ground = player.Picking.GroundHit(ray.Origin, ray.Direction) is { } groundPoint
            ? Vector3.Distance(ray.Origin, groundPoint) : float.MaxValue;
        float bestT = ground;
        var hits = new List<(BuildingSnapshot Site, float T)>();
        foreach (var site in player.Session.CurrentSnapshot.Buildings)
        {
            var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, PlacementCheck.YawOf(site.Rotation));

            // No selectable geometry: an invisible sitting node must leave the click to ground movement.
            var bounds = host.Placement.Footprint(site.Record);
            bool invisible = bounds is null
                && player.Production.Machine(site.Id)?.Function is TownBuildings.ChairFunction or TownBuildings.ThroneFunction;
            if (invisible && host.Parts(site.Record).Count == 0) continue;

            float? t = bounds is { } footprint
                ? player.Picking.BuildingRayT(ray, footprint.Place(site.Position, yaw), footprint.YMin, footprint.YMax, site.Record, site.Position,
                    site.Position.Y, yaw)
                : host.Placement.Plot(site.Record) is { } plot
                    ? WorldPicking.PlotT(ray, plot.Place(site.Position, yaw), ground < float.MaxValue ? ground : null)
                    : WorldPicking.RaySegment(ray.Origin, ray.Direction, site.Position, site.Position + new Vector3(0, 16, 0), 12);
            if (t is not { } hit || hit >= ground) continue;

            hits.Add((site, hit));
            if (hit < bestT) (bestT, best) = (hit, site);
        }

        // A building placed inside the one hit first (its roof or walls, hidden in the interior view) is what the click means, when the view
        // shows that building's inside there.
        while (best is not null
            && hits.Where(h => h.Site.Parent == best.Id && !player.Hidden(ray, h.T)).OrderBy(h => h.T).FirstOrDefault() is { Site: not null } inside)
            (best, bestT) = inside;
        return best is null ? null : (WallOf(best), bestT);
    }

    /// <summary>
    /// The wall an attached short piece, connector or wedge belongs to (its orders, name and materials are the wall's), else the site itself.
    /// </summary>
    BuildingSnapshot WallOf(BuildingSnapshot site)
    {
        for (int guard = 0; guard < 64 && site.WallParent is { } up
                && player.Session.CurrentSnapshot.Buildings.FirstOrDefault(s => s.Id == up) is { } wall; guard++)
            site = wall;
        return site;
    }

    /// <summary>
    /// The distance at which a ray first enters a vertical prism over a convex polygon (the Y range grown a little), or null.
    /// </summary>
    internal static float? RayPrism(Vector3 origin, Vector3 direction, Vector2[] polygon, float yMin, float yMax)
    {
        float t0 = 0, t1 = float.MaxValue;
        // The slab in Y.
        if (MathF.Abs(direction.Y) < 1e-6f)
        {
            if (origin.Y < yMin || origin.Y > yMax) return null;
        }
        else
        {
            float a = (yMin - origin.Y) / direction.Y, c = (yMax - origin.Y) / direction.Y;
            t0 = MathF.Max(t0, MathF.Min(a, c));
            t1 = MathF.Min(t1, MathF.Max(a, c));
        }

        // The convex polygon in X/Z: clip the ray against each edge's half plane.
        float area = 0;
        for (int i = 0; i < polygon.Length; i++)
        {
            var next = polygon[(i + 1) % polygon.Length];
            area += polygon[i].X * next.Y - next.X * polygon[i].Y;
        }

        float sign = area >= 0 ? 1 : -1;
        for (int i = 0; i < polygon.Length; i++)
        {
            var p = polygon[i];
            var e = polygon[(i + 1) % polygon.Length] - p;
            // Outside distance of a point: d(t) = -sign * (e.x (z - p.y) - e.y (x - p.x)) / |e|; inside while <= 0.
            float length = e.Length();
            if (length < 1e-6f) continue;
            float d0 = -sign * (e.X * (origin.Z - p.Y) - e.Y * (origin.X - p.X)) / length;
            float slope = -sign * (e.X * direction.Z - e.Y * direction.X) / length;
            if (MathF.Abs(slope) < 1e-6f)
            {
                if (d0 > 0) return null;
                continue;
            }

            float t = -d0 / slope;
            if (slope > 0)
                t1 = MathF.Min(t1, t);
            else
                t0 = MathF.Max(t0, t);
        }
        return t0 <= t1 ? t0 : null;
    }

    /// <summary>
    /// The name of a site's building, for the menu's title.
    /// </summary>
    internal string SiteName(BuildingSnapshot site) =>
        site.LevelPlacement is not null ? site.Name : Services?.Menu.Catalog.FirstOrDefault(b => b.Id == site.Record)?.Name ?? site.Record;

    /// <summary>
    /// The verbs the selection has for a site (the original's building branch, docs/game/ui-input.md "Context menu verbs"): Build (2) for an
    /// unfinished one (the quick order); finished, a machine's Use (87) or an automatic machine's Auto-Haul (152), a storage's Auto-Haul (124, the
    /// "Hauling to" job) and Inventory (26), a furnace's Inventory, a turret's Man Turret (146); Dismantle (96); Meitou's Cancel for a site nothing
    /// has been delivered to and the building panel's door buttons. Repair (95) shares construction work; town ruins start rebuilding only on an
    /// explicit order.
    /// </summary>
    internal List<(string Caption, Action Give)> SiteVerbs(BuildingSnapshot site)
    {
        var verbs = new List<(string, Action)>();
        var selection = player.SelectedIds();
        if (selection.Count == 0) return verbs;
        // Build is offered on any standing unfinished building, whoever owns it (FUN_1407a6bc0, ui-input.md); Repair and the other verbs are
        // the owner's.
        bool own = site.Owner == player.Session.World.Player.Faction;
        if (own && (site.Destroyed || site.Complete && site.NeedsWork)) verbs.Add(("Repair", () => OrderConstruction(site, selection)));
        else if (!site.Complete && !site.Destroyed) verbs.Add(("Build", () => OrderConstruction(site, selection)));
        player.Production.AddMachineVerb(verbs, site, selection);
        if (site.Complete && player.Production.Machine(site.Id) is { } machine) AddMachineVerbs(verbs, site, selection, machine, own);

        if (own && site.LevelPlacement is null)
            verbs.Add(("Dismantle", () => player.Say(new DismantleOrder(selection, site.Id) { Tick = player.Session.World.Tick })));
        if (site.LevelPlacement is null && !site.Complete && site.Progress <= 0 && site.Materials.All(m => m.Delivered <= 0))
            verbs.Add(("Cancel Construction", () => player.Say(new CancelSite(site.Id) { Tick = player.Session.World.Tick })));
        AddDoorVerbs(verbs, site);
        return verbs;
    }

    /// <summary>
    /// A finished machine's verbs: a storage's Auto-Haul and Inventory, a furnace's Inventory, a corpse machine's Corpse Disposal, a turret's Man
    /// Turret.
    /// </summary>
    void AddMachineVerbs(List<(string, Action)> verbs, BuildingSnapshot site, IReadOnlyList<CharacterId> selection,
        Simulation.Production.MachineSnapshot machine, bool own)
    {
        if (machine.IsStorage)
            // Order 124: one haul trip, with Shift the Hauling to job (FUN_1407fa650).
            verbs.Add(("Auto-Haul", () => player.Say(player.SlowModifier
                ? (SimCommand)new Simulation.Production.AddJob(selection, Data.Gameplay.Ai.TaskType.OperateStorage, site.Id)
                    { Tick = player.Session.World.Tick }
                : new Simulation.Production.OperateOrder(selection, site.Id) { Tick = player.Session.World.Tick })));

        // With Shift the dispatch turns Inventory (26) on a storage into Hauling to (124) and Man Turret (146) into Turret guard (234)
        // (FUN_1407fa650, docs/game/ui-input.md "Click").
        if (machine.IsStorage)
        {
            verbs.Add(("Inventory", () =>
            {
                if (player.SlowModifier)
                    player.Say(new Simulation.Production.AddJob(selection, Data.Gameplay.Ai.TaskType.OperateStorage, site.Id)
                        { Tick = player.Session.World.Tick });
                else
                    OpenStorage(site);
            }));
        }
        else if (machine.Function == MachineData.ItemFurnaceFunction) verbs.Add(("Inventory", () => OpenStorage(site)));

        // A corpse machine (function 11) offers Corpse Disposal (136): with Shift the Corpse disposal job (137 and its finder 138), else the
        // carried corpses go in.
        if (own && machine.Function == TownBuildings.CorpseDisposalFunction)
            verbs.Add(("Corpse Disposal", () => player.Say(player.SlowModifier
                ? (SimCommand)new Simulation.Production.AddJob(selection, Data.Gameplay.Ai.TaskType.FeedCorpseIntoMachine, site.Id)
                    { Tick = player.Session.World.Tick }
                : new Simulation.Production.DisposeCorpseOrder(selection, site.Id) { Tick = player.Session.World.Tick })));
        if (machine.Function == ObjectRanges.TurretFunction)
            verbs.Add(("Man Turret", () => player.Say(player.SlowModifier
                ? (SimCommand)new Simulation.Production.AddJob(selection, Data.Gameplay.Ai.TaskType.UseTurret, site.Id)
                    { Tick = player.Session.World.Tick }
                : new Simulation.Production.UseTurretOrder(selection, site.Id) { Tick = player.Session.World.Tick })));
    }

    /// <summary>
    /// A finished building's doors: the door panel's open/close and lock buttons, for all its doors at once (docs/simulation.md "Cages, beds and
    /// locks").
    /// </summary>
    void AddDoorVerbs(List<(string, Action)> verbs, BuildingSnapshot site)
    {
        var doors = player.Session.CurrentSnapshot.Doors.Where(d => d.Key.Building == site.Id).Select(d => d.Value).ToList();
        if (site.Complete && doors.Count > 0)
        {
            bool open = doors.Any(d => d.Motion == Simulation.Locks.DoorMotion.Open);
            verbs.Add((open ? "Close Doors" : "Open Doors",
                () => player.Say(new Simulation.Locks.ToggleDoorsOrder(site.Id) { Tick = player.Session.World.Tick })));
            // The Security button's sound (FUN_140547060): Build_Lock is the state being left.
            bool locked = doors.All(d => d.WantsLock);
            verbs.Add((locked ? "Unlock Doors" : "Lock Doors", () =>
            {
                player.InterfaceSounds.Sound("Building_Lock_Unlock", "Build_Lock", locked ? "Lock_Building" : "Unlock_Building");
                player.Say(new Simulation.Locks.ToggleDoorLocksOrder(site.Id) { Tick = player.Session.World.Tick });
            }
            ));
        }
    }

    /// <summary>
    /// Orders the selection to build (or repair) the site; with the slow modifier it also adds the matching job.
    /// </summary>
    void OrderConstruction(BuildingSnapshot site, IReadOnlyList<CharacterId> selection)
    {
        player.Say(new BuildOrder(selection, site.Id) { Tick = player.Session.World.Tick });
        if (player.SlowModifier)
        {
            var type = site.Destroyed || site.Complete ? Data.Gameplay.Ai.TaskType.Repair : Data.Gameplay.Ai.TaskType.Build;
            player.Say(new Simulation.Production.AddJob(selection, type, site.Id) { Tick = player.Session.World.Tick });
        }
    }

    /// <summary>
    /// A right press on a site with characters selected: a click released within the hold time orders the build, held longer it opens the menu (true:
    /// taken).
    /// </summary>
    internal bool SitePress(Vector2 p)
    {
        if (player.SelectedIds().Count == 0 || PickSite(p) is not { } site) return false;
        // A finished plain building does not take the click when a container, an item, a door or a character is under the cursor (a chest inside
        // a house).
        if (!SiteIsObject(site) && SomethingInsideAt(p)) return false;
        // With Shift a storage takes a job even without verbs.
        bool shiftStorage = player.SlowModifier && site.Complete && player.Production.Machine(site.Id) is { IsStorage: true };
        if (SiteVerbs(site).Count == 0 && !shiftStorage) return false;
        _sitePending = (site, p, System.Diagnostics.Stopwatch.GetTimestamp());
        return true;
    }

    /// <summary>
    /// The right button let go: a press on a site released before the menu opened gives the quick order (Build; Operate a finished machine). With
    /// Shift a finished machine or storage is added to the selection's job lists instead (the original's Perma-Jobs: Operating machine, Hauling to;
    /// on an unfinished site, Engineer).
    /// </summary>
    internal void SiteRelease()
    {
        if (_sitePending is not { } pending) return;
        _sitePending = null;
        if (player.SlowModifier && pending.Site.Complete && player.Production.Machine(pending.Site.Id) is { } job
            && (job.Operable || job.IsStorage
                || job.Function is ObjectRanges.TurretFunction or MachineData.TrainingFunction
                    or TownBuildings.ChairFunction or TownBuildings.ThroneFunction))
        {
            // A turret: 146 USE_TURRET, which becomes the job 234 Turret guard (PermaJobs).
            var type = job.Function == ObjectRanges.TurretFunction ? Data.Gameplay.Ai.TaskType.UseTurret
                : job.Function == MachineData.TrainingFunction ? Data.Gameplay.Ai.TaskType.UseTrainingDummy
                : job.Function is TownBuildings.ChairFunction or TownBuildings.ThroneFunction ? Data.Gameplay.Ai.TaskType.OperateMachinery
                : job.Operable ? (job.IsAutomatic ? Data.Gameplay.Ai.TaskType.OperateAutomaticMachinery : Data.Gameplay.Ai.TaskType.OperateMachinery)
                : Data.Gameplay.Ai.TaskType.OperateStorage;
            player.Say(new Simulation.Production.AddJob(player.SelectedIds(), type, pending.Site.Id) { Tick = player.Session.World.Tick });
            return;
        }

        // With Shift the Build order becomes the Engineer job (125), which builds any unfinished site.
        if (QuickConstruction(pending.Site)) OrderConstruction(pending.Site, player.SelectedIds());
        else if (player.Production.Machine(pending.Site.Id) is { Operable: true }
            or { Function: MachineData.TrainingFunction or TownBuildings.ChairFunction or TownBuildings.ThroneFunction })
            player.Say(new Simulation.Production.OperateOrder(player.SelectedIds(), pending.Site.Id) { Tick = player.Session.World.Tick });
        else if (player.Production.Machine(pending.Site.Id) is { Function: ObjectRanges.TurretFunction })
        {
            // the original's order 146 USE_TURRET
            player.Say(new Simulation.Production.UseTurretOrder(player.SelectedIds(), pending.Site.Id) { Tick = player.Session.World.Tick });
        }
        else if (player.Production.Machine(pending.Site.Id) is { IsStorage: true } or { Function: MachineData.ItemFurnaceFunction })
            OpenStorage(pending.Site);
        else if (!SiteIsObject(pending.Site) && player.Ray(pending.At) is { } ray && player.Picking.MoveHit(ray.Origin, ray.Direction) is { } hit)
        {
            // A finished plain building's quick right click is a move to the point under the cursor (the original: order 29 MOVE_CUS_ORDERED).
            // The original's target is the building hit's x, z at the terrain height, which the path's goal snap puts on the floor inside or the
            // ground outside; a pick that went on through the roof or wall to the terrain beyond takes that point instead, so the character goes in
            // rather than to the ground behind. A building floor the pick found (a site with an interior, the hidden roof above it nearer) stays:
            // the terrain under the roof hit lay below the floor.
            float distance = Vector3.Distance(ray.Origin, hit);
            bool throughBuilding = player.Picking.BuildingMoveHit?.Invoke(ray.Origin, ray.Direction, distance + 1) is null;
            if (throughBuilding && PickSiteAt(pending.At) is { } at && at.Site.Id == pending.Site.Id && at.T + 1 < distance)
            {
                var p = ray.Origin + ray.Direction * at.T;
                hit = new Vector3(p.X, player.Ground(p.X, p.Z), p.Z);
            }
            player.Move(hit, player.SlowModifier);
            player.MoveMarker.PlaceMarker(hit);
        }
    }

    /// <summary>
    /// A finished storage or furnace of the player's: the selection walks to it, and on arrival its window opens with the character's inventory
    /// beside it (the original's order 26, LOOT_TARGET, pointer search). A left click on a storage shows its window alone.
    /// </summary>
    void OpenStorage(BuildingSnapshot site)
    {
        player.Move(site.Position, false);
        _storageOpening = (site, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// Once a frame: the storage a right click sent the selection to opens on arrival.
    /// </summary>
    void UpdateStorageOpening()
    {
        if (_storageOpening is not { } pending) return;
        var selected = player.Selected();
        double waited = System.Diagnostics.Stopwatch.GetElapsedTime(pending.Since).TotalSeconds;
        if (selected.Count == 0 || waited > 120 || player.Session.CurrentSnapshot.Buildings.All(b => b.Id != pending.Site.Id))
        {
            _storageOpening = null;
            return;
        }

        var at = new Vector2(pending.Site.Position.X, pending.Site.Position.Z);
        if (selected.Any(c => Vector2.Distance(new(c.Position.X, c.Position.Z), at) <= StorageArrive))
        {
            _storageOpening = null;
            // A window shown alone (a left click) or closed by the player opens again through SyncMachineScreens, with the inventory beside it,
            // as when the order opens both.
            bool docked = StorageWithInventory == pending.Site.Id && player.FaithfulScreens?.IsOpen("inventory") == true;
            if (!docked && player.Production.ShownMachine == pending.Site.Id) player.Production.ShownMachine = null;
            if (player.Production.ClosedMachine == pending.Site.Id) player.Production.ClosedMachine = null;
            StorageWithInventory = pending.Site.Id;
            player.BuildingSelection.SetBuilding(player.BuildingSelection.SiteBuilding(pending.Site));
        }
        // they stopped short: another order was given
        else if (waited > 2 && selected.All(c => c.Task != Simulation.Movement.CharacterTask.GoTo)) _storageOpening = null;
    }

    /// <summary>
    /// Once a frame: a site press held long enough opens its menu.
    /// </summary>
    void UpdateSite()
    {
        if (_sitePending is not { } pending) return;
        if (System.Diagnostics.Stopwatch.GetElapsedTime(pending.Since).TotalSeconds < ContextMenuWindow.ContextHoldSeconds) return;

        _sitePending = null;
        if (player.Session.CurrentSnapshot.Buildings.FirstOrDefault(s => s.Id == pending.Site.Id) is not { } site) return;
        player.ContextMenuWindow.OpenMenu = player.ContextMenuWindow.Menu(SiteName(site), pending.At, SiteVerbs(site));
    }

    /// <summary>
    /// A site the quick Build order and the hammer pointer apply to: unfinished, except a purchased town ruin, which needs the held menu's Repair
    /// until reconstruction starts.
    /// </summary>
    static bool QuickConstruction(BuildingSnapshot site) => site.NeedsWork && !(site.LevelPlacement is not null && site.Destroyed);

    /// <summary>
    /// Whether a site is an object to act on, not a plain building: an unfinished one, or a finished machine, storage, furnace or turret. The
    /// original's pointer is the hammer (<c>build</c>) only for an unfinished building; a finished plain building is the arrow, and its quick order
    /// is a move (docs/game/ui-input.md "Building hover and click").
    /// </summary>
    internal bool SiteIsObject(BuildingSnapshot site) =>
        QuickConstruction(site)
        || player.Production.Machine(site.Id) is { Operable: true } or { IsStorage: true }
            or { Function: MachineData.ItemFurnaceFunction or MachineData.TrainingFunction or TownBuildings.ChairFunction
                or TownBuildings.ThroneFunction or ObjectRanges.TurretFunction };

    /// <summary>
    /// The pointer over a site with characters selected: <c>build</c> while unfinished (the original's pointer mode 0xb), <c>search</c> over a
    /// storage, else the class's slot 130 pointer (docs/game/ui-input.md, its table): <c>mine</c> over a machine of function 1 or 27, <c>use</c> over
    /// other machines, a furnace or a turret; none over a plain building.
    /// </summary>
    internal string? SitePointer(Vector2 p)
    {
        if (player.SelectedIds().Count == 0 || PickSite(p) is not { } site) return null;
        if (QuickConstruction(site)) return "build";
        if (player.Production.Machine(site.Id) is not { } m || !SiteIsObject(site)) return null;
        if (m.IsStorage) return "search";   // a finished storage: the original's pointer for opening it
        return m.Function is MachineData.MineFunction or MachineData.NaturalMineFunction ? "mine" : "use";
    }

    /// <summary>
    /// Something more specific than a building lies under the pixel: a character, an item on the ground, a container, a door or a resource rock. A
    /// plain building gives way to it.
    /// </summary>
    bool SomethingInsideAt(Vector2 p) =>
        player.Picking.PickWhere(p, _ => true) is not null
        || player.GroundItemPicks.PickGroundItem(p) is not null
        || player.Furniture.PickContainer(p) is not null
        || player.Furniture.PickDoor(p) is not null
        || player.Furniture.PickRock(p) is not null
        || player.Furniture.PickBed(p) is not null
        || player.Furniture.PickDummyAt(p) is not null
        || player.Furniture.PickSeat(p) is not null;

    /// <summary>
    /// The resource popup over a mine's, well's or farm's ghost (docs/game/buildings-production.md "Placement", <c>FUN_1404d9ea0</c>): the resource
    /// with its value, and for a farm the crop yield and the environment, each value red to green. Drawn as a floating tooltip with its bottom
    /// centred over the top of the ghost's collision box (the original's popup sits above the box's projected top; its exact x is an engine choice).
    /// </summary>
    internal void AddBuildReadout(List<UiControl> controls)
    {
        if (!Active || Services is not { } host || Ghost is not { } g || player.UiPainter is not { } painter) return;
        if (host.Placement.Readout(g.Building.Id, g.Position) is not { } readout) return;

        // The original's label is the resource's name and a colon (FUN_140a0c300's names), the value int(v x 100) and "%", coloured (1 - v, v, 0).
        string name = readout.Resource switch
        {
            ResourceField.IronResource => "Iron",
            ResourceField.StoneResource => "Stone",
            ResourceField.CopperResource => "Copper",
            ResourceField.CarbonResource => "Carbon",
            MachineData.WaterResource => "Water",
            _ => "Fertility",
        };
        static string Percent(float v) => ((int)(v * 100)).ToString(CultureInfo.InvariantCulture) + "%";
        static string Colour(float v)
        {
            float c = Math.Clamp(v, 0, 1);
            return string.Create(CultureInfo.InvariantCulture, $"{1 - c:0.###} {c:0.###} 0");
        }
        var lines = new List<TooltipLine> { new(Loc.Tr(name) + ":", Percent(readout.Value)) };
        var colours = new List<string?> { Colour(readout.Value) };
        if (readout.CropYield is { } crop)
        {
            lines.Add(new("Crop yield:", Percent(crop)));
            colours.Add(Colour(crop));
            // Indoors the environment is "-" in the default colour; else its names in the crop yield's colour.
            bool indoor = readout.Environment is not { Count: > 0 };
            lines.Add(new("Environment:", indoor ? "-" : string.Join(", ", readout.Environment!.Select(Loc.Tr))));
            colours.Add(indoor ? null : Colour(crop));
        }

        if (GhostTop(host, g.Building.Id, g.Position, g.Rotation) is not { } top) return;
        float minimum = player.TitleOpen ? 0 : player.StandardPixels;
        var width = UiTooltips.Arrange(lines, TooltipKind.Floating, player.Width, player.Height, top, null,
            (text, wrap) => painter.MeasureText(TooltipRuntime.TooltipFont, text, player.Height, wrap, minimum) is var s
                ? new(s.Width, s.Height) : default).Panel.Width;
        var anchor = top with { X = top.X + width / 2 };
        player.TooltipRuntime.PlaceTooltip(controls, painter, lines, TooltipKind.Floating, null, "build-readout", anchor, colours);
    }

    /// <summary>
    /// The pixel at the middle top of the ghost's collision box (its corners projected: their mean x, their highest y); null without a box or behind
    /// the camera.
    /// </summary>
    Vector2? GhostTop(BuildServices host, string record, Vector3 position, Quaternion rotation)
    {
        if (!_ghostBoxes.TryGetValue(record, out var box))
        {
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (var (vertices, _) in host.Placement.CollisionMesh(record))
                foreach (var v in vertices) (min, max) = (Vector3.Min(min, v), Vector3.Max(max, v));
            _ghostBoxes[record] = box = min.X <= max.X ? (min, max) : null;
        }
        var (lo, hi) = box ?? (new Vector3(-4, 0, -4), new Vector3(4, 8, 4));
        float left = float.MaxValue, right = float.MinValue, top = float.MaxValue;
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? lo.X : hi.X, (i & 2) == 0 ? lo.Y : hi.Y, (i & 4) == 0 ? lo.Z : hi.Z);
            if (player.Project(position + Vector3.Transform(corner, rotation)) is not { } p) return null;
            (left, right, top) = (MathF.Min(left, p.X), MathF.Max(right, p.X), MathF.Min(top, p.Y));
        }
        return new((left + right) / 2, top);
    }

    #endregion
}