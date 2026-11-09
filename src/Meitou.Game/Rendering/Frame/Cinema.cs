using System.Numerics;
using System.Text.Json;
using Meitou.Game.Rendering.Overlays;

namespace Meitou.Game.Rendering.Frame;

/// <summary>
/// The keys the cinema answers to (named as Silk.NET.Input's, so a host maps one with <c>Enum.TryParse(key.ToString(), out CinemaKey k)</c>).
/// </summary>
enum CinemaKey
{
    /// <summary>
    /// Numpad 0: turns the loop back to the first key on or off.
    /// </summary>
    Keypad0,

    /// <summary>
    /// Numpad 1: cuts to key 1; with Ctrl, saves the camera as key 1.
    /// </summary>
    Keypad1,

    /// <summary>
    /// Numpad 2: cuts to key 2; with Ctrl, saves the camera as key 2.
    /// </summary>
    Keypad2,

    /// <summary>
    /// Numpad 3: cuts to key 3; with Ctrl, saves the camera as key 3.
    /// </summary>
    Keypad3,

    /// <summary>
    /// Numpad 4: cuts to key 4; with Ctrl, saves the camera as key 4.
    /// </summary>
    Keypad4,

    /// <summary>
    /// Numpad 5: cuts to key 5; with Ctrl, saves the camera as key 5.
    /// </summary>
    Keypad5,

    /// <summary>
    /// Numpad 6: cuts to key 6; with Ctrl, saves the camera as key 6.
    /// </summary>
    Keypad6,

    /// <summary>
    /// Numpad 7: cuts to key 7; with Ctrl, saves the camera as key 7.
    /// </summary>
    Keypad7,

    /// <summary>
    /// Numpad 8: cuts to key 8; with Ctrl, saves the camera as key 8.
    /// </summary>
    Keypad8,

    /// <summary>
    /// Numpad 9: cuts to key 9; with Ctrl, saves the camera as key 9.
    /// </summary>
    Keypad9,

    /// <summary>
    /// Numpad Enter: flies the keys or stops; with Shift, records the flight to PNG frames.
    /// </summary>
    KeypadEnter,

    /// <summary>
    /// Numpad +: stretches the timing (the keys, the spacing and the playhead) by 1.25.
    /// </summary>
    KeypadAdd,

    /// <summary>
    /// Numpad -: shortens the timing by 1.25.
    /// </summary>
    KeypadSubtract,

    /// <summary>
    /// Numpad *: the clean picture (no panels, letterbox bars) on or off.
    /// </summary>
    KeypadMultiply,

    /// <summary>
    /// Numpad /: whether the time of day follows the keys (not in the game, whose hour is the simulation clock's).
    /// </summary>
    KeypadDivide,

    /// <summary>
    /// Numpad .: lists the keys; with Ctrl, deletes every key.
    /// </summary>
    KeypadDecimal,

    /// <summary>
    /// Opens or closes the timeline editor.
    /// </summary>
    F6,

    /// <summary>
    /// With the editor open: plays from the playhead or stops.
    /// </summary>
    Space,

    /// <summary>
    /// With the editor open: deletes the selected key.
    /// </summary>
    Delete,

    /// <summary>
    /// Narrows the lens by 2.5 degrees, wherever <see cref="Cinema.LensZoomLive"/>.
    /// </summary>
    PageUp,

    /// <summary>
    /// Widens the lens by 2.5 degrees, wherever <see cref="Cinema.LensZoomLive"/>.
    /// </summary>
    PageDown,

    /// <summary>
    /// Puts the lens back to 50 degrees, wherever <see cref="Cinema.LensResetLive"/>.
    /// </summary>
    Home,
}

/// <summary>
/// The viewer's and the game's cinematic camera (docs/viewer.md "Cinematic camera"), for trailers. Keys are shots on a timeline
/// (<see cref="CameraKey"/>): Ctrl+numpad 1-9 save the camera (with its lens and the time of day) as key 1-9, numpad 1-9 cut to one, numpad Enter
/// flies through them (<see cref="CameraPath"/>), Shift+numpad Enter records the flight to PNG frames at a fixed frame rate. F6 opens the timeline
/// editor along the bottom of the window (<see cref="Draw"/>): drag keys to retime them, drag the ruler to scrub, buttons to add, replace, delete and
/// ease keys, to record a camera path (a key every <see cref="PathStep"/> seconds while you fly) and to delete every key. The keys are kept in
/// <paramref name="file"/> across starts (<see cref="ViewerFile"/>, <see cref="GameFile"/>). Without
/// <paramref name="setHour"/> (the game, whose hour is the simulation clock's) "time follows" is unavailable. PageUp/PageDown zoom the lens wherever
/// <see cref="LensZoomLive"/>: elsewhere in the game they are its floor keys. Home puts the lens back wherever <see cref="LensResetLive"/>.
/// </summary>
sealed class Cinema(Func<WorldCamera> camera, Func<float> getHour, Action<float>? setHour, Func<bool> fullScreenOn, Action toggleFullScreen,
    string file, bool lensKeysAlways = true)
{
    #region Playback

    /// <summary>
    /// The game's other times for the lens keys (and for Home besides the open editor): its free camera and a camera the cinema holds.
    /// </summary>
    public Func<bool>? LensKeys { get; set; }

    /// <summary>
    /// Whether Home puts the lens back (the viewer always; the game in the free camera, on a camera the cinema holds, and with its editor open).
    /// </summary>
    public bool LensResetLive => lensKeysAlways || EditorVisible || LensKeys?.Invoke() == true;

    /// <summary>
    /// Whether PageUp/PageDown zoom the lens (the viewer always; the game in the free camera and on a camera the cinema holds).
    /// </summary>
    public bool LensZoomLive => lensKeysAlways || LensKeys?.Invoke() == true;

    /// <summary>
    /// The viewer's keys file.
    /// </summary>
    public static string ViewerFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meitou",
        "viewer-shots.json");

    /// <summary>
    /// The game's keys file (its own: a world position means the same in both, so the viewer's file can be copied over it).
    /// </summary>
    public static string GameFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meitou",
        "game-shots.json");

    /// <summary>
    /// The most keys the timeline holds, and the frame rate a recorded flight's PNG frames are taken at.
    /// </summary>
    const int MaxKeys = 999, RecordFps = 60;

    /// <summary>
    /// Seconds between the keys a recorded camera path drops.
    /// </summary>
    const float PathStep = 1;

    /// <summary>
    /// The lens: Home's field of view (50 degrees) and the range PageUp/PageDown keep it in (radians); and the least time between two keys (seconds).
    /// </summary>
    const float DefaultFov = 50 * MathF.PI / 180, MinFov = 10 * MathF.PI / 180, MaxFov = 100 * MathF.PI / 180, MinGap = 0.05f;

    /// <summary>
    /// The letterbox's picture shape (2.39:1, anamorphic widescreen).
    /// </summary>
    const float Scope = 2.39f;

    /// <summary>
    /// The file the keys are kept in across starts.
    /// </summary>
    readonly string _file = file;

    /// <summary>
    /// One key on the timeline: its label (the numpad slot or a path key's number), its shot, its time and whether the camera eases to a stop there.
    /// </summary>
    sealed class Entry(int label, CameraShot shot, float time, bool ease)
    {
        /// <summary>
        /// The key's label, 1 .. <see cref="MaxKeys"/>: 1-9 are the numpad slots; a camera path's keys take the lowest free label.
        /// </summary>
        public int Label = label;

        /// <summary>
        /// The camera the key cuts to.
        /// </summary>
        public CameraShot Shot = shot;

        /// <summary>
        /// The key's time on the timeline, in seconds.
        /// </summary>
        public float Time = time;

        /// <summary>
        /// Whether the flight slows to a stop at this key.
        /// </summary>
        public bool Ease = ease;
    }

    /// <summary>
    /// The saved keys, in the order they were made (<see cref="Sorted"/> orders them by time).
    /// </summary>
    readonly List<Entry> _keys = [];

    /// <summary>
    /// The time a new key is placed after the last one, and a loop takes from the last key back to the first.
    /// </summary>
    float _spacing = 6;

    /// <summary>
    /// Whether the flight loops back to the first key (numpad 0), and whether the time of day follows the keys (numpad /).
    /// </summary>
    bool _loop, _followTime;

    /// <summary>
    /// The playhead, seconds on the timeline.
    /// </summary>
    double _time;

    /// <summary>
    /// The playhead moved without playing (a scrub, a click): the camera goes there on the next frame.
    /// </summary>
    bool _preview;

    /// <summary>
    /// The folder a recording writes its frames into; null when not recording.
    /// </summary>
    string? _takeFolder;

    /// <summary>
    /// The number of the next frame a recording writes.
    /// </summary>
    int _takeFrame;

    /// <summary>
    /// The recording reached its end: the folder is let go once the last frame is saved (<see cref="FrameDone"/>).
    /// </summary>
    bool _endTake;

    /// <summary>
    /// Recording a camera path: the playhead time of the next key it drops; null when off.
    /// </summary>
    double? _pathNext;

    /// <summary>
    /// "Delete all keys" was clicked once: a second click before this tick count deletes them.
    /// </summary>
    long _deleteAllUntil;

    /// <summary>
    /// Called whenever the cinema puts the camera somewhere (a cut, a scrub, each frame of a flight): a host with its own camera control lets the
    /// cinema hold it.
    /// </summary>
    public Action? Moved { get; set; }

    /// <summary>
    /// Whether the camera is flying the keys.
    /// </summary>
    public bool Playing { get; private set; }

    /// <summary>
    /// Clean picture: no panels, letterbox bars (numpad *).
    /// </summary>
    public bool Clean { get; private set; }

    /// <summary>
    /// Whether a flight is being recorded to PNG frames.
    /// </summary>
    public bool Recording => _takeFolder is not null;

    /// <summary>
    /// The timeline editor (F6).
    /// </summary>
    public bool EditorVisible { get; private set; }

    /// <summary>
    /// The keys in time order (a new list each call).
    /// </summary>
    List<Entry> Sorted => [.. _keys.OrderBy(k => k.Time)];

    /// <summary>
    /// The keys as the camera path flies them, in time order.
    /// </summary>
    List<CameraKey> Flight => [.. Sorted.Select(k => new CameraKey(k.Shot, k.Time, k.Ease))];

    /// <summary>
    /// The time a loop takes from the last key back to the first; null when not looping (or while recording).
    /// </summary>
    float? LoopBack => _loop && !Recording ? _spacing : null;

    /// <summary>
    /// The first key's time (0 without keys).
    /// </summary>
    float Start => _keys.Count == 0 ? 0 : _keys.Min(k => k.Time);

    /// <summary>
    /// The last key's time, plus the way back to the first when looping (0 without keys).
    /// </summary>
    float End => _keys.Count == 0 ? 0 : _keys.Max(k => k.Time) + (LoopBack ?? 0);

    /// <summary>
    /// The key labelled <paramref name="label"/>; null when there is none.
    /// </summary>
    Entry? Find(int label) => _keys.FirstOrDefault(k => k.Label == label);

    /// <summary>
    /// Handles a cinematic key; false when <paramref name="key"/> is not one.
    /// </summary>
    public bool OnKey(CinemaKey key, bool ctrl, bool shift)
    {
        var cam = camera();
        if (key is >= CinemaKey.Keypad1 and <= CinemaKey.Keypad9)
        {
            int label = key - CinemaKey.Keypad1 + 1;
            if (ctrl) SaveKey(label);
            else if (Find(label) is { } k)
            {
                Stop();
                GoTo(k);
                Say($"cut to key {label}");
            }
            else Say($"key {label} is empty (Ctrl+numpad {label} saves the camera there)");
            return true;
        }
        switch (key)
        {
            case CinemaKey.F6:
                EditorVisible = !EditorVisible;
                Say(EditorVisible ? "timeline editor open (F6 closes)" : "timeline editor closed");
                return true;
            case CinemaKey.Space when EditorVisible: TogglePlay(fromPlayhead: true); return true;
            case CinemaKey.Delete when EditorVisible: DeleteSelected(); return true;
            case CinemaKey.KeypadEnter:
                if (shift && !Playing) StartRecording();
                else TogglePlay(fromPlayhead: false);
                return true;
            case CinemaKey.Keypad0:
                _loop = !_loop;
                Say(_loop ? $"loop on ({_spacing:0.#} s back to the first key)" : "loop off");
                return true;
            case CinemaKey.KeypadAdd: Scale(1.25f); return true;
            case CinemaKey.KeypadSubtract: Scale(1 / 1.25f); return true;
            case CinemaKey.KeypadMultiply:
                Clean = !Clean;
                Say(Clean ? "clean picture: panels hidden, 2.39:1 letterbox" : "panels back, no letterbox");
                return true;
            case CinemaKey.KeypadDivide when setHour is null:
                Say("the time of day is the game clock's here: it does not follow the keys");
                return true;
            case CinemaKey.KeypadDivide:
                _followTime = !_followTime;
                Say(_followTime ? "the time of day follows the keys" : "the time of day stays put");
                return true;
            case CinemaKey.KeypadDecimal when ctrl: DeleteAll(); return true;
            case CinemaKey.KeypadDecimal:
                Say(Summary());
                foreach (var k in Sorted) Console.WriteLine($"  {k.Label,2}  {k.Time,6:0.00} s{(k.Ease ? " ease" : "     ")}  {Describe(k.Shot)}");
                return true;
            // PageUp/PageDown zoom the lens in the viewer and in the game's free and held cameras: in its strategy camera they are floor_up /
            // floor_down.
            case CinemaKey.PageUp when LensZoomLive:
                cam.FieldOfView = MathF.Max(cam.FieldOfView - 2.5f * MathF.PI / 180, MinFov);
                Say($"field of view {Degrees(cam.FieldOfView):0.#} deg");
                return true;
            case CinemaKey.PageDown when LensZoomLive:
                cam.FieldOfView = MathF.Min(cam.FieldOfView + 2.5f * MathF.PI / 180, MaxFov);
                Say($"field of view {Degrees(cam.FieldOfView):0.#} deg");
                return true;
            case CinemaKey.Home when LensResetLive:
                cam.FieldOfView = DefaultFov;
                Say($"field of view {Degrees(DefaultFov):0} deg");
                return true;
        }
        return false;
    }

    /// <summary>
    /// Saves the camera as key <paramref name="label"/>: a new key goes <see cref="_spacing"/> after the last (or at <paramref name="at"/>); an
    /// existing one keeps its time.
    /// </summary>
    void SaveKey(int label, float? at = null)
    {
        var shot = CameraShot.Of(camera(), getHour());
        if (Find(label) is { } k)
        {
            k.Shot = shot;
            Say($"key {label} set to the camera at {k.Time:0.00} s: {Describe(shot)}");
        }
        else
        {
            if (_keys.Count >= MaxKeys)
            {
                Say($"{MaxKeys} keys at most");
                return;
            }

            float t = at ?? (_keys.Count == 0 ? 0 : _keys.Max(e => e.Time) + _spacing);
            while (_keys.Any(e => MathF.Abs(e.Time - t) < MinGap)) t += MinGap;
            _keys.Add(k = new Entry(label, shot, t, false));
            Say($"key {label} saved at {t:0.00} s: {Describe(shot)}; {Summary()}");
        }
        _selected = k;
        Save();
    }

    /// <summary>
    /// Cuts the camera (and, when the time follows, the hour) to key <paramref name="k"/> and puts the playhead and the selection on it.
    /// </summary>
    void GoTo(Entry k)
    {
        k.Shot.Apply(camera());
        if (_followTime) setHour?.Invoke(k.Shot.Hour);
        Moved?.Invoke();
        (_time, _selected) = (k.Time, k);
    }

    /// <summary>
    /// Starts or stops the flight: from the playhead (<paramref name="fromPlayhead"/>, when it is inside the flight) or from the first key; needs two
    /// keys.
    /// </summary>
    void TogglePlay(bool fromPlayhead)
    {
        if (_pathNext is not null) StopPath();
        if (Playing)
        {
            Stop();
            Say($"stopped at {_time:0.00} s");
            return;
        }

        if (_keys.Count < 2)
        {
            Say("save two keys or more first (Ctrl+numpad 1-9, or Add key in the F6 editor)");
            return;
        }

        if (!fromPlayhead || _time < Start || _time >= End - 1e-3) _time = Start;
        Playing = true;
        Say($"playing from {_time:0.00} s, {Summary()} (numpad Enter{(EditorVisible ? " or Space" : "")} stops)");
    }

    /// <summary>
    /// Flies the keys on a loop from the first (<c>--play-shots</c>).
    /// </summary>
    public void PlayLoop()
    {
        _loop = true;
        if (!Playing) TogglePlay(fromPlayhead: false);
    }

    /// <summary>
    /// Starts recording the flight from the first key to PNG frames, into a new folder under <c>C:\Temp</c>; needs two keys.
    /// </summary>
    void StartRecording()
    {
        if (_pathNext is not null) StopPath();
        if (_keys.Count < 2)
        {
            Say("save two keys or more first");
            return;
        }

        _takeFolder = Directory.CreateDirectory(Path.Combine(@"C:\Temp", $"meitou-take-{DateTime.Now:yyyyMMdd-HHmmss}")).FullName;
        (_takeFrame, _time, Playing) = (0, Start, true);
        Say($"recording {End - Start:0.#} s at {RecordFps} fps into {_takeFolder} (numpad Enter stops)");
    }

    /// <summary>
    /// Starts or stops recording a camera path: the camera is saved as a new key now (one spacing after the last key, or at 0) and every
    /// <see cref="PathStep"/> seconds after while you fly it, and once more where the recording stops.
    /// </summary>
    void TogglePath()
    {
        if (_pathNext is not null)
        {
            StopPath();
            return;
        }

        Stop();
        _time = _keys.Count == 0 ? 0 : _keys.Max(k => k.Time) + _spacing;
        if (!DropPathKey()) return;
        _pathNext = _time + PathStep;
        Say($"recording the camera path: a key every {PathStep:0.#} s while you fly (Record path again stops)");
    }

    /// <summary>
    /// Stops recording a camera path, dropping a last key where the camera stopped (when the playhead is past the last key), and saves.
    /// </summary>
    void StopPath()
    {
        _pathNext = null;
        if (_keys.Count > 0 && _time > _keys.Max(k => k.Time) + MinGap) DropPathKey();
        Save();
        Say($"camera path recorded; {Summary()}");
    }

    /// <summary>
    /// Saves the camera as a new key at the playhead; false (and the recording stopped) when the keys are full.
    /// </summary>
    bool DropPathKey()
    {
        if (_keys.Count >= MaxKeys)
        {
            _pathNext = null;
            Save();
            Say($"{MaxKeys} keys at most: the camera path stops here");
            return false;
        }

        var k = new Entry(Enumerable.Range(1, MaxKeys).First(l => Find(l) is null), CameraShot.Of(camera(), getHour()), (float)_time, false);
        _keys.Add(k);
        _selected = k;
        return true;
    }

    /// <summary>
    /// Deletes every key (Ctrl+numpad ., or "Delete all keys" clicked twice).
    /// </summary>
    void DeleteAll()
    {
        Stop();
        _pathNext = null;
        _keys.Clear();
        (_selected, _time) = (null, 0);
        Save();
        Say("all keys deleted");
    }

    /// <summary>
    /// Scales the timing by <paramref name="factor"/> from the first key: the keys' times, the spacing (kept 0.5 .. 120 s) and the playhead.
    /// </summary>
    void Scale(float factor)
    {
        float start = Start;
        foreach (var k in _keys) k.Time = start + (k.Time - start) * factor;
        _spacing = Math.Clamp(_spacing * factor, 0.5f, 120);
        _time = start + (_time - start) * factor;
        Save();
        Say($"timing x{factor:0.##}: {Summary()}");
    }

    /// <summary>
    /// Deletes the key selected on the timeline.
    /// </summary>
    void DeleteSelected()
    {
        if (_selected is null)
        {
            Say("no key selected (click one on the timeline)");
            return;
        }

        _keys.Remove(_selected);
        Say($"key {_selected.Label} deleted; {Summary()}");
        _selected = null;
        Save();
    }

    /// <summary>
    /// The frame's time step: the real one, or exactly one frame of the recording's rate while recording.
    /// </summary>
    public double FrameTime(double real) => Recording ? 1.0 / RecordFps : Math.Min(real, 0.25);

    /// <summary>
    /// Moves the camera (and the hour) along the flight, or to a scrubbed playhead; call once a frame before drawing.
    /// </summary>
    public void Advance(double dt)
    {
        if (_pathNext is { } next)
        {
            _time += dt;
            if (_time >= next)
            {
                _time = next;
                if (DropPathKey()) _pathNext = next + PathStep;
            }

            return;
        }
        if (!Playing && !_preview) return;
        _preview = false;
        var flight = Flight;
        if (flight.Count == 0)
        {
            Stop();
            return;
        }

        var shot = CameraPath.Sample(flight, (float)_time, LoopBack);
        shot.Apply(camera());
        if (_followTime) setHour?.Invoke(shot.Hour);
        Moved?.Invoke();
        if (!Playing) return;
        _time += dt;
        if (LoopBack is not null)
        {
            if (_time >= End) _time = Start + (_time - End);
            return;
        }

        // An open flight ends on its last key (drawn this frame); a recording of a loop is one lap.
        if (_time > End + dt * 0.5)
        {
            if (Recording)
            {
                Say($"recorded {_takeFrame + 1} frames into {_takeFolder}; "
                    + $"to a video: ffmpeg -framerate {RecordFps} -i \"{_takeFolder}\\%05d.png\" -c:v libx264 -crf 16 -pix_fmt yuv420p take.mp4");
            }

            _time = End;
            Stop(keepTake: true);
        }
    }

    /// <summary>
    /// The file this frame is saved to while recording (then the frame is drawn without panels), else null.
    /// </summary>
    public string? TakeFrame() => _takeFolder is null ? null : Path.Combine(_takeFolder, $"{_takeFrame++:00000}.png");

    /// <summary>
    /// Stops the flight; <paramref name="keepTake"/> keeps a recording's folder until its last frame is saved, else the recording ends now.
    /// </summary>
    void Stop(bool keepTake = false)
    {
        Playing = false;
        if (keepTake) _endTake = true;
        else _takeFolder = null;
    }

    /// <summary>
    /// Call after the frame is saved: a recording that reached its end lets go of its folder.
    /// </summary>
    public void FrameDone()
    {
        if (_endTake) (_takeFolder, _endTake) = (null, false);
    }

    // ---- The timeline editor (F6) ----

    #endregion

    #region Timeline editor

    /// <summary>
    /// The editor's colours: the buttons and the ruler's track, the keys and the flight's span, the help text and major ticks, easing keys, the
    /// playhead, and the panel's background.
    /// </summary>
    static readonly Vector4 Track = new(0.3f, 0.3f, 0.32f, 1), Fill = new(0.85f, 0.65f, 0.35f, 1), Dim = new(0.7f, 0.7f, 0.68f, 1),
        EaseColour = new(0.45f, 0.7f, 0.95f, 1), PlayheadColour = new(0.95f, 0.3f, 0.25f, 1), Background = new(0.05f, 0.05f, 0.06f, 0.92f);

    /// <summary>
    /// The editor's layout in pixels: the panel's padding, the ruler's height and half a key marker's width.
    /// </summary>
    const float Pad = 10, RulerHeight = 46, MarkerHalf = 6;

    /// <summary>
    /// The key selected on the timeline (clicked, cut to or saved last); null when none.
    /// </summary>
    Entry? _selected;

    /// <summary>
    /// What the mouse is dragging in the editor.
    /// </summary>
    enum Drag
    {
        /// <summary>
        /// Nothing.
        /// </summary>
        None,

        /// <summary>
        /// The selected key, along the ruler (retiming it).
        /// </summary>
        Key,

        /// <summary>
        /// The playhead (scrubbing).
        /// </summary>
        Playhead,
    }

    /// <summary>
    /// What the mouse drags now.
    /// </summary>
    Drag _drag;

    /// <summary>
    /// The time at the ruler's right end, in seconds (at least 10, in steps of 5).
    /// </summary>
    float _viewEnd = 10;

    /// <summary>
    /// The overlay the editor was last laid out in; null before the first draw.
    /// </summary>
    DebugOverlay? _ui;

    /// <summary>
    /// The editor's layout in pixels: the panel's edges, the buttons' rows, the ruler's top and its time axis' ends.
    /// </summary>
    float _x0, _x1, _y0, _y1, _buttonsTop, _buttonsBottom, _rulerTop, _rulerX0, _rulerX1;

    /// <summary>
    /// The buttons as laid out: label, left and right edges, row, and what a click runs.
    /// </summary>
    readonly List<(string Label, float X0, float X1, int Row, Action Run)> _buttons = [];

    /// <summary>
    /// The top of button row <paramref name="row"/>.
    /// </summary>
    float RowTop(int row) => _buttonsTop + row * (_ui!.LineHeight + 8 + 6);

    /// <summary>
    /// The time on the ruler at <paramref name="x"/> (not before 0).
    /// </summary>
    float TimeAt(float x) => Math.Max(0, (x - _rulerX0) / (_rulerX1 - _rulerX0) * _viewEnd);

    /// <summary>
    /// Where time <paramref name="t"/> is on the ruler.
    /// </summary>
    float XAt(float t) => _rulerX0 + t / _viewEnd * (_rulerX1 - _rulerX0);

    /// <summary>
    /// Lays the editor out at the bottom of a <paramref name="width"/> x <paramref name="height"/> window: the ruler's scale, the buttons in as many
    /// rows as fit, the panel's height.
    /// </summary>
    void Layout(DebugOverlay o, int width, int height)
    {
        _ui = o;
        float line = o.LineHeight;
        (_x0, _x1) = (16, width - 16);
        _y1 = height - 16;
        (_rulerX0, _rulerX1) = (_x0 + Pad + 8, _x1 - Pad - 8);

        // While a key is dragged the scale stays put; otherwise it fits the flight with room to add keys after it.
        if (_drag == Drag.None) _viewEnd = MathF.Max(10, MathF.Ceiling((End + _spacing) / 5) * 5);

        // The buttons, in as many rows as the window's width needs.
        _buttons.Clear();
        float bx = _x0 + Pad;
        int row = 0;
        void Button(string label, Action run)
        {
            float w = label.Length * o.CharWidth + 16;
            if (bx + w > _x1 - Pad && bx > _x0 + Pad) (bx, row) = (_x0 + Pad, row + 1);
            _buttons.Add((label, bx, bx + w, row, run));
            bx += w + 6;
        }
        Button(Playing ? "Stop" : "Play", () => TogglePlay(fromPlayhead: true));
        Button(_loop ? "Loop: on" : "Loop: off", () => OnKey(CinemaKey.Keypad0, false, false));
        Button("Add key at playhead", () => SaveKey(Enumerable.Range(1, MaxKeys).First(l => Find(l) is null), (float)_time));
        Button("Set key to camera", () => { if (_selected is { } s) SaveKey(s.Label); else Say("no key selected"); });
        Button("Go to key", () => { if (_selected is { } s) { Stop(); GoTo(s); } else Say("no key selected"); });
        Button(_selected is { Ease: true } ? "Ease: on" : "Ease: off",
            () => { if (_selected is { } s) { s.Ease = !s.Ease; Save(); _preview = true; } else Say("no key selected"); });
        Button("Delete key", DeleteSelected);
        Button(setHour is null ? "Time follows: n/a" : _followTime ? "Time follows: on" : "Time follows: off", () => OnKey(CinemaKey.KeypadDivide,
            false, false));
        Button("Slower", () => Scale(1.25f));
        Button("Faster", () => Scale(1 / 1.25f));
        Button("Record", () => { if (!Playing) StartRecording(); });
        Button(_pathNext is null ? "Record path: off" : "Record path: on", TogglePath);
        Button(Environment.TickCount64 < _deleteAllUntil ? "Delete all keys: click again" : "Delete all keys", () =>
        {
            if (Environment.TickCount64 < _deleteAllUntil)
            {
                _deleteAllUntil = 0;
                DeleteAll();
            }
            else if (_keys.Count == 0) Say("no keys to delete");
            else
            {
                _deleteAllUntil = Environment.TickCount64 + 3000;
                Say($"click Delete all keys again within 3 s to delete all {_keys.Count}");
            }
        });
        Button(Clean ? "Letterbox: on" : "Letterbox: off", () => Clean = !Clean);
        Button(fullScreenOn() ? "Full screen: on" : "Full screen: off", toggleFullScreen);

        // The button rows, two text lines, the key numbers' line, the ruler.
        float rows = (row + 1) * (line + 8) + row * 6;
        _y0 = _y1 - (Pad + rows + 6 + line + 6 + line + line + RulerHeight + Pad);
        _buttonsTop = _y0 + Pad;
        _buttonsBottom = _buttonsTop + rows;
        _rulerTop = _buttonsBottom + 6 + line + 6 + line + line;
    }

    /// <summary>
    /// Whether a point (window pixels) is on the editor.
    /// </summary>
    public bool Contains(Vector2 p) => EditorVisible && _ui is not null && p.X >= _x0 && p.X <= _x1 && p.Y >= _y0 && p.Y <= _y1;

    /// <summary>
    /// A click on the editor: a button, a key (select, then drag to retime) or the ruler (scrub); true when the editor took it.
    /// </summary>
    public bool MouseDown(Vector2 p)
    {
        if (!Contains(p)) return false;
        float line = _ui!.LineHeight;
        if (p.Y >= _buttonsTop && p.Y <= _buttonsBottom)
        {
            foreach (var b in _buttons)
            {
                if (p.X >= b.X0 && p.X <= b.X1 && p.Y >= RowTop(b.Row) && p.Y <= RowTop(b.Row) + line + 8)
                {
                    b.Run();
                    break;
                }
            }

            return true;
        }
        if (p.Y >= _rulerTop - line && p.Y <= _rulerTop + RulerHeight)
        {
            if (_pathNext is not null) StopPath();

            // The nearest key within reach of the pointer, else the playhead.
            var hit = _keys.Select(k => (k,
                d: MathF.Abs(XAt(k.Time) - p.X))).Where(h => h.d <= MarkerHalf + 3).OrderBy(h => h.d).Select(h => h.k).FirstOrDefault();
            Stop();
            if (hit is not null)
            {
                (_selected, _drag) = (hit, Drag.Key);
                return true;
            }

            _drag = Drag.Playhead;
            MouseMove(p);
        }
        return true;
    }

    /// <summary>
    /// Moves a dragged key (past other keys too, which reorders them; never onto one) or the playhead; true while dragging.
    /// </summary>
    public bool MouseMove(Vector2 p)
    {
        if (_drag == Drag.None || _ui is null) return false;
        float t = TimeAt(p.X);
        if (_drag == Drag.Key && _selected is { } k)
        {
            // To 0.05 s, not before 0; over another key it steps on in the direction it moves, so a key can be pulled in front of or behind others.
            float to = MathF.Max(0, MathF.Round(t * 20) / 20), step = to >= k.Time ? MinGap : -MinGap;
            while (_keys.Any(e => e != k && MathF.Abs(e.Time - to) < MinGap - 1e-4f)) to = to + step < 0 ? (step = MinGap) : to + step;
            k.Time = to;
            _time = k.Time;
        }
        else _time = t;
        _preview = true;
        return true;
    }

    /// <summary>
    /// Ends a drag; true when one was in progress.
    /// </summary>
    public bool MouseUp()
    {
        if (_drag == Drag.None) return false;
        if (_drag == Drag.Key) Save();
        _drag = Drag.None;
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
        if (saving)
        {
            _ui = null;
            return;
        }

        if (EditorVisible)
        {
            DrawEditor(o, width, height);
            return;
        }

        _ui = null;
        if (!showStatus || Clean || (_keys.Count == 0 && !Playing)) return;
        string mode = _pathNext is not null ? $"recording the camera path {_time:0.0} s"
            : Playing ? $"{(Recording ? "recording" : "playing")} {_time:0.0} / {End:0.0} s" : "numpad Enter plays, F6 edits";
        string line = $"keys {Summary()}   {mode}";
        float y = height - o.LineHeight - 12;
        o.Rect(8, y - 4, 16 + line.Length * o.CharWidth, y + o.LineHeight + 2, new Vector4(0.05f, 0.05f, 0.06f, 0.8f));
        o.Text(line, 12, y, DebugOverlay.TextColour);
    }

    /// <summary>
    /// Draws the editor: the panel, the buttons, the selected key's line, the ruler with its keys and the playhead.
    /// </summary>
    void DrawEditor(DebugOverlay o, int width, int height)
    {
        Layout(o, width, height);
        float line = o.LineHeight;
        o.Rect(_x0, _y0, _x1, _y1, Background);
        foreach (var b in _buttons)
        {
            float top = RowTop(b.Row);
            o.Rect(b.X0, top, b.X1, top + line + 8, Track);
            o.Text(b.Label, b.X0 + 8, top + 4, DebugOverlay.TextColour);
        }
        float infoTop = _buttonsBottom + 6;
        string sel = _selected is { } s
            ? $"key {s.Label} at {s.Time:0.00} s{(s.Ease ? ", eases to a stop" : "")}: {Describe(s.Shot)}"
            : "no key selected: click a key on the timeline";
        o.Text(sel, _x0 + Pad, infoTop, DebugOverlay.TextColour);
        string loopNote = _loop ? " (loop)" : "";
        string help = $"playhead {_time:0.00} s, flight {Start:0.00} to {End:0.00} s{loopNote}.  Drag a key to retime it, drag the ruler to scrub; " +
            "Space play, Del delete, Ctrl+Num1-9 save, Num1-9 go, F6 close";
        o.Text(help, _x0 + Pad, infoTop + line + 6, Dim);

        // The ruler: seconds, a tick every step and a number every fifth.
        o.Rect(_rulerX0, _rulerTop + RulerHeight * 0.45f, _rulerX1, _rulerTop + RulerHeight * 0.55f, Track);
        float pxPerSecond = (_rulerX1 - _rulerX0) / _viewEnd;
        float step = new[] { 0.1f, 0.25f, 0.5f, 1, 2, 5, 10, 30, 60 }.First(st => st * pxPerSecond >= 12 || st == 60);
        for (int i = 0; i * step <= _viewEnd + 1e-3f; i++)
        {
            float x = XAt(i * step);
            bool major = i % 5 == 0;
            o.Rect(x, _rulerTop + RulerHeight * (major ? 0.25f : 0.35f), x + 1, _rulerTop + RulerHeight * 0.45f, major ? Dim : Track);
            if (major) o.Text($"{i * step:0.##}", x + 2, _rulerTop + RulerHeight * 0.6f, Dim);
        }

        // The flight's span, and a loop's way back to the first key.
        if (_keys.Count > 0)
        {
            float last = _keys.Max(k => k.Time);
            o.Rect(XAt(Start), _rulerTop + RulerHeight * 0.45f, XAt(last), _rulerTop + RulerHeight * 0.55f, Fill * new Vector4(0.6f, 0.6f, 0.6f, 1));
            if (_loop)
                o.Rect(XAt(last), _rulerTop + RulerHeight * 0.47f, XAt(last + _spacing), _rulerTop + RulerHeight * 0.53f, Dim);
        }

        // A recorded path's keys sit close together: a number only where it clears the one before (the selected key's always).
        float labelEnd = float.MinValue;
        foreach (var k in Sorted)
        {
            float x = XAt(k.Time);
            var colour = k == _selected ? DebugOverlay.TextColour : k.Ease ? EaseColour : Fill;
            o.Rect(x - MarkerHalf, _rulerTop + RulerHeight * 0.2f, x + MarkerHalf, _rulerTop + RulerHeight * 0.8f, colour);
            string label = k.Label.ToString();
            float lx = x - label.Length * o.CharWidth / 2;
            if (lx < labelEnd + 4 && k != _selected) continue;
            o.Text(label, lx, _rulerTop - line + 2, colour);
            labelEnd = lx + label.Length * o.CharWidth;
        }
        float ph = XAt((float)_time);
        o.Rect(ph - 1, _rulerTop - 2, ph + 1, _rulerTop + RulerHeight, PlayheadColour);
    }

    #endregion

    #region Persistence and reporting

    /// <summary>
    /// The keys in a line: their count and labels in time order, the flight's length, and the loop and time-held notes.
    /// </summary>
    string Summary()
    {
        if (_keys.Count == 0) return "none saved";
        string lap = _loop ? " a lap" : "";
        string hold = _followTime ? "" : ", time held";
        return $"{_keys.Count} ({string.Join(" ", Sorted.Select(k => k.Label))}), {End - Start:0.#} s{lap}{hold}";
    }

    /// <summary>
    /// <paramref name="radians"/> in degrees.
    /// </summary>
    static float Degrees(float radians) => radians * 180 / MathF.PI;

    /// <summary>
    /// <paramref name="s"/> as a line: the eye, yaw, pitch, lens and time of day.
    /// </summary>
    static string Describe(CameraShot s)
    {
        string time = WorldFrame.TimeText(s.Hour);
        var eye = s.Eye;
        return $"eye {eye.X:0}, {eye.Y:0}, {eye.Z:0}, yaw {Degrees(s.Yaw):0}, pitch {Degrees(s.Pitch):0}, "
            + $"lens {Degrees(s.FieldOfView):0} deg, {time}";
    }

    /// <summary>
    /// Writes <paramref name="text"/> to the console as a cinema line.
    /// </summary>
    static void Say(string text) => Console.WriteLine($"cinema    {text}");

    /// <summary>
    /// A key as the keys file stores it (JSON); a file from before the timeline has no times.
    /// </summary>
    sealed record Saved(int Slot, float[] Eye, float Yaw, float Pitch, float Distance, float FieldOfView, float Hour, float? Time = null,
        bool Ease = false);

    /// <summary>
    /// Writes the keys to the keys file (an I/O failure is logged, not thrown).
    /// </summary>
    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var list = Sorted.Select(k => new Saved(k.Label, [k.Shot.Eye.X, k.Shot.Eye.Y, k.Shot.Eye.Z], k.Shot.Yaw, k.Shot.Pitch, k.Shot.Distance,
                k.Shot.FieldOfView, k.Shot.Hour, k.Time, k.Ease)).ToList();
            System.IO.File.WriteAllText(_file, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say($"could not save the keys to {_file}: {e.Message}");
        }
    }

    /// <summary>
    /// Reads the saved keys (a file from before the timeline has no times: its shots go <see cref="_spacing"/> apart in slot order).
    /// </summary>
    public void Load()
    {
        try
        {
            if (!System.IO.File.Exists(_file)) return;
            var saved = JsonSerializer.Deserialize<List<Saved>>(System.IO.File.ReadAllText(_file)) ?? [];
            int order = 0;
            foreach (var s in saved.OrderBy(s => s.Time ?? s.Slot))
            {
                if (s.Slot is < 1 or > MaxKeys || s.Eye is not { Length: 3 } || Find(s.Slot) is not null) continue;
                var shot = new CameraShot(new Vector3(s.Eye[0], s.Eye[1], s.Eye[2]), s.Yaw, s.Pitch, s.Distance, s.FieldOfView, s.Hour);
                _keys.Add(new Entry(s.Slot, shot, s.Time ?? order * _spacing, s.Ease));
                order++;
            }
            if (_keys.Count > 0) Say($"keys {Summary()} (from {_file})");
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            Say($"could not read {_file}: {e.Message}");
        }
    }

    #endregion
}