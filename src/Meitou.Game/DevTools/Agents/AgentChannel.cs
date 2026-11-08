using System.Diagnostics;
using System.Text.Json;
using Meitou.Game.DevTools;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Ui;

namespace Meitou.Game.DevTools.Agents;

/// <summary>
/// The agent control channel (debug only, docs/procedures/todo-faster-ingame-agent-tests.md): with <c>--control-dir &lt;dir&gt;</c> (or MEITOU_CONTROL_DIR) the game polls <c>&lt;dir&gt;/cmd</c> on the game thread
/// about four times a second; each line <c>&lt;id&gt; &lt;console command&gt;</c> is run by <see cref="DevConsole.Execute"/> (the console window need not be open), and one result line per command is appended
/// to <c>&lt;dir&gt;/out</c> as one JSON object: <c>{"id":n,"ok":true,"data":..}</c> or <c>{"id":n,"ok":false,"error":"..","data":..}</c>; <c>data</c> is the command's structured result (state, ui-tree, ui-click candidates) or <c>{"text":".."}</c> with the printed lines joined by " | ". <c>--exec</c> (or <c>--setup</c>) runs commands once, in order, after the world has loaded and settled; the first failing one stops the list and the run exits with code 3. Without the flag nothing here runs.
/// </summary>
sealed class AgentChannel(GameHost host)
{
    /// <summary>The whole interface hidden (F7, or the <c>hud hide</c> console command).</summary>
    internal bool interfaceHidden;
    internal string? controlDir, execPending;
    /// <summary>When the world was first seen loaded (for the setup's settle wait), and the exit code the run ends with (0, or 3 when a setup command failed).</summary>
    long setupLoadedAt, shotDueAt;
    /// <summary>--screenshot-after: the setup is done and the timer runs (shotDueAt), or the picture was asked for and its save ends the run.</summary>
    bool autoShotTimer, autoShotAsked;
    internal int controlExitCode;
    long nextControlPoll;
    /// <summary>The settings window of the interactive loop (null in the one-shot modes), for the console commands.</summary>
    internal SettingsMenu? settingsPanel;
    /// <summary>The id whose result waits for the screenshot's frame to be saved, and the file it goes to.</summary>
    internal string? shotWaitId, shotPath, currentControlId;
    /// <summary>Structured result of the running control command (raw JSON), for the file channel; the console keeps its printed text.</summary>
    internal string? controlData;
    /// <summary>Event stream: on by default, last snapshot, event counter, start time; pending wait-for conditions; replies held a few frames to report "changed".</summary>
    internal bool controlEvents = true, controlReplyDeferred;
    ControlSnap? lastSnap;
    long controlSeq, controlStart = Stopwatch.GetTimestamp();
    readonly List<ControlWait> controlWaits = [];
    readonly List<ControlSettle> controlSettles = [];

    /// <summary>The small state diffed every poll tick for events and changed.</summary>
    sealed record ControlSnap(string Screens, string Screen, string Page, bool Loaded, string Mode, int Selected);
    sealed record ControlWait(string Id, string Key, string Value, long Deadline, long Started, double TimeoutSeconds);
    sealed class ControlSettle(string id, string text, string? data, ControlSnap before) { public string Id = id, Text = text; public string? Data = data; public ControlSnap Before = before; public int Frames = 4; }

    internal void InitControl()
    {
        controlDir = host.g.ControlDir ?? Environment.GetEnvironmentVariable("MEITOU_CONTROL_DIR");
        if (string.IsNullOrEmpty(controlDir)) controlDir = null;
        else Directory.CreateDirectory(controlDir);
        execPending = host.g.Exec;
    }

    /// <summary>Once per frame: the one-shot <c>--exec</c> after the load, then the command file at most every 0.25 s.</summary>
    internal void PollControl()
    {
        if (!host.devConsoleHost.consoleRegistered) host.devConsoleHost.RegisterConsoleCommands();
        if (execPending is { } exec && SetupSettled())
        {
            execPending = null;
            foreach (string part in exec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) execQueue.Enqueue(part);
        }
        PumpExec();
        if (host.g.ShotAfter is { } shotDelay && !autoShotTimer && !autoShotAsked && execPending is null && execQueue.Count == 0 && execWaitingFor is null && controlExitCode == 0 && SetupSettled())
        {
            autoShotTimer = true;
            shotDueAt = Stopwatch.GetTimestamp() + (long)(shotDelay * Stopwatch.Frequency);
        }
        if (controlExitCode != 0 && host.g.ShotAfter is not null) host.devConsoleHost.controlQuit = true;   // a failed setup ends a screenshot run at once
        if (autoShotTimer && Stopwatch.GetTimestamp() >= shotDueAt)
        {
            autoShotTimer = false;
            autoShotAsked = true;
            string path = Path.GetFullPath(host.g.ShotOut ?? Path.Combine(controlDir ?? Directory.GetCurrentDirectory(), controlDir is null ? "meitou-shot.png" : "shot.png"));
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.WriteLine($"setup     screenshot failed: {e.Message}"); controlExitCode = 4; host.devConsoleHost.controlQuit = true; }
            if (!host.devConsoleHost.controlQuit)
            {
                shotPath = path;
                shotWaitId = null;
                host.screenshotRequested = true;
            }
        }
        if (controlDir is null) return;
        for (int i = controlSettles.Count - 1; i >= 0; i--)
        {
            var st = controlSettles[i];
            if (--st.Frames > 0) continue;
            controlSettles.RemoveAt(i);
            ControlWrite(st.Id, true, st.Text, st.Data, ControlSnapshot() is var after && (after.Screens != st.Before.Screens || after.Page != st.Before.Page));
        }
        if (Stopwatch.GetTimestamp() < nextControlPoll) return;
        nextControlPoll = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 4;
        ControlTick();
        string file = Path.Combine(controlDir, "cmd");
        string[] lines;
        try
        {
            if (!File.Exists(file)) return;
            lines = File.ReadAllLines(file);
            File.Delete(file);
        }
        catch (IOException e) { Console.WriteLine($"control   cannot read {file}: {e.Message}"); return; }
        foreach (string line in lines.Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            int space = line.IndexOf(' ');
            RunControlLine(space < 0 ? line : line[..space], space < 0 ? "" : line[(space + 1)..].Trim());
        }
    }

    readonly Queue<string> execQueue = new();
    /// <summary>The setup command whose deferred reply (wait-for, order move --wait, spawn-at) is still pending; the setup list goes on when it arrives.</summary>
    string? execWaitingFor;

    /// <summary>Runs the setup list one command at a time; a deferred command is waited on, and its failure ends the list (exit 3).</summary>
    void PumpExec()
    {
        while (execWaitingFor is null && execQueue.Count > 0)
        {
            string part = execQueue.Dequeue();
            bool ok = RunControlLine("exec", part);
            if (ok && controlReplyDeferred)
            {
                if (controlDir is null) { Console.WriteLine($"setup     '{part}' answers later and needs --control-dir to be waited on"); ok = false; }
                else { execWaitingFor = part; return; }
            }
            if (!ok) { ExecFailed(part); return; }
        }
    }

    void ExecFailed(string part)
    {
        Console.WriteLine($"setup     setup failed: '{part}' (the rest was skipped)");
        controlExitCode = 3;
        execQueue.Clear();
        execWaitingFor = null;
    }

    /// <summary>The same "loaded and settled" as <c>mt-ctl --wait-ready --world</c>: a world on screen, a selected character, the simulation has ticked (or is paused). A save with no selection runs after 3 s loaded.</summary>
    bool SetupSettled()
    {
        if (host.Covered || host.player.TitleOpen) { setupLoadedAt = 0; return false; }
        if (setupLoadedAt == 0) setupLoadedAt = Stopwatch.GetTimestamp();
        if (!host.session.Simulation.IsPaused && host.session.ShownTick < 1) return false;
        return host.session.World.Player.Selection.Any() || Stopwatch.GetElapsedTime(setupLoadedAt).TotalSeconds >= 3;
    }

    /// <summary>Runs one command line as the channel does; false when the command failed (printed an error).</summary>
    bool RunControlLine(string id, string command)
    {
        currentControlId = id;
        var output = new List<string>();
        bool failed = false;
        controlData = null;
        controlReplyDeferred = false;
        var before = ControlSnapshot();
        host.devConsoleHost.devConsole.Tap = (text, kind) => { output.Add(text); failed |= kind == DevConsole.Kind.Error; };
        try { host.devConsoleHost.devConsole.Execute(command); }
        finally { host.devConsoleHost.devConsole.Tap = null; }
        string result = string.Join(" | ", output);
        if (failed) Console.WriteLine($"control   {id} {command}: {result}");
        if (!failed && shotWaitId == id && shotPath is not null) return true;   // the screenshot's frame writes this one
        if (controlReplyDeferred) { controlData = null; return !failed; }   // wait-for answers later
        if (!failed && controlDir is not null && (command.StartsWith("ui-click", StringComparison.Ordinal) || command.StartsWith("ui-open", StringComparison.Ordinal)))
            controlSettles.Add(new ControlSettle(id, result, controlData, before));   // reply after a few frames with "changed"
        else ControlWrite(id, !failed, result, controlData);
        controlData = null;
        return !failed;
    }

    /// <summary>Hands a JSON answer to the reply (<see cref="controlData"/>); without a control channel the console prints it instead. The log gets only its size, not up to 23 KB per call.</summary>
    internal void PrintData(DevConsole con, string json)
    {
        controlData = json;
        con.Print(controlDir is null ? json : $"{json.Length} bytes of JSON");
    }

    double ControlNow() => Math.Round(Stopwatch.GetElapsedTime(controlStart).TotalSeconds, 2);

    IReadOnlyList<string> ControlScreenSet()
    {
        var l = new List<string>(host.player.OpenScreens);
        if (settingsPanel?.Visible == true) l.Add("settings");
        return l;
    }

    ControlSnap ControlSnapshot()
    {
        bool loaded = !host.Covered && !host.player.TitleOpen;
        return new ControlSnap(string.Join(',', ControlScreenSet()), ControlScreen(), settingsPanel is { Visible: true } p ? p.PageName : "", loaded, host.player.UiMode.ToString(), loaded ? host.session.World.Player.Selection.Count() : 0);
    }

    /// <summary>Every poll tick: diff the snapshot into events, then check the pending wait-for conditions.</summary>
    void ControlTick()
    {
        host.agentWorld.ControlCameraTick();
        var now = ControlSnapshot();
        if (now.Loaded) { host.player.ControlKills(); host.player.ControlCombatSample(); host.agentPlay.ControlSpawnPoll(); host.agentPlay.ControlMoveWaitPoll(); }
        if (lastSnap is { } old && controlEvents && old != now)
        {
            if (old.Screens != now.Screens) ControlEvent("screen", w => { w.WriteString("screen", now.Screen); w.WriteStartArray("screens"); foreach (string s in ControlScreenSet()) w.WriteStringValue(s); w.WriteEndArray(); });
            if (old.Page != now.Page) ControlEvent("settings-page", w => w.WriteString("page", now.Page.Length > 0 ? now.Page : null));
            if (old.Loaded != now.Loaded) ControlEvent("loaded", w => w.WriteBoolean("loaded", now.Loaded));
            if (old.Mode != now.Mode) ControlEvent("mode", w => w.WriteString("mode", now.Mode));
            if (old.Selected != now.Selected) ControlEvent("selection", w => w.WriteNumber("selected", now.Selected));
        }
        lastSnap = now;
        for (int i = controlWaits.Count - 1; i >= 0; i--)
        {
            var wt = controlWaits[i];
            if (WaitHolds(wt.Key, wt.Value, now)) { controlWaits.RemoveAt(i); WaitReply(wt, true); }
            else if (Stopwatch.GetTimestamp() >= wt.Deadline) { controlWaits.RemoveAt(i); WaitReply(wt, false); }
        }
    }

    void ControlEvent(string kind, Action<Utf8JsonWriter> data)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("event", kind);
            w.WriteNumber("seq", ++controlSeq);
            w.WriteNumber("t", ControlNow());
            w.WriteStartObject("data");
            data(w);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        try { File.AppendAllText(Path.Combine(controlDir!, "out"), System.Text.Encoding.UTF8.GetString(stream.ToArray()) + "\n"); }
        catch (IOException e) { Console.WriteLine($"control   cannot write out: {e.Message}"); }
    }

    bool WaitHolds(string key, string value, ControlSnap now)
    {
        switch (key)
        {
            case "screen": return now.Screen.Equals(value, StringComparison.OrdinalIgnoreCase) || ControlScreenSet().Any(s => s.Equals(value, StringComparison.OrdinalIgnoreCase));
            case "settings-page": return now.Page.Equals(value, StringComparison.OrdinalIgnoreCase);
            case "loaded": return now.Loaded == !value.Equals("false", StringComparison.OrdinalIgnoreCase);
            case "mode": return now.Mode.Equals(value, StringComparison.OrdinalIgnoreCase);
            case "widget": return ControlWidgets().Any(x => x.Id.Equals(value, StringComparison.OrdinalIgnoreCase));
            case "no-widget": return !ControlWidgets().Any(x => x.Id.Equals(value, StringComparison.OrdinalIgnoreCase));
            default: return host.agentWorld.WorldWaitHolds(key, value);
        }
    }

    void WaitReply(ControlWait wt, bool ok)
    {
        double ms = Math.Round(Stopwatch.GetElapsedTime(wt.Started).TotalMilliseconds);
        string data = JsonSerializer.Serialize(new Dictionary<string, object> { ["key"] = wt.Key, ["value"] = wt.Value, ["waitedMs"] = ms, ["clock"] = "real" });
        ControlWrite(wt.Id, ok, ok ? "" : "timeout", data);
    }

    /// <summary>Appends one reply object to out: id (a number when it is one), ok, data (the raw JSON given, else {"text":..}) and, on failure, error.</summary>
    internal void ControlWrite(string id, bool ok, string text, string? dataJson = null, bool? changed = null)
    {
        if (id == "exec" && execWaitingFor is { } waited)   // the deferred reply of a setup command
        {
            if (ok && dataJson is not null && dataJson.Contains("\"result\":\"", StringComparison.Ordinal) && !dataJson.Contains("\"result\":\"arrived\"", StringComparison.Ordinal))
            { ok = false; text = $"waited move did not arrive: {dataJson}"; }   // a waited move that ended stuck, timed out or lost the walker
            if (!ok) Console.WriteLine($"setup     '{waited}': {text}");
            execWaitingFor = null;
            if (!ok) ExecFailed(waited);
        }
        if (controlDir is null) return;
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            if (long.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long n)) w.WriteNumber("id", n);
            else w.WriteString("id", id);
            w.WriteBoolean("ok", ok);
            if (!ok) w.WriteString("error", text);
            if (dataJson is not null) { w.WritePropertyName("data"); w.WriteRawValue(dataJson); }
            else if (ok) { w.WriteStartObject("data"); w.WriteString("text", text); w.WriteEndObject(); }
            if (changed is { } ch) w.WriteBoolean("changed", ch);
            w.WriteEndObject();
        }
        try { File.AppendAllText(Path.Combine(controlDir, "out"), System.Text.Encoding.UTF8.GetString(stream.ToArray()) + "\n"); }
        catch (IOException e) { Console.WriteLine($"control   cannot write out: {e.Message}"); }
    }

    /// <summary>The frame asked for by <c>screenshot</c> is on screen: saves it to the requested file and reports.</summary>
    internal void SaveControlShot(GpuContext context, GpuTexture picture, int width, int height, bool topDown)
    {
        string path = shotPath!;
        string? id = shotWaitId;
        shotPath = shotWaitId = null;
        try
        {
            FramebufferCapture.SavePng(context, picture, path, width, height, topDown);
            Console.WriteLine($"saved     {path}");
            if (id is not null) ControlWrite(id, true, $"saved {path} {width}x{height}");
            if (autoShotAsked) host.devConsoleHost.controlQuit = true;
        }
        catch (Exception e)   // a failed capture (GPU read-back, encoder) must answer too, not leave the client waiting
        {
            ControlShotFailed(e.Message, id);
        }
    }

    /// <summary>A requested control screenshot could not be made: say why to the waiting client, and end a <c>--screenshot-after</c> run with exit 4.</summary>
    internal void ControlShotFailed(string why, string? id = null)
    {
        if (id is null) { id = shotWaitId; host.screenshotRequested = false; }
        shotPath = shotWaitId = null;
        Console.WriteLine($"setup     screenshot failed: {why}");
        if (autoShotAsked) { controlExitCode = 4; host.devConsoleHost.controlQuit = true; }
        if (id is not null) ControlWrite(id, false, $"screenshot failed: {why}");
    }

    // ---- commands

    string ControlScreen()
    {
        if (settingsPanel?.Visible == true) return "settings";
        if (host.Covered) return "loading";
        if (host.player.TitleOpen) return "title";
        if (host.debugMenu.debugMenuOpen) return "debug-menu";
        return host.player.OpenScreens.Count > 0 ? host.player.OpenScreens[^1] : "world";
    }

    string ControlState()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("screen", ControlScreen());
            w.WriteStartArray("openScreens");
            foreach (string id in host.player.OpenScreens) w.WriteStringValue(id);
            w.WriteEndArray();
            w.WriteString("uiMode", host.player.UiMode.ToString());
            w.WriteBoolean("consoleOpen", host.devConsoleHost.devConsole.Open);
            if (settingsPanel is { Visible: true } panel) w.WriteString("settingsPage", panel.PageName);
            else w.WriteNull("settingsPage");
            if (!host.Covered && !host.player.TitleOpen) WriteWorldState(w);
            w.WriteBoolean("paused", host.session.Simulation.IsPaused);
            w.WriteNumber("speed", host.session.Simulation.Speed);
            w.WriteString("save", string.IsNullOrEmpty(host.saves.readingFolder) ? null : Path.GetFileName(host.saves.readingFolder.TrimEnd('/', '\\')));
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    void WriteWorldState(Utf8JsonWriter w)
    {
        var snapshot = host.session.CurrentSnapshot;
        var selected = host.session.World.Player.Selection.ToArray();
        var first = selected.Length > 0 ? snapshot.Characters.FirstOrDefault(c => c.Id == selected[0]) : null;
        w.WriteNumber("selected", selected.Length);
        w.WriteNumber("tick", host.session.ShownTick);   // wait-ready --world: the world has run
        if (first is not null)
        {
            var at = host.DrawnPosition(first);
            w.WriteStartObject("player");
            w.WriteString("id", $"c:{first.Id.Slot}.{first.Id.Generation}");
            w.WriteNumber("x", Math.Round(at.X, 1));
            w.WriteNumber("y", Math.Round(at.Y, 1));
            w.WriteNumber("z", Math.Round(at.Z, 1));
            w.WriteEndObject();
        }
        else w.WriteNull("player");
        w.WriteStartObject("camera");
        w.WriteNumber("x", Math.Round(host.camera.Target.X, 1));
        w.WriteNumber("z", Math.Round(host.camera.Target.Z, 1));
        w.WriteEndObject();
    }

    internal void RegisterControlCommands()
    {
        host.agentWorld.RegisterControlWorldCommands();
        var c = host.devConsoleHost.devConsole;
        void Add(string name, string usage, string help, Action<IReadOnlyList<string>, DevConsole> run, Func<int, string, IEnumerable<string>>? complete = null) =>
            c.Register(new ConsoleCommand(name, usage, help, run) { Complete = complete });

        Add("state", "state", "one line of JSON: screen, open settings page, player position, loaded save", (_, con) => { string json = ControlState(); PrintData(con, json); });

        Add("screenshot", "screenshot <png path>", "save the next frame (interface included, no debug overlays or pointer) to the file", (args, con) =>
        {
            if (args.Count != 1) { con.Error("usage: screenshot <png path>"); return; }
            string path = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            shotPath = path;
            host.screenshotRequested = true;
            shotWaitId = currentControlId;
            con.Print($"queued {path}");
        });

        Add("quit", "quit", "end the interactive loop and exit cleanly (as closing the window does)", (_, con) => { host.devConsoleHost.controlQuit = true; con.Print("quitting"); });

        Add("mouse", "mouse <x> <y>", "headless runs only: put the pointer at screen pixel x y (hover, tooltips and picking follow it; a window's real pointer is not moved)", (args, con) =>
        {
            if (!host.o.Headless) { con.Error("mouse: only with --headless (use the real pointer otherwise)"); return; }
            if (args.Count != 2 || !float.TryParse(args[0], System.Globalization.CultureInfo.InvariantCulture, out float x) || !float.TryParse(args[1], System.Globalization.CultureInfo.InvariantCulture, out float y)) { con.Error("usage: mouse <x> <y>"); return; }
            host.devConsoleHost.headlessMouse = new System.Numerics.Vector2(x, y);
            con.Print($"pointer {x:0} {y:0}");
        });

        Add("click", "click <x> <y> [--button left|right] [--double]", "press and release a pointer button at screen pixel x y through the game's own input path (widgets, world picking, build-mode placement behave as for a user click); windowed and headless; played over the next frames, the reply says it was queued (the window's real pointer is not moved)", (args, con) =>
        {
            bool right = false, dbl = false;
            var pos = new List<string>();
            for (int i = 0; i < args.Count; i++)
            {
                if (args[i] == "--double") dbl = true;
                else if (args[i] == "--button" && i + 1 < args.Count && args[i + 1] is "left" or "right") right = args[++i] == "right";
                else pos.Add(args[i]);
            }
            if (pos.Count != 2 || !float.TryParse(pos[0], System.Globalization.CultureInfo.InvariantCulture, out float x) || !float.TryParse(pos[1], System.Globalization.CultureInfo.InvariantCulture, out float y)) { con.Error("usage: click <x> <y> [--button left|right] [--double]"); return; }
            var at = new System.Numerics.Vector2(x, y);
            host.devConsoleHost.pointerSteps.Enqueue(('m', right, at));
            host.devConsoleHost.pointerSteps.Enqueue(('i', right, at));
            for (int n = 0; n < (dbl ? 2 : 1); n++) { host.devConsoleHost.pointerSteps.Enqueue(('d', right, at)); host.devConsoleHost.pointerSteps.Enqueue(('u', right, at)); }
            con.Print($"queued {(right ? "right" : "left")}{(dbl ? " double" : "")} click at {x:0} {y:0}");
        });

        Add("events", "events on|off", "switch the event lines in out on or off (default on)", (args, con) =>
        {
            if (args.Count != 1 || args[0] is not ("on" or "off")) { con.Error("usage: events on|off"); return; }
            controlEvents = args[0] == "on";
            con.Print($"events {args[0]}");
        });

        Add("wait-for", "wait-for <key>=<value> [timeout=<s>]", "reply when the condition holds (keys: screen, settings-page, loaded, widget, no-widget, mode, camera-settled, selected=<n>, pos-near=<x>,<z>,<r>[,<id>]) or with error timeout after timeout real seconds (default 10)", (args, con) =>
        {
            double seconds = 10;
            var rest = args.ToList();
            if (rest.Count > 1 && rest[^1].StartsWith("timeout=", StringComparison.OrdinalIgnoreCase))
            {
                if (!double.TryParse(rest[^1][8..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out seconds) || seconds < 0) { con.Error("wait-for: bad timeout"); return; }
                rest.RemoveAt(rest.Count - 1);
            }
            string cond = string.Join(' ', rest);
            int eq = cond.IndexOf('=');
            string key = (eq < 0 ? cond : cond[..eq]).Trim().ToLowerInvariant(), value = eq < 0 ? "true" : cond[(eq + 1)..].Trim();
            if (key is not ("screen" or "settings-page" or "loaded" or "widget" or "no-widget" or "mode" or "camera-settled" or "selected" or "pos-near") || (value.Length == 0)) { con.Error("usage: wait-for <screen|settings-page|loaded|widget|no-widget|mode|camera-settled|selected|pos-near>=<value> [timeout=<s>]  (pos-near=<x>,<z>,<r>[,<id>])"); return; }
            var wt = new ControlWait(currentControlId ?? "0", key, value, Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency), Stopwatch.GetTimestamp(), seconds);
            if (WaitHolds(key, value, ControlSnapshot())) { controlData = JsonSerializer.Serialize(new Dictionary<string, object> { ["key"] = key, ["value"] = value, ["waitedMs"] = 0, ["clock"] = "real" }); con.Print($"{key}={value} already holds"); return; }
            controlWaits.Add(wt);
            controlReplyDeferred = true;
            con.Print($"waiting for {key}={value}");
        });

        Add("ui-tree", "ui-tree", "list the clickable widgets of the top screen: index, label, x y w h in screen pixels", (_, con) => ControlUiTree(con));

        Add("ui-click", "ui-click <label|index> | ui-click [--id <widgetId>] [--label <text>] [--screen <name>] [--index <n>]", "press a widget by label (exact, else unique part), ui-tree index, or selectors that must all match (ambiguity is an error)", (args, con) =>
        {
            if (args.Count == 0) { con.Error("usage: ui-click <label|index> | ui-click --id <id> --label <text> --screen <name> --index <n>"); return; }
            ControlUiClick(args, con);
        });

        Add("ui-set", "ui-set <label|index> <0..1> | ui-set [--id ..] [--label ..] [--screen ..] [--index ..] <0..1>", "move a settings slider to a position on its track (0 left, 1 right)", (args, con) => ControlUiSet(args, con));

        Add("clipboard", "clipboard", "print the game's clipboard text as a JSON string (null when it holds none)", (_, con) => { string json = JsonSerializer.Serialize(host.player.ClipboardRead?.Invoke()); PrintData(con, json); });

        Add("ui-open", "ui-open settings[:<page>]", "open the settings window, on a page (" + string.Join(", ", SettingsMenu.PageNames) + ")", (args, con) =>
        {
            if (args.Count != 1) { con.Error("usage: ui-open settings[:<page>]"); return; }
            string[] parts = args[0].Split(':', 2);
            if (!parts[0].Equals("settings", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length == 1 && host.player.OpenFaithfulScreen(parts[0])) con.Print($"{parts[0]} open");
                else con.Error($"ui-open: unknown window '{parts[0]}' (settings, or a screen id such as menu, load, save)");
                return;
            }
            if (settingsPanel is not { } panel) { con.Error("ui-open: this mode has no settings window"); return; }
            if (parts.Length == 2 && !panel.TryOpenPage(parts[1])) { con.Error($"ui-open: unknown settings page '{parts[1]}' (known: {string.Join(", ", SettingsMenu.PageNames)})"); return; }
            panel.Visible = true;
            con.Print($"settings open on {panel.PageName}");
        }, (i, typed) => i == 0 ? SettingsMenu.PageNames.Select(p => "settings:" + p).Where(p => p.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) : []);
    }

    /// <summary>One clickable widget for ui-tree / ui-click: its label, screen-pixel bounds, widget id (screen/name), the screen it is on and how to press it.</summary>
    sealed record ControlWidget(string Label, float X, float Y, float W, float H, string Id, string Screen, Action Click, Meitou.Game.Rendering.Frame.Slider? Slider = null);

    /// <summary>The clickable widgets of the screen on top: the settings window while it is open, else the faithful screens (title, pause menu, load/save, windows) and the world HUD.</summary>
    List<ControlWidget> ControlWidgets()
    {
        var list = new List<ControlWidget>();
        if (settingsPanel is { Visible: true } panel)
            foreach (var (label, b, activate) in panel.Clickables)
                list.Add(new(label, b.X, b.Y, b.Width, b.Height, "settings:" + panel.PageName + "/" + label, "settings", activate));
        if (settingsPanel is { Visible: true } open)
            foreach (var (slider, b) in open.SliderHits)
                list.Add(new(slider.Label, b.X, b.Y, b.Width, b.Height, "settings:" + open.PageName + "/" + slider.Label, "settings", () => { }, slider));
        else
            foreach (var (screen, c) in host.player.UiClickables())
            {
                string label = (c.Caption.Length > 0 ? c.Caption : c.Properties.GetValueOrDefault(UiTooltips.Text, "").Length > 0 ? c.Properties[UiTooltips.Text] : c.Name).ReplaceLineEndings(" ").Trim();
                if (label.Length > 48) label = label[..48].TrimEnd() + "...";   // a long tooltip stays a usable label
                var centre = new System.Numerics.Vector2(c.Bounds.X + c.Bounds.Width / 2, c.Bounds.Y + c.Bounds.Height / 2);
                list.Add(new(label, c.Bounds.X, c.Bounds.Y, c.Bounds.Width, c.Bounds.Height, $"{screen}/{c.Name}", screen, () => host.player.UiClickAt(centre)));
            }
        return list;
    }

    void WriteWidget(Utf8JsonWriter w, int i, ControlWidget x)
    {
        w.WriteStartObject();
        w.WriteNumber("i", i);
        w.WriteString("label", x.Label);
        w.WriteStartArray("rect");
        foreach (float v in new[] { x.X, x.Y, x.W, x.H }) w.WriteNumberValue((int)MathF.Round(v));
        w.WriteEndArray();
        w.WriteString("id", x.Id);
        w.WriteString("screen", x.Screen);
        if (x.Slider is { } sl)
        {
            w.WriteNumber("slider", Math.Round(sl.Fraction, 3));
            w.WriteString("display", sl.Display);
        }
        w.WriteEndObject();
    }

    string WidgetsJson(IEnumerable<(int Index, ControlWidget Widget)> items)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartArray();
            foreach (var (i, x) in items) WriteWidget(w, i, x);
            w.WriteEndArray();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    void ControlUiTree(DevConsole con)
    {
        var widgets = ControlWidgets();
        controlData = WidgetsJson(widgets.Select((x, i) => (i, x)));
        for (int i = 0; i < widgets.Count; i++)
        {
            var w = widgets[i];
            con.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{i} {w.Label} {MathF.Round(w.X)} {MathF.Round(w.Y)} {MathF.Round(w.W)} {MathF.Round(w.H)} [{w.Id}]{(w.Slider is { } sl ? $" [slider] {sl.Fraction:0.###} ({sl.Display})" : "")}"));
        }
        if (widgets.Count == 0) con.Print("(no clickable widgets)");
    }

    /// <summary>Splits <c>--id/--label/--screen/--index &lt;value...&gt;</c> selectors from the positional rest (a value runs to the next --flag); null after an error message.</summary>
    (Dictionary<string, string> Flags, string Positional)? ControlSelector(string cmd, IReadOnlyList<string> args, DevConsole con)
    {
        var flags = new Dictionary<string, string>();
        var rest = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (a is "--id" or "--label" or "--screen" or "--index")
            {
                var value = new List<string>();
                while (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) value.Add(args[++i]);
                if (value.Count == 0) { con.Error($"{cmd}: {a} needs a value"); return null; }
                flags[a] = string.Join(' ', value);
            }
            else rest.Add(a);
        }
        return (flags, string.Join(' ', rest));
    }

    /// <summary>The widget the selectors name: flags AND together, then the positional label (exact, else unique part) or ui-tree index narrows further. Null after an error reply that lists candidates as JSON.</summary>
    ControlWidget? ControlFind(string cmd, IReadOnlyList<string> args, DevConsole con)
    {
        if (ControlSelector(cmd, args, con) is not var (flags, target)) return null;
        var widgets = ControlWidgets();
        var pool = widgets.Select((x, i) => (Index: i, Widget: x)).ToList();
        if (flags.TryGetValue("--id", out string? id)) pool = pool.Where(p => p.Widget.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (flags.TryGetValue("--label", out string? label)) pool = pool.Where(p => p.Widget.Label.Equals(label, StringComparison.OrdinalIgnoreCase)).ToList();
        if (flags.TryGetValue("--screen", out string? screen)) pool = pool.Where(p => p.Widget.Screen.Equals(screen, StringComparison.OrdinalIgnoreCase)).ToList();
        if (flags.TryGetValue("--index", out string? idx))
        {
            if (!int.TryParse(idx, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n)) { con.Error($"{cmd}: --index needs a number"); return null; }
            pool = pool.Where(p => p.Index == n).ToList();
        }
        if (target.Length > 0)
        {
            var hit = pool.Where(p => p.Widget.Label.Equals(target, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hit.Count == 0 && int.TryParse(target, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index)) hit = pool.Where(p => p.Index == index).ToList();
            if (hit.Count == 0) hit = pool.Where(p => p.Widget.Label.Contains(target, StringComparison.OrdinalIgnoreCase)).ToList();
            pool = hit;
        }
        string wanted = string.Join(' ', args);
        if (pool.Count == 0)
        {
            controlData = WidgetsJson(widgets.Select((x, i) => (i, x)));   // nothing matched: everything there is
            con.Error($"{cmd}: nothing matches '{wanted}' (see ui-tree)");
            return null;
        }
        if (pool.Count > 1)
        {
            controlData = WidgetsJson(pool.Select(p => (p.Index, p.Widget)));
            con.Error($"{cmd}: '{wanted}' is ambiguous: {string.Join(", ", pool.Select(p => $"{p.Index} {p.Widget.Label} [{p.Widget.Id}]"))}");
            return null;
        }
        return pool[0].Widget;
    }

    void ControlUiClick(IReadOnlyList<string> args, DevConsole con)
    {
        if (ControlFind("ui-click", args, con) is not { } w) return;
        if (w.Slider is not null) { con.Error($"ui-click: '{w.Label}' is a slider, use ui-set"); return; }
        w.Click();
        con.Print($"clicked {w.Label}");
    }

    void ControlUiSet(IReadOnlyList<string> args, DevConsole con)
    {
        if (args.Count < 2 || !float.TryParse(args[^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float t)) { con.Error("usage: ui-set <label|index> <0..1> (or selectors, then the number)"); return; }
        if (ControlFind("ui-set", args.Take(args.Count - 1).ToList(), con) is not { } w) return;
        if (w.Slider is not { } slider) { con.Error($"ui-set: '{w.Label}' is not a slider"); return; }
        slider.SetFraction(t);
        con.Print($"{w.Label} = {slider.Display}");
    }
}
