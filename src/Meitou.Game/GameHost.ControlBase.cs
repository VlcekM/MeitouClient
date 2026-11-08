using System.Globalization;
using System.Numerics;
using Meitou.Game.DevTools;
using Meitou.Simulation;
using Meitou.Simulation.Locks;

namespace Meitou.Game;

/// <summary>
/// Agent-playtest control channel, base building (docs/procedures/game-launch-procedure.md "Driving a running game by command"): list the placeable buildings, show what a building holds,
/// put items into containers or on the ground, and measure the placed buildings' footprints against each other.
/// </summary>
sealed partial class GameHost
{
    void RegisterControlBaseCommands(Action<string, string, string, Action<IReadOnlyList<string>, DevConsole>> add, Func<DevConsole, bool> loaded)
    {
        add("buildings", "buildings [text]", "list the placeable BUILDING records (id, name, category, path mode, furniture flags) whose id, name or category contains text", (args, con) =>
        {
            string text = args.Count > 0 ? string.Join(' ', args) : "";
            var list = BuildData().Values.Where(b => text.Length == 0 || b.Id.Contains(text, StringComparison.OrdinalIgnoreCase) || b.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                || b.Category.Contains(text, StringComparison.OrdinalIgnoreCase)).OrderBy(b => b.Category, StringComparer.Ordinal).ThenBy(b => b.Name, StringComparer.Ordinal).ToList();
            con.Print($"{list.Count} buildings");
            foreach (var b in list.Take(400))
                con.Print($"{b.Id} | {b.Name} | {b.Category} | path {b.PathMode}{(b.IsInteriorFurniture ? " | interior furniture" : "")}{(b.IsExteriorFurniture ? " | exterior furniture" : "")}{(b.LinksTogether ? " | wall" : "")}");
        });

        add("layouts", "layouts <building record id or name> [exterior]", "the interior (or exterior) layouts a building record has: name, parts, bar / shop flags, the kinds of parts (what place-building --interior takes)", (args, con) =>
        {
            if (args.Count is < 1 or > 2 || scene.Database is not { } db) { con.Error("usage: layouts <building record id or name> [exterior]"); return; }
            var record = db.Find(args[0]);
            if (record is null && BuildData().Values.FirstOrDefault(b => string.Equals(b.Name, args[0], StringComparison.OrdinalIgnoreCase)) is { } bd) record = db.Find(bd.Id);
            if (record is null) { con.Error($"no building '{args[0]}'"); return; }
            bool exterior = args.Count == 2;
            var list = InteriorEditorLayouts(record, exterior);
            con.Print($"{list.Count} {(exterior ? "exterior" : "interior")} layouts for {record.Name} ({record.StringId})");
            foreach (var l in list)
            {
                var parts = scene.Objects is { } o ? o.Layouts.Items(l).GroupBy(i => i.Building.Name).OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Count()}x {g.Key}") : [];
                con.Print($"{l.Name} | {l.Instances.Values.Count(x => !x.IsCleared)} parts{(l.GetBool("is bar") ? " | bar" : "")}{(l.GetBool("is shop") ? " | shop" : "")} | {string.Join(", ", parts)}");
            }
        });

        add("clear-building", "clear-building <building key>", "clear a building by its instance key (a level building or one placed in this session), as the level editor's clear does", (args, con) =>
        {
            if (!loaded(con)) return;
            if (args.Count != 1) { con.Error("usage: clear-building <building key>"); return; }
            con.Print(LevelClear(args[0]) ? $"cleared {args[0]}" : $"no building {args[0]}");
        });

        add("building-info", "building-info <building key>", "what the building with that run-time key holds: fixtures by kind, containers and their items, item slots (total and filled), loose items lying in its slots", (args, con) =>
        {
            if (!loaded(con)) return;
            if (args.Count != 1) { con.Error("usage: building-info <building key>"); return; }
            var world = session.World;
            var fixtures = world.Fixtures.OfBuilding(args[0]);
            var (slots, filled) = world.ItemSlots.OfBuilding(args[0]);
            int lying = world.GroundItems.All.Count(g => g.Slot is { } s && s.StartsWith(args[0] + "/item", StringComparison.Ordinal));
            con.Print($"{fixtures.Count} fixtures: " + string.Join(", ", fixtures.GroupBy(f => f.Kind).Select(g => $"{g.Count()} {g.Key}")) + $"; item slots {slots}, filled {filled}, items lying in them {lying}");
            foreach (var f in fixtures.Where(f => f.Contents is not null))
                con.Print($"container {f.Id} {f.RecordId} items {f.Contents!.Items.Count}: " + string.Join(", ", f.Contents.Items.Take(12).Select(i => $"{i.Quantity}x {i.Name}")));
            foreach (var g in world.GroundItems.All.Where(g => g.Slot is { } s && s.StartsWith(args[0] + "/item", StringComparison.Ordinal)).Take(30))
                con.Print($"slot item {g.Id} {g.Item.Record} {g.Item.Name} at {g.Position.X:F1} {g.Position.Y:F1} {g.Position.Z:F1} slot {g.Slot}");
        });

        add("ground-items", "ground-items <x> <z> <radius>", "count and list (30 at most) the loose items within radius of X Z", (args, con) =>
        {
            if (!loaded(con)) return;
            if (args.Count != 3 || !Num(args[0], out float x) || !Num(args[1], out float z) || !Num(args[2], out float r)) { con.Error("usage: ground-items <x> <z> <radius>"); return; }
            var near = session.World.GroundItems.All.Where(g => Vector2.Distance(new Vector2(g.Position.X, g.Position.Z), new Vector2(x, z)) <= r).ToList();
            con.Print($"{near.Count} items within {r:F0} of {x:F0} {z:F0} (all {session.World.GroundItems.Count} in the world)");
            foreach (var g in near.Take(30))
                con.Print($"item {g.Id} {g.Item.Record} {g.Item.Name} x{g.Item.Quantity} at {g.Position.X:F1} {g.Position.Y:F1} {g.Position.Z:F1}{(g.Slot is null ? "" : " slot " + g.Slot)}");
        });

        add("place-item", "place-item \"<item>\" <x> <z> [yawDegrees] [y] [qty]", "lay a loose item on the ground at X Z (height: the terrain unless y is given as an absolute world height, e.g. a floor)", (args, con) =>
        {
            if (!loaded(con)) return;
            float yaw = 0, y = float.NaN, q = 1;
            if (args.Count < 3 || args.Count > 6 || !Num(args[1], out float x) || !Num(args[2], out float z) || (args.Count > 3 && !Num(args[3], out yaw)) || (args.Count > 4 && !Num(args[4], out y)) || (args.Count > 5 && !Num(args[5], out q)))
            { con.Error("usage: place-item \"<item>\" <x> <z> [yawDegrees] [y] [qty]"); return; }
            if (DebugRecord(args[0], character: false) is not { } item) { con.Error($"no item record matches '{args[0]}'"); return; }
            if (float.IsNaN(y)) y = gpu.Terrain.HeightAt(x, z);
            session.World.Commands.Enqueue(new DebugPlaceItem(item.Id, Math.Clamp((int)q, 1, 1000), new Vector3(x, y, z), yaw * MathF.PI / 180f) { Tick = session.World.Tick });
            con.Print($"placed {item.Name} ({item.Id}) at {x:F1} {y:F1} {z:F1}");
        });

        add("container-put", "container-put <container fixture id> \"<item>\" [qty]", "put items into a container of a building (ids from building-info)", (args, con) =>
        {
            if (!loaded(con)) return;
            int qty = 1;
            if (args.Count < 2 || args.Count > 3 || (args.Count == 3 && !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out qty))) { con.Error("usage: container-put <container fixture id> \"<item>\" [qty]"); return; }
            if (session.World.Fixtures.Find(args[0]) is not { Contents: not null }) { con.Error($"no container '{args[0]}' (building-info lists them)"); return; }
            if (DebugRecord(args[1], character: false) is not { } item) { con.Error($"no item record matches '{args[1]}'"); return; }
            session.World.Commands.Enqueue(new DebugPutItem(args[0], item.Id, Math.Clamp(qty, 1, 1000)) { Tick = session.World.Tick });
            con.Print($"queued {qty} x {item.Name} into {args[0]}");
        });

        add("base-check", "base-check", "footprints of the buildings placed in this session: per building key, record, position, yaw, bounds; and the pair gaps in units (negative: the collision hulls overlap)", (args, con) =>
        {
            if (!loaded(con)) return;
            LevelEditsFollowWorld();
            if (scene.Database is not { } db) { con.Error("no database"); return; }
            var footprints = siteFootprints ??= new Meitou.Simulation.Navigation.BuildingFootprints(install, db, null);
            var items = new List<(string Key, string Id, Vector2[] Hull)>();
            foreach (var p in levelEdits.Added)
            {
                var fp = footprints.Wait(p.BuildingId) ?? footprints.Plot(p.BuildingId);
                if (fp is null) { con.Print($"{p.Key} {p.BuildingId}: no footprint"); continue; }
                var hull = fp.Place(p.Position, p.Rotation);
                var min = new Vector2(float.MaxValue); var max = new Vector2(float.MinValue);
                foreach (var v in hull) { min = Vector2.Min(min, v); max = Vector2.Max(max, v); }
                float yaw = MathF.Atan2(2 * (p.Rotation.W * p.Rotation.Y + p.Rotation.X * p.Rotation.Z), 1 - 2 * (p.Rotation.Y * p.Rotation.Y + p.Rotation.X * p.Rotation.X)) * 180 / MathF.PI;
                con.Print($"building {p.Key} {p.BuildingId} at {p.Position.X:F1} {p.Position.Y:F1} {p.Position.Z:F1} yaw {yaw:F0} bounds x {min.X:F1}..{max.X:F1} z {min.Y:F1}..{max.Y:F1}");
                items.Add((p.Key, p.BuildingId, hull));
            }
            float worst = float.MaxValue; int overlaps = 0;
            for (int i = 0; i < items.Count; i++)
                for (int j = i + 1; j < items.Count; j++)
                {
                    float gap = HullGap(items[i].Hull, items[j].Hull);
                    worst = MathF.Min(worst, gap);
                    if (gap <= 0) overlaps++;
                    con.Print($"gap {items[i].Key} {items[j].Key} {gap:F1}");
                }
            con.Print($"{items.Count} buildings, {overlaps} overlapping pairs, smallest gap {(items.Count > 1 ? worst.ToString("F1", CultureInfo.InvariantCulture) : "n/a")}");
        });
    }

    /// <summary>The gap in X/Z between two convex hulls: negative (the penetration depth of the separating-axis test) when they overlap, else the shortest distance between their edges.</summary>
    static float HullGap(Vector2[] a, Vector2[] b)
    {
        float depth = float.MaxValue;
        foreach (var poly in new[] { a, b })
            for (int i = 0; i < poly.Length; i++)
            {
                var e = poly[(i + 1) % poly.Length] - poly[i];
                var axis = Vector2.Normalize(new Vector2(-e.Y, e.X));
                if (float.IsNaN(axis.X)) continue;
                (float aMin, float aMax) = Project(a, axis); (float bMin, float bMax) = Project(b, axis);
                depth = MathF.Min(depth, MathF.Min(aMax, bMax) - MathF.Max(aMin, bMin));
            }
        if (depth > 0) return -depth;
        float best = float.MaxValue;
        foreach (var (p, q) in new[] { (a, b), (b, a) })
            foreach (var v in p)
                for (int i = 0; i < q.Length; i++)
                {
                    var s = q[i]; var e = q[(i + 1) % q.Length] - s;
                    float t = e.LengthSquared() > 1e-12f ? Math.Clamp(Vector2.Dot(v - s, e) / e.LengthSquared(), 0, 1) : 0;
                    best = MathF.Min(best, Vector2.Distance(v, s + t * e));
                }
        return best;
    }

    static (float Min, float Max) Project(Vector2[] poly, Vector2 axis)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var v in poly) { float d = Vector2.Dot(v, axis); lo = MathF.Min(lo, d); hi = MathF.Max(hi, d); }
        return (lo, hi);
    }
}
