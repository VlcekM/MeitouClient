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
        if (ModHost.EnabledCodeMods(host.Install).Count > 0) _warmupAfterMods = true;
        else JitWarmup.Start();
    }

    /// <summary>
    /// Starts the JIT warm-up held back for the code mods, once they are initialized.
    /// </summary>
    internal void StartDeferredWarmup()
    {
        if (!_warmupAfterMods) return;
        _warmupAfterMods = false;
        JitWarmup.Start();
    }

    /// <summary>
    /// Loads the enabled code mods and runs their initialization; on the boot worker.
    /// </summary>
    internal void StartMods(Data.GameDatabase db, PopulationData data)
    {
        var (enabled, disabled) = ModHost.CodeMods(host.Install);
        if (enabled.Count == 0) return;
        Console.WriteLine($"mods      {enabled.Count} code mod(s) enabled; Harmony {ModHost.HarmonyVersion}, code-mod API {ModApi.Version}");
        Mods = ModHost.Load(new ModHostOptions(enabled, db, data, () => host.Session?.World,
            id => host.Config.ModSettings.TryGetValue(id, out var saved) ? saved : null, RunningVersion(), disabled));
    }

    /// <summary>
    /// The mods' interface extensions and their load notices, for a new interface.
    /// </summary>
    internal void ConnectModUi(PlayerInterface ui)
    {
        if (Mods is null) return;
        Mods.RegisterUi(ui.RegisterUiExtension);
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
    /// This build's nearest game tag (<c>v*</c>, Meitou.Game.csproj) as a version for <c>gameVersion</c> ranges; null for a build without one (no git
    /// history), which passes every range.
    /// </summary>
    static Content.CodeModVersion? RunningVersion() =>
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
        foreach (var line in saved.ChangesTo(CurrentModEnvironment()))
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