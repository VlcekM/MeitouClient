using System.Numerics;
using System.Text.Json;

using Meitou.Rendering;

using Silk.NET.Input;

namespace Meitou.ModelViewer;

/// <summary>
/// The viewer's cinematic camera (docs/viewer.md "Cinematic camera"), for trailers: numpad 1-9 save the camera (with its lens and the time of
/// day) as a shot, Ctrl+numpad 1-9 cut to one, numpad Enter flies through them in order (<see cref="CameraPath"/>), Shift+numpad Enter records
/// the flight to PNG frames at a fixed frame rate. The shots are kept in <c>%LOCALAPPDATA%\Meitou\viewer-shots.json</c> across starts.
/// </summary>
sealed class Cinema
{
    const int Slots = 9, RecordFps = 60;
    const float DefaultFov = 50 * MathF.PI / 180, MinFov = 10 * MathF.PI / 180, MaxFov = 100 * MathF.PI / 180;
    /// <summary>The letterbox's picture shape (2.39:1, anamorphic widescreen).</summary>
    const float Scope = 2.39f;
    static readonly string File = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Meitou", "viewer-shots.json");

    readonly CameraShot?[] shots = new CameraShot?[Slots];
    float perShot = 6;
    bool loop, followTime = true;
    double time;
    string? takeFolder;
    int takeFrame;

    /// <summary>Flying the shots (numpad Enter).</summary>
    public bool Playing { get; private set; }
    /// <summary>Clean picture: no panels, letterbox bars (numpad *).</summary>
    public bool Clean { get; private set; }
    public bool Recording => takeFolder is not null;

    public Cinema() => Load();

    List<CameraShot> Flight => [.. shots.Where(s => s is not null).Select(s => s!.Value)];

    /// <summary>Handles a cinematic key; false when <paramref name="key"/> is not one.</summary>
    public bool OnKey(Key key, bool ctrl, bool shift, WorldCamera camera, Func<float> hour, Action<float> setHour)
    {
        if (key is >= Key.Keypad1 and <= Key.Keypad9)
        {
            int slot = key - Key.Keypad1;
            if (ctrl)
            {
                if (shots[slot] is { } s) { Stop(); s.Apply(camera); if (followTime) setHour(s.Hour); Say($"cut to shot {slot + 1}"); }
                else Say($"shot {slot + 1} is empty (numpad {slot + 1} saves the camera there)");
            }
            else
            {
                shots[slot] = CameraShot.Of(camera, hour());
                Save();
                Say($"saved shot {slot + 1}: {Describe(shots[slot]!.Value)}; {Summary()}");
            }
            return true;
        }
        switch (key)
        {
            case Key.KeypadEnter:
                if (Playing) { Stop(); Say("stopped"); }
                else if (Flight.Count < 2) Say("save two shots or more first (numpad 1-9)");
                else
                {
                    (Playing, time) = (true, 0);
                    if (shift)
                    {
                        takeFolder = Directory.CreateDirectory(Path.Combine(@"C:\Temp", $"meitou-take-{DateTime.Now:yyyyMMdd-HHmmss}")).FullName;
                        takeFrame = 0;
                        Say($"recording {CameraPath.Duration(Flight.Count, perShot, false):0.#} s at {RecordFps} fps into {takeFolder} (numpad Enter stops)");
                    }
                    else Say($"playing {Summary()} (numpad Enter stops)");
                }
                return true;
            case Key.Keypad0: loop = !loop; Say(loop ? "loop on" : "loop off"); return true;
            case Key.KeypadAdd: perShot = MathF.Min(perShot * 1.25f, 120); Say($"{perShot:0.#} s from shot to shot"); return true;
            case Key.KeypadSubtract: perShot = MathF.Max(perShot / 1.25f, 0.5f); Say($"{perShot:0.#} s from shot to shot"); return true;
            case Key.KeypadMultiply: Clean = !Clean; Say(Clean ? "clean picture: panels hidden, 2.39:1 letterbox" : "panels back, no letterbox"); return true;
            case Key.KeypadDivide: followTime = !followTime; Say(followTime ? "the time of day follows the shots" : "the time of day stays put"); return true;
            case Key.KeypadDecimal when ctrl:
                Stop();
                Array.Clear(shots);
                Save();
                Say("all shots deleted");
                return true;
            case Key.KeypadDecimal:
                Say(Summary());
                for (int i = 0; i < Slots; i++) if (shots[i] is { } s) Console.WriteLine($"  {i + 1}  {Describe(s)}");
                return true;
            case Key.PageUp: camera.FieldOfView = MathF.Max(camera.FieldOfView - 2.5f * MathF.PI / 180, MinFov); Say($"field of view {Degrees(camera.FieldOfView):0.#} deg"); return true;
            case Key.PageDown: camera.FieldOfView = MathF.Min(camera.FieldOfView + 2.5f * MathF.PI / 180, MaxFov); Say($"field of view {Degrees(camera.FieldOfView):0.#} deg"); return true;
            case Key.Home: camera.FieldOfView = DefaultFov; Say($"field of view {Degrees(DefaultFov):0} deg"); return true;
        }
        return false;
    }

    /// <summary>The frame's time step: the real one, or exactly one frame of the recording's rate while recording.</summary>
    public double FrameTime(double real) => Recording ? 1.0 / RecordFps : Math.Min(real, 0.25);

    /// <summary>Moves the camera (and the hour) along the flight by <paramref name="dt"/> seconds; call once a frame before drawing.</summary>
    public void Advance(double dt, WorldCamera camera, Action<float> setHour)
    {
        if (!Playing) return;
        var path = Flight;
        if (path.Count < 2) { Stop(); return; }
        float duration = CameraPath.Duration(path.Count, perShot, loop && !Recording);
        var shot = CameraPath.Sample(path, CameraPath.Progress(path.Count, perShot, (float)time, loop && !Recording), loop && !Recording);
        shot.Apply(camera);
        if (followTime) setHour(shot.Hour);
        time += dt;
        // An open flight ends on its last shot (drawn this frame); a recording of a loop is one lap.
        if (time > duration + dt * 0.5 && (!loop || Recording))
        {
            if (Recording) Say($"recorded {takeFrame + 1} frames into {takeFolder}; to a video: ffmpeg -framerate {RecordFps} -i \"{takeFolder}\\%05d.png\" -c:v libx264 -crf 16 -pix_fmt yuv420p take.mp4");
            Stop(keepTake: true);
        }
    }

    /// <summary>The file this frame is saved to while recording (then the frame is drawn without panels), else null.</summary>
    public string? TakeFrame() => takeFolder is null ? null : Path.Combine(takeFolder, $"{takeFrame++:00000}.png");

    bool endTake;

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

    /// <summary>The letterbox bars (clean picture) and, otherwise, a status line at the bottom while there are shots or a flight.</summary>
    public void Draw(DebugOverlay overlay, int width, int height, bool showStatus)
    {
        if (Clean)
        {
            float bar = MathF.Max(0, (height - width / Scope) / 2);
            if (bar > 0)
            {
                overlay.Rect(0, 0, width, bar, new Vector4(0, 0, 0, 1));
                overlay.Rect(0, height - bar, width, height, new Vector4(0, 0, 0, 1));
            }
            return;
        }
        if (!showStatus || (Flight.Count == 0 && !Playing)) return;
        string state = Playing ? $"{(Recording ? "recording" : "playing")} {time:0.0} / {CameraPath.Duration(Flight.Count, perShot, loop):0.0} s" : "numpad Enter plays";
        string line = $"shots {Summary()}   {state}";
        float y = height - overlay.LineHeight - 12;
        overlay.Rect(8, y - 4, 16 + line.Length * overlay.CharWidth, y + overlay.LineHeight + 2, new Vector4(0.05f, 0.05f, 0.06f, 0.8f));
        overlay.Text(line, 12, y, DebugOverlay.TextColour);
    }

    string Summary()
    {
        var saved = Enumerable.Range(0, Slots).Where(i => shots[i] is not null).Select(i => (i + 1).ToString()).ToList();
        return saved.Count == 0 ? "none saved" : $"{string.Join(" ", saved)}, {perShot:0.#} s each{(loop ? ", loop" : "")}{(followTime ? "" : ", time held")}";
    }

    static float Degrees(float radians) => radians * 180 / MathF.PI;

    static string Describe(CameraShot s) =>
        $"eye {s.Eye.X:0}, {s.Eye.Y:0}, {s.Eye.Z:0}, yaw {Degrees(s.Yaw):0}, pitch {Degrees(s.Pitch):0}, lens {Degrees(s.FieldOfView):0} deg, {WorldFrame.TimeText(s.Hour)}";

    static void Say(string text) => Console.WriteLine($"cinema    {text}");

    sealed record Saved(int Slot, float[] Eye, float Yaw, float Pitch, float Distance, float FieldOfView, float Hour);

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            var list = Enumerable.Range(0, Slots).Where(i => shots[i] is not null).Select(i =>
            {
                var s = shots[i]!.Value;
                return new Saved(i + 1, [s.Eye.X, s.Eye.Y, s.Eye.Z], s.Yaw, s.Pitch, s.Distance, s.FieldOfView, s.Hour);
            }).ToList();
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Say($"could not save the shots to {File}: {e.Message}"); }
    }

    void Load()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return;
            foreach (var s in JsonSerializer.Deserialize<List<Saved>>(System.IO.File.ReadAllText(File)) ?? [])
                if (s.Slot is >= 1 and <= Slots && s.Eye is { Length: 3 })
                    shots[s.Slot - 1] = new CameraShot(new Vector3(s.Eye[0], s.Eye[1], s.Eye[2]), s.Yaw, s.Pitch, s.Distance, s.FieldOfView, s.Hour);
            if (Flight.Count > 0) Say($"shots {Summary()} (from {File})");
        }
        catch (Exception e) when (e is IOException or JsonException) { Say($"could not read {File}: {e.Message}"); }
    }
}
