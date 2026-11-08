using System.Diagnostics;
using System.Numerics;
using Meitou.Game.Title;

namespace Meitou.Game;

/// <summary>
/// Loads between scenes (a new game, a loaded save, the main menu) under the loading screen (docs/engine.md "Loading"): a load is a list of stages that run on this
/// thread at frame boundaries, one per frame, each after a covered frame was drawn; a stage may hand back work to wait for (a worker's read, the navmesh), and the
/// frames go on drawing the loading screen while it runs. After the last stage, and after the start, the world is drawn under the loading screen until its frames run
/// smoothly (the first frames of a new place build pipelines and stream its terrain and objects), so play starts on a smooth picture.
/// </summary>
sealed partial class GameHost
{
    internal sealed class SceneLoad
    {
        public readonly Queue<(string Step, Func<Task?> Run, bool DuringAscent)> Stages = new();
        public Task? Waiting;
        /// <summary>A covered frame was drawn since the last stage ran: the next one may run (it may hold the thread).</summary>
        public bool Shown;
        public readonly Stopwatch Clock = Stopwatch.StartNew();
    }

    /// <summary>A stage found the load cannot go on; the message is shown, the world as it was stays.</summary>
    sealed class SceneLoadFailed(string message) : Exception(message);

    internal SceneLoad? sceneLoad;
    SkyArrival? skyArrival;
    float? presentationHour;
    /// <summary>The hour of the save being read, set by the reading worker as soon as its CAMERA is read (NaN until then), for the sky arrival.</summary>
    float readHour = float.NaN;
    internal bool warming;
    double smoothSeconds;
    double warmSeconds;
    double readySeconds;
    double nextWarmReport;
    /// <summary>Seconds of the warm-up the VRAM guard has held its pressure without a break.</summary>
    double guardSeconds;
    bool titleWarmFinalized;
    /// <summary>After content is ready, gameplay's smooth-frame target and run, and the pacing limit (title too); the title requires <see cref="TitleSmoothRun"/>.</summary>
    const double SmoothFrame = 0.04, SmoothRun = 0.5, WarmLimit = 5;
    const double TitleSmoothRun = 1;
    /// <summary>Seconds of the guard's pressure after which the content it holds back is not waited for: nothing new loads until the use falls
    /// (VramGuard), so on a card short of memory it may never come (as <c>WorldObjectRenderer.Settle</c>).</summary>
    const double GuardWait = 5;

    /// <summary>Loading, warm-up, arrival or the loading overlay's final reveal still blocks input.</summary>
    internal bool Covered => sceneLoad is not null || warming || skyArrival is not null || loading is { Exiting: true };

    static (string Step, Func<Task?> Run, bool DuringAscent) Stage(string step, Func<Task?> run, bool duringAscent = false) => (step, run, duringAscent || step == "read");

    void StartSceneLoad(bool mainMenuTransition, float? destinationHour, params (string Step, Func<Task?> Run, bool DuringAscent)[] stages)
    {
        if (sceneLoad is not null) return;
        sceneLoad = new();
        if (player.UiMode == Meitou.Data.Ui.UiMode.Meitou && (mainMenuTransition ? config.MainMenuTransitions : config.InGameTransitions))
            skyArrival = new(camera, presentationHour ?? (TitleVisible ? TitleHour : (float)session.Clock.HourOfDay),
                mainMenuTransition ? config.MainMenuTransitionSpeed : config.InGameTransitionSpeed, immediate: !TitleVisible);
        if (destinationHour is { } hour) skyArrival?.SetDestination(hour);
        Volatile.Write(ref readHour, float.NaN);
        foreach (var stage in stages) sceneLoad.Stages.Enqueue(stage);
        loading?.Restart();
        loadingSteps = SceneSteps;
        SetLoadingStep(stages.Length > 0 ? stages[0].Step : "build");
    }

    /// <summary>Once a frame, between ticks: the next stage of the running load when its work is done and a covered frame was drawn.</summary>
    void StepSceneLoad()
    {
        if (sceneLoad is not { } load || !load.Shown || load.Waiting is { IsCompleted: false }) return;
        // Reading a save can overlap the ascent; world mutations wait until the ground is out of view.
        if (skyArrival is { AtStars: false } && load.Stages.TryPeek(out var next) && !next.DuringAscent) return;
        try
        {
            if (load.Waiting is { IsFaulted: true } failed) throw failed.Exception!.GetBaseException();
            if (!load.Stages.TryDequeue(out var stage))
            {
                // The sky fades to black before the destination is drawn under it.
                if (skyArrival is { Fade: < 1 } covering) { covering.Cover(); return; }
                sceneLoad = null;
                ReportLoading($"scene loaded in {load.Clock.Elapsed.TotalSeconds:0.0} s");
                BeginWarm();
                return;
            }
            SetLoadingStep(stage.Step);
            load.Waiting = stage.Run();
            load.Shown = false;
        }
        catch (Exception e)
        {
            sceneLoad = null;
            skyArrival = null;
            presentationHour = null;
            if (e is not SceneLoadFailed) Console.Error.WriteLine($"scene     load failed: {e}");
            player.Notice(e is SceneLoadFailed ? e.Message : $"Load failed: {e.Message}", true);
        }
    }

    /// <summary>The world is drawn under the loading screen until its frames run smoothly.</summary>
    internal void BeginWarm()
    {
        if (gpu.Characters is { } characters) characters.LoadingFocus = null;   // an earlier warm-up cut short may have left it
        if (loading is null) { skyArrival?.Arrive(); return; }
        // The Metal host draws its own frames and has no Vulkan loading display to measure a warm-up with: let the loading screen
        // slide away and start the sky's descent at once.
        if (loadingDisplay is null) { loading.Finish(); skyArrival?.Arrive(); return; }
        (warming, smoothSeconds, warmSeconds, readySeconds, guardSeconds) = (true, 0, 0, 0, 0);
        nextWarmReport = 5;
        titleWarmFinalized = false;
        SetLoadingStep("view");
    }

    /// <summary>After a covered frame of <paramref name="seconds"/>: the warm-up ends after <see cref="SmoothRun"/> seconds of smooth frames in a row.</summary>
    internal void CoveredFrame(double seconds)
    {
        if (sceneLoad is { } load) load.Shown = true;
        // The frames under the cover count for the loading report (LoadingFrame counts the start's).
        loadingFrames++;
        if (seconds * 1000 > longestGap.Ms) longestGap = (seconds * 1000, currentStep);
        if (!warming) return;
        warmSeconds += seconds;
        // The Faithful title is the original's opaque picture: nothing of the world shows behind it, so its view is not warmed (the original loads the world only for
        // a game). The start's splash plays out first.
        if (player.TitleOpen && player.UiMode == Meitou.Data.Ui.UiMode.Faithful && characterEditor.pendingCreation is null)
        {
            if (loading is { SplashPlaying: true }) return;
            warming = false;
            if (gpu.Characters is { } titleCharacters) titleCharacters.LoadingFocus = null;
            loading?.Finish();
            ReportLoading("the Faithful title shows");
            return;
        }
        // A ready character mesh alone is insufficient: finish terrain, scenery and deferred textures too.
        // Content readiness has no pacing timeout that can expose missing NPCs or white stand-ins.
        bool populationReady = TitleVisible || session.World.PreparePopulation(session.Camera.Current.Target);
        // A game's view waits for the characters round the camera's focus only (CharacterRenderer.LoadingFocus); the rest are prepared once the screen lifts.
        var focus = session.Camera.Current.Target;
        if (!TitleVisible && gpu.Characters is { } near) near.LoadingFocus = new Vector2(focus.X, focus.Z);
        bool charactersReady = gpu.Characters is not { } characters || !characters.ContentPending &&
            !session.CurrentSnapshot.Characters.Any(c => AwaitsLook(c, characters));
        bool sceneReady = TitleVisible ? TitleSceneReady : populationReady && charactersReady &&
            gpu.Streamer is not { Idle: false } && !(gpu.Objects?.Pending > 0) && !(gpu.Foliage?.LoadingPending > 0);
        guardSeconds = gpu.Guard is { Pressure: true } ? guardSeconds + seconds : 0;
        if (!sceneReady && guardSeconds >= GuardWait && (TitleVisible || populationReady))
        {
            sceneReady = true;
            if (guardSeconds - seconds < GuardWait)
                Console.WriteLine($"loading   stopped waiting for content held back by memory pressure (VramGuard, {gpu.Guard!.Fraction * 100:0}% of the {gpu.Guard.BudgetBytes / 1048576} MB budget)");
        }
        if (characterEditor.pendingCreation is not null && !TitleVisible && charactersReady)
        {
            // A new game shows the character editor before the world: the loading screen lifts once the squad is drawable, no sky arrival, and the world's own warm-up
            // runs when the editor closes (ResumeAfterCreation).
            (warming, skyArrival, presentationHour) = (false, null, null);
            if (gpu.Characters is { } editorCharacters) editorCharacters.LoadingFocus = null;
            loading?.Finish();
            ReportLoading($"the squad was ready in {warmSeconds:0.0} s; the editor opens first");
            return;
        }
        if (!sceneReady)
        {
            smoothSeconds = readySeconds = 0;
            if (warmSeconds >= nextWarmReport)
            {
                nextWarmReport = warmSeconds + 5;
                int pendingLooks = gpu.Characters is { } characterRenderer
                    ? TitleVisible ? characterRenderer.PendingLoads : session.CurrentSnapshot.Characters.Count(c => AwaitsLook(c, characterRenderer))
                    : 0;
                Console.WriteLine($"loading   {(TitleVisible ? "menu" : "game")} still preparing: terrain {(gpu.Streamer?.Idle == false ? "pending" : "ready")}, objects {gpu.Objects?.Pending ?? 0}{(gpu.Objects is { Pending: > 0 } po ? $" ({po.PendingDetail})" : "")}, foliage {gpu.Foliage?.LoadingPending ?? 0}{(gpu.Foliage is { LoadingPending: > 0 } pf ? $" ({pf.PendingDetail})" : "")}, population {(populationReady ? "ready" : "pending")}, characters {((TitleVisible ? TitleCharactersReady : charactersReady) ? "ready" : $"pending ({pendingLooks} looks; {gpu.Characters?.PendingLoads ?? 0} assets/textures)")}");
            }
            return;
        }
        if (TitleVisible && !titleWarmFinalized && loadingDisplay is { } display)
        {
            // Streaming allocates after Boot's collection: retire its uploads and compact before exposing the menu.
            titleWarmFinalized = true;
            Meitou.Game.Rendering.Frame.WorldFrame.FinishLoading(display.Context);
            smoothSeconds = 0;
            return;
        }
        readySeconds += seconds;
        smoothSeconds = seconds < SmoothFrame ? smoothSeconds + seconds : 0;
        // A machine that cannot draw the view at the smooth pace is let in after WarmLimit seconds of ready content, on the title as in play.
        if ((TitleVisible ? smoothSeconds < TitleSmoothRun : smoothSeconds < SmoothRun) && readySeconds < WarmLimit) return;
        warming = false;
        if (gpu.Characters is { } world) world.LoadingFocus = null;
        loading?.Finish();
        skyArrival?.Arrive();
        ReportLoading($"the view warmed up in {warmSeconds:0.0} s");
    }

    /// <summary>Whether a character is one the loading screen waits for (within the renderer's <see cref="Meitou.Game.Rendering.Characters.CharacterRenderer.LoadingReach"/> of its focus).</summary>
    bool InLoadingReach(Meitou.Simulation.CharacterSnapshot c) => gpu.Characters is not { LoadingFocus: { } f } r
        || Vector2.DistanceSquared(new Vector2(c.Position.X, c.Position.Z), f) <= r.LoadingReach * r.LoadingReach;

    /// <summary>
    /// Whether the loading screen still waits for a character's look: one in its reach whose drawn look is not prepared. An operator's tool look, once prepared, is
    /// the drawn one (<see cref="HeldLook"/>) and its plain look is listed no more, so that may never be built.
    /// </summary>
    bool AwaitsLook(Meitou.Simulation.CharacterSnapshot c, Meitou.Game.Rendering.Characters.CharacterRenderer characters) =>
        c.Appearance is { } look && InLoadingReach(c) && !characters.IsPrepared(look) && !HeldPrepared(look, c.HeldTool, characters);

    void AdvanceSkyArrival(double seconds)
    {
        if (skyArrival is not { } arrival) return;
        if (Volatile.Read(ref readHour) is var hour && !float.IsNaN(hour)) arrival.SetDestination(hour);
        arrival.Advance(seconds);
        if (!arrival.Finished) return;
        skyArrival = null;
        presentationHour = null;
    }

    void ApplySkyArrival()
    {
        presentationHour = skyArrival?.Apply(camera, TitleVisible ? TitleHour : (float)session.Clock.HourOfDay, warming);
    }

    /// <summary>The host's side of a new world: the camera focus sent again, the first tick (it publishes the snapshot), the draw list and the mouse state dropped, the menus closed.</summary>
    void SettleNewWorld()
    {
        sentFocus = false;
        SendFocus();
        session.World.RunTick();
        indexedFor = null;
        previousById.Clear();
        player.Reset();
        player.CloseFrontend();
        OnWorldSettled();
    }

    partial void OnWorldSettled();
}
