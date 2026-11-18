using System.Globalization;
using System.Text;
using Meitou.Simulation;
using ProvingGrounds.Core;
using ProvingGrounds.Core.Glue;
using ProvingGrounds.Engine;
using ProvingGrounds.Game;

namespace ProvingGrounds.Town;

// The sim half of upstream/ProvingGrounds/plugin/src/TownBookieUI.cpp and TownBookieResultUI.cpp (Proving Grounds by Shutori, GPLv3), task 8.1a (TW §2.6;
// CAP 7): the two windows' models, published like TownArenaUiModel (task 7.5). Every rule and text is Core/Glue/TownBookieLogic.cs (the C++
// TownBookie.cpp MakeView / MakeResultView and the callbacks, re-checked on arrival); this file publishes the views as the models "pg.bookie" and
// "pg.bookie.result" and hands the windows' commands (src/Engine/PgCommand.cs BookieUiCommand) to the logic. The windows themselves (layout,
// widgets, portraits, tips, captions) are task 8.1b: they draw these models and send the commands, numbered by the shared RecruitmentRequestGate.
// A click made while the game is paused applies when it runs (CAP 7.0, as every mod command); the C++ has no "pending" text for it (TownBookie*.cpp:
// its "pending" strings are the ticket's state and "Pending Cats return must be collected before betting."), so none is shown (task brief note 6).

/// <summary>The betting window's model <c>pg.bookie</c>. A new object only when the view's content changed.</summary>
sealed record TownBookieUiModel(TownBookieUiView<CharacterId> View) : PgUiModel;

/// <summary>The result window's model <c>pg.bookie.result</c>.</summary>
sealed record TownBookieResultUiModel(TownBookieResultUiView<CharacterId> View) : PgUiModel;

/// <summary>The bookie windows' sim half (one per world, owned by ProvingGroundsSystem). Sim thread, serial steps.</summary>
sealed class TownBookieUISim(TownBookie bookie)
{
    internal const string ScreenId = "pg.bookie";
    internal const string ResultScreenId = "pg.bookie.result";

    TownBookieUiModel model = new(TownBookieUiView<CharacterId>.Empty);
    TownBookieResultUiModel resultModel = new(TownBookieResultUiView<CharacterId>.Empty);

    internal TownBookie Bookie => bookie;

    /// <summary>The published betting model (rebuilt only when the logic published a new view).</summary>
    internal TownBookieUiModel Model
    {
        get
        {
            var v = bookie.Logic.View;
            if (!ReferenceEquals(v, model.View)) model = new TownBookieUiModel(v) { RequestSerial = v.RequestSerial, RequestOpen = v.Open };
            return model;
        }
    }

    /// <summary>The published result model.</summary>
    internal TownBookieResultUiModel ResultModel
    {
        get
        {
            var v = bookie.Logic.ResultView;
            if (!ReferenceEquals(v, resultModel.View)) resultModel = new TownBookieResultUiModel(v) { RequestSerial = v.RequestSerial, RequestOpen = v.Open };
            return resultModel;
        }
    }

    /// <summary>One window command (serial Inputs), re-checked against what the window showed (TownBookieLogic).</summary>
    internal (string Done, bool Ignored) Apply(World world, BookieUiCommand command)
    {
        var l = bookie.Logic;
        bool ran = bookie.Run(world, h => command switch
        {
            BookieSelectSide c => l.OnSelectSide(h, c.Seq, c.Side),
            BookieChangeStake c => l.OnChangeStake(h, c.Seq, c.Delta, c.ShownStake),
            BookiePlaceBet c => l.OnPlaceBet(h, c.Seq, c.ShownSide, c.ShownStake),
            BookieArrange c => l.OnArrange(h, c.Seq),
            BookieClose c => l.OnClose(h, c.ShownSerial),
            _ => false,
        });
        string done = command.GetType().Name;
        return ran ? (done + ": " + l.View.View.Status, false)
            : (done + ": not run (window closed, an old request, or the button was disabled or changed since it was drawn)", true);
    }

    /// <summary>A generic widget event (task 4.4's UiClick): only the framework's Closed report is ours (C++ the window's close button, TownBookie::Close).
    /// Its Value is the RequestSerial of the model the window closed under (ScreenHost.ReportClosed): a late Closed from an older window (for example the
    /// betting window's, arriving after Tick opened the result window) closes nothing. Task 8.1b: both windows close through this report (or BookieClose
    /// with the drawn serial), never with the current one.</summary>
    internal (string Done, bool Ignored) Apply(World world, UiClickData c)
    {
        if (c.Kind != UiEventKind.Closed) return ($"ignored {c.Kind} on {c.Widget} (the bookie windows send their own commands)", true);
        int serial = c.Value;
        bool ran = bookie.Run(world, h => bookie.Logic.OnClose(h, serial));
        return ran ? ("closed from the window", false) : ("closed (already, or for an older request)", true);
    }

    // ---------- TEST ONLY: pg town bookie open|model|side A|B|stake <delta>|bet|arrange|close|result ----------

    internal const string Usage = "usage pg town bookie open|model|side A|B|stake <delta>|bet|arrange|close|result";

    /// <summary>TEST ONLY <c>pg town bookie ...</c>: one PG-TOWN line (the betting and result models, the ticket, the credit, the market clock). The
    /// window verbs send the window's own commands with the drawn values of the published model (the next request number), so the enabled rules
    /// are the window's; <c>open</c> does what the dialogue's trade does (TownBookie::Show on the nearest Scratch Bookie 111).</summary>
    internal string Command(World world, IReadOnlyList<string> args, Func<World, CharacterId> nearestBookie)
    {
        string verb = args.Count > 0 ? args[0].ToLowerInvariant() : "model";
        var v = Model.View;
        string? error = null;
        string done = "";
        try
        {
            switch (verb)
            {
                case "open":
                {
                    var b = nearestBookie(world);
                    if (b.IsNone) { error = "no Scratch Bookie (111) found"; break; }
                    bookie.Show(world, b);
                    done = "shown";
                    break;
                }
                case "model": case "result": break;
                case "side" when args.Count == 2 && args[1].ToUpperInvariant() is "A" or "B":
                    done = Apply(world, new BookieSelectSide(args[1].ToUpperInvariant() == "A" ? 0 : 1, v.LastSeq + 1) { Tick = world.Tick }).Done; break;
                case "stake" when args.Count == 2 && int.TryParse(args[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int delta):
                    done = Apply(world, new BookieChangeStake(delta, v.LastSeq + 1, v.View.Stake) { Tick = world.Tick }).Done; break;
                case "bet":
                    done = Apply(world, new BookiePlaceBet(v.LastSeq + 1, v.View.SelectedSide, v.View.Stake) { Tick = world.Tick }).Done; break;
                case "arrange":
                    done = Apply(world, new BookieArrange(v.LastSeq + 1) { Tick = world.Tick }).Done; break;
                case "close":
                    done = Apply(world, new BookieClose(v.RequestSerial) { Tick = world.Tick }).Done; break;
                default: error = Usage; break;
            }
        }
        catch (Exception e) { error = $"{e.GetType().Name}: {e.Message}"; }
        string line = ModelLine(world, verb, error, done);
        ProvingGroundsMod.Ctx?.Log("PG-TOWN " + line);
        return line;
    }

    string ModelLine(World world, string verb, string? error, string done)
    {
        static string Q(string? t) => PGLog.Quote(t ?? "");
        static string B(bool b) => b ? "true" : "false";
        static string N(long n) => n.ToString(CultureInfo.InvariantCulture);
        var l = bookie.Logic;
        var m = Model.View; var r = ResultModel.View;
        var bv = m.View; var rv = r.View;
        var t = l.Ticket;
        var sb = new StringBuilder();
        sb.Append("{\"event\":\"bookie\",\"verb\":").Append(Q(verb)).Append(",\"ok\":").Append(B(error is null));
        if (error is not null) sb.Append(",\"error\":").Append(Q(error));
        sb.Append(",\"done\":").Append(Q(done))
          .Append(",\"tick\":").Append(N(world.Tick))
          .Append(",\"money\":").Append(N(world.Player.Money))
          .Append(",\"credit\":").Append(N(bookie.CreditForTest(world)))
          .Append(",\"market\":").Append(B(l.IsMarketOpen)).Append(",\"fighting\":").Append(B(l.IsFighting)).Append(",\"completed\":").Append(B(l.IsCompleted))
          .Append(",\"elapsed\":").Append(Q(CMath.FormatF(l.Elapsed, 2))).Append(",\"hold\":").Append(B(l.HoldForBets()))
          .Append(",\"ability\":[").Append(Q(CMath.FormatG(l.AbilityOf(0)))).Append(',').Append(Q(CMath.FormatG(l.AbilityOf(1)))).Append(']')
          .Append(",\"teams\":[").Append(N(l.Team(0).Count)).Append(',').Append(N(l.Team(1).Count)).Append(']')
          .Append(",\"bookie\":").Append(Q(l.BookieCharacter.IsNone ? "" : CharacterIdText.Format(l.BookieCharacter)))
          .Append(",\"ticket\":{\"pending\":").Append(B(t.Pending)).Append(",\"side\":").Append(N(t.Side)).Append(",\"stake\":").Append(N(t.Stake))
          .Append(",\"payout\":").Append(N(t.Payout)).Append('}')
          .Append(",\"resultScheduled\":").Append(B(l.ResultScheduledNow)).Append(",\"resultReady\":").Append(B(l.ResultReadyNow))
          .Append(",\"nowMs\":").Append(N(bookie.NowMsForTest)).Append(",\"resultScheduledAt\":").Append(N(l.ResultScheduledAtMs))
          .Append(",\"resultShownAt\":").Append(N(l.ResultShownAtMs)).Append(",\"notice\":").Append(Q(bookie.LastNotice))
          .Append(",\"message\":").Append(Q(l.Message)).Append(",\"actionMessage\":").Append(Q(l.ActionMessage))
          .Append(",\"failures\":{\"log\":").Append(N(bookie.LogFailures)).Append(",\"fighterReads\":").Append(N(bookie.FighterReadFailures)).Append('}')
          .Append(",\"model\":{\"open\":").Append(B(m.Open)).Append(",\"requestSerial\":").Append(N(m.RequestSerial)).Append(",\"lastSeq\":").Append(N(m.LastSeq))
          .Append(",\"header\":").Append(Q(bv.Header)).Append(",\"timer\":").Append(Q(bv.Timer)).Append(",\"status\":").Append(Q(bv.Status))
          .Append(",\"slip\":").Append(Q(bv.Slip)).Append(",\"oddsA\":").Append(Q(bv.Odds[0])).Append(",\"oddsB\":").Append(Q(bv.Odds[1]))
          .Append(",\"selectedSide\":").Append(N(bv.SelectedSide)).Append(",\"stake\":").Append(N(bv.Stake))
          .Append(",\"canSelect\":").Append(B(bv.CanSelect)).Append(",\"canStake\":").Append(B(bv.CanStake)).Append(",\"canBet\":").Append(B(bv.CanBet))
          .Append(",\"canArrange\":").Append(B(bv.CanArrange)).Append(",\"adjust\":[");
        for (int i = 0; i < m.AdjustmentEnabled.Count; i++) { if (i > 0) sb.Append(','); sb.Append(B(m.AdjustmentEnabled[i])); }
        sb.Append("],\"fighters\":");
        AppendCards(sb, m.Cards);
        sb.Append("},\"result\":{\"open\":").Append(B(r.Open)).Append(",\"title\":").Append(Q(rv.Title)).Append(",\"context\":").Append(Q(rv.Context))
          .Append(",\"payout\":").Append(Q(rv.Payout)).Append(",\"winningSide\":").Append(N(rv.WinningSide)).Append(",\"selectedSide\":").Append(N(rv.SelectedSide))
          .Append(",\"tone\":").Append(N(rv.Tone)).Append(",\"fighters\":");
        AppendCards(sb, r.Cards);
        sb.Append("}}");
        return sb.ToString();

        static void AppendCards(StringBuilder sb, IReadOnlyList<IReadOnlyList<BookieFighterCard<CharacterId>>> cards)
        {
            sb.Append('[');
            for (int side = 0; side < 2; side++)
            {
                if (side > 0) sb.Append(',');
                sb.Append('[');
                for (int i = 0; i < cards[side].Count; i++)
                {
                    var c = cards[side][i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"id\":").Append(Q(CharacterIdText.Format(c.Actor))).Append(",\"key\":").Append(Q(c.PortraitKey))
                      .Append(",\"name\":").Append(Q(c.View.Name)).Append(",\"style\":").Append(Q(c.View.Style)).Append(",\"record\":").Append(Q(c.View.Record))
                      .Append(",\"stats\":").Append(Q(c.View.Stats)).Append('}');
                }
                sb.Append(']');
            }
            sb.Append(']');
        }
    }
}
