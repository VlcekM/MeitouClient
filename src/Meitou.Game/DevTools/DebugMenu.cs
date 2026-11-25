using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Meitou.Data.Fcs;
using Meitou.Game.DevTools.Agents;
using Meitou.Game.Rendering.Foliage;
using Meitou.Game.Rendering.Frame;
using Meitou.Game.Rendering.Overlays;
using Meitou.Game.Rendering.Profiling;
using Meitou.Game.Ui;
using Meitou.Localisation;
using Meitou.Modding;
using Meitou.Simulation;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Editing;
using EngineKey = Meitou.Game.Host.Input.Key;

namespace Meitou.Game.DevTools;

/// <summary>
/// The debug menu (F10; Ctrl+F10 on Metal, where F10 is the settings panel; docs/game/ui-input.md "Debug menu"): cheats sent to the world as
/// <see cref="DebugCommand"/>s. It opens only with a game loaded to the world (<see cref="DebugMenuAvailable"/>). The menu opens on its
/// categories (<see cref="DebugCategories"/>: Characters and Items (windows of their own), Squad / Faction, World / Spawn, Combat, Cheats, then
/// Mods for code mods' rows ([modding](docs/modding.md)), Tools and Overlays, <see cref="DebugTools"/>); Enter or Right opens one as a sub-view,
/// whose rows are walked by Up and Down, typed into, applied by Enter, and left by Esc. A category whose switches are on shows how many in green
/// ("Cheats   1 active"). Values sit in one column: typed fields, and on / off for switches. Teleport and spawn use the camera's target (the
/// middle of the screen); heal and damage the selected characters, else the inspected one. Heal keeps lost limbs; Heal fully gives them back.
/// Characters closes the menu and opens a window of its own (<see cref="DebugCharactersWindow"/>), whose characters are picked in its list, not
/// from the selection; Items opens the Items window (<see cref="DebugItemsWindow"/>). Recruit takes the inspected character (a click on another
/// faction's character shows it in the HUD and keeps the squad's selection), else the first selected: it adds it to the player's faction and
/// active squad when it has a dialogue line that makes it join and the squad has room, then opens the character editor on it. The Cheats category
/// holds the one-time actions (Heal, Heal fully, Set hunger, Clear bounties, Give item) and the five debug switches (Gigachad mode, Auto heal,
/// Move instantly, One-punch man mode, No stumbling; they apply to player characters, the hits they land or take, or player-issued moves) and Guard
/// (<see cref="DebugGuard"/>: kills hostiles within 30 m of the lead and heals the player's characters; it switches itself off when a game loads).
/// "Teleport on map" is a switch too: while on, a click on the open map sends the selected characters there. Clear bounties clears the selected
/// characters' (else every player character's), all or one faction's.
/// </summary>
sealed class DebugMenu(GameHost host)
{
    /// <summary>
    /// The cheat rows, by their number (the index into DebugFields and DebugLabels).
    /// </summary>
    internal enum DebugRow
    {
        /// <summary>
        /// Teleports the selected characters to the camera's target.
        /// </summary>
        Teleport,

        /// <summary>
        /// Spawns the typed character record at the camera's target.
        /// </summary>
        SpawnNpc,

        /// <summary>
        /// Gives the typed item (name, quantity and quality) to the first selected character.
        /// </summary>
        GiveItem,

        /// <summary>
        /// Adds or takes the typed amount of money.
        /// </summary>
        Money,

        /// <summary>
        /// Heals the targets, lost limbs kept.
        /// </summary>
        Heal,

        /// <summary>
        /// Heals the targets and gives their limbs back.
        /// </summary>
        HealFully,

        /// <summary>
        /// Cuts the targets for the typed damage, on the picked part or at random.
        /// </summary>
        Damage,

        /// <summary>
        /// Makes every blow land on the picked part, or as usual.
        /// </summary>
        ForceHits,

        /// <summary>
        /// Sets the damage multiplier, 0 to 100.
        /// </summary>
        DamageMultiplier,

        /// <summary>
        /// Finishes every research.
        /// </summary>
        UnlockResearch,

        /// <summary>
        /// Clears the selected characters' (else every player character's) bounties, all or one faction's.
        /// </summary>
        ClearBounties,

        /// <summary>
        /// Switches "Gigachad mode (stats 1000)" for every player character.
        /// </summary>
        StatsTo1000,

        /// <summary>
        /// Switches "Auto heal" for every player character.
        /// </summary>
        AutoHeal,

        /// <summary>
        /// Switches "Move instantly": a right click teleports the selected characters instead of walking them.
        /// </summary>
        InstantMove,

        /// <summary>
        /// Switches "One-punch man mode": every hit landed by a player character kills.
        /// </summary>
        OnePunch,

        /// <summary>
        /// Switches "No stumbling": a hit no longer staggers a player character.
        /// </summary>
        NoStumble,

        /// <summary>
        /// Switches the guard (kills hostiles near the lead and heals the player's characters).
        /// </summary>
        Guard,

        /// <summary>
        /// The explosion level, 1 to 5.
        /// </summary>
        ExplodeLevel,

        /// <summary>
        /// An explosion at the camera's target.
        /// </summary>
        Explode,

        /// <summary>
        /// Switches explode on click.
        /// </summary>
        ExplodeClick,

        /// <summary>
        /// Recruits the first target into the player's faction and active squad, then opens the character editor on it.
        /// </summary>
        Recruit,

        /// <summary>
        /// Opens the faction relations sub-view.
        /// </summary>
        FactionRelations,

        /// <summary>
        /// Sets the targets' hunger to the typed HUD value.
        /// </summary>
        SetHunger,
    }

    /// <summary>
    /// The cheat rows' labels, in the order of <see cref="DebugRow"/>.
    /// </summary>
    internal static readonly string[] DebugLabels =
    [
        "Teleport selected here", "Spawn NPC (hostile)", "Give item (name x5 q80)", "Money (+/- cats)", "Heal", "Heal fully (restore limbs)",
        "Damage (cut)", "Force hits to", "Damage multiplier", "Unlock all research", "Clear bounties", "Gigachad mode (stats 1000)", "Auto heal",
            "Move instantly", "One-punch man mode", "No stumbling", "Guard (kill + heal in radius)", "Explosion level", "Explode at target",
            "Explode on click", "Recruit", "Faction relations", "Set hunger (1-300)",
    ];

    /// <summary>
    /// A category of the menu: its title and its rows (a cheat's number, a mod row's, or <see cref="GameMods.DebugRowCount"/> + a tool's index).
    /// <paramref name="Open"/>: it has no rows; opening it closes the menu and runs this (a window of its own).
    /// </summary>
    readonly record struct DebugCategory(string Title, int[] Rows, Action? Open = null);

    /// <summary>
    /// The menu's categories, in the order shown: the cheats by purpose, then the code mods' rows (skipped when no mod added one), then the tools.
    /// </summary>
    DebugCategory[] DebugCategories()
    {
        var worldRows = new List<int> { (int)DebugRow.Teleport, (int)DebugRow.SpawnNpc };
        var tools = new List<int>();
        var overlays = new List<int>();
        if (DebugToolsOn)
        {
            for (int t = 0; t < DebugTools.Length; t++)
            {
                int row = host.Modding.DebugRowCount + t;
                switch (DebugTools[t].Section)
                {
                    case "World": worldRows.Add(row); break;
                    case "Overlays": overlays.Add(row); break;
                    default: tools.Add(row); break;
                }
            }
        }

        var categories = new List<DebugCategory>
        {
            new("Characters", [], Open: OpenCharactersWindow),
            new("Items", [], Open: OpenItemsWindow),
            new("Squad / Faction", [(int)DebugRow.Recruit, (int)DebugRow.Money, (int)DebugRow.FactionRelations,
                (int)DebugRow.UnlockResearch]),
            new("World / Spawn", [.. worldRows]),
            new("Combat", [(int)DebugRow.Damage, (int)DebugRow.ForceHits, (int)DebugRow.DamageMultiplier, (int)DebugRow.ExplodeLevel,
                (int)DebugRow.Explode, (int)DebugRow.ExplodeClick]),
            new("Cheats", [(int)DebugRow.StatsTo1000, (int)DebugRow.OnePunch, (int)DebugRow.AutoHeal, (int)DebugRow.Heal,
                (int)DebugRow.HealFully, (int)DebugRow.SetHunger, (int)DebugRow.Guard, (int)DebugRow.InstantMove, (int)DebugRow.NoStumble,
                (int)DebugRow.GiveItem, (int)DebugRow.ClearBounties]),
        };
        if (host.Modding.DebugRowCount > DebugLabels.Length)
            categories.Add(new("Mods", [.. Enumerable.Range(DebugLabels.Length, host.Modding.DebugRowCount - DebugLabels.Length)]));
        if (tools.Count > 0) categories.Add(new("Tools", [.. tools]));
        if (overlays.Count > 0) categories.Add(new("Overlays", [.. overlays]));
        return [.. categories];
    }

    /// <summary>
    /// The open category's rows, in the order shown (and walked by Up / Down); empty at the categories.
    /// </summary>
    List<int> DebugLayout()
    {
        var categories = DebugCategories();
        return _category >= 0 && _category < categories.Length ? [.. categories[_category].Rows] : [];
    }

    /// <summary>
    /// The value column's start: the label, then the key hint, each padded.
    /// </summary>
    const int DebugLabelWidth = 30, DebugKeyWidth = 10;

    /// <summary>
    /// The rows' typed text: the vanilla rows', then one per code mod row (EnsureDebugFields).
    /// </summary>
    internal readonly List<string> DebugFields = ["", "", "", "1000", "", "", "20", "", "1", "", "", ""];

    /// <summary>
    /// The record each typed name means, by whether a character is wanted and the text; null when none matches.
    /// </summary>
    readonly Dictionary<(bool Character, string Text), (string Id, string Name)?> _debugMatches = [];

    /// <summary>
    /// The Faction relations row's sub-view: a faction, then its relation with every other one.
    /// </summary>
    readonly DebugRelationsEditor _relationsEditor = new(host);

    /// <summary>
    /// The menu is open; opening it shows the categories again. It only opens and shows with a game loaded to the world
    /// (<see cref="DebugMenuAvailable"/>); a load between scenes closes it (<see cref="GameHost.StartSceneLoad"/>), so it does not come back after one.
    /// </summary>
    internal bool DebugMenuOpen
    {
        get => _debugMenuOpen && DebugMenuAvailable;
        set
        {
            _debugMenuOpen = value && DebugMenuAvailable;
            if (!_debugMenuOpen) return;
            _category = -1;
            DebugParts = null;
        }
    }

    /// <summary>
    /// The menu can be shown: a game is loaded to the world (not the title, not a loading screen).
    /// </summary>
    internal bool DebugMenuAvailable => !host.Player.TitleOpen && !host.Covered;

    /// <summary>
    /// The open flag behind <see cref="DebugMenuOpen"/>.
    /// </summary>
    bool _debugMenuOpen;

    /// <summary>
    /// The open category, or -1 at the categories.
    /// </summary>
    int _category = -1;

    /// <summary>
    /// The highlighted category at the categories.
    /// </summary>
    int _categoryCursor;

    /// <summary>
    /// The highlighted row of the open category.
    /// </summary>
    int _debugRow;

    /// <summary>
    /// The status line: the last action's result.
    /// </summary>
    string _debugStatus = "";

    /// <summary>
    /// The highlighted row (the photo and agent tools read it).
    /// </summary>
    internal int RowIndex => _debugRow;

    /// <summary>
    /// The menu is at the categories (no category is open, or the open one is gone).
    /// </summary>
    internal bool DebugAtCategories => _category < 0 || _category >= DebugCategories().Length;

    /// <summary>
    /// The open category's title, or the highlighted one at the categories (the agent tools read it).
    /// </summary>
    internal string DebugCategoryTitle
    {
        get
        {
            var categories = DebugCategories();
            if (categories.Length == 0) return "";
            int at = _category >= 0 && _category < categories.Length ? _category : Math.Clamp(_categoryCursor, 0, categories.Length - 1);
            return categories[at].Title;
        }
    }

    /// <summary>
    /// The status line (the photo and agent tools read it).
    /// </summary>
    internal string DebugStatus => _debugStatus;

    /// <summary>
    /// The open row's sub-list (Enter or Right opens it, Esc or Left closes it). Damage and Force hits: "Random" or "Off", then the first target's
    /// parts as (template id, name); Clear bounties: "All factions", then each faction with a bounty as (faction index, name and total).
    /// </summary>
    internal (string? Id, string Name)[]? DebugParts;

    /// <summary>
    /// The highlighted line of the open sub-list.
    /// </summary>
    int _debugPart;

    /// <summary>
    /// The frame statistics (F11), an overlay the menu's tool rows switch, also on its own key.
    /// </summary>
    internal bool StatsVisible;

    /// <summary>
    /// The fps counter (Shift+F9), an overlay the menu's tool rows switch, also on its own key; shown from the start in a launcher benchmark run.
    /// </summary>
    internal bool FpsVisible = host.GameOptions.Benchmark is not null;

    /// <summary>
    /// The frame profiler (F12) the Profiler and VRAM pie rows switch; null without one.
    /// </summary>
    internal FrameProfiler? Profiler;

    /// <summary>
    /// Whether the Tools section is shown: the Vulkan game loop has these tools, the Metal one does not.
    /// </summary>
    internal bool DebugToolsOn;

    /// <summary>
    /// A tool row: its section, label, key hint ("" for none), its state for the value column ("on", "off", a mode, "" for none) and what Enter does.
    /// </summary>
    internal readonly record struct DebugTool(string Section, string Label, string Key, Func<string> State, Action Switch)
    {
        /// <summary>
        /// A plain on / off row: Left turns it off, Right on (Enter and Space flip it); other rows flip or cycle on every one of the four keys.
        /// </summary>
        public bool IsSwitch { get; init; }

        /// <summary>
        /// A stepped row (the time of day): Left steps back, Right, Enter and Space forward; its state is a value, not on / off.
        /// </summary>
        public Action<int>? Step { get; init; }
    }

    /// <summary>
    /// "on" or "off".
    /// </summary>
    static string OnOff(bool on) => on ? "on" : "off";

    /// <summary>
    /// A switch's new state from a typed field ("on", "1", "true" / "off", "0", "false"), or the opposite of <paramref name="current"/> when the
    /// field says neither.
    /// </summary>
    static bool Toggled(string field, bool current) => field.Trim().ToLowerInvariant() switch
    {
        "on" or "1" or "true" => true,
        "off" or "0" or "false" => false,
        _ => !current,
    };

    /// <summary>
    /// The tool rows below the cheats: each opens a tool or switches an overlay; a new tool adds its row here (tools first, then the Overlays
    /// section).
    /// </summary>
    internal DebugTool[] DebugTools => _debugTools ??=
    [
        new("World", "Time of day (Left/Right: -/+ 1 h)", "", DebugTimeText, () => { }) { Step = DebugStepTime },
        new("Tools", "Pick & collision inspector...", "", () => "", () => { DebugMenuOpen = false; InspectorMenuOpen = true; }),
        new("Tools", "Interior editor...", "", () => "", host.InteriorEditorHost.OpenInteriorEditor),
        new("Tools", "Level editor...", "Shift+F12", () => "", host.LevelEditorHost.OpenLevelEditor),
        new("Tools", "Action log (log_actions.txt)", "", () => host.ActionLogs.Scope.ToString().ToLowerInvariant(), host.ActionLogs.Cycle),
        new("Tools", "Teleport on map...", "", () => OnOff(host.Player.WorldMap.MapPickSticky), ToggleMapTeleport) { IsSwitch = true },
        new("Overlays", "Navmesh view", "Shift+F11", () => host.DebugOverlays.NavViewMode.ToString().ToLowerInvariant(),
            host.DebugOverlays.ToggleNavOverlay),
        new("Overlays", "Frame statistics", "F11", () => OnOff(StatsVisible),
        () => { StatsVisible = !StatsVisible; FoliageGpuCull.CountTriangles = StatsVisible; }) { IsSwitch = true },
        new("Overlays", "  Character boxes", "", () => OnOff(host.DebugOverlays.CharacterBoxes),
            () => host.DebugOverlays.CharacterBoxes = !host.DebugOverlays.CharacterBoxes) { IsSwitch = true },
        new("Overlays", "  Walk paths", "", () => OnOff(host.DebugOverlays.WalkPaths),
        () => host.DebugOverlays.WalkPaths = !host.DebugOverlays.WalkPaths) { IsSwitch = true },
        new("Overlays", "Fps counter", "Shift+F9", () => OnOff(FpsVisible), () => FpsVisible = !FpsVisible) { IsSwitch = true },
        new("Overlays", "VRAM pie", "Ctrl+F12", () => OnOff(Profiler?.ShowVram == true),
            () => { if (Profiler is not null) Profiler.ShowVram = !Profiler.ShowVram; }) { IsSwitch = true },
        new("Overlays", "Profiler", "F12", () => (Profiler?.Showing ?? FrameProfiler.Mode.Off).ToString().ToLowerInvariant(),
            () => { if (Profiler is not null) Profiler.Showing = (FrameProfiler.Mode)(((int)Profiler.Showing + 1) % 3); }),
    ];

    /// <summary>
    /// The tool rows, made on first use.
    /// </summary>
    DebugTool[]? _debugTools;

    /// <summary>
    /// The whole hour the time row last stepped to (-1: none), so Left keeps going back while the clock runs on past it.
    /// </summary>
    int _debugLastHour = -1;

    /// <summary>
    /// The settings window's "Time of day" slider (the same setter as its Advanced page), null where the loop has none.
    /// </summary>
    Slider? DebugTimeSlider => host.AgentChannel.SettingsPanel?.Sliders.FirstOrDefault(s => s.Label == "Time of day");

    /// <summary>
    /// The time row's value text.
    /// </summary>
    string DebugTimeText() => DebugTimeSlider is { } s ? s.Display : "n/a";

    /// <summary>
    /// Moves the time of day to the next whole hour (<paramref name="hours"/> 1) or back (-1): to the start of the current hour, or to the hour
    /// before when the time is within 5 game minutes past the hour or still in the hour this row last stepped to (a running clock moves on from it).
    /// </summary>
    void DebugStepTime(int hours)
    {
        if (DebugTimeSlider is not { } s) return;

        float h = s.Get();
        int whole;
        if (hours > 0)
        {
            whole = (int)MathF.Floor(h + 1e-3f) + hours;
        }
        else
        {
            whole = (int)MathF.Floor(h);
            if (h - whole < 5 / 60f || whole == _debugLastHour) whole += hours;
        }

        whole = (whole % 24 + 24) % 24;
        _debugLastHour = whole;
        s.Set(whole);
    }

    /// <summary>
    /// The "Teleport on map" switch, level-triggered: on, every left click on the open Map tab sends the selected characters (else the lead) to the
    /// clicked spot, and the camera there, until it is switched off (the menu row, or the console's <c>tpmap</c>). The state is the interface's
    /// (<see cref="WorldMap.MapPickSticky"/>); the map shows a hint with the last result while it is on.
    /// </summary>
    internal void SetMapTeleport(bool on)
    {
        if (on) host.Player.WorldMap.ArmMapPick(TeleportToMapPoint);
        else host.Player.WorldMap.DisarmMapPick();
        _mapTeleportNote = "";
        _debugStatus = on ? "map teleport on: close this menu, open the map, click a spot" : "map teleport off";
    }

    /// <summary>
    /// Flips the "Teleport on map" switch.
    /// </summary>
    void ToggleMapTeleport() => SetMapTeleport(!host.Player.WorldMap.MapPickSticky);

    /// <summary>
    /// The result of the last map click, shown in the map's hint.
    /// </summary>
    string _mapTeleportNote = "";

    /// <summary>
    /// Teleports the selected characters (else the lead) to the map point <paramref name="at"/> and centres the camera there, the result noted for
    /// the map's hint.
    /// </summary>
    void TeleportToMapPoint(Vector3 at)
    {
        var who = host.Player.AgentOrders.ControlOrderers();
        string lead = "";
        if (who.Count == 0)
        {
            // Nothing selected (a click in the world deselects): the switch still teleports, the lead.
            if (host.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.IsPlayer) is not { } first)
            {
                _mapTeleportNote = "no player character to teleport";
                host.Session.World.Post("Teleport on map: " + _mapTeleportNote);
                return;
            }
            host.Session.World.Commands.Enqueue(new DebugTeleport([first.Id], at) { Tick = host.Session.World.Tick });
            lead = $" (nothing selected: the lead, {first.Name})";
            _debugStatus = _mapTeleportNote = $"teleported 1 to {at.X:0}, {at.Z:0}{lead}";
            host.Session.World.Post("Teleport on map: " + _mapTeleportNote);
        }
        else
        {
            _debugStatus = DebugApplyNow(DebugRow.Teleport, "", at: at);
            _mapTeleportNote = _debugStatus;
            if (DebugFailed)
            {
                host.Session.World.Post("Teleport on map: " + _mapTeleportNote);
                return;
            }
        }
        CentreCameraOn(at.X, at.Z);
    }

    /// <summary>
    /// Puts the camera's target on world X Z, keeping its angle and distance (the characters just teleported there; a followed one is followed on).
    /// </summary>
    internal void CentreCameraOn(float x, float z)
    {
        var view = host.Session.Camera.Current;
        host.Session.Camera.Place(new Vector2(x, z), view.Yaw, view.Pitch, view.Distance);
    }

    // ---- Explode on click ----

    /// <summary>
    /// The "Explode on click" switch (F10 Combat row, console <c>explode-click</c>): the world it was switched on in and the explode flags used for
    /// each click.
    /// </summary>
    World? _explodeClickWorld;

    /// <summary>
    /// The explode flags each click uses.
    /// </summary>
    string _explodeClickArgs = "";

    /// <summary>
    /// The explosion level (1 to 5, F10 "Explosion level" row) used by <c>explode</c>, the F10 row and each click unless a <c>--level</c> says
    /// otherwise; 3 is normal. A click's normal level is larger than a plain explode's (<see cref="ExplodeLevels.Values"/>).
    /// </summary>
    int _explodeLevel = ExplodeLevels.Normal;

    /// <summary>
    /// Explode on click is on in the running world.
    /// </summary>
    internal bool ExplodeClickOn => _explodeClickWorld is not null && ReferenceEquals(_explodeClickWorld, host.Session.World);

    /// <summary>
    /// <paramref name="field"/> is empty (toggle) or <c>[on|off] [&lt;level 1-5&gt;] [--level &lt;1-5|name&gt;] [--radius &lt;m&gt;] [--force
    /// &lt;n&gt;] [--damage &lt;n&gt;] [--squad]</c>. While on, a left click on the ground blows it up (<see cref="DebugExplodeApply"/>) and the
    /// click is consumed; the switch is the world's, so it is off after another game loads.
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
            else if (ExplodeLevels.TryParse(w, out int level)
                && (w.Length > 1 || i == 0 || words[i - 1].ToLowerInvariant() is not ("--radius" or "--force" or "--damage")))
            {
                kept.Add("--level");
                kept.Add(level.ToString(CultureInfo.InvariantCulture));
            }
            else if (w is "--level" or "--preset" && i + 1 < words.Length && ExplodeLevels.TryParse(words[i + 1], out level))
            {
                kept.Add("--level");
                kept.Add(level.ToString(CultureInfo.InvariantCulture));
                i++;
            }
            else if (w is "--radius" or "--force" or "--damage" && i + 1 < words.Length
                && float.TryParse(words[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                && value >= (w == "--radius" ? 0.01f : 0) && value <= (w == "--radius" ? 500 : w == "--force" ? 1000 : 10000))
            { kept.Add(w); kept.Add(words[++i]); }
            else return DebugFail(usage);
        }
        bool turnOn = on ?? (kept.Count > 0 || !ExplodeClickOn);
        if (turnOn)
        {
            _explodeClickWorld = host.Session.World;
            if (kept.Count > 0 || !ExplodeClickOn) _explodeClickArgs = string.Join(' ', kept);
            host.Player.Picking.WorldClick = ExplodeClickPress;
            return $"explode-click on{(_explodeClickArgs.Length > 0 ? " (" + _explodeClickArgs + ")" : "")}: a left click on the ground explodes it "
                + $"(game unpaused only); explode-click off ends it";
        }
        _explodeClickWorld = null;
        host.Player.Picking.WorldClick = null;
        return "explode-click off";
    }

    /// <summary>
    /// A left press on the world while the switch is on (called by the player interface after the interface widgets declined it): explodes the ground
    /// point. False lets the click act normally.
    /// </summary>
    bool ExplodeClickPress(Vector3? ground)
    {
        if (!ExplodeClickOn) return false;
        if (DebugMenuOpen || host.DevConsoleHost.ConsoleOpen) return false;   // clicks on the F10 menu and the console are theirs
        if (ground is not { } at) return true;
        _debugStatus = DebugExplodeApply(host.Session.World, string.Create(CultureInfo.InvariantCulture, $"{at.X} {at.Z} {_explodeClickArgs}"),
            host.Session.World.Tick, click: true);
        return true;
    }

    /// <summary>
    /// The level each click uses: the one in the switch's flags, else the F10 row's.
    /// </summary>
    int ExplodeClickLevel()
    {
        var words = _explodeClickArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int at = Array.LastIndexOf(words, "--level");
        return at >= 0 && at + 1 < words.Length && ExplodeLevels.TryParse(words[at + 1], out int level) ? level : _explodeLevel;
    }

    /// <summary>
    /// The hint while the switch is on (<paramref name="scale"/> and <paramref name="pass"/> as for <see cref="DrawDebugMenu"/>).
    /// </summary>
    internal void DrawExplodeClickHint(DebugOverlay o, int width, int height, float scale = 1, DebugOverlay.OverlayPass? pass = null)
    {
        int level = ExplodeClickLevel();
        var v = ExplodeLevels.Values(level, true);
        string text = $"explode-click ON: left click = explode, {ExplodeLevels.Label(level)} (r={v.Radius:0.#}, force {v.Force:0.#}, "
            + $"dmg {v.Damage:0.#}{(_explodeClickArgs.Length > 0 ? ", then " + _explodeClickArgs : "")}). Switch off: explode-click off"
            + (host.Session.Simulation.IsPaused ? "   PAUSED: unpause, the blast runs in the next tick" : "");
        float w = text.Length * o.CharWidth * scale + 20 * scale, x = (width - w) / 2;
        o.Rect(x, 8 * scale, x + w, (8 + 12) * scale + o.LineHeight * scale, DebugOverlay.PanelColour);
        o.TextScaled(text, x + 10 * scale, 12 * scale, new Vector4(1f, 0.9f, 0.4f, 1), scale);
        o.Flush(width, height, null, pass);
    }

    /// <summary>
    /// The hint while the Map tab is open and "Teleport on map" is on: the state, how to switch it off and the last result.
    /// </summary>
    internal void DrawMapPickHint(DebugOverlay o, int width, int height, float scale = 1, DebugOverlay.OverlayPass? pass = null)
    {
        string text = "Teleport on map: ON, every click teleports (F10 > Teleport on map or 'tpmap off' ends it)";
        string note = _mapTeleportNote;
        float lineHeight = o.LineHeight * scale;
        float w = Math.Max(text.Length, note.Length) * o.CharWidth * scale + 20 * scale, x = (width - w) / 2;
        float h = lineHeight * (note.Length > 0 ? 2 : 1) + 8 * scale;
        o.Rect(x, 8 * scale, x + w, 8 * scale + h + 8 * scale, DebugOverlay.PanelColour);
        o.TextScaled(text, x + 10 * scale, 12 * scale, new Vector4(1f, 0.9f, 0.4f, 1), scale);
        if (note.Length > 0) o.TextScaled(note, x + 10 * scale, 12 * scale + lineHeight, new Vector4(0.8f, 0.95f, 0.8f, 1), scale);
        o.Flush(width, height, null, pass);
    }

    /// <summary>
    /// Whether <paramref name="row"/> is a tool row.
    /// </summary>
    internal bool DebugIsTool(int row) => row >= host.Modding.DebugRowCount;

    /// <summary>
    /// Whether <paramref name="row"/> takes typed text: a mod row with a field, or a cheat row other than the switches and actions without one.
    /// </summary>
    bool DebugHasField(int row) => !DebugIsTool(row) && (row >= DebugLabels.Length ? host.Modding.ModDebugRows[row - DebugLabels.Length].HasField
        : (DebugRow)row is not (DebugRow.Teleport or DebugRow.Heal or DebugRow.HealFully or DebugRow.ForceHits
            or DebugRow.UnlockResearch or DebugRow.ClearBounties or DebugRow.StatsTo1000 or DebugRow.AutoHeal or DebugRow.InstantMove
            or DebugRow.OnePunch or DebugRow.NoStumble or DebugRow.Guard or DebugRow.ExplodeLevel or DebugRow.Explode or DebugRow.ExplodeClick
            or DebugRow.Recruit or DebugRow.FactionRelations));

    /// <summary>
    /// The rows that open the body part list.
    /// </summary>
    bool DebugPicksPart(int row) => !DebugIsTool(row) && (DebugRow)row is DebugRow.Damage or DebugRow.ForceHits or DebugRow.ClearBounties;

    /// <summary>
    /// The row <paramref name="delta"/> rows from the current one, in the order shown (wrapping).
    /// </summary>
    int DebugStep(int delta)
    {
        var layout = DebugLayout();
        if (layout.Count == 0) return _debugRow;
        int at = layout.IndexOf(_debugRow), n = layout.Count;
        return layout[((at < 0 ? 0 : at) + delta % n + n) % n];
    }

    /// <summary>
    /// How many rows a wheel notch moves a list.
    /// </summary>
    const int ScrollLines = 1;

    /// <summary>
    /// The mouse wheel while the menu is open: the open sub-view (or the categories, or the open category) moves its highlight by
    /// <see cref="ScrollLines"/> rows a notch, up for a wheel away from the user, so a long list scrolls with it.
    /// </summary>
    internal void Scroll(float wheel)
    {
        int lines = -(int)MathF.Round(wheel) * ScrollLines;
        if (lines == 0) return;
        if (_relationsEditor.Open)
        {
            _relationsEditor.Scroll(lines);
            return;
        }

        if (DebugParts is { } parts)
        {
            _debugPart = Math.Clamp(_debugPart + lines, 0, parts.Length - 1);
            return;
        }

        if (DebugAtCategories)
        {
            var categories = DebugCategories();
            if (categories.Length > 0) _categoryCursor = Math.Clamp(_categoryCursor + lines, 0, categories.Length - 1);
            return;
        }

        _debugRow = DebugStep(lines);
    }

    /// <summary>
    /// Whether the menu's selection takes typed text, so the menu must take every key: a row with a field (in a category or its part list) or the
    /// faction relations' values. Otherwise it takes only <see cref="DebugMenuTakesKey"/> and the rest are the game's.
    /// </summary>
    internal bool DebugMenuTakesText
    {
        get
        {
            if (_relationsEditor.Open) return _relationsEditor.TakesText;
            return !DebugAtCategories && DebugHasField(_debugRow);
        }
    }

    /// <summary>
    /// Whether the menu takes <paramref name="key"/> while its selection takes no typed text: the navigation and activation keys only (Escape, Enter
    /// and the arrows); every other key is the game's, as if the menu were closed.
    /// </summary>
    internal static bool DebugMenuTakesKey(EngineKey key) => key is EngineKey.Escape or EngineKey.Enter or EngineKey.Up or EngineKey.Down
        or EngineKey.Left or EngineKey.Right;

    /// <summary>
    /// A key while the menu is open: at the categories Up and Down pick one and Enter or Right opens it; in a category the rows are walked, edited and
    /// applied, the tools switched, and Esc goes back; in a sub-list Up and Down pick and Enter applies.
    /// </summary>
    internal void DebugMenuKey(EngineKey key)
    {
        host.Modding.EnsureDebugFields();
        if (_relationsEditor.Open)
        {
            _relationsEditor.Key(key);
            return;
        }

        if (DebugAtCategories) RootKey(key);
        else CategoryKey(key);
    }

    /// <summary>
    /// A key at the categories: Up and Down pick one (wrapping), Enter or Right opens it, Esc closes the menu.
    /// </summary>
    void RootKey(EngineKey key)
    {
        var categories = DebugCategories();
        if (categories.Length == 0) return;
        switch (key)
        {
            case EngineKey.Up:
                _categoryCursor = (_categoryCursor + categories.Length - 1) % categories.Length;
                break;
            case EngineKey.Down:
                _categoryCursor = (_categoryCursor + 1) % categories.Length;
                break;
            case EngineKey.Enter or EngineKey.Right:
                OpenCategory(_categoryCursor);
                break;
            case EngineKey.Escape:
                DebugMenuOpen = false;
                break;
        }
    }

    /// <summary>
    /// Opens category <paramref name="index"/> on its first row.
    /// </summary>
    void OpenCategory(int index)
    {
        var categories = DebugCategories();
        if (categories.Length == 0) return;
        _category = Math.Clamp(index, 0, categories.Length - 1);
        _categoryCursor = _category;
        if (categories[_category].Open is { } open)
        {
            _category = -1;
            open();
            return;
        }

        var rows = categories[_category].Rows;
        _debugRow = rows.Length > 0 ? rows[0] : 0;
        _debugPart = 0;
        DebugParts = null;
    }

    /// <summary>
    /// Esc from a category: back to the categories.
    /// </summary>
    void CloseCategory()
    {
        _category = -1;
        _categoryCursor = Math.Clamp(_categoryCursor, 0, Math.Max(DebugCategories().Length - 1, 0));
        _debugPart = 0;
        DebugParts = null;
    }

    /// <summary>
    /// A key in a category: in a sub-list Up and Down pick and Enter applies; otherwise the rows are walked, edited and applied, the tools switched,
    /// and Esc goes back to the categories.
    /// </summary>
    void CategoryKey(EngineKey key)
    {
        if (DebugParts is { } parts) PartKey(key, parts);
        else RowKey(key);
    }

    /// <summary>
    /// A key while the open part list is shown: Up and Down pick, Enter applies the row to the picked part, Backspace edits the row's field, Esc or
    /// Left closes the list.
    /// </summary>
    void PartKey(EngineKey key, (string? Id, string Name)[] parts)
    {
        string field = DebugIsTool(_debugRow) ? "" : DebugFields[_debugRow];
        switch (key)
        {
            case EngineKey.Escape or EngineKey.Left:
                DebugParts = null;
                break;
            case EngineKey.Up:
                _debugPart = (_debugPart + parts.Length - 1) % parts.Length;
                break;
            case EngineKey.Down:
                _debugPart = (_debugPart + 1) % parts.Length;
                break;
            case EngineKey.Backspace when field.Length > 0: DebugFields[_debugRow] = field[..^1];
            break;
            case EngineKey.Enter:
                _debugStatus = DebugApplyNow((DebugRow)_debugRow, field.Trim(), parts[_debugPart]);
                break;
        }
    }

    /// <summary>
    /// A key on the rows of a category: Up and Down walk them, Enter applies the highlighted one (or opens its part list or sub-view), Backspace edits
    /// its field, the tools switch on Enter, Space, Left or Right, and Esc goes back to the categories.
    /// </summary>
    void RowKey(EngineKey key)
    {
        string field = DebugIsTool(_debugRow) ? "" : DebugFields[_debugRow];
        switch (key)
        {
            case EngineKey.Enter or EngineKey.Left or EngineKey.Right when _debugRow < DebugLabels.Length
                && (DebugRow)_debugRow == DebugRow.ExplodeLevel:
                _explodeLevel = key == EngineKey.Left ? Math.Max(1, _explodeLevel - 1) : key == EngineKey.Right ? Math.Min(5,
                _explodeLevel + 1) : _explodeLevel % 5 + 1;
                var shown = ExplodeLevels.Values(_explodeLevel, false);
                _debugStatus = $"explosion {ExplodeLevels.Label(_explodeLevel)}: r={shown.Radius:0.#}, force {shown.Force:0.#}, "
                    + $"dmg {shown.Damage:0.#} (click: r={ExplodeLevels.Values(_explodeLevel, true).Radius:0.#}, "
                    + $"force {ExplodeLevels.Values(_explodeLevel, true).Force:0.#}, dmg {ExplodeLevels.Values(_explodeLevel, true).Damage:0.#})";
                break;
            case EngineKey.Enter or EngineKey.Right when !DebugIsTool(_debugRow) && _debugRow == (int)DebugRow.FactionRelations:
                _debugStatus = _relationsEditor.Begin();
                break;
            case EngineKey.Enter or EngineKey.Right when !DebugIsTool(_debugRow) && _debugRow == (int)DebugRow.Recruit:
                RecruitAndEdit();
                break;
            case EngineKey.Enter or EngineKey.Right when DebugPicksPart(_debugRow):
                DebugParts = (DebugRow)_debugRow == DebugRow.ClearBounties ? DebugBountyList() : DebugPartList();
                if (DebugParts is not null) _debugPart = Math.Min(_debugPart, DebugParts.Length - 1);
                break;
            case EngineKey.Escape:
                CloseCategory();
                break;
            case EngineKey.Up:
                _debugRow = DebugStep(-1);
                break;
            case EngineKey.Down:
                _debugRow = DebugStep(1);
                break;
            case EngineKey.Enter or EngineKey.Space or EngineKey.Left or EngineKey.Right when DebugIsTool(_debugRow):
                var tool = DebugTools[_debugRow - host.Modding.DebugRowCount];
                if (tool.Step is { } step)
                {
                    step(key == EngineKey.Left ? -1 : 1);
                    break;
                }

                // Left means off and Right on, so Left then Right never ends up flipping a switch twice.
                if (tool.IsSwitch && key is EngineKey.Left or EngineKey.Right && (tool.State() == "on") == (key == EngineKey.Right))
                    break;
                tool.Switch();
                break;
            case EngineKey.Backspace when field.Length > 0: DebugFields[_debugRow] = field[..^1];
            break;
            case EngineKey.Enter:
                _debugStatus = DebugApplyNow((DebugRow)_debugRow, field.Trim());
                break;
        }
    }

    /// <summary>
    /// The Damage and Force hits list: "Random" or "Off", then the first target's body parts; null (the status says why) without a target.
    /// </summary>
    (string? Id, string Name)[]? DebugPartList()
    {
        if (DebugTargets(host) is not [var first, ..])
        {
            _debugStatus = "select or inspect a character first";
            return null;
        }
        var table = host.Session.World.Characters;
        var body = table.TryResolveNext(first, out int slot) ? table.Cold(slot)?.Medical?.Parts : null;
        var parts = (body ?? []).Select(p => ((string?)p.Template.StringId, p.Template.Name.Length > 0 ? p.Template.Name : p.Template.StringId));
        return [(null, (DebugRow)_debugRow == DebugRow.Damage ? "Random (as a blow)" : "Off (as a blow)"), .. parts];
    }

    /// <summary>
    /// The Clear bounties list: every faction with a bounty on the selected characters (else on any player character), with its total.
    /// </summary>
    (string? Id, string Name)[]? DebugBountyList()
    {
        var table = host.Session.World.Characters;
        var selected = host.Session.World.Player.Selection.ToArray();
        var ledgers = new List<Simulation.Crime.BountyLedger>();
        for (int s = 0; s < table.HighWater; s++)
        {
            if (table.Next[s].Alive && table.Cold(s) is { Bounties: { } ledger } c
                && (selected.Length > 0 ? selected.Contains(table.IdOf(s)) : c.IsPlayer))
                ledgers.Add(ledger);
        }
        var factions = ledgers.SelectMany(l => l.Entries).GroupBy(e => e.Faction).OrderBy(g => g.Key)
            .Select(g => ((string?)g.Key.ToString(CultureInfo.InvariantCulture),
            $"{g.First().FactionName}   c.{g.Sum(e => e.Amount).ToString("N0", CultureInfo.InvariantCulture)}")).ToArray();
        if (factions.Length == 0)
        {
            _debugStatus = selected.Length > 0 ? "no bounties on the selected characters" : "no bounties on your characters";
            return null;
        }
        return [(null, "All factions"), .. factions];
    }

    /// <summary>
    /// Types <paramref name="c"/> into the highlighted row's field (a row with one, not a control character).
    /// </summary>
    internal void DebugMenuText(char c)
    {
        host.Modding.EnsureDebugFields();
        if (_relationsEditor.Open) _relationsEditor.Type(c);
        else if (!DebugAtCategories && DebugHasField(_debugRow) && !char.IsControl(c)) DebugFields[_debugRow] += c;
    }

    /// <summary>
    /// Heal, hunger and damage: the selected characters, else the inspected one.
    /// </summary>
    internal static CharacterId[] DebugTargets(GameHost host)
    {
        var player = host.Session.World.Player;
        // includes a select issued this tick (--setup / --exec run commands in one frame)
        var selected = host.Player.AgentOrders.ControlOrderers().ToArray();
        return selected.Length > 0 ? selected : player.Inspected.IsNone ? [] : [player.Inspected];
    }

    /// <summary>
    /// Set by <see cref="DebugFail"/> while <see cref="DebugApply"/> runs: the last action did not happen (the console prints it as an error).
    /// </summary>
    internal bool DebugFailed;

    /// <summary>
    /// Marks the action as failed (<see cref="DebugFailed"/>) and returns <paramref name="message"/>.
    /// </summary>
    string DebugFail(string message)
    {
        DebugFailed = true;
        return message;
    }

    /// <summary>
    /// The guard row: <paramref name="field"/> is empty (toggle, keeping the radius and animals setting), or <c>[on|off] [--radius &lt;m&gt;]
    /// [--animals]</c> as the console passes it. Without <c>--animals</c> the animals setting resets to off.
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
            else if (w == "--radius" && i + 1 < words.Length
                && float.TryParse(words[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out radius) && radius > 0 && radius <= 2000) { }
            else return DebugFail("usage: guard [on|off] [--radius <m, 0 to 2000>] [--animals]");
        }
        bool guardOn = on ?? !player.Guard;
        world.Commands.Enqueue(new DebugGuard(guardOn, radius, animals) { Tick = tick });
        return guardOn
            ? $"guard on: hostiles within {radius:0.#} m of the lead are killed{(animals ? " (animals too)" : "")}, player characters healed, "
                + $"4 times a second; off when a game loads"
            : "guard off";
    }

    /// <summary>
    /// Largest explode radius in metres (the only cap: the blast scans every living character in the world).
    /// </summary>
    const float ExplodeMaxRadius = 500;

    /// <summary>
    /// The explode row: <paramref name="field"/> is empty (40 m around the shown target, else the selected, else the first, player character) or
    /// <c>[&lt;x&gt; &lt;z&gt;] [--radius &lt;m&gt;] [--force &lt;n&gt;] [--damage &lt;n&gt;] [--squad]</c> as the console passes it.
    /// <see cref="DebugExplode"/> does the rest in the simulation; its summary reaches the message log.
    /// </summary>
    string DebugExplodeApply(World world, string field, long tick, bool click = false)
    {
        const string usage = "usage: explode [<x> <z>|<level 1-5>] [--level <1-5|name>] [--id <c:slot.gen>] [--radius <m>] [--force <n>] "
            + "[--damage <n>] [--squad]";
        int level = _explodeLevel;
        (float radius, float force, float damage) = ExplodeLevels.Values(level, click);
        bool squad = false;
        var numbers = new List<float>();
        CharacterId? byId = null;
        var words = field.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // A single bare integer 1-5 is a level ("explode 4"); two numbers are x z. A flag's value is not a bare number.
        int bare = 0;
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (words[i].ToLowerInvariant() is not "--squad") i++;
            }
            else if (float.TryParse(words[i], NumberStyles.Float, CultureInfo.InvariantCulture, out _)) bare++;
        }
        for (int i = 0; i < words.Length; i++)
        {
            float value;
            if (words[i].Equals("--squad", StringComparison.OrdinalIgnoreCase)) squad = true;
            else if (words[i].Equals("--level", StringComparison.OrdinalIgnoreCase)
                || words[i].Equals("--preset", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= words.Length || !ExplodeLevels.TryParse(words[++i], out level))
                    return DebugFail("explode: --level needs 1 to 5 or underkill, light, normal, overkill, ridiculous");
                (radius, force, damage) = ExplodeLevels.Values(level, click);
            }
            else if (bare == 1 && words[i].Length == 1 && ExplodeLevels.TryParse(words[i], out level))
                (radius, force, damage) = ExplodeLevels.Values(level, click);
            else if (words[i].Equals("--id", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= words.Length || !AgentOrders.TryParseCharacter(words[++i], out var parsed))
                    return DebugFail("explode: --id needs a character id (c:<slot>.<gen>)");
                byId = parsed;
            }
            else if (words[i].Equals("--radius", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= words.Length || !float.TryParse(words[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                    || !float.IsFinite(value) || value <= 0)
                    return DebugFail("explode: --radius needs a finite number above 0");
                if (value > ExplodeMaxRadius)
                    return DebugFail($"explode: --radius {value:0.#} is above the maximum of {ExplodeMaxRadius:0} m");
                radius = value;
            }
            // NaN fails both bounds checks, infinity fails the upper one
            else if (words[i].Equals("--force", StringComparison.OrdinalIgnoreCase) && i + 1 < words.Length
                && float.TryParse(words[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= 0 && value <= 1000)
                force = value;
            else if (words[i].Equals("--damage", StringComparison.OrdinalIgnoreCase) && i + 1 < words.Length
                && float.TryParse(words[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= 0 && value <= 10000)
                damage = value;
            else if (float.TryParse(words[i], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                if (!float.IsFinite(value)) return DebugFail("explode: coordinates must be finite numbers");
                numbers.Add(value);
            }
            else return DebugFail(usage);
        }
        if (numbers.Count is not (0 or 2)) return DebugFail(usage);
        if (DebugExplodeCentre(numbers, byId, out var at, out string onName) is { } failed) return failed;
        world.Commands.Enqueue(new DebugExplode(at, radius, force, damage, squad, onName, level) { Tick = tick });
        return $"explode{(onName.Length > 0 ? " on " + onName : $" at {at.X:0}, {at.Y:0}")}: {ExplodeLevels.Label(level)}, r={radius:0.#}, "
            + $"force {force:0.#}, dmg {damage:0.#}{(squad ? ", squad" : "")}; result in the log, unpause first";
    }

    /// <summary>
    /// The blast's centre: x z when given without --id, else the --id character, the stranger the info panel shows, the selected or the first
    /// squad member; the failure line when there is none.
    /// </summary>
    string? DebugExplodeCentre(List<float> numbers, CharacterId? byId, out Vector2 at, out string onName)
    {
        at = default;
        onName = "";
        var snap = host.Session.CurrentSnapshot;
        if (numbers.Count == 2 && byId is null) at = new Vector2(numbers[0], numbers[1]);
        else
        {
            // Centre priority: --id; else the character the info panel shows (inspected, not one of the squad); else the selected squad character;
            // else the first.
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
        return null;
    }

    /// <summary>
    /// The one implementation of the cheats, for the menu's rows and the dev console's commands (<see cref="DebugFailed"/> says whether it worked).
    /// <paramref name="at"/> is where teleport and spawn go (the camera's target, the middle of the screen, without it).
    /// </summary>
    internal string DebugApply(DebugRow row, string field, (string? Id, string Name) part = default, Vector3? at = null)
    {
        DebugFailed = false;
        var world = host.Session.World;
        long tick = world.Tick;
        var selected = host.Player.AgentOrders.ControlOrderers().ToArray();
        var targets = DebugTargets(host);
        var here = at ?? host.Session.Camera.Current.Target;
        if ((int)row >= DebugLabels.Length)
            return host.Modding.ModDebugApply((int)row - DebugLabels.Length, field, selected, here, tick);
        switch (row)
        {
            case DebugRow.Teleport:
                if (selected.Length == 0) return DebugFail("select characters first");
                world.Commands.Enqueue(new DebugTeleport(selected, here) { Tick = tick });
                return $"teleported {selected.Length} to {here.X:0}, {here.Z:0}";
            case DebugRow.SpawnNpc:
                if (DebugRecord(field, character: true) is not { } npc)
                    return DebugFail($"no character record matches '{field}'");
                world.Commands.Enqueue(new DebugSpawn(npc.Id, new Vector2(here.X, here.Z), DebugSpawn.HostileFaction) { Tick = tick, Aggro = true });
                return $"spawned {npc.Name} (hostile) at {here.X:0}, {here.Z:0}";
            case DebugRow.GiveItem:
                var (name, quantity, quality) = DebugItemField(field);
                return DebugGive(name, quantity, quality);
            case DebugRow.Money:
                if (!long.TryParse(field, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long delta))
                    return DebugFail("money: a whole number, e.g. 500 or -500");
                world.Commands.Enqueue(new DebugMoney(delta) { Tick = tick });
                return $"money {delta.ToString("+#;-#;0", CultureInfo.InvariantCulture)}";
            case DebugRow.Recruit:
                return DebugRecruitApply(world, tick);
            case DebugRow.Heal or DebugRow.HealFully:
                if (targets.Length == 0) return DebugFail("select or inspect a character first");
                bool limbs = row == DebugRow.HealFully;
                world.Commands.Enqueue(new DebugHeal(targets, limbs) { Tick = tick });
                return $"healed {targets.Length}{(limbs ? ", limbs restored" : "")}";
            case DebugRow.SetHunger:
                return DebugHungerApply(world, tick, targets, field);
            case DebugRow.Damage:
                if (targets.Length == 0) return DebugFail("select or inspect a character first");
                if (!float.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out float cut) || !(cut > 0))
                    return DebugFail("damage: a positive number");
                world.Commands.Enqueue(new DebugDamage(targets, cut, part.Id) { Tick = tick });
                return $"{cut:0.#} cut damage to {targets.Length}{(part.Id is null ? "" : $" on {part.Name}")}";
            case DebugRow.ForceHits:
                world.Commands.Enqueue(new DebugForceHits(part.Id is { } forced ? (forced, part.Name ?? forced) : null) { Tick = tick });
                return part.Id is null ? "blows land as usual" : $"every blow lands on {part.Name} (by name on other bodies)";
            case DebugRow.DamageMultiplier:
                if (!float.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out float multiplier)
                    || !(multiplier >= 0) || multiplier > 100)
                    return DebugFail("damage multiplier: a number from 0 to 100 (new game: 0.5 to 4)");
                world.Commands.Enqueue(new DebugDamageMultiplier(multiplier) { Tick = tick });
                return $"damage x{multiplier:0.##} (crossbows when next equipped)";
            case DebugRow.ClearBounties:
                int? faction = part.Id is { } f ? int.Parse(f, CultureInfo.InvariantCulture) : null;
                world.Commands.Enqueue(new DebugClearBounties(selected, faction) { Tick = tick });
                DebugParts = null;
                string whose = selected.Length > 0 ? $"{selected.Length} selected" : "your characters";
                return faction is null ? $"every bounty cleared on {whose}" : $"{part.Name?.Split("   ")[0]} bounties cleared on {whose}";
            case DebugRow.Guard:
                return DebugGuardApply(world, field, tick);
            case DebugRow.Explode:
                return DebugExplodeApply(world, field, tick);
            case DebugRow.ExplodeClick:
                return SetExplodeClick(field);
            case DebugRow.UnlockResearch:
                world.Commands.Enqueue(new DebugUnlockResearch { Tick = tick });
                return "every research finished";
            default:
                return DebugSwitchApply(world, row, field, tick);
        }
    }

    /// <summary>
    /// Queues a hunger change in HUD units for the selected or inspected characters.
    /// </summary>
    string DebugHungerApply(World world, long tick, CharacterId[] targets, string field)
    {
        if (targets.Length == 0) return DebugFail(Tr("No character selected"));

        if (!int.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out int hunger) || hunger is < 1 or > 300)
            return DebugFail(Tr("Enter a hunger value from 1 to 300."));

        world.Commands.Enqueue(new DebugSetHunger(targets, hunger) { Tick = tick });

        return Tr("Hunger set to {1}.", hunger);
    }

    /// <summary>
    /// Translates debug text when the baked overlay font can display it, falling back to English otherwise.
    /// </summary>
    static string Tr(string english, params object?[] args)
    {
        string translated = Loc.Tr(english);

        return Loc.Format(DebugOverlay.SupportsBakedText(translated) ? translated : english, args);
    }

    /// <summary>
    /// The Recruit row: queues the recruitment of the selected or inspected character, or says why it can't join.
    /// </summary>
    private string DebugRecruitApply(World world, long tick)
    {
        var recruitTarget = RecruitTarget(host);
        if (recruitTarget.IsNone) return DebugFail("select or inspect a character first");
        if (host.PopData is not { } data) return DebugFail("no game data");
        if (!world.Characters.TryResolveNext(recruitTarget, out int recruitSlot)) return DebugFail("that character is gone");
        if (DialogueSystem.RecruitRefusal(world, data, recruitTarget) is { } refusal) return DebugFail(refusal);
        world.Commands.Enqueue(new DebugRecruit(recruitTarget) { Tick = tick });
        return $"{world.Characters.Cold(recruitSlot)!.Name} joins your squad";
    }

    /// <summary>
    /// The player-wide switch rows (stats 1000, auto heal, instant move, one punch, no stumbling): <paramref name="field"/> sets or toggles the
    /// switch and queues it; any other row returns an empty string.
    /// </summary>
    private static string DebugSwitchApply(World world, DebugRow row, string field, long tick)
    {
        switch (row)
        {
            case DebugRow.StatsTo1000:
                bool statsOn = Toggled(field, world.Player.StatsTo1000);
                world.Commands.Enqueue(new DebugStats1000(statsOn) { Tick = tick });
                return statsOn ? "stats 1000 on: every player character has all stats at 1000"
                    : "stats 1000 off: each character has its own stats back";
            case DebugRow.AutoHeal:
                bool healOn = Toggled(field, world.Player.AutoHeal);
                world.Commands.Enqueue(new DebugAutoHeal(healOn) { Tick = tick });
                return healOn ? "auto heal on: every player character is healed 5 times a second" : "auto heal off";
            case DebugRow.InstantMove:
                bool instantOn = Toggled(field, world.Player.InstantMove);
                world.Commands.Enqueue(new DebugInstantMove(instantOn) { Tick = tick });
                return instantOn ? "move instantly on: a right click teleports the selected characters" : "move instantly off";
            case DebugRow.OnePunch:
                bool punchOn = Toggled(field, world.Player.OnePunch);
                world.Commands.Enqueue(new DebugOnePunch(punchOn) { Tick = tick });
                return punchOn ? "one-punch man mode on: every hit by one of your characters kills" : "one-punch man mode off";
            case DebugRow.NoStumble:
                bool stumbleOff = Toggled(field, world.Player.NoStumble);
                world.Commands.Enqueue(new DebugNoStumble(stumbleOff) { Tick = tick });
                return stumbleOff ? "no stumbling on: hits no longer stagger your characters" : "no stumbling off";
        }
        return "";
    }

    /// <summary>
    /// <see cref="DebugApply"/> and, while the game is paused, the one tick that carries out the command it queued, so the menu's values show the change
    /// at once; a running game's next tick takes it.
    /// </summary>
    string DebugApplyNow(DebugRow row, string field, (string? Id, string Name) part = default, Vector3? at = null)
    {
        int queued = host.Session.World.Commands.Count;
        string status = DebugApply(row, field, part, at);
        if (host.Session.World.Commands.Count > queued) ApplyWhilePaused();
        return status;
    }

    /// <summary>
    /// The Recruit row's target: the inspected character (a click on another faction's character shows it in the HUD and keeps the squad's
    /// selection), else the first selected.
    /// </summary>
    static CharacterId RecruitTarget(GameHost host)
    {
        var player = host.Session.World.Player;
        if (!player.Inspected.IsNone) return player.Inspected;
        var selected = host.Player.AgentOrders.ControlOrderers();
        return selected.Count > 0 ? selected[0] : CharacterId.None;
    }

    /// <summary>
    /// The Recruit row: the target joins the player's faction and active squad, then the debug menu closes and the character editor opens on it
    /// (name and appearance), as a recruit's would.
    /// </summary>
    void RecruitAndEdit()
    {
        var target = RecruitTarget(host);
        if (target.IsNone)
        {
            _debugStatus = "select or inspect a character first";
            return;
        }

        _debugStatus = DebugApplyNow(DebugRow.Recruit, "");
        if (DebugFailed) return;
        if (!host.Player.CharacterEditor.OpenEditor(EditKind.Recruit, [target]))
        {
            _debugStatus = "the character editor did not open";
            return;
        }

        DebugMenuOpen = false;
    }

    /// <summary>
    /// The Edit characters row: the menu closes and the window opens (it picks its own character; the game's selection is not used).
    /// </summary>
    void OpenCharactersWindow()
    {
        DebugMenuOpen = false;
        host.Player.CharactersWindow.Open();
    }

    /// <summary>
    /// The Items entry: the menu closes and the window opens.
    /// </summary>
    void OpenItemsWindow()
    {
        DebugMenuOpen = false;
        host.Player.ItemsWindow.Open();
    }

    /// <summary>
    /// The console's gigachad mode, the menu's "Gigachad mode (stats 1000)" and "Auto heal" together: <paramref name="field"/> is on / off, or empty to toggle
    /// (on unless both are already on).
    /// </summary>
    internal string DebugGigachadmodeApply(string field)
    {
        DebugFailed = false;
        var world = host.Session.World;
        bool on = Toggled(field, world.Player.StatsTo1000 && world.Player.AutoHeal);
        world.Commands.Enqueue(new DebugGigachadmode(on) { Tick = world.Tick });
        return on
            ? "gigachadmode on: every player character has all stats 1000 and is healed 5 times a second"
            : "gigachadmode off: each character has its own stats back";
    }

    /// <summary>
    /// Runs one simulation tick while the game is paused, so the command the menu just queued takes effect at once (the menu's values then show it);
    /// nothing while the game runs, whose next tick takes it.
    /// </summary>
    internal void ApplyWhilePaused()
    {
        if (!host.Session.Simulation.IsPaused || host.Covered) return;
        host.Session.Step(1);
    }

    /// <summary>
    /// Sets every stat of the targets to 100 (the console's <c>maxstats</c>).
    /// </summary>
    internal string DebugMaxAll()
    {
        DebugFailed = false;
        var targets = DebugTargets(host);
        if (targets.Length == 0) return DebugFail("select or inspect a character first");
        host.Session.World.Commands.Enqueue(new DebugMaxSkills(targets) { Tick = host.Session.World.Tick });
        return $"every stat 100 for {targets.Length}";
    }

    /// <summary>
    /// Takes <c>--maker x</c>, <c>--model x</c> and <c>--level n</c> out of <paramref name="args"/>; null when none is given.
    /// </summary>
    internal static DebugWeaponGrade? TakeGrade(ref IReadOnlyList<string> args)
    {
        string? maker = null, model = null;
        int? level = null;
        var rest = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (i + 1 < args.Count && args[i].Equals("--maker", StringComparison.OrdinalIgnoreCase))
            {
                maker = args[++i];
            }
            else if (i + 1 < args.Count && args[i].Equals("--model", StringComparison.OrdinalIgnoreCase))
            {
                model = args[++i];
            }
            else if (i + 1 < args.Count && args[i].Equals("--level", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out int n))
            {
                level = n;
                i++;
            }
            else
            {
                rest.Add(args[i]);
            }
        }

        args = rest;
        return maker is null && model is null && level is null ? null : new DebugWeaponGrade(maker, model, level);
    }

    /// <summary>
    /// Puts items on the first selected character (the menu's Give item row; the console's <c>give</c>): the record by name, the quantity 1 to 1000,
    /// the quality 0 to 100.
    /// </summary>
    internal string DebugGive(string name, int quantity, int quality, bool equip = false, DebugWeaponGrade? exact = null)
    {
        DebugFailed = false;
        var selected = host.Player.AgentOrders.ControlOrderers().ToArray();
        if (selected.Length == 0) return DebugFail("select a character first");
        quantity = Math.Clamp(quantity, 1, 1000);
        quality = Math.Clamp(quality, 0, 100);
        if (DebugRecord(name, character: false) is not { } item) return DebugFail($"no item record matches '{name}'");
        host.Session.World.Commands.Enqueue(new DebugGiveItem(selected[0], item.Id, quantity,
            quality) { Tick = host.Session.World.Tick, Equip = equip, Exact = exact });
        if (host.Scene.Database is { } db && db.Find(item.Id)?.Type == FcsRecordType.Weapon && DebugGiveItem.Grade(db, quality) is { } grade)
            return $"gave {quantity} x {grade.Name} {item.Name} (level {grade.Level})";
        return $"gave {quantity} x {item.Name}{(DebugHasQuality(item.Id) ? $" (quality {quality})" : "")}";
    }

    /// <summary>
    /// "Katana x5 q80": the name, the quantity (1 without one, at most 1000) and the quality (0 without one, 0 to 100); the suffixes in either order.
    /// </summary>
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

    /// <summary>
    /// Whether item record <paramref name="record"/> takes a quality.
    /// </summary>
    bool DebugHasQuality(string record) => host.Scene.Database?.Find(record) is { } r && DebugGiveItem.QualityTypes.Contains(r.Type);

    /// <summary>
    /// The character (or item) record a typed name means: its string id, the name exactly, else the shortest name containing it (ignoring case).
    /// </summary>
    internal (string Id, string Name)? DebugRecord(string text, bool character)
    {
        text = text.Trim();
        if (text.Length == 0 || host.Scene.Database is not { } db) return null;
        if (_debugMatches.TryGetValue((character, text), out var cached)) return cached;
        bool Fits(FcsRecordType t) => character ? t is FcsRecordType.Character or FcsRecordType.AnimalCharacter : DebugGiveItem.ItemTypes.Contains(t);
        var candidates = db.Records.Values.Where(r => Fits(r.Type));
        var hit = db.Find(text) is { } exact && Fits(exact.Type) ? exact
            : candidates.FirstOrDefault(r => string.Equals(r.Name, text, StringComparison.OrdinalIgnoreCase))
            ?? candidates.Where(r => r.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.Name.Length)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        (string, string)? found = hit is null ? null : (hit.StringId, hit.Name);
        _debugMatches[(character, text)] = found;
        return found;
    }

    /// <summary>
    /// A line of the menu as drawn: its text, whether it is highlighted, whether it is a switch that is on, the row (or category) it belongs to and,
    /// for a line of the open part list, the part's index (-1 otherwise).
    /// </summary>
    readonly record struct MenuLine(string Text, bool Highlight, bool On, int Row, int Part);

    /// <summary>
    /// The menu's lines as last drawn, with their rectangles; a click hit-tests them.
    /// </summary>
    readonly List<(MenuLine Line, Vector4 Rect)> _drawnLines = [];

    /// <summary>
    /// The category view's "back" button's rectangle as last drawn (a click on it goes back to the categories); null at the categories.
    /// </summary>
    Vector4? _backButton;

    /// <summary>
    /// The panel's rectangle as last drawn (x0, y0, x1, y1); a click outside it is not the menu's.
    /// </summary>
    Vector4? _panelRect;

    /// <summary>
    /// Draws the menu at <paramref name="top"/> (on the left, or at the right with <paramref name="atRight"/>): at the categories their titles with how
    /// many switches inside are on, in a category its rows (scrolled to the highlighted one) with a back button, the selection, camera target and money,
    /// the hint with <paramref name="close"/> and the status. <paramref name="scale"/> is the framebuffer's pixels per point (2 on a Retina display):
    /// the text and every size in the menu grow with it. While the faction relations sub-view is open it is drawn instead.
    /// </summary>
    internal void DrawDebugMenu(DebugOverlay o, int width, int height, float top, string close = "F10", bool atRight = false, float scale = 1,
        DebugOverlay.OverlayPass? pass = null)
    {
        if (_relationsEditor.Open)
        {
            _relationsEditor.Draw(o, width, height, top, scale, close, atRight, pass);
            return;
        }

        var world = host.Session.World;
        var here = host.Session.Camera.Current.Target;
        var lines = new List<MenuLine>();
        var onNames = new List<string>();
        host.Modding.EnsureDebugFields();
        DebugMenuAddLines(world, lines, onNames);
        var categories = DebugCategories();
        bool root = _category < 0 || _category >= categories.Length;
        string info = $"{world.Player.Selection.Count} selected   here {here.X:0}, {here.Z:0}   money "
            + $"c.{world.Player.Money.ToString("N0", CultureInfo.InvariantCulture)}";
        string hint = root
            ? $"Up/Down or click pick   Enter open   Esc/{close} close"
            : DebugParts is null
                ? "Up/Down or click pick   type to edit   Enter apply   Esc back"
                : $"Up/Down or click pick {((DebugRow)_debugRow == DebugRow.ClearBounties ? "a faction" : "a part")}   "
                    + $"{((DebugRow)_debugRow == DebugRow.Damage ? "type the damage   " : "")}Enter apply   Esc back";
        var dim = new Vector4(0.7f, 0.75f, 0.8f, 1);
        float pad = 10 * scale, y0 = top + 16 * scale, charWidth = o.CharWidth * scale, lineHeight = o.LineHeight * scale;
        int chars = Math.Max(lines.Count > 0 ? lines.Max(l => l.Text.Length) : 0, Math.Max(info.Length, Math.Max(hint.Length, 30)));
        // The title names the open category, or, in one, every switch that is on (cut with "+N more" to the panel's width).
        string title = root ? $"Debug   ({close} closes)" : $"Debug > {categories[_category].Title}   ({close} closes)";
        if (onNames.Count > 0) title = DebugOnSummary(title, onNames, chars);
        chars = Math.Max(chars, title.Length);
        var status = WrapStatus(_debugStatus, chars, 3);
        float w = chars * charWidth + 2 * pad;
        // The list scrolls when the screen is too low for all of it: the window of lines keeps the highlighted one in view.
        int room = (int)((height - y0 - 4 * pad) / lineHeight) - (root ? 6 : 7);
        int first = 0, count = lines.Count;
        if (count > room && room > 4)
        {
            int at = Math.Max(0, lines.FindIndex(l => l.Highlight));
            first = Math.Clamp(at - room / 2, 0, count - room);
            count = room;
        }
        float h = (count + 5 + (root ? 0 : 1) + status.Count) * lineHeight + 2 * pad;
        float x0 = atRight ? Math.Max(8 * scale, width - w - 16 * scale) : 16 * scale;
        o.Rect(x0, y0, x0 + w, y0 + h, DebugOverlay.PanelColour);
        _panelRect = new Vector4(x0, y0, x0 + w, y0 + h);
        float y = y0 + pad;
        o.TextScaled(title, x0 + pad, y, onNames.Count > 0 ? DebugOnText : DebugOverlay.TextColour, scale);
        y += 1.5f * lineHeight;
        _drawnLines.Clear();
        for (int i = first; i < first + count; i++, y += lineHeight)
        {
            var line = lines[i];
            var (text, highlight, on, _, _) = line;
            if (on) o.Rect(x0 + 4 * scale, y, x0 + w - 4 * scale, y + lineHeight, DebugOnTint);
            if (highlight) o.Rect(x0 + 4 * scale, y, x0 + w - 4 * scale, y + lineHeight, new Vector4(0.25f, 0.4f, 0.65f, 0.6f));
            var rect = new Vector4(x0 + 4 * scale, y, x0 + w - 4 * scale, y + lineHeight);
            DrawHover(o, rect, host.Player.Mouse);
            _drawnLines.Add((line, rect));
            // A switch that is on: green text, a green tint behind and a '*' in the marker column (the cursor's '>' wins on the current row).
            if (!root && on && !highlight && text.StartsWith("  ", StringComparison.Ordinal)) text = "* " + text[2..];
            o.TextScaled(text, x0 + pad, y, on ? DebugOnText : DebugOverlay.TextColour, scale);
        }
        y += 0.5f * lineHeight;
        o.TextScaled(info, x0 + pad, y, dim, scale);
        y += lineHeight;
        _backButton = root ? null : DrawBackButton(o, x0 + pad, y, scale, host.Player.Mouse);
        if (!root) y += lineHeight;
        o.TextScaled(hint, x0 + pad, y, dim, scale);
        for (int k = 0; k < status.Count; k++)
            o.TextScaled(status[k], x0 + pad, y + (1 + k) * lineHeight, new Vector4(1f, 0.9f, 0.4f, 1), scale);
        o.Flush(width, height, null, pass);
    }

    /// <summary>
    /// Whether <paramref name="at"/> lies on the menu as last drawn (the open sub-view's panel while one is open): a click there is the menu's, one
    /// anywhere else is the interface's and the world's.
    /// </summary>
    internal bool DebugMenuContains(Vector2 at)
    {
        if (_relationsEditor.Open) return _relationsEditor.Contains(at);
        return _panelRect is { } rect && Inside(rect, at);
    }

    /// <summary>
    /// A left click on the menu: at the categories a category is opened; in one, the back button goes back as Esc does, a row is picked and, unless it
    /// takes typed text, applied as Enter does (a row with a field is only picked), and a line of the open part list is picked and applied. The caller
    /// checks <see cref="DebugMenuContains"/> first, so a click outside the menu never reaches it.
    /// </summary>
    internal void DebugMenuClick(Vector2 at)
    {
        host.Modding.EnsureDebugFields();
        if (_relationsEditor.Open)
        {
            _relationsEditor.Click(at);
            return;
        }

        if (_backButton is { } back && Inside(back, at))
        {
            CloseCategory();
            return;
        }

        foreach (var (line, rect) in _drawnLines)
        {
            if (!Inside(rect, at)) continue;
            if (DebugAtCategories)
            {
                _categoryCursor = line.Row;
                OpenCategory(line.Row);
                return;
            }

            if (line.Part >= 0 && DebugParts is { } parts)
            {
                _debugPart = Math.Min(line.Part, parts.Length - 1);
                DebugMenuKey(EngineKey.Enter);
                return;
            }

            bool partsOpen = DebugParts is not null;
            DebugParts = null;
            _debugRow = line.Row;
            if (partsOpen) return;   // a click on a row leaves the open part list
            if (!DebugHasField(_debugRow)) DebugMenuKey(EngineKey.Enter);
            return;
        }
    }

    /// <summary>
    /// Whether <paramref name="at"/> lies in <paramref name="rect"/> (x0, y0, x1, y1).
    /// </summary>
    internal static bool Inside(Vector4 rect, Vector2 at) => at.X >= rect.X && at.X <= rect.Z && at.Y >= rect.Y && at.Y <= rect.W;

    /// <summary>
    /// The menu's lines: at the categories one per category (with how many of its switches are on), in a category one per row (with its value and
    /// whether it is on) and the part list of the row picking one; <paramref name="onNames"/> collects the switches that are on.
    /// </summary>
    void DebugMenuAddLines(Simulation.World world, List<MenuLine> lines, List<string> onNames)
    {
        var categories = DebugCategories();
        if (_category < 0 || _category >= categories.Length)
        {
            for (int c = 0; c < categories.Length; c++)
            {
                bool current = c == _categoryCursor;
                int active = DebugActiveCount(categories[c], world);
                string value = categories[c].Open is not null ? "Enter: open the window" : active > 0 ? $"{active} active" : "";
                lines.Add(new((current ? "> " : "  ") + DebugCategoryColumns(categories[c].Title, value), current, active > 0, c, -1));
            }
            return;
        }

        foreach (int i in DebugLayout())
        {
            bool current = i == _debugRow;
            string mark = current && DebugParts is null ? "> " : "  ";
            if (DebugIsTool(i))
            {
                var tool = DebugTools[i - host.Modding.DebugRowCount];
                string state = tool.State();
                bool toolOn = tool.Step is null && (tool.IsSwitch ? state == "on" : state is not ("" or "off"));
                if (toolOn) onNames.Add(DebugShortName(tool.Label));
                lines.Add(new(mark + DebugColumns(tool.Label, tool.Key, state), current && DebugParts is null, toolOn, i, -1));
                continue;
            }
            string value = DebugRowValue(i, current, world, out bool rowOn);
            if (rowOn)
            {
                onNames.Add(DebugShortName(host.Modding.DebugLabel(i))
                    + ((DebugRow)i == DebugRow.DamageMultiplier && i < DebugLabels.Length
                        ? $" x{host.PopData?.BodyOptions.GlobalDamageMultiplier ?? 1:0.##}"
                        : ""));
            }
            string label = i == (int)DebugRow.SetHunger ? Tr("Set hunger (1-300)") : host.Modding.DebugLabel(i);
            string text = mark + DebugColumns(label, "", value);
            lines.Add(new(text, current && DebugParts is null, rowOn, i, -1));
            if (DebugPicksPart(i) && current && DebugParts is { } parts)
            {
                for (int k = 0; k < parts.Length; k++)
                    lines.Add(new((k == _debugPart ? "    > " : "      ") + parts[k].Name, k == _debugPart, false, i, k));
            }
        }
    }

    /// <summary>
    /// How many of a category's rows are switches that are on.
    /// </summary>
    int DebugActiveCount(DebugCategory category, Simulation.World world)
    {
        int count = 0;
        foreach (int row in category.Rows)
        {
            if (DebugRowOn(row, world)) count++;
        }

        return count;
    }

    /// <summary>
    /// Whether row <paramref name="i"/> is a switch that is on (a cheat's live state, or a tool's).
    /// </summary>
    bool DebugRowOn(int i, Simulation.World world)
    {
        if (DebugIsTool(i))
        {
            var tool = DebugTools[i - host.Modding.DebugRowCount];
            string state = tool.State();
            return tool.Step is null && (tool.IsSwitch ? state == "on" : state is not ("" or "off"));
        }

        DebugRowValue(i, false, world, out bool on);
        return on;
    }

    /// <summary>
    /// A row's value column: its typed field (with the record it matches), its live state, or how to pick a part; <paramref name="rowOn"/>:
    /// the row is a switch that is on.
    /// </summary>
    string DebugRowValue(int i, bool current, Simulation.World world, out bool rowOn)
    {
        string value = "";
        rowOn = false;
        if (DebugHasField(i))
        {
            string field = DebugFields[i];
            value = field + (current ? "_" : "");
            if (i < DebugLabels.Length && (DebugRow)i is DebugRow.SpawnNpc or DebugRow.GiveItem && field.Trim().Length > 0)
            {
                value += DebugRecord((DebugRow)i == DebugRow.GiveItem ? DebugItemField(field.Trim()).Name : field,
                    (DebugRow)i == DebugRow.SpawnNpc)
                    is { } r
                    ? $"   -> {r.Name}"
                    : "   -> no match";
            }
        }
        if (i < DebugLabels.Length)
        {
            switch ((DebugRow)i)
            {
                case DebugRow.DamageMultiplier:
                    float multiplier = host.PopData?.BodyOptions.GlobalDamageMultiplier ?? 1;
                    value += $"   now x{multiplier:0.##}";
                    rowOn = multiplier != 1;
                    break;
                case DebugRow.ForceHits:
                    value = host.PopData?.BodyOptions.ForcedHitPart?.Name ?? "off";
                    rowOn = host.PopData?.BodyOptions.ForcedHitPart is not null;
                    break;
                case DebugRow.ExplodeLevel:
                    value = $"{_explodeLevel} {ExplodeLevels.Names[_explodeLevel - 1]}";
                    rowOn = _explodeLevel != ExplodeLevels.Normal;
                    break;
                case DebugRow.StatsTo1000:
                    value = OnOff(world.Player.StatsTo1000);
                    rowOn = world.Player.StatsTo1000;
                    break;
                case DebugRow.AutoHeal:
                    value = OnOff(world.Player.AutoHeal);
                    rowOn = world.Player.AutoHeal;
                    break;
                case DebugRow.InstantMove:
                    value = OnOff(world.Player.InstantMove);
                    rowOn = world.Player.InstantMove;
                    break;
                case DebugRow.OnePunch:
                    value = OnOff(world.Player.OnePunch);
                    rowOn = world.Player.OnePunch;
                    break;
                case DebugRow.NoStumble:
                    value = OnOff(world.Player.NoStumble);
                    rowOn = world.Player.NoStumble;
                    break;
                case DebugRow.Recruit:
                    value = "Enter: recruit and edit";
                    break;
                case DebugRow.FactionRelations:
                    value = "Enter: edit relations";
                    break;
                case DebugRow.ExplodeClick:
                    rowOn = ExplodeClickOn;
                    value = ExplodeClickOn ? $"on{(_explodeClickArgs.Length > 0 ? ", " + _explodeClickArgs : "")}" : "off";
                    break;
                case DebugRow.Guard:
                    rowOn = world.Player.Guard;
                    value = world.Player.Guard ? $"on, {world.Player.GuardRadius:0.#} m{(world.Player.GuardAnimals ? ", animals" : "")}" : "off";
                    break;
            }
        }
        if (DebugPicksPart(i) && current && DebugParts is null)
        {
            value += (value.Length > 0 ? "   " : "")
                + ((DebugRow)i == DebugRow.ClearBounties ? "Enter/Right: pick the faction" : "Enter/Right: pick the body part");
        }
        return value;
    }

    /// <summary>
    /// Word-wraps <paramref name="text"/> to <paramref name="width"/> characters, at most <paramref name="maxLines"/> lines; the last one ends in
    /// "..." when text was left out.
    /// </summary>
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
                if (lines.Count == maxLines)
                {
                    cut = true;
                    break;
                }
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

    /// <summary>
    /// A switch that is on: its text colour (green) and the row's background tint.
    /// </summary>
    static readonly Vector4 DebugOnText = new(0.45f, 1f, 0.5f, 1), DebugOnTint = new(0.1f, 0.5f, 0.15f, 0.35f);

    /// <summary>
    /// A small clickable button's background (the sub-views' "back").
    /// </summary>
    internal static readonly Vector4 ButtonColour = new(0.3f, 0.45f, 0.7f, 0.85f);

    /// <summary>
    /// Draws the sub-views' "back" button at <paramref name="x"/>, <paramref name="y"/> (a click on it steps back, as Esc does); returns its rectangle.
    /// </summary>
    internal static Vector4 DrawBackButton(DebugOverlay o, float x, float y, float scale, Vector2 pointer)
    {
        float pad = 6 * scale, width = 4 * o.CharWidth * scale + 2 * pad, height = o.LineHeight * scale;
        var rect = new Vector4(x, y, x + width, y + height);
        o.Rect(x, y, x + width, y + height, Inside(rect, pointer) ? ButtonHoverColour : ButtonColour);
        o.TextScaled("back", x + pad, y, DebugOverlay.TextColour, scale);
        return rect;
    }

    /// <summary>
    /// The back button's background while the pointer is on it.
    /// </summary>
    internal static readonly Vector4 ButtonHoverColour = new(0.42f, 0.6f, 0.9f, 0.95f);

    /// <summary>
    /// The tint behind a clickable line while the pointer is on it, so the menus show what a click would pick.
    /// </summary>
    internal static readonly Vector4 HoverColour = new(1, 1, 1, 0.12f);

    /// <summary>
    /// Tints <paramref name="rect"/> with <see cref="HoverColour"/> when <paramref name="pointer"/> lies on it.
    /// </summary>
    internal static void DrawHover(DebugOverlay o, Vector4 rect, Vector2 pointer)
    {
        if (Inside(rect, pointer)) o.Rect(rect.X, rect.Y, rect.Z, rect.W, HoverColour);
    }

    /// <summary>
    /// A row label without its hint: "Guard (kill + heal in radius)" is "Guard", "Teleport on map..." is "Teleport on map".
    /// </summary>
    static string DebugShortName(string label)
    {
        label = label.Trim();
        int paren = label.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0) label = label[..paren];
        return label.TrimEnd('.').Trim();
    }

    /// <summary>
    /// The title with " ON: a, b" appended; when it does not fit <paramref name="maxChars"/>, the names that fit then "+N more".
    /// </summary>
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

    /// <summary>
    /// One menu line after its 2-character marker: label, key hint and value, each in its own column so every value starts at the same place.
    /// </summary>
    static string DebugColumns(string label, string key, string value) => $"{label,-DebugLabelWidth} {key,-DebugKeyWidth} {value}".TrimEnd();

    /// <summary>
    /// A category line after its 2-character marker: the title, then how many of its switches are on, in a narrower column than the rows'.
    /// </summary>
    static string DebugCategoryColumns(string title, string value) => value.Length > 0 ? $"{title,-18} {value}" : title;

    /// <summary>
    /// The pick & collision inspector's menu (Meitou only, docs/viewer.md "Pick & collision inspector"). The F10 debug menu's Tools row opens it; it
    /// works like the F10 menu: Up/Down pick a row, Enter or Space switch it, Esc or Enter on Close closes, and while it is open it takes every key.
    /// What was switched on stays on after it closes, so the camera can fly about with the hulls drawn. Its rows' labels.
    /// </summary>
    static readonly string[] InspectorLabels =
    [
        "Info panel (under the cursor, what a click selects)",
        "Pick hull (what a click is tested against)",
        "Collision shapes (each shape's own hull)",
        "Collision mesh (the real triangles, selected building)",
        "Navmesh (off / Meitou's / original / both)",
        "Close",
    ];

    /// <summary>
    /// The inspector's menu is open.
    /// </summary>
    internal bool InspectorMenuOpen;

    /// <summary>
    /// The highlighted row.
    /// </summary>
    int _inspectorRow;

    /// <summary>
    /// Whether inspector switch <paramref name="row"/> (0 to 3) is on.
    /// </summary>
    bool InspectorValue(int row) => row switch
    {
        0 => host.Player.Inspector.InspectorPanel,
        1 => host.Player.Inspector.InspectorHull,
        2 => host.Player.Inspector.InspectorShapes,
        3 => host.Player.Inspector.InspectorMesh,
        _ => false,
    };

    /// <summary>
    /// A key while the inspector's menu is open: Esc closes, Up and Down walk the rows, Enter, Space, Left and Right switch the highlighted one.
    /// </summary>
    internal void InspectorMenuKey(EngineKey key)
    {
        switch (key)
        {
            case EngineKey.Escape:
                InspectorMenuOpen = false;
                break;
            case EngineKey.Up:
                _inspectorRow = (_inspectorRow + InspectorLabels.Length - 1) % InspectorLabels.Length;
                break;
            case EngineKey.Down:
                _inspectorRow = (_inspectorRow + 1) % InspectorLabels.Length;
                break;
            case EngineKey.Enter or EngineKey.Space or EngineKey.Left or EngineKey.Right:
                InspectorSwitch(_inspectorRow);
                break;
        }
    }

    /// <summary>
    /// Switches row <paramref name="row"/>: one of the four views, the navmesh view's next step, or Close; the inspector is told.
    /// </summary>
    void InspectorSwitch(int row)
    {
        switch (row)
        {
            case 0:
                host.Player.Inspector.InspectorPanel = !host.Player.Inspector.InspectorPanel;
                break;
            case 1:
                host.Player.Inspector.InspectorHull = !host.Player.Inspector.InspectorHull;
                break;
            case 2:
                host.Player.Inspector.InspectorShapes = !host.Player.Inspector.InspectorShapes;
                break;
            case 3:
                host.Player.Inspector.InspectorMesh = !host.Player.Inspector.InspectorMesh;
                break;
            case 4:
                host.DebugOverlays.ToggleNavOverlay();
                break;
            default:
                InspectorMenuOpen = false;
                break;
        }
        host.Player.Inspector.InspectorChanged();
    }

    /// <summary>
    /// Draws the inspector's menu at <paramref name="top"/>: each row with its state, the highlighted one marked, and the key hint
    /// (<paramref name="scale"/> and <paramref name="pass"/> as for <see cref="DrawDebugMenu"/>).
    /// </summary>
    internal void DrawInspectorMenu(DebugOverlay o, int width, int height, float top, float scale = 1, DebugOverlay.OverlayPass? pass = null)
    {
        var lines = new List<string>();
        for (int i = 0; i < InspectorLabels.Length; i++)
        {
            string state = i < 4 ? (InspectorValue(i) ? "[x] " : "[ ] ")
                : i == 4 ? $"[{host.DebugOverlays.NavViewMode.ToString().ToLowerInvariant()}] "
                : "    ";
            lines.Add((i == _inspectorRow ? "> " : "  ") + state + InspectorLabels[i]);
        }
        const string hint = "Up/Down pick   Enter/Space switch   Esc close (switches stay on)";
        var dim = new Vector4(0.7f, 0.75f, 0.8f, 1);
        float pad = 10 * scale, x0 = 16 * scale, y0 = top + 16 * scale, lineHeight = o.LineHeight * scale;
        int chars = Math.Max(lines.Max(l => l.Length), hint.Length);
        float w = chars * o.CharWidth * scale + 2 * pad, h = (lines.Count + 4) * lineHeight + 2 * pad;
        o.Rect(x0, y0, x0 + w, y0 + h, DebugOverlay.PanelColour);
        float y = y0 + pad;
        o.TextScaled("Pick & collision inspector", x0 + pad, y, DebugOverlay.TextColour, scale);
        y += 1.5f * lineHeight;
        for (int i = 0; i < lines.Count; i++, y += lineHeight)
        {
            if (i == _inspectorRow)
                o.Rect(x0 + 4 * scale, y, x0 + w - 4 * scale, y + lineHeight, new Vector4(0.25f, 0.4f, 0.65f, 0.6f));
            o.TextScaled(lines[i], x0 + pad, y, DebugOverlay.TextColour, scale);
        }
        o.TextScaled(hint, x0 + pad, y + 0.5f * lineHeight, dim, scale);
        o.Flush(width, height, null, pass);
    }
}
