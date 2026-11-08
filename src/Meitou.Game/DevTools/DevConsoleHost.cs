using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Game.Host;
using Meitou.Game.Rendering.Overlays;
using Meitou.Simulation;
using Meitou.Simulation.Population;
using SilkKey = Silk.NET.Input.Key;

namespace Meitou.Game.DevTools;

/// <summary>
/// The dev console (Meitou only; docs/game/ui-input.md "Dev console"): Insert or Ctrl+` opens a panel over the top of the screen with an input line; while open it takes every key, as the debug
/// menu does. Commands that change the world enqueue <see cref="DebugCommand"/>s like the menu's cheats, so they apply between ticks; the text side is <see cref="DevTools.DevConsole"/>.
/// </summary>
sealed class DevConsoleHost(GameHost host)
{
    internal readonly DevConsole DevConsole = new();

    /// <summary>The <c>version --say</c> bubble waiting for its moment: the speaker picked when the command was typed, the text, and the <see cref="Stopwatch"/> timestamp it is due at.</summary>
    (long DueTimestamp, CharacterId Speaker, string Text)? _pendingVersionSay;

    /// <summary>Once per frame (also while the console is open): enqueues the pending <c>version --say</c> bubble when it is due; dropped when no game is loaded, silently skipped when the speaker is gone or dead.</summary>
    internal void PumpPendingSay()
    {
        if (_pendingVersionSay is not { } pending) return;
        if (host.Covered || host.Player.TitleOpen) { _pendingVersionSay = null; return; }
        if (Stopwatch.GetTimestamp() < pending.DueTimestamp) return;
        _pendingVersionSay = null;
        if (host.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == pending.Speaker) is not { } speaker || speaker.Body is { Dead: true }) return;
        host.Session.World.Commands.Enqueue(new DebugSay(pending.Speaker, pending.Text) { Tick = host.Session.World.Tick });
    }

    /// <summary>The console that ran <c>explode</c> and waits for its result line (see <see cref="GameHost.WorldNews"/>).</summary>
    internal DevConsole? ExplodeAsker;
    readonly Dictionary<bool, List<string>> _consoleNames = [];
    internal bool ConsoleRegistered;
    /// <summary>The pointer in framebuffer pixels as of the last frame, for the commands that mean the point under the cursor.</summary>
    internal Vector2 ConsolePointer;
    /// <summary>--headless: the pointer the control channel's <c>mouse</c> command gave, in screen pixels (null until it does); a window's pointer is read from the window instead.</summary>
    internal Vector2? HeadlessMouse;
    /// <summary>The control channel's <c>click</c>: steps the interactive loop plays one per frame ('m' move, 'i' idle, 'd' press, 'u' release; right button; pixel).</summary>
    internal readonly Queue<(char Kind, bool Right, Vector2 At)> PointerSteps = new();
    /// <summary>The control channel's <c>quit</c>: the interactive loop ends at its next pass (saves the settings, disposes as on any exit).</summary>
    internal bool ControlQuit;

    internal bool ConsoleOpen => DevConsole.Open;

    /// <summary>Insert, or Ctrl plus the backquote key (which the game binds to select all, so it stays free only with Ctrl).</summary>
    internal static bool IsConsoleToggle(SilkKey key, bool ctrl) => key == SilkKey.Insert || (ctrl && key == SilkKey.GraveAccent);

    internal void ToggleConsole()
    {
        if (!ConsoleRegistered) RegisterConsoleCommands();
        DevConsole.Open = !DevConsole.Open;
        if (DevConsole.Open) host.DebugMenu.DebugMenuOpen = false;
    }

    /// <summary>A key pressed while the console is open (the toggle key is handled by the caller).</summary>
    internal void ConsoleKey(Silk.NET.Input.IKeyboard keyboard, SilkKey key)
    {
        bool ctrl = keyboard.IsKeyPressed(SilkKey.ControlLeft) || keyboard.IsKeyPressed(SilkKey.ControlRight);
        switch (key)
        {
            case SilkKey.Escape: DevConsole.Open = false; break;
            case SilkKey.Enter or SilkKey.KeypadEnter: DevConsole.Submit(); break;
            case SilkKey.Backspace: DevConsole.Backspace(); break;
            case SilkKey.Delete: DevConsole.Delete(); break;
            case SilkKey.Left: DevConsole.MoveCaret(DevConsole.Caret - 1); break;
            case SilkKey.Right: DevConsole.MoveCaret(DevConsole.Caret + 1); break;
            case SilkKey.Home: DevConsole.MoveCaret(0); break;
            case SilkKey.End: DevConsole.MoveCaret(DevConsole.Input.Length); break;
            case SilkKey.Up: DevConsole.HistoryStep(-1); break;
            case SilkKey.Down: DevConsole.HistoryStep(1); break;
            case SilkKey.Tab: DevConsole.Complete(); break;
            case SilkKey.PageUp: DevConsole.Scroll += 8; break;
            case SilkKey.PageDown: DevConsole.Scroll = Math.Max(0, DevConsole.Scroll - 8); break;
            case SilkKey.V when ctrl && keyboard.ClipboardText is { } pasted: DevConsole.Paste(pasted); break;
        }
    }

    internal void DrawConsole(DebugOverlay o, int width, int height)
    {
        float pad = 10, lineH = o.LineHeight;
        int rows = Math.Max(4, (int)(height * 0.42f / lineH));
        float panelH = (rows + 1) * lineH + 3 * pad;
        o.Rect(0, 0, width, panelH, new Vector4(0.03f, 0.04f, 0.06f, 0.88f));
        o.Rect(0, panelH, width, panelH + 2, new Vector4(0.25f, 0.4f, 0.65f, 1));
        // Visual lines, newest last: each entry wrapped to the panel's width, the continuation indented.
        var visual = new List<(string Text, DevConsole.Kind Kind)>();
        foreach (var line in DevConsole.Lines)
        {
            string prefix = line.Kind == DevConsole.Kind.Input ? "> " : "";
            var wrapped = o.WrapText(prefix + line.Text, width - 2 * pad);
            for (int i = 0; i < wrapped.Count; i++) visual.Add((i == 0 ? wrapped[i] : "  " + wrapped[i], line.Kind));
        }
        int maxScroll = Math.Max(0, visual.Count - rows);
        DevConsole.Scroll = Math.Min(DevConsole.Scroll, maxScroll);
        int last = visual.Count - DevConsole.Scroll;
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
        if (DevConsole.Scroll > 0) o.Text($"[{DevConsole.Scroll} more below: PageDown]", width - pad - o.CharWidth * 28, pad, new Vector4(0.7f, 0.75f, 0.8f, 1));
        float inputY = pad * 2 + rows * lineH;
        o.Rect(pad / 2, inputY - 2, width - pad / 2, inputY + lineH + 2, new Vector4(0.12f, 0.16f, 0.24f, 0.9f));
        string prompt = "> ";
        o.Text(prompt + DevConsole.Input, pad, inputY, DebugOverlay.TextColour);
        if (Stopwatch.GetTimestamp() / (Stopwatch.Frequency / 2) % 2 == 0)
        {
            float caretX = pad + (prompt.Length + DevConsole.Caret) * o.CharWidth;
            o.Rect(caretX, inputY, caretX + 2, inputY + lineH, DebugOverlay.TextColour);
        }
        o.Flush(width, height);
    }

    // ---- commands

    /// <summary>The world point under the cursor (terrain, a floor or a wall top, as a move order picks it), else null.</summary>
    Vector3? ConsoleCursorPoint() => host.Player.Ray(ConsolePointer) is { } ray ? host.Player.Picking.MoveHit(ray.Origin, ray.Direction) : null;

    /// <summary>Names of the character or item records, sorted, for Tab completion.</summary>
    List<string> ConsoleRecordNames(bool character)
    {
        if (_consoleNames.TryGetValue(character, out var cached)) return cached;
        bool Fits(FcsRecordType t) => character ? t is FcsRecordType.Character or FcsRecordType.AnimalCharacter : DebugGiveItem.ItemTypes.Contains(t);
        var names = host.Scene.Database is { } db
            ? db.Records.Values.Where(r => Fits(r.Type) && r.Name.Length > 0).Select(r => r.Name).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList() : [];
        return _consoleNames[character] = names;
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
        if (host.PopData is not { } data || typed.Length == 0) return null;
        var named = data.Sites.Where(s => s.Town.Type is not (TownType.NestMarker or TownType.Null) && s.Town.Name.Length > 0).ToList();
        var exact = named.Where(s => string.Equals(s.Town.Name, typed, StringComparison.OrdinalIgnoreCase)).ToList();
        var hits = exact.Count > 0 ? exact : [.. named.Where(s => s.Town.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).OrderBy(s => s.Town.Name.Length).ThenBy(s => s.Town.Name, StringComparer.Ordinal)];
        if (hits.Count == 0) return null;
        others = [.. hits.Select(s => s.Town.Name).Distinct().Where(n => n != hits[0].Town.Name).Take(5)];
        return hits[0];
    }

    /// <summary>Edit distance between two command names (for the "close names" hint of help).</summary>
    static int HelpDistance(string a, string b) => DevConsole.EditDistance(a, b);

    /// <summary>Argument completion for the commands the control-side registration helpers add (they take no completer): character ids from the live world, carried items, flag values. Any word starting with a dash completes the command's own flags (DevConsole.Complete).</summary>
    void RegisterConsoleCompletions()
    {
        var c = DevConsole;
        IEnumerable<string> Starting(IEnumerable<string> names, string typed) => names.Where(n => n.StartsWith(typed, StringComparison.OrdinalIgnoreCase));
        IEnumerable<string> Characters(string typed) => Starting(host.Player.AgentOrders.ControlCharacterKeys(), typed);
        c.FlagValues = (flag, typed) => flag.ToLowerInvariant() switch
        {
            "--id" => Characters(typed),
            "--aggro" => ConsoleMatch(ConsoleRecordNames(character: false), typed),
            _ => [],
        };
        foreach (var name in new[] { "follow", "loot", "firstaid", "carry" }) c.SetComplete(name, (i, typed) => i == 0 ? Characters(typed) : []);
        c.SetComplete("select", (_, typed) => Characters(typed));
        c.SetComplete("version", (i, typed) => i == 0 ? Starting(["--say"], typed) : []);
        c.SetComplete("order", (i, typed) => i == 0 ? Starting(["move", "attack"], typed) : i == 1 && c.CompletionArgs is ["attack"] ? Characters(typed) : []);
        c.SetComplete("spawn-at", (i, typed) => i == 2 ? ConsoleMatch(ConsoleRecordNames(character: true), typed) : []);
        c.SetComplete("equip", (i, typed) => i == 0 ? ConsoleMatch(host.Player.AgentOrders.ControlCarriedItemNames(), typed) : []);
        c.SetComplete("pickup", (i, typed) => i == 0 ? Starting(host.Player.AgentOrders.ControlGroundItemKeys(), typed) : []);
        c.SetComplete("door", (i, typed) => i == 0 ? Starting(["nearest"], typed) : []);
        foreach (var name in new[] { "godmode", "guard", "tpmap" }) c.SetComplete(name, (i, typed) => i == 0 ? Starting(["on", "off"], typed) : []);
        IEnumerable<string> Levels(string typed) => Starting(["1", "2", "3", "4", "5", "underkill", "light", "normal", "overkill", "ridiculous"], typed);
        c.SetComplete("explode-click", (i, typed) => i == 0 ? [.. Starting(["on", "off"], typed), .. Levels(typed)] : c.CompletionArgs is [.., "--level" or "--preset"] ? Levels(typed) : i == 1 && c.CompletionArgs is ["on"] ? Levels(typed) : []);
        c.SetComplete("explode", (i, typed) => i == 0 || c.CompletionArgs is [.., "--level" or "--preset"] ? Levels(typed) : []);
    }

    internal void RegisterConsoleCommands()
    {
        ConsoleRegistered = true;
        var c = DevConsole;
        void Add(string name, string usage, string help, Action<IReadOnlyList<string>, DevConsole> run, Func<int, string, IEnumerable<string>>? complete = null) =>
            c.Register(new ConsoleCommand(name, usage, help, run) { Complete = complete });
        void Send(Func<long, SimCommand> command) => host.Session.World.Commands.Enqueue(command(host.Session.World.Tick));
        bool Need(IReadOnlyList<string> args, int min, int max, ConsoleCommand self)
        {
            if (args.Count >= min && args.Count <= max) return true;
            c.Error($"usage: {self.Usage}");
            return false;
        }
        ConsoleCommand Self(string name) => c.Find(name)!;

        Add("help", "help [command]", "list the commands with their usage, show one command's usage and description, or (no exact name) search names, usage and descriptions for the word", (args, con) =>
        {
            bool channel = con.Tap is not null;   // the control channel gets the short list and the structured reply
            if (args.Count > 1) { con.Error("usage: help [command]"); return; }
            if (args.Count == 0)
            {
                if (!channel) { foreach (var cmd in con.Commands) con.Print($"{cmd.Usage}  -  {cmd.Help}"); return; }
                var all = con.Commands.ToList();
                host.AgentChannel.ControlData = System.Text.Json.JsonSerializer.Serialize(new { commands = all.Select(x => new { name = x.Name, usage = x.Usage }) });
                foreach (var cmd in all) con.Print(cmd.Usage);
                con.Print("help <command> shows the description");
                return;
            }
            string name = args[0].ToLowerInvariant();
            if (con.Find(name) is { } one)
            {
                if (channel) host.AgentChannel.ControlData = System.Text.Json.JsonSerializer.Serialize(new { name = one.Name, usage = one.Usage, help = one.Help, flags = one.Flags });
                con.Print($"{one.Usage}\n{one.Help}" + (one.Flags.Count > 0 ? "\nflags: " + string.Join(" ", one.Flags) : ""));
                return;
            }
            // No exact name: search the names, usage and description text for the word (case-insensitive).
            var found = con.Commands.Where(x => x.Name.Contains(name, StringComparison.OrdinalIgnoreCase) || x.Usage.Contains(name, StringComparison.OrdinalIgnoreCase) || x.Help.Contains(name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Name.Contains(name, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
            if (found.Count > 0)
            {
                if (channel) host.AgentChannel.ControlData = System.Text.Json.JsonSerializer.Serialize(new { commands = found.Select(x => new { name = x.Name, usage = x.Usage }) });
                foreach (var cmd in found) con.Print($"{cmd.Usage}  -  {cmd.Help}");
                return;
            }
            var names = con.Commands.Select(x => x.Name).ToList();
            var close = names.Where(n => HelpDistance(n, name) <= 2).ToList();
            con.Error($"nothing matches '{args[0]}'" + (close.Count > 0 ? "; close names: " + string.Join(", ", close.Take(8)) : "; help lists all commands"));
        }, (i, typed) => i == 0 ? c.Commands.Select(x => x.Name).Where(n => n.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) : []);

        Add("clear", "clear", "empty the scrollback", (_, con) => con.Clear());

        Add("version", "version [--say]", "print the Meitou build tag and commit; --say also makes the selected character say it in game after 1 second", (args, con) =>
        {
            bool say = false;
            if (args.Count > 1 || (args.Count == 1 && !(say = args[0].Equals("--say", StringComparison.OrdinalIgnoreCase)))) { con.Error("usage: version [--say]"); return; }
            string text = $"Meitou {BuildVersion.Tag} ({BuildVersion.Commit})";
            if (say)
            {
                if (host.Covered || host.Player.TitleOpen) { con.Error("version: load a game first (no world is loaded yet)"); return; }
                var lead = host.Session.CurrentSnapshot.Characters.FirstOrDefault(ch => ch.IsPlayer && ch.Selected && ch.Body is not { Dead: true }) ?? host.Session.CurrentSnapshot.Characters.FirstOrDefault(ch => ch.IsPlayer && ch.Body is not { Dead: true });
                if (lead is null) { con.Error("version: no living squad member to speak"); return; }
                _pendingVersionSay = (Stopwatch.GetTimestamp() + Stopwatch.Frequency, lead.Id, text);
            }
            con.Print(text);
        });

        // Every command that the F10 menu also has runs the menu's own DebugApply / DebugGive (DebugMenu), so there is one implementation; only kill is the console's.
        void Report(DevConsole con, string text, string? suffix = null)
        {
            if (host.DebugMenu.DebugFailed) con.Error(text);
            else con.Print(text + suffix);
        }

        Add("money", "money <delta>", "add (or with a minus take away) cats; never below 0", (args, con) =>
        {
            if (!Need(args, 1, 1, Self("money"))) return;
            Report(con, host.DebugMenu.DebugApply(DebugMenu.DebugRow.Money, args[0]));
        });

        Add("heal", "heal", "heal the selected characters fully (else the inspected one)", (args, con) =>
        {
            if (!Need(args, 0, 0, Self("heal"))) return;
            Report(con, host.DebugMenu.DebugApply(DebugMenu.DebugRow.Heal, ""));
        });

        Add("godmode", "godmode [on|off]", "god mode for every player character (also later recruits): all stats 1000 (10 x the game's maximum), healed 5 times a second (parts, limbs, blood, bleeding, hunger); off restores each character's own stats; no argument toggles; reports the state", (args, con) =>
        {
            if (!Need(args, 0, 1, Self("godmode"))) return;
            Report(con, host.DebugMenu.DebugApply(DebugMenu.DebugRow.Godmode, args.Count > 0 ? args[0] : ""));
        });

        Add("tpmap", "tpmap [on|off]", "the F10 switch Teleport on map: while on, every left click on the open map tab teleports the selected characters (else the lead) there and puts the camera on the spot; no argument toggles; reports the state", (args, con) =>
        {
            if (!Need(args, 0, 1, Self("tpmap"))) return;
            bool on = !host.Player.WorldMap.MapPickSticky;
            if (args.Count > 0)
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "on" or "1" or "true": on = true; break;
                    case "off" or "0" or "false": on = false; break;
                    default: con.Error("tpmap: on or off"); return;
                }
            }
            host.DebugMenu.SetMapTeleport(on);
            con.Print(on ? "map teleport on: open the map and click a spot" : "map teleport off");
        });

        Add("guard", "guard [on|off] [--radius <m>] [--animals]", "god-mode style safety: 4 times a second kills the hostile characters within the radius (default 30 m) of the selected (else first) squad member, as kill --radius does (never the squad or its faction; animals only with --animals or while one attacks a player character), and heals the player's characters as godmode does; stays on until turned off, switches itself off when a game loads; no argument toggles. Not for combat, survival or loot-source tests", (args, con) =>
        {
            Report(con, host.DebugMenu.DebugApply(DebugMenu.DebugRow.Guard, string.Join(' ', args)));
        });

        Add("explode", "explode [<x> <z> | <level 1-5>] [--level <1-5|name>] [--id <c:slot.gen>] [--radius <m>=40] [--force <n>=100] [--damage <n>=40] [--squad]", "debug blast (default centre: the character the info panel shows if it is not your squad, else the selected, else first, squad member; --id centres on a given character): every living character within the radius, except your squad unless --squad, takes cut damage falling off linearly with distance (full at the centre, 35% at the radius edge), is knocked out for 30 s if it survives, and is thrown away from the centre (force = speed in world units/s at the centre, falling off the same way). The result line (hit by distance band / knocked out / killed, or nobody in range) goes to the message log and a blood ring marks the centre (Blood setting on). Runs in the simulation, so not while the game is paused", (args, con) =>
        {
            Report(con, host.DebugMenu.DebugApply(DebugMenu.DebugRow.Explode, string.Join(' ', args)));
            if (!host.DebugMenu.DebugFailed) ExplodeAsker = con;   // the result line (made in the next tick) is printed here too
        });

        Add("explode-click", "explode-click [on|off] [<level 1-5>] [--level <1-5|name>] [--radius <m>=80] [--force <n>=160] [--damage <n>=100] [--squad]", "the F10 switch Explode on click: while on, a left click on the ground in the world (not on the interface, the F10 menu, the console or the map) explodes the point under the cursor like explode does (flags set the blast used for each click), and the click selects and orders nothing; no on/off argument toggles (flags alone turn it on); stays on until switched off or another game loads. Runs in the simulation, so not while the game is paused", (args, con) =>
        {
            Report(con, host.DebugMenu.DebugApply(DebugMenu.DebugRow.ExplodeClick, string.Join(' ', args)));
        });

        Add("maxstats", "maxstats", "every stat to 100 for the selected characters (else the inspected one)", (args, con) =>
        {
            if (!Need(args, 0, 0, Self("maxstats"))) return;
            Report(con, host.DebugMenu.DebugApply(DebugMenu.DebugRow.MaxSkills, ""));
        });

        Add("give", "give \"<item record>\" [qty] [quality] [--equip]", "put items on the first selected character; quality 0-100 (armour, weapons, crossbows, limbs); one unit is worn when its slot is free; --equip also wears it when the slot is taken (the worn item goes to the inventory, else the ground)", (args, con) =>
        {
            bool equipFlag = args.Any(a => a.Equals("--equip", StringComparison.OrdinalIgnoreCase));
            if (equipFlag) args = [.. args.Where(a => !a.Equals("--equip", StringComparison.OrdinalIgnoreCase))];
            if (!Need(args, 1, 3, Self("give"))) return;
            int quantity = 1, quality = 0;
            if (args.Count > 1 && !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out quantity)) { con.Error($"give: quantity '{args[1]}' is not a whole number"); return; }
            if (args.Count > 2 && !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out quality)) { con.Error($"give: quality '{args[2]}' is not a whole number"); return; }
            Report(con, host.DebugMenu.DebugGive(args[0], quantity, quality, equipFlag));
            // A paused game takes no tick: apply the give now so the reply (and a build right after) already sees the item.
            if (!host.DebugMenu.DebugFailed) host.Session.ApplyPausedCommandsNow();
        }, (i, typed) => i == 0 ? ConsoleMatch(ConsoleRecordNames(character: false), typed) : []);

        Add("spawn", "spawn \"<character record>\"", "spawn a hostile character at the cursor", (args, con) =>
        {
            if (!Need(args, 1, 1, Self("spawn"))) return;
            Report(con, host.DebugMenu.DebugApply(DebugMenu.DebugRow.SpawnNpc, args[0], at: ConsoleCursorPoint()));
        }, (i, typed) => i == 0 ? ConsoleMatch(ConsoleRecordNames(character: true), typed) : []);

        Add("tpcursor", "tpcursor", "teleport the selected characters to the point under the cursor", (args, con) =>
        {
            if (!Need(args, 0, 0, Self("tpcursor"))) return;
            if (ConsoleCursorPoint() is not { } at) { con.Error("no point under the cursor"); return; }
            Report(con, host.DebugMenu.DebugApply(DebugMenu.DebugRow.Teleport, "", at: at));
        });

        Add("tp", "tp <x> <z> | tp <town name>", "teleport the selected characters to world X Z, or to a town (name or its start, ignoring case; the town's centre)", (args, con) =>
        {
            if (!Need(args, 1, 8, Self("tp"))) return;
            if (args.Count == 2 && float.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                && float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
            {
                Report(con, host.DebugMenu.DebugApply(DebugMenu.DebugRow.Teleport, "", at: new Vector3(x, 0, z)));
                if (!host.DebugMenu.DebugFailed) host.DebugMenu.CentreCameraOn(x, z);
                return;
            }
            string typed = string.Join(' ', args);
            if (ConsoleTown(typed, out var others) is not { } town) { con.Error($"no town matches '{typed}'"); return; }
            Report(con, host.DebugMenu.DebugApply(DebugMenu.DebugRow.Teleport, "", at: town.Position), $" ({town.Town.Name}){(others.Count > 0 ? $"; also matches {string.Join(", ", others)}" : "")}");
            if (!host.DebugMenu.DebugFailed) host.DebugMenu.CentreCameraOn(town.Position.X, town.Position.Z);
        }, (i, typed) => host.PopData is { } d
            ? ConsoleMatch(d.Sites.Where(s => s.Town.Type is not (TownType.NestMarker or TownType.Null)).Select(s => s.Town.Name).Where(n => n.Length > 0).Distinct().Order(StringComparer.OrdinalIgnoreCase), typed) : []);

        Add("kill", "kill [--id <c:slot.gen>] | kill --radius <m> [<x> <z>] [--animals]", "kill the selected characters, or the one given by --id; --radius kills every living character within <m> metres (max 2000) of X Z, or of the selected (else first) squad member, in one command; never the player's squad or faction, animals only with --animals", (args, con) =>
        {
            if (args.Any(a => a.Equals("--radius", StringComparison.OrdinalIgnoreCase))) { host.AgentPlay.ControlKillArea(args, con); return; }
            if (!host.AgentPlay.ControlTargets(args, con, "kill", out var selected, out var rest)) return;
            if (rest.Count > 0) { con.Error("usage: kill [--id <c:slot.gen>] | kill --radius <m> [<x> <z>] [--animals]"); return; }
            Send(t => new DebugKill(selected) { Tick = t });
            con.Print($"killed {selected.Length}");
        });

        Add("research", "research [list | queue <tech>]", "finish every research; research list shows the research state, research queue <tech> queues one the way the research window does", (args, con) =>
        {
            if (args.Count > 0) { host.AgentPlay.ControlResearch(args, con); return; }
            if (!Need(args, 0, 0, Self("research"))) return;
            Report(con, host.DebugMenu.DebugApply(DebugMenu.DebugRow.UnlockResearch, ""));
        });

        host.AgentChannel.RegisterControlCommands();
        RegisterConsoleCompletions();

        c.Print("Dev console (Meitou only). help lists the commands, Tab completes, Up/Down is the history, Esc closes.");
    }
}