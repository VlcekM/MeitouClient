using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Meitou.Simulation;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Navigation;

namespace Meitou.Game.DevTools.Agents;

/// <summary>
/// The agent control channel's world layer (docs/procedures/game-launch-procedure.md "Driving a running game by command"): <c>world</c>, <c>world pick</c>, <c>entity-screen</c>, <c>camera ...</c>, <c>select</c>, <c>order move|attack</c>,
/// <c>pause</c>, <c>resume</c>, <c>speed</c>, and the wait-for keys <c>camera-settled</c>, <c>selected</c> and <c>pos-near</c>. The commands are registered by <see cref="AgentChannel.RegisterControlCommands"/>; the data comes from
/// <see cref="PlayerInterface"/> (AgentOrders), which reads the same snapshot and picking the pointer uses.
/// </summary>
sealed class AgentWorld(GameHost host)
{
    /// <summary>The camera at the last poll tick and for how many ticks in a row it did not move (0.25 s each): <c>camera-settled</c> waits for two.</summary>
    (Vector3 Eye, Vector3 Target, float Distance) _camStamp;
    int _camStable;

    bool CameraSettled => _camStable >= 2;

    internal void ControlCameraTick()
    {
        var c = host.Session.Camera.Current;
        var stamp = (c.Eye, c.Target, c.Distance);
        _camStable = stamp == _camStamp ? _camStable + 1 : 0;
        _camStamp = stamp;
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

    CameraMove? _cameraMove;

    /// <summary>Once per frame, before the camera is drawn: advances the running timed move (smoothstep from start to end) and, at its end, replies to the command that started it.</summary>
    internal void ControlCameraMoveTick()
    {
        if (_cameraMove is not { } m) return;
        double t = Math.Min(1, Stopwatch.GetElapsedTime(m.Start).TotalSeconds / m.Seconds);
        float e = (float)(t * t * (3 - 2 * t));
        var p1 = m.Pivot1;
        if (m.Entity is not null && host.Player.AgentOrders.ControlPosition(m.Entity) is { } at) p1 = m.Pivot1 = new Vector2(at.X, at.Z);
        host.Session.Camera.Place(Vector2.Lerp(m.Pivot0, p1, e), m.Yaw0 + (m.Yaw1 - m.Yaw0) * e, m.Pitch0 + (m.Pitch1 - m.Pitch0) * e, m.Dist0 + (m.Dist1 - m.Dist0) * e);
        if (t < 1) return;
        _cameraMove = null;
        if (m.Follow && m.Entity is not null && AgentOrders.TryParseCharacter(m.Entity, out var cid)) host.Player.AgentOrders.ControlFocusCharacter(cid);
        _camStable = 0;
        if (m.Id is { } id) { string json = ControlCameraJson(); host.AgentChannel.ControlWrite(id, true, "camera move done", json); }
    }

    internal static bool Num(string text, out float value) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

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
        var c = host.Session.Camera.Current;
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("mode", host.Session.Camera.IsFree ? "free" : "strategy");
            void Vec(string name, Vector3 v) { w.WriteStartArray(name); foreach (float f in new[] { v.X, v.Y, v.Z }) w.WriteNumberValue(Math.Round(f, 1)); w.WriteEndArray(); }
            Vec("pivot", c.Target);
            Vec("eye", c.Eye);
            w.WriteNumber("yaw", Math.Round(NormalDegrees(c.Yaw), 1));
            w.WriteNumber("pitch", Math.Round(c.Pitch * 180 / MathF.PI, 1));
            w.WriteNumber("zoom", Math.Round(c.Distance, 1));
            w.WriteNumber("fov", Math.Round(host.Camera.FieldOfView * 180 / MathF.PI, 1));
            w.WriteString("focus", host.Player.Follow is { } f ? $"c:{f.Slot}.{f.Generation}" : null);
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
        var c = host.Session.Camera.Current;
        host.Session.Camera.Place(pivot, yaw ?? c.Yaw, pitch ?? c.Pitch, zoom ?? c.Distance);
    }

    /// <summary>The world position of an entity id, or an error line for the console.</summary>
    Vector3? ControlEntity(string id, DevConsole con)
    {
        if (host.Player.AgentOrders.ControlPosition(id) is { } p) return p;
        con.Error($"no entity '{id}' (ids come from world: c:<slot>.<gen> or b:<key>)");
        return null;
    }

    internal void RegisterControlWorldCommands()
    {
        var c = host.DevConsoleHost.DevConsole;
        void Add(string name, string usage, string help, Action<IReadOnlyList<string>, DevConsole> run) => c.Register(new ConsoleCommand(name, usage, help, run));
        bool Loaded(DevConsole con)
        {
            if (!host.Covered && !host.Player.TitleOpen) return true;
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
                    if (host.Player.AgentOrders.ControlEntityScreenJson(entity) is not { } rect) { con.Error($"no entity '{entity}'"); return; }
                    using var doc = JsonDocument.Parse(rect);
                    if (!doc.RootElement.TryGetProperty("center", out var centre)) { con.Error($"world pick: {entity} is offscreen"); return; }
                    (px, py) = (centre[0].GetSingle(), centre[1].GetSingle());
                }
                else if (args.Count != 3 || !Num(args[1], out px) || !Num(args[2], out py)) { con.Error("usage: world pick <screen x> <screen y> | world pick --entity <id>"); return; }
                string pick = host.Player.AgentOrders.ControlPickJson(new Vector2(px, py));
                if (entity is not null)
                {
                    using var doc = JsonDocument.Parse(pick);
                    bool same = doc.RootElement.TryGetProperty("id", out var got) && string.Equals(got.GetString(), entity, StringComparison.OrdinalIgnoreCase);
                    pick = pick[..^1] + ",\"entity\":" + JsonSerializer.Serialize(entity) + ",\"matches\":" + (same ? "true" : "false") + "}";
                }
                host.AgentChannel.ControlData = pick;
                con.Print(pick);
                return;
            }
            var flags = ControlFlags(args);
            foreach (string key in flags.Keys)
                if (key is not ("" or "--near" or "--radius" or "--kind" or "--limit")) { con.Error($"world: unknown flag {key}"); return; }
            if (flags[""].Count > 0) { con.Error($"world: unexpected '{flags[""][0]}'"); return; }
            Vector3 near = host.Session.Camera.Current.Target;
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
            string json = host.Player.AgentOrders.ControlWorldJson(near, radius, kind, limit);
            host.AgentChannel.PrintData(con, json);
        });

        Add("entity-screen", "entity-screen <id>", "the entity's on-screen rect [x,y,w,h] and centre in screen pixels, or state offscreen; characters also report occluded (something else is under the centre)", (args, con) =>
        {
            if (!Loaded(con)) return;
            if (args.Count != 1) { con.Error("usage: entity-screen <id>"); return; }
            if (host.Player.AgentOrders.ControlEntityScreenJson(args[0]) is not { } json) { con.Error($"no entity '{args[0]}'"); return; }
            host.AgentChannel.PrintData(con, json);
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
            if (sub.Length == 0) { string json = ControlCameraJson(); host.AgentChannel.PrintData(con, json); return; }
            if (_cameraMove is { } running)
            {
                _cameraMove = null;
                if (running.Id is { } oldId) host.AgentChannel.ControlWrite(oldId, true, "camera move replaced by a newer one", ControlCameraJson());
            }
            var strat = host.Session.Camera.Strategy;
            var view = host.Session.Camera.Current;
            var pivot = strat.PivotXz;
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
                        host.Player.Follow = null;
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
                            host.Player.Follow = null;
                            if (over > 0) { movePivot = new Vector2(fx, fz); if (orbit) moveYawBy = yaw; break; }
                            CameraPlace(new Vector2(fx, fz), yaw, null, null);
                            break;
                        }
                        if (ControlEntity(args[1], con) is not { } found) return;
                        bool isCharacter = AgentOrders.TryParseCharacter(args[1], out var cid) && !args[1].StartsWith("b:", StringComparison.OrdinalIgnoreCase);
                        if (over > 0)
                        {
                            host.Player.Follow = null;
                            movePivot = new Vector2(found.X, found.Z); moveEntity = args[1]; moveFollow = isCharacter && !orbit;
                            if (orbit) moveYawBy = yaw;
                            break;
                        }
                        if (isCharacter) host.Player.AgentOrders.ControlFocusCharacter(cid); else host.Player.Follow = null;
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
                    Start = Stopwatch.GetTimestamp(),
                    Seconds = over,
                    Pivot0 = pivot,
                    Pivot1 = movePivot ?? pivot,
                    Entity = moveEntity,
                    Follow = moveFollow,
                    Yaw0 = strat.Yaw,
                    Pitch0 = strat.Pitch,
                    Dist0 = strat.Distance,
                    Pitch1 = movePitch ?? strat.Pitch,
                    Dist1 = moveZoom ?? strat.Distance,
                };
                mv.Yaw1 = moveYawBy is { } by ? mv.Yaw0 + by
                    : moveYaw is { } ya ? mv.Yaw0 + Simulation.Session.Interp.WrapAngle(ya - mv.Yaw0)
                    : mv.Yaw0;
                if (host.AgentChannel.ControlDir is not null) { mv.Id = host.AgentChannel.CurrentControlId; host.AgentChannel.ControlReplyDeferred = true; }
                _cameraMove = mv;
                _camStable = 0;
                con.Print($"camera move over {over} s");
                return;
            }
            _camStable = 0;
            string after = ControlCameraJson();
            host.AgentChannel.ControlData = after;
            con.Print(after);
        });

        Add("select", "select <id...> | select --squad | select --none  [--add]", "select characters through the game's select command (the world applies it on its next tick)", (args, con) =>
        {
            if (!Loaded(con)) return;
            bool add = args.Contains("--add", StringComparer.OrdinalIgnoreCase);
            var words = args.Where(a => !a.Equals("--add", StringComparison.OrdinalIgnoreCase)).ToList();
            var ids = new List<CharacterId>();
            if (words.Count == 1 && words[0].Equals("--none", StringComparison.OrdinalIgnoreCase)) { }
            else if (words.Count == 1 && words[0].Equals("--squad", StringComparison.OrdinalIgnoreCase)) ids.AddRange(host.Player.AgentOrders.ControlSquad());
            else if (words.Count == 0) { con.Error("usage: select <id...> | --squad | --none [--add]"); return; }
            else
                foreach (string word in words)
                {
                    if (!word.StartsWith("c:", StringComparison.OrdinalIgnoreCase) || !AgentOrders.TryParseCharacter(word, out var id)) { con.Error($"select: '{word}' is not a character id (c:<slot>.<gen>)"); return; }
                    ids.Add(id);
                }
            if (!host.Player.AgentOrders.ControlSelect(ids, add, out string message)) { con.Error("select: " + message); return; }
            con.Print(message + ": " + string.Join(' ', ids.Select(i => $"c:{i.Slot}.{i.Generation}")));
        });

        Add("order", "order move <x> <z> [--queue] [--wait <s> [--stuck <s>]] | order attack <id>", "give the selected characters a move or attack order through the game's own order commands (what a right click gives); move --wait <s> replies once, when the first ordered character arrives (within 4 m), is stuck (distance fell by less than 1 m for --stuck game seconds, default 10) or <s> game seconds pass: {result: arrived|stuck|timeout, pos, dist_to_target, ticks}", (args, con) =>
        {
            if (!Loaded(con)) return;
            string sub = args.Count > 0 ? args[0].ToLowerInvariant() : "";
            if (sub == "move")
            {
                bool queue = false;
                float waitS = 0, stuckS = AgentPlay.MoveWaitStuckSeconds;
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
                if (!host.Player.AgentOrders.ControlMove(x, z, queue, out string message)) { con.Error("order: " + message); return; }
                if (waited) host.AgentPlay.ControlMoveWaitStart(con, x, z, waitS, stuckS); else con.Print(message);
            }
            else if (sub == "attack")
            {
                if (args.Count != 2 || !args[1].StartsWith("c:", StringComparison.OrdinalIgnoreCase) || !AgentOrders.TryParseCharacter(args[1], out var target)) { con.Error("usage: order attack <c:slot.gen>"); return; }
                if (!host.Player.AgentOrders.ControlAttack(target, out string message)) con.Error("order: " + message); else con.Print(message);
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
            if (host.Scene.Objects is null) { con.Error("no world loaded"); return; }
            var all = host.BuildingSync.BuildData();
            if (!all.TryGetValue(args[0], out var data))
            {
                var named = all.Values.Where(b => string.Equals(b.Name, args[0], StringComparison.OrdinalIgnoreCase)).ToList();
                if (named.Count != 1) { con.Error(named.Count == 0 ? $"no building '{args[0]}'" : $"'{args[0]}' matches {named.Count} buildings; use the record id"); return; }
                data = named[0];
            }
            if (host.Scene.Database?.Find(data.Id) is { } record && host.Scene.Objects is { } objs)
            {
                if (interior is not null && objs.Layouts.Find(record, interior, exterior: false) is null) { con.Error($"no interior layout '{interior}' for {data.Name} (layouts {data.Id} lists them)"); return; }
                if (exterior is not null && objs.Layouts.Find(record, exterior, exterior: true) is null) { con.Error($"no exterior layout '{exterior}' for {data.Name} (layouts {data.Id} exterior lists them)"); return; }
            }
            float y = host.Gpu.Terrain.HeightAt(px, pz) + lift;
            var ghost = new Building.QueuedGhost(data, new Vector3(px, y, pz), Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw * MathF.PI / 180f), 0, null);
            int before = host.LevelEditorHost.LevelEdits.Added.Count;
            (host.LevelEditorHost.PlaceInterior, host.LevelEditorHost.PlaceExterior) = (interior, exterior);
            try { host.LevelEditorHost.LevelPlace([ghost]); }
            finally { (host.LevelEditorHost.PlaceInterior, host.LevelEditorHost.PlaceExterior) = (null, null); }
            var added = host.LevelEditorHost.LevelEdits.Added.Skip(before).LastOrDefault();
            con.Print(added is null ? "placed, no edit recorded" : $"placed {data.Name} ({data.Id}) key {added.Key} at {px:F1} {y:F1} {pz:F1}{(interior is null ? "" : " interior " + interior)}{(exterior is null ? "" : " exterior " + exterior)}");
        });

        Add("quicksave", "quicksave [name]", "queue a quicksave (the F5 key), taken between ticks; with a name, into Meitou's save folder of that name instead of the quicksave slot", (args, con) =>
        {
            if (args.Count > 1) { con.Error("usage: quicksave [name]"); return; }
            if (args.Count == 0) { host.Saves.QuickSaveRequested = true; con.Print("quicksave queued"); return; }
            try { host.Saves.SaveRequestedFolder = Data.Save.SaveCatalog.Folder(Simulation.Saving.QuickSave.DefaultRoot, args[0]); con.Print($"save queued: {host.Saves.SaveRequestedFolder}"); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { con.Error("quicksave: " + e.Message); }
        });
        Add("quickload", "quickload [name]", "queue a quickload (the F9 key); with a name, the save folder of that name written by quicksave <name>", (args, con) =>
        {
            if (args.Count > 1) { con.Error("usage: quickload [name]"); return; }
            if (args.Count == 0) { host.Saves.QuickLoadRequested = true; con.Print("quickload queued"); return; }
            try { host.Saves.LoadRequestedFolder = Data.Save.SaveCatalog.Folder(Simulation.Saving.QuickSave.DefaultRoot, args[0]); con.Print($"load queued: {host.Saves.LoadRequestedFolder}"); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { con.Error("quickload: " + e.Message); }
        });
        RegisterControlBaseCommands(Add, Loaded);
        host.AgentPlay.RegisterControlPlayCommands(Add, Loaded);
        RegisterControlNavCommands(Add, Loaded);

        Add("terrain-height", "terrain-height <x> <z> [radius step]", "terrain height at X Z; with a radius and step, the min, max and spread over the square around it (to find flat ground)", (args, con) =>
        {
            float r = 0, step = 5;
            if (args.Count < 2 || args.Count > 4 || !Num(args[0], out float hx) || !Num(args[1], out float hz) || (args.Count > 2 && !Num(args[2], out r)) || (args.Count > 3 && !Num(args[3], out step)) || step <= 0) { con.Error("usage: terrain-height <x> <z> [radius step]"); return; }
            float lo = float.MaxValue, hi = float.MinValue;
            for (float dx = -r; dx <= r; dx += step)
                for (float dz = -r; dz <= r; dz += step)
                {
                    float h = host.Gpu.Terrain.HeightAt(hx + dx, hz + dz);
                    lo = Math.Min(lo, h); hi = Math.Max(hi, h);
                }
            con.Print($"height {host.Gpu.Terrain.HeightAt(hx, hz):F2} min {lo:F2} max {hi:F2} spread {hi - lo:F2}");
        });

        Add("doors", "doors [building key]", "list the door fixtures (id, position, motion, lock) of one building instance, or the count over every building when none is given", (args, con) =>
        {
            var reg = host.Session.World.Fixtures;
            if (args.Count == 0) { con.Print($"{reg.All.Count(f => f.Kind == FixtureKind.Door)} doors in {reg.All.Select(f => f.Building).Distinct().Count()} buildings"); return; }
            var list = reg.OfBuilding(args[0]);
            con.Print($"{list.Count} fixtures, {list.Count(f => f.Kind == FixtureKind.Door)} doors");
            if (host.Nav is { } ns && list.FirstOrDefault(f => f.Kind == FixtureKind.Door) is { } first)
            {
                Vector3 sum = Vector3.Zero; int near = 0, named = 0, own = 0; float minY = float.MaxValue, maxY = float.MinValue;
                var w = ns.Walkability.World;
                for (int slot = 0; slot < w.ZoneCount; slot++)
                {
                    var m = w.Mesh(slot);
                    for (int p = 0; p < m.PolygonCount; p++)
                    {
                        if (m.Areas[p] != NavArea.Door) continue;
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
            foreach (var f in list.Where(f => f.Kind == FixtureKind.Door))
                con.Print($"door {f.Id} #{f.DoorIndex} pos {f.Position.X:F1} {f.Position.Y:F1} {f.Position.Z:F1} terrainY {host.Gpu.Terrain.HeightAt(f.Position.X, f.Position.Z):F2} spot {(reg.DoorSpotOf(f) is { } sp ? $"centre {sp.Centre.X:F1} {sp.Centre.Y:F1} {sp.Centre.Z:F1} normal {sp.Normal.X:F2} {sp.Normal.Z:F2}" : "none")} motion {f.Motion} lock level {f.Lock?.Level} locked {f.Lock?.Locked} owner {f.Owner}");
        });

        Add("pause", "pause", "stop the game clock (speed 0)", (_, con) => { host.Session.Simulation.SetSpeed(0); con.Print("paused"); });
        Add("resume", "resume", "run the game clock again at the last speed", (_, con) => { host.Session.Simulation.SetSpeed(host.Session.Simulation.LastNonZeroSpeed); con.Print($"speed {host.Session.Simulation.Speed}"); });
        Add("hud", "hud hide|show", "hide or show the whole interface (what F7 does) for filming; the control channel keeps working, ui-tree may be empty while hidden", (args, con) =>
        {
            if (args.Count != 1 || args[0].ToLowerInvariant() is not ("hide" or "show")) { con.Error("usage: hud hide|show"); return; }
            host.AgentChannel.InterfaceHidden = args[0].Equals("hide", StringComparison.OrdinalIgnoreCase);
            con.Print(host.AgentChannel.InterfaceHidden ? "interface hidden" : "interface shown");
        });

        Add("speed", "speed <0|1|2|5>", "set the game speed (0 pauses; 2 and 5 are the default fast speeds, Advanced settings)", (args, con) =>
        {
            var speeds = host.Session.Simulation.Speeds;
            if (args.Count != 1 || !double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || (v != 0 && !speeds.Contains(v)))
            {
                con.Error($"usage: speed <0|{string.Join('|', speeds.Select(s => s.ToString(CultureInfo.InvariantCulture)))}>");
                return;
            }
            host.Session.Simulation.SetSpeed(v);
            con.Print($"speed {v}");
        });
    }

    /// <summary>The world-layer wait-for keys: camera-settled, selected=&lt;n&gt;, pos-near=&lt;x&gt;,&lt;z&gt;,&lt;r&gt;[,&lt;id&gt;] (the first selected character, or the given one, within r of x z).</summary>
    internal bool WorldWaitHolds(string key, string value)
    {
        switch (key)
        {
            case "camera-settled": return CameraSettled == !value.Equals("false", StringComparison.OrdinalIgnoreCase);
            case "selected": return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && !host.Covered && !host.Player.TitleOpen && host.Session.World.Player.Selection.Count() == n;
            case "pos-near":
                {
                    string[] p = value.Split(',');
                    if (p.Length < 3 || !Num(p[0], out float x) || !Num(p[1], out float z) || !Num(p[2], out float r)) return false;
                    Vector3? at = p.Length > 3 ? host.Player.AgentOrders.ControlPosition(p[3]) : host.Player.AgentOrders.ControlLeadPosition();
                    return at is { } a && Vector2.Distance(new Vector2(a.X, a.Z), new Vector2(x, z)) <= r;
                }
            default: return false;
        }
    }

    // <summary>Navigation query commands of the agent control channel (docs/procedures/playtest/agent-play-api.md): <c>path-check</c>.</summary>
    void RegisterControlNavCommands(Action<string, string, string, Action<IReadOnlyList<string>, DevConsole>> add, Func<DevConsole, bool> loaded)
    {
        RegisterControlDoorCommand(add, loaded);
        add("path-check", "path-check <x1> <z1> <x2> <z2> | path-check --id <c:slot.gen> <x> <z>", "ask the game's own navigation query (what a walking character uses, doors as they are now) whether a path exists, without walking: reachable, path length and, when none exists, the doors that block it (the nearest-first doors on the way that are closed to paths, with lock, open, broken state); blockedBy is only searched inside the query's own zone mesh", (args, con) =>
        {
            if (!loaded(con)) return;
            if (host.Nav is null) { con.Error("path-check: no navmesh in this run"); return; }
            string? idText = null;
            var nums = new List<string>();
            for (int i = 0; i < args.Count; i++)
            {
                if (args[i].Equals("--id", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count) idText = args[++i];
                else nums.Add(args[i]);
            }
            var walk = host.Nav.Walkability;
            Vector3 from, to;
            float a = 0, b = 0, c = 0, d = 0;
            if (idText is not null)
            {
                if (!AgentOrders.TryParseCharacter(idText, out _) || host.Player.AgentOrders.ControlPosition(idText.StartsWith("c:", StringComparison.OrdinalIgnoreCase) ? idText : "c:" + idText) is not { } at) { con.Error($"path-check: unknown character '{idText}'"); return; }
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
            bool bothLoaded = walk.World.Contains(Data.World.WorldLayout.ZoneOf(from.X, from.Z)) && walk.World.Contains(Data.World.WorldLayout.ZoneOf(to.X, to.Z));
            // The query also answers "as near as can be got" when the goal lies within its snap distance of reachable ground (just outside a shut door): such a path ends short of the target.
            const float endTolerance = 1.5f;
            var pathEnd = path.Found ? path.Points[Math.Min(path.Exact, path.Points.Count) - 1] : default;
            bool cutOff = path.Found && bothLoaded && (path.IsProvisional || Vector2.Distance(new(pathEnd.X, pathEnd.Z), new(to.X, to.Z)) > endTolerance);
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
            host.AgentChannel.ControlData = Encoding.UTF8.GetString(stream.ToArray());
            con.Print(reachable ? $"reachable, {path.Points.Count} points" : "no path");
        });
    }

    /// <summary>The <c>door</c> command: sets a door fixture's state in a test save (the fields <c>path-check</c> reports) and tells navigation, so the nav door table sees it at once.</summary>
    void RegisterControlDoorCommand(Action<string, string, string, Action<IReadOnlyList<string>, DevConsole>> add, Func<DevConsole, bool> loaded)
    {
        add("door", "door <fixture id> | door nearest <x> <z> [--lock] [--unlock] [--close] [--open] [--break] [--repair] [--all]", "set a door's state for a test (flags apply in the order given): --lock locks it (level at least 1, an open leaf starts closing), --unlock, --close and --open set the leaf at once, --break breaks door and lock, --repair undoes it; --all applies to every door of the building (navigation treats a building as shut only when all its doors are locked); no flag only reports; reply: JSON with the door's lock, broken, motion, openAmount and whether navigation now has the building closed", (args, con) =>
        {
            if (!loaded(con)) return;
            var reg = host.Session.World.Fixtures;
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
                if (host.Nav is { } ns) w.WriteBoolean("navClosed", ns.Doors.IsClosed(door.Building));
                w.WriteEndObject();
            }
            host.AgentChannel.ControlData = Encoding.UTF8.GetString(stream.ToArray());
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
            result.Add(($"{poly.Zone}.{poly.Polygon}", building, host.Session.World.Fixtures.OfBuilding(building).Where(f => f.Kind == FixtureKind.Door).ToList()));
        }
        why = result.Count > 0 ? "a door on the way is closed for paths" : "the path exists with every door open but no door on it is closed for paths (cause not a door)";
        return result;
    }

    // Agent-playtest control channel, base building (docs/procedures/game-launch-procedure.md "Driving a running game by command"): list the placeable buildings, show what a building holds,
    // put items into containers or on the ground, and measure the placed buildings' footprints against each other.
    void RegisterControlBaseCommands(Action<string, string, string, Action<IReadOnlyList<string>, DevConsole>> add, Func<DevConsole, bool> loaded)
    {
        add("buildings", "buildings [text]", "list the placeable BUILDING records (id, name, category, path mode, furniture flags) whose id, name or category contains text", (args, con) =>
        {
            string text = args.Count > 0 ? string.Join(' ', args) : "";
            var list = host.BuildingSync.BuildData().Values.Where(b => text.Length == 0 || b.Id.Contains(text, StringComparison.OrdinalIgnoreCase) || b.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                || b.Category.Contains(text, StringComparison.OrdinalIgnoreCase)).OrderBy(b => b.Category, StringComparer.Ordinal).ThenBy(b => b.Name, StringComparer.Ordinal).ToList();
            con.Print($"{list.Count} buildings");
            foreach (var b in list.Take(400))
                con.Print($"{b.Id} | {b.Name} | {b.Category} | path {b.PathMode}{(b.IsInteriorFurniture ? " | interior furniture" : "")}{(b.IsExteriorFurniture ? " | exterior furniture" : "")}{(b.LinksTogether ? " | wall" : "")}");
        });

        add("layouts", "layouts <building record id or name> [exterior]", "the interior (or exterior) layouts a building record has: name, parts, bar / shop flags, the kinds of parts (what place-building --interior takes)", (args, con) =>
        {
            if (args.Count is < 1 or > 2 || host.Scene.Database is not { } db) { con.Error("usage: layouts <building record id or name> [exterior]"); return; }
            var record = db.Find(args[0]);
            if (record is null && host.BuildingSync.BuildData().Values.FirstOrDefault(b => string.Equals(b.Name, args[0], StringComparison.OrdinalIgnoreCase)) is { } bd) record = db.Find(bd.Id);
            if (record is null) { con.Error($"no building '{args[0]}'"); return; }
            bool exterior = args.Count == 2;
            var list = host.InteriorEditorHost.InteriorEditorLayouts(record, exterior);
            con.Print($"{list.Count} {(exterior ? "exterior" : "interior")} layouts for {record.Name} ({record.StringId})");
            foreach (var l in list)
            {
                var parts = host.Scene.Objects is { } o ? o.Layouts.Items(l).GroupBy(i => i.Building.Name).OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Count()}x {g.Key}") : [];
                con.Print($"{l.Name} | {l.Instances.Values.Count(x => !x.IsCleared)} parts{(l.GetBool("is bar") ? " | bar" : "")}{(l.GetBool("is shop") ? " | shop" : "")} | {string.Join(", ", parts)}");
            }
        });

        add("clear-building", "clear-building <building key>", "clear a building by its instance key (a level building or one placed in this session), as the level editor's clear does", (args, con) =>
        {
            if (!loaded(con)) return;
            if (args.Count != 1) { con.Error("usage: clear-building <building key>"); return; }
            con.Print(host.LevelEditorHost.LevelClear(args[0]) ? $"cleared {args[0]}" : $"no building {args[0]}");
        });

        add("building-info", "building-info <building key>", "what the building with that run-time key holds: fixtures by kind, containers and their items, item slots (total and filled), loose items lying in its slots", (args, con) =>
        {
            if (!loaded(con)) return;
            if (args.Count != 1) { con.Error("usage: building-info <building key>"); return; }
            var world = host.Session.World;
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
            var near = host.Session.World.GroundItems.All.Where(g => Vector2.Distance(new Vector2(g.Position.X, g.Position.Z), new Vector2(x, z)) <= r).ToList();
            con.Print($"{near.Count} items within {r:F0} of {x:F0} {z:F0} (all {host.Session.World.GroundItems.Count} in the world)");
            foreach (var g in near.Take(30))
                con.Print($"item {g.Id} {g.Item.Record} {g.Item.Name} x{g.Item.Quantity} at {g.Position.X:F1} {g.Position.Y:F1} {g.Position.Z:F1}{(g.Slot is null ? "" : " slot " + g.Slot)}");
        });

        add("place-item", "place-item \"<item>\" <x> <z> [yawDegrees] [y] [qty]", "lay a loose item on the ground at X Z (height: the terrain unless y is given as an absolute world height, e.g. a floor)", (args, con) =>
        {
            if (!loaded(con)) return;
            float yaw = 0, y = float.NaN, q = 1;
            if (args.Count < 3 || args.Count > 6 || !Num(args[1], out float x) || !Num(args[2], out float z) || (args.Count > 3 && !Num(args[3], out yaw)) || (args.Count > 4 && !Num(args[4], out y)) || (args.Count > 5 && !Num(args[5], out q)))
            { con.Error("usage: place-item \"<item>\" <x> <z> [yawDegrees] [y] [qty]"); return; }
            if (host.DebugMenu.DebugRecord(args[0], character: false) is not { } item) { con.Error($"no item record matches '{args[0]}'"); return; }
            if (float.IsNaN(y)) y = host.Gpu.Terrain.HeightAt(x, z);
            host.Session.World.Commands.Enqueue(new DebugPlaceItem(item.Id, Math.Clamp((int)q, 1, 1000), new Vector3(x, y, z), yaw * MathF.PI / 180f) { Tick = host.Session.World.Tick });
            con.Print($"placed {item.Name} ({item.Id}) at {x:F1} {y:F1} {z:F1}");
        });

        add("container-put", "container-put <container fixture id> \"<item>\" [qty]", "put items into a container of a building (ids from building-info)", (args, con) =>
        {
            if (!loaded(con)) return;
            int qty = 1;
            if (args.Count < 2 || args.Count > 3 || (args.Count == 3 && !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out qty))) { con.Error("usage: container-put <container fixture id> \"<item>\" [qty]"); return; }
            if (host.Session.World.Fixtures.Find(args[0]) is not { Contents: not null }) { con.Error($"no container '{args[0]}' (building-info lists them)"); return; }
            if (host.DebugMenu.DebugRecord(args[1], character: false) is not { } item) { con.Error($"no item record matches '{args[1]}'"); return; }
            host.Session.World.Commands.Enqueue(new DebugPutItem(args[0], item.Id, Math.Clamp(qty, 1, 1000)) { Tick = host.Session.World.Tick });
            con.Print($"queued {qty} x {item.Name} into {args[0]}");
        });

        add("base-check", "base-check", "footprints of the buildings placed in this session: per building key, record, position, yaw, bounds; and the pair gaps in units (negative: the collision hulls overlap)", (args, con) =>
        {
            if (!loaded(con)) return;
            host.LevelEditorHost.LevelEditsFollowWorld();
            if (host.Scene.Database is not { } db) { con.Error("no database"); return; }
            var footprints = host.Saves.SiteFootprints ??= new BuildingFootprints(host.Install, db, null);
            var items = new List<(string Key, string Id, Vector2[] Hull)>();
            foreach (var p in host.LevelEditorHost.LevelEdits.Added)
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