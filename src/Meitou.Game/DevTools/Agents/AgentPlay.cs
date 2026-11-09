using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Meitou.Data.Fcs;
using Meitou.Simulation;

namespace Meitou.Game.DevTools.Agents;

/// <summary>
/// Agent-playtest control channel, play API (docs/procedures/agent-play-api.md): <c>observe</c> and the order commands an agent needs to play. The
/// commands only parse, check and hand over to <see cref="PlayerInterface"/> (AgentOrders), which gives the game's own command objects to the
/// selected characters.
/// </summary>
sealed class AgentPlay(GameHost host)
{
    /// <summary>
    /// A world is on screen (<see cref="AgentWorld.Loaded"/>); else prints the error for the console and returns false.
    /// </summary>
    bool Loaded(DevConsole con) => host.AgentWorld.Loaded(con);

    /// <summary>
    /// Parses a character id (<c>c:slot.gen</c>); else prints the error for <paramref name="verb"/> and returns false.
    /// </summary>
    static bool Character(string word, DevConsole con, string verb, out CharacterId id)
    {
        id = CharacterId.None;
        if (word.StartsWith("c:", StringComparison.OrdinalIgnoreCase) && AgentOrders.TryParseCharacter(word, out id))
            return true;
        con.Error($"{verb}: '{word}' is not a character id (c:<slot>.<gen>, from observe)");
        return false;
    }

    /// <summary>
    /// Prints the order's message, as an error when it failed.
    /// </summary>
    static void Result(DevConsole con, bool ok, string message)
    {
        if (ok) con.Print(message);
        else con.Error(message);
    }

    /// <summary>
    /// Registers the play API (observe, stop, follow, stance, pickup, loot, build, craft, step, hurt, ...) and its further groups.
    /// </summary>
    internal void RegisterControlPlayCommands(Action<string, string, string, Action<IReadOnlyList<string>, DevConsole>> add)
    {
        add("observe", "observe [--radius <m>] [--full] | observe --brief [--changed]", "--brief: about 200 bytes for polling (tick, clock, paused, "
            + "speed, lead id/name/pos[x,z]/state/hp fraction/task/order, threat = nearest hostile distance, squad alive/total, money); "
            + "--brief --changed: only the fields that differ from the last brief call (tick always). Plain: one JSON for a decision: time, speed, "
            + "the squad (hp, limbs, hunger, task, inventory, skills), what is near (threats, allies, neutrals, animals, traders, bodies), "
            + "loose items, buildings, containers, machines, research, score. Default radius 120; --full lifts the list caps", ControlObserve);

        add("stop", "stop", "stop the selected characters (the Stop order: drops their orders and queue)", ControlStop);

        add("follow", "follow <id> [--guard]", "the selected characters follow a character (--guard: bodyguard, also fights the target's "
            + "attackers)", ControlFollow);

        add("stance", "stance block|hold|passive|ranged|taunt|sneak|jobs [on|off]", "flip (or set) a stance toggle of the selected characters, "
            + "as the orders panel does; no on|off flips it. observe.squad[].stance lists what is on", ControlStance);

        add("pickup", "pickup <item id | nearest>", "the selected characters (the nearest able one) walk to a loose item and take it; "
            + "ids are in observe.groundItems or ground-items", ControlPickup);

        add("loot", "loot <id>", "the selected characters take everything that fits from a dead or knocked-out character "
            + "(observe.nearby.bodies)", ControlLoot);

        add("firstaid", "firstaid <id>", "the selected characters walk to a patient and bandage them with a first aid kit (the patient may be a "
            + "selected character itself)", ControlFirstaid);

        add("build-list", "build-list [text] [--all] [--limit n]", "the building types the player may place now (those research unlocked) "
            + "with their material costs, as JSON; text filters id, name and category; --all adds the locked ones", ControlBuildList);

        add("build", "build <type> <x> <z> [rotationDegrees] [--anyway]", "real build mode: judge the spot by the placement rules, "
            + "then confirm a construction site (PlaceBuildings) and send the selected characters to build it; refused with the reason for a locked "
            + "type, a bad spot or missing materials (--anyway places the site regardless, for a base store to supply). Types: "
            + "build-list", ControlBuild);

        add("operate", "operate <building id>", "the selected characters work a machine of yours (a well, a farm, a research bench, "
            + "a crafting bench); ids in observe.machines", ControlOperate);

        add("craft", "craft <bench id> [recipe key|name] [count]", "without a recipe: list a crafting bench's recipes (JSON); with one: "
            + "queue it count times (default 1)", ControlCraft);

        add("step", "step <n>", "paused game only: run n simulation ticks (1/30 game second each, 1..3000) synchronously and reply with the new "
            + "tick; commands given before it apply in these ticks", ControlStep);

        add("hurt", "hurt <damage> [part id] [--id <c:slot.gen>]", "debug: one cut of that much damage to each selected character (or the one given "
            + "by --id; a random body part, or the part with that template id) so first aid, bleeding and knock-outs can be tested", ControlHurt);

        RegisterControlPlayMoreCommands(add);
        RegisterControlCombatCommands(add);
        RegisterControlAskCommands(add);
        RegisterControlPersonaCommands(add);
    }

    /// <summary>
    /// The <c>observe</c> command (usage and help at its registration in <see cref="RegisterControlPlayCommands"/>).
    /// </summary>
    void ControlObserve(IReadOnlyList<string> args, DevConsole con)
    {
        if (!Loaded(con)) return;
        float radius = 120;
        bool full = false, brief = false, changed = false;
        for (int a = 0; a < args.Count; a++)
        {
            if (args[a].Equals("--full", StringComparison.OrdinalIgnoreCase)) full = true;
            else if (args[a].Equals("--brief", StringComparison.OrdinalIgnoreCase)) brief = true;
            else if (args[a].Equals("--changed", StringComparison.OrdinalIgnoreCase)) changed = true;
            else if (args[a].Equals("--radius", StringComparison.OrdinalIgnoreCase) && a + 1 < args.Count
                && AgentWorld.Num(args[a + 1], out radius) && radius > 0)
                a++;
            else
            {
                con.Error("usage: observe [--radius <m>] [--full] | observe --brief [--changed]");
                return;
            }
        }
        if (changed && !brief)
        {
            con.Error("usage: observe --brief [--changed]");
            return;
        }

        string json = brief ? host.Player.AgentOrders.ControlBriefJson(changed) : host.Player.AgentOrders.ControlObserveJson(radius, full);
        host.AgentChannel.PrintData(con, json);
    }

    /// <summary>
    /// The <c>stop</c> command (usage and help at its registration in <see cref="RegisterControlPlayCommands"/>).
    /// </summary>
    void ControlStop(IReadOnlyList<string> args, DevConsole con)
    {
        if (!Loaded(con)) return;
        Result(con, host.Player.AgentOrders.ControlStop(out string m), m);
    }

    /// <summary>
    /// The <c>follow</c> command (usage and help at its registration in <see cref="RegisterControlPlayCommands"/>).
    /// </summary>
    void ControlFollow(IReadOnlyList<string> args, DevConsole con)
    {
        if (!Loaded(con)) return;
        bool guard = args.Contains("--guard", StringComparer.OrdinalIgnoreCase);
        var words = args.Where(a => !a.Equals("--guard", StringComparison.OrdinalIgnoreCase)).ToList();
        if (words.Count != 1)
        {
            con.Error("usage: follow <c:slot.gen> [--guard]");
            return;
        }

        if (!Character(words[0], con, "follow", out var id)) return;
        Result(con, host.Player.AgentOrders.ControlFollow(id, guard, out string m), m);
    }

    /// <summary>
    /// The <c>stance</c> command (usage and help at its registration in <see cref="RegisterControlPlayCommands"/>).
    /// </summary>
    void ControlStance(IReadOnlyList<string> args, DevConsole con)
    {
        if (!Loaded(con)) return;
        if (args.Count is < 1 or > 2 || (args.Count == 2 && args[1].ToLowerInvariant() is not ("on" or "off")))
        {
            con.Error("usage: stance block|hold|passive|ranged|taunt|sneak|jobs [on|off]");
            return;
        }

        Result(con, host.Player.AgentOrders.ControlStance(args[0], args.Count == 2 ? args[1].Equals("on",
            StringComparison.OrdinalIgnoreCase) : null, out string m), m);
    }

    /// <summary>
    /// The <c>pickup</c> command (usage and help at its registration in <see cref="RegisterControlPlayCommands"/>).
    /// </summary>
    void ControlPickup(IReadOnlyList<string> args, DevConsole con)
    {
        if (!Loaded(con)) return;
        if (args.Count != 1)
        {
            con.Error("usage: pickup <item id | nearest>");
            return;
        }

        Result(con, host.Player.AgentOrders.ControlPickup(args[0], out string m), m);
    }

    /// <summary>
    /// The <c>loot</c> command (usage and help at its registration in <see cref="RegisterControlPlayCommands"/>).
    /// </summary>
    void ControlLoot(IReadOnlyList<string> args, DevConsole con)
    {
        if (!Loaded(con)) return;
        if (args.Count != 1 || !Character(args[0], con, "loot", out var id))
        {
            if (args.Count != 1) con.Error("usage: loot <c:slot.gen>");
            return;
        }

        Result(con, host.Player.AgentOrders.ControlLoot(id, out string m), m);
    }

    /// <summary>
    /// The <c>firstaid</c> command (usage and help at its registration in <see cref="RegisterControlPlayCommands"/>).
    /// </summary>
    void ControlFirstaid(IReadOnlyList<string> args, DevConsole con)
    {
        if (!Loaded(con)) return;
        if (args.Count != 1 || !Character(args[0], con, "firstaid", out var id))
        {
            if (args.Count != 1) con.Error("usage: firstaid <c:slot.gen>");
            return;
        }

        Result(con, host.Player.AgentOrders.ControlFirstAid(id, out string m), m);
    }

    /// <summary>
    /// The <c>build-list</c> command (usage and help at its registration in <see cref="RegisterControlPlayCommands"/>).
    /// </summary>
    void ControlBuildList(IReadOnlyList<string> args, DevConsole con)
    {
        if (!Loaded(con)) return;
        bool all = args.Contains("--all", StringComparer.OrdinalIgnoreCase);
        int limit = 60;
        var words = new List<string>();
        for (int a = 0; a < args.Count; a++)
        {
            if (args[a].Equals("--all", StringComparison.OrdinalIgnoreCase)) continue;
            if (args[a].Equals("--limit", StringComparison.OrdinalIgnoreCase) && a + 1 < args.Count
                && int.TryParse(args[a + 1], NumberStyles.None, CultureInfo.InvariantCulture, out limit))
            {
                a++;
                continue;
            }

            words.Add(args[a]);
        }
        string json = host.Player.AgentOrders.ControlBuildListJson(string.Join(' ', words), all, Math.Clamp(limit, 1, 300));
        host.AgentChannel.PrintData(con, json);
    }

    /// <summary>
    /// The <c>build</c> command (usage and help at its registration in <see cref="RegisterControlPlayCommands"/>).
    /// </summary>
    void ControlBuild(IReadOnlyList<string> args, DevConsole con)
    {
        if (!Loaded(con)) return;
        bool anyway = args.Contains("--anyway", StringComparer.OrdinalIgnoreCase);
        var words = args.Where(a => !a.Equals("--anyway", StringComparison.OrdinalIgnoreCase)).ToList();
        float yaw = 0;
        if (words.Count is < 3 or > 4 || !AgentWorld.Num(words[1], out float x) || !AgentWorld.Num(words[2], out float z)
            || (words.Count == 4 && !AgentWorld.Num(words[3], out yaw)))
        { con.Error("usage: build <type> <x> <z> [rotationDegrees] [--anyway]"); return; }
        bool ok = host.Player.AgentOrders.ControlBuild(words[0], x, z, yaw, anyway, out string m, out string json);
        if (json.Length > 0) host.AgentChannel.ControlData = json;
        Result(con, ok, m);
    }

    /// <summary>
    /// The <c>operate</c> command (usage and help at its registration in <see cref="RegisterControlPlayCommands"/>).
    /// </summary>
    void ControlOperate(IReadOnlyList<string> args, DevConsole con)
    {
        if (!Loaded(con)) return;
        if (args.Count != 1)
        {
            con.Error("usage: operate <building id>");
            return;
        }

        Result(con, host.Player.AgentOrders.ControlOperate(args[0], out string m), m);
    }

    /// <summary>
    /// The <c>craft</c> command (usage and help at its registration in <see cref="RegisterControlPlayCommands"/>).
    /// </summary>
    void ControlCraft(IReadOnlyList<string> args, DevConsole con)
    {
        if (!Loaded(con)) return;
        int count = 1;
        if (args.Count is < 1 or > 3 || (args.Count == 3 && (!int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out count)
            || count < 1 || count > 100)))
        {
            con.Error("usage: craft <bench id> [recipe] [count 1..100]");
            return;
        }

        bool ok = host.Player.AgentOrders.ControlCraft(args[0], args.Count > 1 ? args[1] : null, count, out string m, out string json);
        if (json.Length > 0) host.AgentChannel.ControlData = json;
        Result(con, ok, m);
    }

    /// <summary>
    /// The <c>step</c> command (usage and help at its registration in <see cref="RegisterControlPlayCommands"/>).
    /// </summary>
    void ControlStep(IReadOnlyList<string> args, DevConsole con)
    {
        if (!Loaded(con)) return;
        if (args.Count != 1 || !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < 1 || n > 3000)
        {
            con.Error("usage: step <n 1..3000>");
            return;
        }

        if (!host.Session.Simulation.IsPaused)
        {
            con.Error("step: the game is running; pause first");
            return;
        }

        long before = host.Session.World.Tick;
        long after = host.Session.Step(n);
        host.AgentChannel.ControlData = JsonSerializer.Serialize(new { ticksRun = after - before, tick = after,
            gameHours = Math.Round(host.Session.World.GameHours, 3) });
        con.Print($"stepped {after - before} ticks to {after}");
    }

    /// <summary>
    /// The <c>hurt</c> command (usage and help at its registration in <see cref="RegisterControlPlayCommands"/>).
    /// </summary>
    void ControlHurt(IReadOnlyList<string> args, DevConsole con)
    {
        if (!Loaded(con)) return;
        if (!ControlTargets(args, con, "hurt", out var who, out var rest)) return;
        if (rest.Count is < 1 or > 2 || !AgentWorld.Num(rest[0], out float cut) || cut <= 0)
        {
            con.Error("usage: hurt <damage> [part id] [--id <c:slot.gen>]");
            return;
        }

        host.Session.World.Commands.Enqueue(new DebugDamage(who, cut, rest.Count == 2 ? rest[1] : null) { Tick = host.Session.World.Tick });
        con.Print($"hurt {who.Length} by {cut:0.#}");
    }

    /// <summary>
    /// The <c>research</c> arguments: <c>research list</c> (JSON) and <c>research queue &lt;tech&gt;</c>; no argument is the dev console's
    /// finish-all.
    /// </summary>
    internal void ControlResearch(IReadOnlyList<string> args, DevConsole con)
    {
        if (host.Covered || host.Player.TitleOpen)
        {
            con.Error("no world is loaded yet");
            return;
        }

        string sub = args[0].ToLowerInvariant();
        if (sub == "list" && args.Count == 1)
        {
            string json = host.Player.AgentOrders.ControlResearchJson();
            host.AgentChannel.PrintData(con, json);
            return;
        }

        if (sub == "queue" && args.Count >= 2)
        {
            if (host.Player.AgentOrders.ControlQueueResearch(string.Join(' ', args.Skip(1)), out string m))
                con.Print(m);
            else con.Error(m);

            return;
        }
        con.Error("usage: research (finish all) | research list | research queue <tech id or name>");
    }

    // More commands of the agent control channel's play API (docs/procedures/agent-play-api.md): work, prospect, equip, carry, trade,
    // spawn-at, ko, and the --id target of hurt and kill.

    /// <summary>
    /// A <c>spawn-at</c> waiting for its character to show up in the snapshot (a running game) and, with --ko or --dead, to go down.
    /// </summary>
    sealed class ControlSpawn(string id, HashSet<CharacterId> before, float x, float z, bool ko, bool dead, long deadline)
    {
        public string Id = id;
        public HashSet<CharacterId> Before = before;
        public float X = x, Z = z;
        public bool Ko = ko, Dead = dead, Downed;
        public long Deadline = deadline;
        public CharacterId Found = CharacterId.None;
        public string? Weapon;
        public bool Aggro, Armed;
    }

    readonly List<ControlSpawn> _controlSpawns = [];

    /// <summary>
    /// The characters a command with an optional <c>--id &lt;c:slot.gen&gt;</c> means: that one, else the selection. False with an error printed.
    /// </summary>
    internal bool ControlTargets(IReadOnlyList<string> args, DevConsole con, string verb, out CharacterId[] who, out List<string> rest)
    {
        who = [];
        rest = [];
        CharacterId? one = null;
        for (int a = 0; a < args.Count; a++)
        {
            if (args[a].Equals("--id", StringComparison.OrdinalIgnoreCase))
            {
                if (a + 1 >= args.Count || !AgentOrders.TryParseCharacter(args[a + 1], out var id))
                {
                    con.Error($"{verb}: --id needs a character id (c:<slot>.<gen>)");
                    return false;
                }

                one = id;
                a++;
            }
            else rest.Add(args[a]);
        }
        if (one is { } target)
        {
            if (!host.Session.CurrentSnapshot.Characters.Any(c => c.Id == target))
            {
                con.Error($"{verb}: no character c:{target.Slot}.{target.Generation}");
                return false;
            }

            who = [target];
            return true;
        }
        who = host.Player.AgentOrders.ControlOrderers().ToArray();
        if (who.Length == 0)
        {
            con.Error($"{verb}: select characters first (or pass --id <c:slot.gen>)");
            return false;
        }

        return true;
    }

    /// <summary>
    /// <c>kill --radius &lt;m&gt; [&lt;x&gt; &lt;z&gt;] [--animals]</c>: one DebugKill for every living, non-player character within the radius of
    /// the centre (the given point, else the selected or first squad member), from one pass over the snapshot.
    /// </summary>
    internal void ControlKillArea(IReadOnlyList<string> args, DevConsole con)
    {
        const string usage = "usage: kill --radius <m> [<x> <z>] [--animals]";
        float radius = 0;
        bool animals = false;
        bool haveRadius = false;
        var numbers = new List<float>();
        for (int a = 0; a < args.Count; a++)
        {
            if (args[a].Equals("--animals", StringComparison.OrdinalIgnoreCase)) animals = true;
            else if (args[a].Equals("--radius", StringComparison.OrdinalIgnoreCase) && !haveRadius && a + 1 < args.Count
                && AgentWorld.Num(args[a + 1], out radius))
            {
                haveRadius = true;
                a++;
            }
            else if (AgentWorld.Num(args[a], out float n)) numbers.Add(n);
            else
            {
                con.Error(usage);
                return;
            }
        }
        if (!haveRadius || numbers.Count is not (0 or 2))
        {
            con.Error(usage);
            return;
        }

        if (radius <= 0 || radius > 2000)
        {
            con.Error("kill: --radius must be above 0 and at most 2000 metres");
            return;
        }

        var snap = host.Session.CurrentSnapshot;
        float cx, cz;
        if (numbers.Count == 2) (cx, cz) = (numbers[0], numbers[1]);
        else
        {
            var lead = snap.Characters.FirstOrDefault(c => c.IsPlayer && c.Selected) ?? snap.Characters.FirstOrDefault(c => c.IsPlayer);
            if (lead is null)
            {
                con.Error("kill: no squad member to centre on (pass <x> <z>)");
                return;
            }

            (cx, cz) = (lead.Position.X, lead.Position.Z);
        }
        int own = host.Session.World.Player.Faction;
        var ids = new List<CharacterId>();
        foreach (var c in snap.Characters)
        {
            if (!DebugKillArea.Hits(c.IsPlayer, c.Faction, own, c.Animal, c.Body is { Dead: true }, animals)) continue;
            if (Vector2.Distance(new Vector2(c.Position.X, c.Position.Z), new Vector2(cx, cz)) > radius) continue;
            ids.Add(c.Id);
        }
        if (ids.Count > 0) host.Session.World.Commands.Enqueue(new DebugKill(ids) { Tick = host.Session.World.Tick });
        var shown = ids.Take(20).Select(i => $"\"c:{i.Slot}.{i.Generation}\"");
        con.Print($"{{\"killed\":{ids.Count},\"radius\":{radius.ToString(CultureInfo.InvariantCulture)},\"ids\":[{string.Join(",", shown)}],"
            + $"\"idsTruncated\":{(ids.Count > 20 ? "true" : "false")}}}");
    }

    /// <summary>
    /// Registers more play commands: work, prospect, equip, carry, putdown, say, trade, ko, wake, bandits-loot, spawn-at.
    /// </summary>
    void RegisterControlPlayMoreCommands(Action<string, string, string, Action<IReadOnlyList<string>, DevConsole>> add)
    {
        add("work", "work <site id>", "the selected characters build one of your construction sites, or repair a complete damaged building "
            + "(BuildOrder); ids are observe.buildings.own[].id (b:...)", ControlWork);

        add("prospect", "prospect", "the first selected character prospects where it stands (the Prospect button); "
            + "about a game second later observe.resources holds the zone, the coverages and the iron and copper rocks near the spot with their "
            + "yields", ControlProspect);

        add("equip", "equip <item name or record>", "the first selected character right-clicks an item of its inventory: "
            + "it goes to the slot that takes it, a worn one back to the grid (name: exact, else part of it; see "
            + "observe.squad[].inventory)", ControlEquip);

        add("carry", "carry <id>", "the selected characters pick up a downed character (a knocked-out or dead body, or a stranger: "
            + "a kidnap)", ControlCarry);

        add("putdown", "putdown", "the selected characters put down whom they carry", ControlPutdown);

        add("say", "say <text> [--id <c:slot.gen>]", "write in game as a character: the text (at most 200 characters) "
            + "shows as a speech bubble over its head for 2 + 0.12 s per character (the game's own bubble, as an order refused is barked), "
            + "visible in the normal view; speaker: --id, else the first selected character, else the first squad member; display only, "
            + "the world is not changed", ControlSay);

        add("trade", "trade | trade open <trader id> | trade buy|sell <item name> [n] | trade close", "without arguments: "
            + "the open trade as JSON (stock, goods, prices, money); open: the first selected character walks to the trader; buy/sell: "
            + "units of the named stock item or of the customer's goods; close", ControlTrade);

        add("ko", "ko [seconds] [--id <c:slot.gen>]", "debug: knock the selected characters (or the one given) out for that many character seconds "
            + "(default 600), so loot, carry and first aid can be tested on a body", ControlKo);

        add("wake", "wake [--id <c:slot.gen>]", "debug: bring the selected characters (or the one given) out of a knock-out at once, "
            + "conscious and controllable, without healing them (heal first for a wounded vital part, which would put them down again); "
            + "dead characters are refused, conscious ones skipped (error if none was unconscious)", ControlWake);

        add("bandits-loot", "bandits-loot [on|off]", "read the New game option \"Bandits loot the player\" (CAMERA bl) of the running world; "
            + "on/off sets it (test aid)", ControlBanditsLoot);

        add("spawn-at", "spawn-at <x> <z> [record] [--ko | --dead] [--aggro [weapon]]", "--aggro: also arm the spawn (weapon item, default Katana) "
            + "and order it to attack the lead of the squad (test aid for fights); spawn a hostile character (default record 'Dust Bandit') "
            + "at world X Z without a pointer; the reply has its id, state and position (a paused game steps a few ticks to make it appear); "
            + "--ko / --dead leave a body to test loot, carry and first aid on", ControlSpawnAt);
    }

    /// <summary>
    /// The <c>work</c> command (usage and help at its registration in <see cref="RegisterControlPlayMoreCommands"/>).
    /// </summary>
    void ControlWork(IReadOnlyList<string> args, DevConsole con)
    {
        if (!Loaded(con)) return;
        if (args.Count != 1)
        {
            con.Error("usage: work <site id>");
            return;
        }

        Result(con, host.Player.AgentOrders.ControlWork(args[0], out string m), m);
    }

    /// <summary>
    /// The <c>prospect</c> command (usage and help at its registration in <see cref="RegisterControlPlayMoreCommands"/>).
    /// </summary>
    void ControlProspect(IReadOnlyList<string> args, DevConsole con)
    {
        if (!Loaded(con)) return;
        Result(con, host.Player.AgentOrders.ControlProspect(out string m), m);
    }

    /// <summary>
    /// The <c>equip</c> command (usage and help at its registration in <see cref="RegisterControlPlayMoreCommands"/>).
    /// </summary>
    void ControlEquip(IReadOnlyList<string> args, DevConsole con)
    {
        if (!Loaded(con)) return;
        if (args.Count == 0)
        {
            con.Error("usage: equip <item name or record>");
            return;
        }

        Result(con, host.Player.AgentOrders.ControlEquip(string.Join(' ', args), out string m), m);
    }

    /// <summary>
    /// The <c>carry</c> command (usage and help at its registration in <see cref="RegisterControlPlayMoreCommands"/>).
    /// </summary>
    void ControlCarry(IReadOnlyList<string> args, DevConsole con)
    {
        if (!Loaded(con)) return;
        if (args.Count != 1 || !Character(args[0], con, "carry", out var id))
        {
            if (args.Count != 1) con.Error("usage: carry <c:slot.gen>");
            return;
        }

        Result(con, host.Player.AgentOrders.ControlCarry(id, out string m), m);
    }

    /// <summary>
    /// The <c>putdown</c> command (usage and help at its registration in <see cref="RegisterControlPlayMoreCommands"/>).
    /// </summary>
    void ControlPutdown(IReadOnlyList<string> args, DevConsole con)
    {
        if (!Loaded(con)) return;
        Result(con, host.Player.AgentOrders.ControlPutDown(out string m), m);
    }

    /// <summary>
    /// The <c>say</c> command (usage and help at its registration in <see cref="RegisterControlPlayMoreCommands"/>).
    /// </summary>
    void ControlSay(IReadOnlyList<string> args, DevConsole con)
    {
        if (!Loaded(con)) return;
        var snap = host.Session.CurrentSnapshot;
        CharacterId who = CharacterId.None;
        var words = new List<string>();
        for (int a = 0; a < args.Count; a++)
        {
            if (args[a].Equals("--id", StringComparison.OrdinalIgnoreCase))
            {
                if (a + 1 >= args.Count || !AgentOrders.TryParseCharacter(args[a + 1], out who))
                {
                    con.Error("say: --id needs a character id (c:<slot>.<gen>)");
                    return;
                }

                a++;
            }
            else words.Add(args[a]);
        }
        string text = string.Join(' ', words).Trim();
        if (text.Length == 0)
        {
            con.Error("usage: say <text> [--id <c:slot.gen>]");
            return;
        }

        if (text.Length > 200)
        {
            con.Error($"say: text is {text.Length} characters, at most 200");
            return;
        }

        if (who != CharacterId.None)
        {
            if (snap.Characters.FirstOrDefault(c => c.Id == who) is not { } named)
            {
                con.Error($"say: no character c:{who.Slot}.{who.Generation}");
                return;
            }

            if (named.Body is { Dead: true })
            {
                con.Error($"say: c:{who.Slot}.{who.Generation} is dead");
                return;
            }
        }
        else
        {
            var lead = snap.Characters.FirstOrDefault(c => c.IsPlayer && c.Selected && c.Body is not { Dead: true })
                ?? snap.Characters.FirstOrDefault(c => c.IsPlayer && c.Body is not { Dead: true });
            if (lead is null)
            {
                con.Error("say: no living squad member to speak (pass --id <c:slot.gen>)");
                return;
            }

            who = lead.Id;
        }
        host.Session.World.Commands.Enqueue(new DebugSay(who, text) { Tick = host.Session.World.Tick });
        con.Print($"said as c:{who.Slot}.{who.Generation}: {text}");
    }

    /// <summary>
    /// The <c>trade</c> command (usage and help at its registration in <see cref="RegisterControlPlayMoreCommands"/>).
    /// </summary>
    void ControlTrade(IReadOnlyList<string> args, DevConsole con)
    {
        if (!Loaded(con)) return;
        string sub = args.Count > 0 ? args[0].ToLowerInvariant() : "";
        if (args.Count == 0)
        {
            if (host.Player.AgentOrders.ControlTradeJson(out string json))
            {
                host.AgentChannel.ControlData = json;
                con.Print("trade open");
            }
            else
                con.Error("trade: no trade is open (trade open <trader id>; the customer must walk up, so step first)");
        }
        else if (sub == "open" && args.Count == 2)
        {
            if (Character(args[1], con, "trade open", out var id))
                Result(con, host.Player.AgentOrders.ControlTradeOpen(id, out string m), m);
        }
        else if (sub == "close" && args.Count == 1)
            Result(con, host.Player.AgentOrders.ControlTradeClose(out string m), m);
        else if (sub is "buy" or "sell" && args.Count >= 2)
        {
            int n = 1;
            var words = args.Skip(1).ToList();
            if (words.Count > 1 && int.TryParse(words[^1], NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
            {
                n = parsed;
                words.RemoveAt(words.Count - 1);
            }

            Result(con, host.Player.AgentOrders.ControlTradeDeal(sub == "buy", string.Join(' ', words), n, out string m), m);
        }
        else con.Error("usage: trade | trade open <trader id> | trade buy|sell <item name> [n] | trade close");
    }

    /// <summary>
    /// The <c>ko</c> command (usage and help at its registration in <see cref="RegisterControlPlayMoreCommands"/>).
    /// </summary>
    void ControlKo(IReadOnlyList<string> args, DevConsole con)
    {
        if (!Loaded(con)) return;
        if (!ControlTargets(args, con, "ko", out var who, out var rest)) return;
        float seconds = 600;
        if (rest.Count > 1 || (rest.Count == 1 && (!AgentWorld.Num(rest[0], out seconds) || seconds <= 0)))
        {
            con.Error("usage: ko [seconds] [--id <c:slot.gen>]");
            return;
        }

        host.Session.World.Commands.Enqueue(new DebugKnockOut(who, seconds) { Tick = host.Session.World.Tick });
        con.Print($"knocked out {who.Length} for {seconds:0.#} s");
    }

    /// <summary>
    /// The <c>wake</c> command (usage and help at its registration in <see cref="RegisterControlPlayMoreCommands"/>).
    /// </summary>
    void ControlWake(IReadOnlyList<string> args, DevConsole con)
    {
        if (!Loaded(con)) return;
        if (!ControlTargets(args, con, "wake", out var who, out var rest)) return;
        if (rest.Count > 0)
        {
            con.Error("usage: wake [--id <c:slot.gen>]");
            return;
        }

        var chars = host.Session.CurrentSnapshot.Characters;
        var down = new List<CharacterId>();
        foreach (var id in who)
        {
            var body = chars.FirstOrDefault(c => c.Id == id)?.Body;
            if (body is { Dead: true })
            {
                con.Error($"wake: c:{id.Slot}.{id.Generation} is dead");
                return;
            }

            if (body is { Unconscious: true }) down.Add(id);
        }
        if (down.Count == 0)
        {
            con.Error("wake: none of them is unconscious");
            return;
        }

        host.Session.World.Commands.Enqueue(new DebugWake(down) { Tick = host.Session.World.Tick });
        con.Print($"woke {down.Count}");
    }

    /// <summary>
    /// The <c>bandits-loot</c> command (usage and help at its registration in <see cref="RegisterControlPlayMoreCommands"/>).
    /// </summary>
    void ControlBanditsLoot(IReadOnlyList<string> args, DevConsole con)
    {
        if (!Loaded(con)) return;
        if (args.Count == 1 && args[0].Equals("on", StringComparison.OrdinalIgnoreCase))
            host.Session.World.Player.BanditsLoot = true;
        else if (args.Count == 1 && args[0].Equals("off", StringComparison.OrdinalIgnoreCase))
            host.Session.World.Player.BanditsLoot = false;
        else if (args.Count != 0)
        {
            con.Error("usage: bandits-loot [on|off]");
            return;
        }

        con.Print($"bandits-loot {(host.Session.World.Player.BanditsLoot ? "on" : "off")}");
    }

    /// <summary>
    /// The <c>spawn-at</c> command (usage and help at its registration in <see cref="RegisterControlPlayMoreCommands"/>).
    /// </summary>
    void ControlSpawnAt(IReadOnlyList<string> args, DevConsole con)
    {
        if (!Loaded(con)) return;
        bool dead = args.Contains("--dead", StringComparer.OrdinalIgnoreCase), ko = false;
        var plain = new List<string>();
        bool aggro = false;
        string weapon = "Katana";
        for (int a = 0; a < args.Count; a++)
        {
            if (args[a].Equals("--dead", StringComparison.OrdinalIgnoreCase)) continue;
            if (args[a].Equals("--aggro", StringComparison.OrdinalIgnoreCase))
            {
                aggro = true;
                if (a + 1 < args.Count && !args[a + 1].StartsWith("--")) weapon = args[++a];
                continue;
            }

            if (args[a].Equals("--ko", StringComparison.OrdinalIgnoreCase))
            {
                ko = true;
                continue;
            }

            plain.Add(args[a]);
        }
        if (plain.Count < 2 || !AgentWorld.Num(plain[0], out float x) || !AgentWorld.Num(plain[1], out float z) || (ko && dead)
            || (aggro && (ko || dead)))
        {
            con.Error("usage: spawn-at <x> <z> [record] [--ko | --dead] [--aggro [weapon]]");
            return;
        }

        string record = plain.Count > 2 ? string.Join(' ', plain.Skip(2)) : "Dust Bandit";
        if (host.DebugMenu.DebugRecord(record, character: true) is not { } npc)
        {
            con.Error($"spawn-at: no character record matches '{record}'");
            return;
        }

        string? weaponId = null;
        if (aggro)
        {
            if (host.DebugMenu.DebugRecord(weapon, character: false) is not { } w)
            {
                con.Error($"spawn-at: no item record matches '{weapon}'");
                return;
            }

            weaponId = w.Id;
        }
        var before = new HashSet<CharacterId>(host.Player.AgentOrders.ControlCharactersNear(x, z).Select(c => c.Id));
        host.Session.World.Commands.Enqueue(new DebugSpawn(npc.Id, new Vector2(x, z)) { Tick = host.Session.World.Tick });
        var pending = new ControlSpawn(host.AgentChannel.CurrentControlId ?? "0", before, x, z, ko, dead,
            Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency) { Aggro = aggro, Weapon = weaponId };
        if (host.Session.Simulation.IsPaused)
        {
            for (int k = 0; k < 6 && !ControlSpawnProgress(pending, out _, out _); k++) host.Session.Step(1);
            if (!ControlSpawnProgress(pending, out string json, out string error))
            {
                con.Error($"spawn-at: {error}");
                return;
            }

            host.AgentChannel.ControlData = json;
            con.Print($"spawned {npc.Name}");
            return;
        }
        _controlSpawns.Add(pending);
        host.AgentChannel.ControlReplyDeferred = true;
        con.Print("spawn pending");
    }

    /// <summary>
    /// Moves a spawn along: find the new character, send it down when asked; true (with its JSON) once it is there and down as asked.
    /// <paramref name="error"/> is the reason while it is not.
    /// </summary>
    bool ControlSpawnProgress(ControlSpawn s, out string json, out string error)
    {
        json = "";
        error = "the character did not appear";
        if (s.Found.IsNone)
        {
            var hits = host.Player.AgentOrders.ControlCharactersNear(s.X, s.Z).Where(c => !c.Player && !s.Before.Contains(c.Id) && c.Dist < 300)
                .OrderBy(c => c.Dist)
                .ToList();
            if (hits.Count == 0) return false;
            s.Found = hits[0].Id;
        }
        var near = host.Player.AgentOrders.ControlCharactersNear(s.X, s.Z).FirstOrDefault(c => c.Id == s.Found);
        if (s.Aggro && !s.Armed && host.Player.AgentOrders.ControlSquad() is { Count: > 0 } squad)
        {
            s.Armed = true;
            host.Session.World.Commands.Enqueue(new DebugGiveItem(s.Found, s.Weapon!, 1) { Tick = host.Session.World.Tick });
            host.Session.World.Commands.Enqueue(new Simulation.Combat.AttackOrder([s.Found], squad[0]) { Tick = host.Session.World.Tick });
        }
        if ((s.Ko || s.Dead) && !s.Downed)
        {
            host.Session.World.Commands.Enqueue(s.Dead ? new DebugKill([s.Found]) { Tick = host.Session.World.Tick } : new DebugKnockOut([s.Found],
                600) { Tick = host.Session.World.Tick });
            s.Downed = true;
            error = "the character did not go down";
            return false;
        }
        if ((s.Ko || s.Dead) && !near.Down)
        {
            error = "the character did not go down";
            return false;
        }

        json = host.Player.AgentOrders.ControlCharacterJson(s.Found) ?? "";
        return json.Length > 0;
    }

    /// <summary>
    /// Every poll: finishes the pending spawn-at replies (a running game), or fails them after 5 real seconds.
    /// </summary>
    internal void ControlSpawnPoll()
    {
        for (int i = _controlSpawns.Count - 1; i >= 0; i--)
        {
            var s = _controlSpawns[i];
            if (ControlSpawnProgress(s, out string json, out string error))
            {
                _controlSpawns.RemoveAt(i);
                host.AgentChannel.ControlWrite(s.Id, true, "", json);
            }
            else if (Stopwatch.GetTimestamp() >= s.Deadline)
            {
                _controlSpawns.RemoveAt(i);
                host.AgentChannel.ControlWrite(s.Id, false, "spawn-at: " + error);
            }
        }
    }

    // The <c>ask</c> and <c>task</c> console commands (agent-playtest): the player types a question or an instruction in the dev console and
    // an AI agent reads the queue file (docs/procedures/playtest/agent-play-api.md, "Ask and task").

    const int AskMaxLength = 300;
    readonly object _askLock = new();

    /// <summary>
    /// Registers <c>ask</c> and <c>task</c>, which queue the player's question or instruction for an AI agent.
    /// </summary>
    void RegisterControlAskCommands(Action<string, string, string, Action<IReadOnlyList<string>, DevConsole>> add)
    {
        add("ask", "ask <text>", "queue a question for the AI agent: one line `<iso time><TAB>ask<TAB><text>` is appended to asks.log in the control "
            + "directory (else next to the game's log.txt); the agent answers in game with say; text at most 300 characters; "
            + "the world is not changed, the game is not paused; replies `queued ask #<n>`", (args, con) => AskQueue("ask", args, con));
        add("task", "task <text>", "queue an instruction for the AI agent: one line `<iso time><TAB>task<TAB><text>` is appended to asks.log in the "
            + "control directory (else next to the game's log.txt); the agent plans, does it with the normal commands and reports with say; "
            + "text at most 300 characters; the world is not changed, the game is not paused; replies `queued task #<n>`", (args, con) =>
            AskQueue("task", args, con));
    }

    void AskQueue(string kind, IReadOnlyList<string> args, DevConsole con)
    {
        string text = string.Join(' ', args).Trim().Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        if (text.Length == 0)
        {
            con.Error($"usage: {kind} <text>");
            return;
        }

        if (text.Length > AskMaxLength)
        {
            con.Error($"{kind}: text is {text.Length} characters, at most {AskMaxLength}");
            return;
        }

        string dir = host.AgentChannel.ControlDir ?? AppContext.BaseDirectory;
        string path = Path.Combine(dir, "asks.log");
        try
        {
            int n;
            lock (_askLock)
            {
                n = (File.Exists(path) ? File.ReadAllLines(path).Count(l => l.Length > 0) : 0) + 1;
                File.AppendAllText(path, $"{DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)}\t{kind}\t{text}\n",
                    new UTF8Encoding(false));
            }
            con.Print($"queued {kind} #{n}" + (host.AgentChannel.ControlDir is null ? $" (no control dir: {path})" : ""));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            con.Error($"{kind}: cannot write {path}: {e.Message}");
        }
    }

    /// <summary>
    /// Registers <c>combat-status</c>.
    /// </summary>
    void RegisterControlCombatCommands(Action<string, string, string, Action<IReadOnlyList<string>, DevConsole>> add)
    {
        add("combat-status", "combat-status [--id c:slot.gen]", "read-only check of a character's fight (default the selected, "
            + "else the first squad member) as JSON: stance flags (block hold passive ranged taunt sneak jobs), status (attacking / blocking / idle "
            + "/ ko / dead), target, nearest hostile, damage dealt and taken over 5 s and 30 s (approximate, see its note), "
            + "blows over 30 s (thrown landed takenHits blocks parried evaded dodges) and warn lines (block on while attacking, "
            + "no damage dealt in 30 s with a hostile within 30 m, passive/hold set)", ControlCombatStatus);
    }

    /// <summary>
    /// The <c>combat-status</c> command (usage and help at its registration in <see cref="RegisterControlCombatCommands"/>).
    /// </summary>
    void ControlCombatStatus(IReadOnlyList<string> args, DevConsole con)
    {
        if (!Loaded(con)) return;
        CharacterId? id = null;
        if (args.Count == 2 && args[0] == "--id" && AgentOrders.TryParseCharacter(args[1], out var parsed))
            id = parsed;
        else if (args.Count != 0)
        {
            con.Error("usage: combat-status [--id c:<slot>.<gen>]");
            return;
        }

        string? json = host.Player.AgentOrders.ControlCombatStatusJson(id, out string error);
        if (json is null) con.Error(error);
        else host.AgentChannel.PrintData(con, json);
    }

    /// <summary>
    /// Item search of the agent control channel (docs/procedures/playtest/agent-play-api.md, "Personas"): <c>find-item</c> lists item records
    /// with their key, name, type and numbers.
    /// </summary>
    void RegisterControlPersonaCommands(Action<string, string, string, Action<IReadOnlyList<string>, DevConsole>> add)
    {
        add("unequip", "unequip <slot | item name>", "(backpack: removed and destroyed with its contents, never dropped) "
            + "the first selected character takes a worn item off (slot: head, shirt, armour, legs, boots, back, hip, belt; or the item's name): "
            + "it goes to the inventory grids; the reply cannot tell whether they had room, observe.squad[].inventory shows "
            + "where it is", ControlUnequip);

        add("strip-inventory", "strip-inventory", "the first selected character loses EVERY carried item: grids, worn clothing and weapons, "
            + "the backpack with its contents are destroyed (prosthetic limbs and locked shackles stay); nothing is dropped; "
            + "for test characters before a persona", ControlStripInventory);

        add("find-item", "find-item <text> [--type armour|weapon|item|crossbow|container] [--limit n] [--stats]",
            "search the item records (the ones give can hand out) whose name or key contains the text, ignoring case; "
            + "reply data.items[] has record (a key give and equip take), name, type and, with --stats, the record's numbers (floats, ints) "
            + "and the names its references point to; default limit 20, at most 200", ControlFindItem);
    }

    /// <summary>
    /// The <c>unequip</c> command (usage and help at its registration in <see cref="RegisterControlPersonaCommands"/>).
    /// </summary>
    void ControlUnequip(IReadOnlyList<string> args, DevConsole con)
    {
        if (!Loaded(con)) return;
        if (args.Count == 0)
        {
            con.Error("usage: unequip <slot | item name>");
            return;
        }

        string what = string.Join(' ', args);
        if (what.Equals("backpack", StringComparison.OrdinalIgnoreCase) || what.Equals("pack", StringComparison.OrdinalIgnoreCase))
        {
            var who = host.Player.AgentOrders.ControlOrderers();
            if (who.Count == 0)
            {
                con.Error("refused: no character selected (use select first)");
                return;
            }

            host.Session.World.Commands.Enqueue(new DebugStripItems(who[0], true) { Tick = host.Session.World.Tick });
            host.Session.ApplyPausedCommandsNow();
            con.Print("unequip backpack: the worn backpack is removed and destroyed with its contents (it is never dropped and "
                + "never put into the grids)");
            return;
        }
        if (host.Player.AgentOrders.ControlUnequip(string.Join(' ', args), out string m)) con.Print(m);
        else con.Error(m);
    }

    /// <summary>
    /// The <c>strip-inventory</c> command (usage and help at its registration in <see cref="RegisterControlPersonaCommands"/>).
    /// </summary>
    void ControlStripInventory(IReadOnlyList<string> args, DevConsole con)
    {
        if (!Loaded(con)) return;
        var who = host.Player.AgentOrders.ControlOrderers();
        if (who.Count == 0)
        {
            con.Error("refused: no character selected (use select first)");
            return;
        }

        host.Session.World.Commands.Enqueue(new DebugStripItems(who[0], false) { Tick = host.Session.World.Tick });
        host.Session.ApplyPausedCommandsNow();
        con.Print($"strip-inventory: every carried item of {who[0]} destroyed (nothing dropped)");
    }

    /// <summary>
    /// The <c>find-item</c> command (usage and help at its registration in <see cref="RegisterControlPersonaCommands"/>).
    /// </summary>
    void ControlFindItem(IReadOnlyList<string> args, DevConsole con)
    {
        if (!Loaded(con)) return;
        string text = "";
        string type = "";
        int limit = 20;
        bool stats = false;
        for (int a = 0; a < args.Count; a++)
        {
            if (args[a].Equals("--type", StringComparison.OrdinalIgnoreCase) && a + 1 < args.Count)
                type = args[++a].ToLowerInvariant();
            else if (args[a].Equals("--limit", StringComparison.OrdinalIgnoreCase) && a + 1 < args.Count && int.TryParse(args[a + 1], out int n))
            {
                limit = Math.Clamp(n, 1, 200);
                a++;
            }
            else if (args[a].Equals("--stats", StringComparison.OrdinalIgnoreCase)) stats = true;
            else text = text.Length == 0 ? args[a] : text + " " + args[a];
        }
        FcsRecordType? only = type switch
        {
            "armour" or "armor" => FcsRecordType.Armour,
            "weapon" => FcsRecordType.Weapon,
            "item" => FcsRecordType.Item,
            "crossbow" => FcsRecordType.Crossbow,
            "container" => FcsRecordType.Container,
            _ => null,
        };
        if (type.Length > 0 && only is null)
        {
            con.Error("find-item: --type is armour, weapon, item, crossbow or container");
            return;
        }

        if (host.Scene.Database is not { } db)
        {
            con.Error("find-item: no game data loaded");
            return;
        }

        var hits = db.Records.Values
            .Where(r => DebugGiveItem.ItemTypes.Contains(r.Type) && (only is null || r.Type == only) && r.Name.Length > 0
                && (text.Length == 0 || r.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                || r.StringId.Contains(text, StringComparison.OrdinalIgnoreCase)))
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
                    {
                        w.WriteString(k, string.Join("; ", r.GetReferences(k)
                            .Select(x => $"{(db.Find(x.TargetStringId) is { } t ? t.Name : x.TargetStringId)} "
                                + $"{x.Values.Value0},{x.Values.Value1},{x.Values.Value2}")));
                    }
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        host.AgentChannel.ControlData = Encoding.UTF8.GetString(ms.ToArray());
        con.Print($"{hits.Count} item records match '{text}'{(hits.Count > limit ? $", {limit} shown" : "")}");
    }

    // <c>order move &lt;x&gt; &lt;z&gt; --wait &lt;s&gt; [--stuck &lt;s&gt;]</c>: one reply when the first ordered character arrives, makes
    // no progress, or the time is up (docs/procedures/playtest/agent-play-api.md).

    /// <summary>
    /// Within this many metres of the target counts as arrived.
    /// </summary>
    const float MoveWaitArriveRadius = 4f;
    /// <summary>
    /// Default game seconds without progress that make a waited move "stuck" (override with <c>--stuck</c>).
    /// </summary>
    internal const float MoveWaitStuckSeconds = 10f;
    /// <summary>
    /// Progress means the distance to the target fell by at least this many metres since the last progress.
    /// </summary>
    const float MoveWaitProgressStep = 1f;
    const int MoveWaitTicksPerSecond = 30;
    /// <summary>
    /// Most ticks a paused waited move steps in one frame (the same cap as <c>step</c>).
    /// </summary>
    const int MoveWaitPausedTickCap = 3000;

    sealed class ControlMoveWait(string id, CharacterId who, float x, float z, long startTick, int timeoutTicks, int stuckTicks, float dist0)
    {
        public string Id = id;
        public CharacterId Who = who;
        public float X = x, Z = z;
        public long StartTick = startTick;
        public int TimeoutTicks = timeoutTicks, StuckTicks = stuckTicks;
        public float BestDist = dist0;
        public long ProgressTick = startTick;
        public long RealDeadline;
    }

    readonly List<ControlMoveWait> _controlMoveWaits = [];

    /// <summary>
    /// Starts a waited move after the order was given; true when the reply is deferred or already written into <paramref name="con"/>.
    /// </summary>
    internal void ControlMoveWaitStart(DevConsole con, float x, float z, float waitSeconds, float stuckSeconds)
    {
        var who = host.Player.AgentOrders.ControlOrderers();
        if (who.Count == 0 || host.Player.AgentOrders.ControlPosition($"c:{who[0].Slot}.{who[0].Generation}") is not { } p0)
        {
            con.Error("order: no position for the ordered character");
            return;
        }

        var w = new ControlMoveWait(host.AgentChannel.CurrentControlId ?? "0", who[0], x, z, host.Session.World.Tick,
            (int)MathF.Ceiling(waitSeconds * MoveWaitTicksPerSecond), (int)MathF.Ceiling(stuckSeconds * MoveWaitTicksPerSecond),
            Vector2.Distance(new Vector2(p0.X, p0.Z), new Vector2(x, z)));
        if (host.Session.Simulation.IsPaused)
        {
            // A paused game takes no tick of its own: run them here, checking every tick, but never more than a frame can bear (like `step`).
            int steps = 0, cap = Math.Min(w.TimeoutTicks + 2, MoveWaitPausedTickCap);
            while (steps < cap && !ControlMoveWaitProgress(w, out _))
            {
                host.Session.Step(1);
                steps++;
            }

            if (steps >= cap && w.TimeoutTicks + 2 > cap && !ControlMoveWaitProgress(w, out _))
            {
                con.Error($"order: paused wait stopped after {cap} ticks without a result (cap); the move goes on - `step` and `observe`, "
                    + $"or unpause and use wait-for");
                return;
            }
            host.AgentChannel.ControlData = _controlMoveWaitLast;
            con.Print("move wait done (paused game stepped)");
            return;
        }
        w.RealDeadline = Stopwatch.GetTimestamp() + (long)((waitSeconds * 4 + 30) * Stopwatch.Frequency);   // safety net: a stalled world never ticks
        _controlMoveWaits.Add(w);
        host.AgentChannel.ControlReplyDeferred = true;
        con.Print("move wait pending");
    }

    string _controlMoveWaitLast = "";

    /// <summary>
    /// One check; true (reply JSON in <see cref="_controlMoveWaitLast"/>) once arrived, stuck or timed out.
    /// </summary>
    bool ControlMoveWaitProgress(ControlMoveWait w, out string json)
    {
        json = "";
        var p = host.Player.AgentOrders.ControlPosition($"c:{w.Who.Slot}.{w.Who.Generation}");
        long ticks = host.Session.World.Tick - w.StartTick;
        string result;
        float dist;
        if (p is not { } pos)
        {
            dist = -1;
            result = "lost";
        }
        else
        {
            dist = Vector2.Distance(new Vector2(pos.X, pos.Z), new Vector2(w.X, w.Z));
            if (dist <= MoveWaitArriveRadius) result = "arrived";
            else
            {
                if (dist <= w.BestDist - MoveWaitProgressStep)
                {
                    w.BestDist = dist;
                    w.ProgressTick = host.Session.World.Tick;
                }

                if (host.Session.World.Tick - w.ProgressTick >= w.StuckTicks) result = "stuck";
                else if (ticks >= w.TimeoutTicks) result = "timeout";
                else return false;
            }
        }
        var c = CultureInfo.InvariantCulture;
        json = p is { } q
            ? $"{{\"result\":\"{result}\",\"pos\":[{q.X.ToString("0.#", c)},{q.Z.ToString("0.#", c)}],\"dist_to_target\":{dist.ToString("0.#", c)},"
                + $"\"ticks\":{ticks},\"id\":\"c:{w.Who.Slot}.{w.Who.Generation}\"}}"
            : $"{{\"result\":\"lost\",\"pos\":null,\"dist_to_target\":null,\"ticks\":{ticks},\"id\":\"c:{w.Who.Slot}.{w.Who.Generation}\"}}";
        _controlMoveWaitLast = json;
        return true;
    }

    /// <summary>
    /// Every poll: finish the pending waited moves.
    /// </summary>
    internal void ControlMoveWaitPoll()
    {
        for (int i = _controlMoveWaits.Count - 1; i >= 0; i--)
        {
            var w = _controlMoveWaits[i];
            if (ControlMoveWaitProgress(w, out string json))
            {
                _controlMoveWaits.RemoveAt(i);
                host.AgentChannel.ControlWrite(w.Id, true, "", json);
            }
            else if (Stopwatch.GetTimestamp() >= w.RealDeadline)
            {
                _controlMoveWaits.RemoveAt(i);
                host.AgentChannel.ControlWrite(w.Id, false, "order: the world did not tick (wait gave up)");
            }
        }
    }
}