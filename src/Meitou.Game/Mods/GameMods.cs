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
/// Code mods (docs/modding.md): loaded and initialized once in <see cref="GameHost.BootSimulation"/>, after the data and before the first world's systems; their simulation
/// extensions go into every <see cref="GameHost.BuildSystems"/>, their interface extensions and notices to the interface, their debug rows to the F10 menu, and their
/// systems' save data into and out of the save's sidecar files.
/// </summary>
sealed class GameMods(GameHost host)
{
    internal ModHost? mods;
    // With code mods enabled the JIT warm-up starts only once they are initialized: a method compiled before a Harmony patch keeps any inlined copy
    // of the target.
    bool warmupAfterMods;
    // The mods' hit feeds: the queue the current world fills, and the frame's hits handed to them.
    World? hitsFor;
    ConcurrentQueue<CombatHit> hitFeed = new();
    readonly List<CombatHit> frameHits = [];

    /// <summary>Starts the JIT warm-up now, or (with code mods enabled) once they are initialized.</summary>
    internal void StartWarmup()
    {
        if (ModHost.EnabledCodeMods(host.install).Count > 0) warmupAfterMods = true;
        else JitWarmup.Start();
    }

    internal void StartDeferredWarmup()
    {
        if (!warmupAfterMods) return;
        warmupAfterMods = false;
        JitWarmup.Start();
    }

    /// <summary>Loads the enabled code mods and runs their initialization; on the boot worker.</summary>
    internal void StartMods(Meitou.Data.GameDatabase db, PopulationData data)
    {
        var (enabled, disabled) = ModHost.CodeMods(host.install);
        if (enabled.Count == 0) return;
        Console.WriteLine($"mods      {enabled.Count} code mod(s) enabled; Harmony {ModHost.HarmonyVersion}, code-mod API {ModApi.Version}");
        mods = ModHost.Load(new ModHostOptions(enabled, db, data, () => host.session?.World,
            id => host.config.ModSettings.TryGetValue(id, out var saved) ? saved : null, RunningVersion(), disabled));
    }

    /// <summary>The mods' interface extensions and their load notices, for a new interface.</summary>
    internal void ConnectModUi(PlayerInterface ui)
    {
        if (mods is null) return;
        mods.RegisterUi(ui.RegisterUiExtension);
        foreach (var notice in mods.Notices) ui.Notice(notice, true);
    }

    /// <summary>
    /// Once a frame, after the blood: the combat hits the world published since the last frame to the mods' feeds (docs/modding.md "Hit feed"). A new
    /// world gets a new queue, so the old world's hits never reach it.
    /// </summary>
    internal void UpdateModHits()
    {
        if (mods is not { WantsCombatHits: true }) return;
        var world = host.session.World;
        if (!ReferenceEquals(hitsFor, world))
        {
            hitsFor = world;
            hitFeed = new();
            world.CombatHitFeed = hitFeed;
            return;
        }
        frameHits.Clear();
        while (hitFeed.TryDequeue(out var hit)) frameHits.Add(hit);
        mods.DeliverCombatHits(frameHits);
    }

    /// <summary>
    /// This build's nearest game tag (<c>v*</c>, Meitou.Game.csproj) as a version for <c>gameVersion</c> ranges; null for a build without one (no git
    /// history), which passes every range.
    /// </summary>
    static Meitou.Content.CodeModVersion? RunningVersion() =>
        Meitou.Content.CodeModVersion.TryParse(BuildVersion.Tag.TrimStart('v'), out var version) ? version : null;

    Meitou.Content.CodeModEnvironment CurrentModEnvironment() => mods?.Environment(BuildVersion.Tag) ?? new(BuildVersion.Tag, []);

    /// <summary>
    /// The save data of the world's mod systems and the code mods running (<c>meitou-mods/environment.json</c>) into the save being captured; between
    /// ticks. A game without code mods writes the environment only over a save that had one, so it then records that none run.
    /// </summary>
    internal void CaptureModData(World world, Meitou.Data.Save.SaveGame game)
    {
        mods?.CaptureSaveData(world, game.ModData);
        if (mods is not null || game.ModEnvironment is not null || host.loadedSave?.Source.ModEnvironment is not null)
            game.ModEnvironment = CurrentModEnvironment().ToJson();
    }

    /// <summary>
    /// After a load: what changed in the code mods since the save was made, as notices (docs/modding-dependencies.md "Saves"). Never refuses and
    /// never throws: it runs after the world is replaced, so a fault here would cut the rest of the load short.
    /// </summary>
    internal void NoticeModChanges(Meitou.Data.Save.SaveGame save)
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

    void NoticeModChangesSince(Meitou.Data.Save.SaveGame save)
    {
        if (save.ModEnvironment is not { } bytes) return;
        if (Meitou.Content.CodeModEnvironment.TryRead(bytes) is not { } saved)
        {
            Console.Error.WriteLine("mods      the save's code-mod record could not be read; not compared");
            return;
        }
        foreach (var line in saved.ChangesTo(CurrentModEnvironment()))
        {
            Console.WriteLine($"mods      save: {line}");
            host.player.Notice(line, true);
        }
    }

    /// <summary>The save's mod data into the systems of the world just loaded from it; between ticks, before the first.</summary>
    internal void RestoreModData(World world, Meitou.Data.Save.SaveGame save) => mods?.RestoreSaveData(world, save.ModData);

    internal IReadOnlyList<ModDebugCommand> ModDebugRows => mods?.DebugCommands ?? [];

    internal int DebugRowCount => DebugMenu.DebugLabels.Length + ModDebugRows.Count;

    internal string DebugLabel(int row)
    {
        if (row < DebugMenu.DebugLabels.Length) return DebugMenu.DebugLabels[row];
        var command = ModDebugRows[row - DebugMenu.DebugLabels.Length];
        return $"{command.Label} [{command.Owner}]";
    }

    internal void EnsureDebugFields()
    {
        while (host.debugMenu.debugFields.Count < DebugRowCount) host.debugMenu.debugFields.Add("");
    }

    /// <summary>A mod's debug row: its handler with the field, the selection and the camera target; a throw shows as the status line.</summary>
    internal string ModDebugApply(int row, string field, CharacterId[] selected, Vector3 here, long tick)
    {
        var command = ModDebugRows[row];
        var context = new DebugCommandContext(field, selected, here, tick, host.session.World.Commands.Enqueue);
        try { return command.Run(context) ?? ""; }
        catch (Exception e)
        {
            Console.Error.WriteLine($"mod       {command.Owner}: debug command '{command.Label}' failed: {e}");
            return $"{command.Owner}: {e.GetBaseException().Message}";
        }
    }
}
