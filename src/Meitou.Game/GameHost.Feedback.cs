using System.Diagnostics;
using Meitou.Game.DevTools;
using Meitou.Game.Host;
using Meitou.Game.Ui;

namespace Meitou.Game;

/// <summary>
/// The feedback button's host side (PlayerInterface.Feedback.cs has the form): the frame after the press is drawn without the menus and kept as
/// the screenshot, then the form opens; Send saves the game into a temporary folder when asked and hands everything to <see cref="FeedbackSender"/>.
/// </summary>
sealed partial class GameHost
{
    /// <summary>The next frame is the feedback screenshot (drawn without the menus, the settings and the debug overlays).</summary>
    bool feedbackShotRequested;
    bool feedbackSendRequested;
    /// <summary>The PNG of the last feedback screenshot, encoded off the frame thread; null without one.</summary>
    Task<byte[]>? feedbackPicture;
    Task<FeedbackResult>? feedbackSend;
    string feedbackStatus = "", feedbackStatusShown = "";
    /// <summary>Where the camera was when the button was pressed, sent with every report made in a game; null outside one.</summary>
    string? feedbackLocation;

    /// <summary>The steps of the navmesh screenshot, made when a send asks for it: the zones round the camera load, the view is built, one frame is kept.</summary>
    enum FeedbackNavStage { None, Loading, Drawing, Capturing }

    FeedbackNavStage feedbackNavStage;
    Task? feedbackNavLoad;
    long feedbackNavSince;
    DebugOverlays.NavView feedbackNavRestore;
    /// <summary>The navmesh screenshot's PNG, encoded off the frame thread; null without one.</summary>
    Task<byte[]>? feedbackNavPicture;
    /// <summary>The navmesh screenshot was tried for this form (it may have failed), so a send goes on without waiting for it again.</summary>
    bool feedbackNavTried;
    /// <summary>The check for a newer Meitou, started when the form opens (<see cref="LatestRelease"/>).</summary>
    Task<string?>? feedbackNewer;

    void FeedbackAction(MyGuiEvent e)
    {
        if (e.Action == "feedback-open" && feedbackNavStage == FeedbackNavStage.None) feedbackShotRequested = true;
        else if (e.Action == "feedback-send") feedbackSendRequested = true;
    }

    /// <summary>In a game: there is a world to save, a camera to report and a navmesh to draw.</summary>
    bool FeedbackInGame => SavesAvailable(out _) && session.World.Player.Exists;

    /// <summary>
    /// The feedback frame was drawn: <paramref name="rgba"/> is its picture (rows from the top; null when it could not be read). The form opens; for the navmesh
    /// screenshot's frame the picture is kept and the send goes on.
    /// </summary>
    void FeedbackCaptured(byte[]? rgba, int width, int height)
    {
        feedbackShotRequested = false;
        var picture = rgba is null ? null : Task.Run(() => FeedbackSender.Png(rgba, width, height));
        if (feedbackNavStage == FeedbackNavStage.Capturing)
        {
            feedbackNavPicture = picture;
            EndFeedbackNav();
            return;
        }
        player.FeedbackPrefs = config.Feedback ??= new();   // the same object: what the form changes is saved with the settings
        feedbackPicture = picture;
        feedbackNavPicture = null;
        feedbackNavTried = false;
        bool inGame = FeedbackInGame;
        feedbackLocation = inGame ? CameraLocation() : null;
        player.FeedbackNewer = null;
        feedbackNewer = LatestRelease.NewerThan(BuildVersion.Tag);
        player.OpenFeedback(rgba is not null, inGame && nav is not null && debugOverlays.navOverlayRenderer is not null, inGame ? null : "only in a game");
    }

    /// <summary>The camera as text for the issue: what it looks at, where the eye is, and the heading (the numbers of the F11 statistics).</summary>
    string CameraLocation()
    {
        var c = System.Globalization.CultureInfo.InvariantCulture;
        var (t, e) = (camera.Target, camera.Eye);
        return string.Create(c, $"target {t.X:0}, {t.Y:0}, {t.Z:0}; eye {e.X:0}, {e.Y:0}, {e.Z:0}; yaw {camera.Yaw * 180 / MathF.PI:0}, pitch {camera.Pitch * 180 / MathF.PI:0}, boom {camera.Distance:0}");
    }

    /// <summary>Starts the navmesh screenshot: Meitou's view on, the zones round the camera loading. False when there is no navmesh to draw.</summary>
    bool StartFeedbackNav()
    {
        if (nav is null || debugOverlays.navOverlayRenderer is not { } overlay) return false;
        feedbackNavRestore = debugOverlays.navView;
        debugOverlays.navView = DebugOverlays.NavView.Meitou;
        overlay.Visible = true;
        FinishNavUnload();
        long now = Stopwatch.GetTimestamp();
        var loads = new List<Task>();
        foreach (var z in Meitou.Simulation.Population.ZoneActivation.ZonesAround(camera.Target, 1))
        {
            if (!navZones.ContainsKey(z)) loads.Add(nav.LoadZone(z));
            navZones[z] = now;
        }
        feedbackNavLoad = Task.WhenAll(loads);
        feedbackNavSince = now;
        feedbackNavStage = FeedbackNavStage.Loading;
        return true;
    }

    /// <summary>Once per frame while the navmesh screenshot is made: waits for the zones and the built view, then asks for the frame (<see cref="FeedbackCaptured"/> takes it).</summary>
    void StepFeedbackNav()
    {
        if (feedbackNavStage == FeedbackNavStage.None) return;
        double waited = Stopwatch.GetElapsedTime(feedbackNavSince).TotalSeconds;
        if (nav is null || waited > 30) { EndFeedbackNav(); return; }   // never held up for long: the report goes without it
        if (feedbackNavStage == FeedbackNavStage.Loading && (feedbackNavLoad!.IsCompleted || waited > 10)) feedbackNavStage = FeedbackNavStage.Drawing;
        if (feedbackNavStage == FeedbackNavStage.Drawing
            && (debugOverlays.navOverlayBuild is null && debugOverlays.navOverlayShown == (nav.Walkability.World, DebugOverlays.NavView.Meitou) || waited > 20))
        {
            feedbackNavStage = FeedbackNavStage.Capturing;
            feedbackShotRequested = true;
        }
    }

    /// <summary>The navmesh screenshot is done (or given up): the view goes back to what it was and the send goes on.</summary>
    void EndFeedbackNav()
    {
        feedbackNavStage = FeedbackNavStage.None;
        feedbackNavTried = true;
        feedbackShotRequested = false;
        debugOverlays.navView = feedbackNavRestore;
        if (debugOverlays.navOverlayRenderer is { } overlay) overlay.Visible = debugOverlays.navView != DebugOverlays.NavView.Off;
        feedbackSendRequested = true;
    }

    /// <summary>Once per frame, between ticks (a save may be made): a requested send starts, a finished one reports.</summary>
    void HandleFeedback()
    {
        if (feedbackSend is { IsCompleted: true } done)
        {
            feedbackSend = null;
            player.FeedbackSending = false;
            var result = done.Result;
            if (result.Error is { } error) player.FeedbackProgress(error, error: true);
            else
            {
                Console.WriteLine($"feedback  sent: issue #{result.Number} {result.Url}");
                player.FeedbackSent(result.Number, result.Url!);
            }
        }
        else if (feedbackSend is not null && Volatile.Read(ref feedbackStatus) is var status && status != feedbackStatusShown)
            player.FeedbackProgress(feedbackStatusShown = status);
        if (feedbackNewer is { IsCompleted: true } checkedNewer)
        {
            feedbackNewer = null;
            player.FeedbackNewer = checkedNewer.IsCompletedSuccessfully ? checkedNewer.Result : null;
        }
        StepFeedbackNav();
        if (!feedbackSendRequested) return;
        feedbackSendRequested = false;
        if (feedbackSend is not null || feedbackNavStage != FeedbackNavStage.None) return;
        var form = player.FeedbackValues();
        config.Save();   // the ticks and Discord name, kept for the next report
        if (form.Navmesh && !feedbackNavTried && StartFeedbackNav())
        {
            // The navmesh screenshot first (a few frames); its end asks for the send again.
            player.FeedbackSending = true;
            player.FeedbackProgress("Taking the navmesh screenshot...");
            return;
        }
        string? saveFolder = null;
        Task? saveWritten = null;
        if (form.Save)
        {
            if (Busy()) { player.FeedbackSending = false; player.FeedbackProgress("A save or load is still in progress. Try again in a moment.", error: true); return; }
            saveFolder = Path.Combine(Path.GetTempPath(), $"meitou-feedback-{Guid.NewGuid():N}", $"feedback-{DateTime.Now:yyyyMMdd-HHmmss}");
            var before = saveWrite;
            QuickSaveNow(saveFolder, feedback: true);
            if (ReferenceEquals(saveWrite, before)) { player.FeedbackSending = false; player.FeedbackProgress("The game could not be saved (log.txt says why). Untick the save to send without it.", error: true); return; }
            saveWritten = saveWrite;
        }
        string[] mods = [.. Meitou.Data.LoadOrder.FromInstall(install).Entries.Select(e => e.Name)];
        string? log = form.Log && File.Exists(Path.Combine(AppContext.BaseDirectory, "log.txt")) ? Path.Combine(AppContext.BaseDirectory, "log.txt") : null;
        var package = new FeedbackPackage(form, BuildVersion.Tag, BuildVersion.Commit, FeedbackSender.Platform(), mods, form.Screenshot ? feedbackPicture : null,
            form.Navmesh ? feedbackNavPicture : null, feedbackLocation, saveFolder, saveWritten, log);
        feedbackStatus = feedbackStatusShown = form.Save ? "Saving the game..." : "Sending...";
        player.FeedbackProgress(feedbackStatus);
        player.FeedbackSending = true;
        feedbackSend = Task.Run(() => FeedbackSender.Send(package, s => Volatile.Write(ref feedbackStatus, s)));
    }
}
