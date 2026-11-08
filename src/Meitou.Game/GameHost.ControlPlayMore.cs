using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Game.DevTools;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>More commands of the agent control channel's play API (docs/procedures/agent-play-api.md): work, prospect, equip, carry, trade, spawn-at, ko, and the --id target of hurt and kill.</summary>
sealed partial class GameHost
{
    /// <summary>A <c>spawn-at</c> waiting for its character to show up in the snapshot (a running game) and, with --ko or --dead, to go down.</summary>
    sealed class ControlSpawn(string id, HashSet<CharacterId> before, float x, float z, bool ko, bool dead, long deadline)
    {
        public string Id = id; public HashSet<CharacterId> Before = before; public float X = x, Z = z; public bool Ko = ko, Dead = dead, Downed; public long Deadline = deadline; public CharacterId Found = CharacterId.None; public string? Weapon; public bool Aggro, Armed;
    }

    readonly List<ControlSpawn> controlSpawns = [];

    /// <summary>The characters a command with an optional <c>--id &lt;c:slot.gen&gt;</c> means: that one, else the selection. False with an error printed.</summary>
    bool ControlTargets(IReadOnlyList<string> args, DevConsole con, string verb, out CharacterId[] who, out List<string> rest)
    {
        who = [];
        rest = [];
        CharacterId? one = null;
        for (int a = 0; a < args.Count; a++)
        {
            if (args[a].Equals("--id", StringComparison.OrdinalIgnoreCase))
            {
                if (a + 1 >= args.Count || !PlayerInterface.TryParseCharacter(args[a + 1], out var id)) { con.Error($"{verb}: --id needs a character id (c:<slot>.<gen>)"); return false; }
                one = id; a++;
            }
            else rest.Add(args[a]);
        }
        if (one is { } target)
        {
            if (!session.CurrentSnapshot.Characters.Any(c => c.Id == target)) { con.Error($"{verb}: no character c:{target.Slot}.{target.Generation}"); return false; }
            who = [target];
            return true;
        }
        who = player.ControlOrderers().ToArray();
        if (who.Length == 0) { con.Error($"{verb}: select characters first (or pass --id <c:slot.gen>)"); return false; }
        return true;
    }

    /// <summary><c>kill --radius &lt;m&gt; [&lt;x&gt; &lt;z&gt;] [--animals]</c>: one DebugKill for every living, non-player character within the radius of the centre (the given point, else the selected or first squad member), from one pass over the snapshot.</summary>
    void ControlKillArea(IReadOnlyList<string> args, DevConsole con)
    {
        const string usage = "usage: kill --radius <m> [<x> <z>] [--animals]";
        float radius = 0;
        bool animals = false;
        bool haveRadius = false;
        var numbers = new List<float>();
        for (int a = 0; a < args.Count; a++)
        {
            if (args[a].Equals("--animals", StringComparison.OrdinalIgnoreCase)) animals = true;
            else if (args[a].Equals("--radius", StringComparison.OrdinalIgnoreCase) && !haveRadius && a + 1 < args.Count && Num(args[a + 1], out radius)) { haveRadius = true; a++; }
            else if (Num(args[a], out float n)) numbers.Add(n);
            else { con.Error(usage); return; }
        }
        if (!haveRadius || numbers.Count is not (0 or 2)) { con.Error(usage); return; }
        if (radius <= 0 || radius > 2000) { con.Error("kill: --radius must be above 0 and at most 2000 metres"); return; }
        var snap = session.CurrentSnapshot;
        float cx, cz;
        if (numbers.Count == 2) (cx, cz) = (numbers[0], numbers[1]);
        else
        {
            var lead = snap.Characters.FirstOrDefault(c => c.IsPlayer && c.Selected) ?? snap.Characters.FirstOrDefault(c => c.IsPlayer);
            if (lead is null) { con.Error("kill: no squad member to centre on (pass <x> <z>)"); return; }
            (cx, cz) = (lead.Position.X, lead.Position.Z);
        }
        int own = session.World.Player.Faction;
        var ids = new List<CharacterId>();
        foreach (var c in snap.Characters)
        {
            if (!DebugKillArea.Hits(c.IsPlayer, c.Faction, own, c.Animal, c.Body is { Dead: true }, animals)) continue;
            if (Vector2.Distance(new Vector2(c.Position.X, c.Position.Z), new Vector2(cx, cz)) > radius) continue;
            ids.Add(c.Id);
        }
        if (ids.Count > 0) session.World.Commands.Enqueue(new DebugKill(ids) { Tick = session.World.Tick });
        var shown = ids.Take(20).Select(i => $"\"c:{i.Slot}.{i.Generation}\"");
        con.Print($"{{\"killed\":{ids.Count},\"radius\":{radius.ToString(CultureInfo.InvariantCulture)},\"ids\":[{string.Join(",", shown)}],\"idsTruncated\":{(ids.Count > 20 ? "true" : "false")}}}");
    }

    void RegisterControlPlayMoreCommands(Action<string, string, string, Action<IReadOnlyList<string>, DevConsole>> add, Func<DevConsole, bool> loaded)
    {
        void Result(DevConsole con, bool ok, string message) { if (ok) con.Print(message); else con.Error(message); }
        bool Character(string word, DevConsole con, string verb, out CharacterId id)
        {
            id = CharacterId.None;
            if (word.StartsWith("c:", StringComparison.OrdinalIgnoreCase) && PlayerInterface.TryParseCharacter(word, out id)) return true;
            con.Error($"{verb}: '{word}' is not a character id (c:<slot>.<gen>, from observe)");
            return false;
        }

        add("work", "work <site id>", "the selected characters build one of your construction sites, or repair a complete damaged building (BuildOrder); ids are observe.buildings.own[].id (b:...)", (args, con) =>
        {
            if (!loaded(con)) return;
            if (args.Count != 1) { con.Error("usage: work <site id>"); return; }
            Result(con, player.ControlWork(args[0], out string m), m);
        });

        add("prospect", "prospect", "the first selected character prospects where it stands (the Prospect button); about a game second later observe.resources holds the zone, the coverages and the iron and copper rocks near the spot with their yields", (args, con) =>
        {
            if (!loaded(con)) return;
            Result(con, player.ControlProspect(out string m), m);
        });

        add("equip", "equip <item name or record>", "the first selected character right-clicks an item of its inventory: it goes to the slot that takes it, a worn one back to the grid (name: exact, else part of it; see observe.squad[].inventory)", (args, con) =>
        {
            if (!loaded(con)) return;
            if (args.Count == 0) { con.Error("usage: equip <item name or record>"); return; }
            Result(con, player.ControlEquip(string.Join(' ', args), out string m), m);
        });

        add("carry", "carry <id>", "the selected characters pick up a downed character (a knocked-out or dead body, or a stranger: a kidnap)", (args, con) =>
        {
            if (!loaded(con)) return;
            if (args.Count != 1 || !Character(args[0], con, "carry", out var id)) { if (args.Count != 1) con.Error("usage: carry <c:slot.gen>"); return; }
            Result(con, player.ControlCarry(id, out string m), m);
        });

        add("putdown", "putdown", "the selected characters put down whom they carry", (args, con) =>
        {
            if (!loaded(con)) return;
            Result(con, player.ControlPutDown(out string m), m);
        });

        add("say", "say <text> [--id <c:slot.gen>]", "write in game as a character: the text (at most 200 characters) shows as a speech bubble over its head for 2 + 0.12 s per character (the game's own bubble, as an order refused is barked), visible in the normal view; speaker: --id, else the first selected character, else the first squad member; display only, the world is not changed", (args, con) =>
        {
            if (!loaded(con)) return;
            var snap = session.CurrentSnapshot;
            CharacterId who = CharacterId.None;
            var words = new List<string>();
            for (int a = 0; a < args.Count; a++)
            {
                if (args[a].Equals("--id", StringComparison.OrdinalIgnoreCase))
                {
                    if (a + 1 >= args.Count || !PlayerInterface.TryParseCharacter(args[a + 1], out who)) { con.Error("say: --id needs a character id (c:<slot>.<gen>)"); return; }
                    a++;
                }
                else words.Add(args[a]);
            }
            string text = string.Join(' ', words).Trim();
            if (text.Length == 0) { con.Error("usage: say <text> [--id <c:slot.gen>]"); return; }
            if (text.Length > 200) { con.Error($"say: text is {text.Length} characters, at most 200"); return; }
            if (who != CharacterId.None)
            {
                if (snap.Characters.FirstOrDefault(c => c.Id == who) is not { } named) { con.Error($"say: no character c:{who.Slot}.{who.Generation}"); return; }
                if (named.Body is { Dead: true }) { con.Error($"say: c:{who.Slot}.{who.Generation} is dead"); return; }
            }
            else
            {
                var lead = snap.Characters.FirstOrDefault(c => c.IsPlayer && c.Selected && c.Body is not { Dead: true }) ?? snap.Characters.FirstOrDefault(c => c.IsPlayer && c.Body is not { Dead: true });
                if (lead is null) { con.Error("say: no living squad member to speak (pass --id <c:slot.gen>)"); return; }
                who = lead.Id;
            }
            session.World.Commands.Enqueue(new DebugSay(who, text) { Tick = session.World.Tick });
            con.Print($"said as c:{who.Slot}.{who.Generation}: {text}");
        });

        add("trade", "trade | trade open <trader id> | trade buy|sell <item name> [n] | trade close", "without arguments: the open trade as JSON (stock, goods, prices, money); open: the first selected character walks to the trader; buy/sell: units of the named stock item or of the customer's goods; close", (args, con) =>
        {
            if (!loaded(con)) return;
            string sub = args.Count > 0 ? args[0].ToLowerInvariant() : "";
            if (args.Count == 0)
            {
                if (player.ControlTradeJson(out string json)) { controlData = json; con.Print("trade open"); }
                else con.Error("trade: no trade is open (trade open <trader id>; the customer must walk up, so step first)");
            }
            else if (sub == "open" && args.Count == 2) { if (Character(args[1], con, "trade open", out var id)) Result(con, player.ControlTradeOpen(id, out string m), m); }
            else if (sub == "close" && args.Count == 1) Result(con, player.ControlTradeClose(out string m), m);
            else if (sub is "buy" or "sell" && args.Count >= 2)
            {
                int n = 1;
                var words = args.Skip(1).ToList();
                if (words.Count > 1 && int.TryParse(words[^1], NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)) { n = parsed; words.RemoveAt(words.Count - 1); }
                Result(con, player.ControlTradeDeal(sub == "buy", string.Join(' ', words), n, out string m), m);
            }
            else con.Error("usage: trade | trade open <trader id> | trade buy|sell <item name> [n] | trade close");
        });

        add("ko", "ko [seconds] [--id <c:slot.gen>]", "debug: knock the selected characters (or the one given) out for that many character seconds (default 600), so loot, carry and first aid can be tested on a body", (args, con) =>
        {
            if (!loaded(con)) return;
            if (!ControlTargets(args, con, "ko", out var who, out var rest)) return;
            float seconds = 600;
            if (rest.Count > 1 || (rest.Count == 1 && (!Num(rest[0], out seconds) || seconds <= 0))) { con.Error("usage: ko [seconds] [--id <c:slot.gen>]"); return; }
            session.World.Commands.Enqueue(new DebugKnockOut(who, seconds) { Tick = session.World.Tick });
            con.Print($"knocked out {who.Length} for {seconds:0.#} s");
        });

        add("wake", "wake [--id <c:slot.gen>]", "debug: bring the selected characters (or the one given) out of a knock-out at once, conscious and controllable, without healing them (heal first for a wounded vital part, which would put them down again); dead characters are refused, conscious ones skipped (error if none was unconscious)", (args, con) =>
        {
            if (!loaded(con)) return;
            if (!ControlTargets(args, con, "wake", out var who, out var rest)) return;
            if (rest.Count > 0) { con.Error("usage: wake [--id <c:slot.gen>]"); return; }
            var chars = session.CurrentSnapshot.Characters;
            var down = new List<CharacterId>();
            foreach (var id in who)
            {
                var body = chars.FirstOrDefault(c => c.Id == id)?.Body;
                if (body is { Dead: true }) { con.Error($"wake: c:{id.Slot}.{id.Generation} is dead"); return; }
                if (body is { Unconscious: true }) down.Add(id);
            }
            if (down.Count == 0) { con.Error("wake: none of them is unconscious"); return; }
            session.World.Commands.Enqueue(new DebugWake(down) { Tick = session.World.Tick });
            con.Print($"woke {down.Count}");
        });

        add("bandits-loot", "bandits-loot [on|off]", "read the New game option \"Bandits loot the player\" (CAMERA bl) of the running world; on/off sets it (test aid)", (args, con) =>
        {
            if (!loaded(con)) return;
            if (args.Count == 1 && args[0].Equals("on", StringComparison.OrdinalIgnoreCase)) session.World.Player.BanditsLoot = true;
            else if (args.Count == 1 && args[0].Equals("off", StringComparison.OrdinalIgnoreCase)) session.World.Player.BanditsLoot = false;
            else if (args.Count != 0) { con.Error("usage: bandits-loot [on|off]"); return; }
            con.Print($"bandits-loot {(session.World.Player.BanditsLoot ? "on" : "off")}");
        });

        add("spawn-at", "spawn-at <x> <z> [record] [--ko | --dead] [--aggro [weapon]]", "--aggro: also arm the spawn (weapon item, default Katana) and order it to attack the lead of the squad (test aid for fights); spawn a hostile character (default record 'Dust Bandit') at world X Z without a pointer; the reply has its id, state and position (a paused game steps a few ticks to make it appear); --ko / --dead leave a body to test loot, carry and first aid on", (args, con) =>
        {
            if (!loaded(con)) return;
            bool dead = args.Contains("--dead", StringComparer.OrdinalIgnoreCase), ko = false;
            var plain = new List<string>();
            bool aggro = false;
            string weapon = "Katana";
            for (int a = 0; a < args.Count; a++)
            {
                if (args[a].Equals("--dead", StringComparison.OrdinalIgnoreCase)) continue;
                if (args[a].Equals("--aggro", StringComparison.OrdinalIgnoreCase)) { aggro = true; if (a + 1 < args.Count && !args[a + 1].StartsWith("--")) weapon = args[++a]; continue; }
                if (args[a].Equals("--ko", StringComparison.OrdinalIgnoreCase)) { ko = true; continue; }
                plain.Add(args[a]);
            }
            if (plain.Count < 2 || !Num(plain[0], out float x) || !Num(plain[1], out float z) || (ko && dead) || (aggro && (ko || dead))) { con.Error("usage: spawn-at <x> <z> [record] [--ko | --dead] [--aggro [weapon]]"); return; }
            string record = plain.Count > 2 ? string.Join(' ', plain.Skip(2)) : "Dust Bandit";
            if (DebugRecord(record, character: true) is not { } npc) { con.Error($"spawn-at: no character record matches '{record}'"); return; }
            string? weaponId = null;
            if (aggro)
            {
                if (DebugRecord(weapon, character: false) is not { } w) { con.Error($"spawn-at: no item record matches '{weapon}'"); return; }
                weaponId = w.Id;
            }
            var before = new HashSet<CharacterId>(player.ControlCharactersNear(x, z).Select(c => c.Id));
            session.World.Commands.Enqueue(new DebugSpawn(npc.Id, new System.Numerics.Vector2(x, z)) { Tick = session.World.Tick });
            var pending = new ControlSpawn(currentControlId ?? "0", before, x, z, ko, dead, Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency) { Aggro = aggro, Weapon = weaponId };
            if (session.Simulation.IsPaused)
            {
                for (int k = 0; k < 6 && !ControlSpawnProgress(pending, out _, out _); k++) session.Step(1);
                if (!ControlSpawnProgress(pending, out string json, out string error)) { con.Error($"spawn-at: {error}"); return; }
                controlData = json;
                con.Print($"spawned {npc.Name}");
                return;
            }
            controlSpawns.Add(pending);
            controlReplyDeferred = true;
            con.Print("spawn pending");
        });
    }

    /// <summary>
    /// Moves a spawn along: find the new character, send it down when asked; true (with its JSON) once it is there and down as asked. <paramref name="error"/> is the reason while it is not.
    /// </summary>
    bool ControlSpawnProgress(ControlSpawn s, out string json, out string error)
    {
        json = "";
        error = "the character did not appear";
        if (s.Found.IsNone)
        {
            var hits = player.ControlCharactersNear(s.X, s.Z).Where(c => !c.Player && !s.Before.Contains(c.Id) && c.Dist < 300).OrderBy(c => c.Dist).ToList();
            if (hits.Count == 0) return false;
            s.Found = hits[0].Id;
        }
        var near = player.ControlCharactersNear(s.X, s.Z).FirstOrDefault(c => c.Id == s.Found);
        if (s.Aggro && !s.Armed && player.ControlSquad() is { Count: > 0 } squad)
        {
            s.Armed = true;
            session.World.Commands.Enqueue(new DebugGiveItem(s.Found, s.Weapon!, 1) { Tick = session.World.Tick });
            session.World.Commands.Enqueue(new Meitou.Simulation.Combat.AttackOrder([s.Found], squad[0]) { Tick = session.World.Tick });
        }
        if ((s.Ko || s.Dead) && !s.Downed)
        {
            session.World.Commands.Enqueue(s.Dead ? new DebugKill([s.Found]) { Tick = session.World.Tick } : new DebugKnockOut([s.Found], 600) { Tick = session.World.Tick });
            s.Downed = true;
            error = "the character did not go down";
            return false;
        }
        if ((s.Ko || s.Dead) && !near.Down) { error = "the character did not go down"; return false; }
        json = player.ControlCharacterJson(s.Found) ?? "";
        return json.Length > 0;
    }

    /// <summary>Every poll: finishes the pending spawn-at replies (a running game), or fails them after 5 real seconds.</summary>
    void ControlSpawnPoll()
    {
        for (int i = controlSpawns.Count - 1; i >= 0; i--)
        {
            var s = controlSpawns[i];
            if (ControlSpawnProgress(s, out string json, out string error)) { controlSpawns.RemoveAt(i); ControlWrite(s.Id, true, "", json); }
            else if (Stopwatch.GetTimestamp() >= s.Deadline) { controlSpawns.RemoveAt(i); ControlWrite(s.Id, false, "spawn-at: " + error); }
        }
    }
}
