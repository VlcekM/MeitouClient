using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Meitou.Game.DevTools;
using Meitou.Simulation.Navigation;
using Meitou.Simulation.Locks;

namespace Meitou.Game;

/// <summary>Navigation query commands of the agent control channel (docs/procedures/playtest/agent-play-api.md): <c>path-check</c>.</summary>
sealed partial class GameHost
{
    void RegisterControlNavCommands(Action<string, string, string, Action<IReadOnlyList<string>, DevConsole>> add, Func<DevConsole, bool> loaded)
    {
        RegisterControlDoorCommand(add, loaded);
        add("path-check", "path-check <x1> <z1> <x2> <z2> | path-check --id <c:slot.gen> <x> <z>", "ask the game's own navigation query (what a walking character uses, doors as they are now) whether a path exists, without walking: reachable, path length and, when none exists, the doors that block it (the nearest-first doors on the way that are closed to paths, with lock, open, broken state); blockedBy is only searched inside the query's own zone mesh", (args, con) =>
        {
            if (!loaded(con)) return;
            if (nav is null) { con.Error("path-check: no navmesh in this run"); return; }
            string? idText = null;
            var nums = new List<string>();
            for (int i = 0; i < args.Count; i++)
            {
                if (args[i].Equals("--id", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count) idText = args[++i];
                else nums.Add(args[i]);
            }
            var walk = nav.Walkability;
            Vector3 from, to;
            float a = 0, b = 0, c = 0, d = 0;
            if (idText is not null)
            {
                if (!PlayerInterface.TryParseCharacter(idText, out _) || player.ControlPosition(idText.StartsWith("c:", StringComparison.OrdinalIgnoreCase) ? idText : "c:" + idText) is not { } at) { con.Error($"path-check: unknown character '{idText}'"); return; }
                if (nums.Count != 2 || !Num(nums[0], out a) || !Num(nums[1], out b)) { con.Error("usage: path-check --id <c:slot.gen> <x> <z>"); return; }
                from = at; to = new Vector3(a, walk.GroundHeight(a, b), b);
            }
            else
            {
                if (nums.Count != 4 || !Num(nums[0], out a) || !Num(nums[1], out b) || !Num(nums[2], out c) || !Num(nums[3], out d)) { con.Error("usage: path-check <x1> <z1> <x2> <z2> | path-check --id <c:slot.gen> <x> <z>"); return; }
                from = new Vector3(a, walk.GroundHeight(a, b), b); to = new Vector3(c, walk.GroundHeight(c, d), d);
            }
            var inv = CultureInfo.InvariantCulture;
            var path = walk.FindPath(from, to);
            // Both ends on loaded zones and the query still gave only a provisional path: the goal is cut off (a shut door, water, a wall), the path leads to the nearest point reached, then straight on.
            bool bothLoaded = walk.World.Contains(Meitou.Data.World.WorldLayout.ZoneOf(from.X, from.Z)) && walk.World.Contains(Meitou.Data.World.WorldLayout.ZoneOf(to.X, to.Z));
            // The query also answers "as near as can be got" when the goal lies within its snap distance of reachable ground (just outside a shut door): such a path ends short of the target.
            const float EndTolerance = 1.5f;
            var pathEnd = path.Found ? path.Points[Math.Min(path.Exact, path.Points.Count) - 1] : default;
            bool cutOff = path.Found && bothLoaded && (path.IsProvisional || Vector2.Distance(new(pathEnd.X, pathEnd.Z), new(to.X, to.Z)) > EndTolerance);
            bool reachable = path.Found && !cutOff;
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                w.WriteBoolean("reachable", reachable);
                w.WriteStartArray("from"); w.WriteNumberValue(Math.Round(from.X, 1)); w.WriteNumberValue(Math.Round(from.Z, 1)); w.WriteEndArray();
                w.WriteStartArray("to"); w.WriteNumberValue(Math.Round(to.X, 1)); w.WriteNumberValue(Math.Round(to.Z, 1)); w.WriteEndArray();
                w.WriteNumber("straight", Math.Round(Vector2.Distance(new(from.X, from.Z), new(to.X, to.Z)), 1));
                if (path.Found)
                {
                    double len = 0;
                    for (int i = 1; i < path.Points.Count; i++) len += Vector3.Distance(path.Points[i - 1], path.Points[i]);
                    w.WriteNumber("length", Math.Round(len, 1));
                    w.WriteNumber("points", path.Points.Count);
                    var end = cutOff ? pathEnd : path.Points[^1];
                    w.WriteNumber("ends_short_by", Math.Round(Vector2.Distance(new(end.X, end.Z), new(to.X, to.Z)), 1));
                    if (cutOff) w.WriteBoolean("provisional", true);
                }
                if (!reachable)
                {
                    var blocking = BlockingDoors(walk, from, to, out string why);
                    w.WriteString("why", why);
                    w.WriteStartArray("blockedBy");
                    foreach (var (polygon, building, doorStates) in blocking)
                    {
                        w.WriteStartObject();
                        w.WriteString("polygon", polygon);
                        w.WriteString("building", building);
                        w.WriteBoolean("closedForPaths", true);
                        w.WriteStartArray("doors");
                        foreach (var f in doorStates)
                        {
                            w.WriteStartObject();
                            w.WriteString("fixture", f.Id);
                            w.WriteString("record", f.RecordId);
                            w.WriteBoolean("locked", f.Lock is { EffectivelyLocked: true });
                            w.WriteBoolean("broken", f.DoorBroken);
                            w.WriteString("motion", f.Motion.ToString().ToLowerInvariant());
                            w.WriteNumber("openAmount", Math.Round(f.OpenAmount, 2));
                            w.WriteEndObject();
                        }
                        w.WriteEndArray();
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            controlData = System.Text.Encoding.UTF8.GetString(stream.ToArray());
            con.Print(reachable ? $"reachable, {path.Points.Count} points" : "no path");
        });
    }

    /// <summary>The <c>door</c> command: sets a door fixture's state in a test save (the fields <c>path-check</c> reports) and tells navigation, so the nav door table sees it at once.</summary>
    void RegisterControlDoorCommand(Action<string, string, string, Action<IReadOnlyList<string>, DevConsole>> add, Func<DevConsole, bool> loaded)
    {
        add("door", "door <fixture id> | door nearest <x> <z> [--lock] [--unlock] [--close] [--open] [--break] [--repair] [--all]", "set a door's state for a test (flags apply in the order given): --lock locks it (level at least 1, an open leaf starts closing), --unlock, --close and --open set the leaf at once, --break breaks door and lock, --repair undoes it; --all applies to every door of the building (navigation treats a building as shut only when all its doors are locked); no flag only reports; reply: JSON with the door's lock, broken, motion, openAmount and whether navigation now has the building closed", (args, con) =>
        {
            if (!loaded(con)) return;
            var reg = session.World.Fixtures;
            Fixture? door = null;
            var flags = new List<string>();
            var rest = new List<string>();
            foreach (var a in args) (a.StartsWith("--", StringComparison.Ordinal) ? flags : rest).Add(a.ToLowerInvariant());
            if (rest.Count == 1) door = reg.Find(args.First(a => !a.StartsWith("--", StringComparison.Ordinal)));
            else if (rest.Count == 3 && rest[0] == "nearest" && Num(rest[1], out float nx) && Num(rest[2], out float nz))
            {
                float best = float.MaxValue;
                foreach (var f in reg.All)
                {
                    if (f.Kind != FixtureKind.Door) continue;
                    float dist = Vector2.DistanceSquared(new(f.Position.X, f.Position.Z), new(nx, nz));
                    if (dist < best) { best = dist; door = f; }
                }
            }
            else { con.Error("usage: door <fixture id> | door nearest <x> <z> [--lock] [--unlock] [--close] [--open] [--break] [--repair] [--all]"); return; }
            if (door is null || door.Kind != FixtureKind.Door) { con.Error("door: no such door"); return; }
            foreach (var fl in flags) if (fl is not ("--lock" or "--unlock" or "--close" or "--open" or "--break" or "--repair" or "--all")) { con.Error($"door: unknown flag {fl}"); return; }
            var targets = flags.Contains("--all") ? reg.OfBuilding(door.Building).Where(f => f.Kind == FixtureKind.Door).ToList() : [door];
            if (flags.Any(f => f is "--lock" or "--break") && targets.Any(t => t.Lock is null)) { con.Error("door: this door has no lock"); return; }
            foreach (var t in targets)
            {
                foreach (var fl in flags)
                {
                    switch (fl)
                    {
                        case "--lock": t.Lock!.Level = Math.Max(t.Lock.Level, 1); t.Lock.Locked = true; t.Lock.Broken = false; if (t.Motion is DoorMotion.Open or DoorMotion.Opening) { t.Motion = DoorMotion.Closing; t.AutoOpened = false; } break;
                        case "--unlock": if (t.Lock is { } ul) ul.Locked = false; break;
                        case "--close": t.Motion = DoorMotion.Closed; t.OpenAmount = 0; break;
                        case "--open": t.Motion = DoorMotion.Open; t.OpenAmount = 1; t.DoorProgress = 1; break;
                        case "--break": t.DoorBroken = true; t.Lock!.Broken = true; t.Lock.Locked = false; t.DoorProgress = 0; t.Motion = DoorMotion.Closed; t.OpenAmount = 0; break;
                        case "--repair": t.DoorBroken = false; if (t.Lock is { } rl) rl.Broken = false; t.DoorProgress = 1; break;
                    }
                }
                if (flags.Count > 0) reg.Touch(t);
            }
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                w.WriteString("fixture", door.Id);
                w.WriteString("building", door.Building);
                w.WriteString("record", door.RecordId);
                w.WriteStartArray("pos"); w.WriteNumberValue(Math.Round(door.Position.X, 1)); w.WriteNumberValue(Math.Round(door.Position.Z, 1)); w.WriteEndArray();
                w.WriteNumber("doorsInBuilding", reg.OfBuilding(door.Building).Count(f => f.Kind == FixtureKind.Door));
                w.WriteBoolean("locked", door.Lock is { EffectivelyLocked: true });
                w.WriteNumber("lockLevel", door.Lock?.Level ?? 0);
                w.WriteBoolean("broken", door.DoorBroken);
                w.WriteString("motion", door.Motion.ToString().ToLowerInvariant());
                w.WriteNumber("openAmount", Math.Round(door.OpenAmount, 2));
                if (nav is { } ns) w.WriteBoolean("navClosed", ns.Doors.IsClosed(door.Building));
                w.WriteEndObject();
            }
            controlData = System.Text.Encoding.UTF8.GetString(stream.ToArray());
            con.Print($"door {door.Id}");
        });
    }

    /// <summary>
    /// Why a failed path failed: the same <see cref="NavQuery"/> with the doors table left out finds the corridor the walk would take if no door were shut; the door polygons on it that
    /// the real doors table has closed are the blockers, in order from the start. Empty (with the reason) when the unrestricted query fails too, or the corridor crosses a zone border (only the first zone pair is searched).
    /// </summary>
    List<(string Polygon, string Building, List<Fixture> Doors)> BlockingDoors(NavmeshWalkability walk, Vector3 from, Vector3 to, out string why)
    {
        var result = new List<(string, string, List<Fixture>)>();
        var world = walk.World;
        var free = new NavQuery(world);   // no doors table: every door passable
        if (!world.TryFindPolygon(from, NavQuery.StartSnap, out var start, out var startPoint)) { why = "the start is not on the navmesh"; return result; }
        if (!world.TryFindPolygon(to, NavQuery.GoalSnap, out var goal, out var goalPoint)) { why = "the target is not on the navmesh"; return result; }
        var corridor = free.FindCorridor(start, startPoint, goal, goalPoint, NavAgent.Human);
        if (corridor is null) { why = "no path even with every door open (terrain, water, walls or a zone that is not loaded)"; return result; }
        foreach (var poly in corridor.Polygons)
        {
            var mesh = world.Mesh(poly.Zone);
            if (mesh.DoorOf is not { } of || mesh.DoorIds is not { } ids) continue;
            int di = of[poly.Polygon];
            if (di < 0) continue;
            string building = ids[di];
            if (!walk.Doors.IsClosed(building)) continue;
            if (result.Any(r => r.Item2 == building)) continue;
            result.Add(($"{poly.Zone}.{poly.Polygon}", building, session.World.Fixtures.OfBuilding(building).Where(f => f.Kind == FixtureKind.Door).ToList()));
        }
        why = result.Count > 0 ? "a door on the way is closed for paths" : "the path exists with every door open but no door on it is closed for paths (cause not a door)";
        return result;
    }
}
