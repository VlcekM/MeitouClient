using System.Diagnostics;
using System.Globalization;
using MoonSharp.Interpreter;

namespace Meitou.Lua;

/// <summary>Descriptor for a discovered KenshiLua mod containing <c>scripts/init/*.lua</c>.</summary>
public sealed record LuaModDescriptor(string ModId, string ModFolder, IReadOnlyList<string> InitScripts);

/// <summary>
/// Built-in KenshiLua compatibility runtime for Meitou.
/// Loads enabled mods that contain <c>scripts/init/*.lua</c> into isolated, instruction-budgeted
/// MoonSharp sandboxes, dispatches KenshiLua events each frame, and stays completely idle when
/// no enabled mods contain Lua scripts.
/// </summary>
public sealed class KenshiLuaHost
{
    /// <summary>Maximum cumulative Lua instructions across all mods in one frame.</summary>
    public const long GlobalTickBudget = 2_000_000;

    /// <summary>Maximum world actions one mod may enqueue in a single frame.</summary>
    public const int MaxActionsPerTick = 16;

    /// <summary>Maximum serialized GameData JSON size per mod (256 KB).</summary>
    public const int MaxSaveBytes = 256 * 1024;

    readonly ILuaWorldBridge _bridge;
    readonly LuaHudState? _hud;
    readonly LuaLimits _limits;
    readonly Action<string> _log;
    readonly string? _stateDir;
    readonly Dictionary<string, ModRuntime> _mods = new(StringComparer.OrdinalIgnoreCase);
    readonly Stopwatch _frameClock = Stopwatch.StartNew();
    double _lastFrameSec;
    ulong _seed = 7001;

    sealed class ModRuntime(LuaModDescriptor info)
    {
        public LuaModDescriptor Info { get; } = info;
        public LuaSandbox? Sandbox;
        public bool Running;
        public string? Error;
        public int ActionsThisTick;
        public string? LastSelectedName;
        public HashSet<string> SeenBuildings { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DynValue> HudCallbacks { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DynValue> RequireCache { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether at least one KenshiLua mod is currently running.</summary>
    public bool HasRunningMods => _mods.Values.Any(m => m.Running);

    /// <summary>Creates a new KenshiLua runtime host.</summary>
    public KenshiLuaHost(ILuaWorldBridge bridge, LuaHudState? hud = null, LuaLimits? limits = null, Action<string>? log = null, string? stateDir = null)
    {
        _bridge = bridge;
        _hud = hud;
        _limits = limits ?? new LuaLimits();
        _log = log ?? Console.WriteLine;
        _stateDir = stateDir;
    }

    /// <summary>
    /// Scans the given enabled mod folders for <c>scripts/init/*.lua</c> (skipping KenshiLua itself and reparse points).
    /// Returns the number of discovered Lua mods.
    /// </summary>
    public int DiscoverEnabledMods(IEnumerable<(string ModId, string ModFolder)> enabledMods)
    {
        int found = 0;
        foreach ((string modId, string modFolder) in enabledMods)
        {
            if (string.IsNullOrWhiteSpace(modId) || string.IsNullOrWhiteSpace(modFolder)) continue;
            if (modId.Equals("KenshiLua", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Directory.Exists(modFolder)) continue;
            try
            {
                if ((File.GetAttributes(modFolder) & FileAttributes.ReparsePoint) != 0) continue;
                string initDir = Path.Combine(modFolder, "scripts", "init");
                if (!Directory.Exists(initDir) || (File.GetAttributes(initDir) & FileAttributes.ReparsePoint) != 0) continue;
                List<string> scripts = Directory.GetFiles(initDir, "*.lua")
                    .Where(f => (File.GetAttributes(f) & FileAttributes.ReparsePoint) == 0)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .Select(f => Path.GetRelativePath(modFolder, f).Replace('\\', '/'))
                    .ToList();
                if (scripts.Count == 0) continue;
                if (!_mods.ContainsKey(modId))
                {
                    _mods[modId] = new ModRuntime(new LuaModDescriptor(modId, modFolder, scripts));
                }
                found++;
            }
            catch (Exception) { }
        }
        return found;
    }

    /// <summary>Returns all discovered Lua mods and their runtime status.</summary>
    public IReadOnlyList<(LuaModDescriptor Info, bool Running, string? Error)> List() =>
        _mods.Values.OrderBy(m => m.Info.ModId, StringComparer.OrdinalIgnoreCase)
            .Select(m => (m.Info, m.Running, m.Error)).ToList();

    /// <summary>Starts all discovered enabled Lua mods.</summary>
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

    /// <summary>Starts a single discovered Lua mod by id.</summary>
    public (bool Ok, string Message) Start(string modId)
    {
        if (!_mods.TryGetValue(modId, out ModRuntime? m)) return (false, $"unknown Lua mod '{modId}'");
        if (m.Running) return (true, $"Lua mod '{m.Info.ModId}' is already running");

        LuaSandbox sandbox = new(m.Info.ModId, _limits, _seed++, s => _log($"[Meitou.Lua:{m.Info.ModId}] {s}"), _log);
        m.Sandbox = sandbox;
        m.Error = null;
        m.ActionsThisTick = 0;
        m.HudCallbacks.Clear();
        m.RequireCache.Clear();

        IReadOnlyList<string> ActiveModNames() => _mods.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        bool SpendAction()
        {
            if (m.ActionsThisTick >= MaxActionsPerTick) return false;
            m.ActionsThisTick++;
            return true;
        }

        KenshiLuaApi.Install(sandbox, m.Info.ModId, m.Info.ModFolder, _bridge, ActiveModNames, SpendAction, _log);
        MyGuiShim.Install(sandbox, "kl:" + m.Info.ModId, _hud, m.HudCallbacks, m.Info.ModFolder, () => _bridge.ViewSize);
        sandbox.Expose("", "require", a => ScopedRequire(m, a));

        foreach (string rel in m.Info.InitScripts)
        {
            string? full = LuaSandbox.SafePath(m.Info.ModFolder, rel);
            if (full is null || !File.Exists(full)) continue;
            string text;
            try { text = File.ReadAllText(full); }
            catch (Exception e) { m.Error = e.Message; Stop(modId); return (false, $"Lua mod '{modId}': {e.Message}"); }
            sandbox.Load(rel, text);
            if (sandbox.Faulted || sandbox.LastError is not null)
            {
                m.Error = sandbox.Faulted ? $"{sandbox.FaultReason}: {sandbox.LastError}" : sandbox.LastError;
                Stop(modId);
                return (false, $"Lua mod '{modId}' failed in {rel}: {m.Error}");
            }
        }

        m.Running = true;
        m.SeenBuildings.Clear();
        m.LastSelectedName = null;
        FirePlayerLoad(m);
        return (true, $"started Lua mod '{m.Info.ModId}' ({m.Info.InitScripts.Count} init script(s))");
    }

    /// <summary>Stops a running Lua mod, persists its GameData state, and removes all its HUD widgets.</summary>
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

    /// <summary>Stops all running Lua mods.</summary>
    public void StopAll()
    {
        foreach (string id in _mods.Keys.ToList()) Stop(id);
    }

    /// <summary>Advances all running Lua mods by one UI frame and dispatches any narrow game events.</summary>
    public void Tick(IReadOnlyList<LuaGameEvent>? events = null)
    {
        if (!HasRunningMods) return;

        double nowSec = _frameClock.Elapsed.TotalSeconds;
        double dt = _lastFrameSec > 0 ? Math.Clamp(nowSec - _lastFrameSec, 0.001, 0.1) : 0.016;
        _lastFrameSec = nowSec;
        string dtStr = dt.ToString("0.#####", CultureInfo.InvariantCulture);

        long used = 0;
        LuaWorldSnapshot snap = _bridge.Scan;
        string? selName = _bridge.SelectedName;

        foreach (ModRuntime m in _mods.Values.OrderBy(x => x.Info.ModId, StringComparer.OrdinalIgnoreCase))
        {
            if (!m.Running || m.Sandbox is null) continue;
            m.ActionsThisTick = 0;
            if (used >= GlobalTickBudget) break;

            m.Sandbox.Eval($"__kl_dispatch('MyGUI::Gui::eventFrameStart', {dtStr})");
            used += m.Sandbox.LastCallInstructions;
            if (m.Sandbox.Faulted) { Stop(m.Info.ModId); continue; }
            if (!m.Running || m.Sandbox is null || used >= GlobalTickBudget) continue;

            foreach (LuaDoorSnap d in snap.Doors)
            {
                if (!m.Running || m.Sandbox is null || used >= GlobalTickBudget) break;
                if (!m.SeenBuildings.Add(d.Building)) continue;
                Table bt = new(m.Sandbox.Script);
                bt["id"] = d.Id; bt["building"] = d.Building; bt["name"] = d.Building;
                bt["owner"] = d.Owner; bt["is_player"] = d.IsPlayer;
                bt["x"] = d.X; bt["y"] = d.Y; bt["z"] = d.Z;
                bt["open_amount"] = d.OpenAmount; bt["motion"] = d.Motion;
                bt["locked"] = d.Locked; bt["broken"] = d.Broken; bt["is_gate"] = d.IsGate;
                m.Sandbox.Script.Globals["__kl_bld"] = DynValue.NewTable(bt);
                m.Sandbox.Eval("__kl_dispatch('onBuildingLoaded', __kl_wrap_building(__kl_bld))");
                used += m.Sandbox.LastCallInstructions;
                if (m.Sandbox.Faulted) { Stop(m.Info.ModId); break; }
            }
            if (!m.Running || m.Sandbox is null || used >= GlobalTickBudget) continue;

            if (!string.IsNullOrEmpty(selName) && !string.Equals(selName, m.LastSelectedName, StringComparison.Ordinal))
            {
                m.LastSelectedName = selName;
                LuaCharSnap? selChar = snap.Characters.FirstOrDefault(c => string.Equals(c.Name, selName, StringComparison.OrdinalIgnoreCase));
                if (selChar is not null)
                {
                    Table ct = new(m.Sandbox.Script);
                    ct["slot"] = selChar.Slot; ct["gen"] = selChar.Gen; ct["name"] = selChar.Name;
                    ct["faction"] = selChar.Faction; ct["is_player"] = selChar.IsPlayer;
                    ct["x"] = selChar.X; ct["y"] = selChar.Y; ct["z"] = selChar.Z;
                    ct["hp"] = selChar.Hp; ct["down"] = selChar.Down; ct["dead"] = selChar.Dead;
                    ct["in_combat"] = selChar.InCombat; ct["squad_id"] = selChar.SquadId;
                    m.Sandbox.Script.Globals["__kl_sel"] = DynValue.NewTable(ct);
                    m.Sandbox.Eval("__kl_dispatch('onCharacterSelect', __kl_wrap_char(__kl_sel))");
                    used += m.Sandbox.LastCallInstructions;
                    if (m.Sandbox.Faulted) { Stop(m.Info.ModId); continue; }
                }
            }

            m.Sandbox.Eval("__kl_dispatch('onCharsUpdate', getGameWorld()); __kl_dispatch('GameWorld::charsUpdate', getGameWorld())");
            used += m.Sandbox.LastCallInstructions;
            if (m.Sandbox.Faulted) { Stop(m.Info.ModId); continue; }

            if (events is null || events.Count == 0) continue;
            foreach (LuaGameEvent ev in events)
            {
                if (!m.Running || m.Sandbox is null || used >= GlobalTickBudget) break;
                string klName = ev.Name switch
                {
                    "hit" => "onCombatHit",
                    "hour" => "onHour",
                    "day" => "onDay",
                    "character_down" => "onCharacterDown",
                    _ => "",
                };
                if (klName.Length == 0) continue;
                Table tbl = new(m.Sandbox.Script);
                foreach (KeyValuePair<string, object?> kv in ev.Payload)
                {
                    tbl[kv.Key] = kv.Value switch
                    {
                        null => DynValue.Nil,
                        bool b => DynValue.NewBoolean(b),
                        int i => DynValue.NewNumber(i),
                        long l => DynValue.NewNumber(l),
                        double d => DynValue.NewNumber(d),
                        float f => DynValue.NewNumber(f),
                        string s => DynValue.NewString(s),
                        _ => DynValue.NewString(kv.Value.ToString() ?? ""),
                    };
                }
                m.Sandbox.Script.Globals["__kl_ev"] = DynValue.NewTable(tbl);
                m.Sandbox.Eval($"__kl_dispatch('{klName}', __kl_ev)");
                used += m.Sandbox.LastCallInstructions;
                if (m.Sandbox.Faulted) { Stop(m.Info.ModId); break; }
            }
        }
    }

    /// <summary>Forwards a key-down virtual key code to running Lua mods (never called while typing in a text box).</summary>
    public void NotifyKeyDown(int keyCode)
    {
        if (!HasRunningMods) return;
        foreach (ModRuntime m in _mods.Values.OrderBy(x => x.Info.ModId, StringComparer.OrdinalIgnoreCase))
        {
            if (!m.Running || m.Sandbox is null) continue;
            m.ActionsThisTick = 0;
            m.Sandbox.Script.Globals["__kl_key"] = DynValue.NewNumber(keyCode);
            m.Sandbox.Eval("__kl_dispatch('onKeyDown', __kl_key)");
            if (m.Sandbox.Faulted) Stop(m.Info.ModId);
        }
    }

    /// <summary>Dispatches a click on a MyGUI button widget owned by a Lua mod.</summary>
    public void DispatchHudClick(string ownerKey, string buttonId)
    {
        if (!ownerKey.StartsWith("kl:", StringComparison.OrdinalIgnoreCase)) return;
        string modId = ownerKey[3..];
        if (!_mods.TryGetValue(modId, out ModRuntime? m) || !m.Running || m.Sandbox is null) return;
        if (!m.HudCallbacks.TryGetValue(buttonId, out DynValue? fn)) return;
        m.ActionsThisTick = 0;
        m.Sandbox.Script.Globals["__kl_btn_fn"] = fn;
        m.Sandbox.Eval("__kl_btn_fn()");
        if (m.Sandbox.Faulted) Stop(m.Info.ModId);
    }

    string? ModSavePath(string modId) =>
        string.IsNullOrEmpty(_stateDir) ? null : LuaSandbox.SafePath(_stateDir, "kl_" + modId.ToLowerInvariant() + ".json");

    void FirePlayerLoad(ModRuntime m)
    {
        if (m.Sandbox is null || m.Sandbox.Faulted) return;
        string raw = "{}";
        string? path = ModSavePath(m.Info.ModId);
        if (path is not null && File.Exists(path))
        {
            try
            {
                FileInfo fi = new(path);
                if (fi.Length > 0 && fi.Length <= MaxSaveBytes) raw = File.ReadAllText(path);
            }
            catch (Exception) { }
        }
        m.Sandbox.Script.Globals["__kl_gd_raw"] = DynValue.NewString(raw);
        m.Sandbox.Eval("local gd = __kl_make_gamedata(__kl_gd_raw); __kl_dispatch('onPlayerLoadFromSerialise', getGameWorld().player, gd); __kl_dispatch('PlayerInterface::loadFromSerialise', getGameWorld().player, gd)");
    }

    void FirePlayerSerialise(ModRuntime m)
    {
        if (m.Sandbox is null || m.Sandbox.Faulted) return;
        string? path = ModSavePath(m.Info.ModId);
        if (path is null) return;
        try
        {
            DynValue res = m.Sandbox.Eval("local gd = __kl_make_gamedata('{}'); __kl_dispatch('onPlayerSerialise', getGameWorld().player, gd); __kl_dispatch('PlayerInterface::serialise', getGameWorld().player, gd); return json.encode(gd:toTable())");
            if (res.Type == DataType.String && res.String.Length > 2 && res.String.Length <= MaxSaveBytes)
            {
                Directory.CreateDirectory(_stateDir!);
                File.WriteAllText(path, res.String);
            }
        }
        catch (Exception e) { _log($"[Meitou.Lua:{m.Info.ModId}] save failed: {e.Message}"); }
    }

    DynValue ScopedRequire(ModRuntime m, CallbackArguments a)
    {
        if (m.Sandbox is null || a.Count < 1 || a[0].Type != DataType.String)
        {
            throw new ScriptRuntimeException("require(name) expects a module name string");
        }
        string modName = a[0].String.Trim();
        if (m.RequireCache.TryGetValue(modName, out DynValue? cached)) return cached;
        string relPath = modName.Replace('.', '/') + ".lua";
        string? full = LuaSandbox.SafePath(m.Info.ModFolder, "scripts/" + relPath)
            ?? LuaSandbox.SafePath(m.Info.ModFolder, "lib/" + relPath)
            ?? LuaSandbox.SafePath(m.Info.ModFolder, relPath);
        if (full is null || !File.Exists(full))
        {
            throw new ScriptRuntimeException($"require: module '{modName}' not found inside mod '{m.Info.ModId}'");
        }
        string code = File.ReadAllText(full);
        DynValue res = m.Sandbox.Script.DoString(code, null, modName);
        if (res.IsNil()) res = DynValue.True;
        m.RequireCache[modName] = res;
        return res;
    }
}
