using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Game.Settings;
using Meitou.Game.Title;
using Meitou.Game.Tutorials;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;
using Meitou.Simulation.Saving;

namespace Meitou.Game.Host;

/// <summary>
/// Quicksave (F5) and quickload (F9), docs/simulation.md "Saves". The key only sets a request (a control tick runs inside
/// <c>WorldSession.Advance</c>); the frame loop takes the request right after <c>Advance</c> returns, when no world tick is in flight. A save
/// captures the world there, on the thread that ticks it (a few milliseconds), and writes the files on a worker; a load reads the files on a worker
/// and swaps in a new world there. The slot is Meitou's own <c>%LOCALAPPDATA%\Meitou\save\quicksave</c> (<see cref="QuickSave"/>), never a folder of
/// the original game.
/// </summary>
sealed class GameSaves(GameHost host)
{
    #region Requests

    /// <summary>
    /// Requests for a quicksave and a quickload (F5, F9), taken by <see cref="HandleSaves"/> on the next frame.
    /// </summary>
    internal bool QuickSaveRequested, QuickLoadRequested;
    /// <summary>
    /// The autosave (docs/formats/save.md "Autosave"): real seconds run unpaused since the last one.
    /// </summary>
    double _autosaveSeconds;
    /// <summary>
    /// An autosave announced this frame, written the next.
    /// </summary>
    bool _autosaveDue;
    /// <summary>
    /// Requests to save into and to load from a chosen save folder, taken by <see cref="HandleSaves"/>; ReadingFolder is the folder a load or an
    /// import reads.
    /// </summary>
    internal string? SaveRequestedFolder, LoadRequestedFolder, ReadingFolder;
    /// <summary>
    /// A request to import a save folder into a new world with the given options, taken by <see cref="HandleSaves"/>.
    /// </summary>
    internal (string Folder, ImportOptions Options)? ImportRequested;

    /// <summary>
    /// Once per frame, between ticks: the notices of the write worker, the requests of the keys, the autosave, a finished read. <paramref
    /// name="realDt"/>: the frame's real seconds.
    /// </summary>
    internal void HandleSaves(double realDt)
    {
        while (_saveNotices.TryDequeue(out var notice)) host.Player.Notice(notice.Text, notice.Error);
        host.FeedbackCapture.HandleFeedback();
        PrintSavedLog();
        Autosave(realDt);
        if (QuickSaveRequested)
        {
            QuickSaveRequested = false;
            QuickSaveNow();
        }
        if (QuickLoadRequested)
        {
            QuickLoadRequested = false;
            BeginQuickLoad();
        }
        if (SaveRequestedFolder is { } to)
        {
            SaveRequestedFolder = null;
            QuickSaveNow(to);
        }
        if (LoadRequestedFolder is { } sourceFolder)
        {
            LoadRequestedFolder = null;
            BeginQuickLoad(sourceFolder);
        }
        if (ImportRequested is { } import)
        {
            ImportRequested = null;
            BeginImport(import.Folder, import.Options);
        }

        if (PreparedBase is null && SavesAvailable(out _) && host.Session.World.Player.Exists)
        {
            var (data, from, options) = (host.PopData!, LoadedSave, CaptureOptions());
            (PreparedBase, _preparedFor) = (Task.Run(() => SaveCapture.PrepareBase(data, from, options)), from);
        }
    }

    /// <summary>
    /// The original's autosave (<c>FUN_14047be70</c>, docs/formats/save.md "Autosave"): while <c>Auto save</c> is on and nothing loads, real time run
    /// unpaused adds up; past <c>Auto save time (minutes)</c> it shows "Autosaving..." and the next frame writes the slot after the last one,
    /// <c>autosave0</c> to <c>autosave2</c> in turn.
    /// </summary>
    void Autosave(double realDt)
    {
        if (_autosaveDue)
        {
            _autosaveDue = false;
            host.Config.AutosaveIndex = (host.Config.AutosaveIndex + 1) % QuickSave.AutosaveSlots;
            host.Config.Save();
            QuickSaveNow(QuickSave.AutosaveFolder(host.Config.AutosaveIndex));
            return;
        }
        if (!Gameplay.AutoSave || host.Covered || host.Session.Simulation.IsPaused || !host.Session.World.Player.Exists || !SavesAvailable(out _))
            return;
        _autosaveSeconds += realDt;
        if (_autosaveSeconds < Gameplay.AutoSaveMinutes * 60 || Busy()) return;
        _autosaveSeconds = 0;
        _autosaveDue = true;
        host.Player.Notice("Autosaving...");
    }

    /// <summary>
    /// Whether this run has a game to save and load: the population (so not <c>--no-population</c> or the sandbox).
    /// </summary>
    internal bool SavesAvailable(out string reason)
    {
        reason = host.PopData is null || host.Population is null || host.Battle.IsFlat
            ? "No game to save or load here (needs the town population)" : "";
        return reason.Length == 0;
    }

    /// <summary>
    /// Whether a save's write is still running or the host is covered (<see cref="GameHost.Covered"/>).
    /// </summary>
    internal bool Busy() => SaveWrite is { IsCompleted: false } || host.Covered;

    #endregion

    #region Saving

    /// <summary>
    /// The save the game in play came from or was last written to: the load window cannot delete it.
    /// </summary>
    internal string? SaveInUse;
    /// <summary>
    /// The write worker of the last save: a save or a load waits while it runs (<see cref="Busy"/>).
    /// </summary>
    internal Task? SaveWrite;
    /// <summary>
    /// Results of the write worker, shown on the next frame (the HUD is drawn by the main thread only).
    /// </summary>
    readonly ConcurrentQueue<(string Text, bool Error)> _saveNotices = [];
    /// <summary>
    /// The names of the load order's data files a capture records, read from the install once.
    /// </summary>
    IReadOnlyList<string>? _saveDataFiles;
    /// <summary>
    /// The save a capture starts from (a copy of the loaded one, or a new one from the data) costs tens of milliseconds and holds nothing of the
    /// world: it is made ahead on a worker, so the capture at the tick boundary only reads the squad (a few ms). Stale (for another loaded save)
    /// after a load.
    /// </summary>
    internal Task<SaveBuilder>? PreparedBase;
    /// <summary>
    /// The loaded save <see cref="PreparedBase"/> was made for: a base made for another is stale.
    /// </summary>
    LoadedSave? _preparedFor;
    /// <summary>
    /// The captures made so far, mixed into each capture's seed.
    /// </summary>
    int _captures;

    /// <summary>
    /// The options of a capture: the load order's data files, the town placements and levels, a seed new for each capture, the strategy
    /// camera's view.
    /// </summary>
    SaveCaptureOptions CaptureOptions()
    {
        _saveDataFiles ??= [.. LoadOrder.FromInstall(host.Install).Entries.Select(e => e.Name)];
        // The strategy camera's view (in free flight: where it was left), as the original saves its camera rig.
        var view = host.Session.Camera.Strategy;
        return new SaveCaptureOptions
        {
            DataFiles = _saveDataFiles,
            Placements = host.TownPlacements,
            Levels = host.SaveLevels,
            Seed = host.Session.World.Seed ^ (ulong)++_captures << 20,
            View = new SavedView(view.Pivot, view.Yaw, view.Pitch, view.Distance),
            HandlesWritten = host.BloodDecals.NoteHandles,
        };
    }

    /// <summary>
    /// Saves the world; a <paramref name="feedback"/> save (into a temporary folder for the feedback report) leaves the save in use alone, shows no
    /// notice and fails its write task.
    /// </summary>
    internal void QuickSaveNow(string? destination = null, bool feedback = false)
    {
        if (!SavesAvailable(out string reason))
        {
            host.Player.Notice(reason, true);
            return;
        }
        host.Session.SettleAhead();   // a tick run ahead counts as run: the saved world and its clock agree
        var world = host.Session.World;
        if (!world.Player.Exists)
        {
            host.Player.Notice("Nothing to save: no player squad (start a new game)", true);
            return;
        }
        if (Busy())
        {
            host.Player.Notice("Save or load still in progress", true);
            return;
        }
        var watch = Stopwatch.StartNew();
        SaveGame game;
        bool ahead = PreparedBase is { IsCompletedSuccessfully: true } && ReferenceEquals(_preparedFor, LoadedSave);
        try
        {
            var clock = host.Session.Clock;
            var prepared = ahead ? PreparedBase!.Result : null;
            PreparedBase = null;   // used, or stale
            game = SaveCapture.Capture(world, host.PopData!, new SaveClock((int)clock.Day, clock.Hour, clock.Minute),
                LoadedSave, CaptureOptions(), prepared);
            Difficulty.Write(game.Camera);
            WriteWeather(game, (int)clock.Day, clock.HourOfDay);
            host.BloodDecals.WriteDecals(game);
            host.BloodDecals.WriteWeaponBlood(game);
            host.Player.BiomeBanner.WriteBiome(game.Camera);
            host.Modding.CaptureModData(world, game);
            host.LevelEditorHost.WriteLevelEdits(game);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"save      capture failed: {e}");
            host.Player.Notice(Loc.Tr("Save failed: {1}", e.Message), true);
            return;
        }
        double captureMs = watch.Elapsed.TotalMilliseconds;
        string folder = destination ?? QuickSave.Folder();
        if (!feedback) SaveInUse = folder;
        SaveWrite = Task.Run(() =>
        {
            try
            {
                var written = Stopwatch.StartNew();
                game.Write(folder);
                Console.WriteLine($"save      {folder}: captured in {captureMs:0.0} ms{(ahead ? "" : " (the base made on the spot)")}, "
                    + $"written in {written.ElapsedMilliseconds} ms");
                if (!feedback) _saveNotices.Enqueue(("Saved", false));
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"save      write failed: {e}");
                if (feedback) throw;
                _saveNotices.Enqueue((Loc.Tr("Save failed: {1}", e.Message), true));
            }
        });
    }

    /// <summary>
    /// The save's NPC platoons made since the last call (one line each), to the log.
    /// </summary>
    internal void PrintSavedLog()
    {
        if (host.Session.World.Saved is { } saved)
            foreach (var line in saved.TakeLog()) Console.WriteLine($"saved     {line}");
    }

    /// <summary>
    /// The regions' weather into the save's BIOMES record, and the CAMERA's sky transition as finished at the current sky (docs/formats/save.md
    /// "BIOMES", "CAMERA"): the loader snaps the sky to the camera region's weather, so a transition in progress is not carried.
    /// </summary>
    void WriteWeather(SaveGame game, int day, double hour)
    {
        if (host.ActiveWeather is not { } weather || game.Biomes is not { } biomes || weather.World.ForcedWeather is not null) return;
        SaveWeather.Write(biomes, weather.World.Snapshot(), new WeatherTime(day, hour), host.PopData?.Db);
        var camera = game.Camera;
        var state = weather.State;
        camera.SkyUpdating = false;
        camera.SkyUpdateTotalTime = WeatherWorld.SkyTransitionSeconds;
        camera.SkyUpdateCurrentTime = WeatherWorld.SkyTransitionSeconds;
        camera.SkyAmbientColorMult = state.SkyColourMultiplier;
        camera.SkyCloudsDensity = state.CloudDensity;
    }

    #endregion

    #region Loading

    /// <summary>
    /// What the world was loaded from: the next capture carries the save's records the world does not model through (null for a world that
    /// started new).
    /// </summary>
    internal LoadedSave? LoadedSave;
    /// <summary>
    /// Counts the worlds the host has had: part of a character's key in the draw list, so a new world's characters never take over the renderer's
    /// per-character state of the old one's.
    /// </summary>
    internal int WorldEpoch;

    /// <summary>
    /// Starts loading a save under the loading screen (GameHost.Transition.cs): the files are read on a worker, the new world is made, the navmesh
    /// round the player waits.
    /// </summary>
    void BeginQuickLoad(string? source = null)
    {
        if (!SavesAvailable(out string reason))
        {
            host.Player.Notice(reason, true);
            return;
        }
        if (Busy())
        {
            host.Player.Notice("Save or load still in progress", true);
            return;
        }
        string folder = source ?? QuickSave.Folder();
        if (!File.Exists(Path.Combine(folder, SaveFolder.QuickSaveName)))
        {
            host.Player.Notice(source is null ? "No quicksave yet (F5 saves)" : "Save is no longer available", true);
            return;
        }
        ReadingFolder = folder;
        Task<SaveGame>? read = null;
        string summary = "";
        host.Sound.AudioGameStarts();
        PreparedSystems? prepared = null;
        host.StartSceneLoad(host.Player.TitleOpen, null,
            GameHost.Stage("read", () => read = Task.Run(() => SaveGame.Load(SaveFolder.Open(folder), ReadArrivalHour))),
            GameHost.Stage("systems", () =>
            {
                prepared = PrepareSystems(read!, interactive: true);
                return null;
            }, duringAscent: true),
            GameHost.Stage("build", () => SwapInSave(read!, interactive: true, out summary, prepared: prepared)),
            GameHost.Stage("finishing", () =>
            {
                host.SettleNewWorld();
                host.Player.FollowSelected();
                FinishLoadNotice(summary);
                PauseAfterLoad();
                SaveInUse = folder;
                return null;
            }));
    }

    /// <summary>
    /// Imports a save into a new world (docs/formats/save.md "Importing a game"): the save is read on a worker and cut down to what the ticks keep
    /// (<see cref="SaveImport"/>), then loaded as any save is. The difficulty is what the advanced options hold, not the save's.
    /// </summary>
    void BeginImport(string folder, ImportOptions options)
    {
        if (!SavesAvailable(out string reason))
        {
            host.Player.Notice(reason, true);
            return;
        }
        if (Busy())
        {
            host.Player.Notice("Save or load still in progress", true);
            return;
        }
        if (!File.Exists(Path.Combine(folder, SaveFolder.QuickSaveName)))
        {
            host.Player.Notice("Failed to import game: the save is no longer available", true);
            return;
        }
        ReadingFolder = folder;
        var settings = host.Player.AdvancedOptions.NewGameOptions;
        var data = host.PopData!;
        Task<SaveGame>? read = null;
        PreparedSystems? prepared = null;
        string summary = "";
        host.Sound.AudioGameStarts();
        host.StartSceneLoad(host.Player.TitleOpen, null,
            GameHost.Stage("read", () => read = Task.Run(() =>
            {
                var save = SaveGame.Load(SaveFolder.Open(folder), ReadArrivalHour);
                // A save older than 0.90.0 imports without the reset, buildings, research and dead-NPC ticks (the window's rule).
                options = SaveImport.AllowedFor(options, save.Camera.Version);
                var game = SaveImport.Filter(save, options, data);
                settings.Write(game.Camera);
                return game;
            })),
            GameHost.Stage("systems", () =>
            {
                prepared = PrepareSystems(read!, interactive: true);
                return null;
            }, duringAscent: true),
            // The squads moved to the reset place stand on its ground, whose navmesh is waited for.
            GameHost.Stage("navmesh",
                () => (options & ImportOptions.ResetPositions) != 0 && read!.IsCompletedSuccessfully
                ? NavmeshAround(SaveImport.ResetPosition) : null),
            GameHost.Stage("build", () =>
            {
                if ((options & ImportOptions.ResetPositions) != 0 && host.Nav is not null && read!.IsCompletedSuccessfully)
                    SaveImport.PlaceOnGround(read.Result, host.Nav.Walkability.GroundHeight);
                return SwapInSave(read!, interactive: true, out summary,
                    new SaveLoadOptions { Appearances = data.Appearances, ResetRelations = (options & ImportOptions.Relations) == 0 }, prepared);
            }),
            GameHost.Stage("finishing", () =>
            {
                host.SettleNewWorld();
                host.Player.FollowSelected();
                FinishLoadNotice(summary, imported: true);
                PauseAfterLoad();
                return null;
            }));
    }

    /// <summary>
    /// The systems made ahead for the world a save loads into, with its population data and difficulty (<see cref="PrepareSystems"/>).
    /// </summary>
    internal sealed record PreparedSystems(StandardSystemSet Built, PopulationData Data, NewGameSettings Difficulty);

    /// <summary>
    /// The worker's hand-off of the save's hour to the sky arrival (GameHost.Transition.cs).
    /// </summary>
    void ReadArrivalHour(SaveCamera saved) => Volatile.Write(ref host.ReadHour, saved.Hour + saved.Minute / 60f);

    /// <summary>
    /// The systems of the world the save will be loaded into, with its difficulty: made apart from the swap, so a load builds them while the old
    /// world still shows. Throws <see cref="GameHost.SceneLoadFailed"/> when the save was not read.
    /// </summary>
    PreparedSystems PrepareSystems(Task<SaveGame> read, bool interactive)
    {
        if (read.IsFaulted || read.IsCanceled)
        {
            Console.Error.WriteLine($"load      read failed: {read.Exception?.GetBaseException()}");
            throw new GameHost.SceneLoadFailed($"Load failed: {read.Exception?.GetBaseException().Message ?? "cancelled"}");
        }
        var nextDifficulty = NewGameSettings.Read(read.Result.Camera);
        var nextData = nextDifficulty.Apply(host.PopData!, Gameplay.Dismemberment);
        try
        {
            return new(BuildSystems(interactive, nextData, nextDifficulty), nextData, nextDifficulty);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"load      failed: {e}");
            throw new GameHost.SceneLoadFailed($"Load failed: {e.Message}");
        }
    }

    /// <summary>
    /// Swaps in a world made from the save (at a tick boundary, the frame loop's) and hooks the host to it: the game clock, the camera on the
    /// player's squad. Returns the navmesh of the zones round the player to wait for before the first tick (<see cref="GameHost.SettleNewWorld"/>);
    /// throws <see cref="GameHost.SceneLoadFailed"/> when the save does not load (the old world stays).
    /// </summary>
    internal Task SwapInSave(Task<SaveGame> read, bool interactive, out string summary, SaveLoadOptions? loadOptions = null,
        PreparedSystems? prepared = null)
    {
        var watch = Stopwatch.StartNew();
        LoadedSave? loaded = null;
        var (built, nextData, nextDifficulty) = prepared ?? PrepareSystems(read, interactive);
        try
        {
            var data = nextData;
            // A world that fails to load leaves the old one running (ReplaceWorld drops the half-made one).
            host.Session.ReplaceWorld(built.Systems, world =>
            {
                loaded = SaveLoader.Load(world, read.Result, data, loadOptions ?? new SaveLoadOptions { Appearances = data.Appearances });
                host.Modding.RestoreModData(world, read.Result);
                if (world.Squads.Find(world.Player.Squad) is { Members.Count: > 0 } squad)
                {
                    world.Player.Selection.Clear();
                    world.Player.Selection.Add(squad.Members[0]);
                }
            });
        }
        catch (Exception e) when (e is not GameHost.SceneLoadFailed)
        {
            Console.Error.WriteLine($"load      failed: {e}");
            throw new GameHost.SceneLoadFailed($"Load failed: {e.Message}");
        }
        host.PopData = nextData;
        Difficulty = nextDifficulty;
        // The New game window then shows the loaded save's options (the original loads them into its globals).
        host.Player.AdvancedOptions.NewGameOptions = nextDifficulty;
        host.Population = built.Population;
        if (host.Nav is not null) host.Population!.ZoneGate = zone => host.Nav.Walkability.World.Contains(zone);
        SnapshotPlayerTowns();
        LoadedSave = loaded;
        PreparedBase = null;
        WorldEpoch++;
        host.Modding.NoticeModChanges(read.Result);
        host.LevelEditorHost.PendingLevelEdits = read.Result.LevelEdits;   // replayed once the host has met the new world (UpdateBuildings)
        var world = host.Session.World;
        host.BloodDecals.BloodForNewWorld(read.Result.Decals);
        host.BloodDecals.ReadWeaponBlood(read.Result);
        host.Session.Clock.SetTime(loaded!.Clock.Day, loaded.Clock.HourOfDay);
        // The regions' weather the save holds (docs/formats/save.md "BIOMES"); a save without (an old Meitou one) keeps the schedule rolled from day
        // 0.
        if (host.ActiveWeather is { } weather && read.Result.Biomes is { } biomes && SaveWeather.Read(biomes) is { Count: > 0 } regions)
        {
            weather.Restore(regions, loaded.Clock.Day, loaded.Clock.HourOfDay);
            Console.WriteLine($"load      weather of {regions.Count} region(s) from the save");
        }
        host.PlayerSquad = world.Squads.Find(world.Player.Squad);
        foreach (var note in loaded.Notes) Console.WriteLine($"load      {note}");

        // The saved camera (FUN_1407ade60, docs/formats/camera.md "Saved camera"): pivot, orientation and boom, a pivot out of bounds moved to the
        // loader's fallback. A save without a view (an older Meitou one) puts the camera on the leader, keeping the view's heading, pitch and boom.
        // The zones round the leader get their navmesh before the first tick (like a new game's start).
        Task navmesh = Task.CompletedTask;
        var saved = read.Result.Camera;
        host.Player.BiomeBanner.ReadBiome(saved);
        if (saved.HasView)
        {
            var (yaw, pitch, distance) = KenshiCamera.FromSaved(saved.Rotation, saved.Zoom);
            var pivot = KenshiCamera.SavedPivotInBounds(saved.Position) ? new Vector2(saved.Position.X, saved.Position.Z)
                : KenshiCamera.SavedPivotFallback;
            host.Session.Camera.Place(pivot, yaw, pitch, distance);
        }
        if (host.PlayerSquad is { } squad && !squad.Leader.IsNone)
        {
            var at = world.Characters.Previous[squad.Leader.Slot].Position;
            if (!saved.HasView)
            {
                var view = host.Session.Camera.Current;
                host.Session.Camera.Place(new Vector2(at.X, at.Z), view.Yaw, view.Pitch, view.Distance);
            }
            navmesh = NavmeshAround(at);
        }
        summary = $"{ReadingFolder}: {loaded.PlayerPlatoons.Sum(p => p.Squad.Members.Count)} player character(s), "
            + $"{loaded.RoamingPlatoons.Count} roaming, "
            + $"{loaded.TownBoundPlatoons} town and {loaded.WildPlatoons} wild platoon(s) kept as saved, "
            + $"day {loaded.Clock.Day} {loaded.Clock.Hour:00}:{loaded.Clock.Minute:00}, made in {watch.ElapsedMilliseconds} ms";
        return navmesh;
    }

    /// <summary>
    /// Logs a finished load and shows its notice; a save made with mods not loaded now says so, the message log naming them.
    /// </summary>
    internal void FinishLoadNotice(string summary, bool imported = false)
    {
        Console.WriteLine($"load      {summary}");
        var missing = LoadedSave?.MissingMods ?? [];
        // The message panel fits a line or two: it gives the count, the message log the names.
        if (missing.Count == 0)
        {
            host.Player.Notice(imported ? "Imported" : Loc.TrIn("save notice", "Loaded"));
            return;
        }
        string panel = Loc.Tr("Loaded; the save was made with {1} mod(s) not loaded now (listed in the messages)", missing.Count);
        host.Player.Notice(panel, true, Loc.Tr("Loaded; the save was made with mods not loaded now: {1}", string.Join(", ", missing)));
    }

    /// <summary>
    /// A save loaded from the menus starts paused with <see cref="UserConfig.PauseOnLoad"/> (a player pause: the speed buttons resume it).
    /// </summary>
    void PauseAfterLoad()
    {
        if (host.Config.PauseOnLoad) host.Session.Simulation.Pause();
    }

    /// <summary>
    /// The navmesh of the zones round <paramref name="at"/> (the ones already loaded cost nothing): done when they are, or after 15 s (loading goes
    /// on without them).
    /// </summary>
    internal Task NavmeshAround(Vector3 at)
    {
        if (host.Nav is null) return Task.CompletedTask;
        host.FinishNavUnload();
        long now = Stopwatch.GetTimestamp();
        var pending = new List<Task>();
        foreach (var z in ZoneActivation.ZonesAround(at, 0))
        {
            if (!host.NavZones.ContainsKey(z)) pending.Add(host.Nav.LoadZone(z));
            host.NavZones[z] = now;
        }
        if (pending.Count == 0) return Task.CompletedTask;
        var all = Task.WhenAll(pending);
        return Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(15))).ContinueWith(t =>
        {
            if (t.Result != all) Console.Error.WriteLine("load      the navmesh round the player was not ready after 15 s; loading goes on");
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// The towns the player's faction owns now, for the foliage placer (<see cref="Meitou.Data.World.FoliageWorld.PlayerTowns"/>, a snapshot taken
    /// when a world is made or loaded).
    /// </summary>
    internal void SnapshotPlayerTowns()
    {
        var owned = new HashSet<(int, int)>();
        if (host.PopData is { PlayerFaction: >= 0 } d)
            foreach (var site in d.Sites)
                if (site.Index < d.SiteFactions.Count && d.SiteFactions[site.Index] == d.PlayerFaction)
                    owned.Add(FoliageWorld.TownKey(site.Position.X, site.Position.Z));
        FoliageWorld.PlayerTowns = owned;
    }

    #endregion

    #region Settings and systems

    /// <summary>
    /// The tutorial manager (docs/game/ui-screens.md "Tutorials"), made with the interface; its file is in the working directory, as the game's.
    /// </summary>
    internal TutorialManager? Tutorials;
    /// <summary>
    /// The difficulty (the New game options) of the world in play: written into each save, replaced by a loaded save's.
    /// </summary>
    internal NewGameSettings Difficulty = new();
    /// <summary>
    /// The footprints a worker's reach of a building is measured to (docs/game/buildings-production.md "Work distance").
    /// </summary>
    internal Simulation.Navigation.BuildingFootprints? SiteFootprints;

    /// <summary>
    /// The X/Z gap from a point to a site's footprint hull (its collision, else for a building without it the hull of its parts' bounds); null while
    /// the record is being read, or without a database.
    /// </summary>
    float? SiteEdgeGap(Simulation.Construction.BuildSite site, Vector2 point)
    {
        if (host.Scene.Database is not { } db) return null;
        var footprints = SiteFootprints ??= new Simulation.Navigation.BuildingFootprints(host.Install, db, null);
        if (!footprints.Ready(site.Record)) return null;
        return (footprints.Of(site.Record) ?? footprints.Plot(site.Record))?.Gap(site.Position, site.Rotation, point);
    }

    /// <summary>
    /// The Gameplay settings, first taken from the install's settings.cfg.
    /// </summary>
    internal GameplaySettings Gameplay => host.Config.Gameplay ??= GameplaySettings.FromInstall(host.Install);

    /// <summary>
    /// Hands the Gameplay settings to the running world's systems. Dismemberment goes into the world's body options, which every system holds and
    /// reads when a part is damaged, as the original reads its global.
    /// </summary>
    internal void ApplyGameplay()
    {
        var gameplay = Gameplay;
        if (host.PopData is { } data)
        {
            (data.BodyOptions.Dismemberment, data.BodyOptions.Decapitation, data.BodyOptions.AdvancedRagdolls) =
                (gameplay.Dismemberment, host.Config.Decapitation, host.Config.AdvancedRagdolls);
        }

        foreach (var population in host.Session.World.Systems.OfType<PopulationSystem>())
        {
            (population.Settings.SquadSizeMultiplier, population.Settings.PopulationMultiplier, population.Settings.RaidSizeMult,
                population.Settings.RaidFrequencyMult) = (gameplay.SquadSize, gameplay.Population, gameplay.RaidSize, gameplay.RaidFrequency);
        }
        foreach (var population in host.Session.World.Systems.OfType<PopulationSystem>())
            population.Settings.FastZoneHopping = gameplay.FastZoneHopping;
        foreach (var ai in host.Session.World.Systems.OfType<Simulation.Ai.AiSystem>()) ai.Settings.TownAttacks = gameplay.TownAttacks;
        foreach (var combat in host.Session.World.Systems.OfType<Simulation.Combat.CombatSystem>()) combat.StuckLimit = gameplay.HarpoonLimit;
    }

    /// <summary>
    /// The system list and its order are <see cref="StandardSystems"/>'(docs/simulation.md "Threading"); built again for each world.
    /// </summary>
    internal StandardSystemSet BuildSystems(bool interactive, PopulationData? data = null, NewGameSettings? settings = null) =>
        StandardSystems.Build(data ?? host.PopData!, host.SimWalkability!, new StandardSystemOptions
        {
            // The sandbox compares motion with the original on characters standing still: a hostile foe there must not start a fight by itself. The
            // duel and the battle script their fights. --battle-no-fear: no flee system, so nobody runs away.
            Parts = (host.Sandbox.IsSandbox || host.Duel.IsDuel || host.Battle.IsBattle ? StandardParts.All & ~StandardParts.Aggro : StandardParts.All)
                & ~(host.Battle.IsBattle && host.GameOptions.BattleNoFear ? StandardParts.Flee : StandardParts.None),
            // NPCs that see a player character say a line as a speech bubble (docs/game/dialogue.md "Ambient conversations"); the sandbox, the duel
            // and the battle stay silent.
            Dialogue = new Simulation.Dialogue.DialogueSettings { AmbientEvents = !host.Sandbox.IsSandbox && !host.Duel.IsDuel
                && !host.Battle.IsBattle, Censored = () => host.Config.Censorship },
            Population = new PopulationSettings
            {
                Background = interactive && !host.Duel.IsDuel && !host.Battle.IsBattle,
                KeepRoamers = host.Config.KeepRoamers,
                OffscreenFights = host.Config.OffscreenFights,
                OffscreenHealing = host.Config.OffscreenHealing,
                CampaignAncestorsOnce = host.Config.CampaignAncestorsOnce,
                KeepWild = host.Config.KeepWild,
                ResidentSchedule = host.Config.ResidentSchedule,
                NestsMultiplier = (settings ?? Difficulty)[6],
                SquadSizeMultiplier = Gameplay.SquadSize,
                PopulationMultiplier = Gameplay.Population,
                RaidSizeMult = Gameplay.RaidSize,
                RaidFrequencyMult = Gameplay.RaidFrequency,
                FastZoneHopping = Gameplay.FastZoneHopping,
            },
            Ai = new Simulation.Ai.AiSettings { TownAttacks = Gameplay.TownAttacks },
            Construction = new Simulation.Construction.ConstructionSettings { BuildingSpeed = (settings ?? Difficulty)[5], EdgeGap = SiteEdgeGap },
            Production = new Simulation.Production.ProductionSettings { ProductionOption = (settings ?? Difficulty)[3],
                ResearchOption = (settings ?? Difficulty)[4], EdgeGap = SiteEdgeGap, UnloadedProduction = host.Config.UnloadedProduction },
            // The duel replays exactly: its paths are answered inside the tick, never on a thread whose timing differs from lap to lap.
            SynchronousPaths = !interactive || host.Duel.IsDuel,
            // The duel prints what its measured lap holds (MeasureLap).
            Combat = new Simulation.Combat.CombatOptions { SelfApproach = false, TickMedical = false, RecordLog = host.Duel.IsDuel,
                AttackSlots = host.Config.AttackSlotsOption, StuckLimit = Gameplay.HarpoonLimit, LateDefences = host.Config.LateDefences,
                GunnerFacesBarrel = host.Config.FixGunnerFacing, PunchReachFix = host.Config.FixPunchReach },
            DoorsCloseBehind = host.Config.DoorsCloseBehind,
            AnimalsPickUp = host.Config.AnimalsPickUp,
            AnimationLengths = host.AnimationLengths,
            AnimationLibrary = host.AnimationLibrary,
            AnimationBlendRate = host.PopData!.Bodies.Constants.AnimationBlendRate,
            Ragdolls = host.CharacterRigs,
            RagdollEnvironment = host.RagdollGeometry,
            Extensions = host.Modding.Mods?.Simulation,
        });

    #endregion
}