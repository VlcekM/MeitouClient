using System.Diagnostics;
using System.Numerics;
using Meitou.Game.Title;

namespace Meitou.Game;

/// <summary>
/// Loads between scenes (a new game, a loaded save, the main menu) under the loading screen (docs/engine.md "Loading"): a load is a list of stages
/// that run on this thread at frame boundaries, one per frame, each after a covered frame was drawn; a stage may hand back work to wait for (a
/// worker's read, the navmesh), and the frames go on drawing the loading screen while it runs. After the last stage, and after the start, the world
/// is drawn under the loading screen until its frames run smoothly (the first frames of a new place build pipelines and stream its terrain and
/// objects), so play starts on a smooth picture.
/// </summary>
sealed partial class GameHost
{
    internal sealed class SceneLoad
    {
        public readonly Queue<(string Step, Func<Task?> Run, bool DuringAscent)> Stages = new();
        public Task? Waiting;
        /// <summary>
        /// A covered frame was drawn since the last stage ran: the next one may run (it may hold the thread).
        /// </summary>
        public bool Shown;
        public readonly Stopwatch Clock = Stopwatch.StartNew();
    }

    /// <summary>
    /// A stage found the load cannot go on; the message is shown, the world as it was stays.
    /// </summary>
    internal sealed class SceneLoadFailed(string message) : Exception(message);

    internal SceneLoad? ActiveSceneLoad;
    internal SkyArrival? SkyArrival;
    internal float? PresentationHour;

    /// <summary>
    /// Photo mode's hour of day, drawn in place of the game clock's (the sun, sky, stars and lamps follow it; the clock, the weather and the
    /// simulation do not); null outside photo mode or while it shows the game's own time.
    /// </summary>
    internal float? PhotoHour;

    /// <summary>
    /// Photo mode is on: the right button turns the view like the Orbit action.
    /// </summary>
    internal bool PhotoLook;

    /// <summary>
    /// The photo mode (null before the interactive loop makes it).
    /// </summary>
    internal Host.Photo.PhotoMode? Photo;
    /// <summary>
    /// The hour of the save being read, set by the reading worker as soon as its CAMERA is read (NaN until then), for the sky arrival.
    /// </summary>
    internal float ReadHour = float.NaN;
    internal bool Warming;
    internal double SmoothSeconds;
    double _warmSeconds;
    internal double ReadySeconds;
    double _nextWarmReport;
    /// <summary>
    /// Seconds of the warm-up the VRAM guard has held its pressure without a break.
    /// </summary>
    double _guardSeconds;
    bool _titleWarmFinalized;
    /// <summary>
    /// After content is ready, gameplay's smooth-frame target and run, and the pacing limit (title too); the title requires <see
    /// cref="TitleSmoothRun"/>.
    /// </summary>
    const double SmoothFrame = 0.04, SmoothRun = 0.5, WarmLimit = 5;
    const double TitleSmoothRun = 1;
    /// <summary>
    /// Seconds of the guard's pressure after which the content it holds back is not waited for: nothing new loads until the use falls (VramGuard), so
    /// on a card short of memory it may never come (as <c>WorldObjectRenderer.Settle</c>).
    /// </summary>
    const double GuardWait = 5;

    /// <summary>
    /// Loading, warm-up, arrival or the loading overlay's final reveal still blocks input.
    /// </summary>
    internal bool Covered => ActiveSceneLoad is not null || Warming || SkyArrival is not null || Loading is { Exiting: true };

    internal static (string Step, Func<Task?> Run, bool DuringAscent) Stage(string step, Func<Task?> run, bool duringAscent = false) => (step, run,
        duringAscent || step == "read");

    internal void StartSceneLoad(bool mainMenuTransition, float? destinationHour, params (string Step, Func<Task?> Run, bool DuringAscent)[] stages)
    {
        if (ActiveSceneLoad is not null) return;
        ActiveSceneLoad = new();
        if (Player.UiMode == Data.Ui.UiMode.Meitou && (mainMenuTransition ? Config.MainMenuTransitions : Config.InGameTransitions))
            SkyArrival = new(Camera, PresentationHour ?? (TitleScreen.TitleVisible ? TitleScreen.TitleHour : (float)Session.Clock.HourOfDay),
                mainMenuTransition ? Config.MainMenuTransitionSpeed : Config.InGameTransitionSpeed, immediate: !TitleScreen.TitleVisible);
        if (destinationHour is { } hour) SkyArrival?.SetDestination(hour);
        Volatile.Write(ref ReadHour, float.NaN);
        foreach (var stage in stages) ActiveSceneLoad.Stages.Enqueue(stage);
        Loading?.Restart();
        LoadingSteps = SceneSteps;
        SetLoadingStep(stages.Length > 0 ? stages[0].Step : "build");
    }

    /// <summary>
    /// Once a frame, between ticks: the next stage of the running load when its work is done and a covered frame was drawn.
    /// </summary>
    internal void StepSceneLoad()
    {
        if (ActiveSceneLoad is not { } load || !load.Shown || load.Waiting is { IsCompleted: false }) return;
        // Reading a save can overlap the ascent; world mutations wait until the ground is out of view.
        if (SkyArrival is { AtStars: false } && load.Stages.TryPeek(out var next) && !next.DuringAscent) return;
        try
        {
            if (load.Waiting is { IsFaulted: true } failed) throw failed.Exception!.GetBaseException();
            if (!load.Stages.TryDequeue(out var stage))
            {
                // The sky fades to black before the destination is drawn under it.
                if (SkyArrival is { Fade: < 1 } covering) { covering.Cover(); return; }
                ActiveSceneLoad = null;
                ReportLoading($"scene loaded in {load.Clock.Elapsed.TotalSeconds:0.0} s");
                BeginWarm();
                return;
            }
            SetLoadingStep(stage.Step);
            Console.WriteLine($"loading   stage {stage.Step} starts at {load.Clock.Elapsed.TotalSeconds:0.0} s{(SkyArrival is { } flight ? $" (sky {(flight.AtStars ? "at the stars" : "ascending")})" : "")}");
            load.Waiting = stage.Run();
            load.Shown = false;
        }
        catch (Exception e)
        {
            ActiveSceneLoad = null;
            SkyArrival = null;
            PresentationHour = null;
            if (e is not SceneLoadFailed) Console.Error.WriteLine($"scene     load failed: {e}");
            Player.Notice(e is SceneLoadFailed ? e.Message : $"Load failed: {e.Message}", true);
        }
    }

    /// <summary>
    /// The world is drawn under the loading screen until its frames run smoothly.
    /// </summary>
    internal void BeginWarm()
    {
        if (Gpu.Characters is { } characters) characters.LoadingFocus = null;   // an earlier warm-up cut short may have left it
        if (Loading is null)
        {
            SkyArrival?.Arrive();
            return;
        }
        // The Metal host draws its own frames and has no Vulkan loading display to measure a warm-up with: let the loading screen
        // slide away and start the sky's descent at once.
        if (_loadingDisplay is null)
        {
            Loading.Finish();
            SkyArrival?.Arrive();
            return;
        }
        (Warming, SmoothSeconds, _warmSeconds, ReadySeconds, _guardSeconds) = (true, 0, 0, 0, 0);
        _nextWarmReport = 5;
        _titleWarmFinalized = false;
        SetLoadingStep("view");
    }

    /// <summary>
    /// After a covered frame of <paramref name="seconds"/>: the warm-up ends after <see cref="SmoothRun"/> seconds of smooth frames in a row.
    /// </summary>
    internal void CoveredFrame(double seconds)
    {
        if (ActiveSceneLoad is { } load) load.Shown = true;
        // The frames under the cover count for the loading report (LoadingFrame counts the start's).
        _loadingFrames++;
        if (seconds * 1000 > _longestGap.Ms) _longestGap = (seconds * 1000, _currentStep);
        if (!Warming) return;
        _warmSeconds += seconds;
        // The Faithful title is the original's opaque picture: nothing of the world shows behind it, so its view is not warmed (the original loads
        // the world only for a game). The start's splash plays out first.
        if (Player.TitleOpen && Player.UiMode == Data.Ui.UiMode.Faithful && CharacterEditor.PendingCreation is null)
        {
            if (Loading is { SplashPlaying: true }) return;
            Warming = false;
            if (Gpu.Characters is { } titleCharacters) titleCharacters.LoadingFocus = null;
            Loading?.Finish();
            ReportLoading("the Faithful title shows");
            return;
        }
        // A ready character mesh alone is insufficient: finish terrain, scenery and deferred textures too.
        // Content readiness has no pacing timeout that can expose missing NPCs or white stand-ins.
        bool populationReady = TitleScreen.TitleVisible || Session.World.PreparePopulation(Session.Camera.Current.Target);
        // A game's view waits for the characters round the camera's focus only (CharacterRenderer.LoadingFocus); the rest are prepared once the
        // screen lifts.
        var focus = Session.Camera.Current.Target;
        if (!TitleScreen.TitleVisible && Gpu.Characters is { } near) near.LoadingFocus = new Vector2(focus.X, focus.Z);
        bool charactersReady = Gpu.Characters is not { } characters || !characters.ContentPending &&
            !Session.CurrentSnapshot.Characters.Any(c => AwaitsLook(c, characters));
        bool sceneReady = TitleScreen.TitleVisible ? TitleScreen.TitleSceneReady : populationReady && charactersReady &&
            Gpu.Streamer is not { Idle: false } && !(Gpu.Objects?.LoadingPending > 0) && !(Gpu.Foliage?.LoadingPending > 0);
        _guardSeconds = Gpu.Guard is { Pressure: true } ? _guardSeconds + seconds : 0;
        if (!sceneReady && _guardSeconds >= GuardWait && (TitleScreen.TitleVisible || populationReady))
        {
            sceneReady = true;
            if (_guardSeconds - seconds < GuardWait)
                Console.WriteLine($"loading   stopped waiting for content held back by memory pressure (VramGuard, {Gpu.Guard!.Fraction * 100:0}% of the {Gpu.Guard.BudgetBytes / 1048576} MB budget)");
        }
        if (CharacterEditor.PendingCreation is not null && !TitleScreen.TitleVisible && charactersReady)
        {
            // A new game shows the character editor before the world: the loading screen lifts once the squad is drawable, no sky arrival, and the
            // world's own warm-up runs when the editor closes (ResumeAfterCreation).
            (Warming, SkyArrival, PresentationHour) = (false, null, null);
            if (Gpu.Characters is { } editorCharacters) editorCharacters.LoadingFocus = null;
            Loading?.Finish();
            ReportLoading($"the squad was ready in {_warmSeconds:0.0} s; the editor opens first");
            return;
        }
        if (!sceneReady)
        {
            SmoothSeconds = ReadySeconds = 0;
            if (_warmSeconds >= _nextWarmReport)
            {
                _nextWarmReport = _warmSeconds + 5;
                int pendingLooks = Gpu.Characters is { } characterRenderer
                    ? TitleScreen.TitleVisible ? characterRenderer.PendingLoads
                        : Session.CurrentSnapshot.Characters.Count(c => AwaitsLook(c, characterRenderer))
                    : 0;
                Console.WriteLine($"loading   {(TitleScreen.TitleVisible ? "menu" : "game")} still preparing: terrain {(Gpu.Streamer?.Idle == false ? "pending" : "ready")}, objects {Gpu.Objects?.LoadingPending ?? 0}{(Gpu.Objects is { LoadingPending: > 0 } po ? $" ({po.PendingDetail})" : "")}, foliage {Gpu.Foliage?.LoadingPending ?? 0}{(Gpu.Foliage is { LoadingPending: > 0 } pf ? $" ({pf.PendingDetail})" : "")}, population {(populationReady ? "ready" : "pending")}, characters {((TitleScreen.TitleVisible ? TitleScreen.TitleCharactersReady : charactersReady) ? "ready" : $"pending ({pendingLooks} looks; {Gpu.Characters?.PendingLoads ?? 0} assets/textures)")}, streaming {Rendering.Frame.BackgroundWork.Describe()}, guard {(Gpu.Guard is { } gd ? $"{gd.Fraction:0.00} pressure {gd.Pressure}" : "-")}");
            }
            return;
        }
        if (TitleScreen.TitleVisible && !_titleWarmFinalized && _loadingDisplay is { } display)
        {
            // Streaming allocates after Boot's collection: retire its uploads and compact before exposing the menu.
            _titleWarmFinalized = true;
            Rendering.Frame.WorldFrame.FinishLoading(display.Context);
            SmoothSeconds = 0;
            return;
        }
        ReadySeconds += seconds;
        SmoothSeconds = seconds < SmoothFrame ? SmoothSeconds + seconds : 0;
        // A machine that cannot draw the view at the smooth pace is let in after WarmLimit seconds of ready content, on the title as in play.
        if ((TitleScreen.TitleVisible ? SmoothSeconds < TitleSmoothRun : SmoothSeconds < SmoothRun) && ReadySeconds < WarmLimit) return;
        Warming = false;
        if (Gpu.Characters is { } world) world.LoadingFocus = null;
        Loading?.Finish();
        SkyArrival?.Arrive();
        ReportLoading($"the view warmed up in {_warmSeconds:0.0} s");
    }

    /// <summary>
    /// Whether a character is one the loading screen waits for (within the renderer's <see
    /// cref="Meitou.Game.Rendering.Characters.CharacterRenderer.LoadingReach"/> of its focus).
    /// </summary>
    bool InLoadingReach(Simulation.CharacterSnapshot c) => Gpu.Characters is not { LoadingFocus: { } f } r
        || Vector2.DistanceSquared(new Vector2(c.Position.X, c.Position.Z), f) <= r.LoadingReach * r.LoadingReach;

    /// <summary>
    /// Whether the loading screen still waits for a character's look: one in its reach whose drawn look is not prepared. An operator's tool look,
    /// once prepared, is the drawn one (<see cref="HeldLook"/>) and its plain look is listed no more, so that may never be built.
    /// </summary>
    bool AwaitsLook(Simulation.CharacterSnapshot c, Rendering.Characters.CharacterRenderer characters) =>
        c.Appearance is { } look && InLoadingReach(c) && !characters.IsPrepared(look) && !HeldPrepared(look, c.HeldTool, characters);

    internal void AdvanceSkyArrival(double seconds)
    {
        if (SkyArrival is not { } arrival) return;
        if (Volatile.Read(ref ReadHour) is var hour && !float.IsNaN(hour)) arrival.SetDestination(hour);
        arrival.Advance(seconds);
        if (!arrival.Finished) return;
        SkyArrival = null;
        PresentationHour = null;
    }

    internal void ApplySkyArrival()
    {
        PresentationHour = SkyArrival?.Apply(Camera, TitleScreen.TitleVisible ? TitleScreen.TitleHour : (float)Session.Clock.HourOfDay, Warming);
    }

    /// <summary>
    /// The host's side of a new world: the camera focus sent again, the first tick (it publishes the snapshot), the draw list and the mouse state
    /// dropped, the menus closed.
    /// </summary>
    internal void SettleNewWorld()
    {
        _sentFocus = false;
        SendFocus();
        Session.World.RunTick();
        IndexedFor = null;
        PreviousById.Clear();
        Player.Reset();
        Player.Frontend.CloseFrontend();
        OnWorldSettled();
    }

    partial void OnWorldSettled();
}