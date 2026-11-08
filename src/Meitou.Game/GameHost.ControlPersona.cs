using System.Text;
using System.Text.Json;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Game.DevTools;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>Item search of the agent control channel (docs/procedures/playtest/agent-play-api.md, "Personas"): <c>find-item</c> lists item records with their key, name, type and numbers.</summary>
sealed partial class GameHost
{
    void RegisterControlPersonaCommands(Action<string, string, string, Action<IReadOnlyList<string>, DevConsole>> add, Func<DevConsole, bool> loaded)
    {
        add("unequip", "unequip <slot | item name>", "(backpack: removed and destroyed with its contents, never dropped) the first selected character takes a worn item off (slot: head, shirt, armour, legs, boots, back, hip, belt; or the item's name): it goes to the inventory grids; the reply cannot tell whether they had room, observe.squad[].inventory shows where it is", (args, con) =>
        {
            if (!loaded(con)) return;
            if (args.Count == 0) { con.Error("usage: unequip <slot | item name>"); return; }
            string what = string.Join(' ', args);
            if (what.Equals("backpack", StringComparison.OrdinalIgnoreCase) || what.Equals("pack", StringComparison.OrdinalIgnoreCase))
            {
                var who = player.ControlOrderers();
                if (who.Count == 0) { con.Error("refused: no character selected (use select first)"); return; }
                session.World.Commands.Enqueue(new DebugStripItems(who[0], true) { Tick = session.World.Tick });
                session.ApplyPausedCommandsNow();
                con.Print("unequip backpack: the worn backpack is removed and destroyed with its contents (it is never dropped and never put into the grids)");
                return;
            }
            if (player.ControlUnequip(string.Join(' ', args), out string m)) con.Print(m); else con.Error(m);
        });

        add("strip-inventory", "strip-inventory", "the first selected character loses EVERY carried item: grids, worn clothing and weapons, the backpack with its contents are destroyed (prosthetic limbs and locked shackles stay); nothing is dropped; for test characters before a persona", (args, con) =>
        {
            if (!loaded(con)) return;
            var who = player.ControlOrderers();
            if (who.Count == 0) { con.Error("refused: no character selected (use select first)"); return; }
            session.World.Commands.Enqueue(new DebugStripItems(who[0], false) { Tick = session.World.Tick });
            session.ApplyPausedCommandsNow();
            con.Print($"strip-inventory: every carried item of {who[0]} destroyed (nothing dropped)");
        });

        add("find-item", "find-item <text> [--type armour|weapon|item|crossbow|container] [--limit n] [--stats]", "search the item records (the ones give can hand out) whose name or key contains the text, ignoring case; reply data.items[] has record (a key give and equip take), name, type and, with --stats, the record's numbers (floats, ints) and the names its references point to; default limit 20, at most 200", (args, con) =>
        {
            if (!loaded(con)) return;
            string text = "";
            string type = "";
            int limit = 20;
            bool stats = false;
            for (int a = 0; a < args.Count; a++)
            {
                if (args[a].Equals("--type", StringComparison.OrdinalIgnoreCase) && a + 1 < args.Count) type = args[++a].ToLowerInvariant();
                else if (args[a].Equals("--limit", StringComparison.OrdinalIgnoreCase) && a + 1 < args.Count && int.TryParse(args[a + 1], out int n)) { limit = Math.Clamp(n, 1, 200); a++; }
                else if (args[a].Equals("--stats", StringComparison.OrdinalIgnoreCase)) stats = true;
                else text = text.Length == 0 ? args[a] : text + " " + args[a];
            }
            FcsRecordType? only = type switch
            {
                "armour" or "armor" => FcsRecordType.ARMOUR,
                "weapon" => FcsRecordType.WEAPON,
                "item" => FcsRecordType.ITEM,
                "crossbow" => FcsRecordType.CROSSBOW,
                "container" => FcsRecordType.CONTAINER,
                _ => null,
            };
            if (type.Length > 0 && only is null) { con.Error("find-item: --type is armour, weapon, item, crossbow or container"); return; }
            if (scene.Database is not { } db) { con.Error("find-item: no game data loaded"); return; }
            var hits = db.Records.Values
                .Where(r => DebugGiveItem.ItemTypes.Contains(r.Type) && (only is null || r.Type == only) && r.Name.Length > 0
                    && (text.Length == 0 || r.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || r.StringId.Contains(text, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.StringId, StringComparer.Ordinal).ToList();
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteNumber("total", hits.Count);
                w.WriteStartArray("items");
                foreach (var r in hits.Take(limit))
                {
                    w.WriteStartObject();
                    w.WriteString("record", r.StringId);
                    w.WriteString("name", r.Name);
                    w.WriteString("type", r.Type.ToString());
                    if (stats)
                    {
                        w.WriteStartObject("numbers");
                        foreach (var (k, v) in r.Floats) w.WriteNumber(k, v);
                        foreach (var (k, v) in r.Ints) w.WriteNumber(k, v);
                        w.WriteEndObject();
                        w.WriteStartObject("refs");
                        foreach (string k in r.ReferenceLists)
                            w.WriteString(k, string.Join("; ", r.GetReferences(k).Select(x => $"{(db.Find(x.TargetStringId) is { } t ? t.Name : x.TargetStringId)} {x.Values.Value0},{x.Values.Value1},{x.Values.Value2}")));
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            controlData = Encoding.UTF8.GetString(ms.ToArray());
            con.Print($"{hits.Count} item records match '{text}'{(hits.Count > limit ? $", {limit} shown" : "")}");
        });
    }
}
