using Meitou.Game.DevTools;
using Meitou.Simulation;

namespace Meitou.Game;

sealed partial class GameHost
{
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
    }
}
