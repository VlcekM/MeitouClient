using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Meitou.Game.DevTools;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The agent control channel's world layer (docs/procedures/game-launch-procedure.md "Driving a running game by command"): <c>world</c>, <c>world pick</c>, <c>entity-screen</c>, <c>camera ...</c>, <c>select</c>, <c>order move|attack</c>,
/// <c>pause</c>, <c>resume</c>, <c>speed</c>, and the wait-for keys <c>camera-settled</c>, <c>selected</c> and <c>pos-near</c>. The commands are registered by <see cref="RegisterControlCommands"/>; the data comes from
/// <see cref="PlayerInterface"/> (PlayerInterface.Control.cs), which reads the same snapshot and picking the pointer uses.
/// </summary>
sealed partial class GameHost
{
    /// <summary>The camera at the last poll tick and for how many ticks in a row it did not move (0.25 s each): <c>camera-settled</c> waits for two.</summary>
    (Vector3 Eye, Vector3 Target, float Distance) camStamp;
    int camStable;

    bool CameraSettled => camStable >= 2;

    void ControlCameraTick()
    {
        var c = session.Camera.Current;
        var stamp = (c.Eye, c.Target, c.Distance);
        camStable = stamp == camStamp ? camStable + 1 : 0;
        camStamp = stamp;
    }

    /// <summary>A timed camera move (<c>--over</c>): driven once per frame from <see cref="ControlCameraMoveTick"/>, eased in and out; the command replies when it ends.</summary>
    sealed class CameraMove
    {
        public string? Id;
        public long Start;
        public double Seconds;
        public Vector2 Pivot0, Pivot1;
        public string? Entity;
        public float Yaw0, Yaw1, Pitch0, Pitch1, Dist0, Dist1;
        public bool Follow;
    }

    CameraMove? cameraMove;

    /// <summary>Once per frame, before the camera is drawn: advances the running timed move (smoothstep from start to end) and, at its end, replies to the command that started it.</summary>
    void ControlCameraMoveTick()
    {
        if (cameraMove is not { } m) return;
        double t = Math.Min(1, Stopwatch.GetElapsedTime(m.Start).TotalSeconds / m.Seconds);
        float e = (float)(t * t * (3 - 2 * t));
        var p1 = m.Pivot1;
        if (m.Entity is not null && player.ControlPosition(m.Entity) is { } at) p1 = m.Pivot1 = new Vector2(at.X, at.Z);
        session.Camera.Place(Vector2.Lerp(m.Pivot0, p1, e), m.Yaw0 + (m.Yaw1 - m.Yaw0) * e, m.Pitch0 + (m.Pitch1 - m.Pitch0) * e, m.Dist0 + (m.Dist1 - m.Dist0) * e);
        if (t < 1) return;
        cameraMove = null;
        if (m.Follow && m.Entity is not null && PlayerInterface.TryParseCharacter(m.Entity, out var cid)) player.ControlFocusCharacter(cid);
        camStable = 0;
        if (m.Id is { } id) { string json = ControlCameraJson(); ControlWrite(id, true, "camera move done", json); }
    }

    static bool Num(string text, out float value) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

    /// <summary>Splits <c>--flag v v --flag2 v</c> into flag name to values; the words before the first flag are under "".</summary>
    static Dictionary<string, List<string>> ControlFlags(IReadOnlyList<string> args)
    {
        var map = new Dictionary<string, List<string>> { [""] = [] };
        string current = "";
        foreach (string a in args)
        {
            if (a.StartsWith("--", StringComparison.Ordinal)) { current = a.ToLowerInvariant(); map[current] = []; }
            else map[current].Add(a);
        }
        return map;
    }

    string ControlCameraJson()
    {
        var c = session.Camera.Current;
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("mode", session.Camera.IsFree ? "free" : "strategy");
            void Vec(string name, Vector3 v) { w.WriteStartArray(name); foreach (float f in new[] { v.X, v.Y, v.Z }) w.WriteNumberValue(Math.Round(f, 1)); w.WriteEndArray(); }
            Vec("pivot", c.Target);
            Vec("eye", c.Eye);
            w.WriteNumber("yaw", Math.Round(NormalDegrees(c.Yaw), 1));
            w.WriteNumber("pitch", Math.Round(c.Pitch * 180 / MathF.PI, 1));
            w.WriteNumber("zoom", Math.Round(c.Distance, 1));
            w.WriteNumber("fov", Math.Round(camera.FieldOfView * 180 / MathF.PI, 1));
            w.WriteString("focus", player.Follow is { } f ? $"c:{f.Slot}.{f.Generation}" : null);
            w.WriteBoolean("settled", CameraSettled);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    static float NormalDegrees(float radians)
    {
        float d = radians * 180 / MathF.PI % 360;
        return d > 180 ? d - 360 : d <= -180 ? d + 360 : d;
    }

    void CameraPlace(Vector2 pivot, float? yaw, float? pitch, float? zoom)
    {
        var c = session.Camera.Current;
        session.Camera.Place(pivot, yaw ?? c.Yaw, pitch ?? c.Pitch, zoom ?? c.Distance);
    }

    /// <summary>The world position of an entity id, or an error line for the console.</summary>
    Vector3? ControlEntity(string id, DevConsole con)
    {
        if (player.ControlPosition(id) is { } p) return p;
        con.Error($"no entity '{id}' (ids come from world: c:<slot>.<gen> or b:<key>)");
        return null;
    }

    void RegisterControlWorldCommands()
    {
        var c = devConsole;
        void Add(string name, string usage, string help, Action<IReadOnlyList<string>, DevConsole> run) => c.Register(new ConsoleCommand(name, usage, help, run));
        bool Loaded(DevConsole con)
        {
            if (!Covered && !player.TitleOpen) return true;
            con.Error("no world is loaded yet (state.screen is not world)");
            return false;
        }

        Add("world", "world [--near <id|x z>] [--radius <m>] [--kind character|building|all] [--limit n] | world pick <x> <y> | world pick --entity <id>",
            "JSON array of the entities near a point (default: the camera pivot, radius 300, characters, 50), nearest first; world pick: what a click at a screen pixel hits", (args, con) =>
        {
            if (!Loaded(con)) return;
            if (args.Count > 0 && args[0].Equals("pick", StringComparison.OrdinalIgnoreCase))
            {
                float px = 0, py = 0;
                string? entity = null;
                if (args.Count == 3 && args[1].Equals("--entity", StringComparison.OrdinalIgnoreCase))
                {
                    // Pick at the centre of an entity's on-screen rect (what a click on it would hit); the reply says whether that is the entity itself.
                    entity = args[2];
                    if (player.ControlEntityScreenJson(entity) is not { } rect) { con.Error($"no entity '{entity}'"); return; }
                    using var doc = JsonDocument.Parse(rect);
                    if (!doc.RootElement.TryGetProperty("center", out var centre)) { con.Error($"world pick: {entity} is offscreen"); return; }
                    (px, py) = (centre[0].GetSingle(), centre[1].GetSingle());
                }
                else if (args.Count != 3 || !Num(args[1], out px) || !Num(args[2], out py)) { con.Error("usage: world pick <screen x> <screen y> | world pick --entity <id>"); return; }
                string pick = player.ControlPickJson(new Vector2(px, py));
                if (entity is not null)
                {
                    using var doc = JsonDocument.Parse(pick);
                    bool same = doc.RootElement.TryGetProperty("id", out var got) && string.Equals(got.GetString(), entity, StringComparison.OrdinalIgnoreCase);
                    pick = pick[..^1] + ",\"entity\":" + JsonSerializer.Serialize(entity) + ",\"matches\":" + (same ? "true" : "false") + "}";
                }
                controlData = pick;
                con.Print(pick);
                return;
            }
            var flags = ControlFlags(args);
            foreach (string key in flags.Keys)
                if (key is not ("" or "--near" or "--radius" or "--kind" or "--limit")) { con.Error($"world: unknown flag {key}"); return; }
            if (flags[""].Count > 0) { con.Error($"world: unexpected '{flags[""][0]}'"); return; }
            Vector3 near = session.Camera.Current.Target;
            if (flags.TryGetValue("--near", out var nv))
            {
                if (nv.Count == 1) { if (ControlEntity(nv[0], con) is not { } at) return; near = at; }
                else if (nv.Count == 2 && Num(nv[0], out float nx) && Num(nv[1], out float nz)) near = new Vector3(nx, 0, nz);
                else { con.Error("world: --near needs an entity id or x z"); return; }
            }
            float radius = 300;
            if (flags.TryGetValue("--radius", out var rv) && (rv.Count != 1 || !Num(rv[0], out radius) || radius < 0)) { con.Error("world: --radius needs a number"); return; }
            string kind = "character";
            if (flags.TryGetValue("--kind", out var kv))
            {
                kind = kv.Count == 1 ? kv[0].ToLowerInvariant() : "";
                if (kind is not ("character" or "building" or "all")) { con.Error("world: --kind is character, building or all"); return; }
            }
            int limit = 50;
            if (flags.TryGetValue("--limit", out var lv) && (lv.Count != 1 || !int.TryParse(lv[0], NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1)) { con.Error("world: --limit needs a whole number"); return; }
            string json = player.ControlWorldJson(near, radius, kind, limit);
            PrintData(con, json);
        });

        Add("entity-screen", "entity-screen <id>", "the entity's on-screen rect [x,y,w,h] and centre in screen pixels, or state offscreen; characters also report occluded (something else is under the centre)", (args, con) =>
        {
            if (!Loaded(con)) return;
            if (args.Count != 1) { con.Error("usage: entity-screen <id>"); return; }
            if (player.ControlEntityScreenJson(args[0]) is not { } json) { con.Error($"no entity '{args[0]}'"); return; }
            PrintData(con, json);
        });

        Add("camera", "camera | camera set [--pos x z] [--yaw d] [--pitch d] [--zoom v] | rotate <deg> | pitch <deg> | zoom <v> | focus <id | x z> | orbit <id | x z> <yawDeg>  [--over <s>]",
            "get or move the strategy camera (degrees; yaw/pitch as reported, rotate is relative, pitch/zoom/yaw in set/orbit are absolute; zoom is the boom length 10..2000). --over <s> (0.1..120) makes it a smooth eased move done per frame, the command replies when it ends; for orbit with --over the degrees are relative (the camera swings round the target while it moves there). A new move replaces a running one", (args, con) =>
        {
            if (!Loaded(con)) return;
            float over = 0;
            {
                var list = args.ToList();
                int oi = list.FindIndex(a => a.Equals("--over", StringComparison.OrdinalIgnoreCase));
                if (oi >= 0)
                {
                    if (oi + 1 >= list.Count || !Num(list[oi + 1], out over) || over < 0.1f || over > 120) { con.Error("camera: --over needs seconds between 0.1 and 120"); return; }
                    list.RemoveRange(oi, 2);
                    args = list;
                }
            }
            // The end of the move: target pivot, entity to follow, absolute yaw/pitch/boom (null: unchanged), yaw as a relative amount.
            Vector2? movePivot = null; string? moveEntity = null; float? moveYaw = null, moveYawBy = null, movePitch = null, moveZoom = null; bool moveFollow = false;
            string sub = args.Count > 0 ? args[0].ToLowerInvariant() : "";
            if (sub.Length == 0) { string json = ControlCameraJson(); PrintData(con, json); return; }
            if (cameraMove is { } running)
            {
                cameraMove = null;
                if (running.Id is { } oldId) ControlWrite(oldId, true, "camera move replaced by a newer one", ControlCameraJson());
            }
            var strat = session.Camera.Strategy;
            var view = session.Camera.Current;
            var pivot = strat.PivotXZ;
            float deg = 0;
            bool oneNumber = args.Count == 2 && Num(args[1], out deg);
            switch (sub)
            {
                case "set":
                {
                    var flags = ControlFlags(args.Skip(1).ToList());
                    float? yaw = null, pitch = null, zoom = null;
                    foreach (var (key, values) in flags)
                    {
                        if (key.Length == 0) { if (values.Count > 0) { con.Error("camera set: unexpected " + values[0]); return; } continue; }
                        if (key == "--pos")
                        {
                            if (values.Count != 2 || !Num(values[0], out float x) || !Num(values[1], out float z)) { con.Error("camera set: --pos needs x z"); return; }
                            pivot = new Vector2(x, z);
                        }
                        else if (key is "--yaw" or "--pitch" or "--zoom")
                        {
                            if (values.Count != 1 || !Num(values[0], out float v)) { con.Error($"camera set: {key} needs a number"); return; }
                            if (key == "--yaw") yaw = v * MathF.PI / 180; else if (key == "--pitch") pitch = v * MathF.PI / 180; else zoom = v;
                        }
                        else { con.Error($"camera set: unknown flag {key}"); return; }
                    }
                    player.Follow = null;
                    if (over > 0) { movePivot = pivot; moveYaw = yaw; movePitch = pitch; moveZoom = zoom; break; }
                    CameraPlace(pivot, yaw, pitch, zoom);
                    break;
                }
                case "rotate":
                    if (!oneNumber) { con.Error("usage: camera rotate <degrees> (relative)"); return; }
                    if (over > 0) { moveYawBy = deg * MathF.PI / 180; break; }
                    CameraPlace(pivot, view.Yaw + deg * MathF.PI / 180, null, null);
                    break;
                case "pitch":
                    if (!oneNumber) { con.Error("usage: camera pitch <degrees above the horizon> (absolute; the game limits it)"); return; }
                    if (over > 0) { movePitch = deg * MathF.PI / 180; break; }
                    CameraPlace(pivot, null, deg * MathF.PI / 180, null);
                    break;
                case "zoom":
                    if (!oneNumber) { con.Error("usage: camera zoom <boom length 10..2000> (absolute)"); return; }
                    if (over > 0) { moveZoom = deg; break; }
                    CameraPlace(pivot, null, null, deg);
                    break;
                case "focus":
                case "orbit":
                {
                    // focus <id | x z>; orbit <id | x z> <yawDeg>
                    bool orbit = sub == "orbit";
                    int words = args.Count - 1 - (orbit ? 1 : 0);
                    if (words is < 1 or > 2 || (orbit && !Num(args[^1], out deg))) { con.Error(orbit ? "usage: camera orbit <id | x z> <yawDeg> [--over <s>]" : "usage: camera focus <id | x z> [--over <s>]"); return; }
                    float? yaw = orbit ? deg * MathF.PI / 180 : null;
                    if (words == 2)
                    {
                        if (!Num(args[1], out float fx) || !Num(args[2], out float fz)) { con.Error("camera " + sub + ": x z must be numbers"); return; }
                        player.Follow = null;
                        if (over > 0) { movePivot = new Vector2(fx, fz); if (orbit) moveYawBy = yaw; break; }
                        CameraPlace(new Vector2(fx, fz), yaw, null, null);
                        break;
                    }
                    if (ControlEntity(args[1], con) is not { } found) return;
                    bool isCharacter = PlayerInterface.TryParseCharacter(args[1], out var cid) && !args[1].StartsWith("b:", StringComparison.OrdinalIgnoreCase);
                    if (over > 0)
                    {
                        player.Follow = null;
                        movePivot = new Vector2(found.X, found.Z); moveEntity = args[1]; moveFollow = isCharacter && !orbit;
                        if (orbit) moveYawBy = yaw;
                        break;
                    }
                    if (isCharacter) player.ControlFocusCharacter(cid); else player.Follow = null;
                    CameraPlace(new Vector2(found.X, found.Z), yaw, null, null);
                    break;
                }
                default:
                    con.Error("usage: camera | camera set ... | camera rotate <deg> | camera pitch <deg> | camera zoom <v> | camera focus <id | x z> | camera orbit <id> <yawDeg>");
                    return;
            }
            if (over > 0)
            {
                var mv = new CameraMove
                {
                    Start = Stopwatch.GetTimestamp(), Seconds = over, Pivot0 = pivot, Pivot1 = movePivot ?? pivot, Entity = moveEntity, Follow = moveFollow,
                    Yaw0 = strat.Yaw, Pitch0 = strat.Pitch, Dist0 = strat.Distance, Pitch1 = movePitch ?? strat.Pitch, Dist1 = moveZoom ?? strat.Distance,
                };
                mv.Yaw1 = moveYawBy is { } by ? mv.Yaw0 + by
                    : moveYaw is { } ya ? mv.Yaw0 + Meitou.Simulation.Session.Interp.WrapAngle(ya - mv.Yaw0)
                    : mv.Yaw0;
                if (controlDir is not null) { mv.Id = currentControlId; controlReplyDeferred = true; }
                cameraMove = mv;
                camStable = 0;
                con.Print($"camera move over {over} s");
                return;
            }
            camStable = 0;
            string after = ControlCameraJson();
            controlData = after;
            con.Print(after);
        });

        Add("select", "select <id...> | select --squad | select --none  [--add]", "select characters through the game's select command (the world applies it on its next tick)", (args, con) =>
        {
            if (!Loaded(con)) return;
            bool add = args.Contains("--add", StringComparer.OrdinalIgnoreCase);
            var words = args.Where(a => !a.Equals("--add", StringComparison.OrdinalIgnoreCase)).ToList();
            var ids = new List<CharacterId>();
            if (words.Count == 1 && words[0].Equals("--none", StringComparison.OrdinalIgnoreCase)) { }
            else if (words.Count == 1 && words[0].Equals("--squad", StringComparison.OrdinalIgnoreCase)) ids.AddRange(player.ControlSquad());
            else if (words.Count == 0) { con.Error("usage: select <id...> | --squad | --none [--add]"); return; }
            else
                foreach (string word in words)
                {
                    if (!word.StartsWith("c:", StringComparison.OrdinalIgnoreCase) || !PlayerInterface.TryParseCharacter(word, out var id)) { con.Error($"select: '{word}' is not a character id (c:<slot>.<gen>)"); return; }
                    ids.Add(id);
                }
            if (!player.ControlSelect(ids, add, out string message)) { con.Error("select: " + message); return; }
            con.Print(message + ": " + string.Join(' ', ids.Select(i => $"c:{i.Slot}.{i.Generation}")));
        });

        Add("order", "order move <x> <z> [--queue] [--wait <s> [--stuck <s>]] | order attack <id>", "give the selected characters a move or attack order through the game's own order commands (what a right click gives); move --wait <s> replies once, when the first ordered character arrives (within 4 m), is stuck (distance fell by less than 1 m for --stuck game seconds, default 10) or <s> game seconds pass: {result: arrived|stuck|timeout, pos, dist_to_target, ticks}", (args, con) =>
        {
            if (!Loaded(con)) return;
            string sub = args.Count > 0 ? args[0].ToLowerInvariant() : "";
            if (sub == "move")
            {
                bool queue = false;
                float waitS = 0, stuckS = GameHost.MoveWaitStuckSeconds;
                bool waited = false, stuckGiven = false;
                var nums = new List<string>();
                for (int i = 1; i < args.Count; i++)
                {
                    if (args[i].Equals("--queue", StringComparison.OrdinalIgnoreCase)) queue = true;
                    else if (args[i].Equals("--wait", StringComparison.OrdinalIgnoreCase))
                    {
                        if (i + 1 >= args.Count || !Num(args[++i], out waitS) || waitS < 0.1f || waitS > 3600) { con.Error("order: --wait needs game seconds between 0.1 and 3600"); return; }
                        waited = true;
                    }
                    else if (args[i].Equals("--stuck", StringComparison.OrdinalIgnoreCase))
                    {
                        if (i + 1 >= args.Count || !Num(args[++i], out stuckS) || stuckS < 0.5f || stuckS > 3600) { con.Error("order: --stuck needs game seconds between 0.5 and 3600"); return; }
                        stuckGiven = true;
                    }
                    else nums.Add(args[i]);
                }
                if (nums.Count != 2 || !Num(nums[0], out float x) || !Num(nums[1], out float z) || (stuckGiven && !waited)) { con.Error("usage: order move <x> <z> [--queue] [--wait <s> [--stuck <s>]]"); return; }
                if (!player.ControlMove(x, z, queue, out string message)) { con.Error("order: " + message); return; }
                if (waited) ControlMoveWaitStart(con, x, z, waitS, stuckS); else con.Print(message);
            }
            else if (sub == "attack")
            {
                if (args.Count != 2 || !args[1].StartsWith("c:", StringComparison.OrdinalIgnoreCase) || !PlayerInterface.TryParseCharacter(args[1], out var target)) { con.Error("usage: order attack <c:slot.gen>"); return; }
                if (!player.ControlAttack(target, out string message)) con.Error("order: " + message); else con.Print(message);
            }
            else con.Error("usage: order move <x> <z> [--queue] [--wait <s> [--stuck <s>]] | order attack <id>");
        });

        Add("place-building", "place-building <building record id or name> <x> <z> [yawDegrees] [heightAboveGround] [--interior <layout>] [--exterior <layout>]", "place a level-editor building at world X Z on the terrain (what the F10 Tools placement does without the pointer); --interior / --exterior name the building's furniture / sign layouts (layouts lists them), which gives it containers, item slots and fixtures; prints its instance key", (args, con) =>
        {
            string? interior = null, exterior = null;
            var plain = new List<string>();
            for (int a = 0; a < args.Count; a++)
            {
                if (args[a].Equals("--interior", StringComparison.OrdinalIgnoreCase) && a + 1 < args.Count) interior = args[++a];
                else if (args[a].Equals("--exterior", StringComparison.OrdinalIgnoreCase) && a + 1 < args.Count) exterior = args[++a];
                else plain.Add(args[a]);
            }
            args = plain;
            float yaw = 0, lift = 0;
            if (args.Count < 3 || args.Count > 5 || (args.Count == 5 && !Num(args[4], out lift)) || !Num(args[1], out float px) || !Num(args[2], out float pz) || (args.Count >= 4 && !Num(args[3], out yaw))) { con.Error("usage: place-building <building> <x> <z> [yawDegrees] [heightAboveGround] [--interior <layout>] [--exterior <layout>]"); return; }
            if (scene.Objects is null) { con.Error("no world loaded"); return; }
            var all = BuildData();
            if (!all.TryGetValue(args[0], out var data))
            {
                var named = all.Values.Where(b => string.Equals(b.Name, args[0], StringComparison.OrdinalIgnoreCase)).ToList();
                if (named.Count != 1) { con.Error(named.Count == 0 ? $"no building '{args[0]}'" : $"'{args[0]}' matches {named.Count} buildings; use the record id"); return; }
                data = named[0];
            }
            if (scene.Database?.Find(data.Id) is { } record && scene.Objects is { } objs)
            {
                if (interior is not null && objs.Layouts.Find(record, interior, exterior: false) is null) { con.Error($"no interior layout '{interior}' for {data.Name} (layouts {data.Id} lists them)"); return; }
                if (exterior is not null && objs.Layouts.Find(record, exterior, exterior: true) is null) { con.Error($"no exterior layout '{exterior}' for {data.Name} (layouts {data.Id} exterior lists them)"); return; }
            }
            float y = gpu.Terrain.HeightAt(px, pz) + lift;
            var ghost = new Building.QueuedGhost(data, new Vector3(px, y, pz), Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw * MathF.PI / 180f), 0, null);
            int before = levelEdits.Added.Count;
            (placeInterior, placeExterior) = (interior, exterior);
            try { LevelPlace([ghost]); }
            finally { (placeInterior, placeExterior) = (null, null); }
            var added = levelEdits.Added.Skip(before).LastOrDefault();
            con.Print(added is null ? "placed, no edit recorded" : $"placed {data.Name} ({data.Id}) key {added.Key} at {px:F1} {y:F1} {pz:F1}{(interior is null ? "" : " interior " + interior)}{(exterior is null ? "" : " exterior " + exterior)}");
        });

        Add("quicksave", "quicksave [name]", "queue a quicksave (the F5 key), taken between ticks; with a name, into Meitou's save folder of that name instead of the quicksave slot", (args, con) =>
        {
            if (args.Count > 1) { con.Error("usage: quicksave [name]"); return; }
            if (args.Count == 0) { quickSaveRequested = true; con.Print("quicksave queued"); return; }
            try { saveRequestedFolder = Meitou.Data.Save.SaveCatalog.Folder(Meitou.Simulation.Saving.QuickSave.DefaultRoot, args[0]); con.Print($"save queued: {saveRequestedFolder}"); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { con.Error("quicksave: " + e.Message); }
        });
        Add("quickload", "quickload [name]", "queue a quickload (the F9 key); with a name, the save folder of that name written by quicksave <name>", (args, con) =>
        {
            if (args.Count > 1) { con.Error("usage: quickload [name]"); return; }
            if (args.Count == 0) { quickLoadRequested = true; con.Print("quickload queued"); return; }
            try { loadRequestedFolder = Meitou.Data.Save.SaveCatalog.Folder(Meitou.Simulation.Saving.QuickSave.DefaultRoot, args[0]); con.Print($"load queued: {loadRequestedFolder}"); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { con.Error("quickload: " + e.Message); }
        });
        RegisterControlBaseCommands(Add, Loaded);
        RegisterControlPlayCommands(Add, Loaded);
        RegisterControlNavCommands(Add, Loaded);

        Add("terrain-height", "terrain-height <x> <z> [radius step]", "terrain height at X Z; with a radius and step, the min, max and spread over the square around it (to find flat ground)", (args, con) =>
        {
            float r = 0, step = 5;
            if (args.Count < 2 || args.Count > 4 || !Num(args[0], out float hx) || !Num(args[1], out float hz) || (args.Count > 2 && !Num(args[2], out r)) || (args.Count > 3 && !Num(args[3], out step)) || step <= 0) { con.Error("usage: terrain-height <x> <z> [radius step]"); return; }
            float lo = float.MaxValue, hi = float.MinValue;
            for (float dx = -r; dx <= r; dx += step)
                for (float dz = -r; dz <= r; dz += step)
                {
                    float h = gpu.Terrain.HeightAt(hx + dx, hz + dz);
                    lo = Math.Min(lo, h); hi = Math.Max(hi, h);
                }
            con.Print($"height {gpu.Terrain.HeightAt(hx, hz):F2} min {lo:F2} max {hi:F2} spread {hi - lo:F2}");
        });

        Add("doors", "doors [building key]", "list the door fixtures (id, position, motion, lock) of one building instance, or the count over every building when none is given", (args, con) =>
        {
            var reg = session.World.Fixtures;
            if (args.Count == 0) { con.Print($"{reg.All.Count(f => f.Kind == Meitou.Simulation.Locks.FixtureKind.Door)} doors in {reg.All.Select(f => f.Building).Distinct().Count()} buildings"); return; }
            var list = reg.OfBuilding(args[0]);
            con.Print($"{list.Count} fixtures, {list.Count(f => f.Kind == Meitou.Simulation.Locks.FixtureKind.Door)} doors");
            if (nav is { } ns && list.FirstOrDefault(f => f.Kind == Meitou.Simulation.Locks.FixtureKind.Door) is { } first)
            {
                Vector3 sum = Vector3.Zero; int near = 0, named = 0, own = 0; float minY = float.MaxValue, maxY = float.MinValue;
                var w = ns.Walkability.World;
                for (int slot = 0; slot < w.ZoneCount; slot++)
                {
                    var m = w.Mesh(slot);
                    for (int p = 0; p < m.PolygonCount; p++)
                    {
                        if (m.Areas[p] != Meitou.Simulation.Navigation.NavArea.Door) continue;
                        var c = Vector3.Zero;
                        foreach (int vi in m.Polygons[p]) c += m.Vertices[vi];
                        c /= m.Polygons[p].Length;
                        if (Vector2.Distance(new Vector2(c.X, c.Z), new Vector2(first.Position.X, first.Position.Z)) > 150 && !(m.DoorOf is { } of0 && of0[p] >= 0 && m.DoorIds![of0[p]] == args[0])) continue;
                        near++; minY = Math.Min(minY, c.Y); maxY = Math.Max(maxY, c.Y);
                        if (m.DoorOf is { } of && of[p] >= 0) { named++; if (m.DoorIds![of[p]] == args[0]) { own++; sum += c; } }
                    }
                }
                con.Print($"nav: {near} door-area polygons within 150 of the door or assigned to it (y {minY:F1}..{maxY:F1}), {named} assigned to a building, {own} to this one (mean {(own > 0 ? sum.X / own : 0):F1} {(own > 0 ? sum.Y / own : 0):F1} {(own > 0 ? sum.Z / own : 0):F1}); doors closed in nav: {ns.Doors.IsClosed(args[0])}");
            }
            foreach (var f in list.Where(f => f.Kind == Meitou.Simulation.Locks.FixtureKind.Door))
                con.Print($"door {f.Id} #{f.DoorIndex} pos {f.Position.X:F1} {f.Position.Y:F1} {f.Position.Z:F1} terrainY {gpu.Terrain.HeightAt(f.Position.X, f.Position.Z):F2} spot {(reg.DoorSpotOf(f) is { } sp ? $"centre {sp.Centre.X:F1} {sp.Centre.Y:F1} {sp.Centre.Z:F1} normal {sp.Normal.X:F2} {sp.Normal.Z:F2}" : "none")} motion {f.Motion} lock level {f.Lock?.Level} locked {f.Lock?.Locked} owner {f.Owner}");
        });

        Add("pause", "pause", "stop the game clock (speed 0)", (_, con) => { session.Simulation.SetSpeed(0); con.Print("paused"); });
        Add("resume", "resume", "run the game clock again at the last speed", (_, con) => { session.Simulation.SetSpeed(session.Simulation.LastNonZeroSpeed); con.Print($"speed {session.Simulation.Speed}"); });
        Add("hud", "hud hide|show", "hide or show the whole interface (what F7 does) for filming; the control channel keeps working, ui-tree may be empty while hidden", (args, con) =>
        {
            if (args.Count != 1 || args[0].ToLowerInvariant() is not ("hide" or "show")) { con.Error("usage: hud hide|show"); return; }
            interfaceHidden = args[0].Equals("hide", StringComparison.OrdinalIgnoreCase);
            con.Print(interfaceHidden ? "interface hidden" : "interface shown");
        });

        Add("speed", "speed <0|1|2|5>", "set the game speed (0 pauses; 2 and 5 are the default fast speeds, Advanced settings)", (args, con) =>
        {
            var speeds = session.Simulation.Speeds;
            if (args.Count != 1 || !double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || (v != 0 && !speeds.Contains(v)))
            {
                con.Error($"usage: speed <0|{string.Join('|', speeds.Select(s => s.ToString(CultureInfo.InvariantCulture)))}>");
                return;
            }
            session.Simulation.SetSpeed(v);
            con.Print($"speed {v}");
        });
    }

    /// <summary>The world-layer wait-for keys: camera-settled, selected=&lt;n&gt;, pos-near=&lt;x&gt;,&lt;z&gt;,&lt;r&gt;[,&lt;id&gt;] (the first selected character, or the given one, within r of x z).</summary>
    bool WorldWaitHolds(string key, string value)
    {
        switch (key)
        {
            case "camera-settled": return CameraSettled == !value.Equals("false", StringComparison.OrdinalIgnoreCase);
            case "selected": return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && !Covered && !player.TitleOpen && session.World.Player.Selection.Count() == n;
            case "pos-near":
            {
                string[] p = value.Split(',');
                if (p.Length < 3 || !Num(p[0], out float x) || !Num(p[1], out float z) || !Num(p[2], out float r)) return false;
                Vector3? at = p.Length > 3 ? player.ControlPosition(p[3]) : player.ControlLeadPosition();
                return at is { } a && Vector2.Distance(new Vector2(a.X, a.Z), new Vector2(x, z)) <= r;
            }
            default: return false;
        }
    }
}
