using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Meitou.Data.Fcs;
using Meitou.Game.DevTools.Agents;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Overlays;
using Meitou.Game.Rendering.Profiling;
using Meitou.Game.Ui;
using Meitou.Modding;
using Meitou.Simulation;
using EngineKey = Meitou.Game.Host.Input.Key;

namespace Meitou.Game.DevTools;

/// <summary>
/// The debug menu (F10; Ctrl+F10 on Metal, where F10 is the settings panel; docs/game/ui-input.md "Debug menu"): cheats sent to the world as
/// <see cref="DebugCommand"/>s. Up/Down pick a row, typing fills its field, Enter applies it, Esc or the close key closes; while open it takes every key.
/// The rows are grouped in labelled sections (<see cref="DebugCheatSections"/>: Character, World / Spawn, Combat; then Mods for code mods' rows ([modding](docs/modding.md)); then Tools and Overlays, <see cref="DebugTools"/>, whose rows also take Space, Left and Right); Up and Down walk them in the order shown. Values sit in one column: typed fields, and on / off for switches. Teleport and spawn use the camera's target (the middle of the screen); heal, max skills and damage the selected characters, else the inspected one. Heal keeps lost limbs; its sub-row Heal fully gives them back. Godmode (all player characters; see <see cref="DebugGodmode"/>) is a switch, and so is Guard (<see cref="DebugGuard"/>: kills hostiles within 30 m of the lead and heals the player's characters; it switches itself off when a game loads). "Teleport on map" is a switch too: while on, a click on the open map sends the selected characters there. Clear bounties clears the selected characters' (else every player character's), all or one faction's.
/// </summary>
sealed class DebugMenu(GameHost host)
{
    internal enum DebugRow { Teleport, SpawnNpc, GiveItem, Money, Heal, HealFully, MaxSkills, Damage, ForceHits, DamageMultiplier, UnlockResearch, ClearBounties, Godmode, Guard, ExplodeLevel, Explode, ExplodeClick }

    internal static readonly string[] DebugLabels =
    [
        "Teleport selected here", "Spawn NPC (hostile)", "Give item (name x5 q80)", "Money (+/- cats)", "Heal", "  Heal fully (restore limbs)", "Max out skills", "Damage (cut)",
        "Force hits to", "Damage multiplier", "Unlock all research", "Clear bounties", "Godmode (all players)", "Guard (kill + heal in radius)", "Explosion level", "Explode at target", "Explode on click",
    ];

    /// <summary>The cheat rows' sections, in the order shown (and walked by Up / Down).</summary>
    static readonly (string Title, DebugRow[] Rows)[] DebugCheatSections =
    [
        ("Character", [DebugRow.Teleport, DebugRow.Heal, DebugRow.HealFully, DebugRow.MaxSkills, DebugRow.Godmode, DebugRow.ClearBounties]),
        ("World / Spawn", [DebugRow.SpawnNpc, DebugRow.GiveItem, DebugRow.Money, DebugRow.UnlockResearch]),
        ("Combat", [DebugRow.Damage, DebugRow.ForceHits, DebugRow.DamageMultiplier, DebugRow.Guard, DebugRow.ExplodeLevel, DebugRow.Explode, DebugRow.ExplodeClick]),
    ];

    /// <summary>The menu's rows in display order: (section title when the row starts a section, row number: a cheat, a mod row, or <see cref="GameMods.DebugRowCount"/> + the tool's index).</summary>
    List<(string? Header, int Row)> DebugLayout()
    {
        var layout = new List<(string?, int)>();
        foreach (var (title, rows) in DebugCheatSections)
            for (int k = 0; k < rows.Length; k++) layout.Add((k == 0 ? title : null, (int)rows[k]));
        for (int m = DebugLabels.Length; m < host.modding.DebugRowCount; m++) layout.Add((m == DebugLabels.Length ? "Mods" : null, m));
        if (debugToolsOn)
            for (int t = 0; t < DebugTools.Length; t++) layout.Add((t == 0 || DebugTools[t].Section != DebugTools[t - 1].Section ? DebugTools[t].Section : null, host.modding.DebugRowCount + t));
        return layout;
    }

    /// <summary>The value column's start: the label, then the key hint, each padded.</summary>
    const int DebugLabelWidth = 30, DebugKeyWidth = 10;

    // The rows' typed text: the vanilla rows', then one per code mod row (EnsureDebugFields).
    internal readonly List<string> debugFields = ["", "", "", "1000", "", "", "", "20", "", "1", "", "", ""];
    readonly Dictionary<(bool Character, string Text), (string Id, string Name)?> debugMatches = [];
    internal bool debugMenuOpen;
    int debugRow;
    string debugStatus = "";
    /// <summary>
    /// The open row's sub-list (Enter or Right opens it, Esc or Left closes it). Damage and Force hits: "Random" or "Off", then the first target's parts as (template id, name);
    /// Clear bounties: "All factions", then each faction with a bounty as (faction index, name and total).
    /// </summary>
    internal (string? Id, string Name)[]? debugParts;
    int debugPart;
    // The overlays the menu's tool rows switch, also on their own keys (F11, Ctrl+F9, F12).
    internal bool statsVisible, fpsVisible;
    internal FrameProfiler? profiler;
    /// <summary>Whether the Tools section is shown: the Vulkan game loop has these tools, the Metal one does not.</summary>
    internal bool debugToolsOn;

    /// <summary>A tool row: its section, label, key hint ("" for none), its state for the value column ("on", "off", a mode, "" for none) and what Enter does.</summary>
    readonly record struct DebugTool(string Section, string Label, string Key, Func<string> State, Action Switch)
    {
        /// <summary>A plain on / off row: Left turns it off, Right on (Enter and Space flip it); other rows flip or cycle on every one of the four keys.</summary>
        public bool IsSwitch { get; init; }
    }

    static string OnOff(bool on) => on ? "on" : "off";

    /// <summary>The tool rows below the cheats: each opens a tool or switches an overlay; a new tool adds its row here (tools first, then the Overlays section).</summary>
    DebugTool[] DebugTools => debugTools ??=
    [
        new("Tools", "Pick & collision inspector...", "", () => "", () => { debugMenuOpen = false; inspectorMenuOpen = true; }),
        new("Tools", "Interior editor...", "", () => "", host.interiorEditorHost.OpenInteriorEditor),
        new("Tools", "Level editor...", "Shift+F12", () => "", host.levelEditorHost.OpenLevelEditor),
        new("Tools", "Teleport on map...", "", () => OnOff(host.player.worldMap.MapPickSticky), ToggleMapTeleport) { IsSwitch = true },
        new("Overlays", "Navmesh view", "Shift+F11", () => host.debugOverlays.navView.ToString().ToLowerInvariant(), host.debugOverlays.ToggleNavOverlay),
        new("Overlays", "Frame statistics", "F11", () => OnOff(statsVisible), () => { statsVisible = !statsVisible; FoliageGpuCull.CountTriangles = statsVisible; }) { IsSwitch = true },
        new("Overlays", "  Character boxes", "", () => OnOff(host.debugOverlays.characterBoxes), () => host.debugOverlays.characterBoxes = !host.debugOverlays.characterBoxes) { IsSwitch = true },
        new("Overlays", "  Walk paths", "", () => OnOff(host.debugOverlays.walkPaths), () => host.debugOverlays.walkPaths = !host.debugOverlays.walkPaths) { IsSwitch = true },
        new("Overlays", "Fps counter", "Ctrl+F9", () => OnOff(fpsVisible), () => fpsVisible = !fpsVisible) { IsSwitch = true },
        new("Overlays", "VRAM pie", "Ctrl+F12", () => OnOff(profiler?.ShowVram == true), () => { if (profiler is not null) profiler.ShowVram = !profiler.ShowVram; }) { IsSwitch = true },
        new("Overlays", "Profiler", "F12", () => (profiler?.Showing ?? FrameProfiler.Mode.Off).ToString().ToLowerInvariant(),
            () => { if (profiler is not null) profiler.Showing = (FrameProfiler.Mode)(((int)profiler.Showing + 1) % 3); }),
    ];
    DebugTool[]? debugTools;

    /// <summary>
    /// The "Teleport on map" switch, level-triggered: on, every left click on the open Map tab sends the selected characters (else the lead) to the clicked spot, and the camera there, until it is
    /// switched off (the menu row, or the console's <c>tpmap</c>). The state is the interface's (<see cref="WorldMap.MapPickSticky"/>); the map shows a hint with the last result while it is on.
    /// </summary>
    internal void SetMapTeleport(bool on)
    {
        if (on) host.player.worldMap.ArmMapPick(TeleportToMapPoint);
        else host.player.worldMap.DisarmMapPick();
        mapTeleportNote = "";
        debugStatus = on ? "map teleport on: close this menu, open the map, click a spot" : "map teleport off";
    }

    void ToggleMapTeleport() => SetMapTeleport(!host.player.worldMap.MapPickSticky);

    /// <summary>The result of the last map click, shown in the map's hint.</summary>
    string mapTeleportNote = "";

    void TeleportToMapPoint(Vector3 at)
    {
        var who = host.player.agentOrders.ControlOrderers();
        string lead = "";
        if (who.Count == 0)
        {
            // Nothing selected (a click in the world deselects): the switch still teleports, the lead.
            if (host.session.CurrentSnapshot.Characters.FirstOrDefault(c => c.IsPlayer) is not { } first)
            {
                mapTeleportNote = "no player character to teleport";
                host.session.World.Post("Teleport on map: " + mapTeleportNote);
                return;
            }
            host.session.World.Commands.Enqueue(new DebugTeleport([first.Id], at) { Tick = host.session.World.Tick });
            lead = $" (nothing selected: the lead, {first.Name})";
            debugStatus = mapTeleportNote = $"teleported 1 to {at.X:0}, {at.Z:0}{lead}";
            host.session.World.Post("Teleport on map: " + mapTeleportNote);
        }
        else
        {
            debugStatus = DebugApply(DebugRow.Teleport, "", at: at);
            mapTeleportNote = debugStatus;
            if (debugFailed) { host.session.World.Post("Teleport on map: " + mapTeleportNote); return; }
        }
        CentreCameraOn(at.X, at.Z);
    }

    /// <summary>Puts the camera's target on world X Z, keeping its angle and distance (the characters just teleported there; a followed one is followed on).</summary>
    internal void CentreCameraOn(float x, float z)
    {
        var view = host.session.Camera.Current;
        host.session.Camera.Place(new Vector2(x, z), view.Yaw, view.Pitch, view.Distance);
    }

    // ---- Explode on click ----

    /// <summary>The "Explode on click" switch (F10 Combat row, console <c>explode-click</c>): the world it was switched on in and the explode flags used for each click.</summary>
    World? explodeClickWorld;
    string explodeClickArgs = "";
    /// <summary>The explosion level (1 to 5, F10 "Explosion level" row) used by <c>explode</c>, the F10 row and each click unless a <c>--level</c> says otherwise; 3 is normal. A click's normal level is larger than a plain explode's (<see cref="ExplodeLevels.Values"/>).</summary>
    int explodeLevel = ExplodeLevels.Normal;
    internal bool ExplodeClickOn => explodeClickWorld is not null && ReferenceEquals(explodeClickWorld, host.session.World);

    /// <summary>
    /// <paramref name="field"/> is empty (toggle) or <c>[on|off] [&lt;level 1-5&gt;] [--level &lt;1-5|name&gt;] [--radius &lt;m&gt;] [--force &lt;n&gt;] [--damage &lt;n&gt;] [--squad]</c>. While on, a left click on the ground blows it up (<see cref="DebugExplodeApply"/>)
    /// and the click is consumed; the switch is the world's, so it is off after another game loads.
    /// </summary>
    string SetExplodeClick(string field)
    {
        const string usage = "usage: explode-click [on|off] [<level 1-5>] [--level <1-5|name>] [--radius <m>] [--force <n>] [--damage <n>] [--squad]";
        bool? on = null;
        var kept = new List<string>();
        var words = field.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            float value;
            string w = words[i].ToLowerInvariant();
            if (w is "on" or "1" or "true") on = true;
            else if (w is "off" or "0" or "false") on = false;
            else if (w == "--squad") kept.Add("--squad");
            else if (ExplodeLevels.TryParse(w, out int level) && (w.Length > 1 || i == 0 || words[i - 1].ToLowerInvariant() is not ("--radius" or "--force" or "--damage"))) { kept.Add("--level"); kept.Add(level.ToString(CultureInfo.InvariantCulture)); }
            else if (w is "--level" or "--preset" && i + 1 < words.Length && ExplodeLevels.TryParse(words[i + 1], out level)) { kept.Add("--level"); kept.Add(level.ToString(CultureInfo.InvariantCulture)); i++; }
            else if (w is "--radius" or "--force" or "--damage" && i + 1 < words.Length && float.TryParse(words[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                && value >= (w == "--radius" ? 0.01f : 0) && value <= (w == "--radius" ? 500 : w == "--force" ? 1000 : 10000))
            { kept.Add(w); kept.Add(words[++i]); }
            else return DebugFail(usage);
        }
        bool turnOn = on ?? (kept.Count > 0 || !ExplodeClickOn);
        if (turnOn)
        {
            explodeClickWorld = host.session.World;
            if (kept.Count > 0 || !ExplodeClickOn) explodeClickArgs = string.Join(' ', kept);
            host.player.WorldClick = ExplodeClickPress;
            return $"explode-click on{(explodeClickArgs.Length > 0 ? " (" + explodeClickArgs + ")" : "")}: a left click on the ground explodes it (game unpaused only); explode-click off ends it";
        }
        explodeClickWorld = null;
        host.player.WorldClick = null;
        return "explode-click off";
    }

    /// <summary>A left press on the world while the switch is on (called by the player interface after the interface widgets declined it): explodes the ground point. False lets the click act normally.</summary>
    bool ExplodeClickPress(Vector3? ground)
    {
        if (!ExplodeClickOn) return false;
        if (debugMenuOpen || host.devConsoleHost.ConsoleOpen) return false;   // clicks on the F10 menu and the console are theirs
        if (ground is not { } at) return true;
        debugStatus = DebugExplodeApply(host.session.World, string.Create(CultureInfo.InvariantCulture, $"{at.X} {at.Z} {explodeClickArgs}"), host.session.World.Tick, click: true);
        return true;
    }

    /// <summary>The level each click uses: the one in the switch's flags, else the F10 row's.</summary>
    int ExplodeClickLevel()
    {
        var words = explodeClickArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int at = Array.LastIndexOf(words, "--level");
        return at >= 0 && at + 1 < words.Length && ExplodeLevels.TryParse(words[at + 1], out int level) ? level : explodeLevel;
    }

    /// <summary>The hint while the switch is on.</summary>
    internal void DrawExplodeClickHint(DebugOverlay o, int width, int height)
    {
        int level = ExplodeClickLevel();
        var v = ExplodeLevels.Values(level, true);
        string text = $"explode-click ON: left click = explode, {ExplodeLevels.Label(level)} (r={v.Radius:0.#}, force {v.Force:0.#}, dmg {v.Damage:0.#}{(explodeClickArgs.Length > 0 ? ", then " + explodeClickArgs : "")}). Switch off: explode-click off"
            + (host.session.Simulation.IsPaused ? "   PAUSED: unpause, the blast runs in the next tick" : "");
        float w = text.Length * o.CharWidth + 20, x = (width - w) / 2;
        o.Rect(x, 8, x + w, 8 + o.LineHeight + 12, DebugOverlay.PanelColour);
        o.Text(text, x + 10, 12, new Vector4(1f, 0.9f, 0.4f, 1));
        o.Flush(width, height);
    }

    /// <summary>The hint while the Map tab is open and "Teleport on map" is on: the state, how to switch it off and the last result.</summary>
    internal void DrawMapPickHint(DebugOverlay o, int width, int height)
    {
        string text = "Teleport on map: ON, every click teleports (F10 > Teleport on map or 'tpmap off' ends it)";
        string note = mapTeleportNote;
        float w = Math.Max(text.Length, note.Length) * o.CharWidth + 20, x = (width - w) / 2, h = o.LineHeight * (note.Length > 0 ? 2 : 1) + 8;
        o.Rect(x, 8, x + w, 8 + h + 8, DebugOverlay.PanelColour);
        o.Text(text, x + 10, 12, new Vector4(1f, 0.9f, 0.4f, 1));
        if (note.Length > 0) o.Text(note, x + 10, 12 + o.LineHeight, new Vector4(0.8f, 0.95f, 0.8f, 1));
        o.Flush(width, height);
    }

    /// <summary>The rows of the menu: the cheats, the code mods' rows (<see cref="GameMods.DebugRowCount"/>), then the tool rows.</summary>
    int DebugMenuRows => host.modding.DebugRowCount + (debugToolsOn ? DebugTools.Length : 0);
    bool DebugIsTool(int row) => row >= host.modding.DebugRowCount;

    bool DebugHasField(int row) => !DebugIsTool(row) && (row >= DebugLabels.Length ? host.modding.ModDebugRows[row - DebugLabels.Length].HasField
        : (DebugRow)row is not (DebugRow.Teleport or DebugRow.Heal or DebugRow.HealFully or DebugRow.MaxSkills or DebugRow.ForceHits or DebugRow.UnlockResearch or DebugRow.ClearBounties or DebugRow.Godmode or DebugRow.Guard or DebugRow.ExplodeLevel or DebugRow.Explode or DebugRow.ExplodeClick));

    /// <summary>The rows that open the body part list.</summary>
    bool DebugPicksPart(int row) => !DebugIsTool(row) && (DebugRow)row is DebugRow.Damage or DebugRow.ForceHits or DebugRow.ClearBounties;

    /// <summary>The row Up (-1) or Down (+1) reaches from the current one, in the order shown.</summary>
    int DebugStep(int delta)
    {
        var layout = DebugLayout();
        int at = layout.FindIndex(l => l.Row == debugRow);
        return layout[((at < 0 ? 0 : at) + delta + layout.Count) % layout.Count].Row;
    }

    internal void DebugMenuKey(EngineKey key)
    {
        host.modding.EnsureDebugFields();
        string field = DebugIsTool(debugRow) ? "" : debugFields[debugRow];
        if (debugParts is { } parts)
        {
            switch (key)
            {
                case EngineKey.Escape or EngineKey.Left: debugParts = null; break;
                case EngineKey.Up: debugPart = (debugPart + parts.Length - 1) % parts.Length; break;
                case EngineKey.Down: debugPart = (debugPart + 1) % parts.Length; break;
                case EngineKey.Backspace when field.Length > 0: debugFields[debugRow] = field[..^1]; break;
                case EngineKey.Enter: debugStatus = DebugApply((DebugRow)debugRow, field.Trim(), parts[debugPart]); break;
            }
            return;
        }
        switch (key)
        {
            case EngineKey.Enter or EngineKey.Left or EngineKey.Right when debugRow < DebugLabels.Length && (DebugRow)debugRow == DebugRow.ExplodeLevel:
                explodeLevel = key == EngineKey.Left ? Math.Max(1, explodeLevel - 1) : key == EngineKey.Right ? Math.Min(5, explodeLevel + 1) : explodeLevel % 5 + 1;
                var shown = ExplodeLevels.Values(explodeLevel, false);
                debugStatus = $"explosion {ExplodeLevels.Label(explodeLevel)}: r={shown.Radius:0.#}, force {shown.Force:0.#}, dmg {shown.Damage:0.#} (click: r={ExplodeLevels.Values(explodeLevel, true).Radius:0.#}, force {ExplodeLevels.Values(explodeLevel, true).Force:0.#}, dmg {ExplodeLevels.Values(explodeLevel, true).Damage:0.#})";
                break;
            case EngineKey.Enter or EngineKey.Right when DebugPicksPart(debugRow):
                debugParts = (DebugRow)debugRow == DebugRow.ClearBounties ? DebugBountyList() : DebugPartList();
                if (debugParts is not null) debugPart = Math.Min(debugPart, debugParts.Length - 1);
                break;
            case EngineKey.Escape: debugMenuOpen = false; break;
            case EngineKey.Up: debugRow = DebugStep(-1); break;
            case EngineKey.Down: debugRow = DebugStep(1); break;
            case EngineKey.Enter or EngineKey.Space or EngineKey.Left or EngineKey.Right when DebugIsTool(debugRow):
                var tool = DebugTools[debugRow - host.modding.DebugRowCount];
                // Left means off and Right on, so Left then Right never ends up flipping a switch twice.
                if (tool.IsSwitch && key is EngineKey.Left or EngineKey.Right && (tool.State() == "on") == (key == EngineKey.Right)) break;
                tool.Switch();
                break;
            case EngineKey.Backspace when field.Length > 0: debugFields[debugRow] = field[..^1]; break;
            case EngineKey.Enter: debugStatus = DebugApply((DebugRow)debugRow, field.Trim()); break;
        }
    }

    (string? Id, string Name)[]? DebugPartList()
    {
        if (DebugTargets() is not [var first, ..])
        {
            debugStatus = "select or inspect a character first";
            return null;
        }
        var table = host.session.World.Characters;
        var body = table.TryResolveNext(first, out int slot) ? table.Cold(slot)?.Medical?.Parts : null;
        var parts = (body ?? []).Select(p => ((string?)p.Template.StringId, p.Template.Name.Length > 0 ? p.Template.Name : p.Template.StringId));
        return [(null, (DebugRow)debugRow == DebugRow.Damage ? "Random (as a blow)" : "Off (as a blow)"), .. parts];
    }

    /// <summary>The Clear bounties list: every faction with a bounty on the selected characters (else on any player character), with its total.</summary>
    (string? Id, string Name)[]? DebugBountyList()
    {
        var table = host.session.World.Characters;
        var selected = host.session.World.Player.Selection.ToArray();
        var ledgers = new List<Meitou.Simulation.Crime.BountyLedger>();
        for (int s = 0; s < table.HighWater; s++)
            if (table.Next[s].Alive && table.Cold(s) is { Bounties: { } ledger } c && (selected.Length > 0 ? selected.Contains(table.IdOf(s)) : c.IsPlayer)) ledgers.Add(ledger);
        var factions = ledgers.SelectMany(l => l.Entries).GroupBy(e => e.Faction).OrderBy(g => g.Key)
            .Select(g => ((string?)g.Key.ToString(CultureInfo.InvariantCulture), $"{g.First().FactionName}   c.{g.Sum(e => e.Amount).ToString("N0", CultureInfo.InvariantCulture)}")).ToArray();
        if (factions.Length == 0)
        {
            debugStatus = selected.Length > 0 ? "no bounties on the selected characters" : "no bounties on your characters";
            return null;
        }
        return [(null, "All factions"), .. factions];
    }

    internal void DebugMenuText(char c)
    {
        host.modding.EnsureDebugFields();
        if (DebugHasField(debugRow) && !char.IsControl(c)) debugFields[debugRow] += c;
    }

    /// <summary>Heal, max skills and damage: the selected characters, else the inspected one.</summary>
    CharacterId[] DebugTargets()
    {
        var player = host.session.World.Player;
        var selected = host.player.agentOrders.ControlOrderers().ToArray();   // includes a select issued this tick (--setup / --exec run commands in one frame)
        return selected.Length > 0 ? selected : player.Inspected.IsNone ? [] : [player.Inspected];
    }

    /// <summary>Set by <see cref="DebugFail"/> while <see cref="DebugApply"/> runs: the last action did not happen (the console prints it as an error).</summary>
    internal bool debugFailed;

    string DebugFail(string message)
    {
        debugFailed = true;
        return message;
    }

    /// <summary>
    /// The guard row: <paramref name="field"/> is empty (toggle, keeping the radius and animals setting), or <c>[on|off] [--radius &lt;m&gt;] [--animals]</c> as the console passes it. Without <c>--animals</c> the animals setting resets to off.
    /// </summary>
    string DebugGuardApply(World world, string field, long tick)
    {
        var player = world.Player;
        bool? on = null;
        float radius = player.GuardRadius;
        bool animals = field.Length == 0 && player.GuardAnimals;
        var words = field.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            string w = words[i].ToLowerInvariant();
            if (w is "on" or "1" or "true") on = true;
            else if (w is "off" or "0" or "false") on = false;
            else if (w == "--animals") animals = true;
            else if (w == "--radius" && i + 1 < words.Length && float.TryParse(words[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out radius) && radius > 0 && radius <= 2000) { }
            else return DebugFail("usage: guard [on|off] [--radius <m, 0 to 2000>] [--animals]");
        }
        bool guardOn = on ?? !player.Guard;
        world.Commands.Enqueue(new DebugGuard(guardOn, radius, animals) { Tick = tick });
        return guardOn ? $"guard on: hostiles within {radius:0.#} m of the lead are killed{(animals ? " (animals too)" : "")}, player characters healed, 4 times a second; off when a game loads" : "guard off";
    }

    /// <summary>
    /// The explode row: <paramref name="field"/> is empty (40 m around the shown target, else the selected, else the first, player character) or <c>[&lt;x&gt; &lt;z&gt;] [--radius &lt;m&gt;] [--force &lt;n&gt;] [--damage &lt;n&gt;] [--squad]</c>
    /// as the console passes it. <see cref="DebugExplode"/> does the rest in the simulation; its summary reaches the message log.
    /// </summary>
    /// <summary>Largest explode radius in metres (the only cap: the blast scans every living character in the world).</summary>
    const float ExplodeMaxRadius = 500;

    string DebugExplodeApply(World world, string field, long tick, bool click = false)
    {
        const string usage = "usage: explode [<x> <z>|<level 1-5>] [--level <1-5|name>] [--id <c:slot.gen>] [--radius <m>] [--force <n>] [--damage <n>] [--squad]";
        int level = explodeLevel;
        (float radius, float force, float damage) = ExplodeLevels.Values(level, click);
        bool squad = false;
        var numbers = new List<float>();
        CharacterId? byId = null;
        var words = field.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // A single bare integer 1-5 is a level ("explode 4"); two numbers are x z. A flag's value is not a bare number.
        int bare = 0;
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].StartsWith("--", StringComparison.Ordinal)) { if (words[i].ToLowerInvariant() is not "--squad") i++; }
            else if (float.TryParse(words[i], NumberStyles.Float, CultureInfo.InvariantCulture, out _)) bare++;
        }
        for (int i = 0; i < words.Length; i++)
        {
            float value;
            if (words[i].Equals("--squad", StringComparison.OrdinalIgnoreCase)) squad = true;
            else if (words[i].Equals("--level", StringComparison.OrdinalIgnoreCase) || words[i].Equals("--preset", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= words.Length || !ExplodeLevels.TryParse(words[++i], out level)) return DebugFail("explode: --level needs 1 to 5 or underkill, light, normal, overkill, ridiculous");
                (radius, force, damage) = ExplodeLevels.Values(level, click);
            }
            else if (bare == 1 && words[i].Length == 1 && ExplodeLevels.TryParse(words[i], out level)) (radius, force, damage) = ExplodeLevels.Values(level, click);
            else if (words[i].Equals("--id", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= words.Length || !AgentOrders.TryParseCharacter(words[++i], out var parsed)) return DebugFail("explode: --id needs a character id (c:<slot>.<gen>)");
                byId = parsed;
            }
            else if (words[i].Equals("--radius", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= words.Length || !float.TryParse(words[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !float.IsFinite(value) || value <= 0) return DebugFail("explode: --radius needs a finite number above 0");
                if (value > ExplodeMaxRadius) return DebugFail($"explode: --radius {value:0.#} is above the maximum of {ExplodeMaxRadius:0} m");
                radius = value;
            }
            else if (words[i].Equals("--force", StringComparison.OrdinalIgnoreCase) && i + 1 < words.Length && float.TryParse(words[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= 0 && value <= 1000) force = value;   // NaN fails both bounds checks, infinity fails the upper one
            else if (words[i].Equals("--damage", StringComparison.OrdinalIgnoreCase) && i + 1 < words.Length && float.TryParse(words[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= 0 && value <= 10000) damage = value;
            else if (float.TryParse(words[i], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                if (!float.IsFinite(value)) return DebugFail("explode: coordinates must be finite numbers");
                numbers.Add(value);
            }
            else return DebugFail(usage);
        }
        if (numbers.Count is not (0 or 2)) return DebugFail(usage);
        Vector2 at;
        string onName = "";
        var snap = host.session.CurrentSnapshot;
        if (numbers.Count == 2 && byId is null) at = new Vector2(numbers[0], numbers[1]);
        else
        {
            // Centre priority: --id; else the character the info panel shows (inspected, not one of the squad); else the selected squad character; else the first.
            CharacterSnapshot? centre;
            if (byId is { } wanted)
            {
                centre = snap.Characters.FirstOrDefault(c => c.Id == wanted);
                if (centre is null) return DebugFail($"explode: no character c:{wanted.Slot}.{wanted.Generation}");
            }
            else centre = snap.Characters.FirstOrDefault(c => c.Inspected && !c.IsPlayer)
                ?? snap.Characters.FirstOrDefault(c => c.IsPlayer && c.Selected) ?? snap.Characters.FirstOrDefault(c => c.IsPlayer);
            if (centre is null) return DebugFail("explode: no character to centre on (pass <x> <z>)");
            at = new Vector2(centre.Position.X, centre.Position.Z);
            onName = centre.Name;
        }
        world.Commands.Enqueue(new DebugExplode(at, radius, force, damage, squad, onName, level) { Tick = tick });
        return $"explode{(onName.Length > 0 ? " on " + onName : $" at {at.X:0}, {at.Y:0}")}: {ExplodeLevels.Label(level)}, r={radius:0.#}, force {force:0.#}, dmg {damage:0.#}{(squad ? ", squad" : "")}; result in the log, unpause first";
    }

    /// <summary>
    /// The one implementation of the cheats, for the menu's rows and the dev console's commands (<see cref="debugFailed"/> says whether it worked). <paramref name="at"/> is where teleport and spawn go
    /// (the camera's target, the middle of the screen, without it).
    /// </summary>
    internal string DebugApply(DebugRow row, string field, (string? Id, string Name) part = default, Vector3? at = null)
    {
        debugFailed = false;
        var world = host.session.World;
        long tick = world.Tick;
        var selected = host.player.agentOrders.ControlOrderers().ToArray();
        var targets = DebugTargets();
        var here = at ?? host.session.Camera.Current.Target;
        if ((int)row >= DebugLabels.Length) return host.modding.ModDebugApply((int)row - DebugLabels.Length, field, selected, here, tick);
        switch (row)
        {
            case DebugRow.Teleport:
                if (selected.Length == 0) return DebugFail("select characters first");
                world.Commands.Enqueue(new DebugTeleport(selected, here) { Tick = tick });
                return $"teleported {selected.Length} to {here.X:0}, {here.Z:0}";
            case DebugRow.SpawnNpc:
                if (DebugRecord(field, character: true) is not { } npc) return DebugFail($"no character record matches '{field}'");
                world.Commands.Enqueue(new DebugSpawn(npc.Id, new Vector2(here.X, here.Z)) { Tick = tick });
                return $"spawned {npc.Name} at {here.X:0}, {here.Z:0}";
            case DebugRow.GiveItem:
                var (name, quantity, quality) = DebugItemField(field);
                return DebugGive(name, quantity, quality);
            case DebugRow.Money:
                if (!long.TryParse(field, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long delta)) return DebugFail("money: a whole number, e.g. 500 or -500");
                world.Commands.Enqueue(new DebugMoney(delta) { Tick = tick });
                return $"money {delta.ToString("+#;-#;0", CultureInfo.InvariantCulture)}";
            case DebugRow.Heal or DebugRow.HealFully:
                if (targets.Length == 0) return DebugFail("select or inspect a character first");
                bool limbs = row == DebugRow.HealFully;
                world.Commands.Enqueue(new DebugHeal(targets, limbs) { Tick = tick });
                return $"healed {targets.Length}{(limbs ? ", limbs restored" : "")}";
            case DebugRow.MaxSkills:
                if (targets.Length == 0) return DebugFail("select or inspect a character first");
                world.Commands.Enqueue(new DebugMaxSkills(targets) { Tick = tick });
                return $"every stat 100 for {targets.Length}";
            case DebugRow.Damage:
                if (targets.Length == 0) return DebugFail("select or inspect a character first");
                if (!float.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out float cut) || !(cut > 0)) return DebugFail("damage: a positive number");
                world.Commands.Enqueue(new DebugDamage(targets, cut, part.Id) { Tick = tick });
                return $"{cut:0.#} cut damage to {targets.Length}{(part.Id is null ? "" : $" on {part.Name}")}";
            case DebugRow.ForceHits:
                world.Commands.Enqueue(new DebugForceHits(part.Id is { } forced ? (forced, part.Name ?? forced) : null) { Tick = tick });
                return part.Id is null ? "blows land as usual" : $"every blow lands on {part.Name} (by name on other bodies)";
            case DebugRow.DamageMultiplier:
                if (!float.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out float multiplier) || !(multiplier >= 0) || multiplier > 100) return DebugFail("damage multiplier: a number from 0 to 100 (new game: 0.5 to 4)");
                world.Commands.Enqueue(new DebugDamageMultiplier(multiplier) { Tick = tick });
                return $"damage x{multiplier:0.##} (crossbows when next equipped)";
            case DebugRow.ClearBounties:
                int? faction = part.Id is { } f ? int.Parse(f, CultureInfo.InvariantCulture) : null;
                world.Commands.Enqueue(new DebugClearBounties(selected, faction) { Tick = tick });
                debugParts = null;
                string whose = selected.Length > 0 ? $"{selected.Length} selected" : "your characters";
                return faction is null ? $"every bounty cleared on {whose}" : $"{part.Name?.Split("   ")[0]} bounties cleared on {whose}";
            case DebugRow.Godmode:
                bool godOn = field.Trim().ToLowerInvariant() switch { "on" or "1" or "true" => true, "off" or "0" or "false" => false, _ => !world.Player.Godmode };
                world.Commands.Enqueue(new DebugGodmode(godOn) { Tick = tick });
                return godOn ? "godmode on: every player character has all stats 1000 and is healed 5 times a second" : "godmode off: each character has its own stats back";
            case DebugRow.Guard:
                return DebugGuardApply(world, field, tick);
            case DebugRow.Explode:
                return DebugExplodeApply(world, field, tick);
            case DebugRow.ExplodeClick:
                return SetExplodeClick(field);
            case DebugRow.UnlockResearch:
                world.Commands.Enqueue(new DebugUnlockResearch { Tick = tick });
                return "every research finished";
        }
        return "";
    }


    /// <summary>Puts items on the first selected character (the menu's Give item row; the console's <c>give</c>): the record by name, the quantity 1 to 1000, the quality 0 to 100.</summary>
    internal string DebugGive(string name, int quantity, int quality, bool equip = false)
    {
        debugFailed = false;
        var selected = host.player.agentOrders.ControlOrderers().ToArray();
        if (selected.Length == 0) return DebugFail("select a character first");
        quantity = Math.Clamp(quantity, 1, 1000);
        quality = Math.Clamp(quality, 0, 100);
        if (DebugRecord(name, character: false) is not { } item) return DebugFail($"no item record matches '{name}'");
        host.session.World.Commands.Enqueue(new DebugGiveItem(selected[0], item.Id, quantity, quality) { Tick = host.session.World.Tick, Equip = equip });
        if (host.scene.Database is { } db && db.Find(item.Id)?.Type == Meitou.Data.Fcs.FcsRecordType.WEAPON && DebugGiveItem.Grade(db, quality) is { } grade)
            return $"gave {quantity} x {grade.Name} {item.Name} (level {grade.Level})";
        return $"gave {quantity} x {item.Name}{(DebugHasQuality(item.Id) ? $" (quality {quality})" : "")}";
    }

    /// <summary>"Katana x5 q80": the name, the quantity (1 without one, at most 1000) and the quality (0 without one, 0 to 100); the suffixes in either order.</summary>
    static (string Name, int Quantity, int Quality) DebugItemField(string field)
    {
        int quantity = 1, quality = 0;
        for (Match m; (m = Regex.Match(field, @"^(.*?)\s+([x*q])\s*(\d+)$", RegexOptions.IgnoreCase)).Success; field = m.Groups[1].Value)
        {
            int n = int.TryParse(m.Groups[3].Value, CultureInfo.InvariantCulture, out int v) ? v : 0;
            if (m.Groups[2].Value is "q" or "Q") quality = Math.Clamp(n, 0, 100);
            else quantity = Math.Clamp(n, 1, 1000);
        }
        return (field, quantity, quality);
    }

    bool DebugHasQuality(string record) => host.scene.Database?.Find(record) is { } r && DebugGiveItem.QualityTypes.Contains(r.Type);

    /// <summary>The character (or item) record a typed name means: its string id, the name exactly, else the shortest name containing it (ignoring case).</summary>
    internal (string Id, string Name)? DebugRecord(string text, bool character)
    {
        text = text.Trim();
        if (text.Length == 0 || host.scene.Database is not { } db) return null;
        if (debugMatches.TryGetValue((character, text), out var cached)) return cached;
        bool Fits(FcsRecordType t) => character ? t is FcsRecordType.CHARACTER or FcsRecordType.ANIMAL_CHARACTER : DebugGiveItem.ItemTypes.Contains(t);
        var candidates = db.Records.Values.Where(r => Fits(r.Type));
        var hit = db.Find(text) is { } exact && Fits(exact.Type) ? exact
            : candidates.FirstOrDefault(r => string.Equals(r.Name, text, StringComparison.OrdinalIgnoreCase))
            ?? candidates.Where(r => r.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.Name.Length).ThenBy(r => r.Name, StringComparer.Ordinal).FirstOrDefault();
        (string, string)? found = hit is null ? null : (hit.StringId, hit.Name);
        debugMatches[(character, text)] = found;
        return found;
    }

    internal void DrawDebugMenu(DebugOverlay o, int width, int height, float top, string close = "F10", bool atRight = false)
    {
        var world = host.session.World;
        var here = host.session.Camera.Current.Target;
        // Each line: its text, the row it belongs to (-1 for a section title and a part list line), and whether it is the highlighted one.
        var lines = new List<(string Text, bool Title, bool Highlight, bool On)>();
        var onNames = new List<string>();
        host.modding.EnsureDebugFields();
        foreach (var (header, i) in DebugLayout())
        {
            if (header is not null) lines.Add((header, true, false, false));
            bool current = i == debugRow;
            string mark = current && debugParts is null ? "> " : "  ";
            if (DebugIsTool(i))
            {
                var tool = DebugTools[i - host.modding.DebugRowCount];
                string state = tool.State();
                bool toolOn = tool.IsSwitch ? state == "on" : state is not ("" or "off");
                if (toolOn) onNames.Add(DebugShortName(tool.Label));
                lines.Add((mark + DebugColumns(tool.Label, tool.Key, state), false, current && debugParts is null, toolOn));
                continue;
            }
            string value = "";
            bool rowOn = false;
            if (DebugHasField(i))
            {
                string field = debugFields[i];
                value = field + (current ? "_" : "");
                if (i < DebugLabels.Length && (DebugRow)i is DebugRow.SpawnNpc or DebugRow.GiveItem && field.Trim().Length > 0)
                    value += DebugRecord((DebugRow)i == DebugRow.GiveItem ? DebugItemField(field.Trim()).Name : field, (DebugRow)i == DebugRow.SpawnNpc) is { } r ? $"   -> {r.Name}" : "   -> no match";
            }
            if (i < DebugLabels.Length)
                switch ((DebugRow)i)
                {
                    case DebugRow.DamageMultiplier:
                        float multiplier = host.popData?.BodyOptions.GlobalDamageMultiplier ?? 1;
                        value += $"   now x{multiplier:0.##}";
                        rowOn = multiplier != 1;
                        break;
                    case DebugRow.ForceHits: value = host.popData?.BodyOptions.ForcedHitPart?.Name ?? "off"; rowOn = host.popData?.BodyOptions.ForcedHitPart is not null; break;
                    case DebugRow.ExplodeLevel: value = $"{explodeLevel} {ExplodeLevels.Names[explodeLevel - 1]}"; rowOn = explodeLevel != ExplodeLevels.Normal; break;
                    case DebugRow.Godmode: value = OnOff(world.Player.Godmode); rowOn = world.Player.Godmode; break;
                    case DebugRow.ExplodeClick: rowOn = ExplodeClickOn; value = ExplodeClickOn ? $"on{(explodeClickArgs.Length > 0 ? ", " + explodeClickArgs : "")}" : "off"; break;
                    case DebugRow.Guard: rowOn = world.Player.Guard; value = world.Player.Guard ? $"on, {world.Player.GuardRadius:0.#} m{(world.Player.GuardAnimals ? ", animals" : "")}" : "off"; break;
                }
            if (DebugPicksPart(i) && current && debugParts is null) value += (value.Length > 0 ? "   " : "") + ((DebugRow)i == DebugRow.ClearBounties ? "Enter/Right: pick the faction" : "Enter/Right: pick the body part");
            if (rowOn) onNames.Add(DebugShortName(host.modding.DebugLabel(i)) + ((DebugRow)i == DebugRow.DamageMultiplier && i < DebugLabels.Length ? $" x{host.popData?.BodyOptions.GlobalDamageMultiplier ?? 1:0.##}" : ""));
            lines.Add((mark + DebugColumns(host.modding.DebugLabel(i), "", value), false, current && debugParts is null, rowOn));
            if (DebugPicksPart(i) && current && debugParts is { } parts)
                for (int k = 0; k < parts.Length; k++)
                    lines.Add(((k == debugPart ? "    > " : "      ") + parts[k].Name, false, k == debugPart, false));
        }
        string info = $"{world.Player.Selection.Count} selected   here {here.X:0}, {here.Z:0}   money c.{world.Player.Money.ToString("N0", CultureInfo.InvariantCulture)}";
        string hint = debugParts is null ? $"Up/Down pick   type to edit   Enter apply / switch   Esc/{close} close"
            : $"Up/Down pick {((DebugRow)debugRow == DebugRow.ClearBounties ? "a faction" : "a part")}   {((DebugRow)debugRow == DebugRow.Damage ? "type the damage   " : "")}Enter apply   Esc/Left back";
        var dim = new Vector4(0.7f, 0.75f, 0.8f, 1);
        float pad = 10, y0 = top + 16;
        int chars = Math.Max(lines.Max(l => l.Text.Length), Math.Max(info.Length, Math.Max(hint.Length, 30)));
        // The title line names every switch that is on, cut with "+N more" to the panel's width.
        string title = $"Debug   ({close} closes)";
        if (onNames.Count > 0) title = DebugOnSummary(title, onNames, chars);
        chars = Math.Max(chars, title.Length);
        float w = chars * o.CharWidth + 2 * pad;
        // The list scrolls when the screen is too low for all of it: the window of lines keeps the highlighted one in view (titles take a little extra height).
        int room = (int)((height - y0 - 4 * pad) / o.LineHeight) - 6;
        int first = 0, count = lines.Count;
        if (count > room && room > 4)
        {
            int at = Math.Max(0, lines.FindIndex(l => l.Highlight));
            first = Math.Clamp(at - room / 2, 0, count - room);
            count = room;
        }
        float h = (count + 8) * o.LineHeight + 2 * pad;
        float x0 = atRight ? Math.Max(8, width - w - 16) : 16;
        o.Rect(x0, y0, x0 + w, y0 + h, DebugOverlay.PanelColour);
        float y = y0 + pad;
        o.Text(title, x0 + pad, y, onNames.Count > 0 ? DebugOnText : DebugOverlay.TextColour);
        y += 1.5f * o.LineHeight;
        for (int i = first; i < first + count; i++, y += o.LineHeight)
        {
            var (text, isTitle, highlight, on) = lines[i];
            if (isTitle)
            {
                o.Text("-- " + text + " --", x0 + pad, y, dim);
                continue;
            }
            if (on) o.Rect(x0 + 4, y, x0 + w - 4, y + o.LineHeight, DebugOnTint);
            if (highlight) o.Rect(x0 + 4, y, x0 + w - 4, y + o.LineHeight, new Vector4(0.25f, 0.4f, 0.65f, 0.6f));
            // A switch that is on: green text, a green tint behind and a '*' in the marker column (the cursor's '>' wins on the current row).
            if (on && !highlight && text.StartsWith("  ", StringComparison.Ordinal)) text = "* " + text[2..];
            o.Text(text, x0 + pad, y, on ? DebugOnText : DebugOverlay.TextColour);
        }
        y += 0.5f * o.LineHeight;
        o.Text(info, x0 + pad, y, dim);
        o.Text(hint, x0 + pad, y + o.LineHeight, dim);
        // The status never sets the panel's width: it wraps to it, at most three lines, cut with "...".
        var status = WrapStatus(debugStatus, chars, 3);
        for (int k = 0; k < status.Count; k++) o.Text(status[k], x0 + pad, y + (2 + k) * o.LineHeight, new Vector4(1f, 0.9f, 0.4f, 1));
        o.Flush(width, height);
    }

    /// <summary>Word-wraps <paramref name="text"/> to <paramref name="width"/> characters, at most <paramref name="maxLines"/> lines; the last one ends in "..." when text was left out.</summary>
    static List<string> WrapStatus(string text, int width, int maxLines)
    {
        var lines = new List<string>();
        var current = "";
        bool cut = false;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var w = word.Length > width ? word[..Math.Max(1, width)] : word;
            if (current.Length > 0 && current.Length + 1 + w.Length > width)
            {
                lines.Add(current);
                current = "";
                if (lines.Count == maxLines) { cut = true; break; }
            }
            current = current.Length == 0 ? w : current + " " + w;
        }
        if (!cut && current.Length > 0) lines.Add(current);
        if (cut)
        {
            var last = lines[^1];
            lines[^1] = (last.Length + 3 > width ? last[..Math.Max(0, width - 3)] : last) + "...";
        }
        return lines;
    }

    /// <summary>A switch that is on: its text colour (green) and the row's background tint.</summary>
    static readonly Vector4 DebugOnText = new(0.45f, 1f, 0.5f, 1), DebugOnTint = new(0.1f, 0.5f, 0.15f, 0.35f);

    /// <summary>A row label without its hint: "Godmode (all players)" is "Godmode", "Teleport on map..." is "Teleport on map".</summary>
    static string DebugShortName(string label)
    {
        label = label.Trim();
        int paren = label.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0) label = label[..paren];
        return label.TrimEnd('.').Trim();
    }

    /// <summary>The title with " ON: a, b" appended; when it does not fit <paramref name="maxChars"/>, the names that fit then "+N more".</summary>
    static string DebugOnSummary(string title, List<string> names, int maxChars)
    {
        string head = title + "  ON: ";
        for (int shown = names.Count; shown >= 0; shown--)
        {
            string text = head + string.Join(", ", names.Take(shown));
            if (shown < names.Count) text += (shown > 0 ? ", " : "") + $"+{names.Count - shown} more";
            if (text.Length <= maxChars || shown == 0) return text;
        }
        return head;
    }

    /// <summary>One menu line after its 2-character marker: label, key hint and value, each in its own column so every value starts at the same place.</summary>
    static string DebugColumns(string label, string key, string value) => $"{label,-DebugLabelWidth} {key,-DebugKeyWidth} {value}".TrimEnd();

    // The pick & collision inspector's menu (Meitou only, docs/viewer.md "Pick & collision inspector"). The F10 debug menu's Tools row opens it;
    // it works like the F10 menu: Up/Down pick a row, Enter or Space switch it, Esc or Enter on Close closes, and while it is open it takes every key. What was switched on stays on
    // after it closes, so the camera can fly about with the hulls drawn.
    static readonly string[] InspectorLabels =
    [
        "Info panel (under the cursor, what a click selects)",
        "Pick hull (what a click is tested against)",
        "Collision shapes (each shape's own hull)",
        "Collision mesh (the real triangles, selected building)",
        "Navmesh (off / Meitou's / original / both)",
        "Close",
    ];

    internal bool inspectorMenuOpen;
    int inspectorRow;

    bool InspectorValue(int row) => row switch
    {
        0 => host.player.inspector.InspectorPanel,
        1 => host.player.inspector.InspectorHull,
        2 => host.player.inspector.InspectorShapes,
        3 => host.player.inspector.InspectorMesh,
        _ => false,
    };

    internal void InspectorMenuKey(EngineKey key)
    {
        switch (key)
        {
            case EngineKey.Escape: inspectorMenuOpen = false; break;
            case EngineKey.Up: inspectorRow = (inspectorRow + InspectorLabels.Length - 1) % InspectorLabels.Length; break;
            case EngineKey.Down: inspectorRow = (inspectorRow + 1) % InspectorLabels.Length; break;
            case EngineKey.Enter or EngineKey.Space or EngineKey.Left or EngineKey.Right: InspectorSwitch(inspectorRow); break;
        }
    }

    void InspectorSwitch(int row)
    {
        switch (row)
        {
            case 0: host.player.inspector.InspectorPanel = !host.player.inspector.InspectorPanel; break;
            case 1: host.player.inspector.InspectorHull = !host.player.inspector.InspectorHull; break;
            case 2: host.player.inspector.InspectorShapes = !host.player.inspector.InspectorShapes; break;
            case 3: host.player.inspector.InspectorMesh = !host.player.inspector.InspectorMesh; break;
            case 4: host.debugOverlays.ToggleNavOverlay(); break;
            default: inspectorMenuOpen = false; break;
        }
        host.player.inspector.InspectorChanged();
    }

    internal void DrawInspectorMenu(DebugOverlay o, int width, int height, float top)
    {
        var lines = new List<string>();
        for (int i = 0; i < InspectorLabels.Length; i++)
        {
            string state = i < 4 ? (InspectorValue(i) ? "[x] " : "[ ] ") : i == 4 ? $"[{host.debugOverlays.navView.ToString().ToLowerInvariant()}] " : "    ";
            lines.Add((i == inspectorRow ? "> " : "  ") + state + InspectorLabels[i]);
        }
        const string Hint = "Up/Down pick   Enter/Space switch   Esc close (switches stay on)";
        var dim = new Vector4(0.7f, 0.75f, 0.8f, 1);
        float pad = 10, x0 = 16, y0 = top + 16;
        int chars = Math.Max(lines.Max(l => l.Length), Hint.Length);
        float w = chars * o.CharWidth + 2 * pad, h = (lines.Count + 4) * o.LineHeight + 2 * pad;
        o.Rect(x0, y0, x0 + w, y0 + h, DebugOverlay.PanelColour);
        float y = y0 + pad;
        o.Text("Pick & collision inspector", x0 + pad, y, DebugOverlay.TextColour);
        y += 1.5f * o.LineHeight;
        for (int i = 0; i < lines.Count; i++, y += o.LineHeight)
        {
            if (i == inspectorRow) o.Rect(x0 + 4, y, x0 + w - 4, y + o.LineHeight, new Vector4(0.25f, 0.4f, 0.65f, 0.6f));
            o.Text(lines[i], x0 + pad, y, DebugOverlay.TextColour);
        }
        o.Text(Hint, x0 + pad, y + 0.5f * o.LineHeight, dim);
        o.Flush(width, height);
    }
}
