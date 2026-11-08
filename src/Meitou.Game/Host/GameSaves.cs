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
/// Quicksave (F5) and quickload (F9), docs/simulation.md "Saves". The key only sets a request (a control tick runs inside <c>WorldSession.Advance</c>); the frame loop takes
/// the request right after <c>Advance</c> returns, when no world tick is in flight. A save captures the world there, on the thread that ticks it (a few milliseconds), and writes
/// the files on a worker; a load reads the files on a worker and swaps in a new world there. The slot is Meitou's own <c>%LOCALAPPDATA%\Meitou\save\quicksave</c>
/// (<see cref="QuickSave"/>), never a folder of the original game.
/// </summary>
sealed class GameSaves(GameHost host)
{
    internal bool quickSaveRequested, quickLoadRequested;
    // The autosave (docs/formats/save.md "Autosave"): real seconds run unpaused since the last one, and a save announced this frame and written the next.
    double autosaveSeconds;
    bool autosaveDue;
    internal string? saveRequestedFolder, loadRequestedFolder, readingFolder;
    // The tutorial manager (docs/game/ui-screens.md "Tutorials"), made with the interface; its file is in the working directory, as the game's.
    internal TutorialManager? tutorials;
    // The save the game in play came from or was last written to: the load window cannot delete it.
    internal string? saveInUse;
    internal (string Folder, ImportOptions Options)? importRequested;
    internal Task? saveWrite;

    // What the world was loaded from: the next capture carries the save's records the world does not model through (null for a world that started new).
    internal LoadedSave? loadedSave;
    // Counts the worlds the host has had: part of a character's key in the draw list, so a new world's characters never take over the renderer's per-character state of the old one's.
    internal int worldEpoch;
    // Results of the write worker, shown on the next frame (the HUD is drawn by the main thread only).
    readonly ConcurrentQueue<(string Text, bool Error)> saveNotices = [];
    IReadOnlyList<string>? saveDataFiles;
    // The save a capture starts from (a copy of the loaded one, or a new one from the data) costs tens of milliseconds and holds nothing of the world: it is made ahead on a worker,
    // so the capture at the tick boundary only reads the squad (a few ms). Stale (for another loaded save) after a load.
    internal Task<SaveBuilder>? preparedBase;
    LoadedSave? preparedFor;
    int captures;
    internal NewGameSettings difficulty = new();

    SaveCaptureOptions CaptureOptions()
    {
        saveDataFiles ??= [.. LoadOrder.FromInstall(host.install).Entries.Select(e => e.Name)];
        // The strategy camera's view (in free flight: where it was left), as the original saves its camera rig.
        var view = host.session.Camera.Strategy;
        return new SaveCaptureOptions
        {
            DataFiles = saveDataFiles, Placements = host.townPlacements, Levels = host.saveLevels, Seed = host.session.World.Seed ^ (ulong)++captures << 20,
            View = new SavedView(view.Pivot, view.Yaw, view.Pitch, view.Distance),
        };
    }

    // The footprints a worker's reach of a building is measured to (docs/game/buildings-production.md "Work distance").
    internal Meitou.Simulation.Navigation.BuildingFootprints? siteFootprints;

    /// <summary>
    /// The X/Z gap from a point to a site's footprint hull (its collision, else for a building without it the hull of its parts' bounds); null while the record is being read, or
    /// without a database.
    /// </summary>
    float? SiteEdgeGap(Meitou.Simulation.Construction.BuildSite site, Vector2 point)
    {
        if (host.scene.Database is not { } db) return null;
        var footprints = siteFootprints ??= new Meitou.Simulation.Navigation.BuildingFootprints(host.install, db, null);
        if (!footprints.Ready(site.Record)) return null;
        return (footprints.Of(site.Record) ?? footprints.Plot(site.Record))?.Gap(site.Position, site.Rotation, point);
    }

    /// <summary>The Gameplay settings, first taken from the install's settings.cfg.</summary>
    internal GameplaySettings Gameplay => host.config.Gameplay ??= GameplaySettings.FromInstall(host.install);

    /// <summary>
    /// Hands the Gameplay settings to the running world's systems. Dismemberment goes into the world's body options, which every system holds and reads when a part is
    /// damaged, as the original reads its global.
    /// </summary>
    internal void ApplyGameplay()
    {
        var gameplay = Gameplay;
        if (host.popData is { } data) (data.BodyOptions.Dismemberment, data.BodyOptions.Decapitation, data.BodyOptions.AdvancedRagdolls) = (gameplay.Dismemberment, host.config.Decapitation, host.config.AdvancedRagdolls);
        foreach (var population in host.session.World.Systems.OfType<Meitou.Simulation.Population.PopulationSystem>())
            (population.Settings.SquadSizeMultiplier, population.Settings.PopulationMultiplier, population.Settings.RaidSizeMult, population.Settings.RaidFrequencyMult) =
                (gameplay.SquadSize, gameplay.Population, gameplay.RaidSize, gameplay.RaidFrequency);
        foreach (var population in host.session.World.Systems.OfType<Meitou.Simulation.Population.PopulationSystem>()) population.Settings.FastZoneHopping = gameplay.FastZoneHopping;
        foreach (var ai in host.session.World.Systems.OfType<Meitou.Simulation.Ai.AiSystem>()) ai.Settings.TownAttacks = gameplay.TownAttacks;
        foreach (var combat in host.session.World.Systems.OfType<Meitou.Simulation.Combat.CombatSystem>()) combat.StuckLimit = gameplay.HarpoonLimit;
    }

    /// <summary>The system list and its order are <see cref="StandardSystems"/>'(docs/simulation.md "Threading"); built again for each world.</summary>
    internal Meitou.Simulation.StandardSystemSet BuildSystems(bool interactive, PopulationData? data = null, NewGameSettings? settings = null) =>
        Meitou.Simulation.StandardSystems.Build(data ?? host.popData!, host.simWalkability!, new Meitou.Simulation.StandardSystemOptions
        {
            // The sandbox compares motion with the original on characters standing still: a hostile foe there must not start a fight by itself. The duel and the battle script their fights.
            // --battle-no-fear: no flee system, so nobody runs away.
            Parts = (host.sandbox.IsSandbox || host.duel.IsDuel || host.battle.IsBattle ? Meitou.Simulation.StandardParts.All & ~Meitou.Simulation.StandardParts.Aggro : Meitou.Simulation.StandardParts.All)
                & ~(host.battle.IsBattle && host.g.BattleNoFear ? Meitou.Simulation.StandardParts.Flee : Meitou.Simulation.StandardParts.None),
            // NPCs that see a player character say a line as a speech bubble (docs/game/dialogue.md "Ambient conversations"); the sandbox, the duel and the battle stay silent.
            Dialogue = new Meitou.Simulation.Dialogue.DialogueSettings { AmbientEvents = !host.sandbox.IsSandbox && !host.duel.IsDuel && !host.battle.IsBattle, Censored = () => host.config.Censorship },
            Population = new Meitou.Simulation.Population.PopulationSettings
            {
                Background = interactive && !host.duel.IsDuel && !host.battle.IsBattle, KeepRoamers = host.config.KeepRoamers, OffscreenFights = host.config.OffscreenFights, OffscreenHealing = host.config.OffscreenHealing,
                CampaignAncestorsOnce = host.config.CampaignAncestorsOnce, KeepWild = host.config.KeepWild, ResidentSchedule = host.config.ResidentSchedule,
                NestsMultiplier = (settings ?? difficulty)[6],
                SquadSizeMultiplier = Gameplay.SquadSize, PopulationMultiplier = Gameplay.Population, RaidSizeMult = Gameplay.RaidSize, RaidFrequencyMult = Gameplay.RaidFrequency, FastZoneHopping = Gameplay.FastZoneHopping,
            },
            Ai = new Meitou.Simulation.Ai.AiSettings { TownAttacks = Gameplay.TownAttacks },
            Construction = new Meitou.Simulation.Construction.ConstructionSettings { BuildingSpeed = (settings ?? difficulty)[5], EdgeGap = SiteEdgeGap },
            Production = new Meitou.Simulation.Production.ProductionSettings { ProductionOption = (settings ?? difficulty)[3], ResearchOption = (settings ?? difficulty)[4], EdgeGap = SiteEdgeGap, UnloadedProduction = host.config.UnloadedProduction },
            // The duel replays exactly: its paths are answered inside the tick, never on a thread whose timing differs from lap to lap.
            SynchronousPaths = !interactive || host.duel.IsDuel,
            // The duel prints what its measured lap holds (MeasureLap).
            Combat = new Meitou.Simulation.Combat.CombatOptions { SelfApproach = false, TickMedical = false, RecordLog = host.duel.IsDuel, AttackSlots = host.config.AttackSlotsOption, StuckLimit = Gameplay.HarpoonLimit, LateDefences = host.config.LateDefences, GunnerFacesBarrel = host.config.FixGunnerFacing, PunchReachFix = host.config.FixPunchReach },
            DoorsCloseBehind = host.config.DoorsCloseBehind,
            AnimalsPickUp = host.config.AnimalsPickUp,
            AnimationLengths = host.animationLengths,
            AnimationLibrary = host.animationLibrary,
            AnimationBlendRate = host.popData!.Bodies.Constants.AnimationBlendRate,
            Ragdolls = host.characterRigs,
            RagdollEnvironment = host.ragdollGeometry,
            Extensions = host.modding.mods?.Simulation,
        });

    /// <summary>Once per frame, between ticks: the notices of the write worker, the requests of the keys, the autosave, a finished read. <paramref name="realDt"/>: the frame's real seconds.</summary>
    internal void HandleSaves(double realDt)
    {
        while (saveNotices.TryDequeue(out var notice)) host.player.Notice(notice.Text, notice.Error);
        host.feedbackCapture.HandleFeedback();
        PrintSavedLog();
        Autosave(realDt);
        if (quickSaveRequested) { quickSaveRequested = false; QuickSaveNow(); }
        if (quickLoadRequested) { quickLoadRequested = false; BeginQuickLoad(); }
        if (saveRequestedFolder is { } to) { saveRequestedFolder = null; QuickSaveNow(to); }
        if (loadRequestedFolder is { } sourceFolder) { loadRequestedFolder = null; BeginQuickLoad(sourceFolder); }
        if (importRequested is { } import) { importRequested = null; BeginImport(import.Folder, import.Options); }

        if (preparedBase is null && SavesAvailable(out _) && host.session.World.Player.Exists)
        {
            var (data, from, options) = (host.popData!, loadedSave, CaptureOptions());
            (preparedBase, preparedFor) = (Task.Run(() => SaveCapture.PrepareBase(data, from, options)), from);
        }
    }

    /// <summary>
    /// The original's autosave (<c>FUN_14047be70</c>, docs/formats/save.md "Autosave"): while <c>Auto save</c> is on and nothing loads, real time run unpaused adds up; past
    /// <c>Auto save time (minutes)</c> it shows "Autosaving..." and the next frame writes the slot after the last one, <c>autosave0</c> to <c>autosave2</c> in turn.
    /// </summary>
    void Autosave(double realDt)
    {
        if (autosaveDue)
        {
            autosaveDue = false;
            host.config.AutosaveIndex = (host.config.AutosaveIndex + 1) % QuickSave.AutosaveSlots;
            host.config.Save();
            QuickSaveNow(QuickSave.AutosaveFolder(host.config.AutosaveIndex));
            return;
        }
        if (!Gameplay.AutoSave || host.Covered || host.session.Simulation.IsPaused || !host.session.World.Player.Exists || !SavesAvailable(out _)) return;
        autosaveSeconds += realDt;
        if (autosaveSeconds < Gameplay.AutoSaveMinutes * 60 || Busy()) return;
        autosaveSeconds = 0;
        autosaveDue = true;
        host.player.Notice("Autosaving...");
    }

    /// <summary>Whether this run has a game to save and load: the population (so not <c>--no-population</c> or the sandbox).</summary>
    internal bool SavesAvailable(out string reason)
    {
        reason = host.popData is null || host.population is null || host.battle.IsFlat ? "No game to save or load here (needs the town population)" : "";
        return reason.Length == 0;
    }

    internal bool Busy() => saveWrite is { IsCompleted: false } || host.Covered;

    /// <summary>Saves the world; a <paramref name="feedback"/> save (into a temporary folder for the feedback report) leaves the save in use alone, shows no notice and fails its write task.</summary>
    internal void QuickSaveNow(string? destination = null, bool feedback = false)
    {
        if (!SavesAvailable(out string reason)) { host.player.Notice(reason, true); return; }
        host.session.SettleAhead();   // a tick run ahead counts as run: the saved world and its clock agree
        var world = host.session.World;
        if (!world.Player.Exists) { host.player.Notice("Nothing to save: no player squad (start a new game)", true); return; }
        if (Busy()) { host.player.Notice("Save or load still in progress", true); return; }
        var watch = Stopwatch.StartNew();
        SaveGame game;
        bool ahead = preparedBase is { IsCompletedSuccessfully: true } && ReferenceEquals(preparedFor, loadedSave);
        try
        {
            var clock = host.session.Clock;
            var prepared = ahead ? preparedBase!.Result : null;
            preparedBase = null;   // used, or stale
            game = SaveCapture.Capture(world, host.popData!, new SaveClock((int)clock.Day, clock.Hour, clock.Minute), loadedSave, CaptureOptions(), prepared);
            difficulty.Write(game.Camera);
            WriteWeather(game, (int)clock.Day, clock.HourOfDay);
            host.bloodDecals.WriteDecals(game);
            host.player.WriteBiome(game.Camera);
            host.modding.CaptureModData(world, game);
            host.levelEditorHost.WriteLevelEdits(game);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"save      capture failed: {e}");
            host.player.Notice(Loc.Tr("Save failed: {1}", e.Message), true);
            return;
        }
        double captureMs = watch.Elapsed.TotalMilliseconds;
        string folder = destination ?? QuickSave.Folder();
        if (!feedback) saveInUse = folder;
        saveWrite = Task.Run(() =>
        {
            try
            {
                var written = Stopwatch.StartNew();
                game.Write(folder);
                Console.WriteLine($"save      {folder}: captured in {captureMs:0.0} ms{(ahead ? "" : " (the base made on the spot)")}, written in {written.ElapsedMilliseconds} ms");
                if (!feedback) saveNotices.Enqueue(("Saved", false));
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"save      write failed: {e}");
                if (feedback) throw;
                saveNotices.Enqueue((Loc.Tr("Save failed: {1}", e.Message), true));
            }
        });
    }

    /// <summary>The save's NPC platoons made since the last call (one line each), to the log.</summary>
    internal void PrintSavedLog()
    {
        if (host.session.World.Saved is { } saved)
            foreach (var line in saved.TakeLog()) Console.WriteLine($"saved     {line}");
    }

    /// <summary>
    /// The regions' weather into the save's BIOMES record, and the CAMERA's sky transition as finished at the current sky (docs/formats/save.md "BIOMES", "CAMERA"):
    /// the loader snaps the sky to the camera region's weather, so a transition in progress is not carried.
    /// </summary>
    void WriteWeather(SaveGame game, int day, double hour)
    {
        if (host.ActiveWeather is not { } weather || game.Biomes is not { } biomes || weather.World.ForcedWeather is not null) return;
        SaveWeather.Write(biomes, weather.World.Snapshot(), new Meitou.Data.World.WeatherTime(day, hour), host.popData?.Db);
        var camera = game.Camera;
        var state = weather.State;
        camera.SkyUpdating = false;
        camera.SkyUpdateTotalTime = Meitou.Data.World.WeatherWorld.SkyTransitionSeconds;
        camera.SkyUpdateCurrentTime = Meitou.Data.World.WeatherWorld.SkyTransitionSeconds;
        camera.SkyAmbientColorMult = state.SkyColourMultiplier;
        camera.SkyCloudsDensity = state.CloudDensity;
    }

    /// <summary>Starts loading a save under the loading screen (GameHost.Transition.cs): the files are read on a worker, the new world is made, the navmesh round the player waits.</summary>
    void BeginQuickLoad(string? source = null)
    {
        if (!SavesAvailable(out string reason)) { host.player.Notice(reason, true); return; }
        if (Busy()) { host.player.Notice("Save or load still in progress", true); return; }
        string folder = source ?? QuickSave.Folder();
        if (!File.Exists(Path.Combine(folder, SaveFolder.QuickSaveName))) { host.player.Notice(source is null ? "No quicksave yet (F5 saves)" : "Save is no longer available", true); return; }
        readingFolder = folder;
        Task<SaveGame>? read = null;
        string summary = "";
        host.sound.AudioGameStarts();
        PreparedSystems? prepared = null;
        host.StartSceneLoad(host.player.TitleOpen, null,
            GameHost.Stage("read", () => read = Task.Run(() => SaveGame.Load(SaveFolder.Open(folder), ReadArrivalHour))),
            GameHost.Stage("systems", () => { prepared = PrepareSystems(read!, interactive: true); return null; }, duringAscent: true),
            GameHost.Stage("build", () => SwapInSave(read!, interactive: true, out summary, prepared: prepared)),
            GameHost.Stage("finishing", () => { host.SettleNewWorld(); host.player.FollowSelected(); FinishLoadNotice(summary); PauseAfterLoad(); saveInUse = folder; return null; }));
    }

    /// <summary>
    /// Imports a save into a new world (docs/formats/save.md "Importing a game"): the save is read on a worker and cut down to what the ticks keep (<see cref="SaveImport"/>), then
    /// loaded as any save is. The difficulty is what the advanced options hold, not the save's.
    /// </summary>
    void BeginImport(string folder, ImportOptions options)
    {
        if (!SavesAvailable(out string reason)) { host.player.Notice(reason, true); return; }
        if (Busy()) { host.player.Notice("Save or load still in progress", true); return; }
        if (!File.Exists(Path.Combine(folder, SaveFolder.QuickSaveName))) { host.player.Notice("Failed to import game: the save is no longer available", true); return; }
        readingFolder = folder;
        var settings = host.player.NewGameOptions;
        var data = host.popData!;
        Task<SaveGame>? read = null;
        PreparedSystems? prepared = null;
        string summary = "";
        host.sound.AudioGameStarts();
        host.StartSceneLoad(host.player.TitleOpen, null,
            GameHost.Stage("read", () => read = Task.Run(() =>
            {
                var save = SaveGame.Load(SaveFolder.Open(folder), ReadArrivalHour);
                // A save older than 0.90.0 imports without the reset, buildings, research and dead-NPC ticks (the window's rule).
                options = SaveImport.AllowedFor(options, save.Camera.Version);
                var game = SaveImport.Filter(save, options, data);
                settings.Write(game.Camera);
                return game;
            })),
            GameHost.Stage("systems", () => { prepared = PrepareSystems(read!, interactive: true); return null; }, duringAscent: true),
            // The squads moved to the reset place stand on its ground, whose navmesh is waited for.
            GameHost.Stage("navmesh", () => (options & ImportOptions.ResetPositions) != 0 && read!.IsCompletedSuccessfully ? NavmeshAround(SaveImport.ResetPosition) : null),
            GameHost.Stage("build", () =>
            {
                if ((options & ImportOptions.ResetPositions) != 0 && host.nav is not null && read!.IsCompletedSuccessfully) SaveImport.PlaceOnGround(read.Result, host.nav.Walkability.GroundHeight);
                return SwapInSave(read!, interactive: true, out summary, new SaveLoadOptions { Appearances = data.Appearances, ResetRelations = (options & ImportOptions.Relations) == 0 }, prepared);
            }),
            GameHost.Stage("finishing", () => { host.SettleNewWorld(); host.player.FollowSelected(); FinishLoadNotice(summary, imported: true); PauseAfterLoad(); return null; }));
    }

    internal sealed record PreparedSystems(StandardSystemSet Built, PopulationData Data, NewGameSettings Difficulty);

    /// <summary>The worker's hand-off of the save's hour to the sky arrival (GameHost.Transition.cs).</summary>
    void ReadArrivalHour(SaveCamera saved) => Volatile.Write(ref host.readHour, saved.Hour + saved.Minute / 60f);

    /// <summary>The systems of the world the save will be loaded into, with its difficulty: made apart from the swap, so a load builds them while the old
    /// world still shows. Throws <see cref="GameHost.SceneLoadFailed"/> when the save was not read.</summary>
    PreparedSystems PrepareSystems(Task<SaveGame> read, bool interactive)
    {
        if (read.IsFaulted || read.IsCanceled)
        {
            Console.Error.WriteLine($"load      read failed: {read.Exception?.GetBaseException()}");
            throw new GameHost.SceneLoadFailed($"Load failed: {read.Exception?.GetBaseException().Message ?? "cancelled"}");
        }
        var nextDifficulty = NewGameSettings.Read(read.Result.Camera);
        var nextData = nextDifficulty.Apply(host.popData!, Gameplay.Dismemberment);
        try { return new(BuildSystems(interactive, nextData, nextDifficulty), nextData, nextDifficulty); }
        catch (Exception e)
        {
            Console.Error.WriteLine($"load      failed: {e}");
            throw new GameHost.SceneLoadFailed($"Load failed: {e.Message}");
        }
    }

    /// <summary>
    /// Swaps in a world made from the save (at a tick boundary, the frame loop's) and hooks the host to it: the game clock, the camera on the player's squad. Returns the
    /// navmesh of the zones round the player to wait for before the first tick (<see cref="GameHost.SettleNewWorld"/>); throws <see cref="GameHost.SceneLoadFailed"/> when the save does
    /// not load (the old world stays).
    /// </summary>
    internal Task SwapInSave(Task<SaveGame> read, bool interactive, out string summary, SaveLoadOptions? loadOptions = null, PreparedSystems? prepared = null)
    {
        var watch = Stopwatch.StartNew();
        LoadedSave? loaded = null;
        var (built, nextData, nextDifficulty) = prepared ?? PrepareSystems(read, interactive);
        try
        {
            var data = nextData;
            // A world that fails to load leaves the old one running (ReplaceWorld drops the half-made one).
            host.session.ReplaceWorld(built.Systems, world =>
            {
                loaded = SaveLoader.Load(world, read.Result, data, loadOptions ?? new SaveLoadOptions { Appearances = data.Appearances });
                host.modding.RestoreModData(world, read.Result);
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
        host.popData = nextData;
        difficulty = nextDifficulty;
        host.player.NewGameOptions = nextDifficulty;   // the New game window then shows the loaded save's options (the original loads them into its globals)
        host.population = built.Population;
        if (host.nav is not null) host.population!.ZoneGate = zone => host.nav.Walkability.World.Contains(zone);
        SnapshotPlayerTowns();
        loadedSave = loaded;
        preparedBase = null;
        worldEpoch++;
        host.modding.NoticeModChanges(read.Result);
        host.levelEditorHost.pendingLevelEdits = read.Result.LevelEdits;   // replayed once the host has met the new world (UpdateBuildings)
        var world = host.session.World;
        host.bloodDecals.BloodForNewWorld(read.Result.Decals);
        host.session.Clock.SetTime(loaded!.Clock.Day, loaded.Clock.HourOfDay);
        // The regions' weather the save holds (docs/formats/save.md "BIOMES"); a save without (an old Meitou one) keeps the schedule rolled from day 0.
        if (host.ActiveWeather is { } weather && read.Result.Biomes is { } biomes && SaveWeather.Read(biomes) is { Count: > 0 } regions)
        {
            weather.Restore(regions, loaded.Clock.Day, loaded.Clock.HourOfDay);
            Console.WriteLine($"load      weather of {regions.Count} region(s) from the save");
        }
        host.playerSquad = world.Squads.Find(world.Player.Squad);
        foreach (var note in loaded.Notes) Console.WriteLine($"load      {note}");

        // The saved camera (FUN_1407ade60, docs/formats/camera.md "Saved camera"): pivot, orientation and boom, a pivot out of bounds moved to the loader's fallback. A save
        // without a view (an older Meitou one) puts the camera on the leader, keeping the view's heading, pitch and boom. The zones round the leader get their navmesh
        // before the first tick (like a new game's start).
        Task navmesh = Task.CompletedTask;
        var saved = read.Result.Camera;
        host.player.ReadBiome(saved);
        if (saved.HasView)
        {
            var (yaw, pitch, distance) = KenshiCamera.FromSaved(saved.Rotation, saved.Zoom);
            var pivot = KenshiCamera.SavedPivotInBounds(saved.Position) ? new Vector2(saved.Position.X, saved.Position.Z) : KenshiCamera.SavedPivotFallback;
            host.session.Camera.Place(pivot, yaw, pitch, distance);
        }
        if (host.playerSquad is { } squad && !squad.Leader.IsNone)
        {
            var at = world.Characters.Previous[squad.Leader.Slot].Position;
            if (!saved.HasView)
            {
                var view = host.session.Camera.Current;
                host.session.Camera.Place(new Vector2(at.X, at.Z), view.Yaw, view.Pitch, view.Distance);
            }
            navmesh = NavmeshAround(at);
        }
        summary = $"{readingFolder}: {loaded.PlayerPlatoons.Sum(p => p.Squad.Members.Count)} player character(s), {loaded.RoamingPlatoons.Count} roaming, {loaded.TownBoundPlatoons} town and {loaded.WildPlatoons} wild platoon(s) kept as saved, day {loaded.Clock.Day} {loaded.Clock.Hour:00}:{loaded.Clock.Minute:00}, made in {watch.ElapsedMilliseconds} ms";
        return navmesh;
    }

    internal void FinishLoadNotice(string summary, bool imported = false)
    {
        Console.WriteLine($"load      {summary}");
        var missing = loadedSave?.MissingMods ?? [];
        // The message panel fits a line or two: it gives the count, the message log the names.
        if (missing.Count == 0)
        {
            host.player.Notice(imported ? "Imported" : Loc.TrIn("save notice", "Loaded"));
            return;
        }
        string panel = Loc.Tr("Loaded; the save was made with {1} mod(s) not loaded now (listed in the messages)", missing.Count);
        host.player.Notice(panel, true, Loc.Tr("Loaded; the save was made with mods not loaded now: {1}", string.Join(", ", missing)));
    }

    /// <summary>A save loaded from the menus starts paused with <see cref="UserConfig.PauseOnLoad"/> (a player pause: the speed buttons resume it).</summary>
    void PauseAfterLoad()
    {
        if (host.config.PauseOnLoad) host.session.Simulation.Pause();
    }

    /// <summary>
    /// The navmesh of the zones round <paramref name="at"/> (the ones already loaded cost nothing): done when they are, or after 15 s (loading goes on without them).
    /// </summary>
    internal Task NavmeshAround(Vector3 at)
    {
        if (host.nav is null) return Task.CompletedTask;
        host.FinishNavUnload();
        long now = Stopwatch.GetTimestamp();
        var pending = new List<Task>();
        foreach (var z in ZoneActivation.ZonesAround(at, 0))
        {
            if (!host.navZones.ContainsKey(z)) pending.Add(host.nav.LoadZone(z));
            host.navZones[z] = now;
        }
        if (pending.Count == 0) return Task.CompletedTask;
        var all = Task.WhenAll(pending);
        return Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(15))).ContinueWith(t =>
        {
            if (t.Result != all) Console.Error.WriteLine("load      the navmesh round the player was not ready after 15 s; loading goes on");
        }, TaskScheduler.Default);
    }

    /// <summary>The towns the player's faction owns now, for the foliage placer (<see cref="Meitou.Data.World.FoliageWorld.PlayerTowns"/>, a snapshot taken when a world is made or loaded).</summary>
    internal void SnapshotPlayerTowns()
    {
        var owned = new HashSet<(int, int)>();
        if (host.popData is { PlayerFaction: >= 0 } d)
            foreach (var site in d.Sites)
                if (site.Index < d.SiteFactions.Count && d.SiteFactions[site.Index] == d.PlayerFaction)
                    owned.Add(Meitou.Data.World.FoliageWorld.TownKey(site.Position.X, site.Position.Z));
        Meitou.Data.World.FoliageWorld.PlayerTowns = owned;
    }
}
