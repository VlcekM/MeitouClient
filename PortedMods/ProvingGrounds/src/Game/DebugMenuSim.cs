using System.Globalization;
using System.Text;
using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Port of the rules half of upstream/ProvingGrounds/plugin/src/DebugMenu.h/.cpp (Proving Grounds by Shutori, GPLv3), task 4.6b (PA §2.24; CAP 7.2,
// 7.10). Every decision and text is Core/Glue/DebugMenuLogic.cs (tested in Tests/Glue/DebugMenuLogicTests.cs); the window half is src/Ui/DebugMenu.cs
// (DebugMenuScreen, and the key). This file is the sim half: Toggle (the key, through ArenaUI.PollDebugMenuKey's slot), the button presses as the
// window's DebugMenuPress commands (what the window drew, task 8.7b) and its Closed report (UiClick), Tick, AbandonWorldState, the published DebugMenuUiModel. Meitou 0.17.3 (tag v0.17.3, fa34a1b). Sim thread, serial steps.
//
// Engine calls of the C++ and what replaces them (E1):
//   SquadUtil::CollectPlayerSquad .................. PgEngine.PlayerSquads, members valid and not dead (as PgArenaUIHost, task 4.5a)
//   ArenaIngress::GetBoundRegistry ................. ArenaIngress.GetBoundRegistry
//   PrisonerUtil::HasRuntimeActivity / CollectNearbyCagePrisoners / GetRosterPrisoners / IsRosterPrisoner
//                                                   ... src/Game/PrisonerUtil.cs (task 6.1)
//   c->isValid() / isDead() / getName() ............ PgEngine.Exists / IsDead / Name
//   LeaderboardStore::GetRating / GetMarks ......... LeaderboardStore.GetPlayerRating / GetMarks (src/Game/LeaderboardStore.cs; C++ :425-439, :457-473)
//   LeaderboardStore::SetRating / SetMarks ......... LeaderboardStore.SetRating / SetMarks (the same code as the test-only pg give-rating /
//                                                   pg give-marks: ArenaWorldState.SetRating / SetMarks; refused while the ledger is not Ready)
//   TownArena::BeginTrial / SetScheduleEnabled / IsScheduleEnabled / Cancel / GetStatus
//                                                   ... src/Town/TownArena.cs (task 7.1: the schedule switch, Cancel, the status; task 7.3: BeginTrial)
//   TownDiagnosticRun::Get / StatusText / ValidationError / StartPreset / StartConfigured / Pause / Resume / End / SetTarget / ToggleModel /
//     CycleReducedInfluence / ToggleCoverage / CycleUnderdogPercent / ToggleStyleTierBalance
//                                                   ... the world's run, src/Town/TownDiagnosticRun.cs (TownArena.Diagnostic; task 8.7a): the
//                                                   store's saved state, ValidationError and the starts with this call's world
//   GetTickCount() ................................. PgClock.NowMs
//   PGLog::Debug("... debug menu created") ......... PGLog.Debug, once per process at the first Toggle (the C++ creates the window at the title)
//   every MyGUI call ............................... the window half
// Callers (C++ -> port): ArenaUI.cpp:2967-2976 PollF8Toggle -> Toggle (the frame loop's ArenaUI.PollDebugMenuKey slot, from the key's command);
// ArenaUI.cpp:3154 Tick; ArenaUI.cpp:3004-3007 Escape -> Close (Meitou's own Escape, reported: OnClosedFromWindow); WorldLifecycle.cpp:70, :261
// AbandonWorldState (the save path, task 2.5).

/// <summary>The window model of the debug menu <c>pg.debug</c> (task 4.6b). A new object only when the view changed.</summary>
sealed record DebugMenuUiModel(DebugMenuView View) : PgUiModel;

/// <summary>C++ :639 "Proving Grounds: debug menu created", once per process (the C++ creates the window once, at the title screen).</summary>
static class DebugMenuCreatedLog
{
    static bool logged;

    internal static void Once()
    {
        if (logged) return;
        logged = true;
        PGLog.Debug(DebugMenuTexts.Created);
    }

    /// <summary>TEST seam: the next Once logs again.</summary>
    internal static void ResetForTest() => logged = false;
}

/// <summary>The sim half of the debug menu (task 4.6b). One per world, owned by ProvingGroundsSystem.</summary>
sealed class DebugMenuSim
{
    internal const string ScreenId = "pg.debug";

    /// <summary>What the menu reads and writes in the rest of the system.</summary>
    internal sealed record Services(ArenaIngress Ingress, PrisonerUtil Prisoners, Town.TownArena Town, PgClock Clock,
        Func<World, CharacterId, float> Rating, Func<World, CharacterId, int> Marks,
        Func<World, CharacterId, float, bool> SetRating, Func<World, CharacterId, int, bool> SetMarks);

    readonly Services s;
    readonly DebugMenuLogic<CharacterId> logic = new(CharacterId.None);
    readonly Host host;
    DebugMenuUiModel model = new(new DebugMenuView());
    readonly DebugMenuKeyPresses presses = new();

    internal DebugMenuSim(Services services)
    {
        s = services;
        host = new Host(services);
    }

    internal DebugMenuUiModel Model => model;
    internal DebugMenuLogic<CharacterId> Logic => logic;

    /// <summary>The F10 phase row.</summary>
    internal string Phase => logic.IsVisible ? (logic.OnFightersTab ? "open: fighters" : "open: diagnostics") : "closed";

    /// <summary>IDebugMenuHost over the world of one call (the E1 table above). Bound per call; sim thread.</summary>
    sealed class Host(Services s) : IDebugMenuHost<CharacterId>
    {
        World? world;
        internal Host Bind(World w) { world = w; return this; }
        World W => world ?? throw new InvalidOperationException("DebugMenuSim host used before Bind");

        public CharacterId None => CharacterId.None;
        public bool IsNone(CharacterId c) => c.IsNone;
        public bool IsValid(CharacterId c) => PgEngine.Exists(W, c);
        public bool IsDead(CharacterId c) => PgEngine.IsDead(W, c);
        public string Name(CharacterId c) => PgEngine.Name(W, c) ?? "";
        public string Key(CharacterId c) => CharacterIdText.Format(c);   // port only (task 8.7b): the drawn rows' and selection's identity

        /// <summary>C++ SquadUtil::CollectPlayerSquad (SquadUtil.cpp:22-67): each active player squad's members, valid and not dead.</summary>
        public void CollectPlayerSquad(List<CharacterId> into)
        {
            foreach (var squad in PgEngine.PlayerSquads(W))
                foreach (var m in squad.Members)
                    if (PgEngine.Exists(W, m) && !PgEngine.IsDead(W, m)) into.Add(m);
        }

        public bool HasBoundRegistry() => s.Ingress.GetBoundRegistry(W) is not null;
        public bool PrisonersHaveRuntimeActivity() => s.Prisoners.HasRuntimeActivity(W);
        public void CollectNearbyCagePrisoners(List<CharacterId> into)
        {
            if (s.Ingress.GetBoundRegistry(W) is { } registry) s.Prisoners.CollectNearbyCagePrisoners(W, registry, into);
        }
        public IReadOnlyList<CharacterId> RosterPrisoners() => s.Prisoners.GetRosterPrisoners(W);
        public bool IsRosterPrisoner(CharacterId c) => s.Prisoners.IsRosterPrisoner(W, c);

        public float GetRating(CharacterId c) => s.Rating(W, c);
        public int GetMarks(CharacterId c) => s.Marks(W, c);
        public bool SetRating(CharacterId c, float rating) => s.SetRating(W, c, rating);
        public bool SetMarks(CharacterId c, int marks) => s.SetMarks(W, c, marks);

        public void TownBeginTrial() => s.Town.BeginTrial(W);                                // task 7.3 (the C++ result is not read, DebugMenu.cpp:464)
        public bool TownIsScheduleEnabled() => s.Town.IsScheduleEnabled(W);                   // task 7.1
        public void TownSetScheduleEnabled(bool enabled) => s.Town.SetScheduleEnabled(W, enabled);   // task 7.1
        public void TownCancel() => s.Town.Cancel(W);                                        // task 7.1
        public string TownStatus() => s.Town.GetStatus(W);                                   // task 7.1

        // ---- TownDiagnosticRun: the world's run (src/Town/TownDiagnosticRun.cs, task 8.7a) ----
        public TownDiagnosticData.State Diagnostic() => s.Town.Diagnostic.Get();                                   // C++ TownDiagnosticRun::Get
        public string DiagnosticStatusText() => s.Town.Diagnostic.StatusText();                                    // C++ TownDiagnosticRun::StatusText
        public string DiagnosticValidationError() => s.Town.Diagnostic.ValidationError(W);                         // C++ TownDiagnosticRun::ValidationError
        public bool DiagnosticStartPreset(bool resetTownRecords) => s.Town.Diagnostic.StartPreset(W, resetTownRecords);   // C++ StartPreset
        public bool DiagnosticStartConfigured(bool resetTownRecords) => s.Town.Diagnostic.StartConfigured(W, resetTownRecords);   // C++ StartConfigured
        public bool DiagnosticPause() => s.Town.Diagnostic.Pause();                                                // C++ TownDiagnosticRun::Pause
        public bool DiagnosticResume() => s.Town.Diagnostic.Resume();                                              // C++ TownDiagnosticRun::Resume
        public bool DiagnosticEnd() => s.Town.Diagnostic.End();                                                    // C++ TownDiagnosticRun::End
        public bool DiagnosticSetTarget(int target) => s.Town.Diagnostic.SetTarget(target);                        // C++ TownDiagnosticRun::SetTarget
        public bool DiagnosticToggleModel(int model) => s.Town.Diagnostic.ToggleModel(model);                      // C++ TownDiagnosticRun::ToggleModel
        public bool DiagnosticCycleReducedInfluence() => s.Town.Diagnostic.CycleReducedInfluence();                // C++ CycleReducedInfluence
        public bool DiagnosticToggleCoverage(int coverage) => s.Town.Diagnostic.ToggleCoverage(coverage);          // C++ TownDiagnosticRun::ToggleCoverage
        public bool DiagnosticCycleUnderdogPercent() => s.Town.Diagnostic.CycleUnderdogPercent();                  // C++ CycleUnderdogPercent
        public bool DiagnosticToggleStyleTierBalance() => s.Town.Diagnostic.ToggleStyleTierBalance();              // C++ ToggleStyleTierBalance

        public uint NowMs => s.Clock.NowMs;
    }

    void Publish()
    {
        var view = logic.BuildView();
        if (view.Equals(model.View)) return;
        model = new DebugMenuUiModel(view) { RequestSerial = view.RequestSerial, RequestOpen = view.RequestOpen };
    }

    /// <summary>The debug menu key was pressed (the window side's UiKey postfix sent it for the key the settings page last PUBLISHED, which may
    /// be a tick ahead of the key in force). Counted; the frame loop's PollDebugMenuKey slot toggles once per press while the setting in force is
    /// on (C++ PollF8Toggle's place in the frame, ArenaUI.cpp:3153).</summary>
    internal void KeyPressed() => presses.Press();

    /// <summary>C++: ArenaUI.cpp:2967-2976 PollF8Toggle: each key press since the last slot toggles, when the setting is on now
    /// (PGConfig::F8OpensDebugMenu; the settings page's change is applied at the start of this tick's Inputs, task 2.6). PGSettingsUI::Close has
    /// nothing to close: Meitou's settings panel takes every key while open (GameLoop.cs:883-887), so the key cannot reach here then.</summary>
    internal void PollKey(World world)
    {
        for (int n = presses.Take(PGConfig.F8OpensDebugMenu()); n > 0; n--) Toggle(world);
    }

    /// <summary>C++: DebugMenu.cpp:642-658 Toggle.</summary>
    internal void Toggle(World world)
    {
        DebugMenuCreatedLog.Once();   // C++ :639 (Create, once)
        logic.Toggle(host.Bind(world));
        Publish();
    }

    /// <summary>C++: DebugMenu.cpp:660 Close.</summary>
    internal void Close()
    {
        logic.Close();
        Publish();
    }

    internal bool IsVisible => logic.IsVisible;

    /// <summary>C++: DebugMenu.cpp:662-666 Tick (the frame loop's DebugMenu.Tick slot, ArenaUI.cpp:3154).</summary>
    internal void Tick(World world)
    {
        if (!logic.IsVisible) return;
        if (logic.Tick(host.Bind(world))) Publish();
    }

    /// <summary>C++: DebugMenu.cpp:520-533 AbandonWorldState.</summary>
    internal void AbandonWorldState(World world)
    {
        logic.AbandonWorldState(host.Bind(world));
        Publish();
    }

    /// <summary>A widget event of the window as task 4.4's UiClick: the framework's Closed report (Escape, the title bar's close button:
    /// OnWindowButtonPressed :511-515, checked against the request serial) and a button press with no drawn state (the test-only
    /// <c>pg debug press</c>: pressed against the current published model as if it were drawn now). The window's own presses come as
    /// <see cref="DebugMenuPress"/> (<see cref="ApplyPress"/>).</summary>
    internal (string Done, bool Ignored) Apply(World world, UiClickData c)
    {
        if (c.Kind == UiEventKind.Closed)
        {
            if (!logic.OnClosedFromWindow(c.Value)) return ("closed (already, or for an older request)", true);
            Publish();
            return ("closed from the window", false);
        }
        if (!IsButtonClick(c.Kind)) return ($"{c.Kind} on {c.Widget}: ignored", true);
        return ApplyPress(world, new DebugMenuPress(c.Widget, DebugMenuDrawn.Of(model.View, c.Widget)) { Tick = 0 });
    }

    /// <summary>A button press of the window (each button's eventMouseButtonClick handler) carrying what the window drew for that widget (task
    /// 8.7b; DebugMenuScreen's Click from the model it last drew). A press the C++ could not have received (the window closed, a hidden or
    /// disabled widget) is ignored; one whose drawn state is no longer the sim's (a list that changed between the draw and the click, a label
    /// already replaced: port only) is refused, nothing changes and no status text is shown.</summary>
    internal (string Done, bool Ignored) ApplyPress(World world, DebugMenuPress p)
    {
        var current = DebugMenuDrawn.Of(model.View, p.Widget);   // the published model is the logic's current state
        switch (logic.Press(host.Bind(world), p.Widget, p.Drawn))
        {
            case DebugMenuPressResult.Ignored: return ($"press on {p.Widget}: ignored", true);
            case DebugMenuPressResult.Stale: return ($"press on {p.Widget}: stale, refused: {StaleDifference(p.Drawn, current)}", true);
        }
        Publish();
        return ($"{p.Widget}: {model.View.Status}", false);
    }

    /// <summary>What a stale press's drawn state has that the menu's current one does not (the first field that differs).</summary>
    static string StaleDifference(DebugMenuDrawn drawn, DebugMenuDrawn current)
    {
        static string Tab(bool fighters) => fighters ? "fighters" : "diagnostics";
        if (drawn.Fighters != current.Fighters) return $"drawn on the {Tab(drawn.Fighters)} tab, the menu is on the {Tab(current.Fighters)} tab";
        if (drawn.Shown != current.Shown) return $"drawn '{drawn.Shown}', the menu shows '{current.Shown}'";
        return $"drawn {(drawn.Enabled ? "enabled" : "disabled")}, the menu has it {(current.Enabled ? "enabled" : "disabled")}";
    }

    /// <summary>Every debug menu button binds <c>eventMouseButtonClick</c> (DebugMenu.cpp:138 MakeButton; none binds eventMouseButtonPressed):
    /// the screen keeps the default ButtonEvent (Click, task 4.4b), so a button reaches here as <see cref="UiEventKind.Click"/> only.</summary>
    static bool IsButtonClick(UiEventKind kind) => kind == UiEventKind.Click;

    // ---------- TEST ONLY: pg debug <toggle|press <widget>|model> (task 4.6b) ----------

    const string Usage = "usage pg debug <toggle|press <widget>|model>";

    /// <summary>TEST ONLY <c>pg debug toggle|press &lt;widget&gt;|model</c>: <c>toggle</c> = Toggle (as the key, without the setting check);
    /// <c>press</c> = the window's own press command for a widget (ok:false when the closed window or a hidden / disabled widget ignores it);
    /// <c>model</c> = the model only. Answers one PG-DEBUG line.</summary>
    internal string Command(World world, IReadOnlyList<string> args)
    {
        try
        {
            string verb = args.Count > 0 ? args[0] : "";
            switch (verb)
            {
                case "toggle":
                    Toggle(world);
                    return Answer(world, verb, true, null);
                case "press" when args.Count == 2:
                    var (_, ignored) = Apply(world, new UiClickData(ScreenId, args[1], UiEventKind.Click, "", 0, false));
                    return ignored ? Answer(world, verb, false, "ignored: the window is closed, or the widget hidden or disabled") : Answer(world, verb, true, null);
                case "model":
                    return Answer(world, verb, true, null);
                default:
                    return Answer(world, verb.Length > 0 ? verb : "debug", false, Usage);
            }
        }
        catch (Exception e) { return Answer(world, "debug", false, $"{e.GetType().Name}: {e.Message}"); }
    }

    string Answer(World world, string verb, bool ok, string? error)
    {
        var v = model.View;
        var sb = new StringBuilder();
        sb.Append("{\"event\":").Append(PGLog.Quote(verb)).Append(",\"ok\":").Append(ok ? "true" : "false");
        if (error is not null) sb.Append(",\"error\":").Append(PGLog.Quote(error));
        sb.Append(",\"tick\":").Append(world.Tick.ToString(CultureInfo.InvariantCulture))
          .Append(",\"selected\":").Append(logic.SelectedCharacter.IsNone ? "null" : PGLog.Quote(CharacterIdText.Format(logic.SelectedCharacter)))
          .Append(",\"characters\":").Append(System.Text.Json.JsonSerializer.Serialize(logic.Characters.Select(CharacterIdText.Format)))
          .Append(",\"model\":").Append(System.Text.Json.JsonSerializer.Serialize(new
          {
              open = v.RequestOpen, serial = v.RequestSerial, revision = v.Revision, fighters = v.Fighters, heading = v.Heading, page = v.PageLabel,
              prev = v.PrevEnabled, next = v.NextEnabled, rows = v.Rows.Where(r => r.Visible).Select(r => new { text = r.Text, selected = r.Selected }),
              selectedName = v.SelectedName, rating = v.Rating, marks = v.Marks, summary = v.DiagnosticSummary, diagnostics = v.DiagnosticLabels,
              status = v.Status,
          })).Append('}');
        string json = sb.ToString();
        ProvingGroundsMod.Ctx?.Log("PG-DEBUG " + json);
        return json;
    }
}
