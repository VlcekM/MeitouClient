using Meitou.Rendering;
using Meitou.Rendering.Display;

namespace Meitou.Game;

/// <summary>
/// Loading without a frozen window (docs/engine.md "Loading"): the work that touches no GPU resource runs on workers, the renderer's set-up stays on this thread
/// in steps, and between the steps and while a worker runs the window's events are handled and a <see cref="LoadingScreen"/> frame is drawn.
/// </summary>
sealed partial class GameHost
{
    VulkanDisplay? loadingDisplay;
    DebugOverlay? loadingOverlay;
    LoadingScreen? loading;
    // The steps of a start, in order, with their shown names and about how many seconds each takes (measured 2026-10-14 on an RTX 4070 machine, a cold start at
    // The Hub); the bar's share of a step is its time over the sum. The renderer's steps are named by WorldFrame.CreateGpu. "colourmap" comes only on a start
    // that has to encode the terrain colour cache (the first), so its share is kept small though it then takes the longest.
    static readonly (string Step, string Label, double Seconds)[] StartSteps =
    [
        ("scene", "the world", 0.5), ("terrain", "terrain", 0.3), ("colourmap", "terrain colour map (first start only)", 0.3), ("biomes", "terrain textures", 2.0), ("sky", "sky", 0.25), ("weather", "weather", 0.15),
        ("particles", "weather effects", 0.2), ("water", "water", 0.6), ("reflections", "water", 0.1), ("objects", "buildings", 0.4), ("foliage", "foliage", 0.35),
        ("shadows", "lighting", 0.3), ("gi", "lighting", 0.15), ("people", "people", 0.3), ("world", "the world", 0.1), ("interface", "the interface", 0.3),
        ("navmesh", "paths", 0.5), ("characters", "characters", 0.2), ("finishing", "finishing", 0.4), ("view", "the view", 1.2),
    ];
    // The steps of a load between scenes (GameHost.Transition.cs).
    static readonly (string Step, string Label, double Seconds)[] SceneSteps =
    [
        ("read", "the save", 0.6), ("build", "the world", 0.5), ("people", "people", 0.3), ("finishing", "finishing", 0.2), ("view", "the view", 1.2),
    ];
    (string Step, string Label, double Seconds)[] loadingSteps = StartSteps;

    /// <summary>The original's background-loading bar (Faithful UI mode), stepped once a drawn frame.</summary>
    readonly ResourceLoadBar loadBar = new();

    /// <summary>
    /// What the bar counts as the original's resource loader queue: the objects' and the characters' loads waiting (engine choice: the original's queue holds the
    /// meshes and textures it loads in the background; Meitou's terrain and foliage streaming are left out, as the original's are not in that queue).
    /// </summary>
    int PendingLoads() => (ActiveObjects?.Pending ?? 0) + (ActiveCharacters?.PendingLoads ?? 0);

    /// <summary>Opens the loading screen on the window (nothing without an overlay, e.g. headless).</summary>
    void BeginLoadingScreen(VulkanDisplay display)
    {
        loadingDisplay = display;
        loadingOverlay = DebugOverlay.TryCreate(display.Context, assets.Find);
        if (loadingOverlay is not null) loading = LoadingScreen.Create(loadingOverlay, assets.Find, install.DataDirectory, (g.UiMode ?? config.UiMode) == Meitou.Data.Ui.UiMode.Faithful);
        loadingSteps = StartSteps;
    }

    /// <summary>A step of the load starts: the bar moves to its share and the screen is drawn once (the step itself may then hold the thread).</summary>
    void LoadingStep(string step)
    {
        if (loading is null) return;
        SetLoadingStep(step);
        if (!LoadingFrame()) throw new LoadingCancelledException();
    }

    /// <summary>The bar moves to the step's share (no frame is drawn: the game loop draws the next one).</summary>
    void SetLoadingStep(string step)
    {
        if (loading is null) return;
        int i = Array.FindIndex(loadingSteps, s => s.Step == step);
        if (i < 0) return;
        double total = loadingSteps.Sum(s => s.Seconds), start = loadingSteps.Take(i).Sum(s => s.Seconds) / total;
        loading.Step(loadingSteps[i].Label, start, start + loadingSteps[i].Seconds / total, loadingSteps[i].Seconds);
        currentStep = step;
    }

    long lastLoadingFrame;
    int loadingFrames;
    string currentStep = "", lastFrameStep = "";
    (double Ms, string Step) longestGap;

    /// <summary>Prints how smooth the load was and starts the count again.</summary>
    void ReportLoading(string what)
    {
        if (loadingFrames == 0) return;
        Console.WriteLine($"loading   {what}: {loadingFrames} frames, the longest gap {longestGap.Ms:0} ms (in the step {longestGap.Step})");
        (loadingFrames, lastLoadingFrame, longestGap) = (0, 0, default);
    }

    /// <summary>Draws one loading frame and handles the window's events; false when the window is being closed.</summary>
    bool LoadingFrame()
    {
        if (loadingDisplay?.Window is not { } window || loading is null || loadingOverlay is null) return true;
        // The longest time the window went without a frame (the measure of a frozen screen), and the step it was in.
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (lastLoadingFrame != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(lastLoadingFrame, now).TotalMilliseconds is var gap && gap > longestGap.Ms)
            longestGap = (gap, lastFrameStep);
        if (lastLoadingFrame != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(lastLoadingFrame, now).TotalMilliseconds is var held && held > 250)
            Console.WriteLine($"loading   no frame for {held:0} ms in the step {lastFrameStep}");
        (lastLoadingFrame, lastFrameStep) = (now, currentStep);
        loadingFrames++;
        window.DoEvents();
        if (window.IsClosing) return false;
        var size = window.FramebufferSize;
        if (loadingDisplay.BeginFrame(size.X, size.Y))
        {
            loadingOverlay.Target = loadingDisplay.Backbuffer;
            loadBar.Update(loading.Faithful ? PendingLoads() : 0);
            loading.LoadBar = loadBar;
            loading.Draw(size.X, size.Y);
            loadingDisplay.Present();
        }
        return true;
    }

    /// <summary>Waits for <paramref name="work"/> drawing loading frames (about 60 a second); the window closing cancels the start.</summary>
    T AwaitLoading<T>(Task<T> work)
    {
        while (!work.IsCompleted)
        {
            if (!LoadingFrame()) throw new LoadingCancelledException();
            work.Wait(TimeSpan.FromMilliseconds(15));
        }
        return work.GetAwaiter().GetResult();
    }

    void AwaitLoading(Task work) => AwaitLoading(work.ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }, TaskScheduler.Default));

    void EndLoadingScreen()
    {
        loading?.Dispose();
        loadingOverlay?.Dispose();
        (loading, loadingOverlay, loadingDisplay) = (null, null, null);
    }

    /// <summary>The window was closed while the game was loading.</summary>
    sealed class LoadingCancelledException : Exception;
}
