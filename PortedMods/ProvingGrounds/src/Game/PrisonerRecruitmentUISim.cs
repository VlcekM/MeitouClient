using System.Globalization;
using System.Text;
using System.Text.Json;
using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Port of the rules half of upstream/ProvingGrounds/plugin/src/PrisonerRecruitmentUI.h/.cpp (Proving Grounds by Shutori, GPLv3), task 6.4a (PA §2.19;
// CAP 7.0-7.4, 15). Every decision and text is Core/Glue/PrisonerRecruitmentUILogic.cs (tested in Tests/Glue/PrisonerRecruitmentUILogicTests.cs);
// the window half (the layout from EnsureWindow :486-559, LayoutWindow / LayoutRows / LayoutDetails / FitWindowToScreen, the portraits, the scroll
// canvases) is task 6.4b in src/Ui/PrisonerRecruitmentUI.cs. This file is the sim half: Show / Close / Tick / IsVisible / AbandonWorldState
// (PrisonerRecruitmentUI.h:5-9), the window's commands (src/Engine/PgCommand.cs "task 6.4a": RecruitmentSelect, RecruitmentClose, RecruitPrisoner)
// and the published PrisonerRecruitmentUiModel. Meitou 0.17.3 (tag v0.17.3, fa34a1b). Sim thread, serial steps.
//
// Engine calls of the C++ and what replaces them (E1; IPrisonerRecruitmentUIHost member -> adapter method, bound in Host below):
//   PrisonerUtil::GetRosterPrisoners / IsRosterPrisoner / HasRuntimeActivity (:216, :240, :242) .. src/Game/PrisonerUtil.cs (task 6.1)
//   c->isValid(), isDead(), isUnconcious(), getName() ........ PgEngine.Exists / IsDead / IsUnconscious / Name
//   ArenaIngress::IsPending, GetBoundRegistry (:241, :612) ... src/Game/ArenaIngress.cs (task 4.3b); registry->isValid() = GetBoundRegistry not null
//       (it resolves the bound building now: null when it is gone)
//   SparSession::IsActive (:241) ............................. src/Game/SparSession.cs (task 3.3)
//   ou && ou->player (:244) ................................... src/Engine/Recruiting.cs Ready
//   LeaderboardStore::GetMarks (:247, :268, :290) ............. src/Game/LeaderboardStore.cs (task 5.1)
//   PrisonerRecruitment::AverageCombatStat / RequiredMarks / Recruit (:248, :266-269, :470) .. src/Game/PrisonerRecruitment.cs (task 6.4a)
//   ArenaUI::ShowFromRegistry(registry, false) (:614) ......... src/Game/ArenaUISim.cs ShowFromRegistry (task 4.5a)
//   PGLog::Debug / Error ...................................... PGLog.Debug / ModLog.Error
//   PortraitManager::setImageWidget (BindPortrait :109-122), every MyGUI call ... the window half (6.4b), from the published model
// Callers (C++ -> port): ArenaUI.cpp:1550 Show (the Arena window's "Recruit Prisoners", through PgArenaUIHost.ShowRecruitment); ArenaUI.cpp:3171
// Tick (the frame-loop slot); ArenaUI.cpp:3202, :3254 AbandonWorldState (through PgArenaUIHost); ArenaUI.cpp:2994-2997 Escape -> Close (the window
// framework's Closed report); the TEST-ONLY `pg recruit`.

/// <summary>The window model of the recruitment window <c>pg.recruitment</c> (task 6.4a). A new object only when the view changed.</summary>
sealed record PrisonerRecruitmentUiModel(PrisonerRecruitmentUiView<CharacterId> View) : PgUiModel;

/// <summary>The sim half of the recruitment window (task 6.4a). One per world, owned by ProvingGroundsSystem.</summary>
sealed class PrisonerRecruitmentUISim
{
    internal const string ScreenId = "pg.recruitment";

    /// <summary>The subsystems the window's rules call (built by ProvingGroundsSystem). <see cref="Spar"/> and <see cref="ArenaUi"/> are lazy.</summary>
    internal sealed record Services(PrisonerRecruitment Recruitment, PrisonerUtil Prisoners, ArenaIngress Ingress, Func<SparSession> Spar,
        LeaderboardStore Store, Func<ArenaUISim> ArenaUi);

    readonly Services s;
    readonly PrisonerRecruitmentUILogic<CharacterId> logic = new();
    readonly Host host;
    PrisonerRecruitmentUiModel model;

    internal PrisonerRecruitmentUISim(Services services)
    {
        s = services;
        host = new Host(s);
        model = new PrisonerRecruitmentUiModel(logic.BuildView());
    }

    /// <summary>IPrisonerRecruitmentUIHost bound to the world of the current call.</summary>
    sealed class Host(Services s) : IPrisonerRecruitmentUIHost<CharacterId>
    {
        internal World? World;
        World W => World ?? throw new InvalidOperationException("PrisonerRecruitmentUI used before a world was bound");
        public IReadOnlyList<CharacterId> RosterPrisoners() => s.Prisoners.GetRosterPrisoners(W);
        public bool Valid(CharacterId c) => !c.IsNone && PgEngine.Exists(W, c);
        public bool IsDead(CharacterId c) => PgEngine.IsDead(W, c);
        public bool IsUnconscious(CharacterId c) => PgEngine.IsUnconscious(W, c);
        public bool IsRosterPrisoner(CharacterId c) => s.Prisoners.IsRosterPrisoner(W, c);
        public bool IngressPending() => s.Ingress.IsPending(W);
        public bool SparActive() => s.Spar().IsActive(W);
        public bool PrisonerRuntimeActivity() => s.Prisoners.HasRuntimeActivity(W);
        public bool PlayerReady() => Recruiting.Ready(W);
        public bool RecruitAvailable() => Recruiting.Available;
        public int GetMarks(CharacterId c) => s.Store.GetMarks(W, c);
        public float AverageCombatStat(CharacterId c) => s.Recruitment.AverageCombatStat(W, c);
        public int RequiredMarks(CharacterId c) => s.Recruitment.RequiredMarks(W, c);
        public string Name(CharacterId c) => PgEngine.Name(W, c) ?? "";
        public bool Recruit(CharacterId c, out string status) => s.Recruitment.Recruit(W, c, out status);
        public bool BoundRegistryValid() => s.Ingress.GetBoundRegistry(W) is not null;   // GetBoundRegistry resolves the building now (null: gone)
        public void ShowArenaFromBoundRegistry() => s.ArenaUi().ShowFromRegistry(W, s.Ingress.GetBoundRegistry(W), false);
        public void Debug(string line) => PGLog.Debug(line);
        public void Error(string line) => ModLog.Error(line);
    }

    Host H(World world) { host.World = world; return host; }

    internal PrisonerRecruitmentUiModel Model => model;

    /// <summary>The F10 phase row.</summary>
    internal string Phase => logic.IsVisible ? "open: " + model.View.Status : "closed";

    void Publish()
    {
        var view = logic.BuildView();
        if (view.Equals(model.View)) return;
        model = new PrisonerRecruitmentUiModel(view) { RequestSerial = view.RequestSerial, RequestOpen = view.RequestOpen };
    }

    /// <summary>C++: PrisonerRecruitmentUI.cpp:591-606 Show.</summary>
    internal void Show(World world)
    {
        logic.Show(H(world));
        Publish();
    }

    /// <summary>C++: PrisonerRecruitmentUI.cpp:608-615 Close (the Arena window comes back from the bound Registry).</summary>
    internal void Close(World world)
    {
        logic.Close(H(world));
        Publish();
    }

    /// <summary>C++: PrisonerRecruitmentUI.cpp:617-620 IsVisible.</summary>
    internal bool IsVisible => logic.IsVisible;

    /// <summary>C++: PrisonerRecruitmentUI.cpp:622-636, the frame-loop slot (ArenaUI.cpp:3171): the refresh while visible (the window's fit to the
    /// screen is the window half's).</summary>
    internal void Tick(World world)
    {
        if (!logic.IsVisible) return;
        logic.Tick(H(world));
        Publish();
    }

    /// <summary>C++: PrisonerRecruitmentUI.cpp:638-647 AbandonWorldState.</summary>
    internal void AbandonWorldState(World world)
    {
        logic.AbandonWorldState();
        Publish();
    }

    /// <summary>One recruitment window command (serial Inputs). A row click or the close button that arrives while the window is closed (sent
    /// just before it closed) is ignored: the C++ handlers are reachable only through the visible window. A RecruitPrisoner is always recorded
    /// (the window's wait ends), run only while the window shows that prisoner at that price.</summary>
    internal (string Done, bool Ignored) Apply(World world, RecruitmentUiCommand command)
    {
        string done = command.GetType().Name;
        switch (command)
        {
            case RecruitPrisoner r:
                bool ran = logic.OnRecruitCommand(H(world), r.Prisoner, r.ShownPrice, r.Seq);
                Publish();
                return ran ? (done + ": " + model.View.Status, false)
                    : (done + (model.View.LastSeq != r.Seq ? ": dropped (an old request)" : model.View.LastOfferChanged ? ": not run (the contract changed since it was shown)" : ": ignored (no window or no selection)"), true);
            case RecruitmentSelect select:
                if (!logic.IsVisible) return (done + ": ignored (window closed)", true);
                if (!logic.OnRowClicked(H(world), select.Prisoner)) return (done + ": no row shows " + CharacterIdText.Format(select.Prisoner), true);
                break;
            case RecruitmentClose:
                if (!logic.IsVisible) return (done + ": ignored (window closed)", true);
                logic.OnCloseClicked(H(world));
                break;
            default:
                return ("unknown recruitment command " + done, true);
        }
        Publish();
        return (done + ": " + model.View.Status, false);
    }

    /// <summary>A generic widget event of the window (task 4.4's UiClick): only the framework's Closed report (Escape, the title bar's close
    /// button: OnWindowButtonPressed :480-484, ArenaUI.cpp:2994-2997) is ours; the buttons and rows send RecruitmentUiCommands.</summary>
    internal (string Done, bool Ignored) Apply(World world, UiClickData c)
    {
        if (c.Kind != UiEventKind.Closed) return ($"ignored {c.Kind} on {c.Widget} (the recruitment window sends its own commands)", true);
        if (!logic.OnClosedFromWindow(H(world), c.Value)) return ("closed (already, or for an older request)", true);
        Publish();
        return ("closed from the window", false);
    }

    // ---------- TEST ONLY: pg recruit (task 6.4a; kept for the scenarios beside the window half, task 6.4b) ----------

    const string Usage = "usage pg recruit <open | close | model | select c:<slot>.<gen> | grant c:<slot>.<gen> [force]>";

    /// <summary>The test verb's own RecruitPrisoner sequence (it numbers each request above the model's LastSeq, as the window must: a request at or below it is dropped). Per world by construction.</summary>
    long testSeq = 1_000_000;

    /// <summary>TEST ONLY <c>pg recruit ...</c>: <c>open</c> = Show (as the Arena window's "Recruit Prisoners" opens it); <c>close</c> = the window's
    /// "Back to Arena" (Close: the Arena window comes back from the bound Registry); <c>model</c> = the model; <c>select &lt;id&gt;</c> = a row click;
    /// <c>grant &lt;id&gt;</c> = what the window sends for its "Grant Freedom" button: the row is clicked first when it is not the selection, then a
    /// RecruitPrisoner with the shown price, run at once (the window's command path). A disabled button answers ok:false with its caption and
    /// explanation (the game cannot press it) unless <c>force</c> is given: then the command is sent anyway (a stale click), so the recruit's own
    /// refusal texts can be read. One PG-RECRUIT line, with the character's state (player, imprisoned, in the player squad, Marks).</summary>
    internal string Command(World world, IReadOnlyList<string> args)
    {
        string verb = args.Count > 0 ? args[0].ToLowerInvariant() : "";
        var who = CharacterId.None;
        try
        {
            switch (verb)
            {
                case "open":
                    Show(world);
                    return Answer(world, verb, true, null, who);
                case "close":
                    if (!logic.IsVisible) return Answer(world, verb, false, "the recruitment window is closed", who);
                    Close(world);
                    return Answer(world, verb, true, null, who);
                case "model":
                    return Answer(world, verb, true, null, who);
                case "select" when args.Count == 2 && CharacterIdText.TryParse(args[1], out who):
                {
                    var (d, ignored) = Apply(world, new RecruitmentSelect(who) { Tick = world.Tick });
                    return Answer(world, verb, !ignored, ignored ? d : null, who);
                }
                case "grant" when args.Count is 2 or 3 && CharacterIdText.TryParse(args[1], out who)
                    && (args.Count == 2 || args[2].Equals("force", StringComparison.OrdinalIgnoreCase)):
                {
                    string? error = Grant(world, who, args.Count == 3);
                    return Answer(world, "grant", error is null, error, who);
                }
                default:
                    return Answer(world, verb.Length > 0 ? verb : "recruit", false, Usage, who);
            }
        }
        catch (Exception e) { return Answer(world, "recruit", false, $"{e.GetType().Name}: {e.Message}", who); }
    }

    /// <summary>The "Grant Freedom" button as the window sends it; null = sent (the recruit's answer is the status line), else why the game could not
    /// press it.</summary>
    string? Grant(World world, CharacterId who, bool force)
    {
        if (!logic.IsVisible) return "the recruitment window is closed (pg recruit open first)";
        if (!logic.HasSelected || logic.Selected != who)
        {
            if (!logic.OnRowClicked(H(world), who) && !force) return "no row shows " + CharacterIdText.Format(who);
            Publish();
        }
        var v = model.View;
        if (!v.ActionEnabled && !force) return $"the button is disabled: {v.ActionCaption} ({v.Explanation})";
        testSeq = Math.Max(testSeq, v.LastSeq) + 1;   // above the model's LastSeq (an older sequence is dropped)
        var (done, _) = Apply(world, new RecruitPrisoner(who, testSeq, v.ShownPrice) { Tick = world.Tick });
        ProvingGroundsMod.Ctx?.Log($"prisoner recruitment for {CharacterIdText.Format(who)}: {done}");
        return null;
    }

    string Answer(World world, string verb, bool ok, string? error, CharacterId who)
    {
        var sb = new StringBuilder();
        sb.Append("{\"event\":").Append(PGLog.Quote(verb)).Append(",\"ok\":").Append(ok ? "true" : "false");
        if (error is not null) sb.Append(",\"error\":").Append(PGLog.Quote(error));
        sb.Append(",\"tick\":").Append(world.Tick.ToString(CultureInfo.InvariantCulture))
          .Append(",\"available\":").Append(Recruiting.Available ? "true" : "false");
        if (!who.IsNone) sb.Append(",\"who\":").Append(Who(world, who));
        sb.Append(",\"model\":").Append(ModelJson(model.View)).Append('}');
        string json = sb.ToString();
        ProvingGroundsMod.Ctx?.Log("PG-RECRUIT " + json);
        return json;
    }

    /// <summary>TEST ONLY: a character's state for the scenario (player, imprisoned, in the player squad, roster prisoner, Marks).</summary>
    string Who(World world, CharacterId c) => JsonSerializer.Serialize(new
    {
        id = CharacterIdText.Format(c), exists = PgEngine.Exists(world, c), player = PgEngine.IsPlayer(world, c),
        imprisoned = Cages.IsImprisoned(world, c), inPlayerSquad = SquadUtil.CollectPlayerSquad(world).Contains(c),
        rosterPrisoner = s.Prisoners.IsRosterPrisoner(world, c), marks = s.Store.GetMarks(world, c),
        price = s.Recruitment.RequiredMarks(world, c),
    });

    /// <summary>TEST ONLY: the published view, compactly.</summary>
    internal static string ModelJson(PrisonerRecruitmentUiView<CharacterId> v) => JsonSerializer.Serialize(new
    {
        open = v.RequestOpen, serial = v.RequestSerial, shows = v.Shows, rowBuilds = v.RowBuilds, caption = v.Caption, subtitle = v.Subtitle,
        rosterHeading = v.RosterHeading, close = v.CloseCaption,
        rows = v.Rows.Select(r => new { id = CharacterIdText.Format(r.Character), index = r.Index, name = r.Name, marks = r.Marks, selected = r.Selected }),
        selected = v.HasSelected ? CharacterIdText.Format(v.Selected) : null, portrait = v.PortraitVisible,
        name = v.SelectedName, stats = v.Stats, explanation = v.Explanation, button = v.ActionCaption, enabled = v.ActionEnabled,
        status = v.Status, price = v.ShownPrice, lastSeq = v.LastSeq, offerChanged = v.LastOfferChanged,
    });
}
