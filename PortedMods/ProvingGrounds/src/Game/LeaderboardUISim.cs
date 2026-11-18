using System.Globalization;
using System.Text;
using Meitou.Simulation;
using Meitou.Simulation.Population;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;

namespace ProvingGrounds.Game;

// Port of the rules half of upstream/ProvingGrounds/plugin/src/LeaderboardUI.h/.cpp (Proving Grounds by Shutori, GPLv3), task 4.6a (PA §2.15;
// CAP 7.0-7.4), reading LeaderboardStore::GetStandings / FindRatedCharacter / HasActiveSave / GetActiveSaveKey (LeaderboardStore.cpp:397-423,
// :615-679) from the world's store (src/Game/LeaderboardStore.cs, task 5.1). Every decision and text is
// Core/Glue/LeaderboardUILogic.cs (tested in Tests/Glue/LeaderboardUILogicTests.cs); the window half is src/Ui/LeaderboardUI.cs
// (LeaderboardScreen). This file is the sim half: Show / Close / the reset (OnResetClicked) as commands, the published LeaderboardUiModel.
// The C++ Tick (:721-736) only resizes the open window ("standings remain event-driven"): the window side's; no sim step needed beyond the slot.
// Meitou 0.17.3 (tag v0.17.3, fa34a1b). Sim thread, serial steps only.
//
// Engine calls of the C++ and what replaces them (E1):
//   LeaderboardData::Standings(g_data, kind) / FindRatedCharacter / HasActiveSave / GetActiveSaveKey ... src/Game/LeaderboardStore.cs (task 5.1:
//       the ledger of task 2.4; its FindRatedCharacter walks the squad, the prisoners (M6) and the town board's 2500 sphere)
//   ArenaNativePersistence::CanResetUnmatchedSidecar / ResetUnmatchedSidecar ... ArenaNativePersistence.ResetOffered / ResetArenaProgress
//   c->getName() ... PgEngine.Name
//   PortraitManager::setImageWidget, every MyGUI call ... the window half (Portraits.Bind, spike S7)
// Callers (C++ -> port): ArenaIngress.cpp:973, :1000 Show (the Leaderboard approach, task 4.2) and the TEST-ONLY `pg leaderboard` (kept);
// WorldLifecycle.cpp:259 Close (world teardown: a new world builds a new system); ArenaUI.cpp:3009-3011 the Escape key (Meitou's own Escape,
// reported: OnClosedFromWindow).

/// <summary>The window model of the Leaderboard window <c>pg.leaderboard</c> (task 4.6a). A new object only when the view changed.</summary>
sealed record LeaderboardUiModel(LeaderboardUiView<CharacterId> View) : PgUiModel;

/// <summary>The sim half of the Leaderboard window (task 4.6a). One per world, owned by ProvingGroundsSystem.</summary>
sealed class LeaderboardUISim(ArenaNativePersistence arena, LeaderboardStore store)
{
    internal const string ScreenId = "pg.leaderboard";

    readonly ArenaNativePersistence arena = arena;
    readonly LeaderboardStore store = store;
    readonly LeaderboardUILogic<CharacterId> logic = new();
    LeaderboardUiModel model = new(new LeaderboardUiView<CharacterId>());
    ArenaBuilding? source;   // C++ the Building* passed to Show (its position only: FindRatedCharacter's town search)

    /// <summary>ILeaderboardHost for one Show or click: the world of the call and a memoised FindRatedCharacter (each candidate's key read once,
    /// the town sphere only when a lookup needs it; Core RatedCharacterIndex keeps the C++ order and first match).</summary>
    sealed class Host : ILeaderboardHost<CharacterId>
    {
        readonly LeaderboardUISim sim;
        readonly World world;
        readonly RatedCharacterIndex<CharacterId> index;

        public Host(LeaderboardUISim sim, World world, ArenaBuilding? source)
        {
            this.sim = sim;
            this.world = world;
            index = sim.store.NewRatedCharacterIndex(world, source);
        }

        public bool HasActiveSave => sim.store.HasActiveSave();
        public string ActiveSaveKey => sim.store.GetActiveSaveKey();
        public bool CanResetUnmatchedSidecar() => sim.arena.ResetOffered;
        public bool ResetUnmatchedSidecar() => sim.arena.ResetArenaProgress();
        public IReadOnlyList<LeaderboardData.Standing> RawStandings(LeaderboardData.Kind kind) => LeaderboardData.Standings(sim.store.Data, kind);
        public bool TryRatedCharacter(LeaderboardData.Kind kind, string id, bool withSource, out CharacterId character, out string name)
        {
            // C++ FindRatedCharacter's town search runs only for the town board with a building (:648)
            if (!index.TryFind(id, withSource && kind == LeaderboardData.Kind.Town, out character)) character = CharacterId.None;
            name = character.IsNone ? "" : PgEngine.Name(world, character) ?? "";
            return !character.IsNone;
        }
        public void Debug(string line) => PGLog.Debug(line);
    }

    internal LeaderboardUiModel Model => model;
    internal LeaderboardUILogic<CharacterId> Logic => logic;

    /// <summary>The F10 phase row.</summary>
    internal string Phase => logic.IsVisible ? "open: " + model.View.Caption : "closed";

    void Publish()
    {
        var view = logic.BuildView();
        if (view.Equals(model.View)) return;
        model = new LeaderboardUiModel(view) { RequestSerial = view.RequestSerial, RequestOpen = view.RequestOpen };
    }

    /// <summary>C++: LeaderboardUI.cpp:707-716 Show (<paramref name="building"/> = the C++ <c>Building* source</c>; its kind decides
    /// <paramref name="kind"/> at the caller, ArenaIdentity's GetLeaderboardKind).</summary>
    internal void Show(World world, LeaderboardData.Kind kind, ArenaBuilding? building)
    {
        source = building;
        logic.Show(new Host(this, world, source), kind);
        Publish();
    }

    /// <summary>C++: LeaderboardUI.cpp:718.</summary>
    internal void Close()
    {
        logic.Close();
        Publish();
    }

    /// <summary>C++: LeaderboardUI.cpp:719.</summary>
    internal bool IsVisible => logic.IsVisible;

    /// <summary>C++: LeaderboardUI.cpp:721-736, the frame-loop slot (ArenaUI.cpp:3169): only the window's resize, which is the window side's.</summary>
    internal void Tick(World world) { }   // the C++ slot (ArenaUI.cpp:3169), kept empty on purpose: its only work is the window's resize

    /// <summary>A widget event of the window (task 4.4's UiClick): Close (OnCloseClicked :68), Reset (OnResetClicked :70-91) and the framework's
    /// Closed report (Escape, the title bar's close button: OnWindowButtonPressed :92-95). A press that arrives while the window is closed (sent just
    /// before it closed) is ignored: the C++ handlers are reachable only through the visible window.</summary>
    internal (string Done, bool Ignored) Apply(World world, UiClickData c)
    {
        if (c.Kind == UiEventKind.Closed)
        {
            if (!logic.OnClosedFromWindow(c.Value)) return ("closed (already, or for an older request)", true);
            Publish();
            return ("closed from the window", false);
        }
        if (c.Kind != UiEventKind.Click) return ($"{c.Kind} on {c.Widget}: ignored", true);
        if (!logic.IsVisible) return ($"{c.Widget}: ignored (window closed)", true);
        switch (c.Widget)
        {
            case LeaderboardUINames.Close:
                Close();
                return ("closed (Close)", false);
            case LeaderboardUINames.Reset:
                // the game cannot press a hidden button (LeaderboardUI.cpp:567 binds it; RefreshContent hides it unless the reset is offered)
                if (!model.View.ResetVisible) return ("the Reset button is hidden (the reset is not offered)", true);
                bool changed = logic.OnResetClicked(new Host(this, world, source));
                Publish();
                return (changed ? "reset: " + model.View.ResetCaption + " / " + model.View.Subtitle : "reset: not offered", false);
            default:
                return ($"press on {c.Widget}: ignored", true);
        }
    }

    // ---------- TEST ONLY: pg leaderboard <player|town|close> (task 4.6a, until the Leaderboard approach of task 4.2) ----------

    const string Usage = "usage pg leaderboard <player|town|close|reset|model>";

    /// <summary>TEST ONLY <c>pg leaderboard player|town|close|model</c>: <c>player</c> / <c>town</c> = Show of that board with, as its building,
    /// the nearest finished Leaderboard of that kind (ArenaIdentity's GetFinishedLeaderboardKind) within 25,000 of the squad's first member, or none
    /// (the town board's live search then has no centre); <c>close</c> / <c>reset</c> = a click (the left release over the button, task 4.4b) on the window's Close / Reset
    /// button (the window's own command; ok:false with the reason when it is ignored: the window closed, as pg arena, or the Reset button hidden,
    /// which the game cannot press); <c>model</c> = the model only. Answers one PG-LEADERBOARD line.</summary>
    internal string Command(World world, PopulationData data, IReadOnlyList<string> args)
    {
        try
        {
            string verb = args.Count > 0 ? args[0] : "";
            switch (verb)
            {
                case "player" or "town":
                    var kind = verb == "town" ? LeaderboardData.Kind.Town : LeaderboardData.Kind.Player;
                    var board = NearestBoard(world, data, kind);
                    Show(world, kind, board);
                    return Answer(world, verb, true, null);
                case "close" or "reset":
                    var (done, ignored) = Apply(world, new UiClickData(ScreenId, verb == "close" ? LeaderboardUINames.Close : LeaderboardUINames.Reset,
                        UiEventKind.Click, "", 0, false));
                    return ignored ? Answer(world, verb, false, logic.IsVisible ? "ignored: " + done : "ignored: the Leaderboard window is closed (pg leaderboard player first)")
                        : Answer(world, verb, true, null);
                case "model":
                    return Answer(world, verb, true, null);
                default:
                    return Answer(world, verb.Length > 0 ? verb : "leaderboard", false, Usage);
            }
        }
        catch (Exception e) { return Answer(world, "leaderboard", false, $"{e.GetType().Name}: {e.Message}"); }
    }

    static ArenaBuilding? NearestBoard(World world, PopulationData data, LeaderboardData.Kind kind)
    {
        foreach (var squad in PgEngine.PlayerSquads(world))
            foreach (var m in squad.Members)
                if (PgEngine.Position(world, m) is { } at)
                {
                    var hits = ArenaIdentityLogic.SphereHits(ArenaIdentity.Near(world, data, at, ArenaIdentityLogic.SiteSearchRadius), at);
                    return ArenaIdentityLogic.FindNearestMatching(at, hits, b =>
                    {
                        var k = LeaderboardData.Kind.Player;
                        return ArenaIdentityLogic.GetFinishedLeaderboardKind(b, ref k) && k == kind;
                    }, out _);
                }
        return null;
    }

    string Answer(World world, string verb, bool ok, string? error)
    {
        var v = model.View;
        var sb = new StringBuilder();
        sb.Append("{\"event\":").Append(PGLog.Quote(verb)).Append(",\"ok\":").Append(ok ? "true" : "false");
        if (error is not null) sb.Append(",\"error\":").Append(PGLog.Quote(error));
        sb.Append(",\"tick\":").Append(world.Tick.ToString(CultureInfo.InvariantCulture))
          .Append(",\"source\":").Append(source is { } s ? PGLog.Quote(ArenaIdentity.KeyText(s.Ref)) : "null")
          .Append(",\"model\":").Append(System.Text.Json.JsonSerializer.Serialize(new
          {
              open = v.RequestOpen, serial = v.RequestSerial, revision = v.Revision, caption = v.Caption, subtitle = v.Subtitle, empty = v.Empty,
              reset = v.ResetVisible, resetCaption = v.ResetCaption,
              rows = v.Rows.Select(r => new
              {
                  rank = r.Rank, name = r.Name, rating = r.Rating, record = r.Record, matches = r.Matches, details = r.Details, initial = r.Initial,
                  id = r.HasCharacter ? CharacterIdText.Format(r.Character) : null,
              }),
          })).Append('}');
        string json = sb.ToString();
        ProvingGroundsMod.Ctx?.Log("PG-LEADERBOARD " + json);
        return json;
    }
}
