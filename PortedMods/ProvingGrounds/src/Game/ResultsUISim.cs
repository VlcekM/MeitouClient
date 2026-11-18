using System.Globalization;
using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Port of the rules half of upstream/ProvingGrounds/plugin/src/ResultsUI.h/.cpp (Proving Grounds by Shutori, GPLv3), task 4.6a (PA §2.12;
// CAP 7.0-7.4). Every C++ decision and text is Core/Glue/ResultsUILogic.cs (tested in Tests/Glue/ResultsUILogicTests.cs); the window half (the
// layout from EnsureWindow / LayoutWindow / FitWindowToContent, the portraits, the clicks) is src/Ui/ResultsUI.cs (ResultsScreen). This file is
// the sim half the M4 brief's UI rules ask for: the delayed show in the frame loop (the C++ slot ResultsUI::Tick, ArenaUI.cpp:3168), the
// window's clicks (UiClick) and the published ResultsUiModel. Meitou 0.17.3 (tag v0.17.3, fa34a1b). Sim thread, serial steps only.
//
// Engine calls of the C++ and what replaces them (E1):
//   GetTickCount() (ScheduleShow :614, Tick :655) ... PgClock.NowMs (sim milliseconds, frozen while paused)
//   SparStats::HasSnapshot / GetSnapshot / GetPodiumCharacter (:592, :609, :664-666) ... src/Game/SparStats.cs (task 3.2a; the podium
//       character is the id captured at freeze time, resolved against this tick's table)
//   character->isValid() (BindPortrait :100) ... PgEngine.Exists (inside SparStats.GetPodiumCharacter)
//   PortraitManager::setImageWidget, every MyGUI call ... the window half, from the published model (Portraits.Bind, spike S7)
//   PGLog::Debug ... Core PGLog.Debug
// Callers (C++ -> port): SparSession.cpp:659-660 Cancel / Close, :719 ScheduleShow(1.0f) (task 3.3, through Ui.ResultsUI); ArenaIngress.cpp:1323-1324
// Cancel / Close (task 4.3b); WorldLifecycle.cpp:129 Close in PrepareForManualSave (task 2.5); ArenaUI.cpp:3019-3021 the Escape key (Meitou's
// own Escape closes the window and reports it: OnClosedFromWindow).

/// <summary>The window model of the Results window <c>pg.results</c> (task 4.6a). A new object only when the view changed.</summary>
sealed record ResultsUiModel(ResultsUiView<CharacterId> View) : PgUiModel;

/// <summary>The sim half of the Results window (task 4.6a). One per world, owned by ProvingGroundsSystem (through Ui.ResultsUI).</summary>
sealed class ResultsUISim(SparStats stats, PgClock clock)
{
    internal const string ScreenId = "pg.results";

    readonly ResultsUILogic<CharacterId> logic = new();
    ResultsUiModel model = new(new ResultsUiView<CharacterId>());

    /// <summary>IResultsHost over SparStats, the clock and the world of one call (a struct: nothing outlives the call).</summary>
    readonly struct Host(World world, SparStats stats, PgClock clock) : IResultsHost<CharacterId>
    {
        public uint NowMs => clock.NowMs;
        public bool HasSnapshot() => stats.HasSnapshot();
        public SparPodium.Snapshot GetSnapshot() => stats.GetSnapshot();
        public bool TryPodiumCharacter(int index, out CharacterId id)
        {
            var c = stats.GetPodiumCharacter(world, index);
            id = c ?? CharacterId.None;
            return c is not null;
        }
        public void Debug(string line) => PGLog.Debug(line);
    }

    Host H(World world) => new(world, stats, clock);

    internal ResultsUiModel Model => model;
    internal ResultsUILogic<CharacterId> Logic => logic;

    /// <summary>The F10 phase row.</summary>
    internal string Phase => logic.IsVisible ? "open: " + model.View.Header : logic.Pending ? "scheduled" : "closed";

    void Publish()
    {
        var view = logic.BuildView();
        if (view.Equals(model.View)) return;
        model = new ResultsUiModel(view) { RequestSerial = view.RequestSerial, RequestOpen = view.RequestOpen };
    }

    // C++: ResultsUI.cpp:607-616
    internal void ScheduleShow(World world, float delaySec) => logic.ScheduleShow(H(world), delaySec);

    // C++: ResultsUI.cpp:618-622
    internal void Cancel() => logic.Cancel();

    // C++: ResultsUI.cpp:624-629
    internal void Close()
    {
        logic.Close();
        Publish();
    }

    /// <summary>C++: ResultsUI.cpp:631-634.</summary>
    internal bool IsVisible => logic.IsVisible;

    /// <summary>C++: ResultsUI.cpp:636-683, the frame-loop slot (ArenaUI.cpp:3168): the delayed show (the resize half is the window side's).</summary>
    internal void Tick(World world)
    {
        if (!logic.Pending) return;   // nothing to count down (the common case): no host built
        if (!logic.Tick(H(world))) return;
        Publish();
        // TEST ONLY output (the results-board scenario): the shown model with its sim tick, while the test verbs are armed
        if (Spikes.SpikePatches.Armed)
            ProvingGroundsMod.Ctx?.Log("PG-RESULTS {\"event\":\"shown\",\"tick\":" + world.Tick.ToString(CultureInfo.InvariantCulture)
                + ",\"model\":" + ModelJson() + "}");
    }

    /// <summary>A widget event of the window (task 4.4's UiClick): the "Close results" button (OnCloseClicked :66) and the framework's Closed report
    /// (Escape, the title bar's close button: OnWindowButtonPressed :67-70; Value = the request serial it closed under).
    /// Answers what was done and whether the event was ignored (the framework pattern of ArenaUISim.Apply).</summary>
    internal (string Done, bool Ignored) Apply(UiClickData c)
    {
        if (c.Kind == UiEventKind.Closed)
        {
            if (!logic.OnClosedFromWindow(c.Value)) return ("closed (already, or for an older request)", true);
            Publish();
            return ("closed from the window", false);
        }
        if (c.Kind == UiEventKind.Click && c.Widget == ResultsUINames.Close)
        {
            if (!logic.OnCloseClicked()) return ("Close results: ignored (window closed)", true);
            Publish();
            return ("closed (Close results)", false);
        }
        return ($"{c.Kind} on {c.Widget}: ignored", true);
    }

    /// <summary>TEST ONLY (the results-board scenario): the model as one JSON object.</summary>
    internal string ModelJson()
    {
        var v = model.View;
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            open = v.RequestOpen, serial = v.RequestSerial, revision = v.Revision, pending = logic.Pending, header = v.Header, context = v.Context,
            empty = v.EmptyVisible,
            slots = v.Slots.Select(s => new
            {
                place = s.Place, rank = s.RankCaption, name = s.NameLine, initial = s.Initial, stats = s.Stats,
                id = s.HasCharacter ? CharacterIdText.Format(s.Character) : null,
            }),
        });
    }
}
