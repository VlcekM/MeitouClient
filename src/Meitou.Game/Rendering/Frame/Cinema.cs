using System.Numerics;
using System.Text.Json;

namespace Meitou.Rendering;

/// <summary>The keys the cinema answers to (named as Silk.NET.Input's, so a host maps one with <c>Enum.TryParse(key.ToString(), out CinemaKey k)</c>).</summary>
enum CinemaKey
{
    Keypad0, Keypad1, Keypad2, Keypad3, Keypad4, Keypad5, Keypad6, Keypad7, Keypad8, Keypad9,
    KeypadEnter, KeypadAdd, KeypadSubtract, KeypadMultiply, KeypadDivide, KeypadDecimal,
    F6, Space, Delete, PageUp, PageDown, Home,
}

/// <summary>
/// The viewer's and the game's cinematic camera (docs/viewer.md "Cinematic camera"), for trailers. Keys are shots on a timeline (<see cref="CameraKey"/>):
/// Ctrl+numpad 1-9 save the camera (with its lens and the time of day) as key 1-9, numpad 1-9 cut to one, numpad Enter flies through them
/// (<see cref="CameraPath"/>), Shift+numpad Enter records the flight to PNG frames at a fixed frame rate. F6 opens the timeline editor along
/// the bottom of the window (<see cref="Draw"/>): drag keys to retime them, drag the ruler to scrub, buttons to add, replace, delete and ease keys,
/// to record a camera path (a key every <see cref="PathStep"/> seconds while you fly) and to delete every key.
/// The keys are kept in <paramref name="file"/> across starts (<see cref="ViewerFile"/>, <see cref="GameFile"/>). Without
/// <paramref name="setHour"/> (the game, whose hour is the simulation clock's) "time follows" is unavailable. PageUp/PageDown zoom the lens wherever
/// <see cref="LensZoomLive"/>: elsewhere in the game they are its floor keys. Home puts the lens back wherever <see cref="LensResetLive"/>.
/// </summary>
sealed class Cinema(Func<WorldCamera> camera, Func<float> getHour, Action<float>? setHour, Func<bool> fullScreenOn, Action toggleFullScreen, string file, bool lensKeysAlways = true)
{
    /// <summary>The game's other times for the lens keys (and for Home besides the open editor): its free camera and a camera the cinema holds.</summary>
    public Func<bool>? LensKeys { get; set; }

    /// <summary>Whether Home puts the lens back (the viewer always; the game in the free camera, on a camera the cinema holds, and with its editor open).</summary>
    public bool LensResetLive => lensKeysAlways || EditorVisible || LensKeys?.Invoke() == true;

    /// <summary>Whether PageUp/PageDown zoom the lens (the viewer always; the game in the free camera and on a camera the cinema holds).</summary>
    public bool LensZoomLive => lensKeysAlways || LensKeys?.Invoke() == true;

    /// <summary>The viewer's keys file.</summary>
    public static string ViewerFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meitou", "viewer-shots.json");
    /// <summary>The game's keys file (its own: a world position means the same in both, so the viewer's file can be copied over it).</summary>
    public static string GameFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meitou", "game-shots.json");

    const int MaxKeys = 999, RecordFps = 60;
    /// <summary>Seconds between the keys a recorded camera path drops.</summary>
    const float PathStep = 1;
    const float DefaultFov = 50 * MathF.PI / 180, MinFov = 10 * MathF.PI / 180, MaxFov = 100 * MathF.PI / 180, MinGap = 0.05f;
    /// <summary>The letterbox's picture shape (2.39:1, anamorphic widescreen).</summary>
    const float Scope = 2.39f;
    readonly string File = file;

    sealed class Entry(int label, CameraShot shot, float time, bool ease)
    {
        public int Label = label;
        public CameraShot Shot = shot;
        public float Time = time;
        public bool Ease = ease;
    }

    readonly List<Entry> keys = [];
    /// <summary>The time a new key is placed after the last one, and a loop takes from the last key back to the first.</summary>
    float spacing = 6;
    bool loop, followTime;
    /// <summary>The playhead, seconds on the timeline.</summary>
    double time;
    /// <summary>The playhead moved without playing (a scrub, a click): the camera goes there on the next frame.</summary>
    bool preview;
    string? takeFolder;
    int takeFrame;
    bool endTake;
    /// <summary>Recording a camera path: the playhead time of the next key it drops; null when off.</summary>
    double? pathNext;
    /// <summary>"Delete all keys" was clicked once: a second click before this tick count deletes them.</summary>
    long deleteAllUntil;

    /// <summary>Called whenever the cinema puts the camera somewhere (a cut, a scrub, each frame of a flight): a host with its own camera control lets the cinema hold it.</summary>
    public Action? Moved { get; set; }

    public bool Playing { get; private set; }
    /// <summary>Clean picture: no panels, letterbox bars (numpad *).</summary>
    public bool Clean { get; private set; }
    public bool Recording => takeFolder is not null;
    /// <summary>The timeline editor (F6).</summary>
    public bool EditorVisible { get; private set; }

    List<Entry> Sorted => [.. keys.OrderBy(k => k.Time)];
    List<CameraKey> Flight => [.. Sorted.Select(k => new CameraKey(k.Shot, k.Time, k.Ease))];
    float? LoopBack => loop && !Recording ? spacing : null;
    float Start => keys.Count == 0 ? 0 : keys.Min(k => k.Time);
    float End => keys.Count == 0 ? 0 : keys.Max(k => k.Time) + (LoopBack ?? 0);
    Entry? Find(int label) => keys.FirstOrDefault(k => k.Label == label);

    /// <summary>Handles a cinematic key; false when <paramref name="key"/> is not one.</summary>
    public bool OnKey(CinemaKey key, bool ctrl, bool shift)
    {
        var cam = camera();
        if (key is >= CinemaKey.Keypad1 and <= CinemaKey.Keypad9)
        {
            int label = key - CinemaKey.Keypad1 + 1;
            if (ctrl) SaveKey(label);
            else if (Find(label) is { } k) { Stop(); GoTo(k); Say($"cut to key {label}"); }
            else Say($"key {label} is empty (Ctrl+numpad {label} saves the camera there)");
            return true;
        }
        switch (key)
        {
            case CinemaKey.F6: EditorVisible = !EditorVisible; Say(EditorVisible ? "timeline editor open (F6 closes)" : "timeline editor closed"); return true;
            case CinemaKey.Space when EditorVisible: TogglePlay(fromPlayhead: true); return true;
            case CinemaKey.Delete when EditorVisible: DeleteSelected(); return true;
            case CinemaKey.KeypadEnter:
                if (shift && !Playing) StartRecording();
                else TogglePlay(fromPlayhead: false);
                return true;
            case CinemaKey.Keypad0: loop = !loop; Say(loop ? $"loop on ({spacing:0.#} s back to the first key)" : "loop off"); return true;
            case CinemaKey.KeypadAdd: Scale(1.25f); return true;
            case CinemaKey.KeypadSubtract: Scale(1 / 1.25f); return true;
            case CinemaKey.KeypadMultiply: Clean = !Clean; Say(Clean ? "clean picture: panels hidden, 2.39:1 letterbox" : "panels back, no letterbox"); return true;
            case CinemaKey.KeypadDivide when setHour is null: Say("the time of day is the game clock's here: it does not follow the keys"); return true;
            case CinemaKey.KeypadDivide: followTime = !followTime; Say(followTime ? "the time of day follows the keys" : "the time of day stays put"); return true;
            case CinemaKey.KeypadDecimal when ctrl: DeleteAll(); return true;
            case CinemaKey.KeypadDecimal:
                Say(Summary());
                foreach (var k in Sorted) Console.WriteLine($"  {k.Label,2}  {k.Time,6:0.00} s{(k.Ease ? " ease" : "     ")}  {Describe(k.Shot)}");
                return true;
            // PageUp/PageDown zoom the lens in the viewer and in the game's free and held cameras: in its strategy camera they are floor_up / floor_down.
            case CinemaKey.PageUp when LensZoomLive: cam.FieldOfView = MathF.Max(cam.FieldOfView - 2.5f * MathF.PI / 180, MinFov); Say($"field of view {Degrees(cam.FieldOfView):0.#} deg"); return true;
            case CinemaKey.PageDown when LensZoomLive: cam.FieldOfView = MathF.Min(cam.FieldOfView + 2.5f * MathF.PI / 180, MaxFov); Say($"field of view {Degrees(cam.FieldOfView):0.#} deg"); return true;
            case CinemaKey.Home when LensResetLive: cam.FieldOfView = DefaultFov; Say($"field of view {Degrees(DefaultFov):0} deg"); return true;
        }
        return false;
    }

    /// <summary>Saves the camera as key <paramref name="label"/>: a new key goes <see cref="spacing"/> after the last (or at <paramref name="at"/>); an existing one keeps its time.</summary>
    void SaveKey(int label, float? at = null)
    {
        var shot = CameraShot.Of(camera(), getHour());
        if (Find(label) is { } k) { k.Shot = shot; Say($"key {label} set to the camera at {k.Time:0.00} s: {Describe(shot)}"); }
        else
        {
            if (keys.Count >= MaxKeys) { Say($"{MaxKeys} keys at most"); return; }
            float t = at ?? (keys.Count == 0 ? 0 : keys.Max(e => e.Time) + spacing);
            while (keys.Any(e => MathF.Abs(e.Time - t) < MinGap)) t += MinGap;
            keys.Add(k = new Entry(label, shot, t, false));
            Say($"key {label} saved at {t:0.00} s: {Describe(shot)}; {Summary()}");
        }
        selected = k;
        Save();
    }

    void GoTo(Entry k)
    {
        k.Shot.Apply(camera());
        if (followTime) setHour?.Invoke(k.Shot.Hour);
        Moved?.Invoke();
        (time, selected) = (k.Time, k);
    }

    void TogglePlay(bool fromPlayhead)
    {
        if (pathNext is not null) StopPath();
        if (Playing) { Stop(); Say($"stopped at {time:0.00} s"); return; }
        if (keys.Count < 2) { Say("save two keys or more first (Ctrl+numpad 1-9, or Add key in the F6 editor)"); return; }
        if (!fromPlayhead || time < Start || time >= End - 1e-3) time = Start;
        Playing = true;
        Say($"playing from {time:0.00} s, {Summary()} (numpad Enter{(EditorVisible ? " or Space" : "")} stops)");
    }

    /// <summary>Flies the keys on a loop from the first (<c>--play-shots</c>).</summary>
    public void PlayLoop()
    {
        loop = true;
        if (!Playing) TogglePlay(fromPlayhead: false);
    }

    void StartRecording()
    {
        if (pathNext is not null) StopPath();
        if (keys.Count < 2) { Say("save two keys or more first"); return; }
        takeFolder = Directory.CreateDirectory(Path.Combine(@"C:\Temp", $"meitou-take-{DateTime.Now:yyyyMMdd-HHmmss}")).FullName;
        (takeFrame, time, Playing) = (0, Start, true);
        Say($"recording {End - Start:0.#} s at {RecordFps} fps into {takeFolder} (numpad Enter stops)");
    }

    /// <summary>
    /// Starts or stops recording a camera path: the camera is saved as a new key now (one spacing after the last key, or at 0) and every
    /// <see cref="PathStep"/> seconds after while you fly it, and once more where the recording stops.
    /// </summary>
    void TogglePath()
    {
        if (pathNext is not null) { StopPath(); return; }
        Stop();
        time = keys.Count == 0 ? 0 : keys.Max(k => k.Time) + spacing;
        if (!DropPathKey()) return;
        pathNext = time + PathStep;
        Say($"recording the camera path: a key every {PathStep:0.#} s while you fly (Record path again stops)");
    }

    void StopPath()
    {
        pathNext = null;
        if (keys.Count > 0 && time > keys.Max(k => k.Time) + MinGap) DropPathKey();
        Save();
        Say($"camera path recorded; {Summary()}");
    }

    /// <summary>Saves the camera as a new key at the playhead; false (and the recording stopped) when the keys are full.</summary>
    bool DropPathKey()
    {
        if (keys.Count >= MaxKeys) { pathNext = null; Save(); Say($"{MaxKeys} keys at most: the camera path stops here"); return false; }
        var k = new Entry(Enumerable.Range(1, MaxKeys).First(l => Find(l) is null), CameraShot.Of(camera(), getHour()), (float)time, false);
        keys.Add(k);
        selected = k;
        return true;
    }

    /// <summary>Deletes every key (Ctrl+numpad ., or "Delete all keys" clicked twice).</summary>
    void DeleteAll()
    {
        Stop();
        pathNext = null;
        keys.Clear();
        (selected, time) = (null, 0);
        Save();
        Say("all keys deleted");
    }

    void Scale(float factor)
    {
        float start = Start;
        foreach (var k in keys) k.Time = start + (k.Time - start) * factor;
        spacing = Math.Clamp(spacing * factor, 0.5f, 120);
        time = start + (time - start) * factor;
        Save();
        Say($"timing x{factor:0.##}: {Summary()}");
    }

    void DeleteSelected()
    {
        if (selected is null) { Say("no key selected (click one on the timeline)"); return; }
        keys.Remove(selected);
        Say($"key {selected.Label} deleted; {Summary()}");
        selected = null;
        Save();
    }

    /// <summary>The frame's time step: the real one, or exactly one frame of the recording's rate while recording.</summary>
    public double FrameTime(double real) => Recording ? 1.0 / RecordFps : Math.Min(real, 0.25);

    /// <summary>Moves the camera (and the hour) along the flight, or to a scrubbed playhead; call once a frame before drawing.</summary>
    public void Advance(double dt)
    {
        if (pathNext is { } next)
        {
            time += dt;
            if (time >= next) { time = next; if (DropPathKey()) pathNext = next + PathStep; }
            return;
        }
        if (!Playing && !preview) return;
        preview = false;
        var flight = Flight;
        if (flight.Count == 0) { Stop(); return; }
        var shot = CameraPath.Sample(flight, (float)time, LoopBack);
        shot.Apply(camera());
        if (followTime) setHour?.Invoke(shot.Hour);
        Moved?.Invoke();
        if (!Playing) return;
        time += dt;
        if (LoopBack is not null) { if (time >= End) time = Start + (time - End); return; }
        // An open flight ends on its last key (drawn this frame); a recording of a loop is one lap.
        if (time > End + dt * 0.5)
        {
            if (Recording) Say($"recorded {takeFrame + 1} frames into {takeFolder}; to a video: ffmpeg -framerate {RecordFps} -i \"{takeFolder}\\%05d.png\" -c:v libx264 -crf 16 -pix_fmt yuv420p take.mp4");
            time = End;
            Stop(keepTake: true);
        }
    }

    /// <summary>The file this frame is saved to while recording (then the frame is drawn without panels), else null.</summary>
    public string? TakeFrame() => takeFolder is null ? null : Path.Combine(takeFolder, $"{takeFrame++:00000}.png");

    void Stop(bool keepTake = false)
    {
        Playing = false;
        if (keepTake) endTake = true;
        else takeFolder = null;
    }

    /// <summary>Call after the frame is saved: a recording that reached its end lets go of its folder.</summary>
    public void FrameDone()
    {
        if (endTake) (takeFolder, endTake) = (null, false);
    }

    // ---- The timeline editor (F6) ----

    static readonly Vector4 Track = new(0.3f, 0.3f, 0.32f, 1), Fill = new(0.85f, 0.65f, 0.35f, 1), Dim = new(0.7f, 0.7f, 0.68f, 1),
        EaseColour = new(0.45f, 0.7f, 0.95f, 1), PlayheadColour = new(0.95f, 0.3f, 0.25f, 1), Background = new(0.05f, 0.05f, 0.06f, 0.92f);
    const float Pad = 10, RulerHeight = 46, MarkerHalf = 6;

    Entry? selected;
    enum Drag { None, Key, Playhead }
    Drag drag;
    float viewEnd = 10;
    DebugOverlay? ui;
    float x0, x1, y0, y1, buttonsTop, buttonsBottom, rulerTop, rulerX0, rulerX1;
    readonly List<(string Label, float X0, float X1, int Row, Action Run)> buttons = [];
    float RowTop(int row) => buttonsTop + row * (ui!.LineHeight + 8 + 6);

    float TimeAt(float x) => Math.Max(0, (x - rulerX0) / (rulerX1 - rulerX0) * viewEnd);
    float XAt(float t) => rulerX0 + t / viewEnd * (rulerX1 - rulerX0);

    void Layout(DebugOverlay o, int width, int height)
    {
        ui = o;
        float line = o.LineHeight;
        (x0, x1) = (16, width - 16);
        y1 = height - 16;
        (rulerX0, rulerX1) = (x0 + Pad + 8, x1 - Pad - 8);
        // While a key is dragged the scale stays put; otherwise it fits the flight with room to add keys after it.
        if (drag == Drag.None) viewEnd = MathF.Max(10, MathF.Ceiling((End + spacing) / 5) * 5);

        // The buttons, in as many rows as the window's width needs.
        buttons.Clear();
        float bx = x0 + Pad;
        int row = 0;
        void Button(string label, Action run)
        {
            float w = label.Length * o.CharWidth + 16;
            if (bx + w > x1 - Pad && bx > x0 + Pad) (bx, row) = (x0 + Pad, row + 1);
            buttons.Add((label, bx, bx + w, row, run));
            bx += w + 6;
        }
        Button(Playing ? "Stop" : "Play", () => TogglePlay(fromPlayhead: true));
        Button(loop ? "Loop: on" : "Loop: off", () => OnKey(CinemaKey.Keypad0, false, false));
        Button("Add key at playhead", () => SaveKey(Enumerable.Range(1, MaxKeys).First(l => Find(l) is null), (float)time));
        Button("Set key to camera", () => { if (selected is { } s) SaveKey(s.Label); else Say("no key selected"); });
        Button("Go to key", () => { if (selected is { } s) { Stop(); GoTo(s); } else Say("no key selected"); });
        Button(selected is { Ease: true } ? "Ease: on" : "Ease: off", () => { if (selected is { } s) { s.Ease = !s.Ease; Save(); preview = true; } else Say("no key selected"); });
        Button("Delete key", DeleteSelected);
        Button(setHour is null ? "Time follows: n/a" : followTime ? "Time follows: on" : "Time follows: off", () => OnKey(CinemaKey.KeypadDivide, false, false));
        Button("Slower", () => Scale(1.25f));
        Button("Faster", () => Scale(1 / 1.25f));
        Button("Record", () => { if (!Playing) StartRecording(); });
        Button(pathNext is null ? "Record path: off" : "Record path: on", TogglePath);
        Button(Environment.TickCount64 < deleteAllUntil ? "Delete all keys: click again" : "Delete all keys", () =>
        {
            if (Environment.TickCount64 < deleteAllUntil) { deleteAllUntil = 0; DeleteAll(); }
            else if (keys.Count == 0) Say("no keys to delete");
            else { deleteAllUntil = Environment.TickCount64 + 3000; Say($"click Delete all keys again within 3 s to delete all {keys.Count}"); }
        });
        Button(Clean ? "Letterbox: on" : "Letterbox: off", () => Clean = !Clean);
        Button(fullScreenOn() ? "Full screen: on" : "Full screen: off", toggleFullScreen);

        // The button rows, two text lines, the key numbers' line, the ruler.
        float rows = (row + 1) * (line + 8) + row * 6;
        y0 = y1 - (Pad + rows + 6 + line + 6 + line + line + RulerHeight + Pad);
        buttonsTop = y0 + Pad;
        buttonsBottom = buttonsTop + rows;
        rulerTop = buttonsBottom + 6 + line + 6 + line + line;
    }

    /// <summary>Whether a point (window pixels) is on the editor.</summary>
    public bool Contains(Vector2 p) => EditorVisible && ui is not null && p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1;

    /// <summary>A click on the editor: a button, a key (select, then drag to retime) or the ruler (scrub); true when the editor took it.</summary>
    public bool MouseDown(Vector2 p)
    {
        if (!Contains(p)) return false;
        float line = ui!.LineHeight;
        if (p.Y >= buttonsTop && p.Y <= buttonsBottom)
        {
            foreach (var b in buttons)
                if (p.X >= b.X0 && p.X <= b.X1 && p.Y >= RowTop(b.Row) && p.Y <= RowTop(b.Row) + line + 8) { b.Run(); break; }
            return true;
        }
        if (p.Y >= rulerTop - line && p.Y <= rulerTop + RulerHeight)
        {
            if (pathNext is not null) StopPath();
            // The nearest key within reach of the pointer, else the playhead.
            var hit = keys.Select(k => (k, d: MathF.Abs(XAt(k.Time) - p.X))).Where(h => h.d <= MarkerHalf + 3).OrderBy(h => h.d).Select(h => h.k).FirstOrDefault();
            Stop();
            if (hit is not null) { (selected, drag) = (hit, Drag.Key); return true; }
            drag = Drag.Playhead;
            MouseMove(p);
        }
        return true;
    }

    /// <summary>Moves a dragged key (past other keys too, which reorders them; never onto one) or the playhead; true while dragging.</summary>
    public bool MouseMove(Vector2 p)
    {
        if (drag == Drag.None || ui is null) return false;
        float t = TimeAt(p.X);
        if (drag == Drag.Key && selected is { } k)
        {
            // To 0.05 s, not before 0; over another key it steps on in the direction it moves, so a key can be pulled in front of or behind others.
            float to = MathF.Max(0, MathF.Round(t * 20) / 20), step = to >= k.Time ? MinGap : -MinGap;
            while (keys.Any(e => e != k && MathF.Abs(e.Time - to) < MinGap - 1e-4f)) to = to + step < 0 ? (step = MinGap) : to + step;
            k.Time = to;
            time = k.Time;
        }
        else time = t;
        preview = true;
        return true;
    }

    /// <summary>Ends a drag; true when one was in progress.</summary>
    public bool MouseUp()
    {
        if (drag == Drag.None) return false;
        if (drag == Drag.Key) Save();
        drag = Drag.None;
        return true;
    }

    /// <summary>
    /// The letterbox bars (clean picture), then, unless the frame is being saved (<paramref name="saving"/>: a screenshot or a recorded
    /// frame), the timeline editor (F6, also over the letterbox) or, while there are keys and no letterbox, a status line. Call after the other
    /// panels, so the editor is on top. The game disables <paramref name="showStatus"/> to hide the idle/playback hint bar.
    /// </summary>
    public void Draw(DebugOverlay o, int width, int height, bool saving, bool showStatus = true)
    {
        if (Clean)
        {
            float bar = MathF.Max(0, (height - width / Scope) / 2);
            if (bar > 0)
            {
                o.Rect(0, 0, width, bar, new Vector4(0, 0, 0, 1));
                o.Rect(0, height - bar, width, height, new Vector4(0, 0, 0, 1));
            }
        }
        if (saving) { ui = null; return; }
        if (EditorVisible) { DrawEditor(o, width, height); return; }
        ui = null;
        if (!showStatus || Clean || (keys.Count == 0 && !Playing)) return;
        string line = $"keys {Summary()}   {(pathNext is not null ? $"recording the camera path {time:0.0} s" : Playing ? $"{(Recording ? "recording" : "playing")} {time:0.0} / {End:0.0} s" : "numpad Enter plays, F6 edits")}";
        float y = height - o.LineHeight - 12;
        o.Rect(8, y - 4, 16 + line.Length * o.CharWidth, y + o.LineHeight + 2, new Vector4(0.05f, 0.05f, 0.06f, 0.8f));
        o.Text(line, 12, y, DebugOverlay.TextColour);
    }

    void DrawEditor(DebugOverlay o, int width, int height)
    {
        Layout(o, width, height);
        float line = o.LineHeight;
        o.Rect(x0, y0, x1, y1, Background);
        foreach (var b in buttons)
        {
            float top = RowTop(b.Row);
            o.Rect(b.X0, top, b.X1, top + line + 8, Track);
            o.Text(b.Label, b.X0 + 8, top + 4, DebugOverlay.TextColour);
        }
        float infoTop = buttonsBottom + 6;
        string sel = selected is { } s
            ? $"key {s.Label} at {s.Time:0.00} s{(s.Ease ? ", eases to a stop" : "")}: {Describe(s.Shot)}"
            : "no key selected: click a key on the timeline";
        o.Text(sel, x0 + Pad, infoTop, DebugOverlay.TextColour);
        string help = $"playhead {time:0.00} s, flight {Start:0.00} to {End:0.00} s{(loop ? " (loop)" : "")}.  Drag a key to retime it, drag the ruler to scrub; " +
            "Space play, Del delete, Ctrl+Num1-9 save, Num1-9 go, F6 close";
        o.Text(help, x0 + Pad, infoTop + line + 6, Dim);

        // The ruler: seconds, a tick every step and a number every fifth.
        o.Rect(rulerX0, rulerTop + RulerHeight * 0.45f, rulerX1, rulerTop + RulerHeight * 0.55f, Track);
        float pxPerSecond = (rulerX1 - rulerX0) / viewEnd;
        float step = new[] { 0.1f, 0.25f, 0.5f, 1, 2, 5, 10, 30, 60 }.First(st => st * pxPerSecond >= 12 || st == 60);
        for (int i = 0; i * step <= viewEnd + 1e-3f; i++)
        {
            float x = XAt(i * step);
            bool major = i % 5 == 0;
            o.Rect(x, rulerTop + RulerHeight * (major ? 0.25f : 0.35f), x + 1, rulerTop + RulerHeight * 0.45f, major ? Dim : Track);
            if (major) o.Text($"{i * step:0.##}", x + 2, rulerTop + RulerHeight * 0.6f, Dim);
        }
        // The flight's span, and a loop's way back to the first key.
        if (keys.Count > 0)
        {
            float last = keys.Max(k => k.Time);
            o.Rect(XAt(Start), rulerTop + RulerHeight * 0.45f, XAt(last), rulerTop + RulerHeight * 0.55f, Fill * new Vector4(0.6f, 0.6f, 0.6f, 1));
            if (loop) o.Rect(XAt(last), rulerTop + RulerHeight * 0.47f, XAt(last + spacing), rulerTop + RulerHeight * 0.53f, Dim);
        }
        // A recorded path's keys sit close together: a number only where it clears the one before (the selected key's always).
        float labelEnd = float.MinValue;
        foreach (var k in Sorted)
        {
            float x = XAt(k.Time);
            var colour = k == selected ? DebugOverlay.TextColour : k.Ease ? EaseColour : Fill;
            o.Rect(x - MarkerHalf, rulerTop + RulerHeight * 0.2f, x + MarkerHalf, rulerTop + RulerHeight * 0.8f, colour);
            string label = k.Label.ToString();
            float lx = x - label.Length * o.CharWidth / 2;
            if (lx < labelEnd + 4 && k != selected) continue;
            o.Text(label, lx, rulerTop - line + 2, colour);
            labelEnd = lx + label.Length * o.CharWidth;
        }
        float ph = XAt((float)time);
        o.Rect(ph - 1, rulerTop - 2, ph + 1, rulerTop + RulerHeight, PlayheadColour);
    }

    string Summary() => keys.Count == 0 ? "none saved"
        : $"{keys.Count} ({string.Join(" ", Sorted.Select(k => k.Label))}), {End - Start:0.#} s{(loop ? " a lap" : "")}{(followTime ? "" : ", time held")}";

    static float Degrees(float radians) => radians * 180 / MathF.PI;

    static string Describe(CameraShot s) =>
        $"eye {s.Eye.X:0}, {s.Eye.Y:0}, {s.Eye.Z:0}, yaw {Degrees(s.Yaw):0}, pitch {Degrees(s.Pitch):0}, lens {Degrees(s.FieldOfView):0} deg, {WorldFrame.TimeText(s.Hour)}";

    static void Say(string text) => Console.WriteLine($"cinema    {text}");

    sealed record Saved(int Slot, float[] Eye, float Yaw, float Pitch, float Distance, float FieldOfView, float Hour, float? Time = null, bool Ease = false);

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            var list = Sorted.Select(k => new Saved(k.Label, [k.Shot.Eye.X, k.Shot.Eye.Y, k.Shot.Eye.Z], k.Shot.Yaw, k.Shot.Pitch, k.Shot.Distance,
                k.Shot.FieldOfView, k.Shot.Hour, k.Time, k.Ease)).ToList();
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Say($"could not save the keys to {File}: {e.Message}"); }
    }

    /// <summary>Reads the saved keys (a file from before the timeline has no times: its shots go <see cref="spacing"/> apart in slot order).</summary>
    public void Load()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return;
            var saved = JsonSerializer.Deserialize<List<Saved>>(System.IO.File.ReadAllText(File)) ?? [];
            int order = 0;
            foreach (var s in saved.OrderBy(s => s.Time ?? s.Slot))
            {
                if (s.Slot is < 1 or > MaxKeys || s.Eye is not { Length: 3 } || Find(s.Slot) is not null) continue;
                var shot = new CameraShot(new Vector3(s.Eye[0], s.Eye[1], s.Eye[2]), s.Yaw, s.Pitch, s.Distance, s.FieldOfView, s.Hour);
                keys.Add(new Entry(s.Slot, shot, s.Time ?? order * spacing, s.Ease));
                order++;
            }
            if (keys.Count > 0) Say($"keys {Summary()} (from {File})");
        }
        catch (Exception e) when (e is IOException or JsonException) { Say($"could not read {File}: {e.Message}"); }
    }
}
