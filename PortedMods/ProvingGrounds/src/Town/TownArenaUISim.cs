using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;
using ProvingGrounds.Game;

namespace ProvingGrounds.Town;

// The sim half of upstream/ProvingGrounds/plugin/src/TownArenaUI.cpp (Proving Grounds by Shutori, GPLv3), task 7.5 (TW §2.5; CAP 7): every rule and
// text is Core/Glue/TownArenaUILogic.cs (tested in Tests/Glue/TownArenaUILogicTests.cs); this file binds it to a world and publishes the window model
// TownArenaUiModel (screen "pg.town"), as PrisonerRecruitmentUiModel (task 6.4a). The window half (the layout, widgets, portraits, tips) is task 7.6:
// it draws the model and sends the TownUiCommands of src/Engine/PgCommand.cs.
// E1 (the engine calls of the C++'s sim half and their replacements):
//   SquadUtil::CollectPlayerSquad (:238, :419) ... src/Game/SquadUtil.cs CollectPlayerSquad
//   RosterStatus::Read (:271) ... PgEngine.RosterHealth (task 7.2)
//   ArenaCombatProfile::Read (:525) ... src/Game/ArenaCombatProfile.cs Read (task 7.2)
//   every TownArena:: call ... src/Town/TownArena.Cards.cs (task 7.5) over the same TownArenaLogic and host
//   GetTickCount (:911) ... PgClock.NowMs (the TownArena host's clock)
// Callers (C++ -> port): ArenaUI.cpp:3164 Tick (the frame-loop slot, through Ui.TownArenaUI); ArenaUI::ShowFromRegistry -> Show (PgArenaUIHost.TownArenaShow);
// ArenaUI's Close / AbandonWorldState (PgArenaUIHost); the window's commands (ProvingGroundsSystem.Apply); the TEST ONLY pg town window.

/// <summary>The window model of the town registry window <c>pg.town</c> (task 7.5). A new object only when the view's content changed.</summary>
sealed record TownArenaUiModel(TownArenaUiView<CharacterId> View) : PgUiModel;

/// <summary>The town window's sim half (one per world, owned by ProvingGroundsSystem through Ui.TownArenaUI). Sim thread, serial steps.</summary>
sealed class TownArenaUISim(TownArena town)
{
    internal const string ScreenId = "pg.town";

    readonly TownArenaUILogic<CharacterId> logic = new();
    readonly Host host = new();
    TownArenaUiModel model = new(new TownArenaUiView<CharacterId>());

    sealed class Host : ITownArenaUIHost<CharacterId>
    {
        internal World? World;
        World W => World ?? throw new InvalidOperationException("TownArenaUI used before a world was bound");
        public IReadOnlyList<CharacterId> PlayerSquad() => SquadUtil.CollectPlayerSquad(W);
        public Core.ArenaCombatProfile.Profile Profile(CharacterId c) => Game.ArenaCombatProfile.Read(W, c);
        public bool Recovery(CharacterId c, out float recovery, out RosterStatusPolicy.Band band)
        {
            bool ok = PgEngine.RosterHealth(W, c, out RosterStatus.Snapshot status, out _);
            recovery = status.Recovery; band = status.Band;
            return ok;
        }
        public void Error(string line) => PGLog.Error(line);
    }

    Host U(World world) { host.World = world; return host; }
    Action<TownArenaLogic<CharacterId>, ITownHost<CharacterId>>? tickRun;   // task 9.2b: Tick's call into the town logic, built once

    internal TownArenaUiModel Model => model;
    internal bool IsVisible => logic.IsVisible;
    internal string Phase => logic.IsVisible ? "open: " + model.View.Feedback : "closed";
    internal IReadOnlyList<string> Lineup => logic.Lineup;
    internal TownArenaUILogic<CharacterId> Logic => logic;

    void Publish()
    {
        var view = logic.View;
        if (view.Key == model.View.Key) return;
        model = new TownArenaUiModel(view) { RequestSerial = (int)view.RequestSerial, RequestOpen = view.Open };
    }

    // C++: TownArenaUI.cpp:957-965
    internal void Show(World world, ArenaBuilding registry, bool bindRegistry)
    {
        town.Ui(world, (t, h) => logic.Show(t, h, U(world), registry, bindRegistry));
        Publish();
    }

    // C++: TownArenaUI.cpp:904
    internal void Close(World world) { logic.Close(); Publish(); }

    // C++: TownArenaUI.cpp:905-908
    internal void AbandonWorldState(World world) { logic.AbandonWorldState(); Publish(); }

    // C++: TownArenaUI.cpp:909-956 (the frame-loop slot, ArenaUI.cpp:3164)
    internal void Tick(World world)
    {
        if (!logic.IsVisible) return;
        U(world);   // task 9.2b: bind the window host first, then one cached delegate (no closure per tick while the window is open)
        town.Ui(world, tickRun ??= (t, h) => logic.Tick(t, h, host));
        Publish();
    }

    /// <summary>One window command (serial Inputs), re-checked against what the window showed (TownArenaUILogic).</summary>
    internal (string Done, bool Ignored) Apply(World world, TownUiCommand command)
    {
        string done = command.GetType().Name;
        bool ran = true;
        town.Ui(world, (t, h) =>
        {
            var u = U(world);
            switch (command)
            {
                case TownSelectFighter c: ran = logic.OnRowReleased(t, h, u, c.Seq, c.Id); break;
                case TownBookChallenge c: ran = logic.OnChallenge(t, h, u, c.Seq, c.Slot, c.ShownOffer, new TownChallengeBuyInPolicy.Quote { BuyIn = c.ShownBuyIn, Payout = c.ShownPayout }); break;
                case TownBookDirect c: ran = logic.OnBook(t, h, u, c.Seq, c.ShownCaption); break;
                case TownBuyRefresh c: ran = logic.OnBuyRefresh(t, h, c.Seq, c.ShownCost); break;
                case TownSetDivision c: if (logic.IsVisible) logic.OnDivision(t, h, c.Division); else ran = false; break;
                default: ran = false; break;
            }
        });
        Publish();
        return ran ? (done + ": " + model.View.Feedback, false) : (done + ": not run (window closed, an old request, or the card / button changed since it was drawn)", true);
    }

    /// <summary>TEST ONLY <c>pg town model</c> (task 7.6): the published window model as one PG-TOWN line, so a scenario finds the widget to click
    /// (a fighter's row index, a card's slot and state) and reads what the window draws; it changes nothing.</summary>
    internal string ModelForTest()
    {
        var v = model.View;
        static string Q(string t) => PGLog.Quote(t);
        static string B(bool b) => b ? "true" : "false";
        static string N(long n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sb = new System.Text.StringBuilder();
        sb.Append("{\"event\":\"model\",\"open\":").Append(B(v.Open)).Append(",\"requestSerial\":").Append(N(v.RequestSerial))
          .Append(",\"lastSeq\":").Append(N(v.LastSeq)).Append(",\"feedback\":").Append(Q(v.Feedback)).Append(",\"status\":").Append(Q(v.Status))
          .Append(",\"squad\":").Append(Q(v.Squad)).Append(",\"division\":").Append(N(v.Division)).Append(",\"divisionsEnabled\":").Append(B(v.DivisionsEnabled))
          .Append(",\"refreshButton\":").Append(Q(v.RefreshButton)).Append(",\"refreshCost\":").Append(N(v.RefreshCost)).Append(",\"refreshEnabled\":").Append(B(v.RefreshEnabled))
          .Append(",\"bookingButton\":").Append(Q(v.BookingButton)).Append(",\"bookingEnabled\":").Append(B(v.BookingEnabled))
          .Append(",\"direct\":").Append(Q(v.Direct)).Append(",\"nextRefresh\":").Append(Q(v.NextRefresh)).Append(",\"progress\":").Append(Q(v.Progress))
          .Append(",\"selectionPrompt\":").Append(B(v.SelectionPrompt)).Append(",\"skarnState\":").Append(Q(v.SkarnState))
          .Append(",\"rows\":[");
        for (int i = 0; i < v.Rows.Count; ++i)
        {
            var r = v.Rows[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"index\":").Append(N(i)).Append(",\"id\":").Append(Q(CharacterIdText.Format(r.Character))).Append(",\"identity\":").Append(Q(r.Id))
              .Append(",\"selected\":").Append(B(r.Selected)).Append(",\"caption\":").Append(Q(r.Caption)).Append('}');
        }
        sb.Append("],\"cards\":[");
        for (int i = 0; i < v.Cards.Count; ++i)
        {
            var c = v.Cards[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"slot\":").Append(N(c.Slot)).Append(",\"visible\":").Append(B(c.Visible)).Append(",\"heading\":").Append(Q(c.Heading))
              .Append(",\"text\":").Append(Q(c.Text)).Append(",\"terms\":").Append(Q(c.Terms)).Append(",\"bookVisible\":").Append(B(c.BookVisible))
              .Append(",\"bookEnabled\":").Append(B(c.BookEnabled)).Append(",\"rarity\":").Append(N(c.Rarity)).Append(",\"portraits\":").Append(N(c.PortraitCount))
              .Append(",\"buyIn\":").Append(N(c.Quote.BuyIn)).Append(",\"payout\":").Append(N(c.Quote.Payout)).Append(",\"offer\":").Append(Q(c.OfferKey))
              .Append(",\"names\":[").AppendJoin(",", c.Portraits.Where(p => p.NameVisible).Select(p => Q(p.Name))).Append("]}");
        }
        sb.Append("]}");
        string line = sb.ToString();
        ProvingGroundsMod.Ctx?.Log("PG-TOWN " + line);
        return line;
    }

    /// <summary>A generic widget event (task 4.4's UiClick): only the framework's Closed report is ours.</summary>
    internal (string Done, bool Ignored) Apply(World world, UiClickData c)
    {
        if (c.Kind != UiEventKind.Closed) return ($"ignored {c.Kind} on {c.Widget} (the town window sends its own commands)", true);
        logic.ClosedFromWindow();
        Publish();
        return ("closed from the window", false);
    }
}
