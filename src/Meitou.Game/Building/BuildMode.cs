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
/// Build mode (docs/game/buildings-production.md "Placement" and "Build menu", docs/simulation.md "Buildings"): B opens <c>Kenshi_BuildWindow</c> (categories, groups, the stats, Confirm /
/// Undo / Build, the floor arrows) and a ghost of the chosen building follows the cursor on the terrain, green where <see cref="PlacementCheck"/> allows it and red where not, with the
/// original's message. A click (or the build map's apply key) queues the ghost, Confirm sends the queue as <see cref="PlaceBuildings"/> and leaves build mode. The ghost's controls are the build map's keys at the
/// documented speeds. Right-clicking a site with characters selected orders them to build it; the context menu offers Dismantle.
/// </summary>
sealed class BuildMode(PlayerInterface player)
{
    /// <summary>Rotation per second (degrees), move up and down per second (units; with the slow modifier, <see cref="SlowMove"/>) and the tilt factor's change per second (docs/game/buildings-production.md "Controls").</summary>
    public const float RotateDegreesPerSecond = 80, MoveUnitsPerSecond = 60, SlowMove = 6, TiltPerSecond = 1, DefaultTilt = 0.2f;
    /// <summary>The ghost's height offset is reset above this (<b>Verified</b>, the original's control code).</summary>
    const float MaxLift = 1000;
    /// <summary>How far below the ceiling a <c>ceiling placement</c> ghost's origin hangs (docs/game/buildings-production.md "Placement": 1 below the upward ray's hit).</summary>
    const float CeilingHang = 1;

    internal BuildServices? buildHost;
    internal bool buildMode;
    // Build mode paused a running game (the original's requested pause, FUN_1404e65e0): leaving it resumes the speed the player had.
    bool buildPausedGame;
    string? buildCategory;
    int buildGroup;

    /// <summary>A right click dropped the held building: no building is held until one is chosen from the lists again (<c>FUN_1404e27e0</c>).</summary>
    internal bool buildDiscarded;
    // Which member of the group is chosen (null: its default pick).
    internal BuildingBuildData? buildMember;
    internal readonly List<QueuedGhost> buildQueue = [];
    float ghostYaw, ghostLift, ghostTilt = DefaultTilt;
    // How far the ghost may still sink and rise at its last place (PlacementCheck.Margins, after the nudge); the height keys stop at them.
    PlacementCheck.VerticalMargins ghostMargins = PlacementCheck.VerticalMargins.Unlimited;
    // The ghost now: where the cursor meets the ground or a building's floor (On), its rotation (tilt included), the first failure.
    internal (Vector3 Position, Quaternion Rotation, PlacementFailure Failure, BuildingBuildData Building, Vector2[]? Hull, GhostFloor? On)? ghost;
    readonly HashSet<Key> buildHeld = [];
    long buildClock;
    bool ghostsDrawn;
    string? buildMessage;
    // Walls (docs/game/buildings-production.md "Walls"): the line being laid (its wall, start, the snap there and whether it runs backwards), its pieces this frame with their checks,
    // the snap at its end, and the snap of the ghost before the first click.
    internal (BuildingBuildData Master, Vector3 Start, WallSnap? StartSnap, bool Reversed)? wallLine;
    internal readonly List<(WallPiece Piece, PlacementFailure Failure, Vector2[]? Hull)> wallPieces = [];
    WallSnap? wallEndSnap, ghostSnap;
    Dictionary<string, BuildingBuildData>? buildIndex;

    /// <summary>What build mode needs from the host (null: no build mode, B does nothing).</summary>
    public BuildServices? BuildHost { get => buildHost; set => buildHost = value; }

    public bool BuildModeOn => buildMode;

    internal IReadOnlySet<string> Unlocked() => player.levelEditorPanel.levelBuild ? player.levelEditorPanel.LevelEveryBuilding() : player.session.CurrentSnapshot.UnlockedBuildings;

    /// <summary>B: opens build mode (pausing the game and closing the inventory windows), or closes it (the queue of unconfirmed ghosts is dropped).</summary>
    public void ToggleBuildMode() => SetBuildMode(!buildMode);

    internal void SetBuildMode(bool on)
    {
        if (on && (buildHost is null || player.faithfulScreens.HasModal)) return;
        if (on == buildMode) return;
        buildMode = on;
        buildHeld.Clear();
        ghost = null;
        buildMessage = null;
        buildClock = 0;
        wallLine = null;
        wallPieces.Clear();
        if (on)
        {
            buildQueue.Clear();
            ghostYaw = ghostLift = 0;
            ghostMargins = PlacementCheck.VerticalMargins.Unlimited;
            ghostTilt = DefaultTilt;
            buildCategory = null;
            buildGroup = 0;
            buildMember = null;
            buildDiscarded = false;
            player.inventoryWindows.CloseInventoryWindows();
            player.faithfulScreens.Open("build");
            player.CloseContextMenu();
            if (player.session.Simulation.Speed != 0) { player.session.Simulation.Pause(); buildPausedGame = true; }   // a player pause: the speed buttons and the pause key resume it
        }
        else
        {
            buildQueue.Clear();
            player.faithfulScreens.Close("build");
            ShowGhosts();
            // Confirm, Esc and the window's close all clear the request (FUN_1404d1870 -> FUN_140787d40(0)); a game that was paused before stays paused.
            if (buildPausedGame && player.session.Simulation.Speed == 0) player.session.Simulation.TogglePause();
            buildPausedGame = false;
        }
    }

    // ---- the menu's choice ----

    IReadOnlyList<string> BuildCategories() => buildHost?.Menu.Categories(Unlocked()) is not { } categories ? [] : player.levelEditorPanel.levelBuild ? [.. categories.Order(StringComparer.Ordinal)] : categories;

    /// <summary>The chosen category: the first that shows until one is chosen, none when nothing is unlocked.</summary>
    string? CurrentCategory()
    {
        var categories = BuildCategories();
        if (categories.Count == 0) return null;
        return buildCategory is { } chosen && categories.Contains(chosen) ? chosen : categories[0];
    }

    IReadOnlyList<BuildGroup> BuildGroups() => CurrentCategory() is { } category && buildHost is { } host ? host.Menu.Groups(category, Unlocked()) : [];

    BuildGroup? CurrentGroup()
    {
        var groups = BuildGroups();
        return groups.Count == 0 || buildDiscarded ? null : groups[Math.Clamp(buildGroup, 0, groups.Count - 1)];
    }

    /// <summary>The building the ghost shows: the chosen member of the chosen group (its default pick otherwise).</summary>
    internal BuildingBuildData? CurrentBuilding() =>
        CurrentGroup() is { } group ? buildMember is { } member && group.Members.Contains(member) ? member : group.Default : null;

    void StepVariant(int step)
    {
        if (CurrentGroup() is not { Members.Count: > 1 } group || CurrentBuilding() is not { } current) return;
        int index = ((group.Members.ToList().IndexOf(current) + step) % group.Members.Count + group.Members.Count) % group.Members.Count;
        buildMember = group.Members[index];
        ghostLift = 0;
    }

    // ---- keys ----

    /// <summary>
    /// A key press while build mode is on: the build map (docs/game/ui-input.md section 4, checked first in build mode) takes <c>,</c> <c>.</c> rotate, <c>-</c> <c>=</c> move down / up,
    /// <c>[</c> <c>]</c> tilt, Backspace undo and Space apply. Returns true when the key was taken, so the host does not also pause or turn the camera. NUM+ / NUM- are the original's second keys of both the move and the tilt pairs, and the later registration (tilt) wins in its key map, so they tilt (<c>FUN_1403636c0</c>, <c>FUN_140362260</c>).
    /// </summary>
    public bool BuildKey(Key key, bool shift)
    {
        player.uiInput++;
        if (!buildMode) return false;
        switch (key)
        {
            case EngineKey.Comma or EngineKey.Period or EngineKey.Minus or EngineKey.Equal or EngineKey.LeftBracket or EngineKey.RightBracket or EngineKey.KeypadAdd or EngineKey.KeypadSubtract:
                buildHeld.Add(key);
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

    /// <summary>Whether <paramref name="key"/> is a build map key that the cinema's numpad must not take (NUM+ / NUM- while building).</summary>
    public bool OwnsBuildNumpad(Key key) => buildMode && key is EngineKey.KeypadAdd or EngineKey.KeypadSubtract;

    public void BuildKeyUp(Key key)
    {
        player.uiInput++;
        buildHeld.Remove(key);
    }

    /// <summary>Once a frame: the held keys move the ghost at the documented speeds, the ghost follows the cursor, is judged and drawn.</summary>
    internal void UpdateBuild()
    {
        if (buildMode && !player.faithfulScreens.IsOpen("build")) SetBuildMode(false);   // Esc closed the window
        player.levelEditorPanel.UpdateLevelEditor();
        UpdateSite();
        UpdateStorageOpening();
        player.furniture.UpdateRock();
        if (!buildMode || buildHost is not { } host) { if (ghostsDrawn) ShowGhosts(); return; }
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = buildClock == 0 ? 0 : (float)Math.Min(System.Diagnostics.Stopwatch.GetElapsedTime(buildClock, now).TotalSeconds, 0.1);
        buildClock = now;
        float Held(Key plus, Key minus) => (buildHeld.Contains(plus) ? 1f : 0f) - (buildHeld.Contains(minus) ? 1f : 0f);
        ghostYaw += Held(EngineKey.Period, EngineKey.Comma) * RotateDegreesPerSecond * MathF.PI / 180 * dt;
        // The height keys (FUN_1404d5420): up goes by at most the up margin, and when it cannot (or is not held) down goes by at most the down margin.
        float lift = (player.SlowModifier ? SlowMove : MoveUnitsPerSecond) * dt;
        if (buildHeld.Contains(EngineKey.Equal) && ghostMargins.Up > 0) ghostLift += MathF.Min(lift, ghostMargins.Up);
        else if (buildHeld.Contains(EngineKey.Minus) && ghostMargins.Down > 0) ghostLift -= MathF.Min(lift, ghostMargins.Down);
        if (MathF.Abs(ghostLift) > MaxLift) ghostLift = 0;
        ghostTilt = Math.Clamp(ghostTilt + Math.Clamp(Held(EngineKey.RightBracket, EngineKey.LeftBracket) + Held(EngineKey.KeypadAdd, EngineKey.KeypadSubtract), -1, 1) * TiltPerSecond * dt, 0, 1);

        ghost = null;
        ghostMargins = PlacementCheck.VerticalMargins.Unlimited;
        wallPieces.Clear();
        wallEndSnap = ghostSnap = null;
        if (wallLine is { } line && line.Master != CurrentBuilding()) wallLine = null;
        if (wallLine is { } active && !player.pointerOverUi && player.Ray(player.mouse) is { } lineRay && player.GroundHit(lineRay.Origin, lineRay.Direction) is { } lineEnd)
            LayWall(host, active, lineEnd);
        else if (wallLine is null && CurrentBuilding() is { } building && !player.pointerOverUi && player.Ray(player.mouse) is { } ray && player.GroundHit(ray.Origin, ray.Direction) is { } terrainHit)
        {
            // A building's floor before the terrain: the ghost stands on it, level.
            var floorHit = building.LinksTogether ? null : host.Placement.FloorAlong(ray.Origin, ray.Direction, Vector3.Distance(ray.Origin, terrainHit));
            var on = floorHit?.Floor;
            var hit = floorHit?.Position ?? terrainHit;
            var parts = host.Parts(building.Id);
            var footprint = host.Placement.Footprint(building.Id);
            var at = new Vector2(hit.X, hit.Z);
            var box = footprint?.Bounds ?? (new Vector2(-4), new Vector2(4));
            // The ghost tilts to the ground: all the way for a building that matches the slope, else by the player's tilt factor, never more than its slope limit.
            var normal = on is not null ? Vector3.UnitY : PlacementCheck.GroundNormal(host.Placement.Ground, at, ghostYaw, box);
            float angle = MathF.Acos(Math.Clamp(normal.Y, -1, 1)) * (building.MatchSlope ? 1 : ghostTilt);
            if (building.MaxSlope > 0) angle = MathF.Min(angle, building.MaxSlope * MathF.PI / 180);
            var axis = Vector3.Cross(Vector3.UnitY, normal);
            var tilt = axis.LengthSquared() < 1e-10f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), angle);
            var rotation = tilt * Quaternion.CreateFromAxisAngle(Vector3.UnitY, ghostYaw);
            // A ceiling light hangs CeilingHang below the ceiling over the floor point (its mesh hangs below its origin, so on the floor it was buried under it).
            float y = building.CeilingPlacement && on?.Ceiling is { } ceiling ? ceiling - CeilingHang : hit.Y;
            var position = new Vector3(hit.X, y + ghostLift, hit.Z);
            float yaw = ghostYaw;
            Func<string, Vector3, Quaternion, bool>? ignore = null;
            // A wall ghost before its first click snaps to the nearest link point: it starts there, or ends there on a free start (Verified, FUN_140427950).
            if (building is { LinksTogether: true, LinkType: 0 or 3 } && WallLine.Find(WallLinks(position), position) is { } snap)
            {
                ghostSnap = snap;
                rotation = WallLine.LookRotation(snap.Direction);
                position = snap.Code == 2 ? snap.Point - Vector3.Transform(new Vector3(0, 0, building.LinkLength), rotation) : snap.Point;
                yaw = PlacementCheck.YawOf(rotation);
                ignore = WallIgnore(new WallPiece(building, position, rotation), [snap.Point]);
            }
            var snapshot = player.session.CurrentSnapshot;
            var query = new PlacementQuery(building, position, rotation, yaw, on?.Floor ?? 0, parts,
                buildQueue, snapshot.Characters, snapshot.Buildings, host.Towns(), host.PlayerFactionId(), host.Placement) { Ignore = ignore, On = on, Lift = ghostLift };
            // The ghost settles on the ground by itself (the original nudges it every frame, FUN_1404d1680): the height it keeps is what the placement is judged at and what a click queues.
            if (PlacementCheck.Settle(query) is { } settled)
            {
                ghostLift += settled.Step;
                position.Y += settled.Step;
                ghostMargins = settled.After;
                query = query with { Position = position, Lift = ghostLift };
            }
            var failure = player.levelEditorPanel.levelBuild ? PlacementFailure.None : !Unlocked().Contains(building.Id) ? PlacementFailure.NotAvailable : PlacementCheck.Evaluate(query);
            ghost = (position, rotation, failure, building, footprint?.Place(position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw)), on);
        }
        buildMessage = ghost is { Failure: not PlacementFailure.None } g ? PlacementCheck.Message(g.Failure)
            : wallPieces.FirstOrDefault(p => p.Failure != PlacementFailure.None) is { Piece.Data: not null } bad ? PlacementCheck.Message(bad.Failure) : null;
        ShowGhosts();
    }

    // ---- walls ----

    BuildingBuildData? BuildData(string id)
    {
        buildIndex ??= buildHost?.Menu.Catalog.GroupBy(b => b.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return buildIndex?.GetValueOrDefault(id);
    }

    /// <summary>How far round the cursor the level's walls are read for a snap: the snap radius times sqrt 2, plus the longest wall (so the joins at the ends in reach are counted).</summary>
    const float LevelWallReach = 300;

    /// <summary>
    /// The link points a line can snap to (<b>Verified</b>, <c>FUN_1404289f0</c>): the queued wall ghosts and every building that links together in the loaded zones, any owner. Those are the
    /// player's sites and the level's town walls near <paramref name="near"/>; a level wall that is a site already is the site's.
    /// </summary>
    List<WallLink> WallLinks(Vector3 near)
    {
        var sites = player.session.CurrentSnapshot.Buildings;
        var known = sites.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var level = buildHost is { } host
            ? host.Placement.LinkedAround(new Vector2(near.X, near.Z), LevelWallReach).Where(w => !known.Contains(w.Key)).Select(w => (Data: BuildData(w.Record), w.Position, w.Rotation))
            : [];
        return WallLine.Links(
            buildQueue.Select(q => (q.Building, q.Position, q.Rotation))
                .Concat(sites.Select(s => (Data: BuildData(s.Record), s.Position, s.Rotation)).Where(w => w.Data is not null).Select(w => (w.Data!, w.Position, w.Rotation)))
                .Concat(level.Where(w => w.Data is not null).Select(w => (w.Data!, w.Position, w.Rotation))));
    }

    /// <summary>
    /// What never blocks a wall piece (<b>Observed</b>, <c>FUN_140427b00</c>): connectors and wedges, and near a snapped joint the walls with a link point at that joint.
    /// </summary>
    Func<string, Vector3, Quaternion, bool> WallIgnore(WallPiece piece, IReadOnlyList<Vector3> joints) => (record, position, rotation) =>
    {
        if (BuildData(record) is not { LinksTogether: true } other) return false;
        if (other.LinkType is 1 or 2) return true;
        var (start, end) = WallLine.Ends(other, position, rotation);
        foreach (var joint in joints)
            if (WallLine.NearJoint(piece, joint) && (FlatDistance(start, joint) <= WallLine.JointReach || FlatDistance(end, joint) <= WallLine.JointReach)) return true;
        return false;
    };

    static float FlatDistance(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));

    /// <summary>
    /// Lays the line from its start to the cursor (docs/game/buildings-production.md "Walls"): the end snaps to another wall's start (a backwards line: to an end), every piece is checked
    /// on its own (one failure refuses the line, each is drawn by its own result).
    /// </summary>
    void LayWall(BuildServices host, (BuildingBuildData Master, Vector3 Start, WallSnap? StartSnap, bool Reversed) line, Vector3 hit)
    {
        var end = new Vector3(hit.X, hit.Y + ghostLift, hit.Z);
        var endSnap = WallLine.Find(WallLinks(end), end, line.Reversed ? 1 : 2);
        if (endSnap is { } es && FlatDistance(es.Point, line.Start) < 1) endSnap = null;
        wallEndSnap = endSnap;
        var pieces = WallLine.Layout(line.Master, BuildData, host.Placement.Ground, line.Start, line.StartSnap, endSnap?.Point ?? end, endSnap, line.Reversed);
        var joints = new List<Vector3>();
        if (line.StartSnap is { } s) joints.Add(s.Point);
        if (endSnap is { } e) joints.Add(e.Point);
        var snapshot = player.session.CurrentSnapshot;
        var towns = host.Towns();
        string faction = host.PlayerFactionId();
        bool available = Unlocked().Contains(line.Master.Id);
        foreach (var piece in pieces)
        {
            float yaw = PlacementCheck.YawOf(piece.Rotation);
            var failure = player.levelEditorPanel.levelBuild ? PlacementFailure.None : !available ? PlacementFailure.NotAvailable : PlacementCheck.Evaluate(new PlacementQuery(piece.Data, piece.Position, piece.Rotation, yaw, 0, host.Parts(piece.Data.Id),
                buildQueue, snapshot.Characters, snapshot.Buildings, towns, faction, host.Placement) { WallPiece = true, Ignore = WallIgnore(piece, joints) });
            wallPieces.Add((piece, failure, host.Placement.Footprint(piece.Data.Id)?.Place(piece.Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw))));
        }
    }

    /// <summary>
    /// A click (or Space) for walls (<b>Verified</b>, <c>FUN_140428100</c>): the first starts a line at the ghost (a single piece, a tower or gate, only when snapped); the next queues every
    /// piece when all can stand and, unless the end snapped or the wall is a single, goes on with a new line from the far end. False: not a wall click.
    /// </summary>
    bool WallClick()
    {
        if (wallLine is { } line)
        {
            if (wallPieces.Count == 0) return true;
            if (wallPieces.FirstOrDefault(p => p.Failure != PlacementFailure.None) is { Piece.Data: not null } bad)
            {
                player.Notice(PlacementCheck.Message(bad.Failure), error: true);
                return true;
            }
            foreach (var (piece, _, hull) in wallPieces) buildQueue.Add(new QueuedGhost(piece.Data, piece.Position, piece.Rotation, 0, hull));
            wallLine = null;
            if (wallEndSnap is null && line.Master.LinkType != 3)
            {
                // The chain goes on from the far end of the last piece, snapped to it.
                var last = wallPieces[^1].Piece;
                var (start, end) = WallLine.Ends(last.Data, last.Position, last.Rotation);
                var forward = Vector3.Transform(Vector3.UnitZ, last.Rotation);
                var dir = Vector3.Normalize(new Vector3(forward.X, 0, forward.Z));
                wallLine = line.Reversed ? (line.Master, start, new WallSnap(start, dir, 2), true) : (line.Master, end, new WallSnap(end, dir, 1), false);
            }
            wallPieces.Clear();
            return true;
        }
        if (ghost is { Building: { LinksTogether: true } wall } g && g.Failure != PlacementFailure.NotAvailable && (wall.LinkType == 0 || wall.LinkType == 3 && ghostSnap is not null))
        {
            var start = ghostSnap?.Point ?? g.Position;
            wallLine = (wall, start, ghostSnap, ghostSnap?.Reverses ?? false);
            return true;
        }
        return false;
    }

    /// <summary>A right click in build mode over the world with a building held: it is discarded; the queued ghosts stay (Clear Building: Right-Click).</summary>
    internal bool BuildDiscard()
    {
        if (!buildMode || CurrentBuilding() is null) return false;
        buildDiscarded = true;
        ghost = null;
        return true;
    }

    /// <summary>A right click in build mode while a line is laid: drops the line (true: taken).</summary>
    internal bool WallCancel()
    {
        if (!buildMode || wallLine is null) return false;
        wallLine = null;
        wallPieces.Clear();
        return true;
    }

    /// <summary>Hands the renderer the ghost at the cursor and the queued ones.</summary>
    void ShowGhosts()
    {
        if (buildHost is not { } host) return;
        var draws = new List<WorldObjectRenderer.GhostDraw>();
        if (buildMode)
        {
            foreach (var queued in buildQueue)
                draws.Add(new(host.Parts(queued.Building.Id), Matrix4x4.CreateFromQuaternion(queued.Rotation) * Matrix4x4.CreateTranslation(queued.Position), WorldObjectRenderer.GhostState.Queued));
            if (ghost is { } g)
                draws.Add(new(host.Parts(g.Building.Id), Matrix4x4.CreateFromQuaternion(g.Rotation) * Matrix4x4.CreateTranslation(g.Position),
                    g.Failure == PlacementFailure.None ? WorldObjectRenderer.GhostState.Valid : WorldObjectRenderer.GhostState.Invalid));
            foreach (var (piece, failure, _) in wallPieces)
                draws.Add(new(host.Parts(piece.Data.Id), Matrix4x4.CreateFromQuaternion(piece.Rotation) * Matrix4x4.CreateTranslation(piece.Position),
                    failure == PlacementFailure.None ? WorldObjectRenderer.GhostState.Valid : WorldObjectRenderer.GhostState.Invalid));
        }
        host.DrawGhosts(draws);
        ghostsDrawn = draws.Count > 0;
    }

    // ---- mouse ----

    /// <summary>A left click in the world during build mode: queues the ghost when it can stand (true: the click is taken).</summary>
    internal bool BuildClick()
    {
        if (!buildMode) return false;
        if (WallClick()) return true;
        if (ghost is { Failure: not PlacementFailure.None } failed) { player.interfaceSounds.Sound("Building_Cant_Build_There"); player.Notice(PlacementCheck.Message(failed.Failure), error: true); }
        else QueueGhost();
        return true;
    }

    /// <summary>Queues the ghost as it stands (the original's click: the ghost joins the queue and a new one starts).</summary>
    void QueueGhost()
    {
        if (WallClick()) return;
        if (ghost is not { Failure: PlacementFailure.None } g) return;
        buildQueue.Add(new QueuedGhost(g.Building, g.Position, g.Rotation, g.On?.Floor ?? 0, g.Hull, g.On?.Parent));
        player.interfaceSounds.Sound("Building_Place", "Building_Place", g.On is null ? "Outdoors" : "Indoors");
        ghostLift = 0;
    }

    /// <summary>
    /// The size of a ghost's bounding box (<c>Aabb::getSize</c> of the ghost's bounds, <c>FUN_1404d6d90</c>): the merge of its parts' mesh bounds in their own space, the full X, Y and Z extent.
    /// Zero without readable mesh bounds.
    /// </summary>
    Vector3 GhostBoundsSize(BuildingBuildData building)
    {
        if (buildHost is not { } host) return default;
        Vector3? min = null, max = null;
        foreach (var part in host.Parts(building.Id))
        {
            if (host.Placement.PartBounds(part.MeshPath) is not { } box) continue;
            min = min is { } lo ? Vector3.Min(lo, box.Min) : box.Min;
            max = max is { } hi ? Vector3.Max(hi, box.Max) : box.Max;
        }
        return min is { } a && max is { } b ? b - a : default;
    }

    void BuildUndo()
    {
        if (WallCancel()) return;
        if (buildQueue.Count > 0) { player.interfaceSounds.Sound("Building_Undo"); buildQueue.RemoveAt(buildQueue.Count - 1); }
    }

    void BuildConfirm()
    {
        if (player.levelEditorPanel.levelBuild) { player.levelEditorPanel.LevelConfirm(); return; }
        if (buildQueue.Count == 0) return;
        player.interfaceSounds.Sound("Building_Confirm");
        player.Say(new PlaceBuildings([.. buildQueue.Select(q => new PlannedBuilding(q.Building.Id, q.Position, q.Rotation, q.Floor, q.Parent, GhostBoundsSize(q.Building)))]) { Tick = player.session.World.Tick });
        buildQueue.Clear();
        SetBuildMode(false);
    }

    /// <summary>The pointer in build mode: <c>build</c> over the world while the ghost can stand, <c>invalidmove</c> while it cannot.</summary>
    internal string? BuildPointer() => !buildMode ? null
        : wallLine is not null ? wallPieces.Exists(p => p.Failure != PlacementFailure.None) ? "invalidmove" : "build"
        : ghost is not { } g ? null : g.Failure == PlacementFailure.None ? "build" : "invalidmove";

    // ---- the window ----

    /// <summary>The widgets the HUD gives way to while the build window is open (it covers the same lower edge of the screen).</summary>
    internal static readonly string[] BuildHiddenHud = ["InformationPanel", "MedicalPanel", "SquadPanel", "OrdersPanel", "FloorPanel", "ShortcutsPanel", "SpeedButtonsPanel", "TimeMoneyPanel", "BuildPanel"];

    internal void BindBuildScreen(UiBindings b)
    {
        void Enable(string name, string action) { b.Disabled.Remove(name); b.Actions[name] = action; }
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
        b.Text["DescriptionText"] = building is null || buildHost is null ? "" : buildHost.Description(building.Id);
        b.Properties["DescriptionText"] = new Dictionary<string, string> { ["WordWrap"] = "true", ["TextAlign"] = "Left Top", ["TextColour"] = MyGuiColours.TemplateInk(player.uiResources, "Kenshi_EditBoxStrechEmpty") };
        // The building's picture (docs/game/ui-screens.md "Building icons"): the game's saved one, else the renderer's drawing; the frame stays empty while it loads.
        if (BuildingImage(building) is { } image) b.Properties["BuildingImage"] = image;
        else b.Hidden.Add("BuildingImage");
        b.Text["CommandsTextBox"] = player.levelEditorPanel.levelBuild ? string.Join('\n', LevelEditorPanel.LevelCommandLines) : BuildCommandsText();
        b.Properties["CommandsTextBox"] = new Dictionary<string, string> { ["WordWrap"] = "true", ["TextAlign"] = "Left Top", ["TextColour"] = MyGuiColours.TemplateInk(player.uiResources, "Kenshi_GenericTextBox") };
        b.Text["FloorText"] = Loc.Tr("Floor {1,num}", player.Floor?.Get() ?? 0);
        Enable("FloorArrowUp", "FloorArrowUp");
        Enable("FloorArrowDown", "FloorArrowDown");
        Enable("BuildButton", "BuildButton");
        Enable("ConfirmButton", "ConfirmButton");
        Enable("UndoButton", "UndoButton");
        if (buildQueue.Count == 0) { b.Disabled.Add("ConfirmButton"); b.Disabled.Add("UndoButton"); }
        Enable("BuldingTypePrevButton", "BuldingTypePrevButton");
        Enable("BuldingTypeNextButton", "BuldingTypeNextButton");
        if (group is not { Members.Count: > 1 }) { b.Disabled.Add("BuldingTypePrevButton"); b.Disabled.Add("BuldingTypeNextButton"); }
        // The failure the ghost has now, where the original puts its messages.
        b.Properties["MessagePanel"] = new Dictionary<string, string> { ["Visible"] = buildMessage is null ? "false" : "true" };
    }

    /// <summary>The <c>BuildingImage</c> properties showing <paramref name="building"/>'s picture once it is on the GPU, null until then (or without one).</summary>
    Dictionary<string, string>? BuildingImage(BuildingBuildData? building)
    {
        if (building is null || buildHost is not { } host || player.uiPainter is not { } painter) return null;
        if (host.IconFile(building.Id) is { } file)
            return painter.IconReady(file) ? new() { ["ImageTexture"] = Meitou.Game.Rendering.Ui.UiPainter.IconTexture(file), ["Caption"] = "" } : null;
        string rendered = Meitou.Game.Rendering.Ui.UiPainter.RenderedBuildingIconTexture(building.Id);
        return painter.RenderedIconReady(rendered) ? new() { ["ImageTexture"] = rendered, ["ImageFlipV"] = "true", ["Caption"] = "" } : null;
    }

    /// <summary>
    /// The Commands box (<c>FUN_1404d7d90</c>, <b>Verified</b>, the key strings read from the exe's data): per line a label in the dark tag, a space and the value in the light tag, the lines
    /// joined by line breaks; the last reads "Press [Confirm] when finished". The texts are the original's, not Meitou's bindings (Space also places, Escape leaves build mode).
    /// </summary>
    static string BuildCommandsText()
    {
        const string Label = "#140806", Value = "#afa68b";
        static string Row(string label, string value) => Label + Loc.Tr(label) + Value + " " + value;
        return string.Join('\n',
            Row("Place Building:", Loc.Tr("Left-Click")), Row("Clear Building:", Loc.Tr("Right-Click")),
            Row("Rotate:", Loc.Tr("{1} or {2} keys", "<>", ",.")), Row("Change Height:", Loc.Tr("{1} and {2}", "+", "-")),
            Row("Change Slope:", Loc.Tr("{1} and {2}", "[", "]")), Row("Undo:", Loc.Tr("Backspace")), Row("Cancel All:", Loc.Tr("Esc")),
            Label + Loc.Tr("Press") + " " + Value + "[" + Loc.Tr("Confirm") + "]" + Label + " " + Loc.Tr("when finished"));
    }

    /// <summary>The stats panel's lines, the failure message and the queue count, over the arranged window.</summary>
    internal void AddBuildContent(List<UiControl> controls)
    {
        if (buildHost is not { } host) return;
        if (CurrentBuilding() is { } building)
        {
            var lines = BuildMenu.Stats(building, host.ItemName).Select(l => new PlayerInterface.HudLine(l.Label, l.Value)).ToList();
            if (controls.FirstOrDefault(c => c.Name == "StatsPanel") is { } panel) player.AddLines(controls, "StatsPanel", lines, MathF.Max(14, panel.Bounds.Height / 11));
        }
        if (buildMessage is { } message && controls.FirstOrDefault(c => c.Name == "MessagePanel") is { } messagePanel)
            controls.Add(new("MessageText", "TextBox", messagePanel.Bounds, Loc.Tr(message), "", "Kenshi_TextboxStandardText", true,
                new Dictionary<string, string> { ["FontName"] = "Kenshi_PaintedTextFont_Medium", ["TextAlign"] = "Left VCenter", ["TextColour"] = "1 0.45 0.38" }, Vector4.Zero, Pickable: false, Clip: messagePanel.Clip));
        if (buildQueue.Count > 0 && controls.FirstOrDefault(c => c.Name == "ButtonPanel") is { } buttons)
            controls.Add(new("QueueText", "TextBox", new(buttons.Bounds.X, buttons.Bounds.Y - 20, buttons.Bounds.Width, 18), $"{buildQueue.Count} placed", "", "Kenshi_TextboxStandardText", true,
                new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium", ["TextAlign"] = "Right VCenter", ["TextColour"] = MyGuiColours.TemplateInk(player.uiResources, "Kenshi_TextboxStandardText") }, Vector4.Zero, Pickable: false));
    }

    /// <summary>A button or list of the window.</summary>
    internal void BuildAction(MyGuiEvent e)
    {
        switch (e.Widget, e.Action)
        {
            case ("CategoriesList", "select"):
                buildCategory = e.Text;
                buildDiscarded = false;
                buildGroup = 0;
                buildMember = null;
                break;
            case ("BuildingsList", "select"):
                if (!buildDiscarded && (int)e.Value == buildGroup) { player.levelEditorPanel.DropPlacementChoice(); break; }
                buildGroup = (int)e.Value;
                buildDiscarded = false;
                buildMember = null;
                ghostLift = 0;
                break;
            case ("BuldingTypePrevButton", _): StepVariant(-1); break;
            case ("BuldingTypeNextButton", _): StepVariant(1); break;
            case ("ConfirmButton", _): BuildConfirm(); break;
            case ("UndoButton", _): BuildUndo(); break;
            case ("BuildButton", _): SetBuildMode(false); break;
            case ("FloorArrowUp", _): player.Floor?.Step(1); break;
            case ("FloorArrowDown", _): player.Floor?.Step(-1); break;
        }
    }

    // ---- construction sites: picking and orders ----

    // A right press on a site: when, where and which, for the quick order (build) given on release or the menu opened after the hold.
    (BuildingSnapshot Site, Vector2 At, long Since)? sitePending;

    /// <summary>The nearest of the player's sites (and finished buildings) under a pixel, by a ray against each one's footprint prism (a sphere round its position without a footprint).</summary>
    internal BuildingSnapshot? PickSite(Vector2 pixel) => PickSiteAt(pixel)?.Site;

    /// <summary><see cref="PickSite"/> with the distance along the ray.</summary>
    internal (BuildingSnapshot Site, float T)? PickSiteAt(Vector2 pixel)
    {
        if (buildHost is not { } host || player.session.CurrentSnapshot.Buildings.Count == 0 || player.Ray(pixel) is not { } ray) return null;
        BuildingSnapshot? best = null;
        // Only what the ray meets before the ground counts, as in PickBuilding and the original's raycast (terrain ends the walk): a building's hull and collision reach below the ground,
        // and the part of the ray that has gone through the ground must not find them (the pointer showed the hammer on bare ground beside a building).
        float ground = player.GroundHit(ray.Origin, ray.Direction) is { } groundPoint ? Vector3.Distance(ray.Origin, groundPoint) : float.MaxValue, bestT = ground;
        var hits = new List<(BuildingSnapshot Site, float T)>();
        foreach (var site in player.session.CurrentSnapshot.Buildings)
        {
            var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, PlacementCheck.YawOf(site.Rotation));
            // No selectable geometry: an invisible sitting node must leave the click to ground movement.
            var bounds = host.Placement.Footprint(site.Record);
            if (bounds is null && player.production.Machine(site.Id)?.Function is TownBuildings.ChairFunction or TownBuildings.ThroneFunction && host.Parts(site.Record).Count == 0) continue;
            float? t = bounds is { } footprint
                ? player.BuildingRayT(ray, footprint.Place(site.Position, yaw), footprint.YMin, footprint.YMax, site.Record, site.Position, site.Position.Y, yaw)
                : host.Placement.Plot(site.Record) is { } plot ? PlayerInterface.PlotT(ray, plot.Place(site.Position, yaw), ground < float.MaxValue ? ground : null)
                : PlayerInterface.RaySegment(ray.Origin, ray.Direction, site.Position, site.Position + new Vector3(0, 16, 0), 12);
            if (t is not { } hit || hit >= ground) continue;
            hits.Add((site, hit));
            if (hit < bestT) (bestT, best) = (hit, site);
        }
        // A building placed inside the one hit first (its roof or walls, hidden in the interior view) is what the click means, when the view shows that building's inside there.
        while (best is not null && hits.Where(h => h.Site.Parent == best.Id && !player.Hidden(ray, h.T)).OrderBy(h => h.T).FirstOrDefault() is { Site: not null } inside)
            (best, bestT) = inside;
        return best is null ? null : (WallOf(best), bestT);
    }

    /// <summary>The wall an attached short piece, connector or wedge belongs to (its orders, name and materials are the wall's), else the site itself.</summary>
    BuildingSnapshot WallOf(BuildingSnapshot site)
    {
        for (int guard = 0; guard < 64 && site.WallParent is { } up && player.session.CurrentSnapshot.Buildings.FirstOrDefault(s => s.Id == up) is { } wall; guard++) site = wall;
        return site;
    }

    /// <summary>The distance at which a ray first enters a vertical prism over a convex polygon (the Y range grown a little), or null.</summary>
    internal static float? RayPrism(Vector3 origin, Vector3 direction, Vector2[] polygon, float yMin, float yMax)
    {
        float t0 = 0, t1 = float.MaxValue;
        // The slab in Y.
        if (MathF.Abs(direction.Y) < 1e-6f) { if (origin.Y < yMin || origin.Y > yMax) return null; }
        else
        {
            float a = (yMin - origin.Y) / direction.Y, c = (yMax - origin.Y) / direction.Y;
            t0 = MathF.Max(t0, MathF.Min(a, c));
            t1 = MathF.Min(t1, MathF.Max(a, c));
        }
        // The convex polygon in X/Z: clip the ray against each edge's half plane.
        float area = 0;
        for (int i = 0; i < polygon.Length; i++) area += polygon[i].X * polygon[(i + 1) % polygon.Length].Y - polygon[(i + 1) % polygon.Length].X * polygon[i].Y;
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
            if (MathF.Abs(slope) < 1e-6f) { if (d0 > 0) return null; continue; }
            float t = -d0 / slope;
            if (slope > 0) t1 = MathF.Min(t1, t); else t0 = MathF.Max(t0, t);
        }
        return t0 <= t1 ? t0 : null;
    }

    /// <summary>The name of a site's building, for the menu's title.</summary>
    internal string SiteName(BuildingSnapshot site) => site.LevelPlacement is not null ? site.Name : buildHost?.Menu.Catalog.FirstOrDefault(b => b.Id == site.Record)?.Name ?? site.Record;

    /// <summary>
    /// The verbs the selection has for a site (the original's building branch, docs/game/ui-input.md "Context menu verbs"): Build (2) for an unfinished one (the quick order); finished, a
    /// machine's Use (87) or an automatic machine's Auto-Haul (152), a storage's Auto-Haul (124, the "Hauling to" job) and Inventory (26), a furnace's Inventory, a turret's Man Turret (146);
    /// Dismantle (96); Meitou's Cancel for a site nothing has been delivered to and the building panel's door buttons. Repair (95) shares construction work; town ruins start rebuilding only on an explicit order.
    /// </summary>
    internal List<(string Caption, Action Give)> SiteVerbs(BuildingSnapshot site)
    {
        var verbs = new List<(string, Action)>();
        var selection = player.SelectedIds();
        if (selection.Count == 0) return verbs;
        // Build is offered on any standing unfinished building, whoever owns it (FUN_1407a6bc0, ui-input.md); Repair and the other verbs are the owner's.
        bool own = site.Owner == player.session.World.Player.Faction;
        if (own && (site.Destroyed || site.Complete && site.NeedsWork)) verbs.Add(("Repair", () => OrderConstruction(site, selection)));
        else if (!site.Complete && !site.Destroyed) verbs.Add(("Build", () => OrderConstruction(site, selection)));
        player.production.AddMachineVerb(verbs, site, selection);
        if (site.Complete && player.production.Machine(site.Id) is { } machine)
        {
            if (machine.IsStorage)
                // Order 124: one haul trip, with Shift the Hauling to job (FUN_1407fa650).
                verbs.Add(("Auto-Haul", () => player.Say(player.SlowModifier
                    ? (Meitou.Simulation.SimCommand)new Meitou.Simulation.Production.AddJob(selection, Meitou.Data.Gameplay.Ai.TaskType.OPERATE_STORAGE, site.Id) { Tick = player.session.World.Tick }
                    : new Meitou.Simulation.Production.OperateOrder(selection, site.Id) { Tick = player.session.World.Tick })));
            // With Shift the dispatch turns Inventory (26) on a storage into Hauling to (124) and Man Turret (146) into Turret guard (234) (FUN_1407fa650, docs/game/ui-input.md "Click").
            if (machine.IsStorage)
                verbs.Add(("Inventory", () => { if (player.SlowModifier) player.Say(new Meitou.Simulation.Production.AddJob(selection, Meitou.Data.Gameplay.Ai.TaskType.OPERATE_STORAGE, site.Id) { Tick = player.session.World.Tick }); else OpenStorage(site); }));
            else if (machine.Function == MachineData.ItemFurnaceFunction) verbs.Add(("Inventory", () => OpenStorage(site)));
            // A corpse machine (function 11) offers Corpse Disposal (136): with Shift the Corpse disposal job (137 and its finder 138), else the carried corpses go in.
            if (own && machine.Function == Meitou.Data.World.TownBuildings.CorpseDisposalFunction)
                verbs.Add(("Corpse Disposal", () => player.Say(player.SlowModifier
                    ? (Meitou.Simulation.SimCommand)new Meitou.Simulation.Production.AddJob(selection, Meitou.Data.Gameplay.Ai.TaskType.FEED_CORPSE_INTO_MACHINE, site.Id) { Tick = player.session.World.Tick }
                    : new Meitou.Simulation.Production.DisposeCorpseOrder(selection, site.Id) { Tick = player.session.World.Tick })));
            if (machine.Function == Meitou.Data.World.ObjectRanges.TurretFunction)
                verbs.Add(("Man Turret", () => player.Say(player.SlowModifier
                    ? (Meitou.Simulation.SimCommand)new Meitou.Simulation.Production.AddJob(selection, Meitou.Data.Gameplay.Ai.TaskType.USE_TURRET, site.Id) { Tick = player.session.World.Tick }
                    : new Meitou.Simulation.Production.UseTurretOrder(selection, site.Id) { Tick = player.session.World.Tick })));
        }
        if (own && site.LevelPlacement is null) verbs.Add(("Dismantle", () => player.Say(new DismantleOrder(selection, site.Id) { Tick = player.session.World.Tick })));
        if (site.LevelPlacement is null && !site.Complete && site.Progress <= 0 && site.Materials.All(m => m.Delivered <= 0))
            verbs.Add(("Cancel Construction", () => player.Say(new CancelSite(site.Id) { Tick = player.session.World.Tick })));
        // A finished building's doors: the door panel's open/close and lock buttons, for all its doors at once (docs/simulation.md "Cages, beds and locks").
        var doors = player.session.CurrentSnapshot.Doors.Where(d => d.Key.Building == site.Id).Select(d => d.Value).ToList();
        if (site.Complete && doors.Count > 0)
        {
            bool open = doors.Any(d => d.Motion == Meitou.Simulation.Locks.DoorMotion.Open);
            verbs.Add((open ? "Close Doors" : "Open Doors", () => player.Say(new Meitou.Simulation.Locks.ToggleDoorsOrder(site.Id) { Tick = player.session.World.Tick })));
            // The Security button's sound (FUN_140547060): Build_Lock is the state being left.
            bool locked = doors.All(d => d.WantsLock);
            verbs.Add((locked ? "Unlock Doors" : "Lock Doors", () =>
            {
                player.interfaceSounds.Sound("Building_Lock_Unlock", "Build_Lock", locked ? "Lock_Building" : "Unlock_Building");
                player.Say(new Meitou.Simulation.Locks.ToggleDoorLocksOrder(site.Id) { Tick = player.session.World.Tick });
            }));
        }
        return verbs;
    }

    void OrderConstruction(BuildingSnapshot site, IReadOnlyList<CharacterId> selection)
    {
        player.Say(new BuildOrder(selection, site.Id) { Tick = player.session.World.Tick });
        if (player.SlowModifier) player.Say(new Meitou.Simulation.Production.AddJob(selection,
            site.Destroyed || site.Complete ? Meitou.Data.Gameplay.Ai.TaskType.REPAIR : Meitou.Data.Gameplay.Ai.TaskType.BUILD,
            site.Id) { Tick = player.session.World.Tick });
    }

    /// <summary>A right press on a site with characters selected: a click released within the hold time orders the build, held longer it opens the menu (true: taken).</summary>
    internal bool SitePress(Vector2 p)
    {
        if (player.SelectedIds().Count == 0 || PickSite(p) is not { } site) return false;
        // A finished plain building does not take the click when a container, an item, a door or a character is under the cursor (a chest inside a house).
        if (!SiteIsObject(site) && SomethingInsideAt(p)) return false;
        // With Shift a storage takes a job even without verbs.
        if (SiteVerbs(site).Count == 0 && !(player.SlowModifier && site.Complete && player.production.Machine(site.Id) is { IsStorage: true })) return false;
        sitePending = (site, p, System.Diagnostics.Stopwatch.GetTimestamp());
        return true;
    }

    /// <summary>
    /// The right button let go: a press on a site released before the menu opened gives the quick order (Build; Operate a finished machine). With Shift a finished machine or storage is
    /// added to the selection's job lists instead (the original's Perma-Jobs: Operating machine, Hauling to; on an unfinished site, Engineer).
    /// </summary>
    internal void SiteRelease()
    {
        if (sitePending is not { } pending) return;
        sitePending = null;
        if (player.SlowModifier && pending.Site.Complete && player.production.Machine(pending.Site.Id) is { } job && (job.Operable || job.IsStorage || job.Function is Meitou.Data.World.ObjectRanges.TurretFunction or MachineData.TrainingFunction or TownBuildings.ChairFunction or TownBuildings.ThroneFunction))
        {
            // A turret: 146 USE_TURRET, which becomes the job 234 Turret guard (PermaJobs).
            var type = job.Function == Meitou.Data.World.ObjectRanges.TurretFunction ? Meitou.Data.Gameplay.Ai.TaskType.USE_TURRET
                : job.Function == MachineData.TrainingFunction ? Meitou.Data.Gameplay.Ai.TaskType.USE_TRAINING_DUMMY
                : job.Function is TownBuildings.ChairFunction or TownBuildings.ThroneFunction ? Meitou.Data.Gameplay.Ai.TaskType.OPERATE_MACHINERY
                : job.Operable ? (job.IsAutomatic ? Meitou.Data.Gameplay.Ai.TaskType.OPERATE_AUTOMATIC_MACHINERY : Meitou.Data.Gameplay.Ai.TaskType.OPERATE_MACHINERY)
                : Meitou.Data.Gameplay.Ai.TaskType.OPERATE_STORAGE;
            player.Say(new Meitou.Simulation.Production.AddJob(player.SelectedIds(), type, pending.Site.Id) { Tick = player.session.World.Tick });
            return;
        }
        // With Shift the Build order becomes the Engineer job (125), which builds any unfinished site.
        if (QuickConstruction(pending.Site)) OrderConstruction(pending.Site, player.SelectedIds());
        else if (player.production.Machine(pending.Site.Id) is { Operable: true } or { Function: MachineData.TrainingFunction or TownBuildings.ChairFunction or TownBuildings.ThroneFunction }) player.Say(new Meitou.Simulation.Production.OperateOrder(player.SelectedIds(), pending.Site.Id) { Tick = player.session.World.Tick });
        else if (player.production.Machine(pending.Site.Id) is { Function: Meitou.Data.World.ObjectRanges.TurretFunction })
            player.Say(new Meitou.Simulation.Production.UseTurretOrder(player.SelectedIds(), pending.Site.Id) { Tick = player.session.World.Tick });   // the original's order 146 USE_TURRET
        else if (player.production.Machine(pending.Site.Id) is { IsStorage: true } or { Function: MachineData.ItemFurnaceFunction }) OpenStorage(pending.Site);
        else if (!SiteIsObject(pending.Site) && player.Ray(pending.At) is { } ray && player.MoveHit(ray.Origin, ray.Direction) is { } hit)
        {
            // A finished plain building's quick right click is a move to the point under the cursor (the original: order 29 MOVE_CUS_ORDERED). The original's target is the building
            // hit's x, z at the terrain height, which the path's goal snap puts on the floor inside or the ground outside; a pick that went on through the roof or wall to the
            // terrain beyond takes that point instead, so the character goes in rather than to the ground behind. A building floor the pick found (a site with an interior,
            // the hidden roof above it nearer) stays: the terrain under the roof hit lay below the floor.
            float distance = Vector3.Distance(ray.Origin, hit);
            if (player.BuildingMoveHit?.Invoke(ray.Origin, ray.Direction, distance + 1) is null && PickSiteAt(pending.At) is { } at && at.Site.Id == pending.Site.Id && at.T + 1 < distance)
            {
                var p = ray.Origin + ray.Direction * at.T;
                hit = new Vector3(p.X, player.ground(p.X, p.Z), p.Z);
            }
            player.Move(hit, player.SlowModifier);
            player.PlaceMarker(hit);
        }
    }

    /// <summary>
    /// A finished storage or furnace of the player's: the selection walks to it, and on arrival its window opens with the character's inventory beside it (the original's order 26,
    /// LOOT_TARGET, pointer search). A left click on a storage shows its window alone.
    /// </summary>
    void OpenStorage(BuildingSnapshot site)
    {
        player.Move(site.Position, false);
        storageOpening = (site, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    /// <summary>A storage the selection was sent to open (a quick right click) and when; it opens when one of them is near enough, or is dropped when they stop short.</summary>
    (BuildingSnapshot Site, long Since)? storageOpening;

    /// <summary>The storage whose window shows the character's inventory beside it (opened by an order, not by selecting it).</summary>
    internal string? storageWithInventory;

    /// <summary>How near (X/Z) a selected character must be to open the storage it was sent to; a character takes from a machine within 200.</summary>
    const float StorageArrive = 120;

    /// <summary>Once a frame: the storage a right click sent the selection to opens on arrival.</summary>
    void UpdateStorageOpening()
    {
        if (storageOpening is not { } pending) return;
        var selected = player.Selected();
        double waited = System.Diagnostics.Stopwatch.GetElapsedTime(pending.Since).TotalSeconds;
        if (selected.Count == 0 || waited > 120 || player.session.CurrentSnapshot.Buildings.All(b => b.Id != pending.Site.Id)) { storageOpening = null; return; }
        var at = new Vector2(pending.Site.Position.X, pending.Site.Position.Z);
        if (selected.Any(c => Vector2.Distance(new(c.Position.X, c.Position.Z), at) <= StorageArrive))
        {
            storageOpening = null;
            // A window shown alone (a left click) or closed by the player opens again through SyncMachineScreens, with the inventory beside it, as when the order opens both.
            bool docked = storageWithInventory == pending.Site.Id && player.faithfulScreens?.IsOpen("inventory") == true;
            if (!docked && player.production.shownMachine == pending.Site.Id) player.production.shownMachine = null;
            if (player.production.closedMachine == pending.Site.Id) player.production.closedMachine = null;
            storageWithInventory = pending.Site.Id;
            player.buildingSelection.SetBuilding(player.buildingSelection.SiteBuilding(pending.Site));
        }
        else if (waited > 2 && selected.All(c => c.Task != Meitou.Simulation.Movement.CharacterTask.GoTo)) storageOpening = null;   // they stopped short: another order was given
    }

    /// <summary>Once a frame: a site press held long enough opens its menu.</summary>
    void UpdateSite()
    {
        if (sitePending is not { } pending || System.Diagnostics.Stopwatch.GetElapsedTime(pending.Since).TotalSeconds < PlayerInterface.ContextHoldSeconds) return;
        sitePending = null;
        if (player.session.CurrentSnapshot.Buildings.FirstOrDefault(s => s.Id == pending.Site.Id) is not { } site) return;
        player.contextMenu = player.Menu(SiteName(site), pending.At, SiteVerbs(site));
    }

    /// <summary>A site the quick Build order and the hammer pointer apply to: unfinished, except a purchased town ruin, which needs the held menu's Repair until reconstruction starts.</summary>
    static bool QuickConstruction(BuildingSnapshot site) => site.NeedsWork && !(site.LevelPlacement is not null && site.Destroyed);

    /// <summary>
    /// Whether a site is an object to act on, not a plain building: an unfinished one, or a finished machine, storage, furnace or turret. The original's pointer is the hammer (<c>build</c>)
    /// only for an unfinished building; a finished plain building is the arrow, and its quick order is a move (docs/game/ui-input.md "Building hover and click").
    /// </summary>
    internal bool SiteIsObject(BuildingSnapshot site) =>
        QuickConstruction(site) || player.production.Machine(site.Id) is { Operable: true } or { IsStorage: true } or { Function: MachineData.ItemFurnaceFunction or MachineData.TrainingFunction or TownBuildings.ChairFunction or TownBuildings.ThroneFunction or Meitou.Data.World.ObjectRanges.TurretFunction };

    /// <summary>
    /// The pointer over a site with characters selected: <c>build</c> while unfinished (the original's pointer mode 0xb), <c>search</c> over a storage, else the class's slot 130 pointer
    /// (docs/game/ui-input.md, its table): <c>mine</c> over a machine of function 1 or 27, <c>use</c> over other machines, a furnace or a turret; none over a plain building.
    /// </summary>
    internal string? SitePointer(Vector2 p)
    {
        if (player.SelectedIds().Count == 0 || PickSite(p) is not { } site) return null;
        if (QuickConstruction(site)) return "build";
        if (player.production.Machine(site.Id) is not { } m || !SiteIsObject(site)) return null;
        if (m.IsStorage) return "search";   // a finished storage: the original's pointer for opening it
        return m.Function is MachineData.MineFunction or MachineData.NaturalMineFunction ? "mine" : "use";
    }

    /// <summary>Something more specific than a building lies under the pixel: a character, an item on the ground, a container, a door or a resource rock. A plain building gives way to it.</summary>
    bool SomethingInsideAt(Vector2 p) => player.PickWhere(p, _ => true) is not null || player.groundItemPicks.PickGroundItem(p) is not null || player.furniture.PickContainer(p) is not null || player.furniture.PickDoor(p) is not null || player.furniture.PickRock(p) is not null || player.furniture.PickBed(p) is not null || player.furniture.PickDummyAt(p) is not null || player.furniture.PickSeat(p) is not null;

    // The resource popup over a mine's, well's or farm's ghost (docs/game/buildings-production.md "Placement", <c>FUN_1404d9ea0</c>): the resource with its value, and for a farm the crop
    // yield and the environment, each value red to green. Drawn as a floating tooltip with its bottom centred over the top of the ghost's collision box (the original's popup sits above the
    // box's projected top; its exact x is an engine choice).
    readonly Dictionary<string, (Vector3 Min, Vector3 Max)?> ghostBoxes = new(StringComparer.Ordinal);

    internal void AddBuildReadout(List<UiControl> controls)
    {
        if (!buildMode || buildHost is not { } host || ghost is not { } g || player.uiPainter is not { } painter) return;
        if (host.Placement.Readout(g.Building.Id, g.Position) is not { } readout) return;

        // The original's label is the resource's name and a colon (FUN_140a0c300's names), the value int(v x 100) and "%", coloured (1 - v, v, 0).
        string name = readout.Resource switch
        {
            ResourceField.IronResource => "Iron", ResourceField.StoneResource => "Stone", ResourceField.CopperResource => "Copper", ResourceField.CarbonResource => "Carbon",
            MachineData.WaterResource => "Water", _ => "Fertility",
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
            (text, wrap) => painter.MeasureText(TooltipRuntime.TooltipFont, text, player.Height, wrap, minimum) is var s ? new(s.Width, s.Height) : default).Panel.Width;
        player.tooltipRuntime.PlaceTooltip(controls, painter, lines, TooltipKind.Floating, null, "build-readout", top with { X = top.X + width / 2 }, colours);
    }

    /// <summary>The pixel at the middle top of the ghost's collision box (its corners projected: their mean x, their highest y); null without a box or behind the camera.</summary>
    Vector2? GhostTop(Building.BuildServices host, string record, Vector3 position, Quaternion rotation)
    {
        if (!ghostBoxes.TryGetValue(record, out var box))
        {
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (var (vertices, _) in host.Placement.CollisionMesh(record))
                foreach (var v in vertices) (min, max) = (Vector3.Min(min, v), Vector3.Max(max, v));
            ghostBoxes[record] = box = min.X <= max.X ? (min, max) : null;
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
}
