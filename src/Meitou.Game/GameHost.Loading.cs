using Meitou.Game.Host;
using Meitou.Game.Rendering.Display;
using Meitou.Game.Rendering.Overlays;

namespace Meitou.Game;

/// <summary>
/// Loading without a frozen window (docs/engine.md "Loading"): the work that touches no GPU resource runs on workers, the renderer's set-up stays on
/// this thread in steps, and between the steps and while a worker runs the window's events are handled and a <see cref="LoadingScreen"/> frame is
/// drawn.
/// </summary>
sealed partial class GameHost
{
    VulkanDisplay? _loadingDisplay;
    internal DebugOverlay? LoadingOverlay;
    internal LoadingScreen? Loading;
    // The steps of a start, in order, with their shown names and about how many seconds each takes (measured 2026-10-14 on an RTX 4070 machine, a
    // cold start at The Hub); the bar's share of a step is its time over the sum. The renderer's steps are named by WorldFrame.CreateGpu. "colourmap"
    // comes only on a start that has to encode the terrain colour cache (the first), so its share is kept small though it then takes the longest.
    static readonly (string Step, string Label, double Seconds)[] StartSteps =
    [
        ("scene", "the world", 0.5), ("terrain", "terrain", 0.3), ("colourmap", "terrain colour map (first start only)", 0.3),
        ("biomes", "terrain textures", 2.0), ("sky", "sky", 0.25), ("weather", "weather", 0.15),
        ("particles", "weather effects", 0.2), ("water", "water", 0.6), ("reflections", "water", 0.1),
        ("objects", "buildings", 0.4), ("foliage", "foliage", 0.35),
        ("shadows", "lighting", 0.3), ("gi", "lighting", 0.15), ("people", "people", 0.3), ("world", "the world", 0.1),
        ("interface", "the interface", 0.3),
        ("navmesh", "paths", 0.5), ("characters", "characters", 0.2), ("finishing", "finishing", 0.4), ("view", "the view", 1.2),
    ];
    // The steps of a load between scenes (GameHost.Transition.cs).
    internal static readonly (string Step, string Label, double Seconds)[] SceneSteps =
    [
        ("read", "the save", 0.6), ("build", "the world", 0.5), ("people", "people", 0.3), ("finishing", "finishing", 0.2), ("view", "the view", 1.2),
    ];
    internal (string Step, string Label, double Seconds)[] LoadingSteps = StartSteps;

    /// <summary>
    /// The original's background-loading bar (Faithful UI mode), stepped once a drawn frame.
    /// </summary>
    internal readonly ResourceLoadBar LoadBar = new();

    /// <summary>
    /// What the bar counts as the original's resource loader queue: the objects' and the characters' loads waiting (engine choice: the original's
    /// queue holds the meshes and textures it loads in the background; Meitou's terrain and foliage streaming are left out, as the original's are not
    /// in that queue).
    /// </summary>
    internal int PendingLoads() => (ActiveObjects?.Pending ?? 0) + (ActiveCharacters?.PendingLoads ?? 0);

    /// <summary>
    /// Opens the loading screen on the window (nothing without an overlay, e.g. headless).
    /// </summary>
    internal void BeginLoadingScreen(VulkanDisplay display)
    {
        _loadingDisplay = display;
        LoadingOverlay = DebugOverlay.TryCreate(display.Context, Assets.Find);
        if (LoadingOverlay is not null)
            Loading = LoadingScreen.Create(LoadingOverlay, Assets.Find, Install.DataDirectory,
                (GameOptions.UiMode ?? Config.UiMode) == Data.Ui.UiMode.Faithful);
        LoadingSteps = StartSteps;
    }

    /// <summary>
    /// A step of the load starts: the bar moves to its share and the screen is drawn once (the step itself may then hold the thread).
    /// </summary>
    internal void LoadingStep(string step)
    {
        if (Loading is null) return;
        SetLoadingStep(step);
        if (!LoadingFrame()) throw new LoadingCancelledException();
    }

    /// <summary>
    /// The bar moves to the step's share (no frame is drawn: the game loop draws the next one).
    /// </summary>
    void SetLoadingStep(string step)
    {
        if (Loading is null) return;
        int i = Array.FindIndex(LoadingSteps, s => s.Step == step);
        if (i < 0) return;
        double total = LoadingSteps.Sum(s => s.Seconds), start = LoadingSteps.Take(i).Sum(s => s.Seconds) / total;
        Loading.Step(LoadingSteps[i].Label, start, start + LoadingSteps[i].Seconds / total, LoadingSteps[i].Seconds);
        _currentStep = step;
    }

    long _lastLoadingFrame;
    int _loadingFrames;
    string _currentStep = "", _lastFrameStep = "";
    (double Ms, string Step) _longestGap;

    /// <summary>
    /// Prints how smooth the load was and starts the count again.
    /// </summary>
    void ReportLoading(string what)
    {
        if (_loadingFrames == 0) return;
        Console.WriteLine($"loading   {what}: {_loadingFrames} frames, the longest gap {_longestGap.Ms:0} ms (in the step {_longestGap.Step})");
        (_loadingFrames, _lastLoadingFrame, _longestGap) = (0, 0, default);
    }

    /// <summary>
    /// Draws one loading frame and handles the window's events; false when the window is being closed.
    /// </summary>
    internal bool LoadingFrame()
    {
        if (_loadingDisplay?.Window is not { } window || Loading is null || LoadingOverlay is null) return true;
        // The longest time the window went without a frame (the measure of a frozen screen), and the step it was in.
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastLoadingFrame != 0
                && System.Diagnostics.Stopwatch.GetElapsedTime(_lastLoadingFrame, now).TotalMilliseconds is var gap && gap > _longestGap.Ms)
            _longestGap = (gap, _lastFrameStep);
        if (_lastLoadingFrame != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_lastLoadingFrame, now).TotalMilliseconds is var held && held > 250)
            Console.WriteLine($"loading   no frame for {held:0} ms in the step {_lastFrameStep}");
        (_lastLoadingFrame, _lastFrameStep) = (now, _currentStep);
        _loadingFrames++;
        window.DoEvents();
        if (window.IsClosing) return false;
        var size = window.FramebufferSize;
        if (_loadingDisplay.BeginFrame(size.X, size.Y))
        {
            LoadingOverlay.Target = _loadingDisplay.Backbuffer;
            LoadBar.Update(Loading.Faithful ? PendingLoads() : 0);
            Loading.LoadBar = LoadBar;
            Loading.Draw(size.X, size.Y);
            _loadingDisplay.Present();
        }
        return true;
    }

    /// <summary>
    /// Waits for <paramref name="work"/> drawing loading frames (about 60 a second); the window closing cancels the start.
    /// </summary>
    internal T AwaitLoading<T>(Task<T> work)
    {
        while (!work.IsCompleted)
        {
            if (!LoadingFrame()) throw new LoadingCancelledException();
            work.Wait(TimeSpan.FromMilliseconds(15));
        }
        return work.GetAwaiter().GetResult();
    }

    void AwaitLoading(Task work) => AwaitLoading(work.ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }, TaskScheduler.Default));

    internal void EndLoadingScreen()
    {
        Loading?.Dispose();
        LoadingOverlay?.Dispose();
        (Loading, LoadingOverlay, _loadingDisplay) = (null, null, null);
    }

    /// <summary>
    /// The window was closed while the game was loading.
    /// </summary>
    internal sealed class LoadingCancelledException : Exception;
}