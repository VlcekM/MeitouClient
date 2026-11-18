using System.Globalization;
using System.Text;
using System.Text.Json;
using Meitou.Simulation;
using Meitou.Simulation.Population;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Port of the rules half of upstream/ProvingGrounds/plugin/src/ArenaUI.h/.cpp (Proving Grounds by Shutori, GPLv3), task 4.5a (PA §2.3, §2.4;
// CAP 7.0-7.4). The window half (layout from ArenaUI.cpp:2814-2926, binding, clicks, compact mode, drag highlights, scrolling, the settings
// tooltip) is task 4.5b in src/Ui/ArenaUI.cs. Every C++ function that decides something, holds state or computes text is in
// Core/Glue/ArenaUILogic.cs (in order, with line ranges), tested over a fake host (Tests/Glue/ArenaUILogicTests.cs); src/Game/PgArenaUIHost.cs
// is that host over PgEngine and this world's subsystems. This file is the sim half the M4 brief's UI rules ask for: the 0.20 s refresh in the
// frame loop, the commands (src/Engine/PgCommand.cs "task 4.5a") and the published ArenaUiModel. Meitou 0.17.3 (tag v0.17.3, fa34a1b). Sim
// thread, serial steps only.
//
// Engine calls of the C++ and what replaces them (E1; IArenaUIHost member -> adapter method, bound in PgArenaUIHost):
//   c->isValid(), isDead(), isUnconcious(), getName(), getPosition() ... PgEngine.Exists / IsDead / IsUnconscious / Name / Position
//   c->getStats()->getOverallSkillLevel_0_100() (:565, :1640, :2264) ... PgEngine.Stats + Core.Glue.OverallSkill (spike S12's stand-in)
//   stats->toughness() (:566) ... CombatStats.ScaledToughness (stored toughness x Scale, CharacterStats.cs:59-63)
//   stats->strengthActual(), dexterityActual() (:567-568) ... src/Game/ArenaCombatProfile.cs Actuals (task 7.2: Meitou's CombatSystem.Eff for those stats)
//   RosterStatus::Read(c, status) (:274) ... PgEngine.LimbHealth + BloodRatio into Core RosterStatus.Read (bands: RosterStatusPolicy)
//   LeaderboardStore::GetRating(c) (:2262) ... LeaderboardStore.GetPlayerRating (src/Game/LeaderboardStore.cs, C++ LeaderboardStore.cpp:425-439)
//   SquadUtil::CollectPlayerSquad (:453) ... PgEngine.PlayerSquads, members valid and not dead
//   ArenaIngress::GetBoundRegistry / BindRegistry / IsPending / GetStatus / HasLocationSite / GetMissingLocationMessage / Get/SetLocationMode /
//       Begin / Cancel / GetUiOpener ... src/Game/ArenaIngress.cs (task 4.3b); HasLocationSite through Core.Glue.SiteCheckCache (divergences.md)
//   SparSession::IsActive / GetStatus / Stop(StopManual) ... src/Game/SparSession.cs (task 3.3)
//   TownArena::IsBusy / HasBooking / Cancel ... src/Town/TownArena.cs (task 7.1)
//   ArenaMedical::SetProtocol / GetProtocol / GetProtocolName / NextProtocol ... Game.ArenaMedical (task 4.7) + Core
//       ArenaMedicalPolicy (name, next)
//   PrisonerUtil::* (IsRosterPrisoner, FindCageForOccupant, CollectNearbyCagePrisoners, MatchIncludesPrisoner, GetMatchPrisoners,
//       GetMatchHandlers, IsReturning, PrepareMatch, ReturnAllToCages, ClearMatchState) ... src/Game/PrisonerUtil.cs (task 6.1; ReturnAllToCages task 6.3)
//   PrisonerRecruitmentUI::Show / AbandonWorldState ... src/Ui/PrisonerRecruitmentUI.cs (task 6.4a: its sim half); TownArenaUI::Show / Close /
//       AbandonWorldState (task 7.5: src/Town/TownArenaUISim.cs, the sim half; the window task 7.6); TownBookie::Close (task 8.1a: src/Town/TownBookie.cs); MarksHUD::AbandonWorldState (task 4.6b) / RewardsUI::AbandonWorldState (TO-DO M5)
//   GetTickCount() (AdvanceUiRefreshClock :219) ... PgClock.DtSeconds through Core.Glue.UiRefreshCadence
//   PortraitManager::setImageWidget, every MyGUI call ... the window half (4.5b), from the published model
// Callers (C++ -> port): ArenaIngress.cpp:963, :990 (the Registry approach, task 4.2) and PrisonerRecruitmentUI.cpp:614 -> ShowFromRegistry;
// WorldLifecycle.cpp:69, :263 -> AbandonWorldState (the save safety's cleanup, through Ui.ArenaUI); the TEST-ONLY `pg arena open` (kept
// after task 4.2: the scenarios drive the window with it).

/// <summary>The window model of the Arena window <c>pg.arena</c> (task 4.5a): everything the window half (4.5b) binds, with its open / close
/// request. A new object only when <see cref="View"/> changed (value equality).</summary>
sealed record ArenaUiModel(ArenaUiView<CharacterId> View) : PgUiModel
{
    /// <summary>The layout's shape key (task 4.4: Register once, Replace on a change).</summary>
    internal string ShapeKey => View.ShapeKey;
}

/// <summary>
/// The sim half of the Arena window (task 4.5a): owns <see cref="ArenaUILogic{TId}"/> for this world, runs its 0.20 s refresh in the frame loop
/// (the C++ slot after PrisonerRecruitmentUI::Tick, ArenaUI.cpp:3172-3185), applies the window's commands and publishes <see cref="Model"/>. One
/// per world, owned by ProvingGroundsSystem.
/// </summary>
sealed class ArenaUISim
{
    internal const string ScreenId = "pg.arena";

    /// <summary>The subsystems and reads the window's rules call (built by ProvingGroundsSystem). <see cref="Spar"/> is lazy.</summary>
    internal sealed record Services(
        ArenaIngress Ingress,
        Func<SparSession> Spar,
        PrisonerUtil Prisoners,
        Town.TownArena Town,
        Town.TownBookie Bookie,
        Ui.TownArenaUI TownUi,
        Ui.PrisonerRecruitmentUI Recruitment,
        Ui.MarksHUD Marks,
        Ui.RewardsUI Rewards,
        ArenaMedical Medical,
        PgClock Clock,
        PopulationData Data,
        Func<World, CharacterId, float> Rating);

    readonly Services s;
    readonly ArenaUILogic<CharacterId> logic = new(CharacterId.None);
    readonly PgArenaUIHost host;
    ArenaUiModel model;

    internal ArenaUISim(Services services)
    {
        s = services;
        host = new PgArenaUIHost(services);
        model = new ArenaUiModel(new ArenaUiView<CharacterId> { Preview = CharacterId.None });
    }

    PgArenaUIHost H(World world) => host.Bind(world);

    /// <summary>The logic (tests, the status answer).</summary>
    internal ArenaUILogic<CharacterId> Logic => logic;

    /// <summary>The published model (a new object after each change).</summary>
    internal ArenaUiModel Model => model;

    /// <summary>The F10 phase row.</summary>
    internal string Phase => logic.Visible ? "open: " + logic.Status.Replace('\n', ' ') : "closed";

    /// <summary>A new model when the view changed (value equality; the window side compares models by reference).</summary>
    void Publish(World world)
    {
        var view = logic.BuildView(H(world));
        if (view.Equals(model.View)) return;
        model = new ArenaUiModel(view) { RequestSerial = view.RequestSerial, RequestOpen = view.RequestOpen };
    }

    /// <summary>The frame-loop step: the idle refresh while the window is open (ArenaUI.cpp:3172-3185), then the model if it changed.</summary>
    internal void Tick(World world)
    {
        if (logic.Tick(H(world), s.Clock.DtSeconds)) Publish(world);
    }

    /// <summary>C++ ArenaUI::ShowFromRegistry (ArenaUI.cpp:3251-3284): opens the window from a Registry or Banner (task 4.2's approach, src/Game/
    /// ContextMenuHooks.cs; also the TEST-ONLY <c>pg arena open</c>).</summary>
    internal void ShowFromRegistry(World world, ArenaBuilding? registry, bool bindRegistry = true)
    {
        host.ForgetSiteCheck();
        logic.ShowFromRegistry(H(world), registry, bindRegistry);
        Publish(world);
    }

    /// <summary>C++ ArenaUI::GetPreviewCharacter (ArenaUI.cpp:3286-3291).</summary>
    internal CharacterId GetPreviewCharacter(World world) => logic.GetPreviewCharacter(H(world));

    /// <summary>C++ ArenaUI::AbandonWorldState (ArenaUI.cpp:3197-3249; the save safety's cleanup).</summary>
    internal void AbandonWorldState(World world)
    {
        logic.AbandonWorldState(H(world));
        Publish(world);
    }

    /// <summary>One Arena window command (serial Inputs): its C++ handler, then the model if it changed. Answers what was done (the system's
    /// "window click" line) and whether the closed window ignored the command (ArenaUILogic.AcceptsCommand).</summary>
    internal (string Done, bool Ignored) Apply(World world, ArenaUiCommand command)
    {
        var h = H(world);
        string done = command.GetType().Name;
        if (!logic.AcceptsCommand(command is ArenaClose))
            return (done + ": ignored (window closed)", true);
        switch (command)
        {
            case ArenaSetMode m: logic.OnMode(h, m.Mode); break;
            case ArenaSetDest d: logic.OnDest(h, d.Dest); break;
            case ArenaRowClick r: logic.OnPortraitRowClick(h, r.List, r.Fighter); break;
            case ArenaRowDoubleClick r: logic.OnPortraitRowDoubleClick(h, r.List, r.Fighter); break;
            case ArenaRowRelease r: logic.OnPortraitRowRelease(h, r.List, r.Fighter, r.Right, r.Target, r.Prisoner); break;
            case ArenaRemoveSelected: logic.OnRemove(h); break;
            case ArenaSearch q: logic.OnRosterSearchChanged(h, q.Text ?? ""); break;
            case ArenaToggleBalanceMenu: logic.OnBalanceMenu(h); break;
            case ArenaBalance b: logic.BalanceTeams(h, b.ByRating); break;
            case ArenaClear: logic.OnClearTeams(h); break;
            case ArenaSwap: logic.OnSwapTeams(h); break;
            case ArenaPreset p: logic.OnPreset(h, p.Index, p.Save); break;
            case ArenaLocation l: logic.OnLocation(h, l.Mode); break;
            case ArenaMedicalToggle: logic.OnMedicalProtocolToggle(h); break;
            case ArenaRecruit: logic.OnRecruitPrisoners(h); break;
            case ArenaStart:
                host.ForgetSiteCheck();   // Start always asks ArenaIngress afresh
                logic.OnStart(h);
                break;
            case ArenaStop: logic.OnStop(h); break;
            case ArenaClose: logic.HidePanel(); break;
            default: return ("unknown arena command " + done, false);
        }
        Publish(world);
        return (done + ": " + logic.Status.Replace('\n', ' '), false);
    }

    /// <summary>A generic widget event of the window (task 4.4's UiClick): only the framework's Closed report is ours (the window's own
    /// commands are <see cref="ArenaUiCommand"/>s). Value = the request serial the window closed under. Answers what was done and whether it was
    /// ignored (the framework pattern, as ResultsUISim / LeaderboardUISim).</summary>
    internal (string Done, bool Ignored) Apply(World world, UiClickData c)
    {
        if (c.Kind != UiEventKind.Closed) return ($"ignored {c.Kind} on {c.Widget} (the Arena window sends ArenaUiCommands)", true);
        if (!logic.OnClosedFromWindow(c.Value)) return ("closed (already, or for an older request)", true);
        Publish(world);
        return ("closed from the window", false);
    }

    // ---------- TEST ONLY: pg arena (task 4.5a, until the window half 4.5b) ----------

    const string Usage = "usage pg arena <open|close|mode avb|1v1|ffa|dest a|b|click <id>|assign <id>|remove <id>|search [text]|balance [skill|rating]"
        + "|clear|swap|preset <1-3> [save]|location arena|small|banner|medical|recruit|start|stop|model>";

    static string Q(string? v) => PGLog.Quote(v ?? "");

    /// <summary>TEST ONLY <c>pg arena ...</c>: sends the commands the window will send (one per verb) and answers with the published model as
    /// one PG-ARENA JSON line. <c>open</c> = ShowFromRegistry of the nearest finished Registry or Banner within the site search's 25,000 of the
    /// squad's first member (as the walk-up opens it, task 4.2); <c>assign</c> = a double-click on the fighter's roster row; <c>remove</c> = the
    /// right-button release on the fighter's team / pool row; <c>click</c> = a click on its row; <c>preset N save</c> = a right-click.</summary>
    internal string Command(World world, PopulationData data, IReadOnlyList<string> args)
    {
        try
        {
            if (args.Count == 0) return Answer(world, "arena", false, Usage);
            string verb = args[0];
            long t = world.Tick;   // applied at once, in this serial step (as the window's command would be in its tick)
            string arg = args.Count > 1 ? args[1].ToLowerInvariant() : "";
            ArenaUiCommand? cmd = null;
            switch (verb)
            {
                case "open":
                    ShowFromRegistry(world, NearestOpener(world, data));
                    return Answer(world, verb, true, null);
                case "model":
                    return Answer(world, verb, true, null);
                case "close": cmd = new ArenaClose() { Tick = t }; break;
                case "mode" when arg is "avb" or "1v1" or "ffa":
                    cmd = new ArenaSetMode(arg == "avb" ? ArenaUiMode.AvB : arg == "1v1" ? ArenaUiMode.Teams1v1 : ArenaUiMode.LastStanding) { Tick = t }; break;
                case "dest" when arg is "a" or "b": cmd = new ArenaSetDest(arg == "a" ? ArenaDest.TeamA : ArenaDest.TeamB) { Tick = t }; break;
                case "click" or "assign" or "remove" when args.Count > 1 && CharacterIdText.TryParse(args[1], out var id):
                    var list = ListShowing(id);
                    if (list is null) return Answer(world, verb, false, "no row shows " + args[1]);
                    cmd = verb switch
                    {
                        "click" => new ArenaRowClick(list.Value, id) { Tick = t },
                        "assign" => new ArenaRowDoubleClick(ArenaList.Roster, id) { Tick = t },
                        _ => new ArenaRowRelease(TeamListShowing(id) ?? list.Value, id, true, ArenaDropTarget.None, CharacterId.None) { Tick = t },
                    };
                    break;
                case "search": cmd = new ArenaSearch(string.Join(' ', args.Skip(1))) { Tick = t }; break;
                case "balance" when args.Count == 1: cmd = new ArenaToggleBalanceMenu() { Tick = t }; break;
                case "balance" when arg is "skill" or "rating": cmd = new ArenaBalance(arg == "rating") { Tick = t }; break;
                case "clear": cmd = new ArenaClear() { Tick = t }; break;
                case "swap": cmd = new ArenaSwap() { Tick = t }; break;
                case "preset" when args.Count > 1 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= 3:
                    cmd = new ArenaPreset(n - 1, args.Count > 2 && args[2] == "save") { Tick = t }; break;
                case "location" when arg is "arena" or "small" or "banner":
                    cmd = new ArenaLocation(arg == "arena" ? ArenaLocationMode.Arena : arg == "small" ? ArenaLocationMode.SmallArena : ArenaLocationMode.Banner) { Tick = t }; break;
                case "medical": cmd = new ArenaMedicalToggle() { Tick = t }; break;
                case "recruit": cmd = new ArenaRecruit() { Tick = t }; break;
                case "start": cmd = new ArenaStart() { Tick = t }; break;
                case "stop": cmd = new ArenaStop() { Tick = t }; break;
            }
            if (cmd is null) return Answer(world, verb, false, Usage);
            // task 4.5b: a command the closed window ignores (ArenaUILogic.AcceptsCommand) answers ok:false with the reason
            return Apply(world, cmd).Ignored
                ? Answer(world, verb, false, "ignored: the Arena window is closed (pg arena open first)")
                : Answer(world, verb, true, null);
        }
        catch (Exception e) { return Answer(world, "arena", false, $"{e.GetType().Name}: {e.Message}"); }
    }

    /// <summary>The list whose rows show <paramref name="id"/> (roster first, as the console has no row to name).</summary>
    ArenaList? ListShowing(CharacterId id)
    {
        foreach (var l in new[] { ArenaList.Roster, ArenaList.TeamA, ArenaList.TeamB, ArenaList.Pool })
            if (logic.RowShows(l, id)) return l;
        return null;
    }

    ArenaList? TeamListShowing(CharacterId id)
    {
        foreach (var l in new[] { ArenaList.TeamA, ArenaList.TeamB, ArenaList.Pool })
            if (logic.RowShows(l, id)) return l;
        return null;
    }

    /// <summary>The nearest finished player Registry or Banner (ArenaIdentityLogic.IsMatchUiOpenerFinished, a town Registry left out: the C++
    /// opens the town window for it, task M7) within 25,000 of the squad's first member, or null (then ShowFromRegistry binds nothing).</summary>
    static ArenaBuilding? NearestOpener(World world, PopulationData data)
    {
        foreach (var squad in PgEngine.PlayerSquads(world))
            foreach (var m in squad.Members)
                if (PgEngine.Position(world, m) is { } at)
                {
                    var hits = ArenaIdentityLogic.SphereHits(ArenaIdentity.Near(world, data, at, ArenaIdentityLogic.SiteSearchRadius), at);
                    return ArenaIdentityLogic.FindNearestMatching(at, hits,
                        b => ArenaIdentityLogic.IsMatchUiOpenerFinished(b) && !ArenaIdentityLogic.IsTownRegistry(b), out _);
                }
        return null;
    }

    string Answer(World world, string verb, bool ok, string? error)
    {
        var sb = new StringBuilder();
        sb.Append("{\"event\":").Append(Q(verb)).Append(",\"ok\":").Append(ok ? "true" : "false");
        if (error is not null) sb.Append(",\"error\":").Append(Q(error));
        sb.Append(",\"tick\":").Append(world.Tick.ToString(CultureInfo.InvariantCulture))
          .Append(",\"registry\":").Append(s.Ingress.Logic.BoundRegistryRef is { } r ? Q(ArenaIdentity.KeyText(r)) : "null")
          .Append(",\"model\":").Append(ModelJson(model.View)).Append('}');
        string json = sb.ToString();
        ProvingGroundsMod.Ctx?.Log("PG-ARENA " + json);
        return json;
    }

    static object Button(ArenaButtonView b) => new { visible = b.Visible, enabled = b.Enabled, selected = b.Selected, caption = b.Caption };

    static object Row(ArenaRowView<CharacterId> r) => new
    {
        id = CharacterIdText.Format(r.Id), name = r.Name, caption = r.Caption, selected = r.Selected,
        health = r.HealthVisible ? CMath.FormatF(r.HealthRatio, 2) : null, band = r.HealthVisible ? r.HealthBand.ToString() : null,
        handler = r.HasHandler ? CharacterIdText.Format(r.Handler) : null, handlerLabel = r.HasHandler ? r.HandlerLabel : null,
        dragValid = r.DragValid, handlerCandidate = r.HandlerCandidate, prisoner = r.Prisoner,
    };

    /// <summary>TEST ONLY: the published view as JSON (pg arena model).</summary>
    internal static string ModelJson(ArenaUiView<CharacterId> v) => JsonSerializer.Serialize(new
    {
        open = v.RequestOpen, serial = v.RequestSerial, mode = v.Mode.ToString(), teamMode = v.TeamMode, balanceMenu = v.BalanceMenuOpen,
        shape = v.ShapeKey, rosterCount = v.RosterCount, query = v.RosterQuery,
        roster = v.Roster.Select(Row), teamA = v.TeamA.Select(Row), teamB = v.TeamB.Select(Row), pool = v.Pool.Select(Row),
        destLabel = v.DestLabel, teamALabel = v.TeamALabel, teamBLabel = v.TeamBLabel, poolLabel = v.PoolLabel,
        buttons = new
        {
            modeAvB = Button(v.ModeAvB), mode1v1 = Button(v.ModeTeams1v1), modeLast = Button(v.ModeLast), destA = Button(v.DestA), destB = Button(v.DestB),
            remove = Button(v.Remove), balance = Button(v.Balance), bySkill = Button(v.BalanceSkill), byRating = Button(v.BalanceRating),
            swap = Button(v.Swap), clear = Button(v.Clear), presets = v.Presets.Select(Button), locArena = Button(v.LocArena),
            locSmall = Button(v.LocSmall), locBanner = Button(v.LocBanner), medical = Button(v.Medical), recruit = Button(v.Recruit),
            start = Button(v.Start), stop = Button(v.Stop),
        },
        medicalTip = v.MedicalTip,
        preview = v.HasPreview ? CharacterIdText.Format(v.Preview) : null, previewImage = v.PreviewImageVisible,
        previewName = v.PreviewName, previewMeta = v.PreviewMeta, previewStats = v.PreviewStats,
        summary = v.Summary, status = v.Status,
    });
}
