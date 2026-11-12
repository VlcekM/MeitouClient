using System.Diagnostics;

namespace Meitou.Game.Rendering.Profiling;

/// <summary>
/// Per-stage render-thread time of one frame (for <c>--fly-benchmark</c> and the <see cref="FrameProfiler"/>); <see cref="Lap"/> costs a timestamp
/// read and does nothing unless started. With a <see cref="Profiler"/> each lap also writes a GPU timestamp.
/// </summary>
static class StageClock
{
    #region Stages

    /// <summary>
    /// The stages' names by index, as the profiler, the bench output and the pass meter show them.
    /// </summary>
    public static readonly string[] Names =
    [
        "upd-terrain", "upd-objects", "upd-foliage", "sky-prepare", "reflection", "sky-draw", "terrain", "objects", "foliage", "water", "post", "ui",
        "shadows", "particles", "gi", "uploads", "cull", "bake", "shafts", "fog", "characters", "upd-chars", "submit", "driver-wait", "host", "blood",
        "sim", "birds", "lamp-shadows",
    ];

    /// <summary>
    /// Stages 15 to 17 exist on the GPU only: the pre-frame command buffer's uploads (what it holds besides the next two), the foliage and grass
    /// culls and the impostor bakes, timed by stamps in that buffer (<see cref="FrameProfiler.PreStamp"/>). The render thread has no lap for them. 18
    /// and 19 are the light shafts and the fog volumes, 20 and 21 the characters' colour and motion draws and their update; 10 is post-processing.
    /// </summary>
    public const int Uploads = 15, Cull = 16, Bake = 17, Shafts = 18, Fog = 19, Characters = 20, UpdChars = 21, Post = 10;

    /// <summary>
    /// The interface (everything from the world's last stage to the submission), the submission, and the wait for the driver (CPU only).
    /// </summary>
    public const int Ui = 11, Submit = 22, DriverWait = 23;

    /// <summary>
    /// 24 is the game host's own work in the frame before the world draws (the viewer has none), 25 the blood on the ground: the host's marks and
    /// pools and the decal layers redrawn.
    /// </summary>
    public const int Host = 24, Blood = 25;

    /// <summary>
    /// 26 is the game's simulation ticks: on the render thread, before the frame's clock starts (<see cref="FrameProfiler.Outside(double)"/>); the
    /// viewer has none.
    /// </summary>
    public const int Sim = 26;

    /// <summary>
    /// 27 is the game host's birds (the step runs on its own thread; this is the host's share: collecting the last step's flocks and starting the
    /// next).
    /// </summary>
    public const int Birds = 27;

    /// <summary>
    /// 28 is the lamps' traced shadows (part of the gi group in the legend).
    /// </summary>
    public const int LampShadows = 28;

    /// <summary>
    /// The stage the last lap ended (-1 before the first of the frame).
    /// </summary>
    public static int LastStage { get; private set; }

    /// <summary>
    /// Whether the frame's submission has started (<see cref="BeginSubmission"/>): the GPU time after it counts as submit.
    /// </summary>
    public static bool SubmissionStarted { get; private set; }

    /// <summary>
    /// Whether a frame's clock is running (between <see cref="Start"/> and <see cref="EndFrame"/>); laps do nothing otherwise.
    /// </summary>
    public static bool Active;

    /// <summary>
    /// The profiler that takes a GPU timestamp at every lap; null when none.
    /// </summary>
    public static FrameProfiler? Profiler;

    /// <summary>
    /// Whether a stage has no GPU time of its own: the driver wait and the simulation.
    /// </summary>
    public static bool CpuOnly(int stage) => stage is DriverWait or Sim;

    /// <summary>
    /// Whether a stage exists on the GPU only: the pre-frame uploads, culls and bakes.
    /// </summary>
    public static bool GpuOnly(int stage) => stage is Uploads or Cull or Bake;

    #endregion

    #region Frame clock

    /// <summary>
    /// This frame's render-thread ms per stage.
    /// </summary>
    public static readonly double[] Ms = new double[Names.Length];

    /// <summary>
    /// When the last lap and the last part ended, in <see cref="Stopwatch"/> ticks.
    /// </summary>
    static long s_last, s_partLast;

    /// <summary>
    /// The pass meter's hook (<c>MEITOU_PASS_STATS=1</c>, the viewer's PassMeter; docs/engine.md "Frame cost breakdown"): called with the label of
    /// what just ended and whether it is a part (<see cref="Sub"/>) of the enclosing stage or a stage of its own (<see cref="Lap"/>,
    /// <see cref="Phase"/>); null (the default) costs one compare. <see cref="OnStart"/> is called when a frame's clock starts.
    /// </summary>
    public static Action<string, bool>? OnClose;

    /// <summary>
    /// Called when a frame's clock starts (<see cref="Start"/>); null by default.
    /// </summary>
    public static Action? OnStart;

    /// <summary>
    /// Starts a frame's clock: every stage's, job's and part's time cleared.
    /// </summary>
    public static void Start()
    {
        Active = true;
        SubmissionStarted = false;
        LastStage = -1;
        Array.Clear(Ms);
        Array.Clear(JobMs);
        Array.Clear(PartMs);
        Array.Clear(UiPartMs);
        s_last = s_partLast = Stopwatch.GetTimestamp();
        OnStart?.Invoke();
    }

    /// <summary>
    /// Called before the window copy, or before submission for an offscreen frame: ends the interface stage, and the GPU time after it is submit.
    /// </summary>
    public static void BeginSubmission()
    {
        if (!Active || SubmissionStarted) return;

        Lap(Ui);
        SubmissionStarted = true;
    }

    /// <summary>
    /// Ends the frame's clock with the last stage (the submit or driver wait, else the interface), without a GPU timestamp: submission has closed the
    /// commands.
    /// </summary>
    public static void EndFrame()
    {
        // Submission has closed the commands; a GPU timestamp here would open the next frame.
        Lap(LastStage is Submit or DriverWait ? LastStage : Ui, "gpu-wait", gpuTimestamp: false);
        Active = false;
    }

    /// <summary>
    /// Ends <paramref name="stage"/>'s span; <paramref name="label"/> is what the pass meter names the row (default the stage's name), for a stage
    /// that laps more than once.
    /// </summary>
    public static void Lap(int stage, string? label = null, bool gpuTimestamp = true)
    {
        if (!Active) return;

        long now = Stopwatch.GetTimestamp();
        Ms[stage] += (now - s_last) * 1000.0 / Stopwatch.Frequency;
        s_last = s_partLast = now;
        LastStage = stage;
        if (gpuTimestamp) Profiler?.Stamp(stage);
        OnClose?.Invoke(label ?? Names[stage], false);
    }

    /// <summary>
    /// Ends a part of the stage running (a cascade's terrain, the foliage's grass); the meter shows it under that stage. Does not touch the stage
    /// times.
    /// </summary>
    public static void Sub(string label)
    {
        if (OnClose is { } close && Active) close(label, true);
    }

    /// <summary>
    /// Ends a stage the stage list has no entry for (a shadow cascade, the overlays); the meter shows it as its own row. Does not touch the stage
    /// times.
    /// </summary>
    public static void Phase(string label)
    {
        if (OnClose is { } close && Active) close(label, false);
    }

    #endregion

    #region Parts

    /// <summary>
    /// The parts of the characters' update (<see cref="UpdChars"/>) the profiler lists under it (render thread only): the draw list from the host,
    /// the texture uploads, the gather of the characters in range, their posing, the materials, the stuck bolts, the portraits, the hover masks and
    /// the icons.
    /// </summary>
    public static readonly string[] PartNames = ["fill", "textures", "gather", "pose", "materials", "bolts", "portraits", "hover", "icons"];

    /// <summary>
    /// The indices of <see cref="PartNames"/>.
    /// </summary>
    public const int PartFill = 0, PartTextures = 1, PartGather = 2, PartPose = 3, PartMaterials = 4, PartBolts = 5, PartPortraits = 6, PartHover = 7,
        PartIcons = 8;

    /// <summary>
    /// This frame's render-thread ms per part (<see cref="Part"/>).
    /// </summary>
    public static readonly double[] PartMs = new double[PartNames.Length];

    /// <summary>
    /// The parts of the interface stage (<see cref="Ui"/>, everything from the world's last stage to the submission) the spike log lists under it
    /// (render thread only): the end of the world's draw after its last stage, the markers, the walk steering, the HUD and panels, the debug
    /// overlays, the settings panel, the cinema's layer, the loading screen, the pointer and the composite of the interface over the world.
    /// </summary>
    public static readonly string[] UiPartNames =
    ["world end", "markers", "steer", "hud", "debug", "settings", "cinema", "loading", "pointer", "composite",];

    /// <summary>
    /// The indices of <see cref="UiPartNames"/>.
    /// </summary>
    public const int UiWorldEnd = 0, UiMarkers = 1, UiSteer = 2, UiHud = 3, UiDebug = 4, UiSettings = 5, UiCinema = 6, UiLoading = 7, UiPointer = 8,
        UiComposite = 9;

    /// <summary>
    /// This frame's render-thread ms per interface part (<see cref="UiPart"/>).
    /// </summary>
    public static readonly double[] UiPartMs = new double[UiPartNames.Length];

    /// <summary>
    /// Ends <paramref name="part"/> of the stage running: the time since the last lap or part goes to it (and the pass meter gets it as a part, <see
    /// cref="Sub"/>).
    /// </summary>
    public static void Part(int part)
    {
        if (!Active) return;

        long now = Stopwatch.GetTimestamp();
        PartMs[part] += (now - s_partLast) * 1000.0 / Stopwatch.Frequency;
        s_partLast = now;
        OnClose?.Invoke(PartNames[part], true);
    }

    /// <summary>
    /// Ends <paramref name="part"/> of the interface stage: the time since the last lap or part goes to it.
    /// </summary>
    public static void UiPart(int part)
    {
        if (!Active) return;

        long now = Stopwatch.GetTimestamp();
        UiPartMs[part] += (now - s_partLast) * 1000.0 / Stopwatch.Frequency;
        s_partLast = now;
    }

    #endregion

    #region Recording jobs

    /// <summary>
    /// The recording jobs' CPU time per stage this frame (docs/renderer-native.md 9.3, wave 4): each job's own time, summed whatever thread ran it,
    /// so it does not add up to the frame's wall time; the render thread's wall time (which includes waiting for the jobs) stays in <see cref="Ms"/>.
    /// </summary>
    public static readonly double[] JobMs = new double[Names.Length];

    /// <summary>
    /// Adds a recording job's <paramref name="ticks"/> (Stopwatch) to <paramref name="stage"/> (render thread, after the jobs).
    /// </summary>
    public static void AddJob(int stage, long ticks)
    {
        if (Active && (uint)stage < (uint)JobMs.Length) JobMs[stage] += ticks * 1000.0 / Stopwatch.Frequency;
    }

    #endregion
}
