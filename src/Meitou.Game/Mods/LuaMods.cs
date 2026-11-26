using Meitou.Content;
using Meitou.Data;
using Meitou.Game;
using Meitou.Game.Host;
using Meitou.Lua;
using Meitou.Simulation;

namespace Meitou.Modding;

/// <summary>
/// Hosts built-in KenshiLua-compatible mods (<c>scripts/init/*.lua</c>) discovered from the enabled mod list.
/// Remains completely idle (null) when no enabled mod contains Lua scripts, leaving vanilla determinism untouched.
/// </summary>
sealed class LuaMods
{
    readonly SimulationLuaBridge _bridge;
    readonly LuaHudState _hud = new();
    readonly KenshiLuaHost _host;
    World? _lastWorld;

    LuaMods(SimulationLuaBridge bridge, KenshiLuaHost host)
    {
        _bridge = bridge;
        _host = host;
    }

    /// <summary>Exposes the bounded HUD overlay state for <c>MyGUI.*</c> widgets.</summary>
    public LuaHudState Hud => _hud;

    /// <summary>Whether at least one KenshiLua mod is currently running.</summary>
    public bool HasRunningMods => _host.HasRunningMods;

    /// <summary>
    /// Discovers and starts enabled mods that contain <c>scripts/init/*.lua</c>.
    /// Returns null when no enabled mod has Lua scripts so vanilla runs pay zero cost.
    /// </summary>
    public static LuaMods? TryStart(GameInstall install, GameDatabase db, Data.Gameplay.FactionRelations? relations)
    {
        Dictionary<string, ModInfo> onDisk;
        try
        {
            onDisk = ModList.Discover(install).ToDictionary(m => m.Name, StringComparer.Ordinal);
        }
        catch (Exception)
        {
            return null;
        }

        List<(string ModId, string ModFolder)> enabled = [];
        foreach (string name in ModList.ReadEnabled(install))
        {
            if (onDisk.TryGetValue(name, out ModInfo? info))
            {
                enabled.Add((info.Name, info.Folder));
            }
        }
        if (enabled.Count == 0) return null;

        SimulationLuaBridge bridge = new(db, relations);
        LuaHudState hud = new();
        KenshiLuaHost luaHost = new(bridge, hud, log: Console.WriteLine);
        if (luaHost.DiscoverEnabledMods(enabled) == 0) return null;

        IReadOnlyList<string> started = luaHost.StartAll();
        if (started.Count == 0) return null;

        Console.WriteLine($"lua       started {started.Count} KenshiLua mod(s): {string.Join(", ", started)}");
        return new LuaMods(bridge, luaHost);
    }

    /// <summary>
    /// Steps the simulation bridge (when a world is active) and dispatches per-frame Lua callbacks.
    /// </summary>
    public void UpdateFrame(GameHost host)
    {
        if (!_host.HasRunningMods) return;
        if (host.Session?.World is { } world)
        {
            if (!ReferenceEquals(_lastWorld, world)) _lastWorld = world;
            _bridge.StepWorld(world);
        }
        if (host.Player is { } ui)
        {
            string? selName = null;
            IReadOnlyList<CharacterId> selected = ui.SelectedIds();
            if (selected.Count > 0 && host.Session?.World is { } w
                && w.Characters.TryResolveNext(selected[0], out int slot))
            {
                selName = w.Characters.Cold(slot)?.Name;
            }
            _bridge.SetSelectedName(selName);
        }
        _host.Tick();
    }

    /// <summary>Forwards a non-typing key-down virtual key code to running Lua mods.</summary>
    public void NotifyKeyDown(int vk) => _host.NotifyKeyDown(vk);

    /// <summary>Stops all running Lua mods and clears all HUD overlay widgets.</summary>
    public void StopAll() => _host.StopAll();
}
