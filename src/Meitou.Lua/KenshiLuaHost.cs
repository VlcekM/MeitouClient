using System.Diagnostics;
using MoonSharp.Interpreter;

namespace Meitou.Lua;

/// <summary>
/// Descriptor for a discovered KenshiLua mod containing <c>scripts/init/*.lua</c>.
/// </summary>
public sealed record LuaModDescriptor(string ModId, string ModFolder, IReadOnlyList<string> InitScripts);

/// <summary>
/// Built-in KenshiLua compatibility runtime for Meitou.
/// Loads enabled mods that contain <c>scripts/init/*.lua</c> into isolated, instruction-budgeted
/// MoonSharp sandboxes, dispatches KenshiLua events each frame, and stays completely idle when
/// no enabled mods contain Lua scripts.
/// </summary>
public sealed class KenshiLuaHost : IDisposable
{
    #region Host state

    /// <summary>
    /// The engine abstraction the Lua API reads from and acts through.
    /// </summary>
    readonly ILuaWorldBridge _bridge;

    /// <summary>
    /// The HUD overlay state Lua widgets are written to; null when there is no overlay.
    /// </summary>
    readonly LuaHudState? _hud;

    /// <summary>
    /// The instruction budgets handed to every sandbox.
    /// </summary>
    readonly LuaLimits _limits;

    /// <summary>
    /// Where log lines go.
    /// </summary>
    readonly Action<string> _log;

    /// <summary>
    /// The discovered mods by id, whether running or not.
    /// </summary>
    readonly Dictionary<string, ModRuntime> _mods = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Active file-system hot-reloader; null when hot-reload is disabled.
    /// </summary>
    LuaHotReloader? _hotReloader;

    /// <summary>
    /// Whether at least one KenshiLua mod is currently running.
    /// </summary>
    public bool HasRunningMods => _mods.Values.Any(m => m.Running);

    /// <summary>
    /// Creates a new KenshiLua runtime host.
    /// </summary>
    public KenshiLuaHost(
        ILuaWorldBridge bridge, LuaHudState? hud = null, LuaLimits? limits = null, Action<string>? log = null, string? stateDir = null)
    {
        _bridge = bridge;
        _hud = hud;
        _limits = limits ?? new LuaLimits();
        _log = log ?? Console.WriteLine;
        _stateDir = stateDir;
    }

    /// <summary>
    /// Returns all discovered Lua mods and their runtime status.
    /// </summary>
    public IReadOnlyList<(LuaModDescriptor Info, bool Running, string? Error)> List() =>
        _mods.Values.OrderBy(m => m.Info.ModId, StringComparer.OrdinalIgnoreCase)
            .Select(m => (m.Info, m.Running, m.Error)).ToList();

    /// <summary>
    /// The mutable runtime state of one discovered mod.
    /// </summary>
    sealed class ModRuntime(LuaModDescriptor info)
    {
        /// <summary>
        /// The discovered mod this runtime belongs to.
        /// </summary>
        public LuaModDescriptor Info { get; set; } = info;

        /// <summary>
        /// The mod's sandbox while it runs; null otherwise.
        /// </summary>
        public LuaSandbox? Sandbox;

        /// <summary>
        /// Whether the mod is currently running.
        /// </summary>
        public bool Running;

        /// <summary>
        /// The last start or fault error; null when there is none.
        /// </summary>
        public string? Error;

        /// <summary>
        /// World actions the mod has enqueued in the current frame.
        /// </summary>
        public int ActionsThisTick;

        /// <summary>
        /// The name of the character the mod was last told was selected.
        /// </summary>
        public string? LastSelectedName;

        /// <summary>
        /// The buildings the mod has already been told about.
        /// </summary>
        public HashSet<string> SeenBuildings { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The click callbacks of the mod's HUD buttons by widget id.
        /// </summary>
        public Dictionary<string, DynValue> HudCallbacks { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The results of the mod's <c>require</c> calls by module name.
        /// </summary>
        public Dictionary<string, DynValue> RequireCache { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    #endregion

    #region Discovery

    /// <summary>
    /// Scans the given enabled mod folders for <c>scripts/init/*.lua</c> (skipping KenshiLua itself and reparse points).
    /// Returns the number of discovered Lua mods.
    /// </summary>
    public int DiscoverEnabledMods(IEnumerable<(string ModId, string ModFolder)> enabledMods)
    {
        int found = 0;
        foreach ((string modId, string modFolder) in enabledMods)
        {
            if (IsScannable(modId, modFolder) && TryDiscoverMod(modId, modFolder)) found++;
        }

        return found;
    }

    /// <summary>
    /// Whether a mod entry names a folder that exists and is not KenshiLua itself.
    /// </summary>
    static bool IsScannable(string modId, string modFolder)
    {
        if (string.IsNullOrWhiteSpace(modId) || string.IsNullOrWhiteSpace(modFolder)) return false;
        if (modId.Equals("KenshiLua", StringComparison.OrdinalIgnoreCase)) return false;

        return Directory.Exists(modFolder);
    }

    /// <summary>
    /// Registers the mod when its folder has init scripts; false when it has none or cannot be read.
    /// </summary>
    bool TryDiscoverMod(string modId, string modFolder)
    {
        try
        {
            if ((File.GetAttributes(modFolder) & FileAttributes.ReparsePoint) != 0) return false;

            List<string>? scripts = FindInitScripts(modFolder);
            if (scripts is null || scripts.Count == 0) return false;

            if (!_mods.ContainsKey(modId))
            {
                _mods[modId] = new ModRuntime(new LuaModDescriptor(modId, modFolder, scripts));
            }

            return true;
        }
        catch (Exception)
        {
            // A mod folder that cannot be read is skipped.
            return false;
        }
    }

    /// <summary>
    /// The mod-relative paths of the non-reparse <c>scripts/init/*.lua</c> files in order; null when the init folder is missing or a reparse point.
    /// </summary>
    static List<string>? FindInitScripts(string modFolder)
    {
        string initDir = Path.Combine(modFolder, "scripts", "init");
        if (!Directory.Exists(initDir) || (File.GetAttributes(initDir) & FileAttributes.ReparsePoint) != 0) return null;

        return Directory.GetFiles(initDir, "*.lua")
            .Where(f => (File.GetAttributes(f) & FileAttributes.ReparsePoint) == 0)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => Path.GetRelativePath(modFolder, f).Replace('\\', '/'))
            .ToList();
    }

    #endregion

    #region Lifecycle

    /// <summary>
    /// Maximum world actions one mod may enqueue in a single frame.
    /// </summary>
    public const int MaxActionsPerTick = 16;

    /// <summary>
    /// The seed of the next sandbox's deterministic random generator.
    /// </summary>
    ulong _seed = 7001;

    /// <summary>
    /// Starts all discovered enabled Lua mods.
    /// </summary>
    public IReadOnlyList<string> StartAll()
    {
        List<string> started = [];
        foreach (string id in _mods.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            (bool ok, _) = Start(id);
            if (ok) started.Add(id);
        }

        return started;
    }

    /// <summary>
    /// Starts a single discovered Lua mod by id.
    /// </summary>
    public (bool Ok, string Message) Start(string modId)
    {
        if (!_mods.TryGetValue(modId, out ModRuntime? m)) return (false, $"unknown Lua mod '{modId}'");
        if (m.Running) return (true, $"Lua mod '{m.Info.ModId}' is already running");

        LuaSandbox sandbox = ResetForStart(m);
        InstallApis(m, sandbox);

        string? failure = RunInitScripts(m, sandbox, modId);
        if (failure is not null) return (false, failure);

        m.Running = true;
        m.SeenBuildings.Clear();
        m.LastSelectedName = null;
        FirePlayerLoad(m);

        return (true, $"started Lua mod '{m.Info.ModId}' ({m.Info.InitScripts.Count} init script(s))");
    }

    /// <summary>
    /// Stops a running Lua mod, persists its GameData state, and removes all its HUD widgets.
    /// </summary>
    public (bool Ok, string Message) Stop(string modId)
    {
        if (!_mods.TryGetValue(modId, out ModRuntime? m)) return (false, $"unknown Lua mod '{modId}'");
        if (m.Running && m.Sandbox is { Faulted: false }) FirePlayerSerialise(m);

        _hud?.ClearMod("kl:" + m.Info.ModId);
        m.HudCallbacks.Clear();
        m.RequireCache.Clear();
        m.SeenBuildings.Clear();
        m.LastSelectedName = null;
        m.Sandbox = null;
        m.Running = false;

        return (true, $"stopped Lua mod '{m.Info.ModId}'");
    }

    /// <summary>
    /// Stops all running Lua mods.
    /// </summary>
    public void StopAll()
    {
        foreach (string id in _mods.Keys.ToList()) Stop(id);
    }

    /// <summary>
    /// Hot-reloads a discovered Lua mod from disk: re-scans <c>scripts/init/*.lua</c>,
    /// persists its GameData state, clears old HUD widgets, and restarts it cleanly.
    /// </summary>
    public (bool Ok, string Message) Reload(string modId)
    {
        if (!_mods.TryGetValue(modId, out ModRuntime? m)) return (false, $"unknown Lua mod '{modId}'");

        string folder = m.Info.ModFolder;
        if (m.Running) Stop(modId);

        if (FindInitScripts(folder) is { Count: > 0 } scripts)
        {
            m.Info = new LuaModDescriptor(m.Info.ModId, folder, scripts);
        }

        (bool ok, string msg) = Start(m.Info.ModId);
        if (ok) _log($"[Meitou.Lua:{m.Info.ModId}] hot-reloaded");
        return (ok, msg);
    }

    /// <summary>
    /// Enables live <c>*.lua</c> hot-reloading for all discovered mods.
    /// </summary>
    public void EnableHotReload(TimeSpan? debounce = null)
    {
        _hotReloader?.Dispose();
        _hotReloader = new LuaHotReloader(debounce);
        foreach (ModRuntime m in _mods.Values)
        {
            _hotReloader.Watch(m.Info.ModId, m.Info.ModFolder);
        }
    }

    /// <summary>
    /// Disables live hot-reloading and disposes all file-system watchers.
    /// </summary>
    public void DisableHotReload()
    {
        _hotReloader?.Dispose();
        _hotReloader = null;
    }

    /// <summary>
    /// Reloads any watched mod whose <c>*.lua</c> files changed and settled past the debounce window.
    /// Returns the reloaded mod ids.
    /// </summary>
    public IReadOnlyList<string> PollHotReload()
    {
        if (_hotReloader is null) return [];

        List<string> ready = _hotReloader.DrainReady();
        if (ready.Count == 0) return [];

        List<string> reloaded = new(ready.Count);
        foreach (string modId in ready)
        {
            if (Reload(modId).Ok) reloaded.Add(modId);
        }

        return reloaded;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        DisableHotReload();
        StopAll();
    }

    /// <summary>
    /// Gives the mod a fresh sandbox and clears the state of its previous run.
    /// </summary>
    LuaSandbox ResetForStart(ModRuntime m)
    {
        LuaSandbox sandbox = new(m.Info.ModId, _limits, _seed++, s => _log($"[Meitou.Lua:{m.Info.ModId}] {s}"), _log);
        m.Sandbox = sandbox;
        m.Error = null;
        m.ActionsThisTick = 0;
        m.HudCallbacks.Clear();
        m.RequireCache.Clear();

        return sandbox;
    }

    /// <summary>
    /// Installs the KenshiLua API, the MyGUI shim and the scoped <c>require</c> into the mod's sandbox.
    /// </summary>
    void InstallApis(ModRuntime m, LuaSandbox sandbox)
    {
        KenshiLuaApi.Install(sandbox, m.Info.ModId, m.Info.ModFolder, _bridge, ActiveModNames, () => SpendAction(m), _log);
        MyGuiShim.Install(sandbox, "kl:" + m.Info.ModId, _hud, m.HudCallbacks, m.Info.ModFolder, () => _bridge.ViewSize);
        sandbox.Expose("", "require", a => ScopedRequire(m, a));
    }

    /// <summary>
    /// The ids of all discovered mods in order.
    /// </summary>
    IReadOnlyList<string> ActiveModNames() => _mods.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Takes one of the mod's world actions for this frame; false when it has used them all.
    /// </summary>
    static bool SpendAction(ModRuntime m)
    {
        if (m.ActionsThisTick >= MaxActionsPerTick) return false;

        m.ActionsThisTick++;
        return true;
    }

    /// <summary>
    /// Runs the mod's init scripts in order; returns the failure message after stopping the mod, or null when all ran.
    /// </summary>
    string? RunInitScripts(ModRuntime m, LuaSandbox sandbox, string modId)
    {
        foreach (string rel in m.Info.InitScripts)
        {
            string? full = LuaSandbox.SafePath(m.Info.ModFolder, rel);
            if (full is null || !File.Exists(full)) continue;

            string text;
            try
            {
                text = File.ReadAllText(full);
            }
            catch (Exception e)
            {
                m.Error = e.Message;
                Stop(modId);
                return $"Lua mod '{modId}': {e.Message}";
            }

            sandbox.Load(rel, text);
            if (!sandbox.Faulted && sandbox.LastError is null) continue;

            m.Error = sandbox.Faulted ? $"{sandbox.FaultReason}: {sandbox.LastError}" : sandbox.LastError;
            Stop(modId);
            return $"Lua mod '{modId}' failed in {rel}: {m.Error}";
        }

        return null;
    }

    #endregion

    #region Event dispatch

    /// <summary>
    /// Maximum cumulative Lua instructions across all mods in one frame.
    /// </summary>
    public const long GlobalTickBudget = 2_000_000;

    /// <summary>
    /// Measures the time between frames.
    /// </summary>
    readonly Stopwatch _frameClock = Stopwatch.StartNew();

    /// <summary>
    /// The clock reading of the previous frame in seconds; 0 before the first.
    /// </summary>
    double _lastFrameSec;

    /// <summary>
    /// What one frame delivers to every mod.
    /// </summary>
    sealed record FrameInput(double Dt, LuaWorldSnapshot Snap, string? SelectedName, IReadOnlyList<LuaGameEvent>? Events);

    /// <summary>
    /// Advances all running Lua mods by one UI frame and dispatches any narrow game events.
    /// </summary>
    public void Tick(IReadOnlyList<LuaGameEvent>? events = null)
    {
        PollHotReload();
        if (!HasRunningMods) return;

        FrameInput frame = BeginFrame(events);
        long used = 0;
        foreach (ModRuntime m in _mods.Values.OrderBy(x => x.Info.ModId, StringComparer.OrdinalIgnoreCase))
        {
            if (!m.Running || m.Sandbox is null) continue;

            m.ActionsThisTick = 0;
            if (used >= GlobalTickBudget) break;

            TickMod(m, frame, ref used);
        }
    }

    /// <summary>
    /// Forwards a key-down virtual key code to running Lua mods (never called while typing in a text box).
    /// </summary>
    public void NotifyKeyDown(int keyCode)
    {
        if (!HasRunningMods) return;

        foreach (ModRuntime m in _mods.Values.OrderBy(x => x.Info.ModId, StringComparer.OrdinalIgnoreCase))
        {
            if (!m.Running || m.Sandbox is null) continue;

            m.ActionsThisTick = 0;
            m.Sandbox.CallGlobal("__kl_dispatch", "onKeyDown", keyCode);
            if (m.Sandbox.Faulted) Stop(m.Info.ModId);
        }
    }

    /// <summary>
    /// Dispatches a click on a MyGUI button widget owned by a Lua mod.
    /// </summary>
    public void DispatchHudClick(string ownerKey, string buttonId)
    {
        if (!ownerKey.StartsWith("kl:", StringComparison.OrdinalIgnoreCase)) return;

        string modId = ownerKey[3..];
        if (!_mods.TryGetValue(modId, out ModRuntime? m) || !m.Running || m.Sandbox is null) return;
        if (!m.HudCallbacks.TryGetValue(buttonId, out DynValue? fn)) return;

        m.ActionsThisTick = 0;
        m.Sandbox.Call(fn);
        if (m.Sandbox.Faulted) Stop(m.Info.ModId);
    }

    /// <summary>
    /// Reads the frame time, the world scan and the selection that every mod is given this frame.
    /// </summary>
    FrameInput BeginFrame(IReadOnlyList<LuaGameEvent>? events)
    {
        double nowSec = _frameClock.Elapsed.TotalSeconds;
        double dt = _lastFrameSec > 0 ? Math.Clamp(nowSec - _lastFrameSec, 0.001, 0.1) : 0.016;
        _lastFrameSec = nowSec;

        LuaWorldSnapshot snap = _bridge.Scan;
        string? selName = _bridge.SelectedName;

        return new FrameInput(dt, snap, selName, events);
    }

    /// <summary>
    /// Delivers one frame to one mod: frame start, new buildings, selection, characters update, then game events; stops early when the mod stops or
    /// the frame budget is spent.
    /// </summary>
    void TickMod(ModRuntime m, FrameInput frame, ref long used)
    {
        if (!DispatchFrameStart(m, frame, ref used)) return;
        if (!DispatchCoroutines(m, frame, ref used)) return;
        if (!DispatchBuildings(m, frame.Snap, ref used)) return;
        if (!DispatchSelection(m, frame, ref used)) return;
        if (!DispatchCharsUpdate(m, ref used)) return;

        DispatchGameEvents(m, frame.Events, ref used);
    }

    /// <summary>
    /// Steps active <c>Meitou.spawn</c> coroutines for one frame; false when the rest of the mod's frame is skipped.
    /// </summary>
    bool DispatchCoroutines(ModRuntime m, FrameInput frame, ref long used)
    {
        LuaSandbox sandbox = m.Sandbox!;
        sandbox.CallGlobal("__kl_step_tasks", frame.Dt);
        used += sandbox.LastCallInstructions;
        if (StopIfFaulted(m)) return false;

        return CanContinue(m, used);
    }

    /// <summary>
    /// Whether the mod still runs and the frame budget has room.
    /// </summary>
    static bool CanContinue(ModRuntime m, long used) => m.Running && m.Sandbox is not null && used < GlobalTickBudget;

    /// <summary>
    /// Stops the mod when its sandbox faulted; true when it did.
    /// </summary>
    bool StopIfFaulted(ModRuntime m)
    {
        if (!m.Sandbox!.Faulted) return false;

        Stop(m.Info.ModId);
        return true;
    }

    /// <summary>
    /// Fires the per-frame start event; false when the rest of the mod's frame is skipped.
    /// </summary>
    bool DispatchFrameStart(ModRuntime m, FrameInput frame, ref long used)
    {
        LuaSandbox sandbox = m.Sandbox!;
        sandbox.CallGlobal("__kl_dispatch", "MyGUI::Gui::eventFrameStart", frame.Dt);
        used += sandbox.LastCallInstructions;
        if (StopIfFaulted(m)) return false;

        return CanContinue(m, used);
    }

    /// <summary>
    /// Fires the building-loaded event once per door building the mod has not seen; false when the rest of the mod's frame is skipped.
    /// </summary>
    bool DispatchBuildings(ModRuntime m, LuaWorldSnapshot snap, ref long used)
    {
        foreach (LuaDoorSnap d in snap.Doors)
        {
            if (!CanContinue(m, used)) break;
            if (!m.SeenBuildings.Add(d.Building)) continue;

            LuaSandbox sandbox = m.Sandbox!;
            DynValue wrapped = sandbox.CallGlobal("__kl_wrap_building", BuildingTable(sandbox, d));
            used += sandbox.LastCallInstructions;
            if (StopIfFaulted(m)) break;

            sandbox.CallGlobal("__kl_dispatch", "onBuildingLoaded", wrapped);
            used += sandbox.LastCallInstructions;
            if (StopIfFaulted(m)) break;
        }

        return CanContinue(m, used);
    }

    /// <summary>
    /// The Lua table describing a door building.
    /// </summary>
    static Table BuildingTable(LuaSandbox sandbox, LuaDoorSnap d)
    {
        Table bt = new(sandbox.Script);
        bt["id"] = d.Id;
        bt["building"] = d.Building;
        bt["name"] = d.Building;
        bt["owner"] = d.Owner;
        bt["is_player"] = d.IsPlayer;
        bt["x"] = d.X;
        bt["y"] = d.Y;
        bt["z"] = d.Z;
        bt["open_amount"] = d.OpenAmount;
        bt["motion"] = d.Motion;
        bt["locked"] = d.Locked;
        bt["broken"] = d.Broken;
        bt["is_gate"] = d.IsGate;

        return bt;
    }

    /// <summary>
    /// Fires the character-select event when the selection changed since the mod last saw it; false when the mod faulted and stopped.
    /// </summary>
    bool DispatchSelection(ModRuntime m, FrameInput frame, ref long used)
    {
        string? selName = frame.SelectedName;
        if (string.IsNullOrEmpty(selName) || string.Equals(selName, m.LastSelectedName, StringComparison.Ordinal)) return true;

        m.LastSelectedName = selName;
        LuaCharSnap? selChar = frame.Snap.Characters.FirstOrDefault(c => string.Equals(c.Name, selName, StringComparison.OrdinalIgnoreCase));
        if (selChar is null) return true;

        LuaSandbox sandbox = m.Sandbox!;
        DynValue wrapped = sandbox.CallGlobal("__kl_wrap_char", CharacterTable(sandbox, selChar));
        used += sandbox.LastCallInstructions;
        if (StopIfFaulted(m)) return false;

        sandbox.CallGlobal("__kl_dispatch", "onCharacterSelect", wrapped);
        used += sandbox.LastCallInstructions;

        return !StopIfFaulted(m);
    }

    /// <summary>
    /// The Lua table describing a character.
    /// </summary>
    static Table CharacterTable(LuaSandbox sandbox, LuaCharSnap selChar)
    {
        Table ct = new(sandbox.Script);
        ct["slot"] = selChar.Slot;
        ct["gen"] = selChar.Gen;
        ct["name"] = selChar.Name;
        ct["faction"] = selChar.Faction;
        ct["is_player"] = selChar.IsPlayer;
        ct["x"] = selChar.X;
        ct["y"] = selChar.Y;
        ct["z"] = selChar.Z;
        ct["hp"] = selChar.Hp;
        ct["down"] = selChar.Down;
        ct["dead"] = selChar.Dead;
        ct["in_combat"] = selChar.InCombat;
        ct["squad_id"] = selChar.SquadId;

        return ct;
    }

    /// <summary>
    /// Fires the characters-update events; false when the mod faulted and stopped.
    /// </summary>
    bool DispatchCharsUpdate(ModRuntime m, ref long used)
    {
        LuaSandbox sandbox = m.Sandbox!;
        DynValue world = sandbox.CallGlobal("getGameWorld");
        used += sandbox.LastCallInstructions;
        if (StopIfFaulted(m)) return false;

        sandbox.CallGlobal("__kl_dispatch", "onCharsUpdate", world);
        used += sandbox.LastCallInstructions;
        if (StopIfFaulted(m)) return false;

        sandbox.CallGlobal("__kl_dispatch", "GameWorld::charsUpdate", world);
        used += sandbox.LastCallInstructions;

        return !StopIfFaulted(m);
    }

    /// <summary>
    /// Fires the narrow game events (hit, hour, day, character down) the KenshiLua API knows.
    /// </summary>
    void DispatchGameEvents(ModRuntime m, IReadOnlyList<LuaGameEvent>? events, ref long used)
    {
        if (events is null || events.Count == 0) return;

        foreach (LuaGameEvent ev in events)
        {
            if (!CanContinue(m, used)) break;

            string klName = KenshiEventName(ev.Name);
            if (klName.Length == 0) continue;

            LuaSandbox sandbox = m.Sandbox!;
            sandbox.CallGlobal("__kl_dispatch", klName, PayloadTable(sandbox, ev));
            used += sandbox.LastCallInstructions;
            if (StopIfFaulted(m)) break;
        }
    }

    /// <summary>
    /// The KenshiLua event name of a game event; empty when KenshiLua has none for it.
    /// </summary>
    static string KenshiEventName(string name) => name switch
    {
        "hit" => "onCombatHit",
        "hour" => "onHour",
        "day" => "onDay",
        "character_down" => "onCharacterDown",
        _ => "",
    };

    /// <summary>
    /// The Lua table holding an event's payload.
    /// </summary>
    static Table PayloadTable(LuaSandbox sandbox, LuaGameEvent ev)
    {
        Table tbl = new(sandbox.Script);
        foreach (KeyValuePair<string, object?> kv in ev.Payload)
        {
            tbl[kv.Key] = PayloadValue(kv.Value);
        }

        return tbl;
    }

    /// <summary>
    /// Converts one payload value to Lua; unknown types become their text.
    /// </summary>
    static DynValue PayloadValue(object? value) => value switch
    {
        null => DynValue.Nil,
        bool b => DynValue.NewBoolean(b),
        int i => DynValue.NewNumber(i),
        long l => DynValue.NewNumber(l),
        double d => DynValue.NewNumber(d),
        float f => DynValue.NewNumber(f),
        string s => DynValue.NewString(s),
        _ => DynValue.NewString(value.ToString() ?? ""),
    };

    #endregion

    #region Persistence

    /// <summary>
    /// Maximum serialized GameData JSON size per mod (256 KB).
    /// </summary>
    public const int MaxSaveBytes = 256 * 1024;

    /// <summary>
    /// Lua run on start to hand the saved GameData to the mod's load events.
    /// </summary>
    const string LoadScript = "local gd = __kl_make_gamedata(__kl_gd_raw); "
        + "__kl_dispatch('onPlayerLoadFromSerialise', getGameWorld().player, gd); "
        + "__kl_dispatch('PlayerInterface::loadFromSerialise', getGameWorld().player, gd)";

    /// <summary>
    /// Lua run on stop to collect the mod's GameData as JSON from its serialise events.
    /// </summary>
    const string SerialiseScript = "local gd = __kl_make_gamedata('{}'); "
        + "__kl_dispatch('onPlayerSerialise', getGameWorld().player, gd); "
        + "__kl_dispatch('PlayerInterface::serialise', getGameWorld().player, gd); "
        + "return json.encode(gd:toTable())";

    /// <summary>
    /// The folder the mods' saved GameData lives in; null when saving is off.
    /// </summary>
    readonly string? _stateDir;

    /// <summary>
    /// The path of a mod's saved GameData file; null when saving is off.
    /// </summary>
    string? ModSavePath(string modId) =>
        string.IsNullOrEmpty(_stateDir) ? null : LuaSandbox.SafePath(_stateDir, "kl_" + modId.ToLowerInvariant() + ".json");

    /// <summary>
    /// Hands the mod its saved GameData (or an empty one) through the load events.
    /// </summary>
    void FirePlayerLoad(ModRuntime m)
    {
        if (m.Sandbox is null || m.Sandbox.Faulted) return;

        string raw = ReadSavedState(ModSavePath(m.Info.ModId));
        m.Sandbox.Script.Globals["__kl_gd_raw"] = DynValue.NewString(raw);
        m.Sandbox.Eval(LoadScript);
    }

    /// <summary>
    /// The saved GameData JSON at <paramref name="path"/>; <c>{}</c> when there is none, it is empty, too big or unreadable.
    /// </summary>
    static string ReadSavedState(string? path)
    {
        string raw = "{}";
        if (path is null || !File.Exists(path)) return raw;

        try
        {
            FileInfo fi = new(path);
            if (fi.Length > 0 && fi.Length <= MaxSaveBytes) raw = File.ReadAllText(path);
        }
        catch (Exception)
        {
            // An unreadable save starts the mod with an empty GameData.
        }

        return raw;
    }

    /// <summary>
    /// Collects the mod's GameData through the serialise events and writes it to the mod's save file.
    /// </summary>
    void FirePlayerSerialise(ModRuntime m)
    {
        if (m.Sandbox is null || m.Sandbox.Faulted) return;

        string? path = ModSavePath(m.Info.ModId);
        if (path is null) return;

        try
        {
            DynValue res = m.Sandbox.Eval(SerialiseScript);
            if (res.Type == DataType.String && res.String.Length > 2 && res.String.Length <= MaxSaveBytes)
            {
                Directory.CreateDirectory(_stateDir!);
                File.WriteAllText(path, res.String);
            }
        }
        catch (Exception e)
        {
            _log($"[Meitou.Lua:{m.Info.ModId}] save failed: {e.Message}");
        }
    }

    #endregion

    #region Require

    /// <summary>
    /// The mod's <c>require(name)</c>: loads a module from inside the mod's own folder once and caches its result.
    /// </summary>
    DynValue ScopedRequire(ModRuntime m, CallbackArguments a)
    {
        if (m.Sandbox is null || a.Count < 1 || a[0].Type != DataType.String)
        {
            throw new ScriptRuntimeException("require(name) expects a module name string");
        }

        string modName = a[0].String.Trim();
        if (m.RequireCache.TryGetValue(modName, out DynValue? cached)) return cached;

        string full = ResolveModule(m, modName);
        string code = File.ReadAllText(full);
        DynValue res = m.Sandbox.Script.DoString(code, null, modName);
        if (res.IsNil()) res = DynValue.True;
        m.RequireCache[modName] = res;

        return res;
    }

    /// <summary>
    /// The path of a module inside the mod's folder (under <c>scripts</c>, <c>lib</c>, then the root); throws when it is not there.
    /// </summary>
    static string ResolveModule(ModRuntime m, string modName)
    {
        string relPath = modName.Replace('.', '/') + ".lua";
        string? full = LuaSandbox.SafePath(m.Info.ModFolder, "scripts/" + relPath)
            ?? LuaSandbox.SafePath(m.Info.ModFolder, "lib/" + relPath)
            ?? LuaSandbox.SafePath(m.Info.ModFolder, relPath);
        if (full is null || !File.Exists(full))
        {
            throw new ScriptRuntimeException($"require: module '{modName}' not found inside mod '{m.Info.ModId}'");
        }

        return full;
    }

    #endregion
}
