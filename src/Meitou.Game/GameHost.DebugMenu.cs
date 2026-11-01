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
/// Teleport and spawn use the camera's target (the middle of the screen); heal, max skills and damage the selected characters, else the inspected one.
/// </summary>
sealed partial class GameHost
{
    enum DebugRow { Teleport, SpawnNpc, GiveItem, Money, Heal, MaxSkills, Damage, ForceHits, DamageMultiplier, UnlockResearch }

    static readonly string[] DebugLabels = ["Teleport selected here", "Spawn NPC (hostile)", "Give item (name x5 q80)", "Money (+/- cats)", "Heal", "Max out skills", "Damage (cut)", "Force hits to", "Damage multiplier", "Unlock all research"];
    readonly string[] debugFields = ["", "", "", "1000", "", "", "20", "", "1", ""];
    readonly Dictionary<(bool Character, string Text), (string Id, string Name)?> debugMatches = [];
    bool debugMenuOpen;
    int debugRow;
    string debugStatus = "";
    /// <summary>The Damage row's body part list (Enter or Right opens it, Esc or Left closes it): "Random", then the first target's parts as (template id, name).</summary>
    (string? Id, string Name)[]? debugParts;
    int debugPart;

    static bool DebugHasField(int row) => (DebugRow)row is not (DebugRow.Teleport or DebugRow.Heal or DebugRow.MaxSkills or DebugRow.ForceHits or DebugRow.UnlockResearch);

    /// <summary>The rows that open the body part list.</summary>
    static bool DebugPicksPart(int row) => (DebugRow)row is DebugRow.Damage or DebugRow.ForceHits;


    void DebugMenuKey(EngineKey key)
    {
        string field = debugFields[debugRow];
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
                if (DebugTargets() is not [var first, ..]) { debugStatus = "select or inspect a character first"; break; }
                var table = session.World.Characters;
                var body = table.TryResolveNext(first, out int slot) ? table.Cold(slot)?.Medical?.Parts : null;
                debugParts = [(null, (DebugRow)debugRow == DebugRow.Damage ? "Random (as a blow)" : "Off (as a blow)"), .. (body ?? []).Select(p => ((string?)p.Template.StringId, p.Template.Name.Length > 0 ? p.Template.Name : p.Template.StringId))];
                debugPart = Math.Min(debugPart, debugParts.Length - 1);
                break;
            case EngineKey.Escape: debugMenuOpen = false; break;
            case EngineKey.Up: debugRow = (debugRow + DebugLabels.Length - 1) % DebugLabels.Length; break;
            case EngineKey.Down: debugRow = (debugRow + 1) % DebugLabels.Length; break;
            case EngineKey.Backspace when field.Length > 0: debugFields[debugRow] = field[..^1]; break;
            case EngineKey.Enter: debugStatus = DebugApply((DebugRow)debugRow, field.Trim()); break;
        }
    }

    void DebugMenuText(char c)
    {
        if (DebugHasField(debugRow) && !char.IsControl(c)) debugFields[debugRow] += c;
    }

    /// <summary>Heal, max skills and damage: the selected characters, else the inspected one.</summary>
    CharacterId[] DebugTargets()
    {
        var player = session.World.Player;
        var selected = player.Selection.ToArray();
        return selected.Length > 0 ? selected : player.Inspected.IsNone ? [] : [player.Inspected];
    }

    string DebugApply(DebugRow row, string field, (string? Id, string Name) part = default)
    {
        var world = session.World;
        long tick = world.Tick;
        var selected = world.Player.Selection.ToArray();
        var targets = DebugTargets();
        var here = session.Camera.Current.Target;
        switch (row)
        {
            case DebugRow.Teleport:
                if (selected.Length == 0) return "select characters first";
                world.Commands.Enqueue(new DebugTeleport(selected, here) { Tick = tick });
                return $"teleported {selected.Length} to {here.X:0}, {here.Z:0}";
            case DebugRow.SpawnNpc:
                if (DebugRecord(field, character: true) is not { } npc) return $"no character record matches '{field}'";
                world.Commands.Enqueue(new DebugSpawn(npc.Id, new Vector2(here.X, here.Z)) { Tick = tick });
                return $"spawned {npc.Name} at {here.X:0}, {here.Z:0}";
            case DebugRow.GiveItem:
                if (selected.Length == 0) return "select a character first";
                var (name, quantity, quality) = DebugItemField(field);
                if (DebugRecord(name, character: false) is not { } item) return $"no item record matches '{name}'";
                world.Commands.Enqueue(new DebugGiveItem(selected[0], item.Id, quantity, quality) { Tick = tick });
                if (scene.Database is { } db && db.Find(item.Id)?.Type == Meitou.Data.Fcs.FcsRecordType.WEAPON && DebugGiveItem.Grade(db, quality) is { } grade)
                    return $"gave {quantity} x {grade.Name} {item.Name} (level {grade.Level})";
                return $"gave {quantity} x {item.Name}{(DebugHasQuality(item.Id) ? $" (quality {quality})" : "")}";
            case DebugRow.Money:
                if (!long.TryParse(field, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long delta)) return "money: a whole number, e.g. 500 or -500";
                world.Commands.Enqueue(new DebugMoney(delta) { Tick = tick });
                return $"money {delta.ToString("+#;-#;0", CultureInfo.InvariantCulture)}";
            case DebugRow.Heal:
                if (targets.Length == 0) return "select or inspect a character first";
                world.Commands.Enqueue(new DebugHeal(targets) { Tick = tick });
                return $"healed {targets.Length}";
            case DebugRow.MaxSkills:
                if (targets.Length == 0) return "select or inspect a character first";
                world.Commands.Enqueue(new DebugMaxSkills(targets) { Tick = tick });
                return $"every stat 100 for {targets.Length}";
            case DebugRow.Damage:
                if (targets.Length == 0) return "select or inspect a character first";
                if (!float.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out float cut) || !(cut > 0)) return "damage: a positive number";
                world.Commands.Enqueue(new DebugDamage(targets, cut, part.Id) { Tick = tick });
                return $"{cut:0.#} cut damage to {targets.Length}{(part.Id is null ? "" : $" on {part.Name}")}";
            case DebugRow.ForceHits:
                world.Commands.Enqueue(new DebugForceHits(part.Id is { } forced ? (forced, part.Name ?? forced) : null) { Tick = tick });
                return part.Id is null ? "blows land as usual" : $"every blow lands on {part.Name} (by name on other bodies)";
            case DebugRow.DamageMultiplier:
                if (!float.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out float multiplier) || !(multiplier >= 0) || multiplier > 100) return "damage multiplier: a number from 0 to 100 (new game: 0.5 to 4)";
                world.Commands.Enqueue(new DebugDamageMultiplier(multiplier) { Tick = tick });
                return $"damage x{multiplier:0.##} (crossbows when next equipped)";
            case DebugRow.UnlockResearch:
                world.Commands.Enqueue(new DebugUnlockResearch { Tick = tick });
                return "every research finished";
        }
        return "";
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
        for (int i = 0; i < DebugLabels.Length; i++)
        {
            if (i == debugRow) highlight = lines.Count;
            string line = (i == debugRow && debugParts is null ? "> " : "  ") + DebugLabels[i];
            if (DebugHasField(i))
            {
                string field = debugFields[i];
                line = $"{line,-26} {field}{(i == debugRow ? "_" : "")}";
                if ((DebugRow)i is DebugRow.SpawnNpc or DebugRow.GiveItem && field.Trim().Length > 0)
                    line += DebugRecord((DebugRow)i == DebugRow.GiveItem ? DebugItemField(field.Trim()).Name : field, (DebugRow)i == DebugRow.SpawnNpc) is { } r ? $"   -> {r.Name}" : "   -> no match";
            }
            if ((DebugRow)i == DebugRow.DamageMultiplier) line += $"   now x{popData?.BodyOptions.GlobalDamageMultiplier ?? 1:0.##}";
            if ((DebugRow)i == DebugRow.ForceHits) line = $"{line,-26} {popData?.BodyOptions.ForcedHitPart?.Name ?? "off"}";
            if (DebugPicksPart(i) && i == debugRow && debugParts is null) line += "   Enter/Right: pick the body part";
            lines.Add(line);
            if (DebugPicksPart(i) && i == debugRow && debugParts is { } parts)
                for (int k = 0; k < parts.Length; k++)
                {
                    if (k == debugPart) highlight = lines.Count;
                    lines.Add((k == debugPart ? "    > " : "      ") + parts[k].Name);
                }
        }
        string info = $"{world.Player.Selection.Count} selected   here {here.X:0}, {here.Z:0}   money c.{world.Player.Money.ToString("N0", CultureInfo.InvariantCulture)}";
        string hint = debugParts is null ? $"Up/Down pick   type to edit   Enter apply   Esc/{close} close"
            : $"Up/Down pick a part   {((DebugRow)debugRow == DebugRow.Damage ? "type the damage   " : "")}Enter apply   Esc/Left back";
        var dim = new Vector4(0.7f, 0.75f, 0.8f, 1);
        float pad = 10, x0 = 16, y0 = top + 16;
        int chars = Math.Max(lines.Max(l => l.Length), Math.Max(info.Length, Math.Max(hint.Length, debugStatus.Length)));
        float w = chars * o.CharWidth + 2 * pad, h = (lines.Count + 5) * o.LineHeight + 2 * pad;
        o.Rect(x0, y0, x0 + w, y0 + h, DebugOverlay.PanelColour);
        float y = y0 + pad;
        o.Text($"Debug   ({close} closes)", x0 + pad, y, DebugOverlay.TextColour);
        y += 1.5f * o.LineHeight;
        for (int i = 0; i < lines.Count; i++, y += o.LineHeight)
        {
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
