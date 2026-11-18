using System.Globalization;
using System.Numerics;
using Meitou.Game.DevTools;
using Meitou.Game.Host.Cameras;
using Meitou.Game.Host.Input;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Post;
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

    /// <summary>
    /// The game the mode works on.
    /// </summary>
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
    /// The composition grid.
    /// </summary>
    public PhotoGrid Grid = PhotoGrid.Thirds;

    /// <summary>
    /// The frame bars.
    /// </summary>
    public PhotoFrame Frame = PhotoFrame.Off;

    /// <summary>
    /// Set by the loop: whether photo mode may start now (not over the title, a loading cover, a cinema flight or a squad tour).
    /// </summary>
    public Func<bool> Allowed = () => true;

    /// <summary>
    /// Set by the loop: what entering does first (lets the cinema's held camera go).
    /// </summary>
    public Action Entering = () => { };

    /// <summary>
    /// The camera rig as entering found it, which leaving puts back.
    /// </summary>
    CameraRig.Saved _rig;

    /// <summary>
    /// The character the camera followed on entering.
    /// </summary>
    CharacterId? _follow;

    /// <summary>
    /// The interface was hidden on entering.
    /// </summary>
    bool _interfaceWasHidden;

    /// <summary>
    /// The field of view on entering, which leaving puts back.
    /// </summary>
    float _fieldOfView;

    /// <summary>
    /// The simulation's speeds on entering, which leaving puts back.
    /// </summary>
    Meitou.Simulation.Session.SimulationClock.SpeedState _clock;

    /// <summary>
    /// The photo camera's fly controls (a new set on each entering).
    /// </summary>
    PhotoControls _controls = new();

    /// <summary>
    /// The depth of field is on (units of the settings as <see cref="Params"/> shows them).
    /// </summary>
    bool _dof;

    /// <summary>
    /// The lens focuses itself at <see cref="_focusSpot"/> rather than at <see cref="_focusMetres"/>.
    /// </summary>
    bool _autoFocus = true;

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

    /// <summary>
    /// The slowest game speed the mode runs the simulation at.
    /// </summary>
    const float MinGameSpeed = 0.05f;

    /// <summary>
    /// The fastest game speed the mode runs the simulation at.
    /// </summary>
    const float MaxGameSpeed = 5;

    /// <summary>
    /// The manual focus distance in metres.
    /// </summary>
    float _focusMetres = 10;

    /// <summary>
    /// The lens aperture (f-number).
    /// </summary>
    float _aperture = 1;

    /// <summary>
    /// The exposure compensation in stops.
    /// </summary>
    float _exposureStops;

    /// <summary>
    /// The most stops the exposure compensation goes either way.
    /// </summary>
    const float MaxStops = 5;

    /// <summary>
    /// The picture's size: an index into <see cref="PictureSizes"/>.
    /// </summary>
    int _pictureSize;

    /// <summary>
    /// What the drawn size is multiplied by for a picture: the screen's, then the world drawn again two and three times larger.
    /// </summary>
    static readonly int[] PictureSizes = [1, 2, 3];

    /// <summary>
    /// The longest side a larger picture is drawn at, in pixels (its targets stay within video memory; a larger factor is cut down to it).
    /// </summary>
    const int MaxPictureSide = 8192;

    /// <summary>
    /// The field of view the game had on entering, which <see cref="Reset"/> returns to.
    /// </summary>
    float _entryFieldOfView = 50 * MathF.PI / 180;

    /// <summary>
    /// The aperture the game had on entering, which <see cref="Reset"/> returns to.
    /// </summary>
    float _entryAperture = 1;

    /// <summary>
    /// The depth of field setting the game had on entering, which <see cref="Reset"/> returns to.
    /// </summary>
    bool _entryDof;

    /// <summary>
    /// The file a picture is being saved to (null when none is).
    /// </summary>
    string? _pending;

    /// <summary>
    /// Frames still to draw, with the saving indicator over them, before the picture's own frame is requested (the larger sizes draw the world again
    /// several times inside that one frame, which holds the window: the indicator must be on screen before it starts).
    /// </summary>
    int _shotDelay;

    /// <summary>
    /// How many frames the indicator is drawn before the picture's own frame.
    /// </summary>
    const int ShotDelayFrames = 3;

    /// <summary>
    /// The picture being saved keeps the panel and grid (the control channel's <c>photo shot --ui</c>, for checking them).
    /// </summary>
    bool _shotWithGuides;

    /// <summary>
    /// The message shown over the picture for a while.
    /// </summary>
    string _message = "";

    /// <summary>
    /// When the message stops being shown (<see cref="Environment.TickCount64"/> milliseconds).
    /// </summary>
    long _messageUntil;

    /// <summary>
    /// The panel and guides drawn over the picture.
    /// </summary>
    public PhotoPanel Panel { get; }

    /// <summary>
    /// The characters' poses, places and visibility (the Pose tab).
    /// </summary>
    public PhotoPoses Poses { get; }

    /// <summary>
    /// The transform gizmo over the chosen light or character.
    /// </summary>
    public PhotoGizmo Gizmo { get; } = new();

    /// <summary>
    /// What the gizmo acts on now: the chosen light on the Light tab, the chosen character on the Pose tab; null elsewhere or with the panel hidden.
    /// </summary>
    public GizmoSubject? GizmoSubject => !Active || !PanelShown ? null
        : Panel.Group == LightGroup ? Lights.GizmoSubject()
        : Panel.Group == PoseGroup ? Poses.GizmoSubject()
        : null;

    /// <summary>
    /// Marks tied to places in the world are drawn over the picture (a light's mark, a gizmo). Frame generation is held off meanwhile: its
    /// generated frames lay the last real frame's interface over a moved picture, so the marks bounced a frame behind what they stand on while
    /// the camera moved (**Observed** with DLSS frame generation).
    /// </summary>
    public bool WorldMarksShown => Active && PanelShown && (GizmoSubject is not null || Lights.AnyMarked);

    /// <summary>
    /// The mode's own lights (the Light tab).
    /// </summary>
    public PhotoLights Lights { get; } = new();

    /// <summary>
    /// The filter's look, how much of it applies, and the saturation, contrast, temperature and tint on top (the Look tab).
    /// </summary>
    FilterLook _filter;

    /// <summary>
    /// How much of the filter's look applies, 0 to 1.
    /// </summary>
    float _filterStrength = 1;

    /// <summary>
    /// The filter's saturation and contrast factors (1 unchanged).
    /// </summary>
    float _saturation = 1, _contrast = 1;

    /// <summary>
    /// The filter's temperature and tint, -1 to 1 (0 unchanged).
    /// </summary>
    float _temperature, _tint;

    /// <summary>
    /// Real seconds that went by in the mode while the simulation stood paused: added to the game's motion clock, the hanging gear's swing
    /// (<c>GearSway</c>, which steps by that clock) goes on while a character is moved or posed on a paused simulation.
    /// </summary>
    public double MotionSeconds { get; private set; }

    /// <summary>
    /// The last <see cref="Apply"/>'s <see cref="System.Diagnostics.Stopwatch"/> time: the playing poses move on by the real time since.
    /// </summary>
    long _applied;

    /// <summary>
    /// The mode for <paramref name="host"/>, with its settings and panel built.
    /// </summary>
    public PhotoMode(GameHost host)
    {
        _host = host;
        Poses = new PhotoPoses(host);
        Params = BuildParams();
        Panel = new PhotoPanel(this);
    }

    #endregion

    #region Parameters

    /// <summary>
    /// The panel's Camera tab.
    /// </summary>
    internal const string CameraGroup = "Camera";

    /// <summary>
    /// The panel's Focus tab.
    /// </summary>
    internal const string FocusGroup = "Focus";

    /// <summary>
    /// The panel's Light tab.
    /// </summary>
    internal const string LightGroup = "Light";

    /// <summary>
    /// The panel's Composition tab.
    /// </summary>
    internal const string PictureGroup = "Composition";

    /// <summary>
    /// The panel's Pose tab.
    /// </summary>
    internal const string PoseGroup = "Pose";

    /// <summary>
    /// The panel's Look tab.
    /// </summary>
    internal const string LookGroup = "Look";

    /// <summary>
    /// The panel's Time tab.
    /// </summary>
    internal const string TimeGroup = "Time";

    /// <summary>
    /// The panel's Presets tab.
    /// </summary>
    internal const string PresetGroup = "Presets";

    /// <summary>
    /// The panel's tabs, in order: each setting's <see cref="Param.Group"/> is one of them.
    /// </summary>
    internal static readonly string[] Groups = [CameraGroup, FocusGroup, LightGroup, LookGroup, PictureGroup, PoseGroup, TimeGroup, PresetGroup];

    /// <summary>
    /// The Look tab's settings that are the graphics settings' own (Post FX's vignette and film grain): the tab is a shortcut to them, so a change
    /// there is the setting's and stays after the mode; presets, Reset and leaving leave them alone.
    /// </summary>
    internal static readonly string[] Shortcuts = ["vignette", "vignettestrength", "grain", "grainstrength"];

    /// <summary>
    /// Whether a setting belongs to the mode's remembered settings and presets: not the scene's own (the Pose tab, the lights), not the graphics
    /// settings' (<see cref="Shortcuts"/>), not the simulation's state.
    /// </summary>
    static bool Kept(Param p) => p.Name != "live" && p.Group != PoseGroup && !p.Name.StartsWith("light", StringComparison.Ordinal)
        && !Shortcuts.Contains(p.Name);

    /// <summary>
    /// One setting of the mode: a slider between <see cref="Min"/> and <see cref="Max"/> (<see cref="Log"/>: by the ratio), or the named
    /// <see cref="Choices"/> (two are an off/on switch). <see cref="Name"/> is the <c>photo set</c> name.
    /// </summary>
    internal sealed record Param(string Name, string Label, float Min, float Max, Func<float> Get, Action<float> Set, Func<float, string> Text,
        bool Log = false, string[]? Choices = null, Func<bool>? Enabled = null, string Group = "");

    /// <summary>
    /// The settings in the panel's order.
    /// </summary>
    internal IReadOnlyList<Param> Params { get; }

    /// <summary>
    /// The text of a setting's current value.
    /// </summary>
    internal static string ValueText(Param p) => p.Choices is { } c ? c[Math.Clamp((int)MathF.Round(p.Get()), 0, c.Length - 1)] : p.Text(p.Get());

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
        (Grid, Frame, _pictureSize) = (PhotoGrid.Thirds, PhotoFrame.Off, 0);
        (_dialogue, _gameSpeed, _sharpZone, _focusSpot) = (false, 1, 0, new Vector2(0.5f));
        (_filter, _filterStrength, _saturation, _contrast, _temperature, _tint) = (FilterLook.None, 1, 1, 1, 0, 0);
        Lights.Clear();
        _controls.FlySpeed = 300;
        Poses.Clear();
        SetLive(0);
    }

    /// <summary>
    /// The settings, each with how it reads and writes the camera, the lens, the clock or the mode's own state.
    /// </summary>
    Param[] BuildParams() =>
    [
        new("fov", "Field of view", 10, 100, () => _host.Camera.FieldOfView * 180 / MathF.PI, v => _host.Camera.FieldOfView = v * MathF.PI / 180,
            v => $"{v:0} deg", Group: CameraGroup),
        new("roll", "Roll", -90, 90, () => _host.Session.Camera.Free.Roll * 180 / MathF.PI,
            v => _host.Session.Camera.Free.Roll = Math.Clamp(v, -90f, 90f) * MathF.PI / 180, v => $"{v:0.0} deg", Group: CameraGroup),
        new("dof", "Depth of field", 0, 1, () => _dof ? 1 : 0, v => _dof = v >= 0.5f, v => v >= 0.5f ? "on" : "off", Choices: ["off", "on"],
            Group: FocusGroup),
        new("focusmode", "Focus", 0, 1, () => _autoFocus ? 1 : 0, v => _autoFocus = v >= 0.5f, v => v >= 0.5f ? "auto (centre)" : "manual",
            Choices: ["manual", "auto"], Group: FocusGroup),
        new("focus", "Focus distance", 0.3f, 1000, () => _focusMetres, v => (_focusMetres, _autoFocus) = (Math.Clamp(v, 0.3f, 1000), false),
            v => $"{v:0.0} m", Log: true, Enabled: () => !_autoFocus, Group: FocusGroup),
        new("aperture", "Aperture", 0.25f, 22, () => _aperture, v => _aperture = Math.Clamp(MathF.Round(v * 20) / 20, 0.25f, 22),
            v => $"f/{v:0.0#}", Log: true, Group: FocusGroup),
        new("sharpzone", "Sharp zone", 0, 30, () => _sharpZone, v => _sharpZone = Math.Clamp(MathF.Round(v * 10) / 10, 0, 30), v => $"{v:0.0} m",
            Group: FocusGroup),
        new("exposure", "Exposure", -MaxStops, MaxStops, () => _exposureStops,
            v => _exposureStops = Math.Clamp(MathF.Round(v * 10) / 10, -MaxStops, MaxStops), v => $"{v:+0.0;-0.0;0.0} EV", Group: LightGroup),
        new("hour", "Time of day", 0, 24 - 1 / 60f, () => _host.PhotoHour ?? (float)_host.Session.Clock.HourOfDay,
            v => _host.PhotoHour = Math.Clamp(MathF.Round(v * 60) / 60, 0, 24 - 1 / 60f), WorldFrame.TimeText, Group: LightGroup),
        new("grid", "Grid", 0, 4, () => (float)Grid, v => Grid = (PhotoGrid)Math.Clamp((int)MathF.Round(v), 0, 4), v => Grid.ToString(),
            Choices: ["off", "thirds", "golden", "cross", "diagonals"], Group: PictureGroup),
        new("frame", "Frame", 0, 5, () => (float)Frame, v => Frame = (PhotoFrame)Math.Clamp((int)MathF.Round(v), 0, 5), v => Frame.ToString(),
            Choices: ["off", "2.39:1", "16:9", "4:3", "1:1", "4:5"], Group: PictureGroup),
        new("dialogue", "Dialogue", 0, 1, () => _dialogue ? 1 : 0, v => _dialogue = v >= 0.5f, v => v >= 0.5f ? "shown" : "hidden",
            Choices: ["hidden", "shown"], Group: PictureGroup),
        new("size", "Picture size", 0, PictureSizes.Length - 1, () => _pictureSize,
            v => _pictureSize = Math.Clamp((int)MathF.Round(v), 0, PictureSizes.Length - 1), v => PictureSizes[_pictureSize].ToString(),
            Choices: ["screen", "x2", "x3"], Group: PictureGroup),
        new("speed", "Fly speed", 0.5f, 3000, () => _controls.FlySpeed / 10, v => _controls.FlySpeed = Math.Clamp(v * 10, PhotoControls.MinSpeed,
            PhotoControls.MaxSpeed), v => $"{v:0.#} m/s", Log: true, Group: CameraGroup),
        new("live", "Simulation", 0, 1, () => Running ? 1 : 0, SetLive, v => v >= 0.5f ? "running" : "paused", Choices: ["paused", "running"],
            Group: TimeGroup),
        new("gamespeed", "Game speed", MinGameSpeed, MaxGameSpeed, () => _gameSpeed, SetGameSpeed, v => $"x{v:0.##}", Log: true, Group: TimeGroup),
        .. PoseParams(),
        .. LightParams(),
        .. LookParams(),
    ];

    /// <summary>
    /// The Light tab's settings of the chosen light (<see cref="PhotoLights"/>): on or off, placed or carried with the camera, point or spot, its
    /// colour temperature, power, reach and a spot's cone.
    /// </summary>
    Param[] LightParams()
    {
        bool Lit() => Lights.On;
        return
        [
            new("lighton", "Light", 0, 1, () => Lights.On ? 1 : 0, v => Lights.On = v >= 0.5f, v => v >= 0.5f ? "on" : "off", Choices: ["off", "on"],
                Group: LightGroup),
            new("lightmode", "Stays", 0, 1, () => Lights.Carried ? 1 : 0, v => Lights.Carried = v >= 0.5f, v => "", Choices: ["where placed", "with camera"],
                Enabled: Lit, Group: LightGroup),
            new("lighttype", "Kind", 0, 1, () => Lights.Spot ? 1 : 0, v => Lights.Spot = v >= 0.5f, v => "", Choices: ["point", "spot"], Enabled: Lit,
                Group: LightGroup),
            new("lightcolour", "Colour", 1500, 12000, () => Lights.Kelvin, v => Lights.Kelvin = MathF.Round(v / 50) * 50, v => $"{v:0} K", Log: true,
                Enabled: Lit, Group: LightGroup),
            new("lightpower", "Brightness", 0.05f, 20, () => Lights.Brightness, v => Lights.Brightness = MathF.Round(v * 100) / 100, v => $"{v:0.##}",
                Log: true, Enabled: Lit, Group: LightGroup),
            new("lightreach", "Reach", 1, 100, () => Lights.Reach / 10, v => Lights.Reach = MathF.Round(v * 10), v => $"{v:0.#} m", Log: true,
                Enabled: Lit, Group: LightGroup),
            new("lightcone", "Cone", 5, 150, () => Lights.Cone, v => Lights.Cone = MathF.Round(v), v => $"{v:0} deg", Enabled: () => Lit() && Lights.Spot,
                Group: LightGroup),
        ];
    }

    /// <summary>
    /// The Look tab's settings: the filter (a look, how much of it, then saturation, contrast, temperature and tint), and the shortcuts to the graphics
    /// settings' vignette and film grain (<see cref="Shortcuts"/>).
    /// </summary>
    Param[] LookParams()
    {
        var post = _host.WorldOptions.Post;
        static float Hundredth(float v) => MathF.Round(v * 100) / 100;
        return
        [
            new("filter", "Filter", 0, 8, () => (float)_filter, v => _filter = (FilterLook)Math.Clamp((int)MathF.Round(v), 0, 8), v => "",
                Choices: ["none", "black & white", "noir", "sepia", "warm", "cool", "faded", "vivid", "bleach"], Group: LookGroup),
            new("filterstrength", "Filter strength", 0, 1, () => _filterStrength, v => _filterStrength = Hundredth(Math.Clamp(v, 0, 1)),
                v => $"{v * 100:0} %", Enabled: () => _filter != FilterLook.None, Group: LookGroup),
            new("saturation", "Saturation", 0, 2, () => _saturation, v => _saturation = Hundredth(Math.Clamp(v, 0, 2)), v => $"x{v:0.00}",
                Group: LookGroup),
            new("contrast", "Contrast", 0.5f, 1.5f, () => _contrast, v => _contrast = Hundredth(Math.Clamp(v, 0.5f, 1.5f)), v => $"x{v:0.00}",
                Group: LookGroup),
            new("temperature", "Temperature", -1, 1, () => _temperature, v => _temperature = Hundredth(Math.Clamp(v, -1, 1)),
                v => v > 0 ? $"warmer {v * 100:0}" : v < 0 ? $"cooler {-v * 100:0}" : "neutral", Group: LookGroup),
            new("tint", "Tint", -1, 1, () => _tint, v => _tint = Hundredth(Math.Clamp(v, -1, 1)),
                v => v > 0 ? $"magenta {v * 100:0}" : v < 0 ? $"green {-v * 100:0}" : "neutral", Group: LookGroup),
            new("vignette", "Vignette", 0, 1, () => post.Vignette ? 1 : 0, v => post.Vignette = v >= 0.5f, v => "", Choices: ["off", "on"],
                Group: LookGroup),
            new("vignettestrength", "Vignette strength", 0, 1, () => post.VignetteStrength, v => post.VignetteStrength = MathF.Round(v * 20) / 20,
                v => $"{v:0.00}", Enabled: () => post.Vignette, Group: LookGroup),
            new("grain", "Film grain", 0, 1, () => post.FilmGrain ? 1 : 0, v => post.FilmGrain = v >= 0.5f, v => "", Choices: ["off", "on"],
                Group: LookGroup),
            new("grainstrength", "Grain strength", 0, 1, () => post.GrainStrength, v => post.GrainStrength = MathF.Round(v * 20) / 20,
                v => $"{v:0.00}", Enabled: () => post.FilmGrain, Group: LookGroup),
        ];
    }

    /// <summary>
    /// Places the chosen light where the camera stands, pointing where it looks, and turns it on.
    /// </summary>
    public void PlaceLight()
    {
        Lights.PlaceAt(_host.Camera.Eye, _host.Camera.Forward);
        Say($"{Lights.ChosenName} placed here");
    }

    /// <summary>
    /// The Pose tab's settings, on the chosen character (none acts without one; the pose's own only with a pose): the moment of the pose, whether
    /// it plays and how fast, the moves from its place (metres along its own facing), its turn, its weapon, and whether it and the others are drawn.
    /// </summary>
    Param[] PoseParams()
    {
        bool Chosen() => Poses.Selected is not null;
        bool Posed() => Poses.Pose is not null;
        return
        [
            new("posetime", "Pose moment", 0, 1, () => Poses.PoseTime.Time / Poses.PoseTime.Length, v => Poses.SetPoseTime(v * Poses.PoseTime.Length),
                v => $"{v * Poses.PoseTime.Length:0.00} s", Enabled: Posed, Group: PoseGroup),
            new("playback", "Playback", 0, 1, () => Poses.Playing ? 1 : 0, v => Poses.Playing = v >= 0.5f, v => v >= 0.5f ? "playing" : "held",
                Choices: ["held", "playing"], Enabled: Posed, Group: PoseGroup),
            new("posespeed", "Pose speed", 0.1f, 2, () => Poses.Speed, v => Poses.Speed = Math.Clamp(MathF.Round(v * 100) / 100, 0.1f, 2),
                v => $"x{v:0.##}", Log: true, Enabled: () => Posed() && Poses.Playing, Group: PoseGroup),
            new("forward", "Forward", -PhotoPoses.MoveRange / 10, PhotoPoses.MoveRange / 10, () => Poses.Forward / 10,
                v => Poses.Forward = MathF.Round(v * 100) / 10, v => $"{v:+0.0;-0.0;0.0} m", Enabled: Chosen, Group: PoseGroup),
            new("side", "Sideways", -PhotoPoses.MoveRange / 10, PhotoPoses.MoveRange / 10, () => Poses.Side / 10,
                v => Poses.Side = MathF.Round(v * 100) / 10, v => $"{v:+0.0;-0.0;0.0} m", Enabled: Chosen, Group: PoseGroup),
            new("height", "Height", PhotoPoses.LiftDown / 10, PhotoPoses.LiftUp / 10, () => Poses.Lift / 10,
                v => Poses.Lift = MathF.Round(v * 100) / 10, v => $"{v:+0.00;-0.00;0.00} m", Enabled: Chosen, Group: PoseGroup),
            new("turn", "Turn", -180, 180, () => Poses.TurnDegrees, v => Poses.TurnDegrees = MathF.Round(v), v => $"{v:+0;-0;0} deg", Enabled: Chosen,
                Group: PoseGroup),
            new("look", "Look at camera", 0, 1, () => Poses.Look ? 1 : 0, v => Poses.Look = v >= 0.5f, v => "", Choices: ["off", "head"],
                Enabled: () => Chosen() && Poses.SelectedIsPerson, Group: PoseGroup),
            new("weapon", "Weapon", 0, 3, () => Poses.Weapon, v => Poses.Weapon = Math.Clamp((int)MathF.Round(v), 0, 3), v => "",
                Choices: ["own", "sheathed", "drawn", "hidden"], Enabled: Chosen, Group: PoseGroup),
            new("hidden", "Character", 0, 1, () => Poses.Hidden ? 1 : 0, v => Poses.Hidden = v >= 0.5f, v => v >= 0.5f ? "hidden" : "shown",
                Choices: ["shown", "hidden"], Enabled: Chosen, Group: PoseGroup),
            new("others", "Others", 0, 1, () => Poses.OthersHidden ? 1 : 0, v => Poses.OthersHidden = v >= 0.5f, v => v >= 0.5f ? "hidden" : "shown",
                Choices: ["shown", "hidden"], Group: PoseGroup),
        ];
    }

    /// <summary>
    /// The mode lets the simulation run: its own speed, not <see cref="Meitou.Simulation.Session.SimulationClock.IsPaused"/>, which also holds the
    /// game's requested pause.
    /// </summary>
    bool Running => _host.Session.Simulation.Speed > 0;

    /// <summary>
    /// Lets the simulation run (so a pose can be chosen) or pauses it again.
    /// </summary>
    void SetLive(float value)
    {
        var clock = _host.Session.Simulation;
        clock.SetSpeed(value >= 0.5f ? _gameSpeed : 0);
    }

    /// <summary>
    /// The speed the simulation runs at while the mode lets it run: slow motion below 1 (to pick the instant of a strike), the game's fast speeds
    /// above. Steps of a hundredth, and 1 exactly within five hundredths of it.
    /// </summary>
    void SetGameSpeed(float value)
    {
        float speed = Math.Clamp(MathF.Round(value * 100) / 100, MinGameSpeed, MaxGameSpeed);
        _gameSpeed = MathF.Abs(speed - 1) <= 0.05f ? 1 : speed;

        if (Running) _host.Session.Simulation.SetSpeed(_gameSpeed);
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

        Typing = null;

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

    /// <summary>
    /// Photo mode on or off.
    /// </summary>
    public void Toggle()
    {
        if (Active) Leave();
        else Enter();
    }

    /// <summary>
    /// Clears what the mode draws (exposure, depth of field, sharp zone, focus spot, roll, lens, hour, look, hidden speech bubbles) and the
    /// picture being saved; the mode is off.
    /// </summary>
    void End()
    {
        var options = _host.WorldOptions.Post;
        (options.PhotoExposure, options.PhotoDepthOfField, options.LensSharpZone, options.LensFocusSpot) = (1, null, 0, new Vector2(0.5f));
        (_host.Camera.Roll, _host.Camera.FocusDistance, _host.Camera.Aperture) = (0, null, null);
        (_host.PhotoHour, _host.PhotoLook) = (null, false);
        _host.Player.SpeechBubbles.Hidden = false;
        _host.Gpu.MarkersHidden = false;
        (options.PhotoFilter, options.VignetteAspect, _host.Gpu.PhotoLights) = (null, 0, []);
        Poses.Clear();
        Lights.Clear();
        Gizmo.Release();

        Active = false;
        Typing = null;
        _pending = null;
        _shotDelay = 0;
        _shotWithGuides = false;
        _applied = 0;
        MotionSeconds = 0;
    }

    #endregion

    #region Each frame

    /// <summary>
    /// After the frame's camera is placed: the roll, lens, exposure, depth of field and hour for this frame's draw.
    /// </summary>
    public void Apply()
    {
        if (!Active) return;

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_applied != 0)
        {
            float elapsed = MathF.Min((float)System.Diagnostics.Stopwatch.GetElapsedTime(_applied, now).TotalSeconds, 0.25f);
            Poses.Advance(elapsed);
            if (!Running) MotionSeconds += elapsed;
        }

        _applied = now;
        StepShot();

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
        options.PhotoFilter = ColourFilter.Of(_filter, _filterStrength, _saturation, _temperature, _tint, _contrast);
        options.VignetteAspect = PhotoGuides.Aspect(Frame);
        _host.Gpu.PhotoLights = Lights.Drawn(camera.Eye, camera.Forward);
        _host.PhotoLook = true;
        _host.Player.SpeechBubbles.Hidden = !_dialogue;
        _controls.LookScale = MathF.Tan(camera.FieldOfView * 0.5f) / MathF.Tan(25 * MathF.PI / 180);
        _host.AgentChannel.InterfaceHidden = true;
        _host.Gpu.MarkersHidden = true;
    }

    #endregion

    #region Keys

    /// <summary>
    /// Where the autofocus looks, while it is what focuses the lens (the panel marks it); null otherwise.
    /// </summary>
    public Vector2? FocusSpot => Active && _dof && _autoFocus ? _focusSpot : null;

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
            NamingKey(key, escape);
            return true;
        }

        if (Typing is not null)
        {
            EntryKey(key, escape);
            return true;
        }

        if (Poses.ListOpen)
        {
            Poses.ListKey(key, escape);
            return true;
        }

        if (toggle || escape) Leave();
        else if (bindings.Get(InputAction.ToggleInterface).Contains(Binding.Of(key))) PanelShown = !PanelShown;
        else if (key == Key.Enter || bindings.Get(InputAction.Screenshot).Contains(Binding.Of(key))) TakePicture();
        else if (key is Key.PageUp or Key.PageDown) StepLens(key == Key.PageUp ? -2.5f : 2.5f);
        else if (key == Key.V) _host.Session.Camera.Free.Roll = 0;
        else if (key is Key.Tab or Key.H) PanelShown = !PanelShown;
        else if (key == Key.G) Grid = (PhotoGrid)(((int)Grid + 1) % 5);
        else if (bindings.Get(InputAction.Pause).Contains(Binding.Of(key))) SetLive(Running ? 0 : 1);
        else if (FliesCamera(key)) _host.Session.Input.SetKey(key, true);
        return true;
    }

    /// <summary>
    /// The wheel over the picture: the lens, 8 % a notch (away from the user zooms in), or the fly speed with Alt (Ctrl is the slow modifier).
    /// </summary>
    public void Wheel(float notches, bool speed)
    {
        if (speed) _controls.FlySpeed = Math.Clamp(_controls.FlySpeed * MathF.Pow(1.2f, notches), PhotoControls.MinSpeed, PhotoControls.MaxSpeed);
        else _host.Camera.FieldOfView = Math.Clamp(_host.Camera.FieldOfView * MathF.Pow(0.92f, notches), 10 * MathF.PI / 180, 100 * MathF.PI / 180);
    }

    /// <summary>
    /// A left press on the picture at <paramref name="pixel"/> of a <paramref name="size"/> picture: on the Pose tab it chooses the character there
    /// (or starts dragging the chosen one), on the Light tab the light whose mark is there; a gizmo handle within a few pixels comes first. A plain left
    /// click never sets the focus point on any tab: with Ctrl held (<paramref name="ctrl"/>) the lens focuses there (<see cref="FocusAt"/>).
    /// </summary>
    public void PicturePress(Vector2 pixel, Vector2 size, bool ctrl = false)
    {
        // A click beside the open pose list closes it.
        if (Poses.ListOpen)
        {
            Poses.ToggleList();
            return;
        }

        // A transform handle within a few pixels wins over any character or light mark.
        if (Gizmo.Press(_host.Player, GizmoSubject, pixel))
        {
            Say(Gizmo.Active is GizmoHandle.Yaw or GizmoHandle.Pitch ? "Turning" : "Moving");
            return;
        }

        // On the Light tab a press on a light's mark chooses that light, or starts moving the chosen one.
        if (Panel.Group == LightGroup && PanelShown && Lights.Press(_host.Player, pixel))
        {
            Say(Lights.Dragging ? $"Moving {Lights.ChosenName}" : Lights.ChosenName);
            return;
        }

        if (Panel.Group == PoseGroup && PanelShown && Poses.Press(pixel))
        {
            Say(Poses.Dragging ? $"Moving {Poses.SelectedName}" : Poses.SelectedName);
            return;
        }

        if (ctrl) FocusAt(new Vector2(pixel.X / Math.Max(size.X, 1), pixel.Y / Math.Max(size.Y, 1)));
    }

    /// <summary>
    /// A left click on the picture: the autofocus looks there from now on (a place in the picture, 0..1 from its top left), with the depth of
    /// field on.
    /// </summary>
    public void FocusAt(Vector2 place)
    {
        _focusSpot = Vector2.Clamp(place, new Vector2(0.02f), new Vector2(0.98f));
        (_dof, _autoFocus) = (true, true);
        Say("Focus point set");
    }

    /// <summary>
    /// A key while a name is being typed: Enter keeps it, Esc drops it, Backspace takes a letter back (all of a proposed name); no other key acts.
    /// </summary>
    void NamingKey(Key key, bool escape)
    {
        if (key == Key.Enter) CommitNaming();
        else if (escape || key == Key.Escape) CancelNaming();
        else if (key == Key.Backspace) Naming = _namingFresh || Naming!.Length == 0 ? "" : Naming[..^1];
        _namingFresh = false;
    }

    /// <summary>
    /// The keys the photo camera reads itself (<see cref="FreeCamera.Update"/>): W A S D and the arrows, Q and E, the roll keys, Shift and Ctrl.
    /// </summary>
    static bool FliesCamera(Key key) =>
        key is Key.W or Key.A or Key.S or Key.D or Key.Q or Key.E or Key.Z or Key.X or Key.Up or Key.Down or Key.Left or Key.Right
            or Key.LeftShift or Key.RightShift or Key.LeftControl or Key.RightControl;

    /// <summary>
    /// PageUp zooms the lens in (a narrower view), PageDown out, 2.5 degrees a press.
    /// </summary>
    void StepLens(float degrees) =>
        _host.Camera.FieldOfView = Math.Clamp(_host.Camera.FieldOfView + degrees * MathF.PI / 180, 10 * MathF.PI / 180, 100 * MathF.PI / 180);

    #endregion

    #region Remembered settings and presets

    /// <summary>
    /// The settings the mode was left with and the kept presets, on disk.
    /// </summary>
    readonly PhotoPresets _store = new();

    /// <summary>
    /// The settings a new session of the mode does not start with: the roll and the drawn hour belong to one picture, the simulation starts paused.
    /// </summary>
    internal static readonly string[] NotRemembered = ["roll", "hour", "live"];

    /// <summary>
    /// The kept preset being renamed, -1 for a new preset.
    /// </summary>
    int _namingIndex;

    /// <summary>
    /// The name being typed is still the proposed one (the first letter typed replaces it).
    /// </summary>
    bool _namingFresh;

    /// <summary>
    /// The most characters a typed preset name holds.
    /// </summary>
    const int NameLength = 28;

    /// <summary>
    /// The kept presets.
    /// </summary>
    internal IReadOnlyList<PhotoPresets.Preset> Presets => _store.Presets;

    /// <summary>
    /// The name being typed for a preset (the panel shows it in a field), null when none is: a new preset's (<see cref="_namingIndex"/> -1, proposed
    /// as the first free "Preset n") or a kept one's new name. The first letter typed replaces the proposed name.
    /// </summary>
    public string? Naming { get; private set; }

    /// <summary>
    /// Starts typing a name: for a new preset of the settings as they are, or for the kept preset <paramref name="index"/>.
    /// </summary>
    public void BeginNaming(int index = -1)
    {
        if (index >= _store.Presets.Count) return;

        (_namingIndex, _namingFresh) = (index, true);
        Naming = index < 0 ? FreeName() : _store.Presets[index].Name;

        StopFlying();
    }

    /// <summary>
    /// The slider whose value is being typed (the panel shows the text in place of the value), null when none is.
    /// </summary>
    internal Param? Typing { get; private set; }

    /// <summary>
    /// The value text being typed for <see cref="Typing"/>.
    /// </summary>
    public string Entry { get; private set; } = "";

    /// <summary>
    /// The value being typed is still the one it began with (the first character typed replaces it: the field shows it selected).
    /// </summary>
    public bool EntryFresh => Typing is not null && _namingFresh;

    /// <summary>
    /// The most characters a typed value holds.
    /// </summary>
    const int EntryLength = 10;

    /// <summary>
    /// Starts typing a precise value for slider <paramref name="p"/>: the field starts with the current value in the slider's own units (the first
    /// character typed replaces it), Enter applies it as the slider would take it (clamped to its range and rounded by its own setter), Esc drops it.
    /// </summary>
    internal void BeginEntry(Param p)
    {
        if (p.Choices is not null) return;

        Typing = p;
        Entry = p.Name == "hour" ? $"{(int)p.Get()}:{(int)MathF.Round(p.Get() % 1 * 60):00}" : p.Get().ToString("0.###", CultureInfo.InvariantCulture);
        _namingFresh = true;
        StopFlying();
    }

    /// <summary>
    /// Drops the value being typed.
    /// </summary>
    public void CancelEntry() => Typing = null;

    /// <summary>
    /// Applies the typed value to the slider: a number (a decimal comma too), or hours and minutes as <c>13:30</c> for the time of day; clamped to the
    /// slider's range. Text that is no number leaves the slider as it is.
    /// </summary>
    void CommitEntry()
    {
        if (Typing is not { } p) return;

        Typing = null;
        if (!TryParseEntry(Entry, out float value))
        {
            Say($"'{Entry}' is no value for {p.Label}");
            return;
        }

        p.Set(Math.Clamp(value, p.Min, p.Max));
        Say($"{p.Label} {ValueText(p)}");
    }

    /// <summary>
    /// A typed value: a number, or <c>h:mm</c>.
    /// </summary>
    static bool TryParseEntry(string text, out float value)
    {
        text = text.Trim().Replace(',', '.');
        int colon = text.IndexOf(':');
        if (colon < 0) return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

        bool ok = float.TryParse(text[..colon], NumberStyles.Float, CultureInfo.InvariantCulture, out float hours)
            & float.TryParse(text[(colon + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out float minutes);
        value = hours + minutes / 60;
        return ok && float.IsFinite(value);
    }

    /// <summary>
    /// A key while a value is typed: Enter applies it, Esc drops it, Backspace takes a character back.
    /// </summary>
    void EntryKey(Key key, bool escape)
    {
        if (key == Key.Enter) CommitEntry();
        else if (escape || key == Key.Escape) CancelEntry();
        else if (key == Key.Backspace) Entry = _namingFresh || Entry.Length == 0 ? "" : Entry[..^1];
        _namingFresh = false;
    }

    /// <summary>
    /// The camera stops while the keys type (a preset's name, the pose list's filter).
    /// </summary>
    void StopFlying()
    {
        foreach (var key in Enum.GetValues<Key>())
            if (FliesCamera(key)) _host.Session.Input.SetKey(key, false);
    }

    /// <summary>
    /// Opens the pose list for the chosen character (its filter then takes the keys), or closes it.
    /// </summary>
    public void TogglePoseList()
    {
        if (Poses.Selected is null)
        {
            Say("Choose a character first: click one, or use the arrows");
            return;
        }

        Poses.ToggleList();
        if (Poses.ListOpen) StopFlying();
    }

    /// <summary>
    /// A typed character goes into the name being typed; false when none is.
    /// </summary>
    public bool TextInput(char c)
    {
        if (Active && Poses.ListOpen)
        {
            Poses.Type(c);
            return true;
        }

        if (Active && Typing is not null)
        {
            if (!char.IsAsciiDigit(c) && c is not ('.' or ',' or '-' or '+' or ':')) return true;

            if (_namingFresh) Entry = "";
            _namingFresh = false;
            if (Entry.Length < EntryLength) Entry += c;
            return true;
        }

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
        else if (index < _store.Presets.Count && name.Length > 0) RenamePreset(index, name);
    }

    /// <summary>
    /// Drops the name being typed.
    /// </summary>
    public void CancelNaming() => Naming = null;

    /// <summary>
    /// Opens the folder the pictures are saved to in the system's file browser (made if it is not there yet).
    /// </summary>
    public void OpenGallery()
    {
        string folder = PicturesFolder();
        try
        {
            Directory.CreateDirectory(folder);

            // No desktop to open it on.
            if (_host.WorldOptions.Headless)
            {
                Say(folder);
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = folder, UseShellExecute = true })?.Dispose();
        }
        catch (Exception e)
            when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
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

    /// <summary>
    /// Puts the settings of the kept preset <paramref name="index"/> back (nothing when there is none).
    /// </summary>
    public void LoadPreset(int index)
    {
        if (index < 0 || index >= _store.Presets.Count) return;

        Take(_store.Presets[index].Values, preset: true);
        Say(_store.Presets[index].Name);
    }

    /// <summary>
    /// Deletes the kept preset <paramref name="index"/> (nothing when there is none).
    /// </summary>
    public void DeletePreset(int index)
    {
        if (index < 0 || index >= _store.Presets.Count) return;

        string name = _store.Presets[index].Name;
        _store.Presets.RemoveAt(index);
        _store.Save();
        Say($"Deleted {name}");
    }

    /// <summary>
    /// The settings by their <c>photo set</c> names, and the focus point: all but the simulation's state for a preset (the hour only while one is
    /// drawn), without <see cref="NotRemembered"/> for the next session.
    /// </summary>
    Dictionary<string, float> Values(bool preset)
    {
        var values = new Dictionary<string, float>();
        foreach (var p in Params)
        {
            if (!Kept(p) || !preset && NotRemembered.Contains(p.Name)) continue;
            if (p.Name == "hour" && _host.PhotoHour is null) continue;
            values[p.Name] = p.Get();
        }

        (values["spotx"], values["spoty"]) = (_focusSpot.X, _focusSpot.Y);
        return values;
    }

    /// <summary>
    /// Sets the settings named in <paramref name="values"/> and the focus point; a preset without an hour draws the game's time again.
    /// </summary>
    void Take(IReadOnlyDictionary<string, float> values, bool preset)
    {
        // The focus mode last: setting a focus distance turns the autofocus off.
        foreach (var p in Params)
            if (p.Name != "focusmode" && Kept(p) && values.TryGetValue(p.Name, out float v)) p.Set(v);
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
    /// The first "Preset n" name no kept preset has.
    /// </summary>
    string FreeName() =>
        Enumerable.Range(1, 999)
            .Select(i => $"Preset {i}")
            .First(n => _store.Presets.All(p => p.Name != n));

    /// <summary>
    /// The kept preset <paramref name="index"/> takes <paramref name="name"/>, unless another preset has it.
    /// </summary>
    void RenamePreset(int index, string name)
    {
        bool taken = _store.Presets
            .Where((p, i) => i != index)
            .Any(p => p.Name == name);
        if (taken)
        {
            Say($"{name} exists already");
            return;
        }

        _store.Presets[index].Name = name;
        _store.Save();
        Say($"Renamed to {name}");
    }

    /// <summary>
    /// <c>photo preset save [name] | load &lt;n|name&gt; | delete &lt;n|name&gt; | name [n|name] | list</c>.
    /// </summary>
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

            // As the panel's Save preset (no argument) or a right click on preset n: the name is then typed (photo type, photo key Enter).
            case "name":
                if (name.Length > 0 && (index < 0 || index >= _store.Presets.Count))
                {
                    con.Error("no such preset");
                    break;
                }

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
    /// The message still shown, or null.
    /// </summary>
    public string? Message => Environment.TickCount64 < _messageUntil ? _message : null;

    /// <summary>
    /// Asks for a picture: the next frame is saved to <paramref name="file"/> (null: the Pictures folder's Meitou folder, named by the time), the
    /// size it is drawn at, with the frame bars and without the panel and the grid, or with them when <paramref name="withGuides"/>.
    /// </summary>
    public string? TakePicture(string? file = null, bool withGuides = false, bool answerControl = false)
    {
        // A picture the loop dropped (no frame could be drawn: ControlShotFailed) no longer holds the next one back.
        if (_pending is not null && _host.AgentChannel.ShotPath != _pending) (_pending, _shotWithGuides) = (null, false);
        if (!Active || _pending is not null) return null;

        string path = file is null ? DefaultPath() : Path.GetFullPath(file);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say($"Cannot save to {path}: {e.Message}");
            return null;
        }

        _pending = path;
        _shotWithGuides = withGuides;
        _host.AgentChannel.ShotPath = path;
        _host.AgentChannel.ShotWaitId = answerControl ? _host.AgentChannel.CurrentControlId : null;
        _shotDelay = ShotDelayFrames;
        return path;
    }

    /// <summary>
    /// A picture is being saved, from the request until it is written: the panel shows the saving indicator (never in the saved picture itself).
    /// </summary>
    public bool Busy => Active && _pending is not null;

    /// <summary>
    /// The picture's size in pixels as it will be saved at a <paramref name="width"/> x <paramref name="height"/> display (before the frame's crop).
    /// </summary>
    public (int Width, int Height) PictureSize(int width, int height)
    {
        float scale = PictureScale(width, height);
        return ((int)MathF.Round(width * scale), (int)MathF.Round(height * scale));
    }

    /// <summary>
    /// Counts the frames the saving indicator has been drawn; the last one requests the picture's frame.
    /// </summary>
    void StepShot()
    {
        if (_pending is null || _shotDelay <= 0) return;

        if (--_shotDelay == 0) _host.ScreenshotRequested = true;
    }

    /// <summary>
    /// A frame being saved leaves the panel and the grid out (unless asked to keep them).
    /// </summary>
    public bool GuidesDrawn(bool saving) => !saving || _shotWithGuides;

    /// <summary>
    /// A picture is asked of this frame (<see cref="TakePicture"/>): the loop saves it (<see cref="GameLoop"/>'s <c>SavePhoto</c>).
    /// </summary>
    public bool Saving => Active && _pending is not null;

    /// <summary>
    /// How many times larger than the drawn <paramref name="width"/> x <paramref name="height"/> the picture is drawn: the Picture size, cut down so
    /// its longest side stays within <see cref="MaxPictureSide"/> (1: the frame as drawn). A picture with the panel and grid is the frame's.
    /// </summary>
    public float PictureScale(int width, int height)
    {
        if (_shotWithGuides) return 1;

        int factor = PictureSizes[_pictureSize];
        return factor <= 1 ? 1 : MathF.Max(1, MathF.Min(factor, MaxPictureSide / (float)Math.Max(Math.Max(width, height), 1)));
    }

    /// <summary>
    /// The part of a <paramref name="width"/> x <paramref name="height"/> picture (RGBA8 rows from the top) the frame keeps, as its own picture: a
    /// 4:5 or 2.39 frame is saved at its shape, without the bars. With the panel and grid kept the whole picture is (the bars show where it cuts).
    /// </summary>
    public (byte[] Pixels, int Width, int Height) Cropped(byte[] pixels, int width, int height)
    {
        var window = PhotoGuides.Window(_shotWithGuides ? PhotoFrame.Off : Frame, width, height);
        int left = (int)window.X, top = (int)window.Y, w = (int)window.Z - left, h = (int)window.W - top;
        if (w == width && h == height) return (pixels, width, height);

        var kept = new byte[w * h * 4];
        for (int row = 0; row < h; row++) pixels.AsSpan(((top + row) * width + left) * 4, w * 4).CopyTo(kept.AsSpan(row * w * 4));
        return (kept, w, h);
    }

    /// <summary>
    /// The picture was written (or could not be): says where, or that it failed.
    /// </summary>
    public void PictureSaved(string path, bool saved = true)
    {
        if (_pending is null) return;

        _pending = null;
        _shotDelay = 0;
        _shotWithGuides = false;
        Say(saved ? $"Saved {path}" : $"Cannot save {path}");
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
    /// The folder pictures go to when no file is named: the Pictures folder's Meitou folder.
    /// </summary>
    static string PicturesFolder()
    {
        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (pictures.Length == 0) pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
        return Path.Combine(pictures, "Meitou");
    }

    /// <summary>
    /// Where a picture goes when no file is named: <see cref="PicturesFolder"/>, named by the local time; a second picture within the same second
    /// takes " (2)", " (3)"... rather than replacing the first.
    /// </summary>
    static string DefaultPath()
    {
        string folder = PicturesFolder();
        string stamp = $"Meitou {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
        string path = Path.Combine(folder, stamp + ".png");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{stamp} ({n}).png");
        return path;
    }

    #endregion

    #region Console

    /// <summary>
    /// The <c>photo</c> console command: <c>on|off|toggle</c>, <c>set &lt;name&gt; &lt;value&gt;</c>, <c>get</c>, <c>reset</c>,
    /// <c>cam x y z yaw pitch</c>, <c>panel on|off</c>, <c>key &lt;name&gt; [up]</c> and <c>shot [file] [--ui]</c>.
    /// </summary>
    public void Command(IReadOnlyList<string> args, DevConsole con)
    {
        string verb = args.Count > 0 ? args[0].ToLowerInvariant() : "get";
        if (SwitchCommand(verb, con)) return;

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

        ActiveCommand(verb, args, con);
    }

    /// <summary>
    /// <c>photo on|off|toggle</c>; false for any other verb.
    /// </summary>
    bool SwitchCommand(string verb, DevConsole con)
    {
        switch (verb)
        {
            case "on":
                con.Print(Enter() ? "photo mode on" : "photo mode cannot start here");
                return true;
            case "off":
                Leave();
                con.Print("photo mode off");
                return true;
            case "toggle":
                Toggle();
                con.Print(Active ? "photo mode on" : "photo mode off");
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The verbs that act on the mode while it is on.
    /// </summary>
    void ActiveCommand(string verb, IReadOnlyList<string> args, DevConsole con)
    {
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
            case "pose":
                PoseCommand(args, con);
                break;

            // The chosen light: placed at the eye, another chosen, or pressed and dragged between two pixels as the pointer would (its mark).
            case "light":
                LightCommand(args, con);
                break;

            // The panel's tab, for checking it in a picture (photo shot --ui).
            case "tab" when args.Count > 1 && Groups.FirstOrDefault(g => g.Equals(args[1], StringComparison.OrdinalIgnoreCase)) is { } tab:
                Panel.Group = tab;
                con.Print($"tab {tab}");
                break;

            // A slider's typed value, as a double click on it: photo entry <name> begins, then photo type <text>, photo key Enter.
            case "entry" when args.Count > 1 && Params.FirstOrDefault(p => p.Name.Equals(args[1], StringComparison.OrdinalIgnoreCase)) is { } slider:
                BeginEntry(slider);
                con.Print($"entering {slider.Name}: {Entry}");
                break;
            case "shot":
                ShotCommand(args, con);
                break;
            case "key":
                KeyCommand(args, con);
                break;

            // Characters as the keyboard types them (a preset's name).
            case "type":
                foreach (char c in string.Join(' ', args.Skip(1))) TextInput(c);
                con.Print(Typing is not null ? $"entry: {Entry}" : Naming is null ? "no name is being typed" : $"naming: {Naming}");
                break;

            // A left press at a pixel of the picture and its release, as the mouse does (the Ctrl modifier optional): photo click <x> <y> [ctrl].
            case "click" when args.Count > 2 && float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float px)
                && float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float py):
                var size = new Vector2(_host.WorldOptions.Width, _host.WorldOptions.Height);
                PicturePress(new Vector2(px, py), size, ctrl: args.Count > 3);
                Poses.Release();
                con.Print(FormattableString.Invariant($"focus spot {_focusSpot.X:0.###} {_focusSpot.Y:0.###}, character {Poses.SelectedName}"));
                break;

            // A gizmo handle dragged by pixels, as the mouse would: photo gizmo <x|y|z|yaw|pitch> <dx> <dy> (pressed on the handle, moved, released).
            case "gizmo":
                GizmoCommand(args, con);
                break;

            // The pointer put at a pixel (the hover looks of the panel and the gizmo): photo hover <x> <y>.
            case "hover" when args.Count > 2 && float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float hx)
                && float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float hy):
                Panel.MouseMove(new Vector2(hx, hy));
                con.Print("pointer moved");
                break;
            case "gallery":
                OpenGallery();
                con.Print(PicturesFolder());
                break;
            default:
                con.Error("usage: photo on|off|toggle | get | reset | set <name> <value> | cam <x> <y> <z> <yawDeg> <pitchDeg> | panel on|off"
                    + " | shot [file] [--ui] | key <name> [up] | preset ... | pose ... | light ... | tab <name> | entry <name>"
                    + " | click <x> <y> [ctrl] | gizmo <handle> <dx> <dy> | hover <x> <y> | type <text> | gallery");
                break;
        }
    }

    /// <summary>
    /// <c>photo gizmo &lt;x|y|z|yaw|pitch&gt; &lt;dx&gt; &lt;dy&gt;</c>: presses the handle, drags it by that many pixels, releases it.
    /// </summary>
    void GizmoCommand(IReadOnlyList<string> args, DevConsole con)
    {
        var parsed = args.Count == 4 && Enum.TryParse(args[1], true, out GizmoHandle handle) && handle != GizmoHandle.None;
        float dx = 0, dy = 0;
        parsed = parsed && float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out dx)
            && float.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out dy);
        if (!parsed)
        {
            con.Error("usage: photo gizmo <x|y|z|yaw|pitch> <dx> <dy> (on the Light or Pose tab, panel shown)");
            return;
        }

        var which = Enum.Parse<GizmoHandle>(args[1], true);
        var subject = GizmoSubject;
        Gizmo.Layout(_host.Player, subject, new Vector2(-1000));
        if (Gizmo.Anchor(which) is not { } at)
        {
            con.Error("no such handle here");
            return;
        }

        if (!Gizmo.Press(_host.Player, subject, at))
        {
            con.Error("the handle cannot be taken (hidden or edge-on)");
            return;
        }

        Gizmo.Drag(_host.Player, at + new Vector2(dx, dy));
        Gizmo.Release();
        con.Print(Describe());
    }

    /// <summary>
    /// <c>photo light place</c>, <c>photo light next</c>, <c>photo light drag &lt;x0&gt; &lt;y0&gt; &lt;x1&gt; &lt;y1&gt;</c> (pixels: a press at the first,
    /// a move to the second, a release, as on the Light tab).
    /// </summary>
    void LightCommand(IReadOnlyList<string> args, DevConsole con)
    {
        string sub = args.Count > 1 ? args[1] : "";
        if (sub == "place") PlaceLight();
        else if (sub == "next") Lights.Step(1);
        else if (sub == "where") { }
        else if (sub == "drag" && args.Count == 6)
        {
            var n = new float[4];
            for (int i = 0; i < 4; i++)
                if (!float.TryParse(args[i + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out n[i]))
                {
                    con.Error("photo light drag: four numbers");
                    return;
                }

            bool taken = Lights.Press(_host.Player, new Vector2(n[0], n[1])) && (Lights.Dragging || Lights.Press(_host.Player, new Vector2(n[0], n[1])));
            Lights.Drag(_host.Player, new Vector2(n[2], n[3]));
            Lights.Release();
            if (!taken)
            {
                con.Error("photo light drag: no mark there");
                return;
            }
        }
        else
        {
            con.Error("usage: photo light place | next | where | drag <x0> <y0> <x1> <y1>");
            return;
        }

        con.Print(Lights.MarkOf(_host.Player) is { } at
            ? FormattableString.Invariant($"{Lights.ChosenName}, mark at {at.X:0} {at.Y:0}")
            : Lights.ChosenName);
    }

    /// <summary>
    /// Every setting's value and the camera's eye, for <c>photo get</c>.
    /// </summary>
    string Describe() => string.Join(", ", Params.Select(p => $"{p.Name} {ValueText(p)}"))
        + $", eye {Fmt(_host.Camera.Eye.X)} {Fmt(_host.Camera.Eye.Y)} {Fmt(_host.Camera.Eye.Z)}";

    /// <summary>
    /// A number with at most one decimal, in the invariant culture.
    /// </summary>
    static string Fmt(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>photo set &lt;name&gt; &lt;value&gt;</c>: one setting (<c>hour game</c> draws the game's time, <c>focus auto</c> the autofocus).
    /// </summary>
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

    /// <summary>
    /// A value for a setting: one of its choices by name (true/yes and false/no for a switch), or a finite number.
    /// </summary>
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

    /// <summary>
    /// <c>photo pose next | prev | at &lt;x&gt; &lt;y&gt; | anim &lt;record|own&gt; | list [filter] | reset | clear</c>: chooses a character (the next
    /// in view, or the one at a pixel), sets its pose, lists the poses it can take, drops its changes or everyone's. The rest is <c>photo set</c>'s
    /// Pose settings.
    /// </summary>
    void PoseCommand(IReadOnlyList<string> args, DevConsole con)
    {
        string verb = args.Count > 1 ? args[1].ToLowerInvariant() : "";
        string rest = string.Join(' ', args.Skip(2));
        switch (verb)
        {
            case "next" or "prev":
                Poses.Step(verb == "next" ? 1 : -1);
                break;
            case "at" when args.Count == 4 && float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                && float.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float y):
                Poses.Select(Poses.At(new Vector2(x, y)));
                break;
            case "anim" when Poses.Selected is null:
                con.Error("no character chosen (photo pose next)");
                return;
            case "anim":
                string? record = rest.Equals("own", StringComparison.OrdinalIgnoreCase) ? null
                    : Poses.PoseNames().FirstOrDefault(n => n.Equals(rest, StringComparison.OrdinalIgnoreCase));
                if (record is null && !rest.Equals("own", StringComparison.OrdinalIgnoreCase))
                {
                    con.Error($"'{rest}' is no pose this character has (photo pose list)");
                    return;
                }

                Poses.SetPose(record);
                break;
            case "open":
                TogglePoseList();
                con.Print($"list {(Poses.ListOpen ? "open" : "closed")}, filter '{Poses.Filter}', {Poses.Shown.Count - 1} shown, highlight {Poses.Highlight}");
                return;
            case "list":
                var names = Poses.PoseNames().Where(n => n.Contains(rest, StringComparison.OrdinalIgnoreCase)).ToList();
                con.Print($"{names.Count} poses: {string.Join(", ", names.Take(60))}{(names.Count > 60 ? ", ..." : "")}");
                return;
            case "reset":
                Poses.ResetSelected();
                break;
            case "clear":
                Poses.Clear();
                break;
            default:
                con.Error("usage: photo pose next | prev | at <x> <y> | anim <record|own> | list [filter] | open | reset | clear");
                return;
        }

        var at = Poses.SelectedCharacter?.Position ?? default;
        string pose = Poses.Pose ?? PhotoPoses.OwnAnimation;
        con.Print(FormattableString.Invariant(
            $"character {Poses.SelectedName} at {at.X:0} {at.Y:0} {at.Z:0}, pose {pose} ({Poses.PoseTime.Length:0.###} s, {Poses.RootKeys} root keys)"));
    }

    /// <summary>
    /// <c>photo cam x y z yaw pitch</c>: places the free camera (angles in degrees).
    /// </summary>
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

    /// <summary>
    /// <c>photo shot [file] [--ui]</c>: asks for a picture; with a file named the control channel's answer waits for it.
    /// </summary>
    void ShotCommand(IReadOnlyList<string> args, DevConsole con)
    {
        bool ui = args
            .Skip(1)
            .Contains("--ui");
        string? file = args
            .Skip(1)
            .FirstOrDefault(a => a != "--ui");
        string? path = TakePicture(file, ui, answerControl: file is not null);
        if (path is null) con.Error("no picture can be taken now");
        else con.Print($"queued {path}");
    }

    #endregion
}
