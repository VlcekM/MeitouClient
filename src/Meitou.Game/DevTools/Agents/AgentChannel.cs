using System.Diagnostics;
using System.Text.Json;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Gpu;
using Meitou.Game.Ui;

namespace Meitou.Game.DevTools.Agents;

/// <summary>
/// The agent control channel (debug only, docs/procedures/todo-faster-ingame-agent-tests.md): with <c>--control-dir &lt;dir&gt;</c> (or
/// MEITOU_CONTROL_DIR) the game polls <c>&lt;dir&gt;/cmd</c> on the game thread about four times a second; each line <c>&lt;id&gt; &lt;console
/// command&gt;</c> is run by <see cref="DevConsole.Execute"/> (the console window need not be open), and one result line per command is appended to
/// <c>&lt;dir&gt;/out</c> as one JSON object: <c>{"id":n,"ok":true,"data":..}</c> or <c>{"id":n,"ok":false,"error":"..","data":..}</c>; <c>data</c>
/// is the command's structured result (state, ui-tree, ui-click candidates) or <c>{"text":".."}</c> with the printed lines joined by " | ".
/// <c>--exec</c> (or <c>--setup</c>) runs commands once, in order, after the world has loaded and settled; the first failing one stops the list and
/// the run exits with code 3. Without the flag nothing here runs.
/// </summary>
sealed class AgentChannel(GameHost host)
{
    /// <summary>
    /// The whole interface hidden (F7, or the <c>hud hide</c> console command).
    /// </summary>
    internal bool InterfaceHidden;

    /// <summary>
    /// The control directory (<c>--control-dir</c>, else <c>MEITOU_CONTROL_DIR</c>; null without one), and the <c>--exec</c> list not yet run.
    /// </summary>
    internal string? ControlDir, ExecPending;

    /// <summary>
    /// When the world was first seen loaded (for the setup's settle wait), and the exit code the run ends with (0, or 3 when a setup command failed).
    /// </summary>
    long _setupLoadedAt, _shotDueAt;

    /// <summary>
    /// --screenshot-after: the setup is done and the timer runs (shotDueAt), or the picture was asked for and its save ends the run.
    /// </summary>
    bool _autoShotTimer, _autoShotAsked;

    /// <summary>
    /// The exit code the run ends with: 0, or 3 when a setup command failed.
    /// </summary>
    internal int ControlExitCode;

    /// <summary>
    /// The Stopwatch time of the next command file poll (every 0.25 s).
    /// </summary>
    long _nextControlPoll;

    /// <summary>
    /// The settings window of the interactive loop (null in the one-shot modes), for the console commands.
    /// </summary>
    internal SettingsMenu? SettingsPanel;

    /// <summary>
    /// The id whose result waits for the screenshot's frame to be saved, and the file it goes to.
    /// </summary>
    internal string? ShotWaitId, ShotPath, CurrentControlId;

    /// <summary>
    /// Structured result of the running control command (raw JSON), for the file channel; the console keeps its printed text.
    /// </summary>
    internal string? ControlData;

    /// <summary>
    /// Event stream: on by default, last snapshot, event counter, start time; pending wait-for conditions; replies held a few frames to report
    /// "changed".
    /// </summary>
    internal bool ControlEvents = true, ControlReplyDeferred;

    /// <summary>
    /// The snapshot of the last poll tick; null before the first.
    /// </summary>
    ControlSnap? _lastSnap;

    /// <summary>
    /// The last event's sequence number.
    /// </summary>
    private long _controlSeq;

    /// <summary>
    /// When the channel started (Stopwatch), the events' clock.
    /// </summary>
    private readonly long _controlStart = Stopwatch.GetTimestamp();

    /// <summary>
    /// The pending wait-for conditions.
    /// </summary>
    readonly List<ControlWait> _controlWaits = [];

    /// <summary>
    /// The replies held a few frames to report whether the screen or page changed.
    /// </summary>
    readonly List<ControlSettle> _controlSettles = [];

    /// <summary>
    /// The small state diffed every poll tick for events and changed.
    /// </summary>
    sealed record ControlSnap(string Screens, string Screen, string Page, bool Loaded, string Mode, int Selected);

    /// <summary>
    /// A pending wait-for: the command id, the condition's key and value, the deadline and start (Stopwatch) and the timeout in seconds.
    /// </summary>
    sealed record ControlWait(string Id, string Key, string Value, long Deadline, long Started, double TimeoutSeconds);

    /// <summary>
    /// A reply held a few frames: its id, text, data and the snapshot before the command, written with "changed" when the frames are up.
    /// </summary>
    sealed class ControlSettle(string id, string text, string? data, ControlSnap before)
    {
        /// <summary>
        /// The command id and the reply text.
        /// </summary>
        public string Id = id, Text = text;

        /// <summary>
        /// The reply's raw JSON data; null for none.
        /// </summary>
        public string? Data = data;

        /// <summary>
        /// The snapshot before the command.
        /// </summary>
        public ControlSnap Before = before;

        /// <summary>
        /// The frames left before the reply is written.
        /// </summary>
        public int Frames = 4;
    }

    /// <summary>
    /// Reads the control directory (made when missing) and the <c>--exec</c> list.
    /// </summary>
    internal void InitControl()
    {
        ControlDir = host.GameOptions.ControlDir ?? Environment.GetEnvironmentVariable("MEITOU_CONTROL_DIR");
        if (string.IsNullOrEmpty(ControlDir)) ControlDir = null;
        else Directory.CreateDirectory(ControlDir);
        ExecPending = host.GameOptions.Exec;
    }

    /// <summary>
    /// Once per frame: the one-shot <c>--exec</c> after the load, then the command file at most every 0.25 s.
    /// </summary>
    internal void PollControl()
    {
        if (!host.DevConsoleHost.ConsoleRegistered) host.DevConsoleHost.RegisterConsoleCommands();
        if (ExecPending is { } exec && SetupSettled())
        {
            ExecPending = null;
            foreach (string part in exec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                _execQueue.Enqueue(part);
        }
        PumpExec();
        if (host.GameOptions.ShotAfter is { } shotDelay && !_autoShotTimer && !_autoShotAsked && ExecPending is null && _execQueue.Count == 0
            && _execWaitingFor is null && ControlExitCode == 0 && SetupSettled())
        {
            _autoShotTimer = true;
            _shotDueAt = Stopwatch.GetTimestamp() + (long)(shotDelay * Stopwatch.Frequency);
        }
        // a failed setup ends a screenshot run at once
        if (ControlExitCode != 0 && host.GameOptions.ShotAfter is not null) host.DevConsoleHost.ControlQuit = true;
        if (_autoShotTimer && Stopwatch.GetTimestamp() >= _shotDueAt)
        {
            _autoShotTimer = false;
            _autoShotAsked = true;
            string path = Path.GetFullPath(host.GameOptions.ShotOut
                ?? Path.Combine(ControlDir ?? Directory.GetCurrentDirectory(), ControlDir is null ? "meitou-shot.png" : "shot.png"));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"setup     screenshot failed: {e.Message}");
                ControlExitCode = 4;
                host.DevConsoleHost.ControlQuit = true;
            }

            if (!host.DevConsoleHost.ControlQuit)
            {
                ShotPath = path;
                ShotWaitId = null;
                host.ScreenshotRequested = true;
            }
        }
        if (ControlDir is null) return;
        for (int i = _controlSettles.Count - 1; i >= 0; i--)
        {
            var st = _controlSettles[i];
            if (--st.Frames > 0) continue;
            _controlSettles.RemoveAt(i);
            ControlWrite(st.Id, true, st.Text, st.Data, ControlSnapshot() is var after
                && (after.Screens != st.Before.Screens || after.Page != st.Before.Page));
        }
        if (Stopwatch.GetTimestamp() < _nextControlPoll) return;
        _nextControlPoll = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 4;
        ControlTick();
        string file = Path.Combine(ControlDir, "cmd");
        string[] lines;
        try
        {
            if (!File.Exists(file)) return;
            lines = File.ReadAllLines(file);
            File.Delete(file);
        }
        catch (IOException e)
        {
            Console.WriteLine($"control   cannot read {file}: {e.Message}");
            return;
        }

        foreach (string line in lines.Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            int space = line.IndexOf(' ');
            RunControlLine(space < 0 ? line : line[..space], space < 0 ? "" : line[(space + 1)..].Trim());
        }
    }

    /// <summary>
    /// The setup commands not yet run.
    /// </summary>
    readonly Queue<string> _execQueue = new();

    /// <summary>
    /// The setup command whose deferred reply (wait-for, order move --wait, spawn-at) is still pending; the setup list goes on when it arrives.
    /// </summary>
    string? _execWaitingFor;

    /// <summary>
    /// Runs the setup list one command at a time; a deferred command is waited on, and its failure ends the list (exit 3).
    /// </summary>
    void PumpExec()
    {
        while (_execWaitingFor is null && _execQueue.Count > 0)
        {
            string part = _execQueue.Dequeue();
            bool ok = RunControlLine("exec", part);
            if (ok && ControlReplyDeferred)
            {
                if (ControlDir is null)
                {
                    Console.WriteLine($"setup     '{part}' answers later and needs --control-dir to be waited on");
                    ok = false;
                }
                else
                {
                    _execWaitingFor = part;
                    return;
                }
            }
            if (!ok)
            {
                ExecFailed(part);
                return;
            }
        }
    }

    /// <summary>
    /// Ends the setup list after <paramref name="part"/> failed: exit code 3, the rest skipped.
    /// </summary>
    void ExecFailed(string part)
    {
        Console.WriteLine($"setup     setup failed: '{part}' (the rest was skipped)");
        ControlExitCode = 3;
        _execQueue.Clear();
        _execWaitingFor = null;
    }

    /// <summary>
    /// The same "loaded and settled" as <c>mt-ctl --wait-ready --world</c>: a world on screen, a selected character, the simulation has ticked (or is
    /// paused). A save with no selection runs after 3 s loaded.
    /// </summary>
    bool SetupSettled()
    {
        if (host.Covered || host.Player.TitleOpen)
        {
            _setupLoadedAt = 0;
            return false;
        }

        if (_setupLoadedAt == 0) _setupLoadedAt = Stopwatch.GetTimestamp();
        if (!host.Session.Simulation.IsPaused && host.Session.ShownTick < 1) return false;
        return host.Session.World.Player.Selection.Any() || Stopwatch.GetElapsedTime(_setupLoadedAt).TotalSeconds >= 3;
    }

    /// <summary>
    /// Runs one command line as the channel does; false when the command failed (printed an error).
    /// </summary>
    bool RunControlLine(string id, string command)
    {
        CurrentControlId = id;
        var output = new List<string>();
        bool failed = false;
        ControlData = null;
        ControlReplyDeferred = false;
        var before = ControlSnapshot();
        host.DevConsoleHost.DevConsole.Tap = (text, kind) => { output.Add(text); failed |= kind == DevConsole.Kind.Error; };
        try
        {
            host.DevConsoleHost.DevConsole.Execute(command);
        }
        finally
        {
            host.DevConsoleHost.DevConsole.Tap = null;
        }

        string result = string.Join(" | ", output);
        if (failed) Console.WriteLine($"control   {id} {command}: {result}");
        if (!failed && ShotWaitId == id && ShotPath is not null) return true;   // the screenshot's frame writes this one
        // wait-for answers later
        if (ControlReplyDeferred)
        {
            ControlData = null;
            return !failed;
        }

        if (!failed && ControlDir is not null && (command.StartsWith("ui-click", StringComparison.Ordinal)
            || command.StartsWith("ui-open", StringComparison.Ordinal)))
            _controlSettles.Add(new ControlSettle(id, result, ControlData, before));   // reply after a few frames with "changed"
        else ControlWrite(id, !failed, result, ControlData);
        ControlData = null;
        return !failed;
    }

    /// <summary>
    /// Hands a JSON answer to the reply (<see cref="ControlData"/>); without a control channel the console prints it instead. The log gets only its
    /// size, not up to 23 KB per call.
    /// </summary>
    internal void PrintData(DevConsole con, string json)
    {
        ControlData = json;
        con.Print(ControlDir is null ? json : $"{json.Length} bytes of JSON");
    }

    /// <summary>
    /// Seconds since the channel started, two decimals.
    /// </summary>
    double ControlNow() => Math.Round(Stopwatch.GetElapsedTime(_controlStart).TotalSeconds, 2);

    /// <summary>
    /// The open screens, with <c>settings</c> when the settings window shows.
    /// </summary>
    IReadOnlyList<string> ControlScreenSet()
    {
        var l = new List<string>(host.Player.OpenScreens);
        if (SettingsPanel?.Visible == true) l.Add("settings");
        return l;
    }

    /// <summary>
    /// The snapshot diffed for events: the open screens, the top screen, the settings page, loaded, the UI mode and the selection count.
    /// </summary>
    ControlSnap ControlSnapshot()
    {
        bool loaded = !host.Covered && !host.Player.TitleOpen;
        return new ControlSnap(string.Join(',', ControlScreenSet()), ControlScreen(), SettingsPanel is { Visible: true } p ? p.PageName : "", loaded,
            host.Player.UiMode.ToString(), loaded ? host.Session.World.Player.Selection.Count() : 0);
    }

    /// <summary>
    /// Every poll tick: diff the snapshot into events, then check the pending wait-for conditions.
    /// </summary>
    void ControlTick()
    {
        host.AgentWorld.ControlCameraTick();
        var now = ControlSnapshot();
        if (now.Loaded)
        {
            host.Player.AgentOrders.Kills.ControlKills();
            host.Player.AgentOrders.Combat.ControlCombatSample();
            host.AgentPlay.ControlSpawnPoll();
            host.AgentPlay.ControlMoveWaitPoll();
        }

        if (_lastSnap is { } old && ControlEvents && old != now)
        {
            if (old.Screens != now.Screens)
            {
                ControlEvent("screen", w =>
                {
                    w.WriteString("screen", now.Screen);
                    w.WriteStartArray("screens");
                    foreach (string s in ControlScreenSet()) w.WriteStringValue(s);
                    w.WriteEndArray();
                });
            }
            if (old.Page != now.Page)
                ControlEvent("settings-page", w => w.WriteString("page", now.Page.Length > 0 ? now.Page : null));
            if (old.Loaded != now.Loaded) ControlEvent("loaded", w => w.WriteBoolean("loaded", now.Loaded));
            if (old.Mode != now.Mode) ControlEvent("mode", w => w.WriteString("mode", now.Mode));
            if (old.Selected != now.Selected) ControlEvent("selection", w => w.WriteNumber("selected", now.Selected));
        }
        _lastSnap = now;
        for (int i = _controlWaits.Count - 1; i >= 0; i--)
        {
            var wt = _controlWaits[i];
            if (WaitHolds(wt.Key, wt.Value, now))
            {
                _controlWaits.RemoveAt(i);
                WaitReply(wt, true);
            }
            else if (Stopwatch.GetTimestamp() >= wt.Deadline)
            {
                _controlWaits.RemoveAt(i);
                WaitReply(wt, false);
            }
        }
    }

    /// <summary>
    /// Appends event <paramref name="kind"/> to <c>out</c>: its sequence number, time and the data <paramref name="data"/> writes.
    /// </summary>
    void ControlEvent(string kind, Action<Utf8JsonWriter> data)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("event", kind);
            w.WriteNumber("seq", ++_controlSeq);
            w.WriteNumber("t", ControlNow());
            w.WriteStartObject("data");
            data(w);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        try
        {
            File.AppendAllText(Path.Combine(ControlDir!, "out"), System.Text.Encoding.UTF8.GetString(stream.ToArray()) + "\n");
        }
        catch (IOException e)
        {
            Console.WriteLine($"control   cannot write out: {e.Message}");
        }
    }

    /// <summary>
    /// Whether wait-for condition <paramref name="key"/> = <paramref name="value"/> holds now (screen, settings page, loaded, mode, widget, no
    /// widget, else the world layer's).
    /// </summary>
    bool WaitHolds(string key, string value, ControlSnap now)
    {
        switch (key)
        {
            case "screen":
                return now.Screen.Equals(value, StringComparison.OrdinalIgnoreCase) || ControlScreenSet()
                    .Any(s => s.Equals(value, StringComparison.OrdinalIgnoreCase));
            case "settings-page": return now.Page.Equals(value, StringComparison.OrdinalIgnoreCase);
            case "loaded": return now.Loaded == !value.Equals("false", StringComparison.OrdinalIgnoreCase);
            case "mode": return now.Mode.Equals(value, StringComparison.OrdinalIgnoreCase);
            case "widget": return ControlWidgets().Any(x => x.Id.Equals(value, StringComparison.OrdinalIgnoreCase));
            case "no-widget": return !ControlWidgets().Any(x => x.Id.Equals(value, StringComparison.OrdinalIgnoreCase));
            default: return host.AgentWorld.WorldWaitHolds(key, value);
        }
    }

    /// <summary>
    /// Replies to a wait-for: ok or timeout, with the key, value and the real milliseconds waited.
    /// </summary>
    void WaitReply(ControlWait wt, bool ok)
    {
        double ms = Math.Round(Stopwatch.GetElapsedTime(wt.Started).TotalMilliseconds);
        string data = JsonSerializer.Serialize(new Dictionary<string, object> { ["key"] = wt.Key, ["value"] = wt.Value,
            ["waitedMs"] = ms, ["clock"] = "real" });
        ControlWrite(wt.Id, ok, ok ? "" : "timeout", data);
    }

    /// <summary>
    /// Appends one reply object to out: id (a number when it is one), ok, data (the raw JSON given, else {"text":..}) and, on failure, error.
    /// </summary>
    internal void ControlWrite(string id, bool ok, string text, string? dataJson = null, bool? changed = null)
    {
        if (id == "exec" && _execWaitingFor is { } waited)   // the deferred reply of a setup command
        {
            if (ok && dataJson is not null && dataJson.Contains("\"result\":\"", StringComparison.Ordinal)
                && !dataJson.Contains("\"result\":\"arrived\"", StringComparison.Ordinal))
            { ok = false; text = $"waited move did not arrive: {dataJson}"; }   // a waited move that ended stuck, timed out or lost the walker
            if (!ok) Console.WriteLine($"setup     '{waited}': {text}");
            _execWaitingFor = null;
            if (!ok) ExecFailed(waited);
        }
        if (ControlDir is null) return;
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            if (long.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long n))
                w.WriteNumber("id", n);
            else w.WriteString("id", id);
            w.WriteBoolean("ok", ok);
            if (!ok) w.WriteString("error", text);
            if (dataJson is not null)
            {
                w.WritePropertyName("data");
                w.WriteRawValue(dataJson);
            }
            else if (ok)
            {
                w.WriteStartObject("data");
                w.WriteString("text", text);
                w.WriteEndObject();
            }

            if (changed is { } ch) w.WriteBoolean("changed", ch);
            w.WriteEndObject();
        }
        try
        {
            File.AppendAllText(Path.Combine(ControlDir, "out"), System.Text.Encoding.UTF8.GetString(stream.ToArray()) + "\n");
        }
        catch (IOException e)
        {
            Console.WriteLine($"control   cannot write out: {e.Message}");
        }
    }

    /// <summary>
    /// The frame asked for by <c>screenshot</c> is on screen: saves it to the requested file and reports.
    /// </summary>
    internal void SaveControlShot(GpuContext context, GpuTexture picture, int width, int height, bool topDown)
    {
        string path = ShotPath!;
        string? id = ShotWaitId;
        ShotPath = ShotWaitId = null;
        try
        {
            FramebufferCapture.SavePng(context, picture, path, width, height, topDown);
            Console.WriteLine($"saved     {path}");
            if (id is not null) ControlWrite(id, true, $"saved {path} {width}x{height}");
            if (_autoShotAsked) host.DevConsoleHost.ControlQuit = true;
        }
        catch (Exception e)   // a failed capture (GPU read-back, encoder) must answer too, not leave the client waiting
        {
            ControlShotFailed(e.Message, id);
        }
    }

    /// <summary>
    /// A requested control screenshot could not be made: say why to the waiting client, and end a <c>--screenshot-after</c> run with exit 4.
    /// </summary>
    internal void ControlShotFailed(string why, string? id = null)
    {
        if (id is null)
        {
            id = ShotWaitId;
            host.ScreenshotRequested = false;
        }

        ShotPath = ShotWaitId = null;
        Console.WriteLine($"setup     screenshot failed: {why}");
        if (_autoShotAsked)
        {
            ControlExitCode = 4;
            host.DevConsoleHost.ControlQuit = true;
        }

        if (id is not null) ControlWrite(id, false, $"screenshot failed: {why}");
    }

    // ---- commands

    /// <summary>
    /// The top screen's name: settings, loading, title, debug-menu, the last open screen, else world.
    /// </summary>
    string ControlScreen()
    {
        if (SettingsPanel?.Visible == true) return "settings";
        if (host.Covered) return "loading";
        if (host.Player.TitleOpen) return "title";
        if (host.DebugMenu.DebugMenuOpen) return "debug-menu";
        return host.Player.OpenScreens.Count > 0 ? host.Player.OpenScreens[^1] : "world";
    }

    /// <summary>
    /// The state JSON: the screens, UI mode, console, settings page, the world's state when loaded, pause, speed and the save read.
    /// </summary>
    string ControlState()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("screen", ControlScreen());
            w.WriteStartArray("openScreens");
            foreach (string id in host.Player.OpenScreens) w.WriteStringValue(id);
            w.WriteEndArray();
            w.WriteString("uiMode", host.Player.UiMode.ToString());
            w.WriteBoolean("consoleOpen", host.DevConsoleHost.DevConsole.Open);
            if (SettingsPanel is { Visible: true } panel) w.WriteString("settingsPage", panel.PageName);
            else w.WriteNull("settingsPage");
            if (!host.Covered && !host.Player.TitleOpen) WriteWorldState(w);
            w.WriteBoolean("paused", host.Session.Simulation.IsPaused);
            w.WriteNumber("speed", host.Session.Simulation.Speed);
            w.WriteString("save", string.IsNullOrEmpty(host.Saves.ReadingFolder) ? null : Path.GetFileName(host.Saves.ReadingFolder.TrimEnd('/',
                '\\')));
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Writes the world's state: the selection count, the shown tick, the first selected character's id and position, and the camera's target.
    /// </summary>
    void WriteWorldState(Utf8JsonWriter w)
    {
        var snapshot = host.Session.CurrentSnapshot;
        var selected = host.Session.World.Player.Selection.ToArray();
        var first = selected.Length > 0 ? snapshot.Characters.FirstOrDefault(c => c.Id == selected[0]) : null;
        w.WriteNumber("selected", selected.Length);
        w.WriteNumber("tick", host.Session.ShownTick);   // wait-ready --world: the world has run
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
        w.WriteNumber("x", Math.Round(host.Camera.Target.X, 1));
        w.WriteNumber("z", Math.Round(host.Camera.Target.Z, 1));
        w.WriteEndObject();
        host.WriteInteriorState(w);
    }

    /// <summary>
    /// Registers the agent control channel's commands (state, screenshot, quit, mouse, click, events, wait-for, ui-*, clipboard) after the
    /// world layer's.
    /// </summary>
    internal void RegisterControlCommands()
    {
        host.AgentWorld.RegisterControlWorldCommands();
        var c = host.DevConsoleHost.DevConsole;
        void Add(string name, string usage, string help, Action<IReadOnlyList<string>, DevConsole> run,
            Func<int, string, IEnumerable<string>>? complete = null) =>
            c.Register(new ConsoleCommand(name, usage, help, run) { Complete = complete });

        Add("state", "state", "one line of JSON: screen, open settings page, player position, loaded save",
            (_, con) => { string json = ControlState(); PrintData(con, json); });

        Add("screenshot", "screenshot <png path>", "save the next frame (interface included, no debug overlays or pointer) "
            + "to the file", ControlScreenshot);

        Add("photo", "photo on|off|toggle | get | reset | set <name> <value> | cam <x> <y> <z> <yawDeg> <pitchDeg> | panel on|off | key <name> [up] | shot [file] [--ui]",
            "photo mode (docs/viewer.md): the free camera with lens, roll, exposure, drawn time of day, grids and frames; shot saves a picture without the panel "
            + "and grid (--ui keeps them)", ControlPhoto);
        Add("quit", "quit", "end the interactive loop and exit cleanly (as closing the window does)",
            (_, con) => { host.DevConsoleHost.ControlQuit = true; con.Print("quitting"); });

        Add("mouse", "mouse <x> <y>", "headless runs only: put the pointer at screen pixel x y (hover, tooltips and picking follow it; "
            + "a window's real pointer is not moved)", ControlMouse);

        Add("click", "click <x> <y> [--button left|right] [--double]", "press and release a pointer button at screen pixel x y through the game's "
            + "own input path (widgets, world picking, build-mode placement behave as for a user click); windowed and headless; "
            + "played over the next frames, the reply says it was queued (the window's real pointer is not moved)", ControlClick);

        Add("events", "events on|off", "switch the event lines in out on or off (default on)", ControlEventsCommand);

        Add("wait-for", "wait-for <key>=<value> [timeout=<s>]", "reply when the condition holds (keys: screen, settings-page, loaded, widget, "
            + "no-widget, mode, camera-settled, selected=<n>, pos-near=<x>,<z>,<r>[,<id>]) or with error timeout after timeout real seconds "
            + "(default 10)", ControlWaitFor);

        Add("ui-tree", "ui-tree", "list the clickable widgets of the top screen: index, label, x y w h in screen pixels",
            (_, con) => ControlUiTree(con));

        Add("ui-click", "ui-click <label|index> | ui-click [--id <widgetId>] [--label <text>] [--screen <name>] [--index <n>]",
            "press a widget by label (exact, else unique part), ui-tree index, or selectors that must all match (ambiguity is an "
            + "error)", ControlUiClickCommand);

        Add("ui-set", "ui-set <label|index> <0..1> | ui-set [--id ..] [--label ..] [--screen ..] [--index ..] <0..1>",
            "move a settings slider to a position on its track (0 left, 1 right)", (args, con) => ControlUiSet(args, con));

        Add("clipboard", "clipboard", "print the game's clipboard text as a JSON string (null when it holds none)",
            (_, con) => { string json = JsonSerializer.Serialize(host.Player.ClipboardRead?.Invoke()); PrintData(con, json); });

        Add("ui-open", "ui-open settings[:<page>]", "open the settings window, on a page (" + string.Join(", ", SettingsMenu.PageNames) + ")",
            ControlUiOpen, (i, typed) => i == 0 ? SettingsMenu.PageNames.Select(p => "settings:"
            + p).Where(p => p.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) : []);
    }

    /// <summary>
    /// The <c>screenshot</c> command (usage and help at its registration in <see cref="RegisterControlCommands"/>).
    /// </summary>
    void ControlScreenshot(IReadOnlyList<string> args, DevConsole con)
    {
        if (args.Count != 1)
        {
            con.Error("usage: screenshot <png path>");
            return;
        }

        string path = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ShotPath = path;
        host.ScreenshotRequested = true;
        ShotWaitId = CurrentControlId;
        con.Print($"queued {path}");
    }

    /// <summary>
    /// The <c>photo</c> command (usage and help at its registration in <see cref="RegisterControlCommands"/>).
    /// </summary>
    void ControlPhoto(IReadOnlyList<string> args, DevConsole con)
    {
        if (host.Photo is { } photo)
        {
            photo.Command(args, con);
        }
        else
        {
            con.Error("photo mode is not available here");
        }
    }

    /// <summary>
    /// The <c>mouse</c> command (usage and help at its registration in <see cref="RegisterControlCommands"/>).
    /// </summary>
    void ControlMouse(IReadOnlyList<string> args, DevConsole con)
    {
        if (!host.WorldOptions.Headless)
        {
            con.Error("mouse: only with --headless (use the real pointer otherwise)");
            return;
        }

        if (args.Count != 2 || !float.TryParse(args[0], System.Globalization.CultureInfo.InvariantCulture, out float x)
            || !float.TryParse(args[1], System.Globalization.CultureInfo.InvariantCulture, out float y))
        {
            con.Error("usage: mouse <x> <y>");
            return;
        }

        host.DevConsoleHost.HeadlessMouse = new System.Numerics.Vector2(x, y);
        con.Print($"pointer {x:0} {y:0}");
    }

    /// <summary>
    /// The <c>click</c> command (usage and help at its registration in <see cref="RegisterControlCommands"/>).
    /// </summary>
    void ControlClick(IReadOnlyList<string> args, DevConsole con)
    {
        bool right = false, dbl = false;
        var pos = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "--double") dbl = true;
            else if (args[i] == "--button" && i + 1 < args.Count && args[i + 1] is "left" or "right")
                right = args[++i] == "right";
            else pos.Add(args[i]);
        }
        if (pos.Count != 2 || !float.TryParse(pos[0], System.Globalization.CultureInfo.InvariantCulture, out float x)
            || !float.TryParse(pos[1], System.Globalization.CultureInfo.InvariantCulture, out float y))
        {
            con.Error("usage: click <x> <y> [--button left|right] [--double]");
            return;
        }

        var at = new System.Numerics.Vector2(x, y);
        host.DevConsoleHost.PointerSteps.Enqueue(('m', right, at));
        host.DevConsoleHost.PointerSteps.Enqueue(('i', right, at));
        for (int n = 0; n < (dbl ? 2 : 1); n++)
        {
            host.DevConsoleHost.PointerSteps.Enqueue(('d', right, at));
            host.DevConsoleHost.PointerSteps.Enqueue(('u', right, at));
        }

        con.Print($"queued {(right ? "right" : "left")}{(dbl ? " double" : "")} click at {x:0} {y:0}");
    }

    /// <summary>
    /// The <c>events</c> command (usage and help at its registration in <see cref="RegisterControlCommands"/>).
    /// </summary>
    void ControlEventsCommand(IReadOnlyList<string> args, DevConsole con)
    {
        if (args.Count != 1 || args[0] is not ("on" or "off"))
        {
            con.Error("usage: events on|off");
            return;
        }

        ControlEvents = args[0] == "on";
        con.Print($"events {args[0]}");
    }

    /// <summary>
    /// The <c>wait-for</c> command (usage and help at its registration in <see cref="RegisterControlCommands"/>).
    /// </summary>
    void ControlWaitFor(IReadOnlyList<string> args, DevConsole con)
    {
        double seconds = 10;
        var rest = args.ToList();
        if (rest.Count > 1 && rest[^1].StartsWith("timeout=", StringComparison.OrdinalIgnoreCase))
        {
            if (!double.TryParse(rest[^1][8..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                out seconds) || seconds < 0)
            {
                con.Error("wait-for: bad timeout");
                return;
            }

            rest.RemoveAt(rest.Count - 1);
        }
        string cond = string.Join(' ', rest);
        int eq = cond.IndexOf('=');
        string key = (eq < 0 ? cond : cond[..eq]).Trim().ToLowerInvariant(), value = eq < 0 ? "true" : cond[(eq + 1)..].Trim();
        if (key is not ("screen" or "settings-page" or "loaded" or "widget" or "no-widget" or "mode" or "camera-settled" or "selected" or "pos-near")
            || (value.Length == 0))
        {
            con.Error("usage: wait-for <screen|settings-page|loaded|widget|no-widget|mode|camera-settled|selected|pos-near>=<value> [timeout=<s>]  "
                + "(pos-near=<x>,<z>,<r>[,<id>])");
            return;
        }

        var wt = new ControlWait(CurrentControlId ?? "0", key, value, Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency),
            Stopwatch.GetTimestamp(), seconds);
        if (WaitHolds(key, value, ControlSnapshot()))
        {
            ControlData = JsonSerializer.Serialize(new Dictionary<string, object> { ["key"] = key, ["value"] = value,
                ["waitedMs"] = 0, ["clock"] = "real" });
            con.Print($"{key}={value} already holds");
            return;
        }

        _controlWaits.Add(wt);
        ControlReplyDeferred = true;
        con.Print($"waiting for {key}={value}");
    }

    /// <summary>
    /// The <c>ui-click</c> command (usage and help at its registration in <see cref="RegisterControlCommands"/>).
    /// </summary>
    void ControlUiClickCommand(IReadOnlyList<string> args, DevConsole con)
    {
        if (args.Count == 0)
        {
            con.Error("usage: ui-click <label|index> | ui-click --id <id> --label <text> --screen <name> --index <n>");
            return;
        }

        ControlUiClick(args, con);
    }

    /// <summary>
    /// The <c>ui-open</c> command (usage and help at its registration in <see cref="RegisterControlCommands"/>).
    /// </summary>
    void ControlUiOpen(IReadOnlyList<string> args, DevConsole con)
    {
        if (args.Count != 1)
        {
            con.Error("usage: ui-open settings[:<page>]");
            return;
        }

        string[] parts = args[0].Split(':', 2);
        if (!parts[0].Equals("settings", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length == 1 && host.Player.OpenFaithfulScreen(parts[0])) con.Print($"{parts[0]} open");
            else
                con.Error($"ui-open: unknown window '{parts[0]}' (settings, or a screen id such as menu, load, save)");
            return;
        }
        if (SettingsPanel is not { } panel)
        {
            con.Error("ui-open: this mode has no settings window");
            return;
        }

        if (parts.Length == 2 && !panel.TryOpenPage(parts[1]))
        {
            con.Error($"ui-open: unknown settings page '{parts[1]}' (known: {string.Join(", ", SettingsMenu.PageNames)})");
            return;
        }

        panel.Visible = true;
        con.Print($"settings open on {panel.PageName}");
    }

    /// <summary>
    /// One clickable widget for ui-tree / ui-click: its label, screen-pixel bounds, widget id (screen/name), the screen it is on and how to press it.
    /// </summary>
    sealed record ControlWidget(string Label, float X, float Y, float W, float H, string Id, string Screen, Action Click, Slider? Slider = null);

    /// <summary>
    /// The clickable widgets of the screen on top: the settings window while it is open, else the faithful screens (title, pause menu, load/save,
    /// windows) and the world HUD.
    /// </summary>
    List<ControlWidget> ControlWidgets()
    {
        var list = new List<ControlWidget>();
        if (SettingsPanel is { Visible: true } panel)
        {
            foreach (var (label, b, activate) in panel.Clickables)
                list.Add(new(label, b.X, b.Y, b.Width, b.Height, "settings:" + panel.PageName + "/" + label, "settings", activate));
        }
        if (SettingsPanel is { Visible: true } open)
        {
            foreach (var (slider, b) in open.SliderHits)
            {
                list.Add(new(slider.Label, b.X, b.Y, b.Width, b.Height, "settings:" + open.PageName + "/" + slider.Label,
                    "settings", () => { }, slider));
            }
        }
        else
        {
            foreach (var (screen, c) in host.Player.UiClickables())
            {
                string label = (c.Caption.Length > 0 ? c.Caption
                    : c.Properties.GetValueOrDefault(UiTooltips.Text, "").Length > 0 ? c.Properties[UiTooltips.Text]
                    : c.Name).ReplaceLineEndings(" ").Trim();
                if (label.Length > 48) label = label[..48].TrimEnd() + "...";   // a long tooltip stays a usable label
                var centre = new System.Numerics.Vector2(c.Bounds.X + c.Bounds.Width / 2, c.Bounds.Y + c.Bounds.Height / 2);
                list.Add(new(label, c.Bounds.X, c.Bounds.Y, c.Bounds.Width, c.Bounds.Height, $"{screen}/{c.Name}", screen,
                    () => host.Player.UiClickAt(centre)));
            }
        }
        return list;
    }

    /// <summary>
    /// Writes widget <paramref name="x"/> with its index: label, rectangle, id, screen and, for a slider, its fraction and display text.
    /// </summary>
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

    /// <summary>
    /// The widgets as a JSON array.
    /// </summary>
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

    /// <summary>
    /// <c>ui-tree</c>: prints every widget with its index, label, rectangle and id; the data holds them as JSON.
    /// </summary>
    void ControlUiTree(DevConsole con)
    {
        var widgets = ControlWidgets();
        ControlData = WidgetsJson(widgets.Select((x, i) => (i, x)));
        for (int i = 0; i < widgets.Count; i++)
        {
            var w = widgets[i];
            con.Print(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{i} {w.Label} {MathF.Round(w.X)} {MathF.Round(w.Y)} {MathF.Round(w.W)} {MathF.Round(w.H)} [{w.Id}]"
                + $"{(w.Slider is { } sl ? $" [slider] {sl.Fraction:0.###} ({sl.Display})" : "")}"));
        }
        if (widgets.Count == 0) con.Print("(no clickable widgets)");
    }

    /// <summary>
    /// Splits <c>--id/--label/--screen/--index &lt;value...&gt;</c> selectors from the positional rest (a value runs to the next --flag); null after
    /// an error message.
    /// </summary>
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
                while (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    value.Add(args[++i]);
                if (value.Count == 0)
                {
                    con.Error($"{cmd}: {a} needs a value");
                    return null;
                }

                flags[a] = string.Join(' ', value);
            }
            else rest.Add(a);
        }
        return (flags, string.Join(' ', rest));
    }

    /// <summary>
    /// The widget the selectors name: flags AND together, then the positional label (exact, else unique part) or ui-tree index narrows further. Null
    /// after an error reply that lists candidates as JSON.
    /// </summary>
    ControlWidget? ControlFind(string cmd, IReadOnlyList<string> args, DevConsole con)
    {
        if (ControlSelector(cmd, args, con) is not var (flags, target)) return null;
        var widgets = ControlWidgets();
        var pool = widgets.Select((x, i) => (Index: i, Widget: x)).ToList();
        if (flags.TryGetValue("--id", out string? id))
            pool = pool.Where(p => p.Widget.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (flags.TryGetValue("--label", out string? label))
            pool = pool.Where(p => p.Widget.Label.Equals(label, StringComparison.OrdinalIgnoreCase)).ToList();
        if (flags.TryGetValue("--screen", out string? screen))
            pool = pool.Where(p => p.Widget.Screen.Equals(screen, StringComparison.OrdinalIgnoreCase)).ToList();
        if (flags.TryGetValue("--index", out string? idx))
        {
            if (!int.TryParse(idx, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n))
            {
                con.Error($"{cmd}: --index needs a number");
                return null;
            }

            pool = pool.Where(p => p.Index == n).ToList();
        }
        if (target.Length > 0)
        {
            var hit = pool.Where(p => p.Widget.Label.Equals(target, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hit.Count == 0 && int.TryParse(target, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int index))
                hit = pool.Where(p => p.Index == index).ToList();
            if (hit.Count == 0)
                hit = pool.Where(p => p.Widget.Label.Contains(target, StringComparison.OrdinalIgnoreCase)).ToList();
            pool = hit;
        }
        string wanted = string.Join(' ', args);
        if (pool.Count == 0)
        {
            ControlData = WidgetsJson(widgets.Select((x, i) => (i, x)));   // nothing matched: everything there is
            con.Error($"{cmd}: nothing matches '{wanted}' (see ui-tree)");
            return null;
        }
        if (pool.Count > 1)
        {
            ControlData = WidgetsJson(pool.Select(p => (p.Index, p.Widget)));
            con.Error($"{cmd}: '{wanted}' is ambiguous: {string.Join(", ", pool.Select(p => $"{p.Index} {p.Widget.Label} [{p.Widget.Id}]"))}");
            return null;
        }
        return pool[0].Widget;
    }

    /// <summary>
    /// <c>ui-click</c>: clicks the widget the arguments find (not a slider).
    /// </summary>
    void ControlUiClick(IReadOnlyList<string> args, DevConsole con)
    {
        if (ControlFind("ui-click", args, con) is not { } w) return;
        if (w.Slider is not null)
        {
            con.Error($"ui-click: '{w.Label}' is a slider, use ui-set");
            return;
        }

        w.Click();
        con.Print($"clicked {w.Label}");
    }

    /// <summary>
    /// <c>ui-set</c>: sets the slider the arguments find to a fraction 0..1.
    /// </summary>
    void ControlUiSet(IReadOnlyList<string> args, DevConsole con)
    {
        if (args.Count < 2 || !float.TryParse(args[^1], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float t))
        {
            con.Error("usage: ui-set <label|index> <0..1> (or selectors, then the number)");
            return;
        }

        if (ControlFind("ui-set", args.Take(args.Count - 1).ToList(), con) is not { } w) return;
        if (w.Slider is not { } slider)
        {
            con.Error($"ui-set: '{w.Label}' is not a slider");
            return;
        }

        slider.SetFraction(t);
        con.Print($"{w.Label} = {slider.Display}");
    }
}