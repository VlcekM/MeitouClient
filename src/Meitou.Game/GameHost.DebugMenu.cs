using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Meitou.Data.Fcs;
using Meitou.Rendering;
using Meitou.Simulation;
using EngineKey = Meitou.Engine.Input.Key;

namespace Meitou.Game;

/// <summary>
/// The debug menu (F10; Ctrl+F10 on Metal, where F10 is the settings panel; docs/game/ui-input.md "Debug menu"): cheats sent to the world as
/// <see cref="DebugCommand"/>s. Up/Down pick a row, typing fills its field, Enter applies it, Esc or the close key closes; while open it takes every key.
/// Code mods' rows follow the cheats ([modding](docs/modding.md)). Below them a Tools section opens the debug tools (<see cref="DebugTools"/>; a tool row also takes Space, Left and Right). Teleport and spawn use the camera's target (the middle of the screen); heal, max skills and damage the selected characters, else the inspected one. Heal keeps lost limbs; its sub-row Heal fully gives them back. Clear bounties clears the selected characters' (else every player character's), all or one faction's.
/// </summary>
sealed partial class GameHost
{
    enum DebugRow { Teleport, SpawnNpc, GiveItem, Money, Heal, HealFully, MaxSkills, Damage, ForceHits, DamageMultiplier, UnlockResearch, ClearBounties }

    static readonly string[] DebugLabels =
    [
        "Teleport selected here", "Spawn NPC (hostile)", "Give item (name x5 q80)", "Money (+/- cats)", "Heal", "  Heal fully (restore limbs)", "Max out skills", "Damage (cut)",
        "Force hits to", "Damage multiplier", "Unlock all research", "Clear bounties",
    ];
    // The rows' typed text: the vanilla rows', then one per code mod row (EnsureDebugFields).
    readonly List<string> debugFields = ["", "", "", "1000", "", "", "", "20", "", "1", "", ""];
    readonly Dictionary<(bool Character, string Text), (string Id, string Name)?> debugMatches = [];
    bool debugMenuOpen;
    int debugRow;
    string debugStatus = "";
    /// <summary>
    /// The open row's sub-list (Enter or Right opens it, Esc or Left closes it). Damage and Force hits: "Random" or "Off", then the first target's parts as (template id, name);
    /// Clear bounties: "All factions", then each faction with a bounty as (faction index, name and total).
    /// </summary>
    (string? Id, string Name)[]? debugParts;
    int debugPart;
    // The overlays the menu's tool rows switch, also on their own keys (F11, Ctrl+F9, F12).
    bool statsVisible, fpsVisible;
    FrameProfiler? profiler;
    /// <summary>Whether the Tools section is shown: the Vulkan game loop has these tools, the Metal one does not.</summary>
    bool debugToolsOn;

    /// <summary>A tool row: its label, its state for the row ("x", "off", "" for none) and what Enter does.</summary>
    readonly record struct DebugTool(string Label, Func<string> State, Action Switch);

    /// <summary>The tool rows below the cheats: each opens a tool or switches an overlay; a new tool adds its row here.</summary>
    DebugTool[] DebugTools => debugTools ??=
    [
        new("Pick & collision inspector...", () => "", () => { debugMenuOpen = false; inspectorMenuOpen = true; }),
        new("Interior editor...", () => "", OpenInteriorEditor),
        new("Level editor...      (Shift+F12)", () => "", OpenLevelEditor),
        new("Teleport on map...", () => "", BeginMapTeleport),
        new("Navmesh view        (Shift+F11)", () => navView.ToString().ToLowerInvariant(), ToggleNavOverlay),
        new("Frame statistics    (F11)", () => statsVisible ? "x" : " ", () => { statsVisible = !statsVisible; FoliageGpuCull.CountTriangles = statsVisible; }),
        new("Fps counter         (Ctrl+F9)", () => fpsVisible ? "x" : " ", () => fpsVisible = !fpsVisible),
        new("VRAM pie            (Ctrl+F12)", () => profiler?.ShowVram == true ? "x" : " ", () => { if (profiler is not null) profiler.ShowVram = !profiler.ShowVram; }),
        new("Profiler            (F12)", () => (profiler?.Showing ?? FrameProfiler.Mode.Off).ToString().ToLowerInvariant(),
            () => { if (profiler is not null) profiler.Showing = (FrameProfiler.Mode)(((int)profiler.Showing + 1) % 3); }),
    ];
    DebugTool[]? debugTools;

    /// <summary>The "Teleport on map" tool: closes the menu, opens the Map tab and waits for a click; the selected characters go there (the camera too, as the row "Teleport selected here" leaves it where it is).</summary>
    void BeginMapTeleport()
    {
        if (session.World.Player.Selection.Count == 0) { debugStatus = "select characters first"; return; }
        if (!player.BeginMapPick(TeleportToMapPoint)) { debugStatus = "the interface is hidden (F7)"; return; }
        debugMenuOpen = false;
        debugStatus = "";
    }

    void TeleportToMapPoint(Vector3 at)
    {
        debugStatus = DebugApply(DebugRow.Teleport, "", at: at);
        if (debugFailed) return;
        CentreCameraOn(at.X, at.Z);
    }

    /// <summary>Puts the camera's target on world X Z, keeping its angle and distance (the characters just teleported there; a followed one is followed on).</summary>
    void CentreCameraOn(float x, float z)
    {
        var view = session.Camera.Current;
        session.Camera.Place(new Vector2(x, z), view.Yaw, view.Pitch, view.Distance);
    }

    /// <summary>The one-line hint while the Map tab waits for a teleport click (<see cref="BeginMapTeleport"/>).</summary>
    void DrawMapPickHint(DebugOverlay o, int width, int height)
    {
        const string text = "Teleport: click the map, Esc cancels";
        float w = text.Length * o.CharWidth + 20, x = (width - w) / 2;
        o.Rect(x, 8, x + w, 8 + o.LineHeight + 8, DebugOverlay.PanelColour);
        o.Text(text, x + 10, 12, new Vector4(1f, 0.9f, 0.4f, 1));
        o.Flush(width, height);
    }

    /// <summary>The rows of the menu: the cheats, the code mods' rows (<see cref="DebugRowCount"/>), then the tool rows.</summary>
    int DebugMenuRows => DebugRowCount + (debugToolsOn ? DebugTools.Length : 0);
    bool DebugIsTool(int row) => row >= DebugRowCount;

    bool DebugHasField(int row) => !DebugIsTool(row) && (row >= DebugLabels.Length ? ModDebugRows[row - DebugLabels.Length].HasField
        : (DebugRow)row is not (DebugRow.Teleport or DebugRow.Heal or DebugRow.HealFully or DebugRow.MaxSkills or DebugRow.ForceHits or DebugRow.UnlockResearch or DebugRow.ClearBounties));

    /// <summary>The rows that open the body part list.</summary>
    bool DebugPicksPart(int row) => !DebugIsTool(row) && (DebugRow)row is DebugRow.Damage or DebugRow.ForceHits or DebugRow.ClearBounties;


    void DebugMenuKey(EngineKey key)
    {
        EnsureDebugFields();
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
            case EngineKey.Enter or EngineKey.Right when DebugPicksPart(debugRow):
                debugParts = (DebugRow)debugRow == DebugRow.ClearBounties ? DebugBountyList() : DebugPartList();
                if (debugParts is not null) debugPart = Math.Min(debugPart, debugParts.Length - 1);
                break;
            case EngineKey.Escape: debugMenuOpen = false; break;
            case EngineKey.Up: debugRow = (debugRow + DebugMenuRows - 1) % DebugMenuRows; break;
            case EngineKey.Down: debugRow = (debugRow + 1) % DebugMenuRows; break;
            case EngineKey.Enter or EngineKey.Space or EngineKey.Left or EngineKey.Right when DebugIsTool(debugRow): DebugTools[debugRow - DebugRowCount].Switch(); break;
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
        var table = session.World.Characters;
        var body = table.TryResolveNext(first, out int slot) ? table.Cold(slot)?.Medical?.Parts : null;
        var parts = (body ?? []).Select(p => ((string?)p.Template.StringId, p.Template.Name.Length > 0 ? p.Template.Name : p.Template.StringId));
        return [(null, (DebugRow)debugRow == DebugRow.Damage ? "Random (as a blow)" : "Off (as a blow)"), .. parts];
    }

    /// <summary>The Clear bounties list: every faction with a bounty on the selected characters (else on any player character), with its total.</summary>
    (string? Id, string Name)[]? DebugBountyList()
    {
        var table = session.World.Characters;
        var selected = session.World.Player.Selection.ToArray();
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

    void DebugMenuText(char c)
    {
        EnsureDebugFields();
        if (DebugHasField(debugRow) && !char.IsControl(c)) debugFields[debugRow] += c;
    }

    /// <summary>Heal, max skills and damage: the selected characters, else the inspected one.</summary>
    CharacterId[] DebugTargets()
    {
        var player = session.World.Player;
        var selected = this.player.ControlOrderers().ToArray();   // includes a select issued this tick (--setup / --exec run commands in one frame)
        return selected.Length > 0 ? selected : player.Inspected.IsNone ? [] : [player.Inspected];
    }

    /// <summary>Set by <see cref="DebugFail"/> while <see cref="DebugApply"/> runs: the last action did not happen (the console prints it as an error).</summary>
    bool debugFailed;

    string DebugFail(string message)
    {
        debugFailed = true;
        return message;
    }

    /// <summary>
    /// The one implementation of the cheats, for the menu's rows and the dev console's commands (<see cref="debugFailed"/> says whether it worked). <paramref name="at"/> is where teleport and spawn go
    /// (the camera's target, the middle of the screen, without it).
    /// </summary>
    string DebugApply(DebugRow row, string field, (string? Id, string Name) part = default, Vector3? at = null)
    {
        debugFailed = false;
        var world = session.World;
        long tick = world.Tick;
        var selected = player.ControlOrderers().ToArray();
        var targets = DebugTargets();
        var here = at ?? session.Camera.Current.Target;
        if ((int)row >= DebugLabels.Length) return ModDebugApply((int)row - DebugLabels.Length, field, selected, here, tick);
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
            case DebugRow.UnlockResearch:
                world.Commands.Enqueue(new DebugUnlockResearch { Tick = tick });
                return "every research finished";
        }
        return "";
    }


    /// <summary>Puts items on the first selected character (the menu's Give item row; the console's <c>give</c>): the record by name, the quantity 1 to 1000, the quality 0 to 100.</summary>
    string DebugGive(string name, int quantity, int quality)
    {
        debugFailed = false;
        var selected = player.ControlOrderers().ToArray();
        if (selected.Length == 0) return DebugFail("select a character first");
        quantity = Math.Clamp(quantity, 1, 1000);
        quality = Math.Clamp(quality, 0, 100);
        if (DebugRecord(name, character: false) is not { } item) return DebugFail($"no item record matches '{name}'");
        session.World.Commands.Enqueue(new DebugGiveItem(selected[0], item.Id, quantity, quality) { Tick = session.World.Tick });
        if (scene.Database is { } db && db.Find(item.Id)?.Type == Meitou.Data.Fcs.FcsRecordType.WEAPON && DebugGiveItem.Grade(db, quality) is { } grade)
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

    bool DebugHasQuality(string record) => scene.Database?.Find(record) is { } r && DebugGiveItem.QualityTypes.Contains(r.Type);

    /// <summary>The character (or item) record a typed name means: its string id, the name exactly, else the shortest name containing it (ignoring case).</summary>
    (string Id, string Name)? DebugRecord(string text, bool character)
    {
        text = text.Trim();
        if (text.Length == 0 || scene.Database is not { } db) return null;
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

    void DrawDebugMenu(DebugOverlay o, int width, int height, float top, string close = "F10")
    {
        var world = session.World;
        var here = session.Camera.Current.Target;
        var lines = new List<string>();
        int highlight = 0;
        EnsureDebugFields();
        for (int i = 0; i < DebugRowCount; i++)
        {
            if (i == debugRow) highlight = lines.Count;
            string line = (i == debugRow && debugParts is null ? "> " : "  ") + DebugLabel(i);
            if (DebugHasField(i))
            {
                string field = debugFields[i];
                line = $"{line,-26} {field}{(i == debugRow ? "_" : "")}";
                if ((DebugRow)i is DebugRow.SpawnNpc or DebugRow.GiveItem && field.Trim().Length > 0)
                    line += DebugRecord((DebugRow)i == DebugRow.GiveItem ? DebugItemField(field.Trim()).Name : field, (DebugRow)i == DebugRow.SpawnNpc) is { } r ? $"   -> {r.Name}" : "   -> no match";
            }
            if ((DebugRow)i == DebugRow.DamageMultiplier) line += $"   now x{popData?.BodyOptions.GlobalDamageMultiplier ?? 1:0.##}";
            if ((DebugRow)i == DebugRow.ForceHits) line = $"{line,-26} {popData?.BodyOptions.ForcedHitPart?.Name ?? "off"}";
            if (DebugPicksPart(i) && i == debugRow && debugParts is null) line += (DebugRow)i == DebugRow.ClearBounties ? "   Enter/Right: pick the faction" : "   Enter/Right: pick the body part";
            lines.Add(line);
            if (DebugPicksPart(i) && i == debugRow && debugParts is { } parts)
                for (int k = 0; k < parts.Length; k++)
                {
                    if (k == debugPart) highlight = lines.Count;
                    lines.Add((k == debugPart ? "    > " : "      ") + parts[k].Name);
                }
        }
        int toolsAt = lines.Count;
        if (debugToolsOn)
            for (int i = 0; i < DebugTools.Length; i++)
            {
                if (DebugRowCount + i == debugRow) highlight = lines.Count;
                string state = DebugTools[i].State();
                lines.Add((DebugRowCount + i == debugRow ? "> " : "  ") + (state.Length == 0 ? "    " : $"[{state}] ") + DebugTools[i].Label);
            }
        string info = $"{world.Player.Selection.Count} selected   here {here.X:0}, {here.Z:0}   money c.{world.Player.Money.ToString("N0", CultureInfo.InvariantCulture)}";
        string hint = debugParts is null ? $"Up/Down pick   type to edit   Enter apply / switch   Esc/{close} close"
            : $"Up/Down pick {((DebugRow)debugRow == DebugRow.ClearBounties ? "a faction" : "a part")}   {((DebugRow)debugRow == DebugRow.Damage ? "type the damage   " : "")}Enter apply   Esc/Left back";
        var dim = new Vector4(0.7f, 0.75f, 0.8f, 1);
        float pad = 10, x0 = 16, y0 = top + 16;
        int chars = Math.Max(lines.Max(l => l.Length), Math.Max(info.Length, Math.Max(hint.Length, debugStatus.Length)));
        float w = chars * o.CharWidth + 2 * pad, h = (lines.Count + (debugToolsOn ? 6 : 5)) * o.LineHeight + 2 * pad;
        o.Rect(x0, y0, x0 + w, y0 + h, DebugOverlay.PanelColour);
        float y = y0 + pad;
        o.Text($"Debug   ({close} closes)", x0 + pad, y, DebugOverlay.TextColour);
        y += 1.5f * o.LineHeight;
        for (int i = 0; i < lines.Count; i++, y += o.LineHeight)
        {
            if (debugToolsOn && i == toolsAt)
            {
                o.Text("Tools", x0 + pad, y + 0.25f * o.LineHeight, dim);
                y += 1.25f * o.LineHeight;
            }
            if (i == highlight) o.Rect(x0 + 4, y, x0 + w - 4, y + o.LineHeight, new Vector4(0.25f, 0.4f, 0.65f, 0.6f));
            o.Text(lines[i], x0 + pad, y, DebugOverlay.TextColour);
        }
        y += 0.5f * o.LineHeight;
        o.Text(info, x0 + pad, y, dim);
        o.Text(hint, x0 + pad, y + o.LineHeight, dim);
        if (debugStatus.Length > 0) o.Text(debugStatus, x0 + pad, y + 2 * o.LineHeight, new Vector4(1f, 0.9f, 0.4f, 1));
        o.Flush(width, height);
    }
}
