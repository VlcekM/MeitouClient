using System.Diagnostics;
using Meitou.Game.DevTools;
using Meitou.Game.Ui;

namespace Meitou.Game.Host;

/// <summary>
/// The feedback button's host side (FeedbackPanel has the form): the frame after the press is drawn without the menus and kept as
/// the screenshot, then the form opens; Send saves the game into a temporary folder when asked and hands everything to <see cref="FeedbackSender"/>.
/// </summary>
sealed class FeedbackCapture(GameHost host)
{
    /// <summary>
    /// The next frame is the feedback screenshot (drawn without the menus, the settings and the debug overlays).
    /// </summary>
    internal bool FeedbackShotRequested;
    /// <summary>
    /// The form's Send was pressed: the next frame starts the send.
    /// </summary>
    bool _feedbackSendRequested;
    /// <summary>
    /// The PNG of the last feedback screenshot, encoded off the frame thread; null without one.
    /// </summary>
    Task<byte[]>? _feedbackPicture;
    /// <summary>
    /// The report's send on a worker; null when none runs.
    /// </summary>
    Task<FeedbackResult>? _feedbackSend;
    /// <summary>
    /// The send's progress line (written by the worker), and the one the form shows.
    /// </summary>
    string _feedbackStatus = "", _feedbackStatusShown = "";
    /// <summary>
    /// Where the camera was when the button was pressed, sent with every report made in a game; null outside one.
    /// </summary>
    string? _feedbackLocation;

    /// <summary>
    /// The steps of the navmesh screenshot, made when a send asks for it: the zones round the camera load, the view is built, one frame is kept.
    /// </summary>
    enum FeedbackNavStage
    {
        /// <summary>
        /// No navmesh screenshot under way.
        /// </summary>
        None,
        /// <summary>
        /// The zones round the camera load (at most 10 s).
        /// </summary>
        Loading,
        /// <summary>
        /// The navmesh view is built and drawn.
        /// </summary>
        Drawing,
        /// <summary>
        /// The frame being kept as the navmesh screenshot.
        /// </summary>
        Capturing
    }

    /// <summary>
    /// The navmesh screenshot's step.
    /// </summary>
    FeedbackNavStage _feedbackNavStage;
    /// <summary>
    /// The navmesh loads of the zones round the camera.
    /// </summary>
    Task? _feedbackNavLoad;
    /// <summary>
    /// When the navmesh loads started (a <see cref="Stopwatch"/> timestamp).
    /// </summary>
    long _feedbackNavSince;
    /// <summary>
    /// The navmesh overlay as it was before the screenshot, put back after it.
    /// </summary>
    DebugOverlays.NavView _feedbackNavRestore;
    /// <summary>
    /// The navmesh screenshot's PNG, encoded off the frame thread; null without one.
    /// </summary>
    Task<byte[]>? _feedbackNavPicture;
    /// <summary>
    /// The navmesh screenshot was tried for this form (it may have failed), so a send goes on without waiting for it again.
    /// </summary>
    bool _feedbackNavTried;
    /// <summary>
    /// The check for a newer Meitou, started when the form opens (<see cref="LatestRelease"/>).
    /// </summary>
    Task<string?>? _feedbackNewer;

    /// <summary>
    /// A feedback button's action: open takes the screenshot first (unless the navmesh one is under way), send asks for the send.
    /// </summary>
    internal void FeedbackAction(MyGuiEvent e)
    {
        if (e.Action == "feedback-open" && _feedbackNavStage == FeedbackNavStage.None) FeedbackShotRequested = true;
        else if (e.Action == "feedback-send") _feedbackSendRequested = true;
    }

    /// <summary>
    /// In a game: there is a world to save, a camera to report and a navmesh to draw.
    /// </summary>
    bool FeedbackInGame => host.Saves.SavesAvailable(out _) && host.Session.World.Player.Exists;

    /// <summary>
    /// The feedback frame was drawn: <paramref name="rgba"/> is its picture (rows from the top; null when it could not be read). The form opens; for
    /// the navmesh screenshot's frame the picture is kept and the send goes on.
    /// </summary>
    internal void FeedbackCaptured(byte[]? rgba, int width, int height)
    {
        FeedbackShotRequested = false;
        var picture = rgba is null ? null : Task.Run(() => FeedbackSender.Png(rgba, width, height));
        if (_feedbackNavStage == FeedbackNavStage.Capturing)
        {
            _feedbackNavPicture = picture;
            EndFeedbackNav();
            return;
        }
        // The same object: what the form changes is saved with the settings.
        host.Player.FeedbackPanel.FeedbackPrefs = host.Config.Feedback ??= new();
        _feedbackPicture = picture;
        _feedbackNavPicture = null;
        _feedbackNavTried = false;
        bool inGame = FeedbackInGame;
        _feedbackLocation = inGame ? CameraLocation() : null;
        host.Player.FeedbackPanel.FeedbackNewer = null;
        _feedbackNewer = LatestRelease.NewerThan(BuildVersion.Tag);
        host.Player.FeedbackPanel.OpenFeedback(rgba is not null, inGame && host.Nav is not null && host.DebugOverlays.NavOverlayRenderer is not null,
            inGame ? null : "only in a game");
    }

    /// <summary>
    /// The camera as text for the issue: what it looks at, where the eye is, and the heading (the numbers of the F11 statistics).
    /// </summary>
    string CameraLocation()
    {
        var c = System.Globalization.CultureInfo.InvariantCulture;
        var (t, e) = (host.Camera.Target, host.Camera.Eye);
        return string.Create(c, $"target {t.X:0}, {t.Y:0}, {t.Z:0}; eye {e.X:0}, {e.Y:0}, {e.Z:0}; ")
            + string.Create(c, $"yaw {host.Camera.Yaw * 180 / MathF.PI:0}, pitch {host.Camera.Pitch * 180 / MathF.PI:0}, ")
            + string.Create(c, $"boom {host.Camera.Distance:0}");
    }

    /// <summary>
    /// Starts the navmesh screenshot: Meitou's view on, the zones round the camera loading. False when there is no navmesh to draw.
    /// </summary>
    bool StartFeedbackNav()
    {
        if (host.Nav is null || host.DebugOverlays.NavOverlayRenderer is not { } overlay) return false;
        _feedbackNavRestore = host.DebugOverlays.NavViewMode;
        host.DebugOverlays.NavViewMode = DebugOverlays.NavView.Meitou;
        overlay.Visible = true;
        host.FinishNavUnload();
        long now = Stopwatch.GetTimestamp();
        var loads = new List<Task>();
        foreach (var z in Simulation.Population.ZoneActivation.ZonesAround(host.Camera.Target, 1))
        {
            if (!host.NavZones.ContainsKey(z)) loads.Add(host.Nav.LoadZone(z));
            host.NavZones[z] = now;
        }
        _feedbackNavLoad = Task.WhenAll(loads);
        _feedbackNavSince = now;
        _feedbackNavStage = FeedbackNavStage.Loading;
        return true;
    }

    /// <summary>
    /// Once per frame while the navmesh screenshot is made: waits for the zones and the built view, then asks for the frame (<see
    /// cref="FeedbackCaptured"/> takes it).
    /// </summary>
    void StepFeedbackNav()
    {
        if (_feedbackNavStage == FeedbackNavStage.None) return;
        double waited = Stopwatch.GetElapsedTime(_feedbackNavSince).TotalSeconds;
        // never held up for long: the report goes without it
        if (host.Nav is null || waited > 30)
        {
            EndFeedbackNav();
            return;
        }
        if (_feedbackNavStage == FeedbackNavStage.Loading && (_feedbackNavLoad!.IsCompleted || waited > 10))
            _feedbackNavStage = FeedbackNavStage.Drawing;
        if (_feedbackNavStage == FeedbackNavStage.Drawing
            && (host.DebugOverlays.NavOverlayBuild is null
                && host.DebugOverlays.NavOverlayShown == (host.Nav.Walkability.World, DebugOverlays.NavView.Meitou) || waited > 20))
        {
            _feedbackNavStage = FeedbackNavStage.Capturing;
            FeedbackShotRequested = true;
        }
    }

    /// <summary>
    /// The navmesh screenshot is done (or given up): the view goes back to what it was and the send goes on.
    /// </summary>
    void EndFeedbackNav()
    {
        _feedbackNavStage = FeedbackNavStage.None;
        _feedbackNavTried = true;
        FeedbackShotRequested = false;
        host.DebugOverlays.NavViewMode = _feedbackNavRestore;
        if (host.DebugOverlays.NavOverlayRenderer is { } overlay) overlay.Visible = host.DebugOverlays.NavViewMode != DebugOverlays.NavView.Off;
        _feedbackSendRequested = true;
    }

    /// <summary>
    /// Once per frame, between ticks (a save may be made): a requested send starts, a finished one reports.
    /// </summary>
    internal void HandleFeedback()
    {
        if (_feedbackSend is { IsCompleted: true } done)
        {
            _feedbackSend = null;
            host.Player.FeedbackPanel.FeedbackSending = false;
            var result = done.Result;
            if (result.Error is { } error) host.Player.FeedbackPanel.FeedbackProgress(error, error: true);
            else
            {
                Console.WriteLine($"feedback  sent: issue #{result.Number} {result.Url}");
                host.Player.FeedbackPanel.FeedbackSent(result.Number, result.Url!);
            }
        }
        else if (_feedbackSend is not null && Volatile.Read(ref _feedbackStatus) is var status && status != _feedbackStatusShown)
            host.Player.FeedbackPanel.FeedbackProgress(_feedbackStatusShown = status);
        if (_feedbackNewer is { IsCompleted: true } checkedNewer)
        {
            _feedbackNewer = null;
            host.Player.FeedbackPanel.FeedbackNewer = checkedNewer.IsCompletedSuccessfully ? checkedNewer.Result : null;
        }
        StepFeedbackNav();
        if (!_feedbackSendRequested) return;
        _feedbackSendRequested = false;
        if (_feedbackSend is not null || _feedbackNavStage != FeedbackNavStage.None) return;
        var form = host.Player.FeedbackPanel.FeedbackValues();
        host.Config.Save();   // the ticks and Discord name, kept for the next report
        if (form.Navmesh && !_feedbackNavTried && StartFeedbackNav())
        {
            // The navmesh screenshot first (a few frames); its end asks for the send again.
            host.Player.FeedbackPanel.FeedbackSending = true;
            host.Player.FeedbackPanel.FeedbackProgress("Taking the navmesh screenshot...");
            return;
        }
        string? saveFolder = null;
        Task? saveWritten = null;
        if (form.Save)
        {
            if (host.Saves.Busy())
            {
                host.Player.FeedbackPanel.FeedbackSending = false;
                host.Player.FeedbackPanel.FeedbackProgress("A save or load is still in progress. Try again in a moment.", error: true);
                return;
            }
            saveFolder = Path.Combine(Path.GetTempPath(), $"meitou-feedback-{Guid.NewGuid():N}", $"feedback-{DateTime.Now:yyyyMMdd-HHmmss}");
            var before = host.Saves.SaveWrite;
            host.Saves.QuickSaveNow(saveFolder, feedback: true);
            if (ReferenceEquals(host.Saves.SaveWrite, before))
            {
                host.Player.FeedbackPanel.FeedbackSending = false;
                host.Player.FeedbackPanel.FeedbackProgress(
                    "The game could not be saved (log.txt says why). Untick the save to send without it.", error: true);
                return;
            }
            saveWritten = host.Saves.SaveWrite;
        }
        string[] mods = [.. Data.LoadOrder.FromInstall(host.Install).Entries.Select(e => e.Name)];
        string? log = form.Log && File.Exists(Path.Combine(AppContext.BaseDirectory, "log.txt"))
            ? Path.Combine(AppContext.BaseDirectory, "log.txt") : null;
        var package = new FeedbackPackage(form, BuildVersion.Tag, BuildVersion.Commit, FeedbackSender.Platform(), mods,
            form.Screenshot ? _feedbackPicture : null, form.Navmesh ? _feedbackNavPicture : null, _feedbackLocation, saveFolder, saveWritten, log);
        _feedbackStatus = _feedbackStatusShown = form.Save ? "Saving the game..." : "Sending...";
        host.Player.FeedbackPanel.FeedbackProgress(_feedbackStatus);
        host.Player.FeedbackPanel.FeedbackSending = true;
        _feedbackSend = Task.Run(() => FeedbackSender.Send(package, s => Volatile.Write(ref _feedbackStatus, s)));
    }
}