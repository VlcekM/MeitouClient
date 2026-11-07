using Meitou.Simulation;

namespace Meitou.Game;

sealed partial class GameHost
{
    readonly System.Collections.Concurrent.ConcurrentQueue<string> rootLog = new();

    void RegisterControlCombatCommands(Action<string, string, string, Action<IReadOnlyList<string>, DevConsole>> add, Func<DevConsole, bool> loaded)
    {
        add("combat-status", "combat-status [--id c:slot.gen]", "read-only check of a character's fight (default the selected, else the first squad member) as JSON: stance flags (block hold passive ranged taunt sneak jobs), status (attacking / blocking / idle / ko / dead), target, nearest hostile, damage dealt and taken over 5 s and 30 s (approximate, see its note), blows over 30 s (thrown landed takenHits blocks parried evaded dodges) and warn lines (block on while attacking, no damage dealt in 30 s with a hostile within 30 m, passive/hold set)", (args, con) =>
        {
            if (!loaded(con)) return;
            CharacterId? id = null;
            if (args.Count == 2 && args[0] == "--id" && PlayerInterface.TryParseCharacter(args[1], out var parsed)) id = parsed;
            else if (args.Count != 0) { con.Error("usage: combat-status [--id c:<slot>.<gen>]"); return; }
            string? json = player.ControlCombatStatusJson(id, out string error);
            if (json is null) con.Error(error); else PrintData(con, json);
        });

        add("root-probe", "root-probe [--id c:slot.gen] [--at x z] [length] [directions]", "debug the root-motion wall guard: from the character's spot (default the first squad member) step <length> units (default 6) in <directions> headings (default 16) and print how far each gets (wall slide, end-point refusal); read-only", (args, con) =>
        {
            if (!loaded(con)) return;
            CharacterId? id = null;
            System.Numerics.Vector2? at = null;
            var nums = new List<string>();
            for (int a = 0; a < args.Count; a++)
                if (args[a] == "--id" && a + 1 < args.Count && PlayerInterface.TryParseCharacter(args[a + 1], out var parsed)) { id = parsed; a++; }
                else if (args[a] == "--at" && a + 2 < args.Count && Num(args[a + 1], out float ax) && Num(args[a + 2], out float az)) { at = new System.Numerics.Vector2(ax, az); a += 2; }
                else nums.Add(args[a]);
            float length = nums.Count > 0 && Num(nums[0], out float l) ? l : 6;
            int n = nums.Count > 1 && int.TryParse(nums[1], out int d) ? d : 16;
            id ??= player.ControlSquad() is { Count: > 0 } squad ? squad[0] : null;
            if (id is not { } who) { con.Error("root-probe: no character"); return; }
            foreach (string line in AnimationSystem.ProbeRoot(session.World, who, length, n, at)) con.Print(line);
        });

        add("root-scan", "root-scan <x> <z> <radius> [spacing] [length]", "debug the root-motion wall guard over an area: probe 8 steps of <length> (default 3) from every walkable grid spot (default spacing 4) and count free / slid / cut / refused ones; read-only", (args, con) =>
        {
            if (!loaded(con)) return;
            if (args.Count < 3 || !Num(args[0], out float x) || !Num(args[1], out float z) || !Num(args[2], out float r)) { con.Error("usage: root-scan <x> <z> <radius> [spacing] [length]"); return; }
            float sp = args.Count > 3 && Num(args[3], out float s1) ? s1 : 4, len = args.Count > 4 && Num(args[4], out float l1) ? l1 : 3;
            if (player.ControlSquad() is not { Count: > 0 } squad) { con.Error("root-scan: no character"); return; }
            foreach (string line in AnimationSystem.ScanRoot(session.World, squad[0], x, z, r, sp, len)) con.Print(line);
        });

        add("root-log", "root-log on|off|dump", "log every root-motion step that was shortened (wall slide, body, end-point refusal) by character, then dump and clear the log", (args, con) =>
        {
            if (!loaded(con)) return;
            if (args.Count == 1 && args[0] == "on")
            {
                AnimationSystem.RootClamp = (who, at, want, got, why, refused) =>
                {
                    if (why == AnimationSystem.RootBlock.None && !refused) return;
                    if (rootLog.Count < 4000) rootLog.Enqueue($"t{session.World.Tick} c{who} at {at.X:0.0},{at.Z:0.0} want {want.Length():0.00} got {got.Length():0.00} {(refused ? "REFUSED-END" : why)}");
                };
                con.Print("root-log on");
            }
            else if (args.Count == 1 && args[0] == "off") { AnimationSystem.RootClamp = null; con.Print("root-log off"); }
            else if (args.Count == 1 && args[0] == "dump") { int k = 0; while (rootLog.TryDequeue(out string? line)) { con.Print(line); k++; } con.Print($"{k} lines"); }
            else con.Error("usage: root-log on|off|dump");
        });
    }
}
