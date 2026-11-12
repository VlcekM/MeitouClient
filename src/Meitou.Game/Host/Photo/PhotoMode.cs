using System.Globalization;
using System.Numerics;
using Meitou.Game.DevTools;
using Meitou.Game.Host.Cameras;
using Meitou.Game.Host.Input;
using Meitou.Game.Rendering.Frame;
using Meitou.Simulation;

namespace Meitou.Game.Host.Photo;

/// <summary>
/// Photo mode (docs/viewer.md "Photo mode"): the simulation pauses, the interface hides and the camera flies free of the strategy camera, with a lens
/// (field of view, depth of field), roll, an exposure compensation, a time of day that is only drawn, composition grids and frame bars; a picture is
/// saved without the panel and the grid. Entering keeps what leaving puts back: the rig (the strategy camera where it stood), the pause, the
/// interface, the lens, the hour, the exposure and the depth of field. Nothing here is saved or reaches the simulation: with the mode never entered
/// the game is unchanged.
/// </summary>
sealed class PhotoMode
{
    #region State

    readonly GameHost _host;

    /// <summary>
    /// The game the mode works on (the panel draws through its interface).
    /// </summary>
    internal GameHost Host => _host;

    /// <summary>
    /// Photo mode is on.
    /// </summary>
    public bool Active { get; private set; }

    /// <summary>
    /// The panel is drawn (a key hides it for a clean view).
    /// </summary>
    public bool PanelShown = true;

    /// <summary>
    /// The composition grid and the frame bars.
    /// </summary>
    public PhotoGrid Grid = PhotoGrid.Thirds;
    public PhotoFrame Frame = PhotoFrame.Off;

    /// <summary>
    /// Set by the loop: whether photo mode may start now (not over the title, a loading cover, a cinema flight or a squad tour), and what it does
    /// first (lets the cinema's held camera go).
    /// </summary>
    public Func<bool> Allowed = () => true;
    public Action Entering = () => { };

    /// <summary>
    /// What leaving puts back.
    /// </summary>
    CameraRig.Saved _rig;
    CharacterId? _follow;
    bool _interfaceWasHidden;
    float _fieldOfView;
    (double Speed, double Last) _clock;
    PhotoControls _controls = new();

    /// <summary>
    /// The lens and picture settings (units as <see cref="Params"/> shows them).
    /// </summary>
    bool _dof, _autoFocus = true;
    /// <summary>
    /// The characters' speech bubbles are drawn (hidden on entering: a picture is of the scene).
    /// </summary>
    bool _dialogue;
    float _focusMetres = 10, _aperture = 1, _exposureStops;

    /// <summary>
    /// The lens the game had on entering, which <see cref="Reset"/> returns to.
    /// </summary>
    float _entryFieldOfView = 50 * MathF.PI / 180, _entryAperture = 1;
    bool _entryDof;

    /// <summary>
    /// The file a picture is being saved to (null when none is), whether the panel and grid stay in it (the control channel's <c>photo shot --ui</c>,
    /// for checking them), and the message shown for a while.
    /// </summary>
    string? _pending;
    bool _shotWithGuides;
    string _message = "";
    long _messageUntil;

    /// <summary>
    /// The panel and guides drawn over the picture.
    /// </summary>
    public PhotoPanel Panel { get; }

    public PhotoMode(GameHost host)
    {
        _host = host;
        Params = BuildParams();
        Panel = new PhotoPanel(this);
    }

    #endregion

    #region Parameters

    /// <summary>
    /// One setting of the mode: a slider between <see cref="Min"/> and <see cref="Max"/> (<see cref="Log"/>: by the ratio), or the named
    /// <see cref="Choices"/> (two are an off/on switch). <see cref="Name"/> is the <c>photo set</c> name.
    /// </summary>
    internal sealed record Param(string Name, string Label, float Min, float Max, Func<float> Get, Action<float> Set, Func<float, string> Text,
        bool Log = false, string[]? Choices = null, Func<bool>? Enabled = null);

    /// <summary>
    /// The settings in the panel's order.
    /// </summary>
    internal IReadOnlyList<Param> Params { get; }

    Param[] BuildParams() =>
    [
        new("fov", "Field of view", 10, 100, () => _host.Camera.FieldOfView * 180 / MathF.PI, v => _host.Camera.FieldOfView = v * MathF.PI / 180,
            v => $"{v:0} deg"),
        new("roll", "Roll", -90, 90, () => _host.Session.Camera.Free.Roll * 180 / MathF.PI,
            v => _host.Session.Camera.Free.Roll = Math.Clamp(v, -90f, 90f) * MathF.PI / 180, v => $"{v:0.0} deg"),
        new("dof", "Depth of field", 0, 1, () => _dof ? 1 : 0, v => _dof = v >= 0.5f, v => v >= 0.5f ? "on" : "off", Choices: ["off", "on"]),
        new("focusmode", "Focus", 0, 1, () => _autoFocus ? 1 : 0, v => _autoFocus = v >= 0.5f, v => v >= 0.5f ? "auto (centre)" : "manual",
            Choices: ["manual", "auto"]),
        new("focus", "Focus distance", 0.3f, 1000, () => _focusMetres, v => (_focusMetres, _autoFocus) = (Math.Clamp(v, 0.3f, 1000), false),
            v => $"{v:0.0} m", Log: true, Enabled: () => !_autoFocus),
        new("aperture", "Aperture", 0.25f, 22, () => _aperture, v => _aperture = Math.Clamp(MathF.Round(v * 20) / 20, 0.25f, 22),
            v => $"f/{v:0.0#}", Log: true),
        new("exposure", "Exposure", -3, 3, () => _exposureStops, v => _exposureStops = Math.Clamp(MathF.Round(v * 10) / 10, -3, 3),
            v => $"{v:+0.0;-0.0;0.0} EV"),
        new("hour", "Time of day", 0, 24 - 1 / 60f, () => _host.PhotoHour ?? (float)_host.Session.Clock.HourOfDay,
            v => _host.PhotoHour = Math.Clamp(MathF.Round(v * 60) / 60, 0, 24 - 1 / 60f), WorldFrame.TimeText),
        new("grid", "Grid", 0, 4, () => (float)Grid, v => Grid = (PhotoGrid)Math.Clamp((int)MathF.Round(v), 0, 4), v => Grid.ToString(),
            Choices: ["off", "thirds", "golden", "cross", "diagonals"]),
        new("frame", "Frame", 0, 5, () => (float)Frame, v => Frame = (PhotoFrame)Math.Clamp((int)MathF.Round(v), 0, 5), v => Frame.ToString(),
            Choices: ["off", "2.39:1", "16:9", "4:3", "1:1", "4:5"]),
        new("dialogue", "Dialogue", 0, 1, () => _dialogue ? 1 : 0, v => _dialogue = v >= 0.5f, v => v >= 0.5f ? "shown" : "hidden",
            Choices: ["hidden", "shown"]),
        new("speed", "Fly speed", 0.5f, 3000, () => _controls.FlySpeed / 10, v => _controls.FlySpeed = Math.Clamp(v * 10, PhotoControls.MinSpeed,
            PhotoControls.MaxSpeed), v => $"{v:0.#} m/s", Log: true),
        new("live", "Simulation", 0, 1, () => _host.Session.Simulation.IsPaused ? 0 : 1, SetLive, v => v >= 0.5f ? "running" : "paused",
            Choices: ["paused", "running"]),
    ];

    /// <summary>
    /// The text of a setting's current value.
    /// </summary>
    internal static string ValueText(Param p) => p.Choices is { } c ? c[Math.Clamp((int)MathF.Round(p.Get()), 0, c.Length - 1)] : p.Text(p.Get());

    /// <summary>
    /// Lets the simulation run (so a pose can be chosen) or pauses it again.
    /// </summary>
    void SetLive(float value)
    {
        var clock = _host.Session.Simulation;
        clock.SetSpeed(value >= 0.5f ? (_clock.Last > 0 ? _clock.Last : 1) : 0);
    }

    /// <summary>
    /// The game's own time of day back, drawn again.
    /// </summary>
    public void UseGameTime() => _host.PhotoHour = null;

    /// <summary>
    /// The settings back to what they are on entering (the camera stays where it is; the picture is level).
    /// </summary>
    public void Reset()
    {
        _host.Camera.FieldOfView = _entryFieldOfView;
        _host.Session.Camera.Free.Roll = 0;
        (_dof, _autoFocus, _focusMetres, _aperture, _exposureStops) = (_entryDof, true, 10, _entryAperture, 0);
        UseGameTime();
        (Grid, Frame) = (PhotoGrid.Thirds, PhotoFrame.Off);
        _dialogue = false;
        _controls.FlySpeed = 300;
        SetLive(0);
    }

    #endregion

    #region Entering and leaving

    /// <summary>
    /// Starts photo mode at the camera now drawn; false when it may not (see <see cref="Allowed"/>).
    /// </summary>
    public bool Enter()
    {
        if (Active) return true;
        if (!Allowed()) return false;
        Entering();
        var rig = _host.Session.Camera;
        var clock = _host.Session.Simulation;
        var cam = _host.Camera;
        _rig = rig.Save();
        _follow = _host.Player.Follow;
        _interfaceWasHidden = _host.AgentChannel.InterfaceHidden;
        _fieldOfView = cam.FieldOfView;
        _clock = (clock.Speed, clock.LastNonZeroSpeed);
        rig.TakeOver(new CameraState(cam.Eye, cam.Forward, Vector3.UnitY, cam.Target, cam.Yaw, cam.Pitch, cam.Distance, true), strategy: false);
        _controls = rig.Free.Photo = new PhotoControls();
        var options = _host.WorldOptions.Post;
        (_entryFieldOfView, _entryDof, _entryAperture) = (cam.FieldOfView, options.DepthOfField, options.DofAperture);
        Reset();
        _host.Player.Follow = null;
        _host.AgentChannel.InterfaceHidden = true;
        PanelShown = true;
        Active = true;
        Say("Photo mode");
        Console.WriteLine("photo     on");
        return true;
    }

    /// <summary>
    /// Ends photo mode and puts everything back as <see cref="Enter"/> found it.
    /// </summary>
    public void Leave()
    {
        if (!Active) return;
        var clock = _host.Session.Simulation;
        _host.Session.Camera.Free.Photo = null;
        _host.Session.Camera.Free.Roll = 0;
        _host.Session.Camera.Restore(_rig);
        _host.Camera.FieldOfView = _fieldOfView;
        clock.SetSpeed(_clock.Last);
        clock.SetSpeed(_clock.Speed);
        _host.Player.Follow = _follow;
        _host.AgentChannel.InterfaceHidden = _interfaceWasHidden;
        End();
        Console.WriteLine("photo     off");
    }

    /// <summary>
    /// Ends photo mode where the world it belongs to is gone (a load, the main menu): its drawn effects go, the camera and the clock are the new
    /// world's.
    /// </summary>
    public void Abandon()
    {
        if (!Active) return;
        _host.Session.Camera.Free.Photo = null;
        _host.Session.Camera.Free.Roll = 0;
        _host.Camera.FieldOfView = _fieldOfView;
        _host.AgentChannel.InterfaceHidden = _interfaceWasHidden;
        End();
    }

    void End()
    {
        var options = _host.WorldOptions.Post;
        (options.PhotoExposure, options.PhotoDepthOfField) = (1, null);
        (_host.Camera.Roll, _host.Camera.FocusDistance, _host.Camera.Aperture) = (0, null, null);
        (_host.PhotoHour, _host.PhotoLook) = (null, false);
        _host.Player.SpeechBubbles.Hidden = false;
        Active = false;
        _pending = null;
        _shotWithGuides = false;
    }

    /// <summary>
    /// Photo mode on or off.
    /// </summary>
    public void Toggle()
    {
        if (Active) Leave();
        else Enter();
    }

    #endregion

    #region Each frame

    /// <summary>
    /// After the frame's camera is placed: the roll, lens, exposure, depth of field and hour for this frame's draw.
    /// </summary>
    public void Apply()
    {
        if (!Active) return;
        var camera = _host.Camera;
        var options = _host.WorldOptions.Post;
        camera.Roll = _host.Session.Camera.Free.Roll;
        camera.FocusDistance = _autoFocus ? null : _focusMetres * 10;
        camera.Aperture = _aperture;
        options.PhotoExposure = MathF.Pow(2, _exposureStops);
        options.PhotoDepthOfField = _dof;
        options.CinematicView = true;
        _host.PhotoLook = true;
        _host.Player.SpeechBubbles.Hidden = !_dialogue;
        _controls.LookScale = MathF.Tan(camera.FieldOfView * 0.5f) / MathF.Tan(25 * MathF.PI / 180);
        _host.AgentChannel.InterfaceHidden = true;
    }

    #endregion

    #region Keys

    /// <summary>
    /// A key: the photo mode key starts the mode; while it is on every key is taken. Esc and the same key leave, F7 (the interface key) hides the
    /// panel, Enter and F8 take the picture, PageUp and PageDown set the lens, V levels the roll, the camera's keys go on to the free camera and the
    /// rest are dropped (no order, load or window opens under it). False when the key is not for photo mode.
    /// </summary>
    public bool KeyDown(Key key, bool escape = false)
    {
        var bindings = _host.Session.Bindings;
        bool toggle = bindings.Get(InputAction.TogglePhotoMode).Contains(Binding.Of(key));
        if (!Active) return toggle && Enter();
        if (toggle || escape) Leave();
        else if (bindings.Get(InputAction.ToggleInterface).Contains(Binding.Of(key))) PanelShown = !PanelShown;
        else if (key == Key.Enter || bindings.Get(InputAction.Screenshot).Contains(Binding.Of(key))) TakePicture();
        else if (key is Key.PageUp or Key.PageDown) StepLens(key == Key.PageUp ? -2.5f : 2.5f);
        else if (key == Key.V) _host.Session.Camera.Free.Roll = 0;
        else if (FliesCamera(key)) _host.Session.Input.SetKey(key, true);
        return true;
    }

    /// <summary>
    /// The keys that fly the photo camera: the movement, turn and tilt actions' keys, the roll keys, Space (pause) and the speed modifiers.
    /// </summary>
    bool FliesCamera(Key key)
    {
        if (key is Key.Z or Key.X or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt) return true;
        InputAction[] flying = [InputAction.MoveForward, InputAction.MoveBack, InputAction.MoveLeft, InputAction.MoveRight, InputAction.MoveUp,
            InputAction.MoveDown, InputAction.RotateLeft, InputAction.RotateRight, InputAction.PitchUp, InputAction.PitchDown, InputAction.Pause];
        return flying.Any(a => _host.Session.Bindings.Get(a).Contains(Binding.Of(key)));
    }

    /// <summary>
    /// PageUp zooms the lens in (a narrower view), PageDown out, 2.5 degrees a press.
    /// </summary>
    void StepLens(float degrees) =>
        _host.Camera.FieldOfView = Math.Clamp(_host.Camera.FieldOfView + degrees * MathF.PI / 180, 10 * MathF.PI / 180, 100 * MathF.PI / 180);

    #endregion

    #region Pictures

    /// <summary>
    /// Asks for a picture: the next frame is saved to <paramref name="file"/> (null: the Pictures folder's Meitou folder, named by the time), the
    /// size it is drawn at, with the frame bars and without the panel and the grid, or with them when <paramref name="withGuides"/>.
    /// </summary>
    public string? TakePicture(string? file = null, bool withGuides = false, bool answerControl = false)
    {
        if (!Active || _pending is not null) return null;
        string path = file is null ? DefaultPath() : Path.GetFullPath(file);
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say($"Cannot save to {path}: {e.Message}");
            return null;
        }
        _pending = path;
        _shotWithGuides = withGuides;
        _host.AgentChannel.ShotPath = path;
        _host.AgentChannel.ShotWaitId = answerControl ? _host.AgentChannel.CurrentControlId : null;
        _host.ScreenshotRequested = true;
        return path;
    }

    static string DefaultPath()
    {
        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (pictures.Length == 0) pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
        return Path.Combine(pictures, "Meitou", $"Meitou {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");
    }

    /// <summary>
    /// A frame being saved leaves the panel and the grid out (unless asked to keep them).
    /// </summary>
    public bool GuidesDrawn(bool saving) => !saving || _shotWithGuides;

    /// <summary>
    /// The picture was written: says where.
    /// </summary>
    public void PictureSaved(string path)
    {
        if (_pending is null) return;
        _pending = null;
        _shotWithGuides = false;
        Say($"Saved {path}");
    }

    /// <summary>
    /// A line over the picture for a few seconds.
    /// </summary>
    public void Say(string text)
    {
        _message = text;
        _messageUntil = Environment.TickCount64 + 5000;
    }

    /// <summary>
    /// The message still shown, or null.
    /// </summary>
    public string? Message => Environment.TickCount64 < _messageUntil ? _message : null;

    #endregion

    #region Console

    /// <summary>
    /// The <c>photo</c> console command: <c>on|off|toggle</c>, <c>set &lt;name&gt; &lt;value&gt;</c>, <c>get</c>, <c>reset</c>, <c>cam x y z yaw pitch</c>,
    /// <c>panel on|off</c>, <c>key &lt;name&gt; [up]</c> and <c>shot [file] [--ui]</c>.
    /// </summary>
    public void Command(IReadOnlyList<string> args, DevConsole con)
    {
        string verb = args.Count > 0 ? args[0].ToLowerInvariant() : "get";
        switch (verb)
        {
            case "on":
                con.Print(Enter() ? "photo mode on" : "photo mode cannot start here");
                return;
            case "off":
                Leave();
                con.Print("photo mode off");
                return;
            case "toggle":
                Toggle();
                con.Print(Active ? "photo mode on" : "photo mode off");
                return;
        }
        if (verb == "key")
        {
            KeyCommand(args, con);
            return;
        }
        if (!Active)
        {
            con.Error("photo mode is off (photo on)");
            return;
        }
        switch (verb)
        {
            case "get":
                con.Print(Describe());
                break;
            case "reset":
                Reset();
                con.Print("reset");
                break;
            case "set":
                SetCommand(args, con);
                break;
            case "cam":
                CameraCommand(args, con);
                break;
            case "panel":
                PanelShown = args.Count < 2 || args[1].Equals("on", StringComparison.OrdinalIgnoreCase);
                con.Print(PanelShown ? "panel shown" : "panel hidden");
                break;
            case "shot":
                ShotCommand(args, con);
                break;
            case "key":
                KeyCommand(args, con);
                break;
            default:
                con.Error("usage: photo on|off|toggle | get | reset | set <name> <value> | cam <x> <y> <z> <yawDeg> <pitchDeg> | panel on|off | shot [file] [--ui] | key <name> [up]");
                break;
        }
    }

    string Describe() => string.Join(", ", Params.Select(p => $"{p.Name} {ValueText(p)}"))
        + $", eye {Fmt(_host.Camera.Eye.X)} {Fmt(_host.Camera.Eye.Y)} {Fmt(_host.Camera.Eye.Z)}";

    static string Fmt(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    void SetCommand(IReadOnlyList<string> args, DevConsole con)
    {
        if (args.Count < 3 || Params.FirstOrDefault(p => p.Name.Equals(args[1], StringComparison.OrdinalIgnoreCase)) is not { } p)
        {
            con.Error($"usage: photo set <{string.Join("|", Params.Select(q => q.Name))}> <value> (hour game: the game's time; focus auto)");
            return;
        }
        string value = args[2];
        if (p.Name == "hour" && value.Equals("game", StringComparison.OrdinalIgnoreCase)) UseGameTime();
        else if (p.Name == "focus" && value.Equals("auto", StringComparison.OrdinalIgnoreCase)) _autoFocus = true;
        else if (!TryParse(p, value, out float number))
        {
            con.Error($"'{value}' is no value for {p.Name}" + (p.Choices is { } c ? $" ({string.Join("|", c)})" : ""));
            return;
        }
        else p.Set(number);
        con.Print($"{p.Name} {ValueText(p)}");
    }

    static bool TryParse(Param p, string value, out float number)
    {
        if (p.Choices is { } choices)
        {
            int named = Array.FindIndex(choices, c => c.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (named >= 0)
            {
                number = named;
                return true;
            }
            string lower = value.ToLowerInvariant();
            if (choices.Length == 2 && lower is "true" or "yes")
            {
                number = 1;
                return true;
            }
            if (choices.Length == 2 && lower is "false" or "no")
            {
                number = 0;
                return true;
            }
        }
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && float.IsFinite(number);
    }

    void CameraCommand(IReadOnlyList<string> args, DevConsole con)
    {
        var n = new float[5];
        bool ok = args.Count == 6;
        for (int i = 0; ok && i < 5; i++) ok = float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out n[i]);
        if (!ok)
        {
            con.Error("usage: photo cam <x> <y> <z> <yawDeg> <pitchDeg> (yaw 0 looks along -Z, pitch positive looks down)");
            return;
        }
        var rig = _host.Session.Camera;
        rig.Free.Place(new Vector3(n[0], n[1], n[2]), n[3] * MathF.PI / 180, n[4] * MathF.PI / 180);
        rig.TakeOver(rig.Free.State, strategy: false);
        con.Print("camera placed");
    }

    /// <summary>
    /// <c>photo key &lt;name&gt; [up]</c>: a key goes down (or up) as the keyboard's would, for the control channel (a flight is a key held for a
    /// while).
    /// </summary>
    void KeyCommand(IReadOnlyList<string> args, DevConsole con)
    {
        if (args.Count < 2 || !Enum.TryParse(args[1], true, out Key key))
        {
            con.Error("usage: photo key <Key name, as W, Space, PageUp, Enter> [up]");
            return;
        }
        if (args.Count > 2 && args[2].Equals("up", StringComparison.OrdinalIgnoreCase)) _host.Session.Input.SetKey(key, false);
        else KeyDown(key);
        con.Print($"{key} {(args.Count > 2 ? "up" : "down")}");
    }

    void ShotCommand(IReadOnlyList<string> args, DevConsole con)
    {
        bool ui = args.Skip(1).Contains("--ui");
        string? file = args.Skip(1).FirstOrDefault(a => a != "--ui");
        string? path = TakePicture(file, ui, answerControl: file is not null);
        if (path is null) con.Error("no picture can be taken now");
        else con.Print($"queued {path}");
    }

    #endregion
}
