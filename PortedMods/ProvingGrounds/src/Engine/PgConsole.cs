using System.Globalization;
using HarmonyLib;
using Meitou.Game.DevTools;
using Meitou.Modding;
using ProvingGrounds.Core.Glue;

namespace ProvingGrounds.Engine;

/// <summary>
/// The <c>pg</c> dev-console command (spike S14): scripts reach it through the agent channel (<c>--control-dir</c>, AgentChannel.cs:9-16, each
/// line run by DevConsole.Execute). It runs on the host's frame thread, possibly during a tick: it never touches the world. Read-only answers come
/// from what the system published; anything that needs the world is queued as a <see cref="ConsoleLine"/> command for the next tick (applied in the serial
/// Inputs; mod commands wait while the game is paused, World.Interface.cs:27, so scripts <c>resume</c> first) and answers in log.txt.
/// </summary>
static class PgConsole
{
    static ModContext? ctx;

    internal const string Usage = "pg help | pg status | pg api [in <id> | fighter <name>] | pg census | pg probe [<id>] | pg probe-config | pg spikes on|off";

    // Verbs later milestones add (task text M2.7; not reserved in `pg help` until they exist): pg spar / pg stop (M3, task 3.3, test-only), pg ring (task 3.4, test-only),
    // pg place / findspot / finish-build / sites / findsite / resolve (task 4.1, test-only, spike S15), pg unlock-blueprints (M5),
    // pg town <bout|cards|refresh> (M7; task 7.1 added pg town [status|busy]). Each must answer one JSON line through Ok / Fail below; test-only ones go in
    // docs/divergences.md "Test-only commands".

    static readonly string[] HelpLines =
    [
        "pg help: this list",
        "pg status: the system's state (no world access)",
        "pg api [in <id> | fighter <name>]: what other mods read through ProvingGroundsApi (no world access): who is in a bout, the town fighters, the link to The Wasteland Remembers; in <id> (c:<slot>.<generation>): InBout; fighter <name>: IsTownFighter",
        "pg census: logs the Scratch census next tick as PG-CENSUS lines in log.txt (resume first: mod commands wait while paused)",
        "pg probe <id>: every engine-adapter value for a character (c:<slot>.<generation>) as a PG-PROBE JSON line in log.txt next tick; pg probe: the last one",
        "pg probe-config: one JSON line with every value in force (pg_config.json + the settings page)",
        "pg spikes on|off: arms the test-only spike patches and commands (off by default)",
        "pg identity-dupes, pg identity create <id> (test only, after pg spikes on): fighter-identity token repeats; GetOrCreate for one character",
        "pg give-rating <id> <n>, pg give-marks <id> <n> (test only, after pg spikes on): set a character's player-board rating / marks (pg probe shows them)",
        "pg busy [on|off] (test only, after pg spikes on): a stand-in match for the save safety: a manual save cancels it, an autosave waits until off",
        "pg setting <key> <value> (test only, after pg spikes on): sets one settings-page row (its property name) as the page does; at-once rows apply next tick (pg probe-config)",
        "pg ring at <x> <y> <z> <arena|small|banner> [scale <f>] | around <id...> <kind> [scale <f>] | off | status | push <id> <distance> [<bearing>] | sample [near <id>] <radius...> (test only, after pg spikes on): the ring guard's site (an ingress sets it from the staged building, task 4.3b); PG-RING line in log.txt",
        "pg spar <avb|1v1|ffa> <idA...> [vs <idB...>], pg stop (test only, after pg spikes on): starts a sparring match (avb = AvB, 1v1 = Teams 1v1 from a bench, ffa = Last Standing) / stops it (outcome Stopped, no rating); PG-SPAR lines in log.txt",
        "pg watch <id...> | off (test only, after pg spikes on): a PG-WATCH line each tick a watched character's target, down/dead, combat or running task changed",
        "pg place <registry|arena|small-arena|banner|leaderboard|cage|record> <x> <z> [y], pg findspot <what|record> <x> <z>, pg finish-build <siteId|all> (test only, after pg spikes on): place a player-owned building by the construction command / find a spot the town rule accepts / finish it (PG-PLACE, PG-FINDSPOT, PG-FINISH-BUILD lines)",
        "pg sites [radius], pg findsite <arena|small-arena|banner> (test only, after pg spikes on): the arena buildings ArenaIdentity recognises near the selected character (PG-SITES) / the site search from the first squad member (PG-FINDSITE)",
        "pg resolve <player:id|town:key> <record|registry|...> (test only, after pg spikes on): ArenaIdentity.Resolve of that building ref now (PG-RESOLVE)",
        "pg window test <open|close|rows <1-12>|portrait <id>|portrait off|status> (test only, after pg spikes on): the window framework's test window pg.test (task 4.4): open / close it, change its row count (Replace), show a portrait; a click on its button logs a PG-UI line (Ctrl held: ctrl-click; a right-click: RightPress)",
        "pg window key <key> (test only, after pg spikes on): runs the game's own key call (PlayerInterface.UiKey) for one key at once on the frame thread (task 4.6b: the debug menu key; the agent channel has no key command)",
        "pg debug <toggle|press <widget>|model> (test only, after pg spikes on): the debug menu's toggle, a button press of its window, or its model (PG-DEBUG line, task 4.6b)",
        "pg window escape (test only, after pg spikes on): runs the game's own Esc key handler (PlayerInterface.EscapeKey) at once on the frame thread: closes the top window (task 4.5b; the agent channel has no key command)",
        "pg window rect pg.bookie.result <widget> (test only, after pg spikes on): the widget's rectangle in the result window's last layout and the client's size, with below = it lies below the client (task 8.1b: a two-row result's Close at a small view)",
        "pg window drag <x1> <y1> <x2> <y2> (test only, after pg spikes on): presses the left button at x1 y1, moves to x2 y2 and releases it there, through the game's own pointer calls (PlayerInterface.MouseDown / MouseMove / MouseUp), at once on the frame thread: a row dragged onto a list of the Arena window (task 4.5b; the agent channel has no drag)",
        "pg ingress <avb|1v1|ffa> <arena|small|banner> <idA...> [vs <idB...>] | status | cancel (test only, after pg spikes on): stages a bout at the nearest site as the Arena window's Start does (formation, 4 s hold, countdown, then the match); PG-INGRESS lines in log.txt",
        "pg medical <ringside|bloodsport|status> (test only, after pg spikes on): sets or reads the medical protocol (Ringside Aid / Bloodsport) as the Arena window's toggle will (task 4.5); locked while a match runs or an arena start is pending; PG-MEDICAL line in log.txt",
        "pg reward-item <id> <piece> <tier> [upgrade] [wear] | <id> general <item>, pg items <id> (test only, after pg spikes on): makes and delivers one Rewards catalogue row with the original's engine calls, no rules (task 5.2a; PG-REWARD line) / lists a character's items and its fighter's weapons (PG-ITEMS line)",
        "pg prisoners (test only, after pg spikes on): the Arena window's prisoner discovery around the Registry it is bound to, then the cages it found (id, record, occupant, lock), the roster and match prisoners, the handlers and every player-owned cage (PG-PRISONERS line, task 6.1)",
        "pg imprison <id> <cage site or fixture id> (test only, after pg spikes on): lets go of the character if carried, releases it if imprisoned, then puts it in that cage (Imprison locks the cage; spike S11's recipe; PG-IMPRISON line, task 6.1)",
        "pg ko <id> [seconds] (test only, after pg spikes on): knocks the character out (Meitou's DebugKnockOut, 600 s by default as the agent channel's ko; task 6.1)",
        "pg recruit open | close | model | select <id> | grant <id> [force] (test only, after pg spikes on): the prisoner recruitment window's sim half (task 6.4a): open it (as the Arena window's Recruit Prisoners), Back to Arena, its model, a row click, the Grant Freedom button (a disabled button refused unless force); PG-RECRUIT line in log.txt",
        "pg town [status|start|roster|search|search-poll|model|cards|book|cancel|refresh|money|hold|care|heal|crowd|sit|stand|corpses|limbs|sever|announcer|bookie] (test only, after pg spikes on): TownArena's state (Scratch active: a finished Town Registry bound and a player character in its town; the 5 s schedule clock; the status text; task 7.3: the planned and running bout, its fighters, teams and town-board rows, the borrowed NPCs, anyone outside the bout who targeted a bout fighter); start plans a bout when none is announced and makes the announced bout due now (task 7.3, no 1-2 game-hour wait); roster adds the matchmaking snapshot's counts (fighters by type and record, medics, rejections with reasons; task 7.2); search queues one Easy / Common card search with the first squad member on the background worker, search-poll takes one result (task 7.2); task 7.5: cards <ids> refreshes the challenge cards for that team (the window's refresh), book <0-4|direct> <ids> books a card or the direct 1v1 as the window's Book buttons, cancel cancels the booking, refresh buys a paid refresh, hold on|off holds the next event's start (the host answers the arena busy), money <cats> sets the player's money; every line gives the cards, money, account and booking; task 7.6: model gives the town window's published model (rows, cards, buttons; event model); task 8.4a: care adds the aftercare (medics with slot, action, line spot, holds; patients; the hospital's beds with kind, power, occupant; every hold with its owners); task 8.4c: heal <id> heals one character, a town NPC too (Meitou's DebugHeal); task 8.2: crowd adds the spectators (guests with seat, seated, occupant, task, holds; pending releases; the profile and chatter state; the stand-in counts), sit [id] holds one NPC (default: a free Bloodstamp civilian, bar resident or cage handler) and sends it to the nearest free Arena Pillow seat (spike S16), stand stands it up and lets it go; task 8.6: corpses adds the corpse cleanup (status, batch, furnace, cage handler, bodies, deferred handlers, reservations, counts); task 8.5: limbs [id] adds the limb shop (phase, status, the visit's fighter and trader, target, approach, orders with grade, quality and both lines, reservations, counts; with an id that character's four limbs and limb-section items), sever <id> <0-3> cuts that limb off (0 left arm, 1 right arm, 2 left leg, 3 right leg; Meitou's MedicalState.Sever and the after-sever step); task 8.3: announcer adds the announcer (programme, variant, status, cues, duration, approach, arena and spot, speakers with position, target, yaw, task and holds, pending releases, reservations, counts, failures); task 8.1a: bookie open|model|side A|B|stake <delta>|bet|arrange|close|result gives the bookie (market, clock, hold, ticket, credit, money, the betting and result window models; event bookie): open shows the betting window for the nearest Scratch Bookie as its dialogue trade does, side / stake / bet / arrange / close send the window's own commands with the drawn values; PG-TOWN line in log.txt (task 7.1)",
        "pg perf [on|off|reset] (test only, after pg spikes on): the cost-per-tick instrument (task 9.2a; off by default): on / off switch it, reset empties its numbers; every form answers SlowWorld's and Inputs' average and maximum over the last 600 measured ticks and each Harmony patch's calls, total, average and maximum since the last reset; while on the F10 status row ends with tick avg <x> ms max <y> ms (<n>)",
        "pg reward <id> claim <piece> | licence <T1|T2|T3> | weapon-licence | general <item> | offer <piece> | drop <piece> (test only, after pg spikes on): a Rewards purchase as the Rewards window sends it (RewardDelivery: ClaimNext / PurchaseLicence / PurchaseWeaponLicence / PurchaseGeneral) / the offer of a piece / destroys every copy of a piece the fighter holds (task 5.2b; PG-PURCHASE line)",
    ];

    // Every verb answers ONE JSON line (task 2.7, Core/Glue/PgConsoleAnswer.cs): {"verb":..,"ok":true,..} printed, or {"verb":..,"ok":false,
    // "error":..} through con.Error (the agent channel then answers ok:false, AgentChannel.cs:300-303). The SPIKE verbs (Spikes.Verbs,
    // task 0.7, inert until pg spikes on) keep their M0 text answers; spike S6/S7's win / portrait went with task 4.4 (pg window test).
    static void Ok(DevConsole con, string verb, params (string, string)[] fields) => con.Print(PgConsoleAnswer.Ok(verb, fields));
    static void Fail(DevConsole con, string verb, string error) => con.Error(PgConsoleAnswer.Fail(verb, error));
    static (string, string) Text(string key, string value) => (key, PgConsoleAnswer.Text(value));

    /// <summary>Queues a world-reading verb for the next tick (applied in the serial Inputs); false with no world.</summary>
    static bool Queue(string verb, string[] args, out long tick)
    {
        tick = 0;
        if (ProvingGroundsSystem.Current is null || ctx is null) return false;
        tick = ctx.Tick + 1;
        ctx.Enqueue(new ConsoleLine(verb, args) { Tick = tick });
        return true;
    }

    static string Num(long n) => n.ToString(CultureInfo.InvariantCulture);

    static long rewardSeq = 1_000_000;   // TEST ONLY pg reward: the test verb's RewardPurchase sequence, its own range (the Rewards window counts from 1)

    /// <summary>The test-only verbs (refused until <c>pg spikes on</c>): ONE set, read by the refusal in <c>default</c> and by every
    /// <c>when Armed(verb)</c> case, so a verb missing here never runs (it falls through to "unknown verb"). The SPIKE verbs of
    /// Spikes.Verbs (task 0.7) are test-only too.</summary>
    static readonly HashSet<string> TestOnlyVerbs = new(StringComparer.Ordinal)
        { "busy", "identity-dupes", "identity", "give-rating", "give-marks", "setting", "spar", "stop", "ring", "watch", "place", "findspot", "finish-build",
          "sites", "findsite", "resolve", "ingress", "medical", "window", "arena",
          "leaderboard" /* task 4.6a */, "debug" /* task 4.6b */, "items", "reward-item" /* task 5.2a */, "reward" /* task 5.2b */,
          "rewards" /* task 5.3a */, "prisoners", "imprison", "ko" /* task 6.1 */, "recruit" /* task 6.4a */,
          "town" /* task 7.1 */, "perf" /* task 9.2a */ };

    static bool TestOnly(string verb) => TestOnlyVerbs.Contains(verb) || Spikes.Spikes.Verbs.Contains(verb);

    /// <summary>A test-only verb while the test verbs are armed.</summary>
    static bool Armed(string verb) => TestOnlyVerbs.Contains(verb) && Spikes.SpikePatches.Armed;

    /// <summary>The F10 row "Proving Grounds: census" (task 2.7; frame thread): queues the census (task 0.6, src/Town/Census.cs) for the next tick,
    /// as <c>pg census</c> does, and shows the last summary.</summary>
    internal static string QueueCensus()
    {
        if (!Queue("census", [], out long tick)) return "Proving Grounds: no world yet";
        string last = ProvingGroundsSystem.Current?.Snapshot.LastCensus ?? "none";
        return $"Proving Grounds census queued for tick {Num(tick)} (PG-CENSUS lines in log.txt); last: {last}";
    }

    /// <summary>The console's Harmony patch (applied by <see cref="Install"/> and by PgCheck hooks, through PgPatches.Apply).</summary>
    internal static IEnumerable<PgPatch> Patches() =>
        [new(Part.Console, "DevConsoleHost.RegisterConsoleCommands postfix", Members.RegisterConsoleCommands, null, PgPatches.Method(typeof(PgConsole), nameof(AfterRegister)))];

    /// <summary>Patches DevConsoleHost.RegisterConsoleCommands; false (with a log line) when a member is missing.</summary>
    internal static bool Install(ModContext c)
    {
        ctx = c;
        try
        {
            // Inside the try: a TypeInitializationException from Members' static lookups must not escape into Initialize.
            var missing = Members.MissingConsole().ToList();
            if (missing.Count > 0) { c.Log($"pg console command off: {string.Join(", ", missing)} not found"); return false; }
            foreach (var p in Patches()) PgPatches.Apply(c.Harmony, p);
            return true;
        }
        catch (Exception e) { c.Log($"pg console command off: patch failed ({e.GetType().Name}: {e.Message})"); return false; }
    }

    static void AfterRegister(object __instance)
    {
        using var perfScope = PgPerf.Measure(PgPerf.ConsoleRegister);   // task 9.2a: the cost instrument (off: one volatile read)
        try
        {
            if (Members.DevConsoleField!.GetValue(__instance) is not DevConsole console) { ctx?.Log("pg console command off: no console"); return; }
            console.Register(new ConsoleCommand("pg", Usage, "Proving Grounds test commands", Run));
            ctx?.Log("pg console command registered");
        }
        catch (Exception e) { ctx?.Log($"pg console command off: {e.GetType().Name}: {e.Message}"); }
    }

    static void Run(IReadOnlyList<string> args, DevConsole con)
    {
        string verb = args.Count > 0 ? args[0].ToLowerInvariant() : "help";
        switch (verb)
        {
            case "help":
                Ok(con, verb, ("commands", PgConsoleAnswer.TextList(HelpLines)));
                return;
            case "probe":   // pg probe <id>: queued (world access), answered in log.txt; pg probe (no id): the last answer, from the snapshot
                if (args.Count < 2)
                {
                    var last = ProvingGroundsSystem.Current?.Snapshot.LastProbe;
                    if (last is null) { Fail(con, verb, "no world yet"); return; }
                    Ok(con, verb, ("probe", last == "none" ? "null" : last));
                    return;
                }
                if (!CharacterIdText.TryParse(args[1], out var probeId)) { Fail(con, verb, "usage pg probe c:<slot>.<generation>"); return; }
                if (!Queue(verb, [CharacterIdText.Format(probeId)], out long probeTick)) { Fail(con, verb, "no world yet"); return; }
                Ok(con, verb, Text("id", CharacterIdText.Format(probeId)), ("queued", Num(probeTick)),
                    Text("answer", "the PG-PROBE line in log.txt; or say pg probe"));
                return;
            case "probe-config":   // task 2.6: the values in force (file + settings page), published as immutable text (no world access)
                con.Print(PgConsoleAnswer.WithOk(verb, Ui.PGSettingsUI.ProbeJson));
                return;
            case "spikes":   // SPIKE: arms the test-only patches and verbs (off by default)
                if (args.Count > 1 && args[1] is "on" or "off")
                {
                    Spikes.SpikePatches.Armed = args[1] == "on";
                    if (args[1] == "off") Spikes.Spikes.ClearRequested = true;   // the next tick forgets the holds, spar and carry job (sim thread)
                }
                Ok(con, verb, ("armed", PgConsoleAnswer.Bool(Spikes.SpikePatches.Armed)), Text("text", $"pg spikes {(Spikes.SpikePatches.Armed ? "on" : "off")}"));
                return;
            case "status":
                if (ProvingGroundsSystem.Current is not { } current) { Fail(con, verb, "no world yet"); return; }
                Ok(con, verb, Text("status", current.Status()), Text("row", current.StatusRow()));
                return;
            case "api":   // task 10.1 (port only): the published view ProvingGroundsApi answers from; no world access
            {
                if (ProvingGroundsSystem.Current is not { } apiSys) { Fail(con, verb, "no world yet"); return; }
                if (args.Count == 3 && args[1] == "in" && CharacterIdText.TryParse(args[2], out var apiId))
                {
                    Ok(con, verb, Text("id", CharacterIdText.Format(apiId)), ("inBout", PgConsoleAnswer.Bool(ProvingGroundsApi.InBout(apiId))),
                        ("townFighter", PgConsoleAnswer.Bool(ProvingGroundsApi.IsTownFighterId(apiId))));
                    return;
                }
                if (args.Count >= 3 && args[1] == "fighter")
                {
                    string apiName = string.Join(' ', args.Skip(2));
                    Ok(con, verb, Text("name", apiName), ("townFighter", PgConsoleAnswer.Bool(ProvingGroundsApi.IsTownFighter(apiName))));
                    return;
                }
                if (args.Count > 1) { Fail(con, verb, "usage pg api [in c:<slot>.<generation> | fighter <name>]"); return; }
                var apiView = apiSys.ApiView;
                Ok(con, verb, ("version", Num(ProvingGroundsApi.Version)), ("ready", PgConsoleAnswer.Bool(apiView.Ready)),
                    ("inBout", Num(apiView.InBoutCount)), ("townFighters", Num(apiView.TownFighterCount)),
                    Text("ids", string.Join(" ", apiView.InBoutIds.Select(CharacterIdText.Format).Order(StringComparer.Ordinal))),
                    Text("text", apiSys.InteropPhase));
                return;
            }
            case "census":
                if (!Queue(verb, args.Skip(1).ToArray(), out long censusTick)) { Fail(con, verb, "no world yet"); return; }
                Ok(con, verb, ("queued", Num(censusTick)), Text("answer", "PG-CENSUS lines in log.txt"));
                return;
            case "busy" when Armed(verb):   // TEST ONLY (task 2.5): frame thread, no world access
                if (ProvingGroundsSystem.Current is not { } sys) { Fail(con, verb, "no world yet"); return; }
                if (args.Count > 1 && args[1] is "on" or "off") sys.Lifecycle.ForcedBusy = args[1] == "on";
                else if (args.Count > 1) { Fail(con, verb, "usage pg busy [on|off]"); return; }
                Ok(con, verb, ("busy", PgConsoleAnswer.Bool(sys.Lifecycle.ForcedBusy)),
                    Text("text", $"pg busy {(sys.Lifecycle.ForcedBusy ? "on" : "off")}; saves: {sys.Snapshot.SaveSafety}"));
                return;
            case "identity-dupes" or "identity" when Armed(verb):   // TEST ONLY (task 2.3): world work next tick, answered in log.txt
                if (verb == "identity" && (args.Count < 3 || args[1] != "create" || !CharacterIdText.TryParse(args[2], out _)))
                { Fail(con, verb, "usage pg identity create c:<slot>.<generation>"); return; }
                if (!Queue(verb, args.Skip(1).ToArray(), out long idTick)) { Fail(con, verb, "no world yet"); return; }
                Ok(con, verb, ("queued", Num(idTick)), Text("answer", "the PG-IDENTITY line in log.txt"));
                return;
            case "give-rating" or "give-marks" when Armed(verb):   // TEST ONLY (task 2.4): world work next tick, answered in log.txt
                if (args.Count < 3 || !CharacterIdText.TryParse(args[1], out var giveId)) { Fail(con, verb, $"usage pg {verb} c:<slot>.<generation> <n>"); return; }
                if (!Queue(verb, [CharacterIdText.Format(giveId), args[2]], out long giveTick)) { Fail(con, verb, "no world yet"); return; }
                Ok(con, verb, Text("id", CharacterIdText.Format(giveId)), ("queued", Num(giveTick)), Text("answer", $"the 'pg {verb} at tick' line in log.txt"));
                return;
            case "spar" or "stop" when Armed(verb):   // TEST ONLY (task 3.3): world work next tick, answered in log.txt
                if (verb == "spar" && !SparArgs.TryParse<Meitou.Simulation.CharacterId>(args.Skip(1).ToList(), CharacterIdText.TryParse, out _, out _, out _, out string sparError))
                { Fail(con, verb, sparError); return; }
                if (!Queue(verb, args.Skip(1).ToArray(), out long sparTick)) { Fail(con, verb, "no world yet"); return; }
                Ok(con, verb, ("queued", Num(sparTick)), Text("answer", "the PG-SPAR line in log.txt"));
                return;
            case "watch" when Armed(verb):   // TEST ONLY (task 3.5): world work next tick, answered in log.txt (PG-WATCH lines)
                if (!Queue(verb, args.Skip(1).ToArray(), out long watchTick)) { Fail(con, verb, "no world yet"); return; }
                Ok(con, verb, ("queued", Num(watchTick)), Text("answer", "PG-WATCH lines in log.txt"));
                return;
            case "place" or "findspot" or "finish-build" or "sites" or "findsite" or "resolve" when Armed(verb):   // TEST ONLY (task 4.1): world work next tick
                if (!Queue(verb, args.Skip(1).ToArray(), out long buildTick)) { Fail(con, verb, "no world yet"); return; }
                Ok(con, verb, ("queued", Num(buildTick)), Text("answer", $"the PG-{verb.ToUpperInvariant()} line in log.txt"));
                return;
            case "ingress" when Armed(verb):   // TEST ONLY (task 4.3b): world work next tick, answered in log.txt
                if (!Queue(verb, args.Skip(1).ToArray(), out long ingressTick)) { Fail(con, verb, "no world yet"); return; }
                Ok(con, verb, ("queued", Num(ingressTick)), Text("answer", "the PG-INGRESS line in log.txt"));
                return;
            case "medical" when Armed(verb):   // TEST ONLY (task 4.7, until the Arena window 4.5): world work next tick, answered in log.txt
                if (args.Count > 2 || (args.Count == 2 && args[1].ToLowerInvariant() is not ("ringside" or "bloodsport" or "status")))
                { Fail(con, verb, "usage pg medical <ringside|bloodsport|status>"); return; }
                if (!Queue(verb, args.Skip(1).ToArray(), out long medicalTick)) { Fail(con, verb, "no world yet"); return; }
                Ok(con, verb, ("queued", Num(medicalTick)), Text("answer", "the PG-MEDICAL line in log.txt"));
                return;
            case "perf" when Armed(verb):   // TEST ONLY (task 9.2a): frame thread, no world access: the switch, the patch counters, the published tick windows
                if (args.Count > 2 || (args.Count == 2 && args[1] is not ("on" or "off" or "reset"))) { Fail(con, verb, "usage pg perf [on|off|reset]"); return; }
                if (args.Count == 2)
                {
                    if (args[1] == "reset") PgPerf.Reset();
                    else PgPerf.On = args[1] == "on";
                }
                {
                    var perf = ProvingGroundsSystem.Current?.Snapshot.Perf;   // published at the 0.20 s publish (at most that old); null: nothing measured in this world
                    Ok(con, verb, ("on", PgConsoleAnswer.Bool(PgPerf.On)),
                        ("tick", perf is null ? "null" : TickCostJson.Window(perf.Tick)), ("inputs", perf is null ? "null" : TickCostJson.Window(perf.Inputs)),
                        ("patches", TickCostJson.Patches(PgPerf.Patches)),
                        ("steps", ProvingGroundsSystem.Current is { } stepSys ? TickCostJson.Patches(stepSys.StepCosts) : "null"),   // task 9.2b: SlowWorld per step
                        Text("answer", args.Count == 2 && args[1] == "reset" ? "patch counters reset now; the tick windows on the next measured tick" : "tick / inputs: the last publish (0.20 s)"));
                }
                return;
            case "town" when Armed(verb):   // TEST ONLY (task 7.1): world work next tick, answered in log.txt
                if (args.Count >= 2 && args[1] is "cards" or "book" or "money" or "cancel" or "refresh" or "hold" or "heal" or "sit" or "limbs" or "sever" or "bookie") { /* task 7.5 (heal: task 8.4c, sit: task 8.2, limbs / sever: task 8.5, bookie: task 8.1a): their arguments are checked with the world */ }
                else if (args.Count > 2 || (args.Count == 2 && args[1] is not ("status" or "start" or "roster" or "search" or "search-poll" or "model" or "care" or "crowd" or "stand" or "corpses" or "announcer"))) { Fail(con, verb, "usage pg town [status|start|roster|search|search-poll|model|cards <ids>|book <0-4|direct> <ids>|cancel|refresh|money <cats>|hold on|off|care|heal <id>|crowd|sit [id]|stand|corpses|limbs [id]|sever <id> <0-3>|announcer|bookie <verb>]"); return; }
                if (!Queue(verb, args.Skip(1).ToArray(), out long townTick)) { Fail(con, verb, "no world yet"); return; }
                Ok(con, verb, ("queued", Num(townTick)), Text("answer", "the PG-TOWN line in log.txt"));
                return;
            case "ring" when Armed(verb):   // TEST ONLY (task 3.4): world work next tick, answered in log.txt
                if (!Queue(verb, args.Skip(1).ToArray(), out long ringTick)) { Fail(con, verb, "no world yet"); return; }
                Ok(con, verb, ("queued", Num(ringTick)), Text("answer", "the PG-RING line in log.txt"));
                return;
            case "setting" when Armed(verb):   // TEST ONLY (task 2.6 review): frame thread, the page's own Changed path, no world access
                if (args.Count < 3) { Fail(con, verb, "usage pg setting <key> <value>"); return; }
                if (Ui.PGSettingsUI.SetForTest(args[1], string.Join(' ', args.Skip(2))) is { } settingError) { Fail(con, verb, settingError); return; }
                Ok(con, verb, Text("key", args[1]), Text("value", string.Join(' ', args.Skip(2))),
                    Text("answer", "at-once rows apply on the next tick: read pg probe-config"));
                return;
            case "window" when Armed(verb) && args.Count == 2 && args[1] == "escape":   // TEST ONLY (task 4.5b): frame thread, the game's Esc handler
                if (Ui.ScreenHost.TestEscape() is { } escError) { Fail(con, verb, escError); return; }
                Ok(con, verb, Text("answer", "PlayerInterface.EscapeKey ran (the Esc key's handler)"));
                return;
            // ---- task 4.6b ----
            case "window" when Armed(verb) && args.Count == 3 && args[1] == "key":   // TEST ONLY (task 4.6b): frame thread, the game's own key call
                if (Ui.DebugMenuKey.TestKey(args[2], out bool keyTaken) is { } keyError) { Fail(con, verb, keyError); return; }
                Ok(con, verb, Text("key", args[2]), ("taken", PgConsoleAnswer.Bool(keyTaken)), Text("answer", "PlayerInterface.UiKey ran (a key press's interface call)"));
                return;
            case "debug" when Armed(verb):   // TEST ONLY (task 4.6b): pg debug toggle|press <widget>|model -> the debug menu's sim half next tick
                if (args.Count < 2) { Fail(con, verb, "usage pg debug <toggle|press <widget>|model>"); return; }
                if (!Queue(verb, args.Skip(1).ToArray(), out long debugTick)) { Fail(con, verb, "no world yet"); return; }
                Ok(con, verb, ("queued", Num(debugTick)), Text("answer", "the PG-DEBUG line in log.txt"));
                return;
            // ---- end task 4.6b ----
            // ---- task 5.3b ----
            case "window" when Armed(verb) && args.Count == 4 && args[1] == "caption":   // TEST ONLY (task 5.3b): frame thread, the last arrange's text
                if (Ui.ScreenHost.TestCaption(args[2], args[3], out string? captionError, out string captionRect, out bool captionEnabled) is not { } caption) { Fail(con, verb, captionError ?? "no caption"); return; }
                Ok(con, verb, Text("screen", args[2]), Text("widget", args[3]), Text("caption", caption), Text("rect", captionRect),
                    ("enabled", PgConsoleAnswer.Bool(captionEnabled)));   // task 6.4b review: rect and enabled
                return;
            case "window" when Armed(verb) && args.Count == 5 && args[1] == "wheel":   // TEST ONLY (task 5.3b): frame thread, the screens' own wheel call
                if (Ui.ScreenHost.TestWheel(args[2], args[3], args[4], out bool wheelTaken) is { } wheelError) { Fail(con, verb, wheelError); return; }
                Ok(con, verb, ("taken", PgConsoleAnswer.Bool(wheelTaken)), ("rewardsBody", Num(Ui.RewardsScreen.BodyForTest)),
                    Text("answer", "MyGuiScreenHost.Wheel ran (the faithful screens' wheel)"));
                return;
            // ---- end task 5.3b ----
            case "window" when Armed(verb) && args.Count == 4 && args[1] == "rect" && args[2] == Town.TownBookieUISim.ResultScreenId:   // TEST ONLY (task 8.1b)
                if (Ui.TownBookieResultScreen.LaidOutForTest(args[3], out string? rectError) is not { } laid) { Fail(con, verb, rectError ?? "not laid out"); return; }
                Ok(con, verb, Text("screen", args[2]), Text("widget", args[3]), ("x", Num(laid.Rect.X)), ("y", Num(laid.Rect.Y)), ("w", Num(laid.Rect.W)),
                    ("h", Num(laid.Rect.H)), ("clientWidth", Num(laid.ClientWidth)), ("clientHeight", Num(laid.ClientHeight)),
                    ("below", PgConsoleAnswer.Bool(laid.Rect.Y + laid.Rect.H > laid.ClientHeight)));
                return;
            case "window" when Armed(verb) && args.Count == 6 && args[1] == "drag":   // TEST ONLY (task 4.5b): frame thread, the game's own pointer calls
                if (Ui.ScreenHost.TestDrag(args[2], args[3], args[4], args[5]) is { } dragError) { Fail(con, verb, dragError); return; }
                Ok(con, verb, Text("answer", "pressed, moved and released through PlayerInterface.MouseDown / MouseMove / MouseUp"));
                return;
            case "window" when Armed(verb):   // TEST ONLY (task 4.4): pg window test ... -> the pg.test window's sim half next tick
                if (args.Count < 3 || args[1] != "test") { Fail(con, verb, "usage pg window test <open|close|rows <1-12>|portrait <c:slot.gen>|portrait off|status>"); return; }
                if (!Queue(verb, args.Skip(1).ToArray(), out long windowTick)) { Fail(con, verb, "no world yet"); return; }
                Ok(con, verb, ("queued", Num(windowTick)), Text("answer", "the 'pg window at tick' line in log.txt (PG-UI lines for clicks)"));
                return;
            case "arena" when Armed(verb):   // TEST ONLY (task 4.5a): pg arena ... -> the Arena window's sim half next tick
                if (args.Count < 2) { Fail(con, verb, "usage pg arena <open|close|mode ...|dest a|b|click|assign|remove <id>|search|balance|clear|swap|preset|location|medical|recruit|start|stop|model>"); return; }
                if (!Queue(verb, args.Skip(1).ToArray(), out long arenaTick)) { Fail(con, verb, "no world yet"); return; }
                Ok(con, verb, ("queued", Num(arenaTick)), Text("answer", "the PG-ARENA line in log.txt"));
                return;
            // ---- task 4.6a ----
            case "leaderboard" when Armed(verb):   // TEST ONLY (task 4.6a): pg leaderboard player|town|close|reset|model -> the Leaderboard window's sim half next tick
                if (args.Count < 2) { Fail(con, verb, "usage pg leaderboard <player|town|close|reset|model>"); return; }
                if (!Queue(verb, args.Skip(1).ToArray(), out long boardTick)) { Fail(con, verb, "no world yet"); return; }
                Ok(con, verb, ("queued", Num(boardTick)), Text("answer", "the PG-LEADERBOARD line in log.txt"));
                return;
            // ---- end task 4.6a ----
            case "items" or "reward-item" when Armed(verb):   // TEST ONLY (task 5.2a): world work next tick, answered in log.txt
                if (args.Count < 2 || !CharacterIdText.TryParse(args[1], out _)) { Fail(con, verb, $"usage pg {verb} c:<slot>.<generation> ..."); return; }
                if (!Queue(verb, args.Skip(1).ToArray(), out long itemTick)) { Fail(con, verb, "no world yet"); return; }
                Ok(con, verb, ("queued", Num(itemTick)), Text("answer", verb == "items" ? "the PG-ITEMS line in log.txt" : "the PG-REWARD line in log.txt"));
                return;
            case "rewards" when Armed(verb):   // TEST ONLY (task 5.3a): pg rewards open|close|model|click ... -> the Rewards window's sim half next tick
                if (args.Count < 2) { Fail(con, verb, "usage pg rewards <open [id]|close|model|click <fighter id|tab t|claim p|licence row|weapon-licence|general i|close>>"); return; }
                if (!Queue(verb, args.Skip(1).ToArray(), out long rewardsTick)) { Fail(con, verb, "no world yet"); return; }
                Ok(con, verb, ("queued", Num(rewardsTick)), Text("answer", "the PG-REWARDS line in log.txt"));
                return;
            case "recruit" when Armed(verb):   // TEST ONLY (task 6.4a): pg recruit open|close|model|select <id>|grant <id> [force] -> the recruitment window's sim half next tick
                if (args.Count < 2) { Fail(con, verb, "usage pg recruit <open|close|model|select <id>|grant <id> [force]>"); return; }
                if (!Queue(verb, args.Skip(1).ToArray(), out long recruitTick)) { Fail(con, verb, "no world yet"); return; }
                Ok(con, verb, ("queued", Num(recruitTick)), Text("answer", "the PG-RECRUIT line in log.txt"));
                return;
            case "prisoners" when Armed(verb):   // TEST ONLY (task 6.1): world work next tick, answered in log.txt
                if (!Queue(verb, [], out long prisonersTick)) { Fail(con, verb, "no world yet"); return; }
                Ok(con, verb, ("queued", Num(prisonersTick)), Text("answer", "the PG-PRISONERS line in log.txt"));
                return;
            case "imprison" when Armed(verb):   // TEST ONLY (task 6.1): world work next tick, answered in log.txt
                if (args.Count < 3 || !CharacterIdText.TryParse(args[1], out var imprisonId)) { Fail(con, verb, "usage pg imprison c:<slot>.<generation> <cage site or fixture id>"); return; }
                if (!Queue(verb, [CharacterIdText.Format(imprisonId), args[2]], out long imprisonTick)) { Fail(con, verb, "no world yet"); return; }
                Ok(con, verb, ("queued", Num(imprisonTick)), Text("answer", "the PG-IMPRISON line in log.txt"));
                return;
            case "ko" when Armed(verb):   // TEST ONLY (task 6.1): Meitou's own DebugKnockOut command (DebugCommands.cs:212, :649-660), enqueued as the agent
            {                             // channel's ko does (AgentPlay.cs:748-761: 600 s by default); no world access here
                float koSeconds = 600;
                if (args.Count < 2 || !CharacterIdText.TryParse(args[1], out var koId)
                    || (args.Count > 2 && (!float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out koSeconds) || !float.IsFinite(koSeconds) || koSeconds <= 0)))
                { Fail(con, verb, "usage pg ko c:<slot>.<generation> [seconds]"); return; }
                if (ProvingGroundsSystem.Current is null || ctx is null) { Fail(con, verb, "no world yet"); return; }
                long koTick = ctx.Tick + 1;
                ctx.Enqueue(new Meitou.Simulation.DebugKnockOut([koId], koSeconds) { Tick = koTick });
                Ok(con, verb, Text("id", CharacterIdText.Format(koId)), ("seconds", ProvingGrounds.Core.CMath.FormatG(koSeconds, 9)), ("queued", Num(koTick)));
                return;
            }
            case "reward" when Armed(verb):   // TEST ONLY (task 5.2b): a purchase is a RewardPurchase command (the Rewards window's, task 5.3); offer / drop a ConsoleLine
            {
                if (args.Count < 3 || !CharacterIdText.TryParse(args[1], out var rewardId)) { Fail(con, verb, RewardPurchaseArgs.Usage); return; }
                var words = args.Skip(2).ToList();
                if (words[0].ToLowerInvariant() is "offer" or "drop")
                {
                    if (words.Count != 2) { Fail(con, verb, RewardPurchaseArgs.Usage); return; }
                    if (!Queue(verb, [CharacterIdText.Format(rewardId), words[0], words[1]], out long offerTick)) { Fail(con, verb, "no world yet"); return; }
                    Ok(con, verb, ("queued", Num(offerTick)), Text("answer", "the PG-PURCHASE line in log.txt"));
                    return;
                }
                if (!RewardPurchaseArgs.TryParse(words, out var kind, out int item, out string rewardError)) { Fail(con, verb, rewardError); return; }
                if (ProvingGroundsSystem.Current is null || ctx is null) { Fail(con, verb, "no world yet"); return; }
                long purchaseTick = ctx.Tick + 1;
                long seq = Interlocked.Increment(ref rewardSeq);
                ctx.Enqueue(new RewardPurchase(rewardId, kind, item, seq, null, RewardPurchaseSender.TestVerb) { Tick = purchaseTick });   // no shown offer: the test verb has no window
                Ok(con, verb, Text("id", CharacterIdText.Format(rewardId)), Text("kind", kind.ToString()), ("seq", Num(seq)), ("queued", Num(purchaseTick)),
                    Text("answer", "the PG-PURCHASE line in log.txt"));
                return;
            }
            default:
                if (TestOnly(verb) && !Spikes.SpikePatches.Armed) { Fail(con, verb, "test-only; say pg spikes on first"); return; }
                if (Spikes.Spikes.Verbs.Contains(verb) && ProvingGroundsSystem.Current is not null && ctx is not null)   // SPIKE: world work next tick
                {
                    ctx.Enqueue(new ConsoleLine(verb, args.Skip(1).ToArray()) { Tick = ctx.Tick + 1 });
                    con.Print($"pg {verb}: queued for tick {ctx.Tick + 1}; see the log");
                    return;
                }
                Fail(con, verb, $"unknown verb '{verb}'. {Usage}");
                return;
        }
    }
}
