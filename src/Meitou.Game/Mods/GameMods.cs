using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Game;
using Meitou.Game.DevTools;
using Meitou.Game.Host;
using Meitou.Simulation;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Population;

namespace Meitou.Modding;

/// <summary>
/// Code mods (docs/modding.md): loaded and initialized once in <see cref="GameHost.BootSimulation"/>, after the data and before the first world's
/// systems; their simulation extensions go into every <see cref="GameSaves.BuildSystems"/>, their interface extensions and notices to the interface,
/// their debug rows to the F10 menu, and their systems' save data into and out of the save's sidecar files.
/// </summary>
sealed class GameMods(GameHost host)
{
    /// <summary>
    /// The session's code mods; null without enabled code mods.
    /// </summary>
    internal ModHost? Mods;
    /// <summary>
    /// The notices of the mods whose settings Meitou took into its own features at this start (<see cref="BuiltInMods.Adopt"/>).
    /// </summary>
    List<string> _builtInNotices = [];
    /// <summary>
    /// Whether the JIT warm-up waits for the code mods (<see cref="StartDeferredWarmup"/>). With code mods enabled the JIT warm-up starts only once
    /// they are initialized: a method compiled before a Harmony patch keeps any inlined copy of the target.
    /// </summary>
    bool _warmupAfterMods;
    /// <summary>
    /// The world whose combat hit feed is <see cref="_hitFeed"/>; a new world gets a new queue.
    /// </summary>
    World? _hitsFor;
    /// <summary>
    /// The queue the current world publishes its combat hits to.
    /// </summary>
    ConcurrentQueue<CombatHit> _hitFeed = new();
    /// <summary>
    /// This frame's hits handed to the mods' feeds, kept to avoid allocating.
    /// </summary>
    readonly List<CombatHit> _frameHits = [];

    /// <summary>
    /// Starts the JIT warm-up now, or (with code mods enabled) once they are initialized.
    /// </summary>
    internal void StartWarmup()
    {
        if (CodeMods().Enabled.Count > 0) _warmupAfterMods = true;
        else JitWarmup.Start();
    }

    /// <summary>
    /// The code mods on disk (<see cref="ModHost.CodeMods(Content.GameInstall)"/>): the data phase's reading when the session has one, so the
    /// folders are scanned once.
    /// </summary>
    (List<Content.ModInfo> Enabled, List<Content.ModInfo> Disabled) CodeMods() => host.DataPhase?.CodeMods() ?? ModHost.CodeMods(host.Install);

    /// <summary>
    /// Starts the JIT warm-up held back for the code mods, once they are initialized.
    /// </summary>
    internal void StartDeferredWarmup()
    {
        if (!_warmupAfterMods) return;
        _warmupAfterMods = false;
        JitWarmup.Start(late: true);
    }

    /// <summary>
    /// Loads the enabled code mods and runs their initialization; on the boot worker. Mods whose code the data phase loaded already
    /// (<see cref="ModDataPhase"/>) are initialized from there.
    /// </summary>
    internal void StartMods(Data.GameDatabase db, PopulationData data)
    {
        // The world's body options were made from the config before this, so settings taken in from a mod reach them here.
        _builtInNotices = BuiltInMods.Adopt(host.Config, host.Install);
        (data.BodyOptions.SquadXp, data.BodyOptions.OthersXp) = (host.Config.SquadXp, host.Config.OthersXp);

        if (host.DataPhase?.Mods is { } prepared)
        {
            StartPrepared(prepared, host.DataPhase.Enabled, db, data);
            return;
        }

        var (enabled, disabled) = CodeMods();
        BuiltInMods.SkipCovered(enabled, disabled);
        if (enabled.Count == 0) return;
        LogEnabled(enabled.Count);
        Mods = ModHost.Load(Options(enabled, db, data, disabled));

        // No mod was found with a data phase, so an entry that is one anyway has it where the scan cannot see it.
        foreach (var mod in Mods.Mods.Where(m => m.Instance is IModDataPhase)) ModHost.NoteUnscannedDataPhase(mod);

        KeepModSettings(Mods);
    }

    /// <summary>
    /// Initializes the mods the data phase loaded (<see cref="ModHost.Start"/>).
    /// </summary>
    void StartPrepared(ModHost prepared, IReadOnlyList<Content.ModInfo> enabled, Data.GameDatabase db, PopulationData data)
    {
        if (enabled.Count == 0) return;
        LogEnabled(enabled.Count);
        prepared.Start(Options(enabled, db, data));
        Mods = prepared;
        KeepModSettings(prepared);
    }

    /// <summary>
    /// Keeps a value a mod sets itself (ModSetting.Set, Reset) as the page's are, on either start path; the page's own change is kept twice, the
    /// same value.
    /// </summary>
    void KeepModSettings(ModHost mods)
    {
        foreach (var group in mods.Settings) group.Changed += host.Config.KeepModSetting;
    }

    /// <summary>
    /// The log line naming how many code mods start.
    /// </summary>
    static void LogEnabled(int count) =>
        Console.WriteLine($"mods      {count} code mod(s) enabled; Harmony {ModHost.HarmonyVersion}, code-mod API {ModApi.Version}");

    /// <summary>
    /// What the mods are loaded or initialized with: the data, the population, the running world and the player's saved settings.
    /// </summary>
    ModHostOptions Options(IReadOnlyList<Content.ModInfo> enabled, Data.GameDatabase db, PopulationData data,
        IReadOnlyList<Content.ModInfo>? disabled = null) =>
        new(enabled, db, data, () => host.Session?.World, SavedSettings, RunningVersion(), disabled);

    /// <summary>
    /// The player's saved settings of a mod by its id, or null.
    /// </summary>
    IReadOnlyDictionary<string, string>? SavedSettings(string id) => host.Config.ModSettings.TryGetValue(id, out var saved) ? saved : null;

    /// <summary>
    /// The mods' interface extensions and their load notices, for a new interface.
    /// </summary>
    internal void ConnectModUi(PlayerInterface ui)
    {
        foreach (var notice in _builtInNotices) ui.Notice(notice);
        if (Mods is null) return;
        Mods.RegisterUi(ui.RegisterUiExtension);
        if (Mods.ArrangesFactionList) ui.FactionWindow.ArrangeList = Mods.ArrangeFactionList;
        foreach (var notice in Mods.Notices) ui.Notice(notice, true);
    }

    /// <summary>
    /// Once a frame, after the blood: the combat hits the world published since the last frame to the mods' feeds (docs/modding.md "Hit feed"). A new
    /// world gets a new queue, so the old world's hits never reach it.
    /// </summary>
    internal void UpdateModHits()
    {
        if (Mods is not { WantsCombatHits: true }) return;
        var world = host.Session.World;
        if (!ReferenceEquals(_hitsFor, world))
        {
            _hitsFor = world;
            _hitFeed = new();
            world.CombatHitFeed = _hitFeed;
            return;
        }
        _frameHits.Clear();
        while (_hitFeed.TryDequeue(out var hit)) _frameHits.Add(hit);
        Mods.DeliverCombatHits(_frameHits);
    }

    /// <summary>
    /// Once a frame: a notice for each mod system the world stopped after a fault (docs/modding.md "Tick systems").
    /// </summary>
    internal void UpdateModFaults()
    {
        if (Mods is null || host.Session?.World is not { } world) return;

        while (world.TryTakeModFaultNotice(out var notice)) Notice(notice);
    }

    /// <summary>
    /// This build's nearest game tag (<c>v*</c>, Meitou.Game.csproj) as a version for <c>gameVersion</c> ranges; null for a build without one (no git
    /// history), which passes every range.
    /// </summary>
    internal static Content.CodeModVersion? RunningVersion() =>
        Content.CodeModVersion.TryParse(BuildVersion.Tag.TrimStart('v'), out var version) ? version : null;

    /// <summary>
    /// The code mods running (none without code mods), tagged with this build's version.
    /// </summary>
    Content.CodeModEnvironment CurrentModEnvironment() => Mods?.Environment(BuildVersion.Tag) ?? new(BuildVersion.Tag, []);

    /// <summary>
    /// The save data of the world's mod systems and the code mods running (<c>meitou-mods/environment.json</c>) into the save being captured; between
    /// ticks. A game without code mods writes the environment only over a save that had one, so it then records that none run.
    /// </summary>
    internal void CaptureModData(World world, Data.Save.SaveGame game)
    {
        Mods?.CaptureSaveData(world, game.ModData);
        if (Mods is not null || game.ModEnvironment is not null || host.Saves.LoadedSave?.Source.ModEnvironment is not null)
            game.ModEnvironment = CurrentModEnvironment().ToJson();
    }

    /// <summary>
    /// After a load: what changed in the code mods since the save was made, as notices (docs/modding-dependencies.md "Saves"). Never refuses and
    /// never throws: it runs after the world is replaced, so a fault here would cut the rest of the load short.
    /// </summary>
    internal void NoticeModChanges(Data.Save.SaveGame save)
    {
        try
        {
            NoticeModChangesSince(save);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"mods      the save's code-mod record was not compared: {e}");
        }
    }

    /// <summary>
    /// Compares the save's code-mod record with the mods running now and notices each change.
    /// </summary>
    void NoticeModChangesSince(Data.Save.SaveGame save)
    {
        if (save.ModEnvironment is not { } bytes) return;
        if (Content.CodeModEnvironment.TryRead(bytes) is not { } saved)
        {
            Console.Error.WriteLine("mods      the save's code-mod record could not be read; not compared");
            return;
        }
        foreach (var line in BuiltInMods.WithoutCovered(saved).ChangesTo(CurrentModEnvironment()))
        {
            Console.WriteLine($"mods      save: {line}");
            host.Player.Notice(line, true);
        }
    }

    /// <summary>
    /// The save's mod data into the systems of the world just loaded from it; between ticks, before the first.
    /// </summary>
    internal void RestoreModData(World world, Data.Save.SaveGame save) => Mods?.RestoreSaveData(world, save.ModData);

    /// <summary>
    /// The squads of the world just loaded that a code mod made autonomous or kept out of the faction size count, when that mod is not running now
    /// (not installed, refused or failed to start), go back to the player (<see cref="Simulation.Ai.SquadAutonomy.DropUnowned"/>) and count again
    /// (<see cref="Simulation.Factions.SquadManagement.DropUnownedUncounted"/>), each with a notice; between ticks, before the first.
    /// </summary>
    internal void DropUnownedAutonomy(World world)
    {
        var running = (Mods?.Mods ?? [])
            .Select(m => m.Manifest.Id)
            .ToHashSet(StringComparer.Ordinal);
        int dropped = Simulation.Ai.SquadAutonomy.DropUnowned(world, running.Contains);
        if (dropped > 0) Notice($"{dropped} squad(s) run by a mod that is not loaded are yours again.");

        int counted = Simulation.Factions.SquadManagement.DropUnownedUncounted(world, running.Contains);
        if (counted > 0) Notice($"{counted} squad(s) a mod that is not loaded kept out of your faction size count are counted again.");

        int purses = Simulation.Factions.SquadManagement.DropUnownedPurses(world, running.Contains);
        if (purses > 0) Notice($"{purses} squad purse(s) of a mod that is not loaded went into your money.");

        int woken = Simulation.Factions.SquadManagement.DropUnownedDormant(world, running.Contains);
        if (woken > 0) Notice($"{woken} squad(s) a mod that is not loaded put to sleep are awake again.");
    }

    /// <summary>
    /// Shows a line about the mods to the player and in the console.
    /// </summary>
    void Notice(string line)
    {
        Console.WriteLine($"mods      {line}");
        host.Player.Notice(line, true);
    }

    /// <summary>
    /// The debug menu rows the mods registered, after the game's own.
    /// </summary>
    internal IReadOnlyList<ModDebugCommand> ModDebugRows => Mods?.DebugCommands ?? [];

    /// <summary>
    /// The debug menu's rows: the game's labels, then the mods'.
    /// </summary>
    internal int DebugRowCount => DebugMenu.DebugLabels.Length + ModDebugRows.Count;

    /// <summary>
    /// A debug menu row's label: the game's, or the mod's command with its owner in brackets.
    /// </summary>
    internal string DebugLabel(int row)
    {
        if (row < DebugMenu.DebugLabels.Length) return DebugMenu.DebugLabels[row];
        var command = ModDebugRows[row - DebugMenu.DebugLabels.Length];
        return $"{command.Label} [{command.Owner}]";
    }

    /// <summary>
    /// Grows the debug menu's typed fields to one per row.
    /// </summary>
    internal void EnsureDebugFields()
    {
        while (host.DebugMenu.DebugFields.Count < DebugRowCount) host.DebugMenu.DebugFields.Add("");
    }

    /// <summary>
    /// A mod's debug row: its handler with the field, the selection and the camera target; a throw shows as the status line.
    /// </summary>
    internal string ModDebugApply(int row, string field, CharacterId[] selected, Vector3 here, long tick)
    {
        var command = ModDebugRows[row];
        var context = new DebugCommandContext(field, selected, here, tick, host.Session.World.Commands.Enqueue);
        try
        {
            return command.Run(context) ?? "";
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"mod       {command.Owner}: debug command '{command.Label}' failed: {e}");
            return $"{command.Owner}: {e.GetBaseException().Message}";
        }
    }
}