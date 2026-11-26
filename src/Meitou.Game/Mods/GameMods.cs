using System.Collections.Concurrent;
using System.Numerics;
using Meitou.Game;
using Meitou.Game.DevTools;
using Meitou.Game.Effects;
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
    /// Built-in KenshiLua compatibility host; null when no enabled mod contains <c>scripts/init/*.lua</c>.
    /// </summary>
    internal LuaMods? Lua;
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
    /// The world the mods' effects were queued for; a new world drops them.
    /// </summary>
    World? _effectsFor;
    /// <summary>
    /// Plays the mods' effects; made with the first one.
    /// </summary>
    ModEffectPlayer? _effectPlayer;
    /// <summary>
    /// The world the mods' glows were set for and its game second at the last frame; a new world drops them.
    /// </summary>
    (World? World, double Seconds) _glowsFor;
    /// <summary>
    /// This frame's glows, kept to avoid allocating.
    /// </summary>
    readonly List<(GlowTarget Target, GlowNow Glow)> _frameGlows = [];
    /// <summary>
    /// The bolts the projectile feeds saw last, and the world and snapshot they were seen in; a new world starts them again.
    /// </summary>
    readonly ProjectileWatch _bolts = new();
    /// <summary>
    /// The world and snapshot tick <see cref="_bolts"/> last looked at.
    /// </summary>
    (World? World, long Tick) _boltsAt = (null, long.MinValue);
    /// <summary>
    /// This frame's bolt changes, kept to avoid allocating.
    /// </summary>
    readonly List<ProjectileEvent> _frameBolts = [];
    /// <summary>
    /// The world the mods' trails were kept for; a new world drops them.
    /// </summary>
    World? _trailsFor;

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
        data.BodyOptions.SquadSkillXp = Meitou.Game.Settings.SkillXp.Rates(host.Config.SquadSkillXp);
        (data.BodyOptions.MaxFactionSize, data.BodyOptions.MaxSquadSize, data.BodyOptions.MaxSquads) =
            (host.Config.MaxFactionSize, host.Config.MaxSquadSize, host.Config.MaxSquads);

        if (host.DataPhase?.Mods is { } prepared) StartPrepared(prepared, host.DataPhase.Enabled, db, data);
        else LoadMods(db, data);

        // Every skill is added now: a weapon naming one that is not uses its category's, which the log says once (docs/modding.md "Weapon types").
        Simulation.Bodies.ModSkills.WarnUnknown(db);

        // Built-in KenshiLua compat host: stays null (zero cost) when no enabled mod has scripts/init/*.lua.
        Lua = LuaMods.TryStart(host.Install, db, data.Relations);
    }

    /// <summary>
    /// Loads and initializes the enabled code mods when no data phase loaded their code (<see cref="StartPrepared"/> starts those).
    /// </summary>
    void LoadMods(Data.GameDatabase db, PopulationData data)
    {
        var (enabled, disabled) = CodeMods();
        BuiltInMods.SkipCovered(enabled, disabled);
        if (enabled.Count == 0) return;
        LogEnabled(enabled.Count);
        Mods = ModHost.Load(Options(enabled, db, data, disabled));
        Mods.AudioModulations.Attach(host.Sound.Audio?.Player);

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
        Mods.AudioModulations.Attach(host.Sound.Audio?.Player);
        KeepModSettings(prepared);
    }

    /// <summary>
    /// Keeps a value a mod sets itself (ModSetting.Set, Reset) as the page's are, on either start path; the page's own change is kept twice, the
    /// same value.
    /// </summary>
    void KeepModSettings(ModHost mods)
    {
        foreach (var group in mods.Settings)
        {
            group.Changed += host.Config.KeepModSetting;
            group.UsePresets(host.Config.ModSettingPresets.GetValueOrDefault(group.ModId));
            group.PresetsChanged += host.Config.KeepModPresets;
        }
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
        Mods.RegisterUi((extension, folder, texts) => ui.RegisterUiExtension(extension, folder, texts));
        Mods.AttachInterface(new Game.Ui.PlayerModInterface(ui));
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
    /// Once a frame, after the hits: the bolts' changes between the shown snapshots to the mods' projectile feeds (docs/modding.md "Projectile
    /// feed"). Nothing without a feed; a new world is taken as it is, telling nothing of its bolts already there.
    /// </summary>
    internal void UpdateModProjectiles()
    {
        if (Mods is not { WantsProjectiles: true } || host.Session?.CurrentSnapshot is not { } snapshot) return;

        var world = host.Session.World;
        if (!ReferenceEquals(_boltsAt.World, world))
        {
            _bolts.Reset(snapshot.Projectiles);
            _boltsAt = (world, snapshot.Tick);
            return;
        }
        if (snapshot.Tick == _boltsAt.Tick) return;

        _boltsAt.Tick = snapshot.Tick;
        _frameBolts.Clear();
        _bolts.Look(snapshot.Projectiles, _frameBolts);
        Mods.DeliverProjectiles(_frameBolts);
    }

    /// <summary>
    /// Once a frame, after the hits: the mods' queued effects start and the playing ones follow their characters (docs/modding.md "Visual
    /// effects"). Nothing without code mods; a new world drops the old world's effects.
    /// </summary>
    internal void UpdateModEffects()
    {
        if (Mods is null) return;

        var effects = Mods.Effects;
        _effectPlayer ??= new ModEffectPlayer(host);
        effects.Resolve ??= _effectPlayer.Resolve;
        effects.Exists ??= _effectPlayer.Exists;
        effects.Locate ??= _effectPlayer.Place;
        var world = host.Session?.World;
        if (!ReferenceEquals(_effectsFor, world))
        {
            _effectsFor = world;
            effects.Clear();
        }
        if (!effects.Idle) _effectPlayer.Advance(effects);
    }

    /// <summary>
    /// Once a frame, after the effects: the mods' trails sample their anchors by the particles' frame clock and hand their quads to the particles
    /// (docs/modding.md "Trails"). Nothing without code mods; a new world drops the old world's trails.
    /// </summary>
    internal void UpdateModTrails()
    {
        if (Mods is null) return;

        var trails = Mods.Trails;
        var world = host.Session?.World;
        if (!ReferenceEquals(_trailsFor, world))
        {
            _trailsFor = world;
            trails.Clear();
        }
        if (host.Gpu?.Particles is not { } particles) return;

        _effectPlayer ??= new ModEffectPlayer(host);
        if (trails.Count > 0 || particles.Ribbons.Count > 0) trails.Advance(particles.LastStep, _effectPlayer, particles.Ribbons);
    }

    /// <summary>
    /// Once a frame: ages the mods' glows by the game clock (pause and game speed hold and hasten them) and hands them to the character
    /// renderer by draw key (docs/modding.md "Glow"). Nothing without code mods; a new world drops the old world's glows.
    /// </summary>
    internal void UpdateModGlows()
    {
        if (Mods is null) return;

        var glows = Mods.Glows;
        var world = host.Session?.World;
        double now = host.Session?.Clock.GameSeconds ?? 0;
        if (!ReferenceEquals(_glowsFor.World, world))
        {
            _glowsFor = (world, now);
            glows.Clear();
        }
        glows.Advance((float)Math.Clamp(now - _glowsFor.Seconds, 0, 1));
        _glowsFor.Seconds = now;
        ShowGlows(glows);
    }

    /// <summary>
    /// Puts this frame's glows into the character renderer's table (left empty when none glow).
    /// </summary>
    void ShowGlows(ModGlows glows)
    {
        if (host.Gpu?.Characters is not { } crowd) return;

        crowd.Glows.Clear();
        host.Gpu.ThroughKeys.Clear();
        if (glows.Count == 0) return;

        glows.Current(_frameGlows);
        foreach (var (target, glow) in _frameGlows)
        {
            long key = host.DrawKey(target.On);
            crowd.SetGlow(key, target, glow.Colour, glow.Strength, glow.Mode);
            if (glow.ThroughWalls) host.Gpu.ThroughKeys.Add((key, new Vector4(glow.Colour, Math.Clamp(glow.Strength, 0, 1))));
        }
    }

    /// <summary>
    /// Once a frame: a notice for each mod system the world stopped after a fault (docs/modding.md "Tick systems") and one for each mod that
    /// failed anywhere as built for another version of the game (<see cref="Content.CodeModFaults.Tell"/>).
    /// </summary>
    internal void UpdateModFaults()
    {
        if (Mods is null) return;

        while (Content.CodeModFaults.TryTakeNotice(out var outdated)) Notice(outdated);
        if (host.Session?.World is not { } world) return;

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
    /// Once a frame, after the hits: the mods' per-frame callbacks get what the host shows (docs/modding.md "Frame"). Nothing without one, or over
    /// the title (no world shown).
    /// </summary>
    internal void UpdateModFrames()
    {
        if (host.Player is null || host.Player.TitleOpen) return;
        Lua?.UpdateFrame(host);
        if (Mods is not { WantsFrames: true } mods) return;

        mods.DeliverFrame(_frame ??= new ModFrame(host));
    }

    /// <summary>
    /// The frame the mods read, made with the first callback (its reads are live).
    /// </summary>
    ModFrame? _frame;

    /// <summary>
    /// A save folder was written: its full path to the mods' save-written listeners (docs/modding.md "Persistence"); on the host's thread.
    /// </summary>
    internal void TellSaveWritten(string folder) => Mods?.TellSaveWritten(Path.GetFullPath(folder));

    /// <summary>
    /// The save's mod data into the systems of the world just loaded from it; between ticks, before the first.
    /// </summary>
    internal void RestoreModData(World world, Data.Save.SaveGame save) => Mods?.RestoreSaveData(world, save.ModData);

    /// <summary>
    /// The squads of the world just loaded that a code mod made autonomous or kept out of the faction size count, when that mod is not running now
    /// (not installed, refused or failed to start), go back to the player (<see cref="Simulation.Ai.SquadAutonomy.DropUnowned"/>) and count again
    /// (<see cref="Simulation.Factions.SquadManagement.DropUnownedUncounted"/>), and lose the shops, purses, sleep and squad AI options it
    /// gave them, each with a notice; the NPC platoons it held in its name move on their own again
    /// (<see cref="Simulation.Population.PopulationSystem.DropUnownedHolds"/>,
    /// a log line only); between ticks, before the first.
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

        int shops = Simulation.Trading.TradeSystem.DropUnownedShops(world, running.Contains, out int grounded);
        if (shops > 0) Notice(ShopsClosed(shops, grounded));

        int purses = Simulation.Factions.SquadManagement.DropUnownedPurses(world, running.Contains);
        if (purses > 0) Notice($"{purses} squad purse(s) of a mod that is not loaded went into your money.");

        int woken = Simulation.Factions.SquadManagement.DropUnownedDormant(world, running.Contains);
        if (woken > 0) Notice($"{woken} squad(s) a mod that is not loaded put to sleep are awake again.");

        int options = Simulation.Factions.SquadManagement.DropUnownedOptions(world, running.Contains);
        if (options > 0)
        {
            Notice(Meitou.Localisation.Loc.Tr(
                "{1} squad(s) given their own squad AI options by a mod that is not loaded follow yours again.", options));
        }

        // NPC platoons a missing mod held go back to the population; the player sees no change, so the log only.
        int released = Simulation.Population.PopulationSystem.DropUnownedHolds(world, running.Contains);
        if (released > 0)
            Console.WriteLine($"mods      {released} platoon(s) a mod that is not loaded held move on their own again.");
    }

    /// <summary>
    /// The notice for the shops closed at a load: their goods went to the squads' members, and what did not fit onto the ground.
    /// </summary>
    static string ShopsClosed(int shops, int dropped) => dropped == 0
        ? $"{shops} shop(s) a mod that is not loaded gave your squads are closed; their goods went to the squads' members."
        : $"{shops} shop(s) a mod that is not loaded gave your squads are closed; their goods went to the squads' members where they fit, "
            + $"and {dropped} stack(s) lie on the ground by the squads.";

    /// <summary>
    /// Shows a line about the mods to the player and in the console.
    /// </summary>
    void Notice(string line)
    {
        Console.WriteLine($"mods      {line}");
        host.Player.Notice(line, true);
    }

    /// <summary>
    /// The mods' key actions, in load order (none without code mods), for the bindings (<see cref="GameHost.UseSavedBindings"/>).
    /// </summary>
    internal IReadOnlyList<Game.Host.Input.ModKeyAction> KeyActions => Mods?.KeyActions ?? [];

    /// <summary>
    /// Once a control tick on the host's thread: each mod key action's held state, and its press for each one pressed this tick (docs/modding.md
    /// "Key actions"), with the snapshot on screen, the selection and the command queue; none over the title or a loading cover.
    /// </summary>
    internal void PressKeyActions(Game.Host.Input.ActionState actions)
    {
        var keyActions = KeyActions;
        if (keyActions.Count == 0) return;

        bool playing = host.Player is { TitleOpen: false } && !host.Covered;
        Game.Host.Input.ModKeyContext? context = null;
        foreach (var action in keyActions)
        {
            action.Held = playing && actions.Held(action.Action);
            if (!playing || !actions.Pressed(action.Action)) continue;

            context ??= KeyContext();
            action.Press(context);
        }
    }

    /// <summary>
    /// A key action press's context: the snapshot on screen, the selection, the tick, the command queue and the modifiers held now.
    /// </summary>
    Game.Host.Input.ModKeyContext KeyContext()
    {
        var input = host.Session.Input;
        bool Held(Game.Host.Input.Key left, Game.Host.Input.Key right) => input.IsHeld(left) || input.IsHeld(right);

        return new Game.Host.Input.ModKeyContext(host.Session.CurrentSnapshot, host.Player.SelectedIds(), host.Session.World.Tick,
            host.Session.World.Commands.Enqueue)
        {
            Frame = _frame ??= new ModFrame(host),
            Ctrl = Held(Game.Host.Input.Key.LeftControl, Game.Host.Input.Key.RightControl),
            Shift = Held(Game.Host.Input.Key.LeftShift, Game.Host.Input.Key.RightShift),
            Alt = Held(Game.Host.Input.Key.LeftAlt, Game.Host.Input.Key.RightAlt),
        };
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
            Content.CodeModFaults.Tell(command.Owner, e);
            return $"{command.Owner}: {e.GetBaseException().Message}";
        }
    }
}