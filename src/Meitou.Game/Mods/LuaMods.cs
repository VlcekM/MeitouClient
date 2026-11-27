using System.Numerics;
using Meitou.Content;
using Meitou.Data;
using Meitou.Game;
using Meitou.Game.Host;
using Meitou.Game.Host.Input;
using Meitou.Game.Rendering.Frame;
using Meitou.Lua;
using Meitou.Simulation;
using Meitou.Simulation.Combat;

namespace Meitou.Modding;

/// <summary>
/// Hosts built-in KenshiLua-compatible mods (<c>scripts/init/*.lua</c>) discovered from the enabled mod list.
/// Remains completely idle (null) when no enabled mod contains Lua scripts, leaving vanilla determinism untouched.
/// </summary>
sealed class LuaMods
{
    /// <summary>
    /// The most combat hit events forwarded to Lua in a single frame.
    /// </summary>
    const int MaxHitsPerFrame = 64;

    /// <summary>
    /// The bridge the Lua mods read the simulation through and queue their actions on.
    /// </summary>
    readonly SimulationLuaBridge _bridge;

    /// <summary>
    /// The HUD state the Lua mods' widgets live in.
    /// </summary>
    readonly LuaHudState _hud;

    /// <summary>
    /// The MyGUI overlay that shows <see cref="_hud"/>.
    /// </summary>
    readonly LuaHudOverlay _overlay;

    /// <summary>
    /// The KenshiLua host running the mods.
    /// </summary>
    readonly KenshiLuaHost _host;

    /// <summary>
    /// Combat hit events collected since the last frame tick, cleared after dispatch.
    /// </summary>
    readonly List<LuaGameEvent> _pendingEvents = [];

    /// <summary>
    /// The world the bridge last stepped.
    /// </summary>
    World? _lastWorld;

    /// <summary>
    /// Wraps the started <paramref name="host"/> with its bridge, HUD state and overlay.
    /// </summary>
    LuaMods(SimulationLuaBridge bridge, LuaHudState hud, KenshiLuaHost host)
    {
        _bridge = bridge;
        _hud = hud;
        _host = host;
        _overlay = new LuaHudOverlay(hud, host.DispatchHudClick);
    }

    /// <summary>
    /// Exposes the bounded HUD overlay state for <c>MyGUI.*</c> widgets.
    /// </summary>
    public LuaHudState Hud => _hud;

    /// <summary>
    /// Whether at least one KenshiLua mod is currently running.
    /// </summary>
    public bool HasRunningMods => _host.HasRunningMods;

    /// <summary>
    /// Discovers and starts enabled mods that contain <c>scripts/init/*.lua</c>.
    /// Returns null when no enabled mod has Lua scripts so vanilla runs pay zero cost.
    /// </summary>
    public static LuaMods? TryStart(GameInstall install, GameDatabase db, Data.Gameplay.FactionRelations? relations)
    {
        Dictionary<string, ModInfo>? onDisk = TryDiscover(install);
        if (onDisk is null) return null;

        List<(string ModId, string ModFolder)> enabled = EnabledMods(install, onDisk);
        if (enabled.Count == 0) return null;

        SimulationLuaBridge bridge = new(db, relations);
        LuaHudState hud = new();
        KenshiLuaHost luaHost = new(bridge, hud, log: Console.WriteLine);
        if (luaHost.DiscoverEnabledMods(enabled) == 0) return null;

        IReadOnlyList<string> started = luaHost.StartAll();
        if (started.Count == 0) return null;

        luaHost.EnableHotReload();
        Console.WriteLine($"lua       started {started.Count} KenshiLua mod(s): {string.Join(", ", started)}");
        return new LuaMods(bridge, hud, luaHost);
    }

    /// <summary>
    /// Connects the Lua HUD overlay and non-typing key forwarding to <paramref name="ui"/>.
    /// </summary>
    public void AttachUi(PlayerInterface ui)
    {
        _overlay.EnsureRegistered(ui.FaithfulScreens);
        ui.LuaHudBind = b => _overlay.Bind(b, ui.Width, ui.Height);
        ui.LuaHudEvent = _overlay.HandleEvent;
        ui.LuaKeyDown = ForwardKeyDown;
    }

    /// <summary>
    /// Queues resolved combat hits from the current frame so <see cref="UpdateFrame"/> dispatches them as <c>onCombatHit</c>.
    /// </summary>
    public void RecordCombatHits(World world, IReadOnlyList<CombatHit> hits)
    {
        if (!_host.HasRunningMods || hits.Count == 0) return;

        int take = Math.Min(hits.Count, MaxHitsPerFrame - _pendingEvents.Count);
        for (int i = 0; i < take; i++)
        {
            _pendingEvents.Add(BuildCombatHitEvent(world, hits[i]));
        }
    }

    /// <summary>
    /// Steps the simulation bridge (when a world is active) and dispatches per-frame Lua callbacks.
    /// </summary>
    public void UpdateFrame(GameHost host)
    {
        if (!_host.HasRunningMods) return;

        StepWorld(host);
        IReadOnlyList<LuaGameEvent>? events = _pendingEvents.Count > 0 ? _pendingEvents : null;

        if (host.Player is not { } ui)
        {
            _host.Tick(events);
            _pendingEvents.Clear();
            return;
        }

        _bridge.SetViewSize(ui.Width, ui.Height);
        if (host.Camera is { } cam) _bridge.SetCamera(CaptureCamera(cam, ui.Width, ui.Height));
        _bridge.SetSelectedName(SelectedName(ui, host));
        _host.Tick(events);
        _pendingEvents.Clear();
        _overlay.SyncOpen(ui.FaithfulScreens);
    }

    /// <summary>
    /// Captures a <see cref="LuaCameraSnap"/> from the active <see cref="WorldCamera"/> and viewport dimensions.
    /// </summary>
    internal static LuaCameraSnap CaptureCamera(WorldCamera cam, int width, int height)
    {
        Vector3 eye = cam.Eye;
        Vector3 fwd = cam.Target - eye;
        float len = fwd.Length();
        fwd = len > 1e-5f ? fwd / len : new Vector3(0, 0, -1);

        double yaw = ((Math.Atan2(fwd.X, fwd.Z) * 180.0 / Math.PI) + 360.0) % 360.0;
        double pitch = Math.Asin(Math.Clamp((double)fwd.Y, -1.0, 1.0)) * 180.0 / Math.PI;
        double fovDeg = float.IsFinite(cam.FieldOfView) && cam.FieldOfView > 0.05f
            ? cam.FieldOfView * 180.0 / Math.PI
            : 50.0;
        double aspect = width > 0 && height > 0 ? (double)width / height : (16.0 / 9.0);

        return new LuaCameraSnap(eye.X, eye.Y, eye.Z, fwd.X, fwd.Y, fwd.Z, yaw, pitch, fovDeg, aspect);
    }

    /// <summary>
    /// Builds the read-only <see cref="LuaGameEvent"/> payload for one resolved <see cref="CombatHit"/>.
    /// </summary>
    internal static LuaGameEvent BuildCombatHitEvent(World? world, in CombatHit hit)
    {
        double cut = hit.Damage.Cut;
        double blunt = hit.Damage.Blunt;
        double pierce = hit.Damage.Pierce;

        Dictionary<string, object?> payload = new(20)
        {
            ["tick"] = hit.Tick,
            ["source"] = hit.Source.ToString().ToLowerInvariant(),
            ["attackerSlot"] = hit.Attacker.Slot,
            ["attackerGen"] = hit.Attacker.Generation,
            ["attackerName"] = ResolveCharacterName(world, hit.Attacker),
            ["victimSlot"] = hit.Victim.Slot,
            ["victimGen"] = hit.Victim.Generation,
            ["victimName"] = ResolveCharacterName(world, hit.Victim),
            ["part"] = hit.Part,
            ["partName"] = ResolvePartName(world, hit.Victim, hit.Part),
            ["cut"] = cut,
            ["blunt"] = blunt,
            ["pierce"] = pierce,
            ["stun"] = (double)hit.Damage.ExtraStun,
            ["damage"] = cut + blunt + pierce,
            ["x"] = (double)hit.VictimPosition.X,
            ["y"] = (double)hit.VictimPosition.Y,
            ["z"] = (double)hit.VictimPosition.Z,
            ["intended"] = hit.Intended,
            ["lowStrike"] = hit.LowStrike,
        };
        return new LuaGameEvent("hit", payload);
    }

    /// <summary>
    /// The character's name when it resolves in <paramref name="world"/>; empty otherwise.
    /// </summary>
    static string ResolveCharacterName(World? world, CharacterId id)
    {
        if (world is null || id.IsNone) return "";
        if (!world.Characters.TryResolveNext(id, out int slot)) return "";

        return world.Characters.Cold(slot)?.Name ?? "";
    }

    /// <summary>
    /// The body part string id of <paramref name="partIndex"/> on <paramref name="victim"/>; empty when unavailable.
    /// </summary>
    static string ResolvePartName(World? world, CharacterId victim, int partIndex)
    {
        if (world is null || victim.IsNone || partIndex < 0) return "";
        if (!world.Characters.TryResolveNext(victim, out int slot)) return "";

        var parts = world.Characters.Cold(slot)?.Medical?.Parts;
        if (parts is null || partIndex >= parts.Count) return "";

        return parts[partIndex].Template?.StringId ?? "";
    }

    /// <summary>
    /// Forwards a non-typing key-down virtual key code to running Lua mods.
    /// </summary>
    public void NotifyKeyDown(int vk) => _host.NotifyKeyDown(vk);

    /// <summary>
    /// Stops all running Lua mods, disposes hot-reload watchers, and clears all HUD overlay widgets.
    /// </summary>
    public void StopAll() => _host.Dispose();

    /// <summary>
    /// The mods on disk by name; null when the mod folder cannot be listed.
    /// </summary>
    static Dictionary<string, ModInfo>? TryDiscover(GameInstall install)
    {
        try
        {
            return ModList.Discover(install).ToDictionary(m => m.Name, StringComparer.Ordinal);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The id and folder of each enabled mod that is on disk, in the enabled order.
    /// </summary>
    static List<(string ModId, string ModFolder)> EnabledMods(GameInstall install, Dictionary<string, ModInfo> onDisk)
    {
        List<(string ModId, string ModFolder)> enabled = [];
        foreach (string name in ModList.ReadEnabled(install))
        {
            if (onDisk.TryGetValue(name, out ModInfo? info))
            {
                enabled.Add((info.Name, info.Folder));
            }
        }

        return enabled;
    }

    /// <summary>
    /// Steps the bridge on the session's world, when there is one.
    /// </summary>
    void StepWorld(GameHost host)
    {
        if (host.Session?.World is not { } world) return;

        if (!ReferenceEquals(_lastWorld, world))
        {
            _lastWorld = world;
            _pendingEvents.Clear();
        }
        _bridge.StepWorld(world);
    }

    /// <summary>
    /// The name of the first selected character, or null when none is selected or it is gone.
    /// </summary>
    static string? SelectedName(PlayerInterface ui, GameHost host)
    {
        IReadOnlyList<CharacterId> selected = ui.SelectedIds();
        if (selected.Count == 0) return null;
        if (host.Session?.World is not { } w) return null;
        if (!w.Characters.TryResolveNext(selected[0], out int slot)) return null;

        return w.Characters.Cold(slot)?.Name;
    }

    /// <summary>
    /// Forwards a key the interface did not give to a text field to the Lua mods, when it maps to a virtual key code.
    /// </summary>
    void ForwardKeyDown(Key k)
    {
        int vk = MapVirtualKey(k);
        if (vk != 0) _host.NotifyKeyDown(vk);
    }

    /// <summary>
    /// The Windows virtual key code of letters, digits, Space, Tab and Escape; 0 for any other key.
    /// </summary>
    static int MapVirtualKey(Key k) => k switch
    {
        >= Key.A and <= Key.Z => 0x41 + (k - Key.A),
        >= Key.D0 and <= Key.D9 => 0x30 + (k - Key.D0),
        Key.Space => 0x20,
        Key.Tab => 0x09,
        Key.Escape => 0x1B,
        _ => 0,
    };
}
