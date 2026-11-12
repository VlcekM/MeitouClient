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
    Meitou.Simulation.Session.SimulationClock.SpeedState _clock;
    PhotoControls _controls = new();

    /// <summary>
    /// The lens and picture settings (units as <see cref="Params"/> shows them).
    /// </summary>
    bool _dof, _autoFocus = true;
    /// <summary>
    /// The characters' speech bubbles are drawn (hidden on entering: a picture is of the scene).
    /// </summary>
    bool _dialogue;
    /// <summary>
    /// Metres each side of the focus that are wholly sharp (several figures at different distances in focus at a wide aperture).
    /// </summary>
    float _sharpZone;
    /// <summary>
    /// Where in the picture the autofocus looks (0..1 from the top left; the centre until a click moves it).
    /// </summary>
    Vector2 _focusSpot = new(0.5f);
    /// <summary>
    /// The simulation's speed while it runs in the mode (<see cref="SetGameSpeed"/>).
    /// </summary>
    float _gameSpeed = 1;
    const float MinGameSpeed = 0.05f, MaxGameSpeed = 5;
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
        bool Log = false, string[]? Choices = null, Func<bool>? Enabled = null, string Group = "");

    /// <summary>
    /// The panel's tabs, in order: each setting's <see cref="Param.Group"/> is one of them.
    /// </summary>
    internal const string CameraGroup = "Camera", FocusGroup = "Focus", LightGroup = "Light", PictureGroup = "Composition", TimeGroup = "Time",
        PresetGroup = "Presets";
    internal static readonly string[] Groups = [CameraGroup, FocusGroup, LightGroup, PictureGroup, TimeGroup, PresetGroup];

    /// <summary>
    /// The settings in the panel's order.
    /// </summary>
    internal IReadOnlyList<Param> Params { get; }

    Param[] BuildParams() =>
    [
        new("fov", "Field of view", 10, 100, () => _host.Camera.FieldOfView * 180 / MathF.PI, v => _host.Camera.FieldOfView = v * MathF.PI / 180,
            v => $"{v:0} deg", Group: CameraGroup),
        new("roll", "Roll", -90, 90, () => _host.Session.Camera.Free.Roll * 180 / MathF.PI,
            v => _host.Session.Camera.Free.Roll = Math.Clamp(v, -90f, 90f) * MathF.PI / 180, v => $"{v:0.0} deg", Group: CameraGroup),
        new("dof", "Depth of field", 0, 1, () => _dof ? 1 : 0, v => _dof = v >= 0.5f, v => v >= 0.5f ? "on" : "off", Choices: ["off", "on"], Group: FocusGroup),
        new("focusmode", "Focus", 0, 1, () => _autoFocus ? 1 : 0, v => _autoFocus = v >= 0.5f, v => v >= 0.5f ? "auto (centre)" : "manual",
            Choices: ["manual", "auto"], Group: FocusGroup),
        new("focus", "Focus distance", 0.3f, 1000, () => _focusMetres, v => (_focusMetres, _autoFocus) = (Math.Clamp(v, 0.3f, 1000), false),
            v => $"{v:0.0} m", Log: true, Enabled: () => !_autoFocus, Group: FocusGroup),
        new("aperture", "Aperture", 0.25f, 22, () => _aperture, v => _aperture = Math.Clamp(MathF.Round(v * 20) / 20, 0.25f, 22),
            v => $"f/{v:0.0#}", Log: true, Group: FocusGroup),
        new("sharpzone", "Sharp zone", 0, 30, () => _sharpZone, v => _sharpZone = Math.Clamp(MathF.Round(v * 10) / 10, 0, 30), v => $"{v:0.0} m",
            Group: FocusGroup),
        new("exposure", "Exposure", -3, 3, () => _exposureStops, v => _exposureStops = Math.Clamp(MathF.Round(v * 10) / 10, -3, 3),
            v => $"{v:+0.0;-0.0;0.0} EV", Group: LightGroup),
        new("hour", "Time of day", 0, 24 - 1 / 60f, () => _host.PhotoHour ?? (float)_host.Session.Clock.HourOfDay,
            v => _host.PhotoHour = Math.Clamp(MathF.Round(v * 60) / 60, 0, 24 - 1 / 60f), WorldFrame.TimeText, Group: LightGroup),
        new("grid", "Grid", 0, 4, () => (float)Grid, v => Grid = (PhotoGrid)Math.Clamp((int)MathF.Round(v), 0, 4), v => Grid.ToString(),
            Choices: ["off", "thirds", "golden", "cross", "diagonals"], Group: PictureGroup),
        new("frame", "Frame", 0, 5, () => (float)Frame, v => Frame = (PhotoFrame)Math.Clamp((int)MathF.Round(v), 0, 5), v => Frame.ToString(),
            Choices: ["off", "2.39:1", "16:9", "4:3", "1:1", "4:5"], Group: PictureGroup),
        new("dialogue", "Dialogue", 0, 1, () => _dialogue ? 1 : 0, v => _dialogue = v >= 0.5f, v => v >= 0.5f ? "shown" : "hidden",
            Choices: ["hidden", "shown"], Group: PictureGroup),
        new("speed", "Fly speed", 0.5f, 3000, () => _controls.FlySpeed / 10, v => _controls.FlySpeed = Math.Clamp(v * 10, PhotoControls.MinSpeed,
            PhotoControls.MaxSpeed), v => $"{v:0.#} m/s", Log: true, Group: CameraGroup),
        new("live", "Simulation", 0, 1, () => _host.Session.Simulation.IsPaused ? 0 : 1, SetLive, v => v >= 0.5f ? "running" : "paused",
            Choices: ["paused", "running"], Group: TimeGroup),
        new("gamespeed", "Game speed", MinGameSpeed, MaxGameSpeed, () => _gameSpeed, SetGameSpeed, v => $"x{v:0.##}", Log: true, Group: TimeGroup),
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
        clock.SetSpeed(value >= 0.5f ? _gameSpeed : 0);
    }

    /// <summary>
    /// The speed the simulation runs at while the mode lets it run: slow motion below 1 (to pick the instant of a strike), the game's fast speeds above.
    /// Steps of a hundredth, and 1 exactly within five hundredths of it.
    /// </summary>
    void SetGameSpeed(float value)
    {
        float speed = Math.Clamp(MathF.Round(value * 100) / 100, MinGameSpeed, MaxGameSpeed);
        _gameSpeed = MathF.Abs(speed - 1) <= 0.05f ? 1 : speed;
        var clock = _host.Session.Simulation;
        if (!clock.IsPaused) clock.SetSpeed(_gameSpeed);
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
        (_dialogue, _gameSpeed, _sharpZone, _focusSpot) = (false, 1, 0, new Vector2(0.5f));
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
        _clock = clock.SaveSpeeds();
        rig.TakeOver(new CameraState(cam.Eye, cam.Forward, Vector3.UnitY, cam.Target, cam.Yaw, cam.Pitch, cam.Distance, true), strategy: false);
        _controls = rig.Free.Photo = new PhotoControls();
        var options = _host.WorldOptions.Post;
        (_entryFieldOfView, _entryDof, _entryAperture) = (cam.FieldOfView, options.DepthOfField, options.DofAperture);
        Reset();
        Remembered();
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
        Remember();   // before the lens and the clock go back
        var clock = _host.Session.Simulation;
        _host.Session.Camera.Free.Photo = null;
        _host.Session.Camera.Free.Roll = 0;
        _host.Session.Camera.Restore(_rig);
        _host.Camera.FieldOfView = _fieldOfView;
        clock.RestoreSpeeds(_clock);
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
        (options.PhotoExposure, options.PhotoDepthOfField, options.LensSharpZone, options.LensFocusSpot) = (1, null, 0, new Vector2(0.5f));
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
        options.LensSharpZone = _sharpZone * 10;
        options.LensFocusSpot = _focusSpot;
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
        if (Naming is not null)
        {
            // A name is being typed: Enter keeps it, Esc drops it, Backspace takes a letter back (all of a proposed name); no other key acts.
            if (key == Key.Enter) CommitNaming();
            else if (escape || key == Key.Escape) CancelNaming();
            else if (key == Key.Backspace) Naming = _namingFresh || Naming.Length == 0 ? "" : Naming[..^1];
            _namingFresh = false;
            return true;
        }
        if (toggle || escape) Leave();
        else if (bindings.Get(InputAction.ToggleInterface).Contains(Binding.Of(key))) PanelShown = !PanelShown;
        else if (key == Key.Enter || bindings.Get(InputAction.Screenshot).Contains(Binding.Of(key))) TakePicture();
        else if (key is Key.PageUp or Key.PageDown) StepLens(key == Key.PageUp ? -2.5f : 2.5f);
        else if (key == Key.V) _host.Session.Camera.Free.Roll = 0;
        else if (key is Key.Tab or Key.H) PanelShown = !PanelShown;
        else if (key == Key.G) Grid = (PhotoGrid)(((int)Grid + 1) % 5);
        else if (bindings.Get(InputAction.Pause).Contains(Binding.Of(key))) SetLive(_host.Session.Simulation.IsPaused ? 1 : 0);
        else if (FliesCamera(key)) _host.Session.Input.SetKey(key, true);
        return true;
    }

    /// <summary>
    /// The keys the photo camera reads itself (<see cref="FreeCamera.Update"/>): W A S D and the arrows, Q and E, the roll keys, Shift and Ctrl.
    /// </summary>
    static bool FliesCamera(Key key) => key is Key.W or Key.A or Key.S or Key.D or Key.Q or Key.E or Key.Z or Key.X or Key.Up or Key.Down or Key.Left or Key.Right
        or Key.LeftShift or Key.RightShift or Key.LeftControl or Key.RightControl;

    /// <summary>
    /// PageUp zooms the lens in (a narrower view), PageDown out, 2.5 degrees a press.
    /// </summary>
    void StepLens(float degrees) =>
        _host.Camera.FieldOfView = Math.Clamp(_host.Camera.FieldOfView + degrees * MathF.PI / 180, 10 * MathF.PI / 180, 100 * MathF.PI / 180);

    /// <summary>
    /// The wheel over the picture: the lens, 8 % a notch (away from the user zooms in), or the fly speed with Ctrl.
    /// </summary>
    public void Wheel(float notches, bool speed)
    {
        if (speed) _controls.FlySpeed = Math.Clamp(_controls.FlySpeed * MathF.Pow(1.2f, notches), PhotoControls.MinSpeed, PhotoControls.MaxSpeed);
        else _host.Camera.FieldOfView = Math.Clamp(_host.Camera.FieldOfView * MathF.Pow(0.92f, notches), 10 * MathF.PI / 180, 100 * MathF.PI / 180);
    }

    /// <summary>
    /// A left click on the picture: the autofocus looks there from now on (a place in the picture, 0..1 from its top left), with the depth of field on.
    /// </summary>
    public void FocusAt(Vector2 place)
    {
        _focusSpot = Vector2.Clamp(place, new Vector2(0.02f), new Vector2(0.98f));
        (_dof, _autoFocus) = (true, true);
        Say("Focus point set");
    }

    /// <summary>
    /// Where the autofocus looks, while it is what focuses the lens (the panel marks it); null otherwise.
    /// </summary>
    public Vector2? FocusSpot => Active && _dof && _autoFocus ? _focusSpot : null;

    #endregion

    #region Remembered settings and presets

    readonly PhotoPresets _store = new();

    /// <summary>
    /// The settings a new session of the mode does not start with: the roll and the drawn hour belong to one picture, the simulation starts paused.
    /// </summary>
    internal static readonly string[] NotRemembered = ["roll", "hour", "live"];

    internal IReadOnlyList<PhotoPresets.Preset> Presets => _store.Presets;

    /// <summary>
    /// The settings by their <c>photo set</c> names, and the focus point: all but the simulation's state for a preset (the hour only while one is
    /// drawn), without <see cref="NotRemembered"/> for the next session.
    /// </summary>
    Dictionary<string, float> Values(bool preset)
    {
        var values = new Dictionary<string, float>();
        foreach (var p in Params)
        {
            if (p.Name == "live" || !preset && NotRemembered.Contains(p.Name)) continue;
            if (p.Name == "hour" && _host.PhotoHour is null) continue;
            values[p.Name] = p.Get();
        }

        (values["spotx"], values["spoty"]) = (_focusSpot.X, _focusSpot.Y);
        return values;
    }

    void Take(IReadOnlyDictionary<string, float> values, bool preset)
    {
        // The focus mode last: setting a focus distance turns the autofocus off.
        foreach (var p in Params)
            if (p.Name is not ("live" or "focusmode") && values.TryGetValue(p.Name, out float v)) p.Set(v);
        if (values.TryGetValue("focusmode", out float auto)) _autoFocus = auto >= 0.5f;
        if (preset && !values.ContainsKey("hour")) UseGameTime();
        if (values.TryGetValue("spotx", out float x) && values.TryGetValue("spoty", out float y)) _focusSpot = new Vector2(x, y);
    }

    /// <summary>
    /// On entering: the settings the mode was left with (the file is read each time: another run may have written it).
    /// </summary>
    void Remembered()
    {
        _store.Load();
        Take(_store.Last, preset: false);
    }

    /// <summary>
    /// On leaving: the settings are kept for the next time.
    /// </summary>
    void Remember()
    {
        _store.Last = Values(preset: false);
        _store.Save();
    }

    /// <summary>
    /// The name being typed for a preset (the panel shows it in a field), null when none is: a new preset's (<see cref="_namingIndex"/> -1, proposed
    /// as the first free "Preset n") or a kept one's new name. The first letter typed replaces the proposed name.
    /// </summary>
    public string? Naming { get; private set; }
    int _namingIndex;
    bool _namingFresh;
    const int NameLength = 28;

    string FreeName() => Enumerable.Range(1, 999).Select(i => $"Preset {i}").First(n => _store.Presets.All(p => p.Name != n));

    /// <summary>
    /// Starts typing a name: for a new preset of the settings as they are, or for the kept preset <paramref name="index"/>.
    /// </summary>
    public void BeginNaming(int index = -1)
    {
        if (index >= _store.Presets.Count) return;
        (_namingIndex, _namingFresh) = (index, true);
        Naming = index < 0 ? FreeName() : _store.Presets[index].Name;
        foreach (var key in Enum.GetValues<Key>()) if (FliesCamera(key)) _host.Session.Input.SetKey(key, false);   // the camera stops while the keys type
    }

    /// <summary>
    /// A typed character goes into the name being typed; false when none is.
    /// </summary>
    public bool TextInput(char c)
    {
        if (!Active || Naming is null) return false;
        if (char.IsControl(c)) return true;
        if (_namingFresh) Naming = "";
        _namingFresh = false;
        if (Naming.Length < NameLength) Naming += c;
        return true;
    }

    /// <summary>
    /// Keeps the typed name: the new preset is saved under it (one of that name is replaced), or the kept preset takes it (unless another has it).
    /// </summary>
    public void CommitNaming()
    {
        if (Naming is not { } typed) return;
        string name = typed.Trim();
        int index = _namingIndex;
        Naming = null;
        if (index < 0) SavePreset(name);
        else if (index < _store.Presets.Count && name.Length > 0)
        {
            if (_store.Presets.Where((p, i) => i != index).Any(p => p.Name == name)) { Say($"{name} exists already"); return; }
            _store.Presets[index].Name = name;
            _store.Save();
            Say($"Renamed to {name}");
        }
    }

    public void CancelNaming() => Naming = null;

    /// <summary>
    /// Opens the folder the pictures are saved to in the system's file browser (made if it is not there yet).
    /// </summary>
    public void OpenGallery()
    {
        string folder = Path.GetDirectoryName(DefaultPath())!;
        try
        {
            Directory.CreateDirectory(folder);
            if (_host.WorldOptions.Headless) { Say(folder); return; }   // no desktop to open it on
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = folder, UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Say($"Cannot open {folder}: {e.Message}");
        }
    }

    /// <summary>
    /// Keeps the settings as a preset: under <paramref name="name"/> (replacing one of that name), or as the first free "Preset n".
    /// </summary>
    public void SavePreset(string? name = null)
    {
        name = string.IsNullOrWhiteSpace(name) ? FreeName() : name.Trim();
        var values = Values(preset: true);
        if (_store.Presets.FirstOrDefault(p => p.Name == name) is { } old) old.Values = values;
        else _store.Presets.Add(new PhotoPresets.Preset { Name = name, Values = values });
        _store.Save();
        Say($"Saved {name}");
    }

    public void LoadPreset(int index)
    {
        if (index < 0 || index >= _store.Presets.Count) return;
        Take(_store.Presets[index].Values, preset: true);
        Say(_store.Presets[index].Name);
    }

    public void DeletePreset(int index)
    {
        if (index < 0 || index >= _store.Presets.Count) return;
        string name = _store.Presets[index].Name;
        _store.Presets.RemoveAt(index);
        _store.Save();
        Say($"Deleted {name}");
    }

    void PresetCommand(IReadOnlyList<string> args, DevConsole con)
    {
        string verb = args.Count > 1 ? args[1].ToLowerInvariant() : "list";
        string name = string.Join(' ', args.Skip(2));
        int index = int.TryParse(name, out int n) ? n - 1 : _store.Presets.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        switch (verb)
        {
            case "save":
                SavePreset(name);
                con.Print($"{_store.Presets.Count} presets");
                break;
            case "load" when index >= 0 && index < _store.Presets.Count:
                LoadPreset(index);
                con.Print($"loaded {_store.Presets[index].Name}");
                break;
            case "delete" when index >= 0 && index < _store.Presets.Count:
                DeletePreset(index);
                con.Print($"{_store.Presets.Count} presets");
                break;
            case "list":
                con.Print(_store.Presets.Count == 0 ? "no preset" : string.Join(", ", _store.Presets.Select((p, i) => $"{i + 1} {p.Name}")));
                break;
            case "name":   // as the panel's Save preset (no argument) or a right click on preset n: the name is then typed (photo type, photo key Enter)
                if (name.Length > 0 && (index < 0 || index >= _store.Presets.Count)) { con.Error("no such preset"); break; }
                BeginNaming(name.Length > 0 ? index : -1);
                con.Print($"naming: {Naming}");
                break;
            default:
                con.Error("usage: photo preset save [name] | load <n|name> | delete <n|name> | name [n|name] | list");
                break;
        }
    }

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
            case "preset":
                PresetCommand(args, con);
                break;
            case "shot":
                ShotCommand(args, con);
                break;
            case "key":
                KeyCommand(args, con);
                break;
            case "type":   // characters as the keyboard types them (a preset's name)
                foreach (char c in string.Join(' ', args.Skip(1))) TextInput(c);
                con.Print(Naming is null ? "no name is being typed" : $"naming: {Naming}");
                break;
            case "gallery":
                OpenGallery();
                con.Print(Path.GetDirectoryName(DefaultPath())!);
                break;
            default:
                con.Error("usage: photo on|off|toggle | get | reset | set <name> <value> | cam <x> <y> <z> <yawDeg> <pitchDeg> | panel on|off | shot [file] [--ui] | key <name> [up] | preset ... | type <text> | gallery");
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
