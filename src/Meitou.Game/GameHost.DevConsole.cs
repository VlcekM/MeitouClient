using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Rendering;
using Meitou.Simulation;
using SilkKey = Silk.NET.Input.Key;

namespace Meitou.Game;

/// <summary>
/// The dev console (Meitou only; docs/game/ui-input.md "Dev console"): Insert or Ctrl+` opens a panel over the top of the screen with an input line; while open it takes every key, as the debug
/// menu does. Commands that change the world enqueue <see cref="DebugCommand"/>s like the menu's cheats, so they apply between ticks; the text side is <see cref="DevConsole"/>.
/// </summary>
sealed partial class GameHost
{
    readonly DevConsole devConsole = new();
    readonly Dictionary<bool, List<string>> consoleNames = [];
    bool consoleRegistered;
    /// <summary>The pointer in framebuffer pixels as of the last frame, for the commands that mean the point under the cursor.</summary>
    Vector2 consolePointer;

    bool ConsoleOpen => devConsole.Open;

    /// <summary>Insert, or Ctrl plus the backquote key (which the game binds to select all, so it stays free only with Ctrl).</summary>
    static bool IsConsoleToggle(SilkKey key, bool ctrl) => key == SilkKey.Insert || (ctrl && key == SilkKey.GraveAccent);

    void ToggleConsole()
    {
        if (!consoleRegistered) RegisterConsoleCommands();
        devConsole.Open = !devConsole.Open;
        if (devConsole.Open) debugMenuOpen = false;
    }

    /// <summary>A key pressed while the console is open (the toggle key is handled by the caller).</summary>
    void ConsoleKey(Silk.NET.Input.IKeyboard keyboard, SilkKey key)
    {
        bool ctrl = keyboard.IsKeyPressed(SilkKey.ControlLeft) || keyboard.IsKeyPressed(SilkKey.ControlRight);
        switch (key)
        {
            case SilkKey.Escape: devConsole.Open = false; break;
            case SilkKey.Enter or SilkKey.KeypadEnter: devConsole.Submit(); break;
            case SilkKey.Backspace: devConsole.Backspace(); break;
            case SilkKey.Delete: devConsole.Delete(); break;
            case SilkKey.Left: devConsole.MoveCaret(devConsole.Caret - 1); break;
            case SilkKey.Right: devConsole.MoveCaret(devConsole.Caret + 1); break;
            case SilkKey.Home: devConsole.MoveCaret(0); break;
            case SilkKey.End: devConsole.MoveCaret(devConsole.Input.Length); break;
            case SilkKey.Up: devConsole.HistoryStep(-1); break;
            case SilkKey.Down: devConsole.HistoryStep(1); break;
            case SilkKey.Tab: devConsole.Complete(); break;
            case SilkKey.PageUp: devConsole.Scroll += 8; break;
            case SilkKey.PageDown: devConsole.Scroll = Math.Max(0, devConsole.Scroll - 8); break;
            case SilkKey.V when ctrl && keyboard.ClipboardText is { } pasted: devConsole.Paste(pasted); break;
        }
    }

    void DrawConsole(DebugOverlay o, int width, int height)
    {
        float pad = 10, lineH = o.LineHeight;
        int rows = Math.Max(4, (int)(height * 0.42f / lineH));
        float panelH = (rows + 1) * lineH + 3 * pad;
        o.Rect(0, 0, width, panelH, new Vector4(0.03f, 0.04f, 0.06f, 0.88f));
        o.Rect(0, panelH, width, panelH + 2, new Vector4(0.25f, 0.4f, 0.65f, 1));
        // Visual lines, newest last: each entry wrapped to the panel's width, the continuation indented.
        var visual = new List<(string Text, DevConsole.Kind Kind)>();
        foreach (var line in devConsole.Lines)
        {
            string prefix = line.Kind == DevConsole.Kind.Input ? "> " : "";
            var wrapped = o.WrapText(prefix + line.Text, width - 2 * pad);
            for (int i = 0; i < wrapped.Count; i++) visual.Add((i == 0 ? wrapped[i] : "  " + wrapped[i], line.Kind));
        }
        int maxScroll = Math.Max(0, visual.Count - rows);
        devConsole.Scroll = Math.Min(devConsole.Scroll, maxScroll);
        int last = visual.Count - devConsole.Scroll;
        float y = pad;
        for (int i = Math.Max(0, last - rows); i < last; i++, y += lineH)
        {
            var colour = visual[i].Kind switch
            {
                DevConsole.Kind.Input => new Vector4(0.55f, 0.8f, 1f, 1),
                DevConsole.Kind.Error => new Vector4(1f, 0.45f, 0.4f, 1),
                _ => DebugOverlay.TextColour,
            };
            o.Text(visual[i].Text, pad, y, colour);
        }
        if (devConsole.Scroll > 0) o.Text($"[{devConsole.Scroll} more below: PageDown]", width - pad - o.CharWidth * 28, pad, new Vector4(0.7f, 0.75f, 0.8f, 1));
        float inputY = pad * 2 + rows * lineH;
        o.Rect(pad / 2, inputY - 2, width - pad / 2, inputY + lineH + 2, new Vector4(0.12f, 0.16f, 0.24f, 0.9f));
        string prompt = "> ";
        o.Text(prompt + devConsole.Input, pad, inputY, DebugOverlay.TextColour);
        if (Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 2) % 2 == 0)
        {
            float caretX = pad + (prompt.Length + devConsole.Caret) * o.CharWidth;
            o.Rect(caretX, inputY, caretX + 2, inputY + lineH, DebugOverlay.TextColour);
        }
        o.Flush(width, height);
    }

    // ---- commands

    /// <summary>The world point under the cursor (terrain, a floor or a wall top, as a move order picks it), else null.</summary>
    Vector3? ConsoleCursorPoint() => player.Ray(consolePointer) is { } ray ? player.MoveHit(ray.Origin, ray.Direction) : null;

    /// <summary>Names of the character or item records, sorted, for Tab completion.</summary>
    List<string> ConsoleRecordNames(bool character)
    {
        if (consoleNames.TryGetValue(character, out var cached)) return cached;
        bool Fits(FcsRecordType t) => character ? t is FcsRecordType.CHARACTER or FcsRecordType.ANIMAL_CHARACTER : DebugGiveItem.ItemTypes.Contains(t);
        var names = scene.Database is { } db
            ? db.Records.Values.Where(r => Fits(r.Type) && r.Name.Length > 0).Select(r => r.Name).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList() : [];
        return consoleNames[character] = names;
    }

    static IEnumerable<string> ConsoleMatch(IEnumerable<string> names, string typed)
    {
        var all = names as IReadOnlyCollection<string> ?? [.. names];
        var starts = all.Where(n => n.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).ToList();
        return starts.Count > 0 || typed.Length == 0 ? starts.Count > 0 ? starts : all : all.Where(n => n.Contains(typed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The placed town a typed name means (ignoring case): the exact name, else the shortest name starting with it. Its position is the town's centre as the level data places it.</summary>
    TownSite? ConsoleTown(string typed, out List<string> others)
    {
        others = [];
        if (popData is not { } data || typed.Length == 0) return null;
        var named = data.Sites.Where(s => s.Town.Type is not (TownType.NestMarker or TownType.Null) && s.Town.Name.Length > 0).ToList();
        var exact = named.Where(s => string.Equals(s.Town.Name, typed, StringComparison.OrdinalIgnoreCase)).ToList();
        var hits = exact.Count > 0 ? exact : [.. named.Where(s => s.Town.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.Town.Name.Length).ThenBy(s => s.Town.Name, StringComparer.Ordinal)];
        if (hits.Count == 0) return null;
        others = [.. hits.Select(s => s.Town.Name).Distinct().Where(n => n != hits[0].Town.Name).Take(5)];
        return hits[0];
    }

    void RegisterConsoleCommands()
    {
        consoleRegistered = true;
        var c = devConsole;
        void Add(string name, string usage, string help, Action<IReadOnlyList<string>, DevConsole> run, Func<int, string, IEnumerable<string>>? complete = null) =>
            c.Register(new ConsoleCommand(name, usage, help, run) { Complete = complete });
        void Send(Func<long, SimCommand> command) => session.World.Commands.Enqueue(command(session.World.Tick));
        bool Need(IReadOnlyList<string> args, int min, int max, ConsoleCommand self)
        {
            if (args.Count >= min && args.Count <= max) return true;
            c.Error($"usage: {self.Usage}");
            return false;
        }
        ConsoleCommand Self(string name) => c.Find(name)!;

        Add("help", "help [command]", "list the commands, or show one command's usage", (args, con) =>
        {
            if (args.Count == 0) { foreach (var cmd in con.Commands) con.Print($"{cmd.Usage}  -  {cmd.Help}"); return; }
            if (con.Find(args[0]) is { } one) con.Print($"{one.Usage}\n{one.Help}");
            else con.Error($"unknown command '{args[0]}'");
        }, (i, typed) => i == 0 ? c.Commands.Select(x => x.Name).Where(n => n.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) : []);

        Add("clear", "clear", "empty the scrollback", (_, con) => con.Clear());

        // Every command that the F10 menu also has runs the menu's own DebugApply / DebugGive (GameHost.DebugMenu.cs), so there is one implementation; only kill is the console's.
        void Report(DevConsole con, string text, string? suffix = null)
        {
            if (debugFailed) con.Error(text);
            else con.Print(text + suffix);
        }

        Add("money", "money <delta>", "add (or with a minus take away) cats; never below 0", (args, con) =>
        {
            if (!Need(args, 1, 1, Self("money"))) return;
            Report(con, DebugApply(DebugRow.Money, args[0]));
        });

        Add("heal", "heal", "heal the selected characters fully (else the inspected one)", (args, con) =>
        {
            if (!Need(args, 0, 0, Self("heal"))) return;
            Report(con, DebugApply(DebugRow.Heal, ""));
        });

        Add("maxstats", "maxstats", "every stat to 100 for the selected characters (else the inspected one)", (args, con) =>
        {
            if (!Need(args, 0, 0, Self("maxstats"))) return;
            Report(con, DebugApply(DebugRow.MaxSkills, ""));
        });

        Add("give", "give \"<item record>\" [qty] [quality]", "put items on the first selected character; quality 0-100 (armour, weapons, crossbows, limbs)", (args, con) =>
        {
            if (!Need(args, 1, 3, Self("give"))) return;
            int quantity = 1, quality = 0;
            if (args.Count > 1 && !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out quantity)) { con.Error($"give: quantity '{args[1]}' is not a whole number"); return; }
            if (args.Count > 2 && !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out quality)) { con.Error($"give: quality '{args[2]}' is not a whole number"); return; }
            Report(con, DebugGive(args[0], quantity, quality));
        }, (i, typed) => i == 0 ? ConsoleMatch(ConsoleRecordNames(character: false), typed) : []);

        Add("spawn", "spawn \"<character record>\"", "spawn a hostile character at the cursor", (args, con) =>
        {
            if (!Need(args, 1, 1, Self("spawn"))) return;
            Report(con, DebugApply(DebugRow.SpawnNpc, args[0], at: ConsoleCursorPoint()));
        }, (i, typed) => i == 0 ? ConsoleMatch(ConsoleRecordNames(character: true), typed) : []);

        Add("tpcursor", "tpcursor", "teleport the selected characters to the point under the cursor", (args, con) =>
        {
            if (!Need(args, 0, 0, Self("tpcursor"))) return;
            if (ConsoleCursorPoint() is not { } at) { con.Error("no point under the cursor"); return; }
            Report(con, DebugApply(DebugRow.Teleport, "", at: at));
        });

        Add("tp", "tp <x> <z> | tp <town name>", "teleport the selected characters to world X Z, or to a town (name or its start, ignoring case; the town's centre)", (args, con) =>
        {
            if (!Need(args, 1, 8, Self("tp"))) return;
            if (args.Count == 2 && float.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                && float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
            {
                Report(con, DebugApply(DebugRow.Teleport, "", at: new Vector3(x, 0, z)));
                if (!debugFailed) CentreCameraOn(x, z);
                return;
            }
            string typed = string.Join(' ', args);
            if (ConsoleTown(typed, out var others) is not { } town) { con.Error($"no town matches '{typed}'"); return; }
            Report(con, DebugApply(DebugRow.Teleport, "", at: town.Position), $" ({town.Town.Name}){(others.Count > 0 ? $"; also matches {string.Join(", ", others)}" : "")}");
            if (!debugFailed) CentreCameraOn(town.Position.X, town.Position.Z);
        }, (i, typed) => popData is { } d
            ? ConsoleMatch(d.Sites.Where(s => s.Town.Type is not (TownType.NestMarker or TownType.Null)).Select(s => s.Town.Name).Where(n => n.Length > 0).Distinct().Order(StringComparer.OrdinalIgnoreCase), typed) : []);

        Add("kill", "kill", "kill the selected characters", (args, con) =>
        {
            if (!Need(args, 0, 0, Self("kill"))) return;
            var selected = session.World.Player.Selection.ToArray();
            if (selected.Length == 0) { con.Error("select characters first"); return; }
            Send(t => new DebugKill(selected) { Tick = t });
            con.Print($"killed {selected.Length}");
        });

        Add("research", "research", "finish every research", (args, con) =>
        {
            if (!Need(args, 0, 0, Self("research"))) return;
            Report(con, DebugApply(DebugRow.UnlockResearch, ""));
        });

        c.Print("Dev console (Meitou only). help lists the commands, Tab completes, Up/Down is the history, Esc closes.");
    }
}
