using System.Globalization;
using Meitou.Game.DevTools;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// Agent-playtest control channel, play API (docs/procedures/agent-play-api.md): <c>observe</c> and the order commands an agent needs to play. The commands only parse, check and hand over to
/// <see cref="PlayerInterface"/> (PlayerInterface.ControlPlay.cs), which gives the game's own command objects to the selected characters.
/// </summary>
sealed partial class GameHost
{
    void RegisterControlPlayCommands(Action<string, string, string, Action<IReadOnlyList<string>, DevConsole>> add, Func<DevConsole, bool> loaded)
    {
        bool Character(string word, DevConsole con, string verb, out CharacterId id)
        {
            id = CharacterId.None;
            if (word.StartsWith("c:", StringComparison.OrdinalIgnoreCase) && PlayerInterface.TryParseCharacter(word, out id)) return true;
            con.Error($"{verb}: '{word}' is not a character id (c:<slot>.<gen>, from observe)");
            return false;
        }
        void Result(DevConsole con, bool ok, string message) { if (ok) con.Print(message); else con.Error(message); }

        add("observe", "observe [--radius <m>] [--full] | observe --brief [--changed]", "--brief: about 200 bytes for polling (tick, clock, paused, speed, lead id/name/pos[x,z]/state/hp fraction/task/order, threat = nearest hostile distance, squad alive/total, money); --brief --changed: only the fields that differ from the last brief call (tick always). Plain: one JSON for a decision: time, speed, the squad (hp, limbs, hunger, task, inventory, skills), what is near (threats, allies, neutrals, animals, traders, bodies), loose items, buildings, containers, machines, research, score. Default radius 120; --full lifts the list caps", (args, con) =>
        {
            if (!loaded(con)) return;
            float radius = 120;
            bool full = false, brief = false, changed = false;
            for (int a = 0; a < args.Count; a++)
            {
                if (args[a].Equals("--full", StringComparison.OrdinalIgnoreCase)) full = true;
                else if (args[a].Equals("--brief", StringComparison.OrdinalIgnoreCase)) brief = true;
                else if (args[a].Equals("--changed", StringComparison.OrdinalIgnoreCase)) changed = true;
                else if (args[a].Equals("--radius", StringComparison.OrdinalIgnoreCase) && a + 1 < args.Count && Num(args[a + 1], out radius) && radius > 0) a++;
                else { con.Error("usage: observe [--radius <m>] [--full] | observe --brief [--changed]"); return; }
            }
            if (changed && !brief) { con.Error("usage: observe --brief [--changed]"); return; }
            string json = brief ? player.ControlBriefJson(changed) : player.ControlObserveJson(radius, full);
            PrintData(con, json);
        });

        add("stop", "stop", "stop the selected characters (the Stop order: drops their orders and queue)", (args, con) =>
        {
            if (!loaded(con)) return;
            Result(con, player.ControlStop(out string m), m);
        });

        add("follow", "follow <id> [--guard]", "the selected characters follow a character (--guard: bodyguard, also fights the target's attackers)", (args, con) =>
        {
            if (!loaded(con)) return;
            bool guard = args.Contains("--guard", StringComparer.OrdinalIgnoreCase);
            var words = args.Where(a => !a.Equals("--guard", StringComparison.OrdinalIgnoreCase)).ToList();
            if (words.Count != 1) { con.Error("usage: follow <c:slot.gen> [--guard]"); return; }
            if (!Character(words[0], con, "follow", out var id)) return;
            Result(con, player.ControlFollow(id, guard, out string m), m);
        });

        add("stance", "stance block|hold|passive|ranged|taunt|sneak|jobs [on|off]", "flip (or set) a stance toggle of the selected characters, as the orders panel does; no on|off flips it. observe.squad[].stance lists what is on", (args, con) =>
        {
            if (!loaded(con)) return;
            if (args.Count is < 1 or > 2 || (args.Count == 2 && args[1].ToLowerInvariant() is not ("on" or "off"))) { con.Error("usage: stance block|hold|passive|ranged|taunt|sneak|jobs [on|off]"); return; }
            Result(con, player.ControlStance(args[0], args.Count == 2 ? args[1].Equals("on", StringComparison.OrdinalIgnoreCase) : null, out string m), m);
        });

        add("pickup", "pickup <item id | nearest>", "the selected characters (the nearest able one) walk to a loose item and take it; ids are in observe.groundItems or ground-items", (args, con) =>
        {
            if (!loaded(con)) return;
            if (args.Count != 1) { con.Error("usage: pickup <item id | nearest>"); return; }
            Result(con, player.ControlPickup(args[0], out string m), m);
        });

        add("loot", "loot <id>", "the selected characters take everything that fits from a dead or knocked-out character (observe.nearby.bodies)", (args, con) =>
        {
            if (!loaded(con)) return;
            if (args.Count != 1 || !Character(args[0], con, "loot", out var id)) { if (args.Count != 1) con.Error("usage: loot <c:slot.gen>"); return; }
            Result(con, player.ControlLoot(id, out string m), m);
        });

        add("firstaid", "firstaid <id>", "the selected characters walk to a patient and bandage them with a first aid kit (the patient may be a selected character itself)", (args, con) =>
        {
            if (!loaded(con)) return;
            if (args.Count != 1 || !Character(args[0], con, "firstaid", out var id)) { if (args.Count != 1) con.Error("usage: firstaid <c:slot.gen>"); return; }
            Result(con, player.ControlFirstAid(id, out string m), m);
        });

        add("build-list", "build-list [text] [--all] [--limit n]", "the building types the player may place now (those research unlocked) with their material costs, as JSON; text filters id, name and category; --all adds the locked ones", (args, con) =>
        {
            if (!loaded(con)) return;
            bool all = args.Contains("--all", StringComparer.OrdinalIgnoreCase);
            int limit = 60;
            var words = new List<string>();
            for (int a = 0; a < args.Count; a++)
            {
                if (args[a].Equals("--all", StringComparison.OrdinalIgnoreCase)) continue;
                if (args[a].Equals("--limit", StringComparison.OrdinalIgnoreCase) && a + 1 < args.Count && int.TryParse(args[a + 1], NumberStyles.None, CultureInfo.InvariantCulture, out limit)) { a++; continue; }
                words.Add(args[a]);
            }
            string json = player.ControlBuildListJson(string.Join(' ', words), all, Math.Clamp(limit, 1, 300));
            PrintData(con, json);
        });

        add("build", "build <type> <x> <z> [rotationDegrees] [--anyway]", "real build mode: judge the spot by the placement rules, then confirm a construction site (PlaceBuildings) and send the selected characters to build it; refused with the reason for a locked type, a bad spot or missing materials (--anyway places the site regardless, for a base store to supply). Types: build-list", (args, con) =>
        {
            if (!loaded(con)) return;
            bool anyway = args.Contains("--anyway", StringComparer.OrdinalIgnoreCase);
            var words = args.Where(a => !a.Equals("--anyway", StringComparison.OrdinalIgnoreCase)).ToList();
            float yaw = 0;
            if (words.Count is < 3 or > 4 || !Num(words[1], out float x) || !Num(words[2], out float z) || (words.Count == 4 && !Num(words[3], out yaw)))
            { con.Error("usage: build <type> <x> <z> [rotationDegrees] [--anyway]"); return; }
            bool ok = player.ControlBuild(words[0], x, z, yaw, anyway, out string m, out string json);
            if (json.Length > 0) controlData = json;
            Result(con, ok, m);
        });

        add("operate", "operate <building id>", "the selected characters work a machine of yours (a well, a farm, a research bench, a crafting bench); ids in observe.machines", (args, con) =>
        {
            if (!loaded(con)) return;
            if (args.Count != 1) { con.Error("usage: operate <building id>"); return; }
            Result(con, player.ControlOperate(args[0], out string m), m);
        });

        add("craft", "craft <bench id> [recipe key|name] [count]", "without a recipe: list a crafting bench's recipes (JSON); with one: queue it count times (default 1)", (args, con) =>
        {
            if (!loaded(con)) return;
            int count = 1;
            if (args.Count is < 1 or > 3 || (args.Count == 3 && (!int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out count) || count < 1 || count > 100))) { con.Error("usage: craft <bench id> [recipe] [count 1..100]"); return; }
            bool ok = player.ControlCraft(args[0], args.Count > 1 ? args[1] : null, count, out string m, out string json);
            if (json.Length > 0) controlData = json;
            Result(con, ok, m);
        });

        add("step", "step <n>", "paused game only: run n simulation ticks (1/30 game second each, 1..3000) synchronously and reply with the new tick; commands given before it apply in these ticks", (args, con) =>
        {
            if (!loaded(con)) return;
            if (args.Count != 1 || !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < 1 || n > 3000) { con.Error("usage: step <n 1..3000>"); return; }
            if (!session.Simulation.IsPaused) { con.Error("step: the game is running; pause first"); return; }
            long before = session.World.Tick;
            long after = session.Step(n);
            controlData = System.Text.Json.JsonSerializer.Serialize(new { ticksRun = after - before, tick = after, gameHours = Math.Round(session.World.GameHours, 3) });
            con.Print($"stepped {after - before} ticks to {after}");
        });

        add("hurt", "hurt <damage> [part id] [--id <c:slot.gen>]", "debug: one cut of that much damage to each selected character (or the one given by --id; a random body part, or the part with that template id) so first aid, bleeding and knock-outs can be tested", (args, con) =>
        {
            if (!loaded(con)) return;
            if (!ControlTargets(args, con, "hurt", out var who, out var rest)) return;
            if (rest.Count is < 1 or > 2 || !Num(rest[0], out float cut) || cut <= 0) { con.Error("usage: hurt <damage> [part id] [--id <c:slot.gen>]"); return; }
            session.World.Commands.Enqueue(new DebugDamage(who, cut, rest.Count == 2 ? rest[1] : null) { Tick = session.World.Tick });
            con.Print($"hurt {who.Length} by {cut:0.#}");
        });

        RegisterControlPlayMoreCommands(add, loaded);
        RegisterControlCombatCommands(add, loaded);
        RegisterControlAskCommands(add);
        RegisterControlPersonaCommands(add, loaded);
    }

    /// <summary>The <c>research</c> arguments: <c>research list</c> (JSON) and <c>research queue &lt;tech&gt;</c>; no argument is the dev console's finish-all.</summary>
    void ControlResearch(IReadOnlyList<string> args, DevConsole con)
    {
        if (Covered || player.TitleOpen) { con.Error("no world is loaded yet"); return; }
        string sub = args[0].ToLowerInvariant();
        if (sub == "list" && args.Count == 1) { string json = player.ControlResearchJson(); PrintData(con, json); return; }
        if (sub == "queue" && args.Count >= 2)
        {
            if (player.ControlQueueResearch(string.Join(' ', args.Skip(1)), out string m)) con.Print(m); else con.Error(m);
            return;
        }
        con.Error("usage: research (finish all) | research list | research queue <tech id or name>");
    }
}
